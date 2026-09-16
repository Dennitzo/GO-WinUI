using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace GoAi.Server.Core.Research;

public sealed partial class WebResearchService
{
    private const int MaximumFetchBytes = 25 * 1024 * 1024;
    private const int MaximumExtractedCharacters = 512_000;
    private const long MaximumOfficeUncompressedBytes = 64L * 1024 * 1024;
    private const int MaximumDocumentPages = 500;
    private const int MaximumRedirects = 5;
    private const string BrowserCompatibleUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 GO-AI-Server/1.0";
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GoAiServerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, EngineCooldown> _engineCooldowns = new(StringComparer.OrdinalIgnoreCase);

    public WebResearchService(
        IHttpClientFactory httpClientFactory,
        IOptions<GoAiServerOptions> options,
        TimeProvider? timeProvider = null)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<WebSearchResponse> SearchAsync(
        WebSearchRequest request,
        bool youtubeFallback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Query) || request.Query.Length > 500)
        {
            throw new ArgumentException("A search query between 1 and 500 characters is required.", nameof(request));
        }
        if (request.Language?.Length > 16)
        {
            throw new ArgumentException("Search language may contain at most 16 characters.", nameof(request));
        }
        if (!SearxngSearchProfiles.IsValid(request.Profile))
            throw new ArgumentException("Search profile must be auto, general, python, web, dotnet or images.", nameof(request));

        var maximum = Math.Clamp(request.MaximumResults, 1, 20);
        var youtubeApiKey = _options.YouTubeApiKey;
        if (youtubeFallback && !string.IsNullOrWhiteSpace(youtubeApiKey))
        {
            return await SearchYouTubeAsync(request, maximum, youtubeApiKey, cancellationToken).ConfigureAwait(false);
        }

        var query = youtubeFallback ? $"site:youtube.com/watch {request.Query}" : request.Query;
        // Technical reference material is often English even when the conversation is German.
        // SearXNG's CSE engine turns de-DE into a strict document-language filter.
        var language = youtubeFallback ? request.Language ?? "de-DE"
            : SearxngSearchProfiles.SearchLanguage(request.Profile, query, request.Language);
        var profileEngines = youtubeFallback ? null : SearxngSearchProfiles.Engines(request.Profile, query)?.Split(',');
        var skipped = new List<SearchEngineFailure>();
        var selectedEngines = profileEngines?.Where(engine =>
        {
            if (!_engineCooldowns.TryGetValue(engine, out var cooldown) || cooldown.Until <= _timeProvider.GetUtcNow()) return true;
            skipped.Add(new(engine, CleanDiagnostic("Vorübergehend ausgelassen: " + cooldown.Reason, 160)));
            return false;
        }).ToArray();
        if (selectedEngines is { Length: 0 }) throw new SearxngEngineUnavailableException(skipped);
        var builder = new UriBuilder(new Uri(_options.SearxngUri, "/search"))
        {
            Query = $"q={Uri.EscapeDataString(query)}&format=json&language={Uri.EscapeDataString(language)}"
                + (request.Profile == "images" ? "&categories=images" : string.Empty),
        };
        if (selectedEngines is not null)
            builder.Query += "&engines=" + Uri.EscapeDataString(string.Join(',', selectedEngines));
        var client = _httpClientFactory.CreateClient(nameof(WebResearchService));
        client.Timeout = TimeSpan.FromSeconds(20);
        using var response = await client.GetAsync(builder.Uri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        var results = new List<WebSearchResult>();
        if (document.RootElement.TryGetProperty("results", out var rawResults))
        {
            foreach (var raw in rawResults.EnumerateArray().Take(maximum))
            {
                var url = GetString(raw, "url");
                var title = GetString(raw, "title");
                if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                results.Add(new WebSearchResult(
                    title,
                    url,
                    GetString(raw, "content"),
                    GetString(raw, "engine"),
                    null,
                    GetString(raw, "img_src") ?? GetString(raw, "thumbnail_src") ?? GetString(raw, "thumbnail")));
            }
        }

        var failures = ReadEngineFailures(document.RootElement);
        foreach (var failure in failures)
        {
            // SearXNG already suspends these engines. Preserve that decision across calls
            // instead of immediately asking a known blocked engine with a different query.
            var duration = EngineCooldownDuration(failure.Reason);
            if (duration > TimeSpan.Zero)
                _engineCooldowns[failure.Engine] = new(_timeProvider.GetUtcNow() + duration, failure.Reason);
        }
        failures.AddRange(skipped.Where(failure => !failures.Any(current => current.Engine.Equals(failure.Engine, StringComparison.OrdinalIgnoreCase))));
        // An empty answer from a healthy engine is a query problem, not an outage.
        // Keep it refinable even when another engine was blocked or deliberately skipped.
        if (results.Count == 0 && failures.Count > 0 && (selectedEngines is null
            || selectedEngines.All(engine => failures.Any(failure => failure.Engine.Equals(engine, StringComparison.OrdinalIgnoreCase)))))
            throw new SearxngEngineUnavailableException(failures);
        return new WebSearchResponse(
            request.Query,
            results,
            "searxng",
            youtubeFallback,
            DateTimeOffset.UtcNow,
            failures.Count > 0 ? failures : null,
            language,
            selectedEngines,
            results.Count == 0 && selectedEngines is not null
                ? "Die antwortenden Suchmaschinen lieferten keine Treffer. Verkürze die nächste Abfrage auf den exakten API-Namen und einen Aspekt oder prüfe eine bekannte Originalquelle mit web.fetch. Vorübergehend gesperrte Engines werden ausgelassen."
                : null);
    }

    private static TimeSpan EngineCooldownDuration(string reason) =>
        reason.Contains("captcha", StringComparison.OrdinalIgnoreCase) ? TimeSpan.FromHours(1)
        : reason.Contains("429", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("too many requests", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("unusual traffic", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("403", StringComparison.OrdinalIgnoreCase) ? TimeSpan.FromMinutes(3)
        : TimeSpan.Zero;

    private sealed record EngineCooldown(DateTimeOffset Until, string Reason);

    private static List<SearchEngineFailure> ReadEngineFailures(JsonElement response)
    {
        if (!response.TryGetProperty("unresponsive_engines", out var engines) || engines.ValueKind != JsonValueKind.Array)
            return [];
        var failures = new List<SearchEngineFailure>();
        foreach (var engine in engines.EnumerateArray().Take(8))
        {
            if (engine.ValueKind != JsonValueKind.Array || engine.GetArrayLength() < 2
                || engine[0].ValueKind != JsonValueKind.String || engine[1].ValueKind != JsonValueKind.String) continue;
            var name = CleanDiagnostic(engine[0].GetString()!, 64);
            var reason = CleanDiagnostic(engine[1].GetString()!, 160);
            if (name.Length > 0) failures.Add(new(name, reason.Length > 0 ? reason : "Engine meldet keine Antwort"));
        }
        return failures;
    }

    private static string CleanDiagnostic(string value, int maximum) =>
        new(value.Take(maximum).Select(static character => char.IsControl(character) ? ' ' : character).ToArray());

    private async Task<WebSearchResponse> SearchYouTubeAsync(
        WebSearchRequest request,
        int maximum,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var language = NormalizeLanguage(request.Language);
        var query = new StringBuilder()
            .Append("part=snippet&type=video&safeSearch=moderate")
            .Append("&maxResults=").Append(maximum.ToString(CultureInfo.InvariantCulture))
            .Append("&q=").Append(Uri.EscapeDataString(request.Query));
        if (language is not null)
        {
            query.Append("&relevanceLanguage=").Append(Uri.EscapeDataString(language));
        }

        var client = _httpClientFactory.CreateClient(nameof(WebResearchService) + ".YouTube");
        client.Timeout = TimeSpan.FromSeconds(20);
        using var searchRequest = CreateGoogleRequest(
            new UriBuilder("https://www.googleapis.com/youtube/v3/search") { Query = query.ToString() }.Uri,
            apiKey);
        using var searchResponse = await client.SendAsync(searchRequest, cancellationToken).ConfigureAwait(false);
        searchResponse.EnsureSuccessStatusCode();
        using var searchDocument = JsonDocument.Parse(
            await searchResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));

        var items = new List<YouTubeSearchItem>();
        if (searchDocument.RootElement.TryGetProperty("items", out var rawItems))
        {
            foreach (var raw in rawItems.EnumerateArray().Take(maximum))
            {
                if (!raw.TryGetProperty("id", out var id)
                    || !raw.TryGetProperty("snippet", out var snippet)
                    || GetString(id, "videoId") is not { Length: > 0 } videoId
                    || GetString(snippet, "title") is not { Length: > 0 } title)
                {
                    continue;
                }

                items.Add(new YouTubeSearchItem(
                    videoId,
                    WebUtility.HtmlDecode(title),
                    WebUtility.HtmlDecode(GetString(snippet, "description") ?? string.Empty),
                    WebUtility.HtmlDecode(GetString(snippet, "channelTitle") ?? "YouTube"),
                    ParsePublishedAt(GetString(snippet, "publishedAt")),
                    ReadThumbnail(snippet)));
            }
        }

        var durations = await ReadYouTubeDurationsAsync(client, items, apiKey, cancellationToken).ConfigureAwait(false);
        var results = items.Select(item => new WebSearchResult(
            item.Title,
            $"https://www.youtube.com/watch?v={Uri.EscapeDataString(item.VideoId)}",
            item.Description,
            item.Channel,
            item.PublishedAt,
            item.ThumbnailUrl,
            durations.GetValueOrDefault(item.VideoId))).ToArray();
        return new WebSearchResponse(
            request.Query,
            results,
            "youtube-data-api-v3",
            false,
            DateTimeOffset.UtcNow);
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadYouTubeDurationsAsync(
        HttpClient client,
        IReadOnlyList<YouTubeSearchItem> items,
        string apiKey,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var ids = string.Join(',', items.Select(static item => item.VideoId));
        var uri = new UriBuilder("https://www.googleapis.com/youtube/v3/videos")
        {
            Query = $"part=contentDetails&id={Uri.EscapeDataString(ids)}",
        }.Uri;
        using var request = CreateGoogleRequest(uri, apiKey);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        var durations = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!document.RootElement.TryGetProperty("items", out var rawItems))
        {
            return durations;
        }

        foreach (var raw in rawItems.EnumerateArray())
        {
            if (GetString(raw, "id") is not { Length: > 0 } id
                || !raw.TryGetProperty("contentDetails", out var details)
                || GetString(details, "duration") is not { Length: > 0 } duration)
            {
                continue;
            }
            durations[id] = FormatDuration(duration);
        }
        return durations;
    }

    private static HttpRequestMessage CreateGoogleRequest(Uri uri, string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("X-Goog-Api-Key", apiKey);
        return request;
    }

    private static string? NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }
        var normalized = language.Trim().Split('-', '_')[0];
        return normalized.Length is 2 or 3 ? normalized : null;
    }

    private static DateTimeOffset? ParsePublishedAt(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var published)
            ? published
            : null;

    private static string? ReadThumbnail(JsonElement snippet)
    {
        if (!snippet.TryGetProperty("thumbnails", out var thumbnails))
        {
            return null;
        }
        foreach (var name in new[] { "high", "medium", "default" })
        {
            if (thumbnails.TryGetProperty(name, out var thumbnail)
                && GetString(thumbnail, "url") is { Length: > 0 } url)
            {
                return url;
            }
        }
        return null;
    }

    private static string FormatDuration(string value)
    {
        try
        {
            var duration = XmlConvert.ToTimeSpan(value);
            var totalHours = checked((int)duration.TotalHours);
            return totalHours > 0
                ? $"{totalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
                : $"{duration.Minutes}:{duration.Seconds:00}";
        }
        catch (FormatException)
        {
            return value;
        }
        catch (OverflowException)
        {
            return value;
        }
    }

    public static async Task<WebFetchResponse> FetchAsync(WebFetchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Url)
            || request.Url.Length > 2_048
            || !Uri.TryCreate(request.Url, UriKind.Absolute, out var current)
            || current.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(current.UserInfo))
        {
            throw new ArgumentException("Only absolute HTTP and HTTPS URLs are allowed.", nameof(request));
        }

        var redirects = new List<string>();
        for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            var addresses = await ResolvePublicAddressesAsync(current, cancellationToken).ConfigureAwait(false);
            using var handler = CreatePinnedHandler(current.Host, addresses);
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30),
            };
            using var message = CreateFetchRequest(current);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (IsRedirect(response.StatusCode))
            {
                if (response.Headers.Location is null || redirect == MaximumRedirects)
                {
                    throw new HttpRequestException("Fetch exceeded the redirect limit or received an invalid redirect.");
                }

                redirects.Add(current.ToString());
                current = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(current, response.Headers.Location);
                if (current.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(current.UserInfo))
                {
                    throw new HttpRequestException("Fetch redirect changed to a forbidden URL scheme.");
                }

                continue;
            }

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumFetchBytes)
            {
                throw new HttpRequestException("Fetch response exceeds the 25 MiB limit.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var bytes = await ReadBoundedAsync(stream, MaximumFetchBytes, cancellationToken).ConfigureAwait(false);
            var mediaType = DetectMediaType(
                current,
                response.Content.Headers.ContentType?.MediaType,
                bytes);
            var content = ExtractFetchedContent(
                bytes,
                mediaType,
                response.Content.Headers.ContentType?.CharSet,
                cancellationToken);

            return new WebFetchResponse(current.ToString(), mediaType, content, true, DateTimeOffset.UtcNow, redirects);
        }

        throw new HttpRequestException("Fetch failed unexpectedly.");
    }

    internal static HttpRequestMessage CreateFetchRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", BrowserCompatibleUserAgent);
        request.Headers.TryAddWithoutValidation(
            "Accept",
            "text/html,application/xhtml+xml,application/pdf," +
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document," +
            "application/rtf,text/plain;q=0.9,*/*;q=0.8");
        request.Headers.TryAddWithoutValidation("Accept-Language", "de-DE,de;q=0.9,en;q=0.7");
        return request;
    }

    private static SocketsHttpHandler CreatePinnedHandler(string host, IReadOnlyList<IPAddress> addresses)
    {
        var next = 0;
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.Brotli | DecompressionMethods.Deflate | DecompressionMethods.GZip,
            ConnectCallback = async (context, cancellationToken) =>
            {
                if (!string.Equals(context.DnsEndPoint.Host, host, StringComparison.OrdinalIgnoreCase))
                {
                    throw new HttpRequestException("DNS host changed during fetch.");
                }

                Exception? lastError = null;
                for (var attempt = 0; attempt < addresses.Count; attempt++)
                {
                    var address = addresses[Interlocked.Increment(ref next) % addresses.Count];
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception exception) when (exception is SocketException or OperationCanceledException)
                    {
                        lastError = exception;
                        socket.Dispose();
                    }
                }

                throw new HttpRequestException("No validated address could be reached.", lastError);
            },
        };
    }

    private static async Task<IReadOnlyList<IPAddress>> ResolvePublicAddressesAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(uri.DnsSafeHost, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken).ConfigureAwait(false);
        }

        var allowed = addresses.Where(static address => !IsForbiddenAddress(address)).Distinct().ToArray();
        if (allowed.Length == 0 || allowed.Length != addresses.Distinct().Count())
        {
            throw new HttpRequestException("Fetch target resolves to a private, loopback, link-local, or otherwise forbidden address.");
        }

        return allowed;
    }

    private static bool IsForbiddenAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal)
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 0
                || bytes[0] == 10
                || (bytes[0] == 100 && (bytes[1] & 0xC0) == 64)
                || bytes[0] == 127
                || (bytes[0] == 169 && bytes[1] == 254)
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0)
                || (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 192 && bytes[1] == 88 && bytes[2] == 99)
                || (bytes[0] == 198 && bytes[1] is 18 or 19)
                || (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100)
                || (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113)
                || bytes[0] >= 224;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return address.Equals(IPAddress.IPv6Any)
                || address.Equals(IPAddress.IPv6None)
                || (bytes[0] & 0xFE) == 0xFC
                || bytes is [0x00, 0x64, 0xFF, 0x9B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, ..]
                || bytes is [0x20, 0x01, 0x00, 0x00, ..]
                || bytes is [0x20, 0x01, 0x0D, 0xB8, ..]
                || bytes is [0x20, 0x02, ..];
        }

        return true;
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > maximum)
            {
                throw new HttpRequestException("Fetch response exceeds the 25 MiB limit.");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    internal static string DetectMediaType(Uri source, string? declaredMediaType, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.AsSpan().StartsWith("%PDF-"u8))
        {
            return "application/pdf";
        }

        if (LooksLikeZip(bytes) && ContainsWordDocument(bytes))
        {
            return "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
        }

        var extension = Path.GetExtension(source.AbsolutePath).ToLowerInvariant();
        var extensionMediaType = extension switch
        {
            ".pdf" => "application/pdf",
            ".docx" or ".docm" or ".dotx" or ".dotm" =>
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".doc" => "application/msword",
            ".rtf" => "application/rtf",
            ".html" or ".htm" => "text/html",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".md" => "text/markdown",
            ".csv" => "text/csv",
            ".txt" => "text/plain",
            _ => null,
        };
        var normalizedDeclared = string.IsNullOrWhiteSpace(declaredMediaType)
            ? null
            : declaredMediaType.Trim().ToLowerInvariant();
        return normalizedDeclared is null or "application/octet-stream" or "binary/octet-stream"
            ? extensionMediaType ?? normalizedDeclared ?? "application/octet-stream"
            : normalizedDeclared;
    }

    internal static string ExtractFetchedContent(
        byte[] bytes,
        string mediaType,
        string? charset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            throw new ArgumentException("A response media type is required.", nameof(mediaType));
        }

        var normalized = mediaType.Trim().ToLowerInvariant();
        if (normalized is "application/pdf" or "application/x-pdf")
        {
            return ExtractPdf(bytes, cancellationToken);
        }
        if (IsOpenXmlWordMediaType(normalized))
        {
            return ExtractOpenXmlWord(bytes, cancellationToken);
        }
        if (normalized is "application/msword")
        {
            throw new InvalidDataException(
                "Legacy Word .doc files cannot be safely extracted. Use DOCX or RTF instead.");
        }

        var decoded = DecodeContent(bytes, charset);
        if (normalized.Contains("html", StringComparison.Ordinal))
        {
            return NormalizeHtml(decoded);
        }
        if (normalized is "application/rtf" or "text/rtf")
        {
            return LimitExtractedContent(RtfToText(decoded));
        }
        if (normalized.StartsWith("text/", StringComparison.Ordinal)
            || normalized is "application/json" or "application/xml" or "application/xhtml+xml"
            || normalized.EndsWith("+json", StringComparison.Ordinal)
            || normalized.EndsWith("+xml", StringComparison.Ordinal)
            || normalized == "application/octet-stream" && LooksLikeText(bytes))
        {
            return LimitExtractedContent(decoded);
        }

        throw new InvalidDataException($"Unsupported web response media type {mediaType}.");
    }

    private static string ExtractPdf(byte[] bytes, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var document = PdfDocument.Open(stream);
            var result = new StringBuilder(Math.Min(MaximumExtractedCharacters, bytes.Length * 2));
            var pageNumber = 0;
            foreach (var page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();
                pageNumber++;
                if (pageNumber > MaximumDocumentPages)
                {
                    AppendBounded(result, $"\n[PDF nach {MaximumDocumentPages} Seiten gekürzt]");
                    break;
                }

                if (!AppendBounded(
                    result,
                    $"\n\n[Seite {pageNumber}]\n{ContentOrderTextExtractor.GetText(page).Trim()}"))
                {
                    break;
                }
            }

            return CompleteDocumentExtraction(result, "PDF");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not InvalidDataException)
        {
            throw new InvalidDataException("PDF text extraction failed.", exception);
        }
    }

    private static string ExtractOpenXmlWord(byte[] bytes, CancellationToken cancellationToken)
    {
        try
        {
            ValidateOfficeArchive(bytes);
            using var stream = new MemoryStream(bytes, writable: false);
            using var document = WordprocessingDocument.Open(stream, false);
            var mainPart = document.MainDocumentPart
                ?? throw new InvalidDataException("DOCX contains no main document part.");
            var body = mainPart.Document?.Body
                ?? throw new InvalidDataException("DOCX contains no document body.");
            var result = new StringBuilder(Math.Min(MaximumExtractedCharacters, bytes.Length * 2));
            AppendWordParagraphs(result, body.Descendants<Paragraph>(), cancellationToken);
            foreach (var header in mainPart.HeaderParts)
            {
                AppendWordParagraphs(result, header.Header?.Descendants<Paragraph>() ?? [], cancellationToken);
            }
            foreach (var footer in mainPart.FooterParts)
            {
                AppendWordParagraphs(result, footer.Footer?.Descendants<Paragraph>() ?? [], cancellationToken);
            }

            return CompleteDocumentExtraction(result, "DOCX");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not InvalidDataException)
        {
            throw new InvalidDataException("DOCX text extraction failed.", exception);
        }
    }

    private static void AppendWordParagraphs(
        StringBuilder result,
        IEnumerable<Paragraph> paragraphs,
        CancellationToken cancellationToken)
    {
        foreach (var paragraph in paragraphs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = string.Concat(paragraph.Descendants<Text>().Select(static node => node.Text)).Trim();
            if (text.Length > 0 && !AppendBounded(result, text + Environment.NewLine))
            {
                return;
            }
        }
    }

    private static void ValidateOfficeArchive(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count > 10_000
            || !archive.Entries.Any(static entry =>
                string.Equals(entry.FullName, "word/document.xml", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("The downloaded archive is not a valid Word document.");
        }

        long totalLength = 0;
        foreach (var entry in archive.Entries)
        {
            totalLength = checked(totalLength + entry.Length);
            if (totalLength > MaximumOfficeUncompressedBytes)
            {
                throw new InvalidDataException("The Word document exceeds the safe decompressed size limit.");
            }
        }
    }

    private static string CompleteDocumentExtraction(StringBuilder result, string documentType)
    {
        var text = result.ToString().Trim();
        if (text.Length == 0)
        {
            throw new InvalidDataException(
                $"The {documentType} document contains no extractable text.");
        }

        return LimitExtractedContent(text);
    }

    private static bool AppendBounded(StringBuilder result, string value)
    {
        var remaining = MaximumExtractedCharacters - result.Length;
        if (remaining <= 0)
        {
            return false;
        }
        if (value.Length <= remaining)
        {
            result.Append(value);
            return true;
        }

        result.Append(value.AsSpan(0, remaining));
        return false;
    }

    private static string LimitExtractedContent(string content) => content.Length <= MaximumExtractedCharacters
        ? content.Trim()
        : content[..MaximumExtractedCharacters].TrimEnd() + "\n[Dokumentauszug gekürzt]";

    private static bool IsOpenXmlWordMediaType(string mediaType) => mediaType is
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document" or
        "application/vnd.ms-word.document.macroenabled.12" or
        "application/vnd.openxmlformats-officedocument.wordprocessingml.template" or
        "application/vnd.ms-word.template.macroenabled.12";

    private static bool LooksLikeZip(byte[] bytes) => bytes.Length >= 4
        && bytes[0] == (byte)'P'
        && bytes[1] == (byte)'K'
        && bytes[2] is 3 or 5 or 7
        && bytes[3] is 4 or 6 or 8;

    private static bool ContainsWordDocument(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            return archive.Entries.Any(static entry =>
                string.Equals(entry.FullName, "word/document.xml", StringComparison.OrdinalIgnoreCase));
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static bool LooksLikeText(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return true;
        }

        var sampleLength = Math.Min(bytes.Length, 8_192);
        var controls = 0;
        for (var index = 0; index < sampleLength; index++)
        {
            var value = bytes[index];
            if (value == 0)
            {
                return false;
            }
            if (value < 0x20 && value is not (byte)'\r' and not (byte)'\n' and not (byte)'\t')
            {
                controls++;
            }
        }

        return controls * 100 < sampleLength * 2;
    }

    private static string DecodeContent(byte[] bytes, string? charset)
    {
        Encoding encoding;
        try
        {
            encoding = string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            encoding = Encoding.UTF8;
        }

        return encoding.GetString(bytes);
    }

    private static string RtfToText(string rtf)
    {
        var result = new StringBuilder(rtf.Length);
        var depth = 0;
        var skipDepth = -1;
        for (var index = 0; index < rtf.Length; index++)
        {
            var current = rtf[index];
            if (current == '{') { depth++; continue; }
            if (current == '}') { if (depth == skipDepth) skipDepth = -1; depth--; continue; }
            if (skipDepth >= 0) continue;
            if (current != '\\') { result.Append(current); continue; }
            if (++index >= rtf.Length) break;
            current = rtf[index];
            if (current is '\\' or '{' or '}') { result.Append(current); continue; }
            if (current == '*') { skipDepth = depth; continue; }
            if (current == '\'')
            {
                if (index + 2 < rtf.Length
                    && byte.TryParse(
                        rtf.AsSpan(index + 1, 2),
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out var hex))
                {
                    result.Append((char)hex);
                    index += 2;
                }
                continue;
            }

            var start = index;
            while (index < rtf.Length && char.IsLetter(rtf[index])) index++;
            var word = rtf[start..index];
            while (index < rtf.Length && (char.IsDigit(rtf[index]) || rtf[index] == '-')) index++;
            if (index < rtf.Length && rtf[index] != ' ') index--;
            if (word is "par" or "line") result.AppendLine();
            else if (word == "tab") result.Append('\t');
        }

        return result.ToString().Trim();
    }

    private static string NormalizeHtml(string html)
    {
        var withoutScripts = ScriptAndStyleRegex().Replace(html, " ");
        // Inline markup does not insert a word boundary. Sphinx splits API names
        // across spans (asyncio.<span>timeout</span>); adding spaces breaks exact
        // targeted lookups. Structural elements still separate paragraphs/cells.
        var withoutInlineTags = InlineHtmlTagRegex().Replace(withoutScripts, string.Empty);
        var withoutTags = HtmlTagRegex().Replace(withoutInlineTags, " ");
        return WhitespaceRegex().Replace(WebUtility.HtmlDecode(withoutTags), " ").Trim();
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.Moved or
        HttpStatusCode.Redirect or
        HttpStatusCode.RedirectMethod or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    [GeneratedRegex("<(script|style)\\b[^>]*>.*?</\\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ScriptAndStyleRegex();

    [GeneratedRegex("</?(?:span|code|a|strong|em|b|i|u|s|small|mark|abbr|kbd|samp|var|sub|sup|wbr)\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InlineHtmlTagRegex();

    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    private sealed record YouTubeSearchItem(
        string VideoId,
        string Title,
        string Description,
        string Channel,
        DateTimeOffset? PublishedAt,
        string? ThumbnailUrl);
}
