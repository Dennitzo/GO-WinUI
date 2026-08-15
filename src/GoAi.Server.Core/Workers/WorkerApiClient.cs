using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Security;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace GoAi.Server.Core.Workers;

public sealed class WorkerApiClient
{
    private readonly HttpClient _httpClient;
    private readonly WorkerKeyStore _keys;
    private readonly GoAiServerOptions _options;
    private readonly JsonSerializerOptions _jsonOptions = GoAiProtocol.CreateJsonOptions();

    public WorkerApiClient(
        HttpClient httpClient,
        WorkerKeyStore keys,
        IOptions<GoAiServerOptions> options)
    {
        _httpClient = httpClient;
        _keys = keys;
        _options = options.Value;
        _httpClient.Timeout = TimeSpan.FromHours(2);
    }

    public Task<TranscriptionResponse> TranscribeAsync(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<TranscriptionResponse>(
            "speech",
            _options.SpeechWorkerUri,
            "/transcriptions",
            request,
            cancellationToken);

    public async Task<TranscriptionResponse> TranscribeLiveCaptionAsync(
        ReadOnlyMemory<byte> waveAudio,
        string? language,
        LiveCaptionMode mode,
        string sessionId,
        string? previousContext,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(_options.SpeechWorkerUri, "/live-captions"));
        request.Headers.TryAddWithoutValidation(
            GoAiHeaders.WorkerKey,
            await _keys.ReadAsync("speech", cancellationToken).ConfigureAwait(false));
        if (!string.IsNullOrWhiteSpace(language))
        {
            request.Headers.TryAddWithoutValidation("X-GO-AI-Caption-Language", language);
        }
        request.Headers.TryAddWithoutValidation(
            "X-GO-AI-Caption-Task",
            mode == LiveCaptionMode.TranslateToEnglish ? "translate" : "transcribe");
        request.Headers.TryAddWithoutValidation("X-GO-AI-Caption-Session", sessionId);
        if (!string.IsNullOrWhiteSpace(previousContext))
        {
            var boundedContext = previousContext.Length <= 1_000
                ? previousContext
                : previousContext[^1_000..];
            request.Headers.TryAddWithoutValidation(
                "X-GO-AI-Caption-Context-B64",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(boundedContext)));
        }
        request.Content = new ReadOnlyMemoryContent(waveAudio);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Worker speech returned HTTP {(int)response.StatusCode}.",
                inner: null,
                response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<TranscriptionResponse>(
            _jsonOptions,
            cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException("Worker speech returned an empty live-caption response.");
    }

    public Task<WorkerSpeechResult> SynthesizeAsync(
        SpeechRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<WorkerSpeechResult>(
            "speech",
            _options.SpeechWorkerUri,
            "/speech",
            request,
            cancellationToken);

    public Task<JsonElement> LoadSpeechComponentAsync(
        string component,
        CancellationToken cancellationToken = default)
    {
        if (component is not ("stt" or "tts" or "speaker"))
        {
            throw new ArgumentOutOfRangeException(nameof(component));
        }

        return SendAsync<JsonElement>(
            "speech",
            _options.SpeechWorkerUri,
            "/load",
            new { component },
            cancellationToken);
    }

    public Task<JsonElement> LoadWorkerAsync(
        string workerName,
        CancellationToken cancellationToken = default) =>
        SendAsync<JsonElement>(
            workerName,
            ResolveWorkerUri(workerName),
            "/load",
            body: null,
            cancellationToken);

    public async Task<JsonElement> GetStatusAsync(
        string workerName,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        return await SendGetAsync<JsonElement>(
            workerName,
            ResolveWorkerUri(workerName),
            "/status",
            timeout.Token).ConfigureAwait(false);
    }

    public Task<WorkerImageResult> GenerateImageAsync(
        ImageGenerationRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<WorkerImageResult>(
            "image",
            _options.ImageWorkerUri,
            "/generate",
            request,
            cancellationToken);

    public Task<WorkerMediaResult> InspectMediaAsync(
        WorkerMediaRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<WorkerMediaResult>(
            "media",
            _options.MediaWorkerUri,
            "/inspect",
            request,
            cancellationToken);

    public async Task ReleaseAllAsync(string? exceptWorker, CancellationToken cancellationToken = default)
    {
        var definitions = new[]
        {
            (Name: "speech", Uri: _options.SpeechWorkerUri),
            (Name: "media", Uri: _options.MediaWorkerUri),
            (Name: "image", Uri: _options.ImageWorkerUri),
        };
        foreach (var definition in definitions)
        {
            if (string.Equals(definition.Name, exceptWorker, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                _ = await SendAsync<JsonElement>(
                    definition.Name,
                    definition.Uri,
                    "/release",
                    body: null,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                // A stopped optional worker has no resources to release.
            }
        }
    }

    public async Task ReleaseAsync(string workerName, CancellationToken cancellationToken = default)
    {
        var uri = ResolveWorkerUri(workerName);
        _ = await SendAsync<JsonElement>(workerName, uri, "/release", null, cancellationToken).ConfigureAwait(false);
    }

    private Uri ResolveWorkerUri(string workerName) => workerName switch
    {
        "speech" => _options.SpeechWorkerUri,
        "media" => _options.MediaWorkerUri,
        "image" => _options.ImageWorkerUri,
        _ => throw new ArgumentOutOfRangeException(nameof(workerName)),
    };

    private async Task<T> SendGetAsync<T>(
        string workerName,
        Uri baseUri,
        string path,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, path));
        request.Headers.TryAddWithoutValidation(
            GoAiHeaders.WorkerKey,
            await _keys.ReadAsync(workerName, cancellationToken).ConfigureAwait(false));
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Worker {workerName} returned HTTP {(int)response.StatusCode}.",
                inner: null,
                response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<T>(_jsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException($"Worker {workerName} returned an empty response.");
    }

    private async Task<T> SendAsync<T>(
        string workerName,
        Uri baseUri,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, path));
        request.Headers.TryAddWithoutValidation(GoAiHeaders.WorkerKey, await _keys.ReadAsync(workerName, cancellationToken).ConfigureAwait(false));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: _jsonOptions);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Worker {workerName} returned HTTP {(int)response.StatusCode}.",
                inner: null,
                response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<T>(_jsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException($"Worker {workerName} returned an empty response.");
    }
}

public sealed record WorkerArtifact(
    string RelativePath,
    string FileName,
    string MediaType,
    string? Role = null,
    double? TimecodeSeconds = null,
    string? Group = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record WorkerSpeechResult(
    string RelativePath,
    string FileName,
    string MediaType,
    string Provider,
    bool IsFallback,
    IReadOnlyDictionary<string, string>? Metadata);

public sealed record WorkerImageResult(
    string Provider,
    string Model,
    long DurationMilliseconds,
    IReadOnlyList<WorkerArtifact> Artifacts);

public sealed record WorkerMediaRequest(
    string UploadId,
    string MediaType,
    IReadOnlyList<WorkerTimeWindow>? DetailWindows = null);

public sealed record WorkerTimeWindow(double Start, double End);

public sealed record WorkerMediaResult(
    string Kind,
    JsonElement Metadata,
    IReadOnlyList<WorkerArtifact> Artifacts,
    IReadOnlyList<WorkerArtifact> Frames);
