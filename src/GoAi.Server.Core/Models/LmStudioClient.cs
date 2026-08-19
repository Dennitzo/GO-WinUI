using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Security;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace GoAi.Server.Core.Models;

public sealed partial class LmStudioClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly GoAiServerOptions _options;
    private readonly DpapiSecretStore _secretStore;
    private readonly ILogger<LmStudioClient> _logger;
    private readonly SemaphoreSlim _modelOperationGate = new(1, 1);
    private readonly SemaphoreSlim _statusGate = new(1, 1);
    private readonly object _idleTimerSync = new();
    private readonly object _statusCacheSync = new();
    private CancellationTokenSource? _idleUnloadCancellation;
    private ModelStatusSnapshot? _cachedStatus;
    private DateTimeOffset _statusCacheExpiresAt;
    private static readonly TimeSpan StatusCacheDuration = TimeSpan.FromSeconds(15);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
    };

    public LmStudioClient(
        HttpClient httpClient,
        IOptions<GoAiServerOptions> options,
        DpapiSecretStore secretStore,
        ILogger<LmStudioClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _secretStore = secretStore;
        _logger = logger;
        _httpClient.BaseAddress = EnsureTrailingSlash(_options.LmStudioUri);
        _httpClient.Timeout = TimeSpan.FromMinutes(30);
    }

    public async Task<ModelStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var cached = GetCachedStatus();
        if (cached is not null)
        {
            return cached;
        }

        await _statusGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = GetCachedStatus();
            if (cached is not null)
            {
                return cached;
            }

            using var request = await CreateRequestAsync(HttpMethod.Get, "api/v1/models", null, cancellationToken).ConfigureAwait(false);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<LmStudioModelList>(_jsonOptions, cancellationToken).ConfigureAwait(false)
                ?? new LmStudioModelList([]);
            var models = CreateConfiguredModelStatus(result.Models);
            return CacheStatus(new ModelStatusSnapshot(true, _options.LmStudioUri.ToString(), models, DateTimeOffset.UtcNow));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return CacheStatus(new ModelStatusSnapshot(
                false,
                _options.LmStudioUri.ToString(),
                CreateConfiguredModelStatus([]),
                DateTimeOffset.UtcNow,
                exception is TaskCanceledException ? "lmstudio.timeout" : "lmstudio.unreachable"));
        }
        finally
        {
            _statusGate.Release();
        }
    }

    public Task<bool> HasConfiguredTokenAsync(CancellationToken cancellationToken = default) =>
        HasConfiguredTokenCoreAsync(cancellationToken);

    public async Task<string> EnsureModelLoadedAsync(
        string modelId,
        int contextLength,
        CancellationToken cancellationToken = default)
    {
        await BeginModelOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = await GetRawModelsAsync(cancellationToken).ConfigureAwait(false);
            var selected = status.Models.FirstOrDefault(model => string.Equals(model.Key, modelId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Configured LM Studio model is not downloaded: {modelId}");
            if (string.Equals(modelId, _options.CodeModelId, StringComparison.OrdinalIgnoreCase)
                && selected.MaximumContextLength < contextLength)
            {
                throw new LmStudioContextLengthException(modelId, contextLength, selected.MaximumContextLength);
            }
            var expectedContextLength = Math.Min(contextLength, selected.MaximumContextLength);
            var loaded = selected.LoadedInstances is { Count: > 0 } loadedInstances
                ? loadedInstances[0]
                : null;
            var isEmbedding = string.Equals(selected.Type, "embedding", StringComparison.OrdinalIgnoreCase);
            await UnloadIncompatibleModelsAsync(status.Models, modelId, cancellationToken).ConfigureAwait(false);
            if (loaded is not null && HasRequiredConfiguration(loaded, expectedContextLength, isEmbedding))
            {
                InvalidateStatusCache();
                return loaded.ModelInstanceId ?? loaded.Id ?? modelId;
            }

            if (loaded is not null)
            {
                await UnloadModelInstancesAsync([selected], cancellationToken).ConfigureAwait(false);
            }
            // The installed LM Studio load API rejects a `ttl` property. Explicit
            // loads are released by this client's own ModelTtlSeconds idle timer;
            // inference requests still carry LM Studio's supported JIT TTL.
            object body = isEmbedding
                ? new
                {
                    model = modelId,
                    context_length = expectedContextLength,
                    echo_load_config = true,
                }
                : new
                {
                    model = modelId,
                    context_length = expectedContextLength,
                    parallel = 1,
                    flash_attention = true,
                    offload_kv_cache_to_gpu = true,
                    echo_load_config = true,
                };
            return await LoadModelWithRetryAsync(
                modelId,
                body,
                expectedContextLength,
                isEmbedding,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            EndModelOperation();
        }
    }

    public async Task UnloadAllModelsAsync(CancellationToken cancellationToken = default)
    {
        await BeginModelOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = await GetRawModelsAsync(cancellationToken).ConfigureAwait(false);
            await UnloadModelInstancesAsync(status.Models, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            InvalidateStatusCache();
            CancelIdleUnload();
            _modelOperationGate.Release();
        }
    }

    public async Task UnloadModelsExceptAsync(
        IReadOnlyCollection<string> preservedModelIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preservedModelIds);
        await BeginModelOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var preserved = new HashSet<string>(preservedModelIds, StringComparer.OrdinalIgnoreCase);
            var status = await GetRawModelsAsync(cancellationToken).ConfigureAwait(false);
            await UnloadModelInstancesAsync(
                status.Models.Where(model => !preserved.Contains(model.Key)),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            InvalidateStatusCache();
            EndModelOperation();
        }
    }

    public async IAsyncEnumerable<LmStudioResponseEvent> StreamResponseAsync(
        string modelId,
        IReadOnlyList<RunMessage> messages,
        string systemPolicy,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await BeginModelOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        var input = new List<object>
        {
            new { role = "system", content = systemPolicy },
        };
        foreach (var message in messages)
        {
            var text = string.Join(
                "\n",
                message.Content.Where(static part => !string.IsNullOrWhiteSpace(part.Text)).Select(static part => part.Text));
            if (!string.IsNullOrWhiteSpace(text))
            {
                input.Add(new { role = NormalizeRole(message.Role), content = text });
            }
        }

        var body = new
        {
            model = modelId,
            input,
            stream = true,
            reasoning = new { effort = "low" },
            ttl = _options.ModelTtlSeconds,
        };
        using var request = await CreateRequestAsync(HttpMethod.Post, "v1/responses", body, cancellationToken).ConfigureAwait(false);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: false);
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null || !line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var json = line.AsSpan(5).TrimStart().ToString();
            if (json.Length == 0 || string.Equals(json, "[DONE]", StringComparison.Ordinal))
            {
                continue;
            }

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            if (string.Equals(type, "response.output_text.delta", StringComparison.Ordinal)
                && root.TryGetProperty("delta", out var deltaElement))
            {
                yield return new LmStudioResponseEvent("response.output_text.delta", deltaElement.GetString(), null, null);
            }
            else if (string.Equals(type, "response.completed", StringComparison.Ordinal))
            {
                var usage = TryReadUsage(root);
                yield return new LmStudioResponseEvent("response.completed", null, usage.InputTokens, usage.OutputTokens);
            }
            else if (string.Equals(type, "error", StringComparison.Ordinal)
                || string.Equals(type, "response.failed", StringComparison.Ordinal))
            {
                var message = root.TryGetProperty("message", out var messageElement)
                    ? messageElement.GetString()
                    : "LM Studio response failed.";
                throw new InvalidOperationException(message);
            }
        }
        }
        finally
        {
            EndModelOperation();
        }
    }

    public async Task<LmChatResult> CompleteChatAsync(
        string modelId,
        IReadOnlyList<LmChatMessage> messages,
        IReadOnlyList<LmToolDefinition> tools,
        int maximumOutputTokens = 8_192,
        CancellationToken cancellationToken = default)
    {
        await BeginModelOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        var instructions = string.Join(
            "\n\n",
            messages
                .Where(static message => string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase))
                .Select(static message => message.Content)
                .Where(static content => !string.IsNullOrWhiteSpace(content)));
        var input = CreateResponsesInput(messages);
        var toolPayload = tools.Select(static tool => new
        {
            type = "function",
            name = tool.Name,
            description = tool.Description,
            parameters = tool.Parameters,
        }).ToArray();
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = modelId,
            ["instructions"] = instructions,
            ["input"] = input,
            ["stream"] = false,
            ["temperature"] = 0.2,
            ["max_output_tokens"] = Math.Clamp(maximumOutputTokens, 1, 65_536),
            ["reasoning"] = new { effort = "low" },
            ["ttl"] = _options.ModelTtlSeconds,
            ["store"] = false,
        };
        if (toolPayload.Length > 0)
        {
            body["tools"] = toolPayload;
            body["tool_choice"] = "auto";
            body["parallel_tool_calls"] = false;
        }

        HttpResponseMessage response;
        try
        {
            response = await SendResponsesWithRetryAsync(modelId, body, cancellationToken).ConfigureAwait(false);
        }
        catch (ResponsesCompatibilityException exception)
        {
            LogResponsesEndpointFallback((int)exception.StatusCode, modelId);
            return await CompleteChatViaChatCompletionsAsync(
                modelId,
                messages,
                tools,
                maximumOutputTokens,
                cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception) when (
            exception.StatusCode is { } statusCode && IsTransientProviderStatus(statusCode))
        {
            LogResponsesEndpointFallback((int)statusCode, modelId);
            return await CompleteChatViaChatCompletionsAsync(
                modelId,
                messages,
                tools,
                maximumOutputTokens,
                cancellationToken).ConfigureAwait(false);
        }

        using var responseScope = response;
        using var document = JsonDocument.Parse(await responseScope.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        var root = document.RootElement;
        if (root.TryGetProperty("status", out var status)
            && status.ValueKind == JsonValueKind.String
            && !string.Equals(status.GetString(), "completed", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("LM Studio Responses request did not complete.");
        }
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("LM Studio Responses result contains no output array.");
        }

        var textParts = new List<string>();
        var calls = new List<LmToolCall>();
        foreach (var item in output.EnumerateArray())
        {
            var type = item.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString()
                : null;
            if (string.Equals(type, "message", StringComparison.Ordinal))
            {
                if (!item.TryGetProperty("content", out var messageContent)
                    || messageContent.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                foreach (var contentItem in messageContent.EnumerateArray())
                {
                    if (contentItem.TryGetProperty("type", out var contentType)
                        && string.Equals(contentType.GetString(), "output_text", StringComparison.Ordinal)
                        && contentItem.TryGetProperty("text", out var textElement)
                        && textElement.ValueKind == JsonValueKind.String
                        && !string.IsNullOrEmpty(textElement.GetString()))
                    {
                        textParts.Add(textElement.GetString()!);
                    }
                }
                continue;
            }

            if (!string.Equals(type, "function_call", StringComparison.Ordinal))
            {
                // Reasoning and provider metadata are intentionally neither exposed nor persisted.
                continue;
            }

            var callId = item.TryGetProperty("call_id", out var callIdElement)
                ? callIdElement.GetString()
                : item.TryGetProperty("id", out var idElement)
                    ? idElement.GetString()
                    : null;
            var name = item.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            var argumentsText = item.TryGetProperty("arguments", out var argumentsElement)
                ? argumentsElement.ValueKind == JsonValueKind.String
                    ? argumentsElement.GetString()
                    : argumentsElement.GetRawText()
                : null;
            if (string.IsNullOrWhiteSpace(callId)
                || string.IsNullOrWhiteSpace(name)
                || string.IsNullOrWhiteSpace(argumentsText))
            {
                throw new JsonException("LM Studio returned an incomplete structured function call.");
            }

            using var argumentsDocument = JsonDocument.Parse(argumentsText);
            calls.Add(new LmToolCall(callId, name, argumentsDocument.RootElement.Clone()));
        }

        var inputTokens = 0;
        var outputTokens = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            inputTokens = TryReadInt32(usage, "input_tokens");
            outputTokens = TryReadInt32(usage, "output_tokens");
        }

        var content = textParts.Count == 0 ? null : string.Join("\n", textParts);
        return new LmChatResult(content, calls, inputTokens, outputTokens);
        }
        finally
        {
            EndModelOperation();
        }
    }

    public async Task<IReadOnlyList<IReadOnlyList<double>>> CreateEmbeddingsAsync(
        string modelId,
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default)
    {
        await BeginModelOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        if (inputs.Count is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(inputs));
        }

        var body = new { model = modelId, input = inputs, ttl = _options.ModelTtlSeconds };
        using var request = await CreateRequestAsync(HttpMethod.Post, "v1/embeddings", body, cancellationToken).ConfigureAwait(false);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("LM Studio embedding response contains no data array.");
        }

        return data.EnumerateArray()
            .OrderBy(static item => item.TryGetProperty("index", out var index) ? index.GetInt32() : 0)
            .Select(static item => (IReadOnlyList<double>)item.GetProperty("embedding")
                .EnumerateArray()
                .Select(static number => number.GetDouble())
                .ToArray())
            .ToArray();
        }
        finally
        {
            EndModelOperation();
        }
    }

    public async Task<string> AnalyzeImagesAsync(
        string modelId,
        string prompt,
        IReadOnlyList<string> imagePaths,
        CancellationToken cancellationToken = default)
    {
        await BeginModelOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        if (imagePaths.Count is < 1 or > 48)
        {
            throw new ArgumentOutOfRangeException(nameof(imagePaths));
        }

        var content = new List<object> { new { type = "text", text = prompt } };
        foreach (var path in imagePaths)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 25L * 1024 * 1024)
            {
                throw new InvalidDataException("Vision input is missing or exceeds 25 MiB.");
            }

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var mediaType = DetectImageMediaType(bytes);
            content.Add(new
            {
                type = "image_url",
                image_url = new { url = $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}" },
            });
        }

        var body = new
        {
            model = modelId,
            messages = new object[]
            {
                new { role = "system", content = "Analysiere ausschließlich die bereitgestellten Medien fachlich. Erfinde keine sichtbaren Details." },
                new { role = "user", content = content.ToArray() },
            },
            stream = false,
            temperature = 0.1,
            ttl = _options.ModelTtlSeconds,
        };
        using var request = await CreateRequestAsync(HttpMethod.Post, "v1/chat/completions", body, cancellationToken).ConfigureAwait(false);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        var choices = document.RootElement.GetProperty("choices");
        var message = choices[0].GetProperty("message");
        var result = message.TryGetProperty("content", out var value) ? value.GetString() : null;
        return string.IsNullOrWhiteSpace(result)
            ? throw new JsonException("Vision model returned no text response.")
            : result;
        }
        finally
        {
            EndModelOperation();
        }
    }

    internal static string DetectImageMediaType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8
            && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }))
        {
            return "image/png";
        }
        if (bytes.Length >= 3 && bytes[..3].SequenceEqual(new byte[] { 0xff, 0xd8, 0xff }))
        {
            return "image/jpeg";
        }
        if (bytes.Length >= 12
            && bytes[..4].SequenceEqual("RIFF"u8)
            && bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }
        throw new InvalidDataException("Vision input is not a supported PNG, JPEG, or WebP image.");
    }

    private async Task BeginModelOperationAsync(CancellationToken cancellationToken)
    {
        await _modelOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        CancelIdleUnload();
    }

    private void EndModelOperation()
    {
        // Model residency is coordinated explicitly by WorkerOrchestrator. The
        // permanent General/STT/TTS set must never disappear because of idleness.
        _modelOperationGate.Release();
    }

    private void CancelIdleUnload()
    {
        lock (_idleTimerSync)
        {
            _idleUnloadCancellation?.Cancel();
            _idleUnloadCancellation = null;
        }
    }

    private void ScheduleIdleUnload()
    {
        var cancellation = new CancellationTokenSource();
        lock (_idleTimerSync)
        {
            _idleUnloadCancellation?.Cancel();
            _idleUnloadCancellation = cancellation;
        }

        _ = UnloadAfterIdleAsync(cancellation);
    }

    private async Task UnloadAfterIdleAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(_options.ModelTtlSeconds), cancellation.Token).ConfigureAwait(false);
            await _modelOperationGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
            try
            {
                lock (_idleTimerSync)
                {
                    if (!ReferenceEquals(_idleUnloadCancellation, cancellation))
                    {
                        return;
                    }

                    _idleUnloadCancellation = null;
                }

                var status = await GetRawModelsAsync(cancellation.Token).ConfigureAwait(false);
                await UnloadModelInstancesAsync(status.Models, cancellation.Token).ConfigureAwait(false);
                LogIdleModelsUnloaded(_options.ModelTtlSeconds);
            }
            finally
            {
                _modelOperationGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            LogIdleUnloadFailed(exception);
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private static bool HasRequiredConfiguration(
        LmStudioLoadedInstance loaded,
        int requestedContextLength,
        bool isEmbedding)
    {
        if (loaded.Config is null)
        {
            return false;
        }

        return loaded.Config.ContextLength >= requestedContextLength
            && (isEmbedding
                || loaded.Config.Parallel is 1
                    && loaded.Config.FlashAttention is true
                    && loaded.Config.OffloadKvCacheToGpu is true);
    }

    private static void ValidateLoadResponse(
        LmStudioLoadResponse response,
        int expectedContextLength,
        bool isEmbedding)
    {
        if (!string.Equals(response.Status, "loaded", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("LM Studio did not report a loaded model.");
        }

        if (response.LoadConfig is null
            || response.LoadConfig.ContextLength != expectedContextLength
            || !isEmbedding
                && (response.LoadConfig.Parallel is not 1
                    || response.LoadConfig.FlashAttention is not true
                    || response.LoadConfig.OffloadKvCacheToGpu is not true))
        {
            throw new InvalidOperationException("LM Studio did not apply the required model load profile.");
        }
    }

    public void Dispose()
    {
        lock (_idleTimerSync)
        {
            _idleUnloadCancellation?.Cancel();
            _idleUnloadCancellation = null;
        }

        _modelOperationGate.Dispose();
        _statusGate.Dispose();
    }

    [LoggerMessage(
        EventId = 3101,
        Level = LogLevel.Information,
        Message = "LM Studio models were unloaded after {ttlSeconds} idle seconds.")]
    private partial void LogIdleModelsUnloaded(int ttlSeconds);

    [LoggerMessage(
        EventId = 3102,
        Level = LogLevel.Warning,
        Message = "LM Studio idle unload failed.")]
    private partial void LogIdleUnloadFailed(Exception exception);

    [LoggerMessage(
        EventId = 3103,
        Level = LogLevel.Warning,
        Message = "LM Studio Responses returned transient HTTP {statusCode}; retrying attempt {attempt} with the same model.")]
    private partial void LogTransientResponseRetry(int statusCode, int attempt);

    [LoggerMessage(
        EventId = 3104,
        Level = LogLevel.Warning,
        Message = "LM Studio model load returned transient HTTP {statusCode}; retrying attempt {attempt} for the same model {modelId}.")]
    private partial void LogTransientModelLoadRetry(int statusCode, int attempt, string modelId);

    [LoggerMessage(
        EventId = 3105,
        Level = LogLevel.Warning,
        Message = "LM Studio Responses returned incompatible HTTP {statusCode}; using Chat Completions with the same model {modelId}.")]
    private partial void LogResponsesEndpointFallback(int statusCode, string modelId);

    private async Task<string> LoadModelWithRetryAsync(
        string modelId,
        object body,
        int expectedContextLength,
        bool isEmbedding,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 2;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            using var request = await CreateRequestAsync(
                HttpMethod.Post,
                "api/v1/models/load",
                body,
                cancellationToken).ConfigureAwait(false);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var loadedResponse = await response.Content.ReadFromJsonAsync<LmStudioLoadResponse>(
                    _jsonOptions,
                    cancellationToken).ConfigureAwait(false)
                    ?? throw new JsonException("LM Studio returned no model load response.");
                ValidateLoadResponse(loadedResponse, expectedContextLength, isEmbedding);
                InvalidateStatusCache();
                return loadedResponse.InstanceId
                    ?? loadedResponse.ModelInstanceId
                    ?? throw new JsonException("LM Studio returned no model instance identifier.");
            }

            var statusCode = response.StatusCode;
            if (attempt >= maximumAttempts || !IsTransientProviderStatus(statusCode))
            {
                throw new HttpRequestException(
                    $"LM Studio model load returned HTTP {(int)statusCode}.",
                    inner: null,
                    statusCode);
            }

            LogTransientModelLoadRetry((int)statusCode, attempt + 1, modelId);
            await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken).ConfigureAwait(false);

            var refreshed = await GetRawModelsAsync(cancellationToken).ConfigureAwait(false);
            var refreshedModel = refreshed.Models.FirstOrDefault(model =>
                string.Equals(model.Key, modelId, StringComparison.OrdinalIgnoreCase));
            var refreshedInstance = refreshedModel?.LoadedInstances?.FirstOrDefault(instance =>
                HasRequiredConfiguration(instance, expectedContextLength, isEmbedding));
            if (refreshedInstance is not null)
            {
                InvalidateStatusCache();
                return refreshedInstance.ModelInstanceId ?? refreshedInstance.Id ?? modelId;
            }

            if (refreshedModel?.LoadedInstances is { Count: > 0 })
            {
                await UnloadModelInstancesAsync([refreshedModel], cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("LM Studio model load retry loop ended unexpectedly.");
    }

    private async Task<LmStudioModelList> GetRawModelsAsync(CancellationToken cancellationToken)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, "api/v1/models", null, cancellationToken).ConfigureAwait(false);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LmStudioModelList>(_jsonOptions, cancellationToken).ConfigureAwait(false)
            ?? new LmStudioModelList([]);
    }

    private async Task<bool> HasConfiguredTokenCoreAsync(CancellationToken cancellationToken)
    {
        var token = await _secretStore.ReadLmStudioTokenAsync(cancellationToken).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(token);
    }

    private static object[] CreateResponsesInput(IReadOnlyList<LmChatMessage> messages)
    {
        var input = new List<object>();
        foreach (var message in messages)
        {
            if (string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (message.ToolCalls is { Count: > 0 })
            {
                if (!string.IsNullOrWhiteSpace(message.Content))
                {
                    input.Add(new { role = "assistant", content = message.Content });
                }
                foreach (var call in message.ToolCalls)
                {
                    input.Add(new
                    {
                        type = "function_call",
                        call_id = call.Id,
                        name = call.Name,
                        arguments = call.Arguments.GetRawText(),
                    });
                }
                continue;
            }

            if (!string.IsNullOrWhiteSpace(message.ToolCallId))
            {
                input.Add(new
                {
                    type = "function_call_output",
                    call_id = message.ToolCallId,
                    output = message.Content ?? string.Empty,
                });
                continue;
            }

            input.Add(new
            {
                role = NormalizeRole(message.Role),
                content = message.Content ?? string.Empty,
            });
        }

        return input.ToArray();
    }

    private static int TryReadInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var result)
            ? result
            : 0;

    private async Task UnloadIncompatibleModelsAsync(
        IReadOnlyList<LmStudioModel> models,
        string targetModelId,
        CancellationToken cancellationToken)
    {
        var targetKeepsGeneralSet = IsGeneralCompatibleModel(targetModelId);
        var incompatible = models.Where(model =>
            !string.Equals(model.Key, targetModelId, StringComparison.OrdinalIgnoreCase)
            && (!targetKeepsGeneralSet || !IsGeneralCompatibleModel(model.Key)));
        await UnloadModelInstancesAsync(incompatible, cancellationToken).ConfigureAwait(false);
    }

    private bool IsGeneralCompatibleModel(string modelId) =>
        string.Equals(modelId, _options.GeneralModelId, StringComparison.OrdinalIgnoreCase)
        || string.Equals(modelId, _options.VisionModelId, StringComparison.OrdinalIgnoreCase)
        || string.Equals(modelId, _options.EmbeddingModelId, StringComparison.OrdinalIgnoreCase);

    private async Task UnloadModelInstancesAsync(
        IEnumerable<LmStudioModel> models,
        CancellationToken cancellationToken)
    {
        foreach (var model in models)
        {
            foreach (var loaded in model.LoadedInstances ?? [])
            {
                var instanceId = loaded.ModelInstanceId ?? loaded.Id;
                if (string.IsNullOrWhiteSpace(instanceId))
                {
                    continue;
                }

                using var request = await CreateRequestAsync(
                    HttpMethod.Post,
                    "api/v1/models/unload",
                    new { instance_id = instanceId },
                    cancellationToken).ConfigureAwait(false);
                using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                InvalidateStatusCache();
            }
        }
    }

    private ModelStatusSnapshot? GetCachedStatus()
    {
        lock (_statusCacheSync)
        {
            return _cachedStatus is not null && DateTimeOffset.UtcNow < _statusCacheExpiresAt
                ? _cachedStatus
                : null;
        }
    }

    private ModelStatusSnapshot CacheStatus(ModelStatusSnapshot status)
    {
        lock (_statusCacheSync)
        {
            _cachedStatus = status;
            _statusCacheExpiresAt = DateTimeOffset.UtcNow.Add(StatusCacheDuration);
            return status;
        }
    }

    private void InvalidateStatusCache()
    {
        lock (_statusCacheSync)
        {
            _cachedStatus = null;
            _statusCacheExpiresAt = default;
        }
    }

    private ModelRuntimeStatus[] CreateConfiguredModelStatus(IReadOnlyList<LmStudioModel> rawModels)
    {
        var definitions = new[]
        {
            (_options.GeneralModelId, "general", _options.GeneralContextLength),
            (_options.CodeModelId, "code", _options.CodeContextLength),
            (_options.VisionModelId, "vision", 65536),
            (_options.EmbeddingModelId, "embedding", 8192),
        };
        var configured = definitions.Select(definition =>
        {
            var raw = rawModels.FirstOrDefault(model => string.Equals(model.Key, definition.Item1, StringComparison.OrdinalIgnoreCase));
            var loaded = raw?.LoadedInstances is { Count: > 0 };
            return new ModelRuntimeStatus(
                definition.Item1,
                definition.Item2,
                raw is not null,
                loaded,
                raw is null ? "Fehlt" : loaded ? "Geladen" : "Bereit zum Laden",
                raw?.MaximumContextLength ?? definition.Item3);
        }).ToList();
        foreach (var raw in rawModels.Where(raw => configured.All(item =>
                     !string.Equals(item.Id, raw.Key, StringComparison.OrdinalIgnoreCase))))
        {
            var loaded = raw.LoadedInstances is { Count: > 0 };
            configured.Add(new ModelRuntimeStatus(
                raw.Key,
                string.Equals(raw.Type, "embedding", StringComparison.OrdinalIgnoreCase) ? "embedding" : "general",
                true,
                loaded,
                loaded ? "Geladen" : "Bereit zum Laden",
                raw.MaximumContextLength));
        }
        return configured.ToArray();
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: _jsonOptions);
        }

        var token = await _secretStore.ReadLmStudioTokenAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    private async Task<HttpResponseMessage> SendResponsesWithRetryAsync(
        string modelId,
        object body,
        CancellationToken cancellationToken)
    {
        // LM Studio 0.4.x can sporadically reject otherwise valid gpt-oss
        // Harmony output with HTTP 500 (peg-native parser). Retrying the exact
        // same request is safe because no tool result has been executed yet.
        var maximumAttempts = modelId.Contains("gpt-oss", StringComparison.OrdinalIgnoreCase) ? 4 : 2;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            using var request = await CreateRequestAsync(
                HttpMethod.Post,
                "v1/responses",
                body,
                cancellationToken).ConfigureAwait(false);
            var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            var statusCode = response.StatusCode;
            var errorPayload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            response.Dispose();
            if (IsResponsesCompatibilityFailure(errorPayload))
            {
                throw new ResponsesCompatibilityException(statusCode);
            }
            if (attempt < maximumAttempts && IsTransientProviderStatus(statusCode))
            {
                LogTransientResponseRetry((int)statusCode, attempt + 1);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }

            throw new HttpRequestException(
                $"LM Studio Responses returned HTTP {(int)statusCode}.",
                inner: null,
                statusCode);
        }

        throw new InvalidOperationException("LM Studio Responses retry loop ended unexpectedly.");
    }

    private static bool IsTransientProviderStatus(System.Net.HttpStatusCode statusCode) => statusCode is
        System.Net.HttpStatusCode.InternalServerError or
        System.Net.HttpStatusCode.BadGateway or
        System.Net.HttpStatusCode.ServiceUnavailable or
        System.Net.HttpStatusCode.GatewayTimeout;

    private static bool IsResponsesCompatibilityFailure(string payload) =>
        payload.Contains("peg-native", StringComparison.OrdinalIgnoreCase)
        || payload.Contains("expected peg native", StringComparison.OrdinalIgnoreCase);

    private async Task<LmChatResult> CompleteChatViaChatCompletionsAsync(
        string modelId,
        IReadOnlyList<LmChatMessage> messages,
        IReadOnlyList<LmToolDefinition> tools,
        int maximumOutputTokens,
        CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = modelId,
            ["messages"] = CreateChatCompletionMessages(messages),
            ["stream"] = false,
            ["temperature"] = 0.2,
            ["max_tokens"] = Math.Clamp(maximumOutputTokens, 1, 65_536),
            ["ttl"] = _options.ModelTtlSeconds,
        };
        if (tools.Count > 0)
        {
            body["tools"] = tools.Select(static tool => new
            {
                type = "function",
                function = new
                {
                    name = tool.Name,
                    description = tool.Description,
                    parameters = tool.Parameters,
                },
            }).ToArray();
            body["tool_choice"] = "auto";
            body["parallel_tool_calls"] = false;
        }

        using var request = await CreateRequestAsync(
            HttpMethod.Post,
            "v1/chat/completions",
            body,
            cancellationToken).ConfigureAwait(false);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        var root = document.RootElement;
        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0
            || !choices[0].TryGetProperty("message", out var message))
        {
            throw new JsonException("LM Studio Chat Completions result contains no assistant message.");
        }

        var content = message.TryGetProperty("content", out var contentElement)
            && contentElement.ValueKind == JsonValueKind.String
            ? contentElement.GetString()
            : null;
        var calls = new List<LmToolCall>();
        if (message.TryGetProperty("tool_calls", out var toolCalls)
            && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in toolCalls.EnumerateArray())
            {
                var id = call.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                if (!call.TryGetProperty("function", out var function)
                    || function.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException("LM Studio returned an incomplete Chat Completions tool call.");
                }
                var name = function.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                var argumentsText = function.TryGetProperty("arguments", out var argumentsElement)
                    ? argumentsElement.ValueKind == JsonValueKind.String
                        ? argumentsElement.GetString()
                        : argumentsElement.GetRawText()
                    : null;
                if (string.IsNullOrWhiteSpace(id)
                    || string.IsNullOrWhiteSpace(name)
                    || string.IsNullOrWhiteSpace(argumentsText))
                {
                    throw new JsonException("LM Studio returned an incomplete Chat Completions tool call.");
                }

                using var argumentsDocument = JsonDocument.Parse(argumentsText);
                calls.Add(new LmToolCall(id, name, argumentsDocument.RootElement.Clone()));
            }
        }

        var inputTokens = 0;
        var outputTokens = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            inputTokens = TryReadInt32(usage, "prompt_tokens");
            outputTokens = TryReadInt32(usage, "completion_tokens");
        }
        return new LmChatResult(content, calls, inputTokens, outputTokens);
    }

    private static object[] CreateChatCompletionMessages(IReadOnlyList<LmChatMessage> messages)
    {
        var result = new List<object>(messages.Count);
        foreach (var message in messages)
        {
            if (message.ToolCalls is { Count: > 0 })
            {
                result.Add(new
                {
                    role = "assistant",
                    content = message.Content,
                    tool_calls = message.ToolCalls.Select(static call => new
                    {
                        id = call.Id,
                        type = "function",
                        function = new
                        {
                            name = call.Name,
                            arguments = call.Arguments.GetRawText(),
                        },
                    }).ToArray(),
                });
                continue;
            }

            if (!string.IsNullOrWhiteSpace(message.ToolCallId))
            {
                result.Add(new
                {
                    role = "tool",
                    tool_call_id = message.ToolCallId,
                    content = message.Content ?? string.Empty,
                });
                continue;
            }

            result.Add(new
            {
                role = NormalizeRole(message.Role),
                content = message.Content ?? string.Empty,
            });
        }
        return result.ToArray();
    }

    private sealed class ResponsesCompatibilityException(System.Net.HttpStatusCode statusCode) : Exception
    {
        public System.Net.HttpStatusCode StatusCode { get; } = statusCode;
    }

    private static (int InputTokens, int OutputTokens) TryReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("response", out var response)
            || !response.TryGetProperty("usage", out var usage))
        {
            return (0, 0);
        }

        var input = usage.TryGetProperty("input_tokens", out var inputElement) && inputElement.TryGetInt32(out var inputValue)
            ? inputValue
            : 0;
        var output = usage.TryGetProperty("output_tokens", out var outputElement) && outputElement.TryGetInt32(out var outputValue)
            ? outputValue
            : 0;
        return (input, output);
    }

    private static string NormalizeRole(string role) => role.ToLowerInvariant() switch
    {
        "assistant" => "assistant",
        "system" => "system",
        _ => "user",
    };

    private static Uri EnsureTrailingSlash(Uri uri) => uri.AbsoluteUri.EndsWith('/')
        ? uri
        : new Uri(uri.AbsoluteUri + "/", UriKind.Absolute);
}

public sealed record LmStudioResponseEvent(
    string Type,
    string? Delta,
    int? InputTokens,
    int? OutputTokens);
