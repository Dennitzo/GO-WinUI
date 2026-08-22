using GoAi.Contracts;
using System.Buffers;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GoAi.Client;

public sealed class GoAiClient : IDisposable
{
    private const int MaximumRateLimitRetries = 3;
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions = GoAiProtocol.CreateJsonOptions();
    private readonly bool _ownsHttpClient;
    private bool _disposed;

    public GoAiClient(HttpClient httpClient, string apiKey, bool ownsHttpClient = false)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("An API key is required.", nameof(apiKey));
        }

        _ownsHttpClient = ownsHttpClient;
        _httpClient.DefaultRequestHeaders.Remove(GoAiHeaders.ApiKey);
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(GoAiHeaders.ApiKey, apiKey);
    }

    public Task<HealthSnapshot> GetLiveHealthAsync(CancellationToken cancellationToken = default) =>
        GetAsync<HealthSnapshot>("v1/health/live", cancellationToken);

    public async Task<HealthSnapshot> GetReadyHealthAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var response = await _httpClient.GetAsync("v1/health/ready", cancellationToken).ConfigureAwait(false);
        // A readiness endpoint intentionally uses HTTP 503 while returning a
        // valid HealthSnapshot with the exact missing dependency and repair
        // instruction. Preserve that diagnostic instead of turning a reachable
        // server into a generic transport failure.
        if (response.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable)
        {
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        }

        return await response.Content
            .ReadFromJsonAsync<HealthSnapshot>(_jsonOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new JsonException("The server returned no HealthSnapshot payload.");
    }

    public Task<CapabilitySnapshot> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        GetAsync<CapabilitySnapshot>("v1/capabilities", cancellationToken);

    public Task<ModelStatusSnapshot> GetModelStatusAsync(CancellationToken cancellationToken = default) =>
        GetAsync<ModelStatusSnapshot>("v1/models/status", cancellationToken);

    public Task<GeneralModelSelection> SelectGeneralModelAsync(
        string modelId,
        CancellationToken cancellationToken = default) =>
        PostAsync<GeneralModelSelection, GeneralModelSelection>(
            "v1/models/general",
            new GeneralModelSelection(modelId, 0, false),
            cancellationToken);

    public Task<CodingModelSelection> SelectCodingModelAsync(
        string modelId,
        CancellationToken cancellationToken = default) =>
        PostAsync<CodingModelSelection, CodingModelSelection>(
            "v1/models/code",
            new CodingModelSelection(modelId, string.Empty, 0, false),
            cancellationToken);

    public Task<GpuStatusSnapshot> GetGpuStatusAsync(CancellationToken cancellationToken = default) =>
        GetAsync<GpuStatusSnapshot>("v1/gpu/status", cancellationToken);

    public Task<EmbeddingBatchResponse> CreateEmbeddingsAsync(
        EmbeddingBatchRequest request,
        CancellationToken cancellationToken = default) =>
        PostAsync<EmbeddingBatchRequest, EmbeddingBatchResponse>(
            "v1/context/embeddings",
            request,
            cancellationToken);

    public async Task ReleaseEmbeddingModelAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var response = await _httpClient.PostAsync(
            "v1/context/embeddings/release",
            content: null,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<ServiceStatusSnapshot>> GetServiceStatusAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<ServiceStatusSnapshot>>("v1/services/status", cancellationToken);

    public Task<WebSearchResponse> SearchWebAsync(WebSearchRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<WebSearchRequest, WebSearchResponse>("v1/research/web", request, cancellationToken);

    public Task<WebSearchResponse> SearchYouTubeAsync(WebSearchRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<WebSearchRequest, WebSearchResponse>("v1/research/youtube", request, cancellationToken);

    public Task<WebFetchResponse> FetchWebAsync(WebFetchRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<WebFetchRequest, WebFetchResponse>("v1/research/fetch", request, cancellationToken);

    public Task<TranscriptionResponse> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<TranscriptionRequest, TranscriptionResponse>("v1/audio/transcriptions", request, cancellationToken);

    public Task<SpeechResponse> SynthesizeSpeechAsync(SpeechRequest request, CancellationToken cancellationToken = default) =>
        PostWithRateLimitRetryAsync<SpeechRequest, SpeechResponse>("v1/audio/speech", request, cancellationToken);

    public Task<SpeechSessionSnapshot> CreateSpeechSessionAsync(
        SpeechSessionRequest request,
        CancellationToken cancellationToken = default) =>
        PostWithRateLimitRetryAsync<SpeechSessionRequest, SpeechSessionSnapshot>(
            "v1/audio/speech/sessions",
            request,
            cancellationToken);

    public Task<SpeechParagraphResponse> SynthesizeSpeechParagraphAsync(
        string sessionId,
        SpeechParagraphRequest request,
        CancellationToken cancellationToken = default) =>
        PostWithRateLimitRetryAsync<SpeechParagraphRequest, SpeechParagraphResponse>(
            $"v1/audio/speech/sessions/{Uri.EscapeDataString(sessionId)}/paragraphs",
            request,
            cancellationToken);

    public Task<SpeechSessionSnapshot> EndSpeechSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        PostWithoutBodyWithRateLimitRetryAsync<SpeechSessionSnapshot>(
            $"v1/audio/speech/sessions/{Uri.EscapeDataString(sessionId)}/end",
            cancellationToken);

    public Task<SpeechSessionSnapshot> CancelSpeechSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        PostWithoutBodyWithRateLimitRetryAsync<SpeechSessionSnapshot>(
            $"v1/audio/speech/sessions/{Uri.EscapeDataString(sessionId)}/cancel",
            cancellationToken);

    public Task<UtteranceIntentResponse> ClassifyUtteranceIntentAsync(
        UtteranceIntentRequest request,
        CancellationToken cancellationToken = default) =>
        PostAsync<UtteranceIntentRequest, UtteranceIntentResponse>(
            "v1/audio/utterance-intent", request, cancellationToken);

    public Task<LiveCaptionSessionSnapshot> CreateLiveCaptionSessionAsync(
        LiveCaptionSessionRequest request,
        CancellationToken cancellationToken = default) =>
        PostAsync<LiveCaptionSessionRequest, LiveCaptionSessionSnapshot>(
            "v1/audio/live-captions/sessions",
            request,
            cancellationToken);

    public Task<LiveCaptionSessionSnapshot> GetLiveCaptionSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        GetAsync<LiveCaptionSessionSnapshot>(
            $"v1/audio/live-captions/sessions/{Uri.EscapeDataString(sessionId)}",
            cancellationToken);

    public Task<LiveCaptionSessionSnapshot> KeepLiveCaptionSessionAliveAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        PostWithoutBodyAsync<LiveCaptionSessionSnapshot>(
            $"v1/audio/live-captions/sessions/{Uri.EscapeDataString(sessionId)}/heartbeat",
            cancellationToken);

    public async Task<LiveCaptionChunkResponse> SendLiveCaptionChunkAsync(
        string sessionId,
        long sequence,
        ReadOnlyMemory<byte> waveAudio,
        CancellationToken cancellationToken = default) =>
        await SendLiveCaptionChunkAsync(
            sessionId,
            sequence,
            waveAudio,
            metadata: null,
            cancellationToken).ConfigureAwait(false);

    public async Task<LiveCaptionChunkResponse> SendLiveCaptionChunkAsync(
        string sessionId,
        long sequence,
        ReadOnlyMemory<byte> waveAudio,
        LiveCaptionChunkMetadata? metadata,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        if (waveAudio.Length is < 44 or > GoAiProtocol.MaximumLiveCaptionChunkBytes)
        {
            throw new ArgumentException("Live-caption WAV chunk has an invalid size.", nameof(waveAudio));
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"v1/audio/live-captions/sessions/{Uri.EscapeDataString(sessionId)}/chunks/{sequence}")
        {
            Content = new ReadOnlyMemoryContent(waveAudio),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        if (metadata is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(metadata.TurnId);
            ArgumentOutOfRangeException.ThrowIfNegative(metadata.Revision);
            ArgumentOutOfRangeException.ThrowIfNegative(metadata.WindowStartMilliseconds);
            request.Headers.TryAddWithoutValidation(GoAiHeaders.CaptionTurnId, metadata.TurnId);
            request.Headers.TryAddWithoutValidation(
                GoAiHeaders.CaptionRevision,
                metadata.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
            request.Headers.TryAddWithoutValidation(
                GoAiHeaders.CaptionWindowStartMilliseconds,
                metadata.WindowStartMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            request.Headers.TryAddWithoutValidation(GoAiHeaders.CaptionFinal, metadata.IsFinal ? "true" : "false");
        }
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync<LiveCaptionChunkResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    public Task<LiveCaptionSessionSnapshot> StopLiveCaptionSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        PostWithoutBodyAsync<LiveCaptionSessionSnapshot>(
            $"v1/audio/live-captions/sessions/{Uri.EscapeDataString(sessionId)}/stop",
            cancellationToken);

    public Task<RunAccepted> GenerateImageAsync(
        ImageGenerationRequest request,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default) =>
        PostRunWorkloadAsync("v1/images/generations", request, idempotencyKey, cancellationToken);

    public Task<RunAccepted> AnalyzeMediaAsync(
        MediaJobRequest request,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default) =>
        PostRunWorkloadAsync("v1/media/analyze", request, idempotencyKey, cancellationToken);

    public async Task<RunAccepted> CreateRunAsync(
        RunRequest request,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/runs")
        {
            Content = JsonContent.Create(request, options: _jsonOptions),
        };
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            message.Headers.TryAddWithoutValidation(GoAiHeaders.IdempotencyKey, idempotencyKey);
        }

        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync<RunAccepted>(response, cancellationToken).ConfigureAwait(false);
    }

    public Task<RunSnapshot> GetRunAsync(string runId, CancellationToken cancellationToken = default) =>
        GetAsync<RunSnapshot>($"v1/runs/{Uri.EscapeDataString(runId)}", cancellationToken);

    public async Task CancelRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var response = await _httpClient.PostAsync(
            $"v1/runs/{Uri.EscapeDataString(runId)}/cancel",
            content: null,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task SubmitClientToolResultAsync(
        string runId,
        ClientToolResult result,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var response = await _httpClient.PostAsJsonAsync(
            $"v1/runs/{Uri.EscapeDataString(runId)}/client-tool-results",
            result,
            _jsonOptions,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<RunEvent> StreamRunEventsAsync(
        string runId,
        long lastEventId = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"v1/runs/{Uri.EscapeDataString(runId)}/events");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (lastEventId > 0)
        {
            request.Headers.TryAddWithoutValidation(GoAiHeaders.LastEventId, lastEventId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: false);

        var data = new StringBuilder();
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    var item = JsonSerializer.Deserialize<RunEvent>(data.ToString(), _jsonOptions)
                        ?? throw new JsonException("The server returned an empty SSE event.");
                    data.Clear();
                    yield return item;
                }

                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(line.AsSpan(5).TrimStart());
            }
        }
    }

    public async Task<UploadCreated> CreateUploadAsync(
        UploadManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var response = await _httpClient.PostAsJsonAsync(
            "v1/uploads",
            manifest,
            _jsonOptions,
            cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync<UploadCreated>(response, cancellationToken).ConfigureAwait(false);
    }

    public Task<UploadCreated> GetUploadAsync(
        string uploadId,
        CancellationToken cancellationToken = default) =>
        GetAsync<UploadCreated>($"v1/uploads/{Uri.EscapeDataString(uploadId)}", cancellationToken);

    public async Task DeleteUploadAsync(
        string uploadId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var response = await _httpClient.DeleteAsync(
            $"v1/uploads/{Uri.EscapeDataString(uploadId)}",
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UploadChunkReceipt> PutUploadChunkAsync(
        string uploadId,
        int index,
        Stream content,
        string sha256,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"v1/uploads/{Uri.EscapeDataString(uploadId)}/chunks/{index}")
        {
            Content = new StreamContent(content),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.TryAddWithoutValidation("X-Chunk-SHA256", sha256);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync<UploadChunkReceipt>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UploadCompleted> CompleteUploadAsync(
        string uploadId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var response = await _httpClient.PostAsync(
            $"v1/uploads/{Uri.EscapeDataString(uploadId)}/complete",
            content: null,
            cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync<UploadCompleted>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UploadCompleted> UploadFileAsync(
        string filePath,
        string mediaType,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(filePath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("Upload file does not exist.", filePath);
        }

        var overallHash = await HashFileAsync(filePath, cancellationToken).ConfigureAwait(false);
        var chunkCount = checked((int)Math.Ceiling(file.Length / (double)GoAiProtocol.UploadChunkSize));
        var upload = await CreateUploadAsync(
            new UploadManifest(file.Name, mediaType, file.Length, overallHash, GoAiProtocol.UploadChunkSize, chunkCount),
            cancellationToken).ConfigureAwait(false);
        return await UploadFileCoreAsync(file, upload, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UploadCompleted> ResumeUploadFileAsync(
        string uploadId,
        string filePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(filePath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("Upload file does not exist.", filePath);
        }

        var upload = await GetUploadAsync(uploadId, cancellationToken).ConfigureAwait(false);
        var expectedChunkCount = checked((int)Math.Ceiling(file.Length / (double)upload.ChunkSize));
        if (upload.ChunkSize != GoAiProtocol.UploadChunkSize || upload.ChunkCount != expectedChunkCount)
        {
            throw new InvalidDataException("The local file does not match the resumable upload shape.");
        }

        return await UploadFileCoreAsync(file, upload, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<UploadCompleted> UploadFileCoreAsync(
        FileInfo file,
        UploadCreated upload,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var received = upload.ReceivedChunks.ToHashSet();
        var buffer = ArrayPool<byte>.Shared.Rent(upload.ChunkSize);
        try
        {
            await using var stream = new FileStream(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                upload.ChunkSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            for (var index = 0; index < upload.ChunkCount; index++)
            {
                var requested = (int)Math.Min(upload.ChunkSize, stream.Length - stream.Position);
                var read = await ReadExactlyOrToEndAsync(stream, buffer, requested, cancellationToken).ConfigureAwait(false);
                if (!received.Contains(index))
                {
                    var hash = Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, read))).ToLowerInvariant();
                    await using var chunk = new MemoryStream(buffer, 0, read, writable: false, publiclyVisible: true);
                    _ = await PutUploadChunkAsync(upload.UploadId, index, chunk, hash, cancellationToken).ConfigureAwait(false);
                }

                progress?.Report((index + 1d) / upload.ChunkCount);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return await CompleteUploadAsync(upload.UploadId, cancellationToken).ConfigureAwait(false);
    }

    public async Task DownloadArtifactAsync(
        string artifactId,
        string destinationPath,
        long existingLength = 0,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(existingLength);
        if (existingLength > 0)
        {
            var destination = new FileInfo(destinationPath);
            if (!destination.Exists || destination.Length != existingLength)
            {
                throw new InvalidDataException("The local artifact length does not match the requested resume offset.");
            }
        }

        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"v1/artifacts/{Uri.EscapeDataString(artifactId)}");
            if (existingLength > 0)
            {
                request.Headers.Range = new RangeHeaderValue(existingLength, null);
            }

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (await WaitForRateLimitRetryAsync(response, attempt, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            if (existingLength > 0
                && (response.StatusCode != System.Net.HttpStatusCode.PartialContent
                    || response.Content.Headers.ContentRange?.From != existingLength))
            {
                throw new InvalidDataException("The server did not honor the artifact resume range.");
            }
            var mode = existingLength > 0 ? FileMode.Append : FileMode.Create;
            await using var output = new FileStream(destinationPath, mode, FileAccess.Write, FileShare.None, 81920, true);
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            return;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var response = await _httpClient.GetAsync(path, cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(
        string path,
        TRequest value,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var response = await _httpClient.PostAsJsonAsync(path, value, _jsonOptions, cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync<TResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResponse> PostWithRateLimitRetryAsync<TRequest, TResponse>(
        string path,
        TRequest value,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        for (var attempt = 0; ; attempt++)
        {
            using var response = await _httpClient.PostAsJsonAsync(
                path,
                value,
                _jsonOptions,
                cancellationToken).ConfigureAwait(false);
            if (await WaitForRateLimitRetryAsync(response, attempt, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            return await ReadRequiredAsync<TResponse>(response, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<TResponse> PutAsync<TRequest, TResponse>(
        string path,
        TRequest value,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var response = await _httpClient.PutAsJsonAsync(
            path,
            value,
            _jsonOptions,
            cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync<TResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResponse> PostWithoutBodyAsync<TResponse>(
        string path,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync<TResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResponse> PostWithoutBodyWithRateLimitRetryAsync<TResponse>(
        string path,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (await WaitForRateLimitRetryAsync(response, attempt, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            return await ReadRequiredAsync<TResponse>(response, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> WaitForRateLimitRetryAsync(
        HttpResponseMessage response,
        int attempt,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode != System.Net.HttpStatusCode.TooManyRequests
            || attempt >= MaximumRateLimitRetries)
        {
            return false;
        }

        var retryAfter = response.Headers.RetryAfter;
        var delay = retryAfter?.Delta
            ?? (retryAfter?.Date is { } retryAt
                ? retryAt - DateTimeOffset.UtcNow
                : TimeSpan.FromSeconds(Math.Min(8, 1 << attempt)));
        delay = TimeSpan.FromMilliseconds(Math.Clamp(delay.TotalMilliseconds, 0, TimeSpan.FromSeconds(65).TotalMilliseconds));
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<RunAccepted> PostRunWorkloadAsync<TRequest>(
        string path,
        TRequest value,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var message = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(value, options: _jsonOptions),
        };
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            message.Headers.TryAddWithoutValidation(GoAiHeaders.IdempotencyKey, idempotencyKey);
        }

        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync<RunAccepted>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> ReadRequiredAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(_jsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException($"The server returned no {typeof(T).Name} payload.");
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        GoAiProblem? problem = null;
        try
        {
            problem = await response.Content.ReadFromJsonAsync<GoAiProblem>(_jsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // A non-conforming intermediary response is represented by the status code below.
        }

        throw new GoAiApiException(
            problem?.Detail ?? $"GO AI Server returned HTTP {(int)response.StatusCode}.",
            (int)response.StatusCode,
            problem);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<int> ReadExactlyOrToEndAsync(
        Stream stream,
        byte[] buffer,
        int requested,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < requested)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, requested - total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
