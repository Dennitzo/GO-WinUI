using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace GoAi.Server.Core.Research;

public sealed partial class WebResearchService
{
    private const int MaximumFetchBytes = 5 * 1024 * 1024;
    private const int MaximumRedirects = 5;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GoAiServerOptions _options;

    public WebResearchService(IHttpClientFactory httpClientFactory, IOptions<GoAiServerOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
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

        var maximum = Math.Clamp(request.MaximumResults, 1, 20);
        if (youtubeFallback && !string.IsNullOrWhiteSpace(_options.YouTubeApiKey))
        {
            return await SearchYouTubeAsync(request, maximum, _options.YouTubeApiKey, cancellationToken).ConfigureAwait(false);
        }

        var query = youtubeFallback ? $"site:youtube.com/watch {request.Query}" : request.Query;
        var builder = new UriBuilder(new Uri(_options.SearxngUri, "/search"))
        {
            Query = $"q={Uri.EscapeDataString(query)}&format=json&language={Uri.EscapeDataString(request.Language ?? "de-DE")}",
        };
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
                    GetString(raw, "thumbnail")));
            }
        }

        return new WebSearchResponse(
            request.Query,
            results,
            "searxng",
            youtubeFallback,
            DateTimeOffset.UtcNow);
    }

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
            using var message = new HttpRequestMessage(HttpMethod.Get, current);
            message.Headers.UserAgent.ParseAdd("GO-AI-Server/1.0");
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
                throw new HttpRequestException("Fetch response exceeds the 5 MiB limit.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var bytes = await ReadBoundedAsync(stream, MaximumFetchBytes, cancellationToken).ConfigureAwait(false);
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            var content = DecodeContent(bytes, response.Content.Headers.ContentType?.CharSet);
            if (mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                content = NormalizeHtml(content);
            }

            return new WebFetchResponse(current.ToString(), mediaType, content, true, DateTimeOffset.UtcNow, redirects);
        }

        throw new HttpRequestException("Fetch failed unexpectedly.");
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
                throw new HttpRequestException("Fetch response exceeds the 5 MiB limit.");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
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

    private static string NormalizeHtml(string html)
    {
        var withoutScripts = ScriptAndStyleRegex().Replace(html, " ");
        var withoutTags = HtmlTagRegex().Replace(withoutScripts, " ");
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
