using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GoAi.Server.Core.Models;

/// <summary>
/// Connects the Docker gateway to LM Studio's local REST and OpenAI-compatible
/// APIs. GO remains the only tool executor; LM Studio owns model residency and
/// produces text, embeddings, vision responses and native tool calls.
/// </summary>
public sealed class ModelRuntimeClient : IDisposable
{
    private static readonly Action<ILogger, Exception?> LogRouterUnavailable = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(4101, "ModelRouterUnavailable"),
        "The LM Studio model server is unavailable.");
    private static readonly Action<ILogger, Exception?> LogInferenceRetry = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(4102, "ModelInferenceRetry"),
        "Transient LM Studio inference failure; retrying before any tool is executed.");
    private static readonly Action<ILogger, int, string, int, string, int, bool, Exception?> LogStreamingAttempt =
        LoggerMessage.Define<int, string, int, string, int, bool>(
            LogLevel.Warning,
            new EventId(4103, "ModelStreamingAttemptIncomplete"),
            "LM Studio inference attempt {Attempt}/3 ended ({FailureKind}); fragments={GeneratedFragments}, tool={ToolName}, argumentCharacters={ArgumentCharacters}, argumentJsonComplete={ArgumentJsonComplete}.");
    private static readonly TimeSpan StatusCacheDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ModelLoadTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ModelTurnTimeout = TimeSpan.FromMinutes(20);
    private readonly HttpClient _httpClient;
    private readonly GoAiServerOptions _options;
    private readonly ILogger<ModelRuntimeClient> _logger;
    private readonly SemaphoreSlim _modelGate = new(1, 1);
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, RuntimeModel> _runtimeCatalog = new(StringComparer.OrdinalIgnoreCase);
    private ModelStatusSnapshot? _cachedStatus;
    private IReadOnlyList<ModelRuntimeStatus> _lastReachableModels = [];
    private DateTimeOffset _cacheExpiresAt;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public ModelRuntimeClient(
        HttpClient httpClient,
        IOptions<GoAiServerOptions> options,
        ILogger<ModelRuntimeClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _httpClient.BaseAddress = EnsureTrailingSlash(_options.ModelRuntimeUri);
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<ModelStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        lock (_cacheLock)
        {
            if (_cachedStatus is not null && DateTimeOffset.UtcNow < _cacheExpiresAt)
            {
                return _cachedStatus;
            }
        }

        try
        {
            using var response = await _httpClient.GetAsync("api/v1/models", cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
            var runtimeModels = ReadRuntimeModels(document.RootElement);
            RememberRuntimeModels(runtimeModels);
            var models = BuildRuntimeStatuses(runtimeModels);
            return Cache(new ModelStatusSnapshot(
                true,
                _options.ModelRuntimeUri.ToString(),
                models,
                DateTimeOffset.UtcNow));
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            LogRouterUnavailable(_logger, exception);
            return Cache(new ModelStatusSnapshot(
                false,
                _options.ModelRuntimeUri.ToString(),
                GetLastReachableModels(),
                DateTimeOffset.UtcNow,
                exception is TaskCanceledException ? "modelRuntime.timeout" : "modelRuntime.unreachable"));
        }
    }

    public async Task<string> EnsureModelLoadedAsync(
        string modelId,
        int contextLength,
        CancellationToken cancellationToken = default) =>
        (await EnsureModelPreparedAsync(modelId, contextLength, null, cancellationToken).ConfigureAwait(false)).InstanceId;

    internal async Task<ModelPreparation> EnsureModelPreparedAsync(
        string modelId,
        int contextLength,
        Func<CancellationToken, Task>? loadingStarted,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        await _modelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ModelLoadTimeout);
            var operationToken = timeout.Token;
            var runtimeModels = await GetRuntimeModelsAsync(operationToken).ConfigureAwait(false);
            var selected = runtimeModels.FirstOrDefault(candidate =>
                MatchesRuntimeModel(candidate, ResolveKnownRuntimeModelId(modelId)));
            if (selected is null)
            {
                throw new FileNotFoundException(
                    $"Das LM-Studio-Modell '{modelId}' ist nicht installiert.");
            }
            RememberRuntimeModels(runtimeModels);
            var definition = CreateDefinition(selected);
            var availableContextLength = selected.MaximumContextLength > 0
                ? selected.MaximumContextLength
                : Math.Max(contextLength, 2_048);
            if (contextLength > availableContextLength)
            {
                throw new ModelContextLengthException(modelId, contextLength, availableContextLength);
            }
            var canReuseLoadedInstance = string.Equals(selected.State, "loaded", StringComparison.OrdinalIgnoreCase)
                && selected.LoadedContextLength >= contextLength
                && !string.IsNullOrWhiteSpace(selected.InstanceId);
            if (canReuseLoadedInstance)
            {
                return new ModelPreparation(selected.InstanceId!, WasAlreadyLoaded: true);
            }

            if (loadingStarted is not null)
            {
                await loadingStarted(cancellationToken).ConfigureAwait(false);
            }
            foreach (var loaded in runtimeModels.Where(candidate =>
                         string.Equals(candidate.State, "loaded", StringComparison.OrdinalIgnoreCase)
                         && !MatchesRuntimeModel(candidate, definition.RuntimeModelId)
                         && !string.IsNullOrWhiteSpace(candidate.InstanceId)))
            {
                await UnloadRuntimeInstanceAsync(loaded.InstanceId!, operationToken).ConfigureAwait(false);
            }
            if (string.Equals(selected.State, "loaded", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(selected.InstanceId))
            {
                await UnloadRuntimeInstanceAsync(selected.InstanceId!, operationToken).ConfigureAwait(false);
            }
            string instanceId;
            try
            {
                instanceId = await LoadRuntimeModelAsync(
                    definition,
                    contextLength,
                    operationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                IsTransientInferenceFailure(exception)
                && !operationToken.IsCancellationRequested)
            {
                // LM Studio can close the HTTP load channel while the native
                // runtime finishes loading successfully. Reconcile against the
                // authoritative model catalog before failing the run or issuing
                // a second load request.
                var recoveredInstanceId = await WaitForLoadedInstanceAsync(
                    definition.RuntimeModelId,
                    contextLength,
                    operationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(recoveredInstanceId))
                {
                    throw;
                }
                instanceId = recoveredInstanceId;
            }
            InvalidateStatus();
            return new ModelPreparation(instanceId, WasAlreadyLoaded: false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Das Modell '{modelId}' wurde nicht innerhalb von 30 Minuten geladen.");
        }
        finally
        {
            _modelGate.Release();
        }
    }

    public async Task<bool> UnloadModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        await _modelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var runtimeModels = await GetRuntimeModelsAsync(cancellationToken).ConfigureAwait(false);
            var loaded = runtimeModels.FirstOrDefault(candidate =>
                MatchesRuntimeModel(candidate, ResolveKnownRuntimeModelId(modelId))
                && string.Equals(candidate.State, "loaded", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(candidate.InstanceId));
            if (loaded is null)
            {
                return false;
            }
            await UnloadRuntimeInstanceAsync(loaded.InstanceId!, cancellationToken).ConfigureAwait(false);
            InvalidateStatus();
            return true;
        }
        finally
        {
            _modelGate.Release();
        }
    }

    public Task UnloadAllModelsAsync(CancellationToken cancellationToken = default) =>
        UnloadModelsExceptAsync([], cancellationToken);

    public async Task UnloadModelsExceptAsync(
        IReadOnlyCollection<string> preservedModelIds,
        CancellationToken cancellationToken = default)
    {
        var preserved = new HashSet<string>(preservedModelIds, StringComparer.OrdinalIgnoreCase);
        await _modelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var runtimeModels = await GetRuntimeModelsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var model in runtimeModels.Where(candidate =>
                         string.Equals(candidate.State, "loaded", StringComparison.OrdinalIgnoreCase)
                         && !preserved.Any(preservedId =>
                             MatchesRuntimeModel(candidate, ResolveKnownRuntimeModelId(preservedId)))
                         && !string.IsNullOrWhiteSpace(candidate.InstanceId)))
            {
                await UnloadRuntimeInstanceAsync(model.InstanceId!, cancellationToken).ConfigureAwait(false);
            }
            InvalidateStatus();
        }
        finally
        {
            _modelGate.Release();
        }
    }

    public async Task<LmChatResult> CompleteChatAsync(
        string modelId,
        IReadOnlyList<LmChatMessage> messages,
        IReadOnlyList<LmToolDefinition> tools,
        int maximumOutputTokens = 8_192,
        string modelRole = "general",
        string? reasoningEffort = null,
        bool requireToolCall = false,
        string? requiredToolName = null,
        int? requiredContextLength = null,
        Func<ModelRuntimeProgress, CancellationToken, ValueTask>? nativeProgress = null,
        bool structuredToolOnly = false,
        CancellationToken cancellationToken = default)
    {
        ValidateToolChoice(tools, requireToolCall, requiredToolName);
        var modelContext = TryGetRuntimeModel(modelId, out var catalogModel)
            && catalogModel.MaximumContextLength > 0
                ? catalogModel.MaximumContextLength
                : string.Equals(modelRole, "code", StringComparison.OrdinalIgnoreCase)
                    ? ModelContextProfiles.ResolveMaximum(modelId, modelRole)
                    : _options.GeneralContextLength;
        var context = requiredContextLength is { } requested
            ? Math.Clamp(requested, 1, modelContext)
            : modelContext;
        var preparation = await EnsureModelPreparedAsync(
            modelId,
            context,
            loadingStarted: null,
            cancellationToken).ConfigureAwait(false);
        await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (nativeProgress is not null)
            {
                await nativeProgress(new ModelRuntimeProgress("generationStarted"), cancellationToken).ConfigureAwait(false);
            }

            var body = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["model"] = preparation.InstanceId,
                ["messages"] = NormalizeMessageOrderForLmStudio(messages)
                    .Select(ToOpenAiMessage)
                    .ToArray(),
                // Streaming is an internal transport detail. GO buffers and
                // validates the complete assistant turn before exposing text or
                // executing a tool, while incremental bytes keep long LM Studio
                // predictions from looking like an idle/dead HTTP channel.
                ["stream"] = true,
                ["stream_options"] = new { include_usage = true },
                ["max_tokens"] = Math.Clamp(maximumOutputTokens, 1, 65_536),
                ["parallel_tool_calls"] = false,
            };
            ApplyModelSampling(body, modelId);
            ApplyReasoningSettings(body, modelId, modelRole, reasoningEffort);
            if (tools.Count > 0)
            {
                body["tools"] = tools.Select(static tool => new
                {
                    type = "function",
                    function = new
                    {
                        name = ToTransportToolName(tool.Name),
                        description = tool.Description,
                        parameters = tool.Parameters,
                    },
                }).ToArray();
                // LM Studio's OpenAI-compatible endpoint accepts auto/required/none.
                // The host uses "required" whenever the current agent protocol
                // mandates one structured action. Schema validation still decides
                // which of the supplied tools and arguments are acceptable.
                body["tool_choice"] = requireToolCall || requiredToolName is { Length: > 0 }
                    ? "required"
                    : "auto";
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ModelTurnTimeout);
            var transportToolNames = tools.ToDictionary(
                static tool => ToTransportToolName(tool.Name),
                static tool => tool.Name,
                StringComparer.Ordinal);
            var result = await CompleteStreamingChatWithBoundedRetryAsync(
                body,
                transportToolNames,
                nativeProgress,
                structuredToolOnly,
                timeout.Token).ConfigureAwait(false);
            if (nativeProgress is not null)
            {
                await nativeProgress(
                    new ModelRuntimeProgress(
                        "tokenProgress",
                        PromptProgress: 1,
                        PromptTokens: result.InputTokens,
                        ProcessedPromptTokens: result.InputTokens,
                        GeneratedTokens: result.OutputTokens,
                        CurrentTokens: result.InputTokens + result.OutputTokens),
                    cancellationToken).ConfigureAwait(false);
                if (result.ToolCalls.Count > 0)
                {
                    var call = result.ToolCalls[0];
                    await nativeProgress(
                        new ModelRuntimeProgress("toolSelected", call.Name, call.Arguments.GetRawText().Length),
                        cancellationToken).ConfigureAwait(false);
                }
            }
            return result;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ModelGenerationTerminatedException("model_turn_timeout", exception);
        }
        finally
        {
            _turnGate.Release();
        }
    }

    public async Task<IReadOnlyList<IReadOnlyList<double>>> CreateEmbeddingsAsync(
        string modelId,
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default)
    {
        if (inputs.Count is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(inputs));
        }
        var preparation = await EnsureModelPreparedAsync(
            modelId,
            _options.EmbeddingContextLength,
            loadingStarted: null,
            cancellationToken).ConfigureAwait(false);
        using var response = await SendJsonAsync(
            HttpMethod.Post,
            "v1/embeddings",
            new { model = preparation.InstanceId, input = inputs },
            cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        var data = document.RootElement.GetProperty("data");
        return data.EnumerateArray()
            .OrderBy(static item => item.GetProperty("index").GetInt32())
            .Select(static item => (IReadOnlyList<double>)item.GetProperty("embedding")
                .EnumerateArray().Select(static number => number.GetDouble()).ToArray())
            .ToArray();
    }

    public async Task<string> AnalyzeImagesAsync(
        string modelId,
        string prompt,
        IReadOnlyList<string> imagePaths,
        CancellationToken cancellationToken = default)
    {
        if (imagePaths.Count is < 1 or > 48)
        {
            throw new ArgumentOutOfRangeException(nameof(imagePaths));
        }
        var preparation = await EnsureModelPreparedAsync(
            modelId,
            _options.VisionContextLength,
            loadingStarted: null,
            cancellationToken).ConfigureAwait(false);
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
        using var response = await SendJsonAsync(
            HttpMethod.Post,
            "v1/chat/completions",
            new
            {
                model = preparation.InstanceId,
                stream = false,
                messages = new object[]
                {
                    new { role = "system", content = "Analysiere ausschließlich die bereitgestellten Medien fachlich. Erfinde keine sichtbaren Details." },
                    new { role = "user", content = content.ToArray() },
                },
            },
            cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        var text = ReadContent(document.RootElement.GetProperty("choices")[0].GetProperty("message"));
        return string.IsNullOrWhiteSpace(text)
            ? throw new JsonException("Das Vision-Modell lieferte keine Textantwort.")
            : text;
    }

    internal static string DetectImageMediaType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }))
            return "image/png";
        if (bytes.Length >= 3 && bytes[..3].SequenceEqual(new byte[] { 0xff, 0xd8, 0xff }))
            return "image/jpeg";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
            return "image/webp";
        throw new InvalidDataException("Vision input is not a supported PNG, JPEG, or WebP image.");
    }

    private async Task<RuntimeModel[]> GetRuntimeModelsAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync("api/v1/models", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        var models = ReadRuntimeModels(document.RootElement);
        RememberRuntimeModels(models);
        return models;
    }

    private async Task<string> LoadRuntimeModelAsync(
        ModelDefinition definition,
        int contextLength,
        CancellationToken cancellationToken)
    {
        using var response = await SendJsonAsync(
            HttpMethod.Post,
            "api/v1/models/load",
            string.Equals(definition.Role, "embedding", StringComparison.OrdinalIgnoreCase)
                ? new
                {
                    model = definition.RuntimeModelId,
                    context_length = contextLength,
                    echo_load_config = true,
                }
                : (object)new
                {
                    model = definition.RuntimeModelId,
                    context_length = contextLength,
                    parallel = 1,
                    flash_attention = true,
                    offload_kv_cache_to_gpu = true,
                    echo_load_config = true,
                },
            cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        var instanceId = document.RootElement.TryGetProperty("instance_id", out var instance)
            ? instance.GetString()
            : document.RootElement.TryGetProperty("model_instance_id", out var modelInstance)
                ? modelInstance.GetString()
                : null;
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            throw new JsonException($"LM Studio lieferte für '{definition.RuntimeModelId}' keine Instanz-ID.");
        }
        return instanceId;
    }

    private async Task<string?> WaitForLoadedInstanceAsync(
        string runtimeModelId,
        int contextLength,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 15; attempt++)
        {
            try
            {
                var models = await GetRuntimeModelsAsync(cancellationToken).ConfigureAwait(false);
                var loaded = models.FirstOrDefault(candidate =>
                    MatchesRuntimeModel(candidate, runtimeModelId)
                    && string.Equals(candidate.State, "loaded", StringComparison.OrdinalIgnoreCase)
                    && candidate.LoadedContextLength >= contextLength
                    && !string.IsNullOrWhiteSpace(candidate.InstanceId));
                if (loaded?.InstanceId is { Length: > 0 } instanceId)
                {
                    return instanceId;
                }
            }
            catch (Exception exception) when (
                IsTransientInferenceFailure(exception)
                && !cancellationToken.IsCancellationRequested)
            {
                // The catalog endpoint may briefly restart together with the
                // inference engine. The bounded reconciliation loop continues.
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private async Task UnloadRuntimeInstanceAsync(string instanceId, CancellationToken cancellationToken)
    {
        using var response = await SendJsonAsync(
            HttpMethod.Post,
            "api/v1/models/unload",
            new { instance_id = instanceId },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<LmChatResult> CompleteStreamingChatWithBoundedRetryAsync(
        object body,
        IReadOnlyDictionary<string, string> transportToolNames,
        Func<ModelRuntimeProgress, CancellationToken, ValueTask>? nativeProgress,
        bool structuredToolOnly,
        CancellationToken cancellationToken)
    {
        Exception? last = null;
        const int maximumAttempts = 3;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            try
            {
                using var response = await SendJsonAsync(
                    HttpMethod.Post,
                    "v1/chat/completions",
                    body,
                    cancellationToken).ConfigureAwait(false);
                return await ParseStreamingChatResponseAsync(
                    response,
                    transportToolNames,
                    nativeProgress,
                    structuredToolOnly,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                IsTransientInferenceFailure(exception)
                && !cancellationToken.IsCancellationRequested)
            {
                last = exception;
                var incomplete = exception as IncompleteStreamingChatException;
                var snapshot = incomplete?.Snapshot ?? StreamingAttemptSnapshot.Empty;
                var failureKind = incomplete?.FailureKind ?? "transport";
                LogStreamingAttempt(
                    _logger,
                    attempt,
                    failureKind,
                    snapshot.GeneratedFragments,
                    snapshot.ToolName ?? "<none>",
                    snapshot.ArgumentCharacters,
                    snapshot.ToolArgumentsJsonComplete,
                    exception);
                if (nativeProgress is not null)
                {
                    await nativeProgress(
                        new ModelRuntimeProgress(
                            "generationRetry",
                            snapshot.ToolName,
                            snapshot.ArgumentCharacters,
                            PromptTokens: snapshot.InputTokens > 0 ? snapshot.InputTokens : null,
                            GeneratedTokens: snapshot.OutputTokens > 0
                                ? snapshot.OutputTokens
                                : snapshot.GeneratedFragments,
                            CurrentTokens: snapshot.InputTokens > 0
                                ? snapshot.InputTokens + Math.Max(snapshot.OutputTokens, snapshot.GeneratedFragments)
                                : snapshot.GeneratedFragments,
                            Attempt: attempt,
                            FailureKind: failureKind,
                            ToolArgumentsJsonComplete: snapshot.ToolArgumentsJsonComplete,
                            ContentCharacters: snapshot.ContentCharacters,
                            FinishObserved: snapshot.FinishObserved),
                        cancellationToken).ConfigureAwait(false);
                }
                if (attempt >= maximumAttempts)
                {
                    throw new ModelGenerationTerminatedException("transport_retry_exhausted", exception);
                }
                LogInferenceRetry(_logger, exception);
                await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
        throw new ModelGenerationTerminatedException("transport_retry_exhausted", last);
    }

    private static async Task<LmChatResult> ParseStreamingChatResponseAsync(
        HttpResponseMessage response,
        IReadOnlyDictionary<string, string> transportToolNames,
        Func<ModelRuntimeProgress, CancellationToken, ValueTask>? nativeProgress,
        bool structuredToolOnly,
        CancellationToken cancellationToken)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
            return ParseChatResult(document.RootElement, transportToolNames, structuredToolOnly);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: false);
        var accumulator = new StreamingChatAccumulator(structuredToolOnly);
        var eventData = new StringBuilder();
        var progressClock = Stopwatch.StartNew();
        var lastReportedFragments = 0;

        try
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                {
                    await ProcessEventAsync().ConfigureAwait(false);
                    continue;
                }
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }
                if (eventData.Length > 0)
                {
                    eventData.Append('\n');
                }
                eventData.Append(line.AsSpan(5).TrimStart());
            }
            await ProcessEventAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException
            && exception is not OutOfMemoryException
            && exception is not IncompleteStreamingChatException)
        {
            throw new IncompleteStreamingChatException(
                exception is JsonException ? "invalid_sse_json" : "stream_read_error",
                accumulator.CreateSnapshot(transportToolNames),
                exception);
        }

        if (!accumulator.Done && !accumulator.FinishObserved)
        {
            // LM Studio can close its engine channel after it has already sent a
            // complete single tool call but before the final finish_reason or
            // [DONE] frame. The host still validates the tool name, JSON schema,
            // workspace revision and idempotency key before execution, so this
            // complete payload is safe to keep. Never salvage partial JSON or a
            // mixed text/tool response.
            if (accumulator.TryBuildCompleteToolCall(transportToolNames, out var completedToolCall))
            {
                return completedToolCall;
            }
            throw new IncompleteStreamingChatException(
                "premature_eof",
                accumulator.CreateSnapshot(transportToolNames));
        }
        return accumulator.Build(transportToolNames);

        async Task ProcessEventAsync()
        {
            if (eventData.Length == 0)
            {
                return;
            }
            var payload = eventData.ToString();
            eventData.Clear();
            if (payload == "[DONE]")
            {
                accumulator.Done = true;
                return;
            }

            using var chunk = JsonDocument.Parse(payload);
            var contentDelta = accumulator.Add(chunk.RootElement);
            if (!structuredToolOnly && nativeProgress is not null && !string.IsNullOrEmpty(contentDelta))
            {
                await nativeProgress(
                    new ModelRuntimeProgress(
                        "contentDelta",
                        ContentCharacters: contentDelta.Length,
                        ContentDelta: contentDelta),
                    cancellationToken).ConfigureAwait(false);
            }
            if (nativeProgress is null
                || accumulator.GeneratedFragments == lastReportedFragments
                || (accumulator.GeneratedFragments - lastReportedFragments < 8
                    && progressClock.Elapsed < TimeSpan.FromMilliseconds(250)))
            {
                return;
            }

            lastReportedFragments = accumulator.GeneratedFragments;
            progressClock.Restart();
            await nativeProgress(
                new ModelRuntimeProgress(
                    "tokenProgress",
                    PromptProgress: accumulator.InputTokens > 0 ? 1 : null,
                    PromptTokens: accumulator.InputTokens > 0 ? accumulator.InputTokens : null,
                    ProcessedPromptTokens: accumulator.InputTokens > 0 ? accumulator.InputTokens : null,
                    GeneratedTokens: accumulator.OutputTokens > 0
                        ? accumulator.OutputTokens
                        : accumulator.GeneratedFragments,
                    CurrentTokens: accumulator.InputTokens > 0
                        ? accumulator.InputTokens + Math.Max(accumulator.OutputTokens, accumulator.GeneratedFragments)
                        : accumulator.GeneratedFragments),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class StreamingChatAccumulator(bool structuredToolOnly)
    {
        private readonly StringBuilder _content = new();
        private readonly StringBuilder _reasoningContent = new();
        private readonly Dictionary<int, StreamingToolCall> _toolCalls = [];

        public int InputTokens { get; private set; }
        public int OutputTokens { get; private set; }
        public int ReasoningTokens { get; private set; }
        public int GeneratedFragments { get; private set; }
        public bool HadReasoning { get; private set; }
        public bool FinishObserved { get; private set; }
        public bool Done { get; set; }

        public StreamingAttemptSnapshot CreateSnapshot(IReadOnlyDictionary<string, string> transportToolNames)
        {
            var first = _toolCalls.OrderBy(static item => item.Key).Select(static item => item.Value).FirstOrDefault();
            var transportName = first?.Name.ToString();
            var logicalName = string.IsNullOrWhiteSpace(transportName)
                ? null
                : transportToolNames.GetValueOrDefault(transportName, transportName);
            var arguments = first?.Arguments.ToString() ?? string.Empty;
            return new StreamingAttemptSnapshot(
                GeneratedFragments,
                InputTokens,
                OutputTokens,
                logicalName,
                arguments.Length,
                IsCompleteJsonObject(arguments),
                _content.Length,
                FinishObserved,
                Done,
                _toolCalls.Count);
        }

        public string? Add(JsonElement root)
        {
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                InputTokens = ReadInt(usage, "prompt_tokens", "input_tokens");
                OutputTokens = ReadInt(usage, "completion_tokens", "output_tokens");
                if (usage.TryGetProperty("completion_tokens_details", out var details))
                {
                    ReasoningTokens = ReadInt(details, "reasoning_tokens");
                    HadReasoning |= ReasoningTokens > 0;
                }
            }
            if (!root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                return null;
            }

            var choice = choices[0];
            if (choice.TryGetProperty("finish_reason", out var finish)
                && finish.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(finish.GetString()))
            {
                FinishObserved = true;
            }
            if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            var contentDelta = AppendString(delta, "content", _content);
            if (delta.TryGetProperty("reasoning_content", out var reasoning)
                && reasoning.ValueKind == JsonValueKind.String
                && reasoning.GetString() is { Length: > 0 } reasoningText)
            {
                _reasoningContent.Append(reasoningText);
                HadReasoning = true;
                GeneratedFragments++;
            }
            if (!delta.TryGetProperty("tool_calls", out var calls) || calls.ValueKind != JsonValueKind.Array)
            {
                return contentDelta;
            }
            foreach (var call in calls.EnumerateArray())
            {
                var index = call.TryGetProperty("index", out var indexValue) && indexValue.TryGetInt32(out var parsedIndex)
                    ? parsedIndex
                    : 0;
                if (!_toolCalls.TryGetValue(index, out var target))
                {
                    target = new StreamingToolCall();
                    _toolCalls[index] = target;
                }
                if (call.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                {
                    target.Id ??= id.GetString();
                }
                if (!call.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                AppendString(function, "name", target.Name);
                AppendString(function, "arguments", target.Arguments);
            }
            return contentDelta;
        }

        public LmChatResult Build(IReadOnlyDictionary<string, string> transportToolNames)
        {
            if (_toolCalls.Count > 1)
            {
                throw new JsonException("Der Modellturn darf genau einen Toolaufruf liefern.");
            }
            var calls = new List<LmToolCall>(_toolCalls.Count);
            foreach (var item in _toolCalls.OrderBy(static item => item.Key).Select(static item => item.Value))
            {
                var name = item.Name.ToString();
                var argumentsText = item.Arguments.ToString();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(argumentsText))
                {
                    throw new JsonException("Das Modell lieferte einen unvollständigen gestreamten Toolaufruf.");
                }
                using var arguments = JsonDocument.Parse(argumentsText);
                calls.Add(new LmToolCall(
                    string.IsNullOrWhiteSpace(item.Id) ? $"call-{Guid.NewGuid():N}" : item.Id,
                    transportToolNames.GetValueOrDefault(name, name),
                    arguments.RootElement.Clone()));
            }
            if (calls.Count == 0
                && _content.Length == 0
                && TryParseReasoningToolCall(
                    _reasoningContent.ToString(),
                    transportToolNames,
                    out var reasoningCall))
            {
                calls.Add(reasoningCall);
            }
            return new LmChatResult(
                structuredToolOnly || _content.Length == 0 ? null : _content.ToString(),
                calls,
                InputTokens,
                OutputTokens > 0 ? OutputTokens : GeneratedFragments,
                HadReasoning,
                ReasoningTokens);
        }

        public bool TryBuildCompleteToolCall(
            IReadOnlyDictionary<string, string> transportToolNames,
            out LmChatResult result)
        {
            result = default!;
            if (!structuredToolOnly && _content.Length != 0)
            {
                return false;
            }

            if (_toolCalls.Count == 0
                && TryParseReasoningToolCall(
                    _reasoningContent.ToString(),
                    transportToolNames,
                    out var reasoningCall))
            {
                result = new LmChatResult(
                    null,
                    [reasoningCall],
                    InputTokens,
                    OutputTokens > 0 ? OutputTokens : GeneratedFragments,
                    HadReasoning,
                    ReasoningTokens);
                return true;
            }

            if (_toolCalls.Count != 1)
            {
                return false;
            }

            var call = _toolCalls.Values.Single();
            if (string.IsNullOrWhiteSpace(call.Name.ToString())
                || !IsCompleteJsonObject(call.Arguments.ToString()))
            {
                return false;
            }

            result = Build(transportToolNames);
            return true;
        }

        private string? AppendString(JsonElement source, string name, StringBuilder target)
        {
            if (source.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } text)
            {
                target.Append(text);
                GeneratedFragments++;
                return text;
            }
            return null;
        }

        private static bool IsCompleteJsonObject(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
            try
            {
                using var document = JsonDocument.Parse(value);
                return document.RootElement.ValueKind == JsonValueKind.Object;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }

    private sealed class StreamingToolCall
    {
        public string? Id { get; set; }
        public StringBuilder Name { get; } = new();
        public StringBuilder Arguments { get; } = new();
    }

    private sealed record StreamingAttemptSnapshot(
        int GeneratedFragments,
        int InputTokens,
        int OutputTokens,
        string? ToolName,
        int ArgumentCharacters,
        bool ToolArgumentsJsonComplete,
        int ContentCharacters,
        bool FinishObserved,
        bool Done,
        int ToolCallCount)
    {
        public static StreamingAttemptSnapshot Empty { get; } = new(
            0,
            0,
            0,
            null,
            0,
            false,
            0,
            false,
            false,
            0);
    }

    private sealed class IncompleteStreamingChatException(
        string failureKind,
        StreamingAttemptSnapshot snapshot,
        Exception? innerException = null)
        : IOException(
            $"LM Studio beendete den Streaming-Turn unvollständig ({failureKind}).",
            innerException)
    {
        public string FailureKind { get; } = failureKind;
        public StreamingAttemptSnapshot Snapshot { get; } = snapshot;
    }

    internal static bool IsTransientInferenceFailure(Exception exception)
    {
        if (exception is IOException)
        {
            return true;
        }
        if (exception is not HttpRequestException http)
        {
            return false;
        }
        return http.StatusCode is null
            or System.Net.HttpStatusCode.RequestTimeout
            or System.Net.HttpStatusCode.TooManyRequests
            or System.Net.HttpStatusCode.InternalServerError
            or System.Net.HttpStatusCode.BadGateway
            or System.Net.HttpStatusCode.ServiceUnavailable
            or System.Net.HttpStatusCode.GatewayTimeout;
    }

    private async Task<HttpResponseMessage> SendJsonAsync(
        HttpMethod method,
        string path,
        object body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body, options: _json),
        };
        var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            response.Dispose();
            detail = detail.Length <= 2_000 ? detail : detail[..2_000];
            throw new HttpRequestException(
                $"LM Studio returned HTTP {(int)response.StatusCode}: {detail}",
                null,
                response.StatusCode);
        }
        return response;
    }

    private static LmChatResult ParseChatResult(
        JsonElement root,
        IReadOnlyDictionary<string, string> transportToolNames,
        bool structuredToolOnly = false)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            throw new JsonException("Die LM-Studio-Antwort enthält keine Auswahl.");
        }
        var message = choices[0].GetProperty("message");
        var calls = new List<LmToolCall>();
        if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
        {
            if (toolCalls.GetArrayLength() > 1)
            {
                throw new JsonException("Der Modellturn darf genau einen Toolaufruf liefern.");
            }
            foreach (var item in toolCalls.EnumerateArray())
            {
                var function = item.GetProperty("function");
                var name = function.GetProperty("name").GetString();
                var argumentsProperty = function.GetProperty("arguments");
                var argumentsText = argumentsProperty.ValueKind == JsonValueKind.String
                    ? argumentsProperty.GetString()
                    : argumentsProperty.GetRawText();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(argumentsText))
                {
                    throw new JsonException("Das Modell lieferte einen unvollständigen Toolaufruf.");
                }
                using var arguments = JsonDocument.Parse(argumentsText);
                calls.Add(new LmToolCall(
                    item.TryGetProperty("id", out var id) && !string.IsNullOrWhiteSpace(id.GetString())
                        ? id.GetString()!
                        : $"call-{Guid.NewGuid():N}",
                    transportToolNames.GetValueOrDefault(name, name),
                    arguments.RootElement.Clone()));
            }
        }

        var inputTokens = 0;
        var outputTokens = 0;
        var reasoningTokens = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            inputTokens = ReadInt(usage, "prompt_tokens", "input_tokens");
            outputTokens = ReadInt(usage, "completion_tokens", "output_tokens");
            if (usage.TryGetProperty("completion_tokens_details", out var details))
            {
                reasoningTokens = ReadInt(details, "reasoning_tokens");
            }
        }
        var content = ReadContent(message);
        var hasReasoningContent = message.TryGetProperty("reasoning_content", out var reasoningContent)
            && reasoningContent.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(reasoningContent.GetString());
        if (calls.Count == 0
            && string.IsNullOrWhiteSpace(content)
            && hasReasoningContent
            && TryParseReasoningToolCall(
                reasoningContent.GetString()!,
                transportToolNames,
                out var reasoningCall))
        {
            calls.Add(reasoningCall);
        }
        return new LmChatResult(
            structuredToolOnly ? null : content,
            calls,
            inputTokens,
            outputTokens,
            hasReasoningContent || reasoningTokens > 0,
            reasoningTokens);
    }

    internal static bool TryParseReasoningToolCall(
        string reasoningContent,
        IReadOnlyDictionary<string, string> transportToolNames,
        out LmToolCall call)
    {
        call = default!;
        if (string.IsNullOrWhiteSpace(reasoningContent) || transportToolNames.Count == 0)
        {
            return false;
        }

        const string toolOpen = "<tool_call>";
        const string toolClose = "</tool_call>";
        var toolStart = reasoningContent.IndexOf(toolOpen, StringComparison.OrdinalIgnoreCase);
        if (toolStart < 0
            || reasoningContent.IndexOf(toolOpen, toolStart + toolOpen.Length, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return false;
        }
        var toolEnd = reasoningContent.IndexOf(toolClose, toolStart + toolOpen.Length, StringComparison.OrdinalIgnoreCase);
        if (toolEnd < 0
            || reasoningContent.IndexOf(toolClose, toolEnd + toolClose.Length, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return false;
        }

        var block = reasoningContent[(toolStart + toolOpen.Length)..toolEnd].Trim();
        const string functionPrefix = "<function=";
        const string functionClose = "</function>";
        if (!block.StartsWith(functionPrefix, StringComparison.OrdinalIgnoreCase)
            || !block.EndsWith(functionClose, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var functionTagEnd = block.IndexOf('>');
        if (functionTagEnd <= functionPrefix.Length)
        {
            return false;
        }
        var generatedName = block[functionPrefix.Length..functionTagEnd].Trim();
        if (!IsReasoningIdentifier(generatedName)
            || !TryResolveReasoningToolName(generatedName, transportToolNames, out var logicalName))
        {
            return false;
        }

        var parameterBlock = block[(functionTagEnd + 1)..^functionClose.Length];
        var parameters = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var offset = 0;
        const string parameterPrefix = "<parameter=";
        const string parameterClose = "</parameter>";
        while (offset < parameterBlock.Length)
        {
            while (offset < parameterBlock.Length && char.IsWhiteSpace(parameterBlock[offset]))
            {
                offset++;
            }
            if (offset >= parameterBlock.Length)
            {
                break;
            }
            if (!parameterBlock.AsSpan(offset).StartsWith(parameterPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            var tagEnd = parameterBlock.IndexOf('>', offset + parameterPrefix.Length);
            if (tagEnd < 0)
            {
                return false;
            }
            var parameterName = parameterBlock[(offset + parameterPrefix.Length)..tagEnd].Trim();
            if (!IsReasoningIdentifier(parameterName) || parameters.ContainsKey(parameterName))
            {
                return false;
            }
            var valueStart = tagEnd + 1;
            var valueEnd = parameterBlock.IndexOf(parameterClose, valueStart, StringComparison.OrdinalIgnoreCase);
            if (valueEnd < 0)
            {
                return false;
            }
            var rawValue = parameterBlock[valueStart..valueEnd].Trim();
            parameters.Add(parameterName, ParseReasoningParameterValue(rawValue));
            offset = valueEnd + parameterClose.Length;
        }

        var arguments = JsonSerializer.SerializeToElement(parameters);
        var callHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(block)))
            .ToLowerInvariant()[..16];
        call = new LmToolCall($"call-reasoning-{callHash}", logicalName, arguments);
        return true;
    }

    private static JsonElement ParseReasoningParameterValue(string rawValue)
    {
        if (!string.IsNullOrWhiteSpace(rawValue))
        {
            try
            {
                using var parsed = JsonDocument.Parse(rawValue);
                return parsed.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Qwen emits enum and path strings without JSON quotes in its
                // reasoning envelope. Preserve those values as plain strings;
                // the regular tool-schema validator remains authoritative.
            }
        }
        return JsonSerializer.SerializeToElement(rawValue);
    }

    private static bool TryResolveReasoningToolName(
        string generatedName,
        IReadOnlyDictionary<string, string> transportToolNames,
        out string logicalName)
    {
        if (transportToolNames.TryGetValue(generatedName, out logicalName!))
        {
            return true;
        }
        foreach (var candidate in transportToolNames.Values)
        {
            if (string.Equals(candidate, generatedName, StringComparison.Ordinal))
            {
                logicalName = candidate;
                return true;
            }
        }
        logicalName = string.Empty;
        return false;
    }

    private static bool IsReasoningIdentifier(string value) =>
        value.Length is > 0 and <= 128
        && value.All(static character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '_' or '-' or '.');

    private static object ToOpenAiMessage(LmChatMessage message)
    {
        if (string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
            && message.ToolCalls is { Count: > 0 })
        {
            return new
            {
                role = "assistant",
                content = message.Content,
                tool_calls = message.ToolCalls.Select(static call => new
                {
                    id = call.Id,
                    type = "function",
                    function = new
                    {
                        name = ToTransportToolName(call.Name),
                        arguments = call.Arguments.GetRawText(),
                    },
                }).ToArray(),
            };
        }
        if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                role = "tool",
                content = message.Content ?? string.Empty,
                tool_call_id = message.ToolCallId,
            };
        }
        return new { role = NormalizeRole(message.Role), content = message.Content ?? string.Empty };
    }

    internal static IReadOnlyList<LmChatMessage> NormalizeMessageOrderForLmStudio(
        IReadOnlyList<LmChatMessage> messages)
    {
        if (messages.Count == 0)
        {
            return [];
        }

        var normalized = new List<LmChatMessage>(messages.Count);
        var initialSystemParts = new List<string>();
        var conversationStarted = false;
        foreach (var message in messages)
        {
            if (string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase))
            {
                var content = message.Content?.Trim();
                if (!conversationStarted)
                {
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        initialSystemParts.Add(content);
                    }
                    continue;
                }

                // Qwen's LM Studio templates require every system instruction
                // to precede the conversation. Runtime retry and tool guidance
                // is chronological, so keep it in place as a controlled user
                // turn instead of moving stale instructions to the beginning.
                normalized.Add(new LmChatMessage(
                    "user",
                    string.IsNullOrWhiteSpace(content)
                        ? "GO-Laufanweisung: Setze den aktuellen Lauf am gespeicherten Stand fort."
                        : "GO-Laufanweisung:\n" + content));
                continue;
            }

            if (!conversationStarted)
            {
                if (initialSystemParts.Count > 0)
                {
                    normalized.Add(new LmChatMessage("system", string.Join("\n\n", initialSystemParts)));
                }
                conversationStarted = true;
            }
            normalized.Add(message);
        }

        if (!conversationStarted && initialSystemParts.Count > 0)
        {
            normalized.Add(new LmChatMessage("system", string.Join("\n\n", initialSystemParts)));
        }
        return normalized;
    }

    private static string? ReadContent(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind == JsonValueKind.Null)
            return null;
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString();
        if (content.ValueKind != JsonValueKind.Array)
            return content.GetRawText();
        return string.Join("\n", content.EnumerateArray()
            .Where(static part => part.TryGetProperty("text", out _))
            .Select(static part => part.GetProperty("text").GetString())
            .Where(static text => !string.IsNullOrWhiteSpace(text)));
    }

    private static int ReadInt(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed))
                return parsed;
        }
        return 0;
    }

    private static void ValidateToolChoice(
        IReadOnlyList<LmToolDefinition> tools,
        bool requireToolCall,
        string? requiredToolName)
    {
        if ((requireToolCall || !string.IsNullOrWhiteSpace(requiredToolName)) && tools.Count == 0)
            throw new ArgumentException("Ein erforderlicher Toolaufruf benötigt mindestens ein Tool.");
        if (!string.IsNullOrWhiteSpace(requiredToolName)
            && !tools.Any(tool => string.Equals(tool.Name, requiredToolName, StringComparison.Ordinal)))
            throw new ArgumentException($"Das erforderliche Tool '{requiredToolName}' ist nicht im Katalog.");
    }

    private static void ApplyModelSampling(Dictionary<string, object?> body, string modelId)
    {
        if (!CodingModelCatalog.TryGet(modelId, out var profile)
            || !string.Equals(profile.SamplingProfile, "qwen3-coder-next", StringComparison.Ordinal))
        {
            return;
        }

        // Official Qwen GGUF sampling defaults for native agent/tool use.
        body["temperature"] = 1.0;
        body["top_p"] = 0.95;
        body["top_k"] = 40;
        body["min_p"] = 0.0;
    }

    private void ApplyReasoningSettings(
        Dictionary<string, object?> body,
        string modelId,
        string modelRole,
        string? requestedEffort)
    {
        // No GO-side preset means LM Studio and the loaded model keep their own
        // default reasoning configuration. In particular, do not translate a
        // missing value into the catalog's advertised default.
        if (string.IsNullOrWhiteSpace(requestedEffort)
            || string.Equals(
                requestedEffort.Trim(),
                ModelReasoningProfiles.Automatic,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var profile = ResolveRuntimeReasoningProfile(modelId, modelRole);
        var explicitEffort = requestedEffort.Trim();
        if (!string.IsNullOrWhiteSpace(explicitEffort) && !profile.Supports(explicitEffort))
        {
            var supported = profile.SupportedEfforts.Count == 0
                ? "keine steuerbare Stufe"
                : string.Join(", ", profile.SupportedEfforts);
            throw new InvalidOperationException(
                $"reasoningEffort '{explicitEffort}' wird vom ausgewÃ¤hlten Modell nicht unterstÃ¼tzt ({supported}).");
        }
        var effort = profile.Resolve(explicitEffort);
        if (string.IsNullOrWhiteSpace(effort))
        {
            return;
        }

        if (string.Equals(profile.Family, "lmstudio-native", StringComparison.Ordinal)
            || string.Equals(profile.Family, ModelReasoningProfiles.GptOssFamily, StringComparison.Ordinal))
        {
            body["reasoning_effort"] = string.Equals(effort, "none", StringComparison.OrdinalIgnoreCase)
                ? "off"
                : effort;
            return;
        }

        if (string.Equals(profile.Family, ModelReasoningProfiles.Qwen38Family, StringComparison.Ordinal))
        {
            body["chat_template_kwargs"] = string.Equals(effort, "none", StringComparison.Ordinal)
                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["enable_thinking"] = false,
                }
                : new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["enable_thinking"] = true,
                    ["reasoning_effort"] = effort,
                };
        }
    }

    private static string NormalizeRole(string role) => role.ToLowerInvariant() switch
    {
        "system" => "system",
        "assistant" => "assistant",
        "tool" => "tool",
        _ => "user",
    };

    internal static string ToTransportToolName(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        var readable = new StringBuilder(toolName.Length);
        var previousWasSeparator = false;
        foreach (var character in toolName)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                readable.Append(char.ToLowerInvariant(character));
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator)
            {
                readable.Append('_');
                previousWasSeparator = true;
            }
        }

        var stem = readable.ToString().Trim('_');
        if (stem.Length == 0)
        {
            stem = "tool";
        }
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(toolName)))
            .ToLowerInvariant()[..8];
        const int maximumStemLength = 51; // go_ + stem + _ + hash <= 63
        if (stem.Length > maximumStemLength)
        {
            stem = stem[..maximumStemLength].TrimEnd('_');
        }
        return $"go_{stem}_{hash}";
    }

    private static string ResolveKnownRuntimeModelId(string modelId) => modelId.ToLowerInvariant() switch
    {
        CodingModelCatalog.GptOss120BId => CodingModelCatalog.GptOss120BRuntimeId,
        CodingModelCatalog.Qwen38Id => CodingModelCatalog.Qwen38RuntimeId,
        CodingModelCatalog.Qwen3CoderNextQ8Id => CodingModelCatalog.Qwen3CoderNextRuntimeId,
        "qwen3-vl-30b-a3b-instruct" => "qwen3-vl-30b-a3b-instruct",
        "text-embedding-bge-m3" => "text-embedding-bge-m3",
        _ => modelId,
    };

    internal static RuntimeModel[] ReadRuntimeModels(JsonElement root)
    {
        var array = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                ? data
                : root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array
                    ? models
                    : default;
        if (array.ValueKind != JsonValueKind.Array)
            throw new JsonException("LM Studio lieferte keinen Modellkatalog.");
        return array.EnumerateArray().Select(static item =>
        {
            var id = item.TryGetProperty("key", out var keyValue) ? keyValue.GetString()
                : item.TryGetProperty("id", out var idValue) ? idValue.GetString()
                : item.TryGetProperty("model", out var modelValue) ? modelValue.GetString()
                : null;
            var maximumContextLength = item.TryGetProperty("max_context_length", out var maximumContext)
                && maximumContext.TryGetInt32(out var parsedMaximumContext)
                    ? parsedMaximumContext
                    : 0;
            var type = item.TryGetProperty("type", out var typeValue)
                ? typeValue.GetString() ?? "llm"
                : "llm";
            var displayName = item.TryGetProperty("display_name", out var displayNameValue)
                ? displayNameValue.GetString()
                : null;
            var architecture = item.TryGetProperty("architecture", out var architectureValue)
                ? architectureValue.GetString()
                : null;
            string? quantization = null;
            if (item.TryGetProperty("quantization", out var quantizationValue))
            {
                quantization = quantizationValue.ValueKind == JsonValueKind.String
                    ? quantizationValue.GetString()
                    : quantizationValue.ValueKind == JsonValueKind.Object
                        && quantizationValue.TryGetProperty("name", out var quantizationName)
                            ? quantizationName.GetString()
                            : null;
            }
            var supportsVision = false;
            var supportsTools = false;
            IReadOnlyList<string> reasoningEfforts = [];
            string? defaultReasoningEffort = null;
            if (item.TryGetProperty("capabilities", out var capabilities)
                && capabilities.ValueKind == JsonValueKind.Object)
            {
                supportsVision = ReadBoolean(capabilities, "vision");
                supportsTools = ReadBoolean(capabilities, "trained_for_tool_use");
                if (capabilities.TryGetProperty("reasoning", out var reasoning)
                    && reasoning.ValueKind == JsonValueKind.Object)
                {
                    reasoningEfforts = ReadReasoningEfforts(reasoning);
                    defaultReasoningEffort = reasoning.TryGetProperty("default", out var defaultValue)
                        ? NormalizeReasoningEffort(defaultValue.GetString())
                        : null;
                }
            }
            string? instanceId = null;
            var loadedContextLength = 0;
            if (item.TryGetProperty("loaded_instances", out var loadedInstances)
                && loadedInstances.ValueKind == JsonValueKind.Array
                && loadedInstances.GetArrayLength() > 0)
            {
                var loadedInstance = loadedInstances[0];
                instanceId = loadedInstance.TryGetProperty("id", out var instance)
                    ? instance.GetString()
                    : null;
                if (loadedInstance.TryGetProperty("config", out var config)
                    && config.ValueKind == JsonValueKind.Object
                    && config.TryGetProperty("context_length", out var loadedContext)
                    && loadedContext.TryGetInt32(out var parsedLoadedContext))
                {
                    loadedContextLength = parsedLoadedContext;
                }
            }
            return new RuntimeModel(
                id ?? string.Empty,
                type,
                string.IsNullOrWhiteSpace(instanceId) ? "unloaded" : "loaded",
                instanceId,
                maximumContextLength,
                loadedContextLength,
                displayName,
                architecture,
                quantization,
                supportsTools,
                supportsVision,
                reasoningEfforts,
                defaultReasoningEffort);
        }).Where(static model => model.Id.Length > 0).ToArray();
    }

    private static bool MatchesRuntimeModel(RuntimeModel candidate, string runtimeModelId) =>
        RuntimeIdentifiersEqual(candidate.Id, runtimeModelId)
        || RuntimeIdentifiersEqual(candidate.InstanceId, runtimeModelId);

    private static bool RuntimeIdentifiersEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // LM Studio exposes the downloaded catalog key without a publisher
        // prefix (for example "qwen3.8-27b"), while GO deliberately keeps the
        // stable load identifier ("qwen/qwen3.8-27b"). Loaded instance IDs may
        // use either form. Match the terminal alias as well so status, reuse,
        // unload and model switching all observe the same installed model.
        return string.Equals(
            TerminalRuntimeAlias(left),
            TerminalRuntimeAlias(right),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string TerminalRuntimeAlias(string value)
    {
        var normalized = value.Trim().TrimEnd('/').Replace('\\', '/');
        var separator = normalized.LastIndexOf('/');
        return separator >= 0 ? normalized[(separator + 1)..] : normalized;
    }

    private static ModelRuntimeStatus[] BuildRuntimeStatuses(RuntimeModel[] runtimeModels)
    {
        var statuses = new List<ModelRuntimeStatus>(runtimeModels.Length * 2);
        foreach (var model in runtimeModels)
        {
            var context = model.MaximumContextLength > 0 ? model.MaximumContextLength : 2_048;
            var displayName = string.IsNullOrWhiteSpace(model.Quantization)
                ? model.DisplayName ?? model.Id
                : $"{model.DisplayName ?? model.Id} · {model.Quantization}";
            var loaded = string.Equals(model.State, "loaded", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(model.Type, "embedding", StringComparison.OrdinalIgnoreCase))
            {
                statuses.Add(CreateRuntimeStatus(model, "embedding", context, displayName, loaded));
                continue;
            }

            statuses.Add(CreateRuntimeStatus(model, "general", context, displayName, loaded));
            statuses.Add(CreateRuntimeStatus(model, "code", context, displayName, loaded));
            if (model.SupportsVision)
            {
                statuses.Add(CreateRuntimeStatus(model, "vision", context, displayName, loaded));
            }
        }
        return [.. statuses];
    }

    private static ModelRuntimeStatus CreateRuntimeStatus(
        RuntimeModel model,
        string role,
        int context,
        string displayName,
        bool loaded)
    {
        var reasoning = ResolveRuntimeReasoningProfile(model, role);
        return new ModelRuntimeStatus(
            model.Id,
            role,
            true,
            loaded,
            model.State,
            context,
            displayName,
            model.SupportsTools,
            model.SupportsVision,
            reasoning.SupportedEfforts,
            reasoning.DefaultEffort,
            model.Architecture,
            model.Quantization);
    }

    private static ModelDefinition CreateDefinition(RuntimeModel model) => new(
        model.Id,
        model.Id,
        string.Equals(model.Type, "embedding", StringComparison.OrdinalIgnoreCase) ? "embedding" : "general",
        model.MaximumContextLength > 0 ? model.MaximumContextLength : 2_048,
        model.DisplayName ?? model.Id);

    private void RememberRuntimeModels(IEnumerable<RuntimeModel> models)
    {
        lock (_cacheLock)
        {
            _runtimeCatalog.Clear();
            foreach (var model in models)
            {
                _runtimeCatalog[model.Id] = model;
            }
        }
    }

    private bool TryGetRuntimeModel(string modelId, out RuntimeModel model)
    {
        lock (_cacheLock)
        {
            model = _runtimeCatalog.Values.FirstOrDefault(candidate =>
                MatchesRuntimeModel(candidate, ResolveKnownRuntimeModelId(modelId)))!;
            return model is not null;
        }
    }

    private ModelReasoningProfile ResolveRuntimeReasoningProfile(string modelId, string role) =>
        TryGetRuntimeModel(modelId, out var model)
            ? ResolveRuntimeReasoningProfile(model, role)
            : ModelReasoningProfiles.Resolve(modelId, role);

    private static ModelReasoningProfile ResolveRuntimeReasoningProfile(RuntimeModel model, string role)
    {
        if (model.ReasoningEfforts.Count > 0)
        {
            var defaultEffort = model.DefaultReasoningEffort;
            if (string.IsNullOrWhiteSpace(defaultEffort)
                || !model.ReasoningEfforts.Contains(defaultEffort, StringComparer.OrdinalIgnoreCase))
            {
                defaultEffort = model.ReasoningEfforts[0];
            }
            return new ModelReasoningProfile("lmstudio-native", model.ReasoningEfforts, defaultEffort);
        }

        // LM Studio versions before the reasoning-capability field still expose
        // architecture and model key. Retain the known OpenAI-compatible
        // gpt-oss control while treating every other unknown model conservatively.
        return model.Id.Contains("gpt-oss", StringComparison.OrdinalIgnoreCase)
            ? ModelReasoningProfiles.Resolve(model.Id, role)
            : new ModelReasoningProfile(ModelReasoningProfiles.UnknownFamily, ["none"], "none");
    }

    private static bool ReadBoolean(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
        && value.GetBoolean();

    private static string[] ReadReasoningEfforts(JsonElement reasoning)
    {
        if (!reasoning.TryGetProperty("allowed_options", out var options)
            || options.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return options.EnumerateArray()
            .Select(static value => NormalizeReasoningEffort(value.GetString()))
            .Where(static value => value is not null)
            .Select(static value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? NormalizeReasoningEffort(string? effort) => effort?.Trim().ToLowerInvariant() switch
    {
        "off" => "none",
        "none" => "none",
        "on" => "on",
        "minimal" => "minimal",
        "low" => "low",
        "medium" => "medium",
        "high" => "high",
        "xhigh" => "xhigh",
        _ => null,
    };

    private ModelStatusSnapshot Cache(ModelStatusSnapshot status)
    {
        lock (_cacheLock)
        {
            _cachedStatus = status;
            if (status.ProviderReachable && status.Models.Count > 0)
            {
                _lastReachableModels = status.Models;
            }
            _cacheExpiresAt = DateTimeOffset.UtcNow.Add(StatusCacheDuration);
        }
        return status;
    }

    private IReadOnlyList<ModelRuntimeStatus> GetLastReachableModels()
    {
        lock (_cacheLock)
        {
            return _lastReachableModels;
        }
    }

    private void InvalidateStatus()
    {
        lock (_cacheLock)
        {
            _cachedStatus = null;
            _cacheExpiresAt = default;
        }
    }

    private static Uri EnsureTrailingSlash(Uri uri) => uri.AbsoluteUri.EndsWith('/')
        ? uri
        : new Uri(uri.AbsoluteUri + "/", UriKind.Absolute);

    public void Dispose()
    {
        _modelGate.Dispose();
        _turnGate.Dispose();
    }

    private sealed record ModelDefinition(
        string Id,
        string RuntimeModelId,
        string Role,
        int ContextTokens,
        string DisplayName);

    internal sealed record RuntimeModel(
        string Id,
        string Type,
        string State,
        string? InstanceId,
        int MaximumContextLength,
        int LoadedContextLength,
        string? DisplayName,
        string? Architecture,
        string? Quantization,
        bool SupportsTools,
        bool SupportsVision,
        IReadOnlyList<string> ReasoningEfforts,
        string? DefaultReasoningEffort);
}
