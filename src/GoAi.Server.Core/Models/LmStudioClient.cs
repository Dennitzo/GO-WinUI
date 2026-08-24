using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Security;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace GoAi.Server.Core.Models;

public sealed partial class LmStudioClient : IDisposable
{
    // Legacy router constants remain until the compatibility helpers below are
    // removed after downstream test migration. The active coding path never
    // invokes that two-pass router.
    private const string CodingToolFinalSelection = "__final__";
    private const int MaximumCodingRouterUserCharacters = 6_000;
    private const int MaximumCodingRouterHistoryCharacters = 8_000;
    private const int MaximumCodingRouterEntryCharacters = 1_200;
    private const int MaximumCodingRecoverySchemaCharacters = 48_000;
    private const int MaximumCodingArgumentUserCharacters = 10_000;
    private const int MaximumCodingArgumentRepositoryCharacters = 16_000;
    private const int MaximumCodingArgumentHistoryCharacters = 64_000;
    private const int MaximumCodingArgumentEntryCharacters = 48_000;
    private readonly HttpClient _httpClient;
    private readonly GoAiServerOptions _options;
    private readonly DpapiSecretStore _secretStore;
    private readonly ILogger<LmStudioClient> _logger;
    private readonly ILmStudioNativeAgentClient? _nativeAgentClient;
    private readonly bool _ownsHttpClient;
    private readonly SemaphoreSlim _modelOperationGate = new(1, 1);
    private readonly SemaphoreSlim _statusGate = new(1, 1);
    private readonly object _statusCacheSync = new();
    private ModelStatusSnapshot? _cachedStatus;
    private DateTimeOffset _statusCacheExpiresAt;
    private static readonly TimeSpan StatusCacheDuration = TimeSpan.FromSeconds(15);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
    };

    internal LmStudioClient(
        HttpClient httpClient,
        IOptions<GoAiServerOptions> options,
        DpapiSecretStore secretStore,
        ILogger<LmStudioClient> logger)
        : this(httpClient, options, secretStore, logger, nativeAgentClient: null)
    {
    }

    internal LmStudioClient(
        HttpClient httpClient,
        IOptions<GoAiServerOptions> options,
        DpapiSecretStore secretStore,
        ILogger<LmStudioClient> logger,
        ILmStudioNativeAgentClient? nativeAgentClient)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _secretStore = secretStore;
        _logger = logger;
        _nativeAgentClient = nativeAgentClient;
        _httpClient.BaseAddress = EnsureTrailingSlash(_options.LmStudioUri);
        // Coding requests, especially with large local profiles and a long
        // prompt prefix, can legitimately take longer than 30 minutes. The run
        // processor already owns the authoritative timeout and cancellation token,
        // so a second HttpClient timeout must not terminate an otherwise healthy run.
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    internal LmStudioClient(
        IOptions<GoAiServerOptions> options,
        DpapiSecretStore secretStore,
        ILogger<LmStudioClient> logger,
        ILmStudioNativeAgentClient nativeAgentClient)
        : this(
            new HttpClient(new SdkOnlyHttpMessageHandler(), disposeHandler: true),
            options,
            secretStore,
            logger,
            nativeAgentClient)
    {
        _ownsHttpClient = true;
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

            var result = await GetRawModelsAsync(cancellationToken).ConfigureAwait(false);
            var models = CreateConfiguredModelStatus(result.Models);
            return CacheStatus(new ModelStatusSnapshot(true, _options.LmStudioUri.ToString(), models, DateTimeOffset.UtcNow));
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException
            && exception is not OutOfMemoryException)
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
        CancellationToken cancellationToken = default) =>
        (await EnsureModelPreparedAsync(
            modelId,
            contextLength,
            loadingStarted: null,
            cancellationToken).ConfigureAwait(false)).InstanceId;

    internal async Task<LmStudioModelPreparation> EnsureModelPreparedAsync(
        string modelId,
        int contextLength,
        Func<CancellationToken, Task>? loadingStarted,
        CancellationToken cancellationToken = default)
    {
        await BeginModelOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = await GetRawModelsAsync(cancellationToken).ConfigureAwait(false);
            var selected = status.Models.FirstOrDefault(model => string.Equals(model.Key, modelId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Configured LM Studio model is not downloaded: {modelId}");
            var isCodingModel = CodingModelCatalog.TryGet(modelId, out _);
            if (isCodingModel
                && selected.MaximumContextLength < contextLength)
            {
                throw new LmStudioContextLengthException(modelId, contextLength, selected.MaximumContextLength);
            }
            var expectedContextLength = Math.Min(contextLength, selected.MaximumContextLength);
            var loaded = selected.LoadedInstances is { Count: > 0 } loadedInstances
                ? loadedInstances[0]
                : null;
            var isEmbedding = string.Equals(selected.Type, "embedding", StringComparison.OrdinalIgnoreCase);
            var canReuseLoadedInstance = loaded is not null
                && HasRequiredConfiguration(loaded, expectedContextLength, isEmbedding);

            if (!canReuseLoadedInstance && loadingStarted is not null)
            {
                await loadingStarted(cancellationToken).ConfigureAwait(false);
            }

            await UnloadIncompatibleModelsAsync(status.Models, modelId, cancellationToken).ConfigureAwait(false);
            if (canReuseLoadedInstance)
            {
                // LM Studio is the authoritative source for actual residency and the
                // effective context/load profile. A process marker is useful for
                // diagnostics, but it must never force an otherwise compatible,
                // already resident coding model through an unload/reload cycle.
                InvalidateStatusCache();
                return new LmStudioModelPreparation(
                    loaded!.ModelInstanceId ?? loaded.Id ?? modelId,
                    WasAlreadyLoaded: true);
            }

            if (loaded is not null)
            {
                await UnloadModelInstancesAsync([selected], cancellationToken).ConfigureAwait(false);
            }
            // Explicit loads intentionally have no TTL. The selected model remains
            // resident until a later AI request asks for an incompatible target.
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
            return new LmStudioModelPreparation(
                await LoadModelWithRetryAsync(
                    modelId,
                    body,
                    expectedContextLength,
                    isEmbedding,
                    cancellationToken).ConfigureAwait(false),
                WasAlreadyLoaded: false);
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
            _modelOperationGate.Release();
        }
    }

    public async Task<bool> UnloadModelAsync(
        string modelId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        await BeginModelOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = await GetRawModelsAsync(cancellationToken).ConfigureAwait(false);
            var matches = status.Models
                .Where(model => string.Equals(model.Key, modelId, StringComparison.OrdinalIgnoreCase)
                    && model.LoadedInstances is { Count: > 0 })
                .ToArray();
            if (matches.Length == 0)
            {
                return false;
            }

            await UnloadModelInstancesAsync(matches, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            InvalidateStatusCache();
            EndModelOperation();
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

    public Task<LmChatResult> CompleteChatAsync(
        string modelId,
        IReadOnlyList<LmChatMessage> messages,
        IReadOnlyList<LmToolDefinition> tools,
        int maximumOutputTokens,
        CancellationToken cancellationToken) =>
        CompleteChatAsync(
            modelId,
            messages,
            tools,
            maximumOutputTokens,
            modelRole: "general",
            cancellationToken: cancellationToken);

    public async Task<LmChatResult> CompleteChatAsync(
        string modelId,
        IReadOnlyList<LmChatMessage> messages,
        IReadOnlyList<LmToolDefinition> tools,
        int maximumOutputTokens = 8_192,
        string modelRole = "general",
        string? reasoningEffort = null,
        bool requireToolCall = false,
        string? requiredToolName = null,
        Func<LmStudioNativeAgentProgress, CancellationToken, ValueTask>? nativeProgress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredToolChoice(tools, requireToolCall, requiredToolName);
        await BeginModelOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        var codingModelRequest = string.Equals(modelRole, "code", StringComparison.OrdinalIgnoreCase);
        if (_nativeAgentClient is not null)
        {
            return codingModelRequest
                ? await CompleteCodingAgentRoundViaNativeSdkAsync(
                    modelId,
                    messages,
                    tools,
                    maximumOutputTokens,
                    reasoningEffort,
                    requireToolCall,
                    requiredToolName,
                    nativeProgress,
                    cancellationToken).ConfigureAwait(false)
                : await CompleteModelRoundViaNativeSdkAsync(
                    modelId,
                    messages,
                    tools,
                    maximumOutputTokens,
                    modelRole,
                    reasoningEffort,
                    requireToolCall,
                    requiredToolName,
                    nativeProgress,
                    cancellationToken).ConfigureAwait(false);
        }
        if (codingModelRequest)
        {
            return await CompleteCodingAgentRoundAsync(
                modelId,
                messages,
                tools,
                maximumOutputTokens,
                reasoningEffort,
                requireToolCall,
                requiredToolName,
                nativeProgress,
                cancellationToken).ConfigureAwait(false);
        }

        var primarySystemMessageIndex = FindPrimarySystemMessageIndex(messages);
        var instructions = primarySystemMessageIndex >= 0
            ? messages[primarySystemMessageIndex].Content ?? string.Empty
            : string.Empty;
        var input = CreateResponsesInput(messages, primarySystemMessageIndex);
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
            ["max_output_tokens"] = Math.Clamp(maximumOutputTokens, 1, 65_536),
            ["store"] = false,
        };
        ApplySamplingProfile(
            body,
            modelId,
            includeReasoning: true,
            modelRole,
            reasoningEffort,
            maximumOutputTokens);
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
            var fallback = await CompleteChatViaChatCompletionsAsync(
                modelId,
                messages,
                tools,
                maximumOutputTokens,
                modelRole,
                reasoningEffort,
                requireToolCall: false,
                requiredToolName: null,
                cancellationToken).ConfigureAwait(false);
            return fallback;
        }
        catch (HttpRequestException exception) when (
            exception.StatusCode is { } statusCode && IsTransientProviderStatus(statusCode))
        {
            LogResponsesEndpointFallback((int)statusCode, modelId);
            var fallback = await CompleteChatViaChatCompletionsAsync(
                modelId,
                messages,
                tools,
                maximumOutputTokens,
                modelRole,
                reasoningEffort,
                requireToolCall: false,
                requiredToolName: null,
                cancellationToken).ConfigureAwait(false);
            return fallback;
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
        var hadReasoning = false;
        foreach (var item in output.EnumerateArray())
        {
            var type = item.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString()
                : null;
            if (string.Equals(type, "reasoning", StringComparison.Ordinal))
            {
                // Reasoning text remains private. We only retain its presence and
                // token count so the agent can distinguish provider budget exhaustion
                // from an actually empty model response.
                hadReasoning = true;
                continue;
            }
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
        var reasoningTokens = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            inputTokens = TryReadInt32(usage, "input_tokens");
            outputTokens = TryReadInt32(usage, "output_tokens");
            if (usage.TryGetProperty("output_tokens_details", out var outputDetails))
            {
                reasoningTokens = TryReadInt32(outputDetails, "reasoning_tokens");
            }
        }

        var content = textParts.Count == 0 ? null : string.Join("\n", textParts);
        return new LmChatResult(content, calls, inputTokens, outputTokens, hadReasoning, reasoningTokens);
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

        if (_nativeAgentClient is not null)
        {
            return await _nativeAgentClient.CreateEmbeddingsAsync(
                modelId,
                inputs,
                cancellationToken).ConfigureAwait(false);
        }

        var body = new { model = modelId, input = inputs };
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

    private async Task<LmChatResult> CompleteSingleCodingToolTurnWithRecoveryAsync(
        string modelId,
        Dictionary<string, object?> body,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 2;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            try
            {
                return await CompleteSingleCodingToolTurnViaResponsesStreamAsync(
                    modelId,
                    body,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (LmStudioGenerationTerminatedException exception) when (
                attempt < maximumAttempts
                && !cancellationToken.IsCancellationRequested)
            {
                LogResponsesStreamRetry(exception.ProviderCode, attempt + 1, modelId);
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("LM Studio Responses stream recovery loop ended unexpectedly.");
    }

    private async Task<LmChatResult> CompleteSingleCodingToolTurnViaResponsesStreamAsync(
        string modelId,
        Dictionary<string, object?> body,
        CancellationToken cancellationToken)
    {
        using var response = await SendResponsesStreamWithRetryAsync(
            modelId,
            body,
            cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: false);
        var visibleText = new StringBuilder();
        PendingFunctionCall? pendingCall = null;
        var hadReasoning = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }
            if (!line.StartsWith("data:", StringComparison.Ordinal))
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
            var eventType = ReadString(root, "type");
            switch (eventType)
            {
                case "response.output_text.delta":
                    if (ReadString(root, "delta") is { Length: > 0 } delta)
                    {
                        visibleText.Append(delta);
                    }
                    break;

                case "response.output_text.done":
                    if (visibleText.Length == 0 && ReadString(root, "text") is { Length: > 0 } completedText)
                    {
                        visibleText.Append(completedText);
                    }
                    break;

                case "response.output_item.added":
                    if (!root.TryGetProperty("item", out var addedItem)
                        || addedItem.ValueKind != JsonValueKind.Object)
                    {
                        break;
                    }
                    var addedType = ReadString(addedItem, "type");
                    if (string.Equals(addedType, "reasoning", StringComparison.Ordinal))
                    {
                        hadReasoning = true;
                    }
                    else if (pendingCall is null
                        && string.Equals(addedType, "function_call", StringComparison.Ordinal))
                    {
                        pendingCall = PendingFunctionCall.FromItem(
                            addedItem,
                            TryReadInt32(root, "output_index"));
                    }
                    break;

                case "response.function_call_arguments.delta":
                    if (pendingCall is not null
                        && pendingCall.Matches(root)
                        && ReadString(root, "delta") is { Length: > 0 } argumentsDelta)
                    {
                        pendingCall.Arguments.Append(argumentsDelta);
                    }
                    break;

                case "response.function_call_arguments.done":
                    if (pendingCall is not null && pendingCall.Matches(root))
                    {
                        return CreateCompleteToolTurnResult(
                            pendingCall,
                            ReadString(root, "arguments"),
                            visibleText,
                            hadReasoning);
                    }
                    break;

                case "response.output_item.done":
                    if (!root.TryGetProperty("item", out var completedItem)
                        || completedItem.ValueKind != JsonValueKind.Object
                        || !string.Equals(ReadString(completedItem, "type"), "function_call", StringComparison.Ordinal))
                    {
                        break;
                    }
                    pendingCall ??= PendingFunctionCall.FromItem(
                        completedItem,
                        TryReadInt32(root, "output_index"));
                    if (pendingCall.Matches(root))
                    {
                        pendingCall.FillMissingMetadata(completedItem);
                        return CreateCompleteToolTurnResult(
                            pendingCall,
                            ReadString(completedItem, "arguments"),
                            visibleText,
                            hadReasoning);
                    }
                    break;

                case "response.completed":
                    var usage = TryReadUsage(root);
                    if (pendingCall is not null)
                    {
                        return CreateCompleteToolTurnResult(
                            pendingCall,
                            argumentsOverride: null,
                            visibleText,
                            hadReasoning,
                            usage.InputTokens,
                            usage.OutputTokens,
                            TryReadReasoningTokens(root));
                    }
                    return new LmChatResult(
                        visibleText.Length == 0 ? null : visibleText.ToString(),
                        [],
                        usage.InputTokens,
                        usage.OutputTokens,
                        hadReasoning,
                        TryReadReasoningTokens(root));

                case "error":
                case "response.failed":
                    if (cancellationToken.IsCancellationRequested)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    throw new LmStudioGenerationTerminatedException(ReadResponsesStreamError(root));
            }
        }

        if (pendingCall is not null)
        {
            return CreateCompleteToolTurnResult(
                pendingCall,
                argumentsOverride: null,
                visibleText,
                hadReasoning);
        }
        if (visibleText.Length > 0)
        {
            return new LmChatResult(visibleText.ToString(), [], 0, 0, hadReasoning);
        }
        throw new InvalidOperationException("LM Studio Responses stream ended without text or a complete function call.");
    }

    private async Task<LmChatResult> CompleteChatViaStreamingChatCompletionsWithRecoveryAsync(
        string modelId,
        IReadOnlyList<LmChatMessage> messages,
        IReadOnlyList<LmToolDefinition> tools,
        int maximumOutputTokens,
        string modelRole,
        string? reasoningEffort,
        bool requireToolCall,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 2;
        LmStudioGenerationTerminatedException? lastTermination = null;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            try
            {
                return await CompleteChatViaStreamingChatCompletionsAsync(
                    modelId,
                    messages,
                    tools,
                    maximumOutputTokens,
                    modelRole,
                    reasoningEffort,
                    requireToolCall,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (LmStudioGenerationTerminatedException exception) when (
                attempt < maximumAttempts
                && !cancellationToken.IsCancellationRequested)
            {
                lastTermination = exception;
                LogChatCompletionsStreamRetry(exception.ProviderCode, attempt + 1, modelId);
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception) when (
                attempt < maximumAttempts
                && !cancellationToken.IsCancellationRequested)
            {
                lastTermination = new LmStudioGenerationTerminatedException(
                    "chat_completions_channel_error",
                    exception);
                LogChatCompletionsStreamRetry("channel_error", attempt + 1, modelId);
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
        }

        throw lastTermination
            ?? new LmStudioGenerationTerminatedException("chat_completions_incomplete_tool_call");
    }

    private async Task<LmChatResult> CompleteChatViaStreamingChatCompletionsAsync(
        string modelId,
        IReadOnlyList<LmChatMessage> messages,
        IReadOnlyList<LmToolDefinition> tools,
        int maximumOutputTokens,
        string modelRole,
        string? reasoningEffort,
        bool requireToolCall,
        CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = modelId,
            ["messages"] = CreateChatCompletionMessages(messages),
            ["stream"] = false,
            ["max_tokens"] = Math.Clamp(maximumOutputTokens, 1, 65_536),
        };
        ApplySamplingProfile(
            body,
            modelId,
            includeReasoning: false,
            modelRole,
            reasoningEffort,
            maximumOutputTokens);
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
            body["tool_choice"] = requireToolCall ? "required" : "auto";
            body["parallel_tool_calls"] = false;
        }

        using var request = await CreateRequestAsync(
            HttpMethod.Post,
            "v1/chat/completions",
            body,
            cancellationToken).ConfigureAwait(false);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: false);

        var visibleText = new StringBuilder();
        var toolCallId = (string?)null;
        var toolName = (string?)null;
        var toolArguments = new StringBuilder();
        var sawToolCall = false;
        var inputTokens = 0;
        var outputTokens = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line.AsSpan(5).TrimStart().ToString();
            if (payload.Length == 0 || string.Equals(payload, "[DONE]", StringComparison.Ordinal))
            {
                continue;
            }

            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.TryGetProperty("usage", out var usage)
                && usage.ValueKind == JsonValueKind.Object)
            {
                inputTokens = TryReadInt32(usage, "prompt_tokens");
                outputTokens = TryReadInt32(usage, "completion_tokens");
            }
            if (!root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                continue;
            }

            var choice = choices[0];
            if (choice.TryGetProperty("delta", out var delta)
                && delta.ValueKind == JsonValueKind.Object)
            {
                if (delta.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.String
                    && content.GetString() is { Length: > 0 } text)
                {
                    visibleText.Append(text);
                }

                if (delta.TryGetProperty("tool_calls", out var toolCalls)
                    && toolCalls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var toolCall in toolCalls.EnumerateArray())
                    {
                        if (toolCall.TryGetProperty("index", out var index)
                            && index.TryGetInt32(out var toolIndex)
                            && toolIndex != 0)
                        {
                            continue;
                        }

                        sawToolCall = true;
                        if (toolCallId is null
                            && toolCall.TryGetProperty("id", out var id)
                            && id.ValueKind == JsonValueKind.String)
                        {
                            toolCallId = id.GetString();
                        }
                        if (!toolCall.TryGetProperty("function", out var function)
                            || function.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }
                        if (toolName is null
                            && function.TryGetProperty("name", out var name)
                            && name.ValueKind == JsonValueKind.String)
                        {
                            toolName = name.GetString();
                        }
                        if (function.TryGetProperty("arguments", out var arguments)
                            && arguments.ValueKind == JsonValueKind.String)
                        {
                            toolArguments.Append(arguments.GetString());
                        }
                    }
                }
            }

            if (choice.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.Object)
            {
                if (message.TryGetProperty("content", out var messageContent)
                    && messageContent.ValueKind == JsonValueKind.String
                    && visibleText.Length == 0)
                {
                    visibleText.Append(messageContent.GetString());
                }
                if (message.TryGetProperty("tool_calls", out var messageToolCalls)
                    && messageToolCalls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var toolCall in messageToolCalls.EnumerateArray())
                    {
                        sawToolCall = true;
                        toolCallId ??= toolCall.TryGetProperty("id", out var id) ? id.GetString() : null;
                        if (toolCall.TryGetProperty("function", out var function)
                            && function.ValueKind == JsonValueKind.Object)
                        {
                            toolName ??= function.TryGetProperty("name", out var name) ? name.GetString() : null;
                            if (function.TryGetProperty("arguments", out var arguments)
                                && arguments.ValueKind == JsonValueKind.String)
                            {
                                toolArguments.Append(arguments.GetString());
                            }
                        }
                    }
                }
            }

            if (sawToolCall
                && TryCreateChatCompletionToolCall(
                    toolCallId,
                    toolName,
                    toolArguments.ToString(),
                    out var completedCall))
            {
                return new LmChatResult(
                    visibleText.Length == 0 ? null : visibleText.ToString(),
                    [completedCall],
                    inputTokens,
                    outputTokens);
            }
        }

        if (sawToolCall)
        {
            throw new LmStudioGenerationTerminatedException("chat_completions_incomplete_tool_call");
        }
        if (visibleText.Length > 0)
        {
            return new LmChatResult(visibleText.ToString(), [], inputTokens, outputTokens);
        }
        throw new LmStudioGenerationTerminatedException("chat_completions_empty_response");
    }

    private static bool TryCreateChatCompletionToolCall(
        string? id,
        string? name,
        string argumentsText,
        out LmToolCall call)
    {
        call = default!;
        if (string.IsNullOrWhiteSpace(id)
            || string.IsNullOrWhiteSpace(name)
            || string.IsNullOrWhiteSpace(argumentsText))
        {
            return false;
        }

        try
        {
            using var argumentsDocument = JsonDocument.Parse(argumentsText);
            call = new LmToolCall(id, name, argumentsDocument.RootElement.Clone());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static LmChatResult CreateCompleteToolTurnResult(
        PendingFunctionCall pendingCall,
        string? argumentsOverride,
        StringBuilder visibleText,
        bool hadReasoning,
        int inputTokens = 0,
        int outputTokens = 0,
        int reasoningTokens = 0)
    {
        try
        {
            return CreateSingleToolTurnResult(
                pendingCall,
                argumentsOverride,
                visibleText,
                hadReasoning,
                inputTokens,
                outputTokens,
                reasoningTokens);
        }
        catch (JsonException exception)
        {
            throw new LmStudioGenerationTerminatedException(
                "incomplete_tool_call",
                exception);
        }
    }

    private static LmChatResult CreateSingleToolTurnResult(
        PendingFunctionCall pendingCall,
        string? argumentsOverride,
        StringBuilder visibleText,
        bool hadReasoning,
        int inputTokens = 0,
        int outputTokens = 0,
        int reasoningTokens = 0)
    {
        var argumentsText = string.IsNullOrWhiteSpace(argumentsOverride)
            ? pendingCall.Arguments.ToString()
            : argumentsOverride;
        if (string.IsNullOrWhiteSpace(pendingCall.CallId)
            || string.IsNullOrWhiteSpace(pendingCall.Name)
            || string.IsNullOrWhiteSpace(argumentsText))
        {
            throw new JsonException("LM Studio returned an incomplete structured function call.");
        }

        using var argumentsDocument = JsonDocument.Parse(argumentsText);
        var call = new LmToolCall(
            pendingCall.CallId,
            pendingCall.Name,
            argumentsDocument.RootElement.Clone());
        return new LmChatResult(
            visibleText.Length == 0 ? null : visibleText.ToString(),
            [call],
            inputTokens,
            outputTokens,
            hadReasoning,
            reasoningTokens);
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int TryReadReasoningTokens(JsonElement root)
    {
        if (!root.TryGetProperty("response", out var response)
            || !response.TryGetProperty("usage", out var usage)
            || !usage.TryGetProperty("output_tokens_details", out var details))
        {
            return 0;
        }
        return TryReadInt32(details, "reasoning_tokens");
    }

    private static string ReadResponsesStreamError(JsonElement root)
    {
        if (ReadString(root, "message") is { Length: > 0 } direct)
        {
            return direct;
        }
        if (root.TryGetProperty("error", out var error)
            && ReadString(error, "message") is { Length: > 0 } nested)
        {
            return nested;
        }
        if (root.TryGetProperty("response", out var response)
            && response.TryGetProperty("error", out var responseError)
            && ReadString(responseError, "message") is { Length: > 0 } responseMessage)
        {
            return responseMessage;
        }
        return "LM Studio reported a Responses API stream failure.";
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

        if (_nativeAgentClient is not null)
        {
            return await _nativeAgentClient.AnalyzeImagesAsync(
                modelId,
                prompt,
                imagePaths,
                cancellationToken).ConfigureAwait(false);
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
    }

    private void EndModelOperation()
    {
        // Model residency is coordinated explicitly by WorkerOrchestrator. The
        // permanent General/STT/TTS set must never disappear because of idleness.
        _modelOperationGate.Release();
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
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
        _modelOperationGate.Dispose();
        _statusGate.Dispose();
    }

    private sealed class SdkOnlyHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                $"Der produktive LM-Studio-Client darf keinen HTTP-Endpunkt aufrufen: {request.RequestUri}");
    }

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

    [LoggerMessage(
        EventId = 3106,
        Level = LogLevel.Warning,
        Message = "LM Studio terminated a coding response stream ({providerCode}); retrying attempt {attempt} with the same loaded model {modelId}.")]
    private partial void LogResponsesStreamRetry(string providerCode, int attempt, string modelId);

    [LoggerMessage(
        EventId = 3107,
        Level = LogLevel.Warning,
        Message = "LM Studio terminated the coding Responses stream ({providerCode}); using Chat Completions recovery with the same loaded model {modelId}.")]
    private partial void LogResponsesChatCompletionsFallback(string providerCode, string modelId);

    [LoggerMessage(
        EventId = 3108,
        Level = LogLevel.Warning,
        Message = "LM Studio terminated the Chat Completions coding stream ({providerCode}); retrying attempt {attempt} with the same loaded model {modelId}.")]
    private partial void LogChatCompletionsStreamRetry(string providerCode, int attempt, string modelId);

    [LoggerMessage(
        EventId = 3109,
        Level = LogLevel.Warning,
        Message = "LM Studio atomic Chat Completions request failed transiently ({providerCode}); retrying attempt {attempt} with the same loaded model {modelId}.")]
    private partial void LogChatCompletionsAtomicRetry(string providerCode, int attempt, string modelId);

    [LoggerMessage(
        EventId = 3110,
        Level = LogLevel.Debug,
        Message = "LM Studio atomic tool turn uses {toolCount} tool schema(s); required tool: {requiredToolName}.")]
    private partial void LogAtomicToolSelection(int toolCount, string requiredToolName);

    [LoggerMessage(
        EventId = 3111,
        Level = LogLevel.Information,
        Message = "LM Studio selected coding tool {toolName} from {toolCount} candidates; generating arguments with its isolated schema.")]
    private partial void LogCodingToolSelected(string toolName, int toolCount);

    [LoggerMessage(
        EventId = 3112,
        Level = LogLevel.Warning,
        Message = "LM Studio coding tool router failed with {failureType}; continuing with safe recovery tool {toolName}.")]
    private partial void LogCodingToolRouterRecovery(string toolName, string failureType);

    [LoggerMessage(
        EventId = 3113,
        Level = LogLevel.Warning,
        Message = "LM Studio native coding action failed validation ({reason}); starting one schema-free host repair with model {modelId}.")]
    private partial void LogCodingToolRepairStarted(string reason, string modelId);

    [LoggerMessage(
        EventId = 3114,
        Level = LogLevel.Information,
        Message = "LM Studio coding action was recovered and validated on repair attempt {attempt} with model {modelId}.")]
    private partial void LogCodingToolRepairCompleted(int attempt, string modelId);

    private async Task<string> LoadModelWithRetryAsync(
        string modelId,
        object body,
        int expectedContextLength,
        bool isEmbedding,
        CancellationToken cancellationToken)
    {
        if (_nativeAgentClient is not null)
        {
            var preparation = await _nativeAgentClient.LoadModelAsync(
                modelId,
                expectedContextLength,
                isEmbedding,
                cancellationToken).ConfigureAwait(false);
            InvalidateStatusCache();
            return preparation.InstanceId;
        }

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
        if (_nativeAgentClient is not null)
        {
            return await _nativeAgentClient.GetModelsAsync(cancellationToken).ConfigureAwait(false);
        }

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

    private static int FindPrimarySystemMessageIndex(IReadOnlyList<LmChatMessage> messages)
    {
        for (var index = 0; index < messages.Count; index++)
        {
            if (string.Equals(messages[index].Role, "system", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(messages[index].Content))
            {
                return index;
            }
        }

        return -1;
    }

    private static object[] CreateResponsesInput(
        IReadOnlyList<LmChatMessage> messages,
        int primarySystemMessageIndex)
    {
        var input = new List<object>();
        for (var messageIndex = 0; messageIndex < messages.Count; messageIndex++)
        {
            var message = messages[messageIndex];
            if (messageIndex == primarySystemMessageIndex)
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

            if (string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase))
            {
                // LM Studio may fold every system-role input into the leading
                // instruction block. A runtime repair hint added after a tool
                // failure would then invalidate the entire prompt cache. Keep
                // the immutable first system message in `instructions` and
                // represent later server-authored guidance chronologically.
                input.Add(new
                {
                    role = "user",
                    content = "[GO_RUNTIME_GUIDANCE]\n" + (message.Content ?? string.Empty),
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
        string.Equals(modelId, _options.GeneralModelId, StringComparison.OrdinalIgnoreCase);

    private async Task UnloadModelInstancesAsync(
        IEnumerable<LmStudioModel> models,
        CancellationToken cancellationToken)
    {
        var materialized = models.ToArray();
        if (_nativeAgentClient is not null)
        {
            var identifiers = materialized
                .SelectMany(static model => model.LoadedInstances ?? [])
                .Select(static loaded => loaded.ModelInstanceId ?? loaded.Id)
                .Where(static instanceId => !string.IsNullOrWhiteSpace(instanceId))
                .Select(static instanceId => instanceId!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (identifiers.Length > 0)
            {
                _ = await _nativeAgentClient.UnloadModelsAsync(
                    identifiers,
                    unloadAll: false,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                InvalidateStatusCache();
            }
            return;
        }

        foreach (var model in materialized)
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
        var definitions = new List<(string Id, string Role, int ContextLength, string? DisplayName)>
        {
            (_options.GeneralModelId, "general", _options.GeneralContextLength, null),
            (_options.VisionModelId, "vision", 65_536, null),
            (_options.EmbeddingModelId, "embedding", 8_192, null),
        };
        definitions.AddRange(CodingModelCatalog.Models.Select(static profile =>
            (profile.Id, "code", profile.ContextLength, (string?)profile.DisplayName)));
        // One physical LM Studio model can intentionally serve more than one
        // logical role. Keep both descriptors so the settings page can offer
        // gpt-oss-120b independently as General AI and as Coding AI.
        definitions = definitions
            .DistinctBy(
                static definition => $"{definition.Id}\0{definition.Role}",
                StringComparer.OrdinalIgnoreCase)
            .ToList();
        var configured = definitions.Select(definition =>
        {
            var raw = rawModels.FirstOrDefault(model => string.Equals(model.Key, definition.Id, StringComparison.OrdinalIgnoreCase));
            var loaded = raw?.LoadedInstances is { Count: > 0 };
            return new ModelRuntimeStatus(
                definition.Id,
                definition.Role,
                raw is not null,
                loaded,
                raw is null ? "Fehlt" : loaded ? "Geladen" : "Bereit zum Laden",
                raw?.MaximumContextLength ?? definition.ContextLength,
                definition.DisplayName);
        }).ToList();
        foreach (var raw in rawModels.Where(raw => configured.All(item =>
                     !string.Equals(item.Id, raw.Key, StringComparison.OrdinalIgnoreCase))))
        {
            if (string.Equals(raw.Type, "llm", StringComparison.OrdinalIgnoreCase)
                && raw.Capabilities?.TrainedForToolUse == false)
            {
                continue;
            }

            var loaded = raw.LoadedInstances is { Count: > 0 };
            configured.Add(new ModelRuntimeStatus(
                raw.Key,
                string.Equals(raw.Type, "embedding", StringComparison.OrdinalIgnoreCase) ? "embedding" : "general",
                true,
                loaded,
                loaded ? "Geladen" : "Bereit zum Laden",
                raw.MaximumContextLength,
                raw.Key));
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
        var maximumAttempts = modelId.Contains("gpt-oss", StringComparison.OrdinalIgnoreCase)
            ? 4
            : CodingModelCatalog.TryGet(modelId, out _)
                ? 3
                : 2;
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

    private async Task<HttpResponseMessage> SendResponsesStreamWithRetryAsync(
        string modelId,
        object body,
        CancellationToken cancellationToken)
    {
        var maximumAttempts = modelId.Contains("gpt-oss", StringComparison.OrdinalIgnoreCase) ? 4 : 2;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            using var request = await CreateRequestAsync(
                HttpMethod.Post,
                "v1/responses",
                body,
                cancellationToken).ConfigureAwait(false);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
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

        throw new InvalidOperationException("LM Studio Responses streaming retry loop ended unexpectedly.");
    }

    private static bool IsTransientProviderStatus(System.Net.HttpStatusCode statusCode) => statusCode is
        System.Net.HttpStatusCode.InternalServerError or
        System.Net.HttpStatusCode.BadGateway or
        System.Net.HttpStatusCode.ServiceUnavailable or
        System.Net.HttpStatusCode.GatewayTimeout;

    private static bool IsResponsesCompatibilityFailure(string payload) =>
        payload.Contains("peg-native", StringComparison.OrdinalIgnoreCase)
        || payload.Contains("expected peg native", StringComparison.OrdinalIgnoreCase);

    private async Task<LmChatResult> CompleteCodingAgentRoundAsync(
        string modelId,
        IReadOnlyList<LmChatMessage> messages,
        IReadOnlyList<LmToolDefinition> tools,
        int maximumOutputTokens,
        string? reasoningEffort,
        bool requireToolCall,
        string? requiredToolName,
        Func<LmStudioNativeAgentProgress, CancellationToken, ValueTask>? nativeProgress,
        CancellationToken cancellationToken)
    {
        // Kept only for isolated unit tests that construct LmStudioClient
        // without the production SDK service. The application always registers
        // ILmStudioNativeAgentClient and never reaches this REST compatibility path.
        LmChatResult? nativeResult = null;
        string recoveryReason;
        try
        {
            // Professional coding agents keep tool selection and argument
            // generation in one prediction. The host persists the returned
            // assistant tool call, executes it, appends the tool result and only
            // then starts the next prediction. Splitting selection and arguments
            // into separate model calls loses that atomic intent and doubles the
            // LM Studio native tool-channel failure surface.
            nativeResult = await CompleteChatViaChatCompletionsAsync(
                modelId,
                messages,
                tools,
                maximumOutputTokens,
                modelRole: "code",
                reasoningEffort,
                requireToolCall,
                requiredToolName,
                cancellationToken).ConfigureAwait(false);
            if (TryNormalizeCodingAgentResult(
                    nativeResult,
                    tools,
                    requireToolCall,
                    requiredToolName,
                    out var normalized,
                    out recoveryReason))
            {
                return normalized;
            }
        }
        catch (Exception exception) when (
            !cancellationToken.IsCancellationRequested
            && IsRecoverableCodingToolFailure(exception))
        {
            recoveryReason = exception switch
            {
                LmStudioGenerationTerminatedException terminated => terminated.ProviderCode,
                JsonException => "invalid_provider_json",
                HttpRequestException http when http.StatusCode is { } statusCode => $"http_{(int)statusCode}",
                HttpRequestException => "transport_error",
                _ => exception.GetType().Name,
            };
        }

        LogCodingToolRepairStarted(recoveryReason, modelId);
        return await CompleteCodingAgentRepairAsync(
            modelId,
            messages,
            tools,
            maximumOutputTokens,
            requireToolCall,
            requiredToolName,
            nativeResult,
            recoveryReason,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<LmChatResult> CompleteCodingAgentRoundViaNativeSdkAsync(
        string modelId,
        IReadOnlyList<LmChatMessage> messages,
        IReadOnlyList<LmToolDefinition> tools,
        int maximumOutputTokens,
        string? reasoningEffort,
        bool requireToolCall,
        string? requiredToolName,
        Func<LmStudioNativeAgentProgress, CancellationToken, ValueTask>? nativeProgress,
        CancellationToken cancellationToken)
    {
        var sampling = CreateNativeSampling(
            modelId,
            modelRole: "code",
            reasoningEffort,
            maximumOutputTokens);
        IReadOnlyList<LmChatMessage> currentMessages = messages;
        LmChatResult? accumulatedUsage = null;
        var validationError = string.Empty;
        string? failedRawContent = null;
        var retryRequiredToolName = requiredToolName;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            LmChatResult result;
            try
            {
                result = await _nativeAgentClient!.CompleteAsync(
                    new LmStudioNativeAgentRequest(
                        modelId,
                        currentMessages,
                        tools,
                        maximumOutputTokens,
                        requireToolCall,
                        retryRequiredToolName,
                        sampling,
                        nativeProgress),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (LmStudioNativeAgentException exception) when (
                attempt < 2
                && !cancellationToken.IsCancellationRequested
                && IsRecoverableNativeAgentFailure(exception))
            {
                validationError = exception.Code;
                failedRawContent = exception.RawContent;
                if (string.IsNullOrWhiteSpace(retryRequiredToolName)
                    && !string.IsNullOrWhiteSpace(exception.ToolName)
                    && tools.Any(tool => string.Equals(
                        tool.Name,
                        exception.ToolName,
                        StringComparison.Ordinal)))
                {
                    // The first native pass already selected a valid tool name.
                    // Retry with only that schema so LM Studio can finish the
                    // interrupted argument object without re-routing the action.
                    retryRequiredToolName = exception.ToolName;
                }
                LogCodingToolRepairStarted(validationError, modelId);
                currentMessages = CreateNativeToolRetryMessages(
                    messages,
                    validationError,
                    failedRawContent);
                continue;
            }

            accumulatedUsage = accumulatedUsage is null
                ? result
                : CombineUsage(accumulatedUsage, result);
            if (TryNormalizeCodingAgentResult(
                    result,
                    tools,
                    requireToolCall,
                    requiredToolName,
                    out var normalized,
                    out validationError))
            {
                if (attempt > 1)
                {
                    LogCodingToolRepairCompleted(attempt, modelId);
                }
                return accumulatedUsage with
                {
                    Content = normalized.Content,
                    ToolCalls = normalized.ToolCalls,
                };
            }

            if (attempt < 2)
            {
                LogCodingToolRepairStarted(validationError, modelId);
                currentMessages = CreateNativeToolRetryMessages(
                    messages,
                    validationError,
                    failedRawContent: null);
            }
        }

        throw new InvalidDataException(
            $"LM Studio returned no valid native coding action after one native retry: {validationError}");
    }

    private static bool IsRecoverableNativeAgentFailure(LmStudioNativeAgentException exception) =>
        exception.Code is
            "tool_generation_failed" or
            "tool_generation_timeout" or
            "prediction_failed" or
            "native_sdk_error" or
            "sidecar_exited" or
            "sidecar_protocol_error";

    private async Task<LmChatResult> CompleteModelRoundViaNativeSdkAsync(
        string modelId,
        IReadOnlyList<LmChatMessage> messages,
        IReadOnlyList<LmToolDefinition> tools,
        int maximumOutputTokens,
        string modelRole,
        string? reasoningEffort,
        bool requireToolCall,
        string? requiredToolName,
        Func<LmStudioNativeAgentProgress, CancellationToken, ValueTask>? nativeProgress,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<LmChatMessage> currentMessages = messages;
        LmChatResult? accumulatedUsage = null;
        var validationError = string.Empty;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            LmChatResult result;
            try
            {
                result = await _nativeAgentClient!.CompleteAsync(
                    new LmStudioNativeAgentRequest(
                        modelId,
                        currentMessages,
                        tools,
                        maximumOutputTokens,
                        requireToolCall,
                        requiredToolName,
                        CreateNativeSampling(modelId, modelRole, reasoningEffort, maximumOutputTokens),
                        nativeProgress),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (LmStudioNativeAgentException exception) when (
                attempt < 2
                && tools.Count == 1
                && !cancellationToken.IsCancellationRequested
                && IsRecoverableNativeAgentFailure(exception))
            {
                validationError = exception.Code;
                currentMessages = CreateNativeToolRetryMessages(
                    messages,
                    validationError,
                    exception.RawContent);
                continue;
            }

            accumulatedUsage = accumulatedUsage is null
                ? result
                : CombineUsage(accumulatedUsage, result);
            if (TryNormalizeCodingAgentResult(
                    result,
                    tools,
                    requireToolCall,
                    requiredToolName,
                    out var normalized,
                    out validationError))
            {
                return accumulatedUsage with
                {
                    Content = normalized.Content,
                    ToolCalls = normalized.ToolCalls,
                };
            }
            if (attempt < 2 && tools.Count == 1)
            {
                currentMessages = CreateNativeToolRetryMessages(
                    messages,
                    validationError,
                    failedRawContent: null);
                continue;
            }
            break;
        }

        throw new InvalidDataException(
            $"LM Studio returned no valid native SDK model action: {validationError}");
    }

    private static IReadOnlyList<LmChatMessage> CreateNativeToolRetryMessages(
        IReadOnlyList<LmChatMessage> messages,
        string reason,
        string? failedRawContent)
    {
        const int maximumRawCharacters = 4_000;
        var boundedRaw = string.IsNullOrWhiteSpace(failedRawContent)
            ? string.Empty
            : failedRawContent.Length <= maximumRawCharacters
                ? failedRawContent
                : failedRawContent[..maximumRawCharacters];
        var guidance = new StringBuilder()
            .AppendLine("[GO_NATIVE_TOOL_RETRY]")
            .AppendLine("Der vorige native LM-Studio-Toolaufruf war unvollstaendig oder ungueltig.")
            .AppendLine("Erzeuge ueber die erneut bereitgestellten nativen Tools genau eine vollstaendige Aktion.")
            .AppendLine("Nutze den exakten Toolnamen und ein vollstaendiges JSON-Argumentobjekt. Kein Ersatzformat und kein Toolaufruf als Fliesstext.")
            .Append("Diagnose: ")
            .AppendLine(reason);
        if (boundedRaw.Length > 0)
        {
            guidance.AppendLine("Unvollstaendige Modellausgabe zur Korrektur:")
                .AppendLine(boundedRaw);
        }
        return [.. messages, new LmChatMessage("user", guidance.ToString())];
    }

    private static LmStudioNativeSampling CreateNativeSampling(
        string modelId,
        string modelRole,
        string? reasoningEffort,
        int maximumOutputTokens)
    {
        var profile = new Dictionary<string, object?>(StringComparer.Ordinal);
        ApplySamplingProfile(
            profile,
            modelId,
            includeReasoning: false,
            modelRole,
            reasoningEffort,
            maximumOutputTokens);
        return new LmStudioNativeSampling(
            ReadDouble(profile, "temperature"),
            ReadDouble(profile, "top_p"),
            ReadInt32(profile, "top_k"),
            ReadDouble(profile, "min_p"),
            ReadDouble(profile, "repetition_penalty"));
    }

    private static double? ReadDouble(Dictionary<string, object?> values, string key) =>
        values.TryGetValue(key, out var value) && value is IConvertible convertible
            ? convertible.ToDouble(System.Globalization.CultureInfo.InvariantCulture)
            : null;

    private static int? ReadInt32(Dictionary<string, object?> values, string key) =>
        values.TryGetValue(key, out var value) && value is IConvertible convertible
            ? convertible.ToInt32(System.Globalization.CultureInfo.InvariantCulture)
            : null;

    private async Task<LmChatResult> CompleteCodingAgentRepairAsync(
        string modelId,
        IReadOnlyList<LmChatMessage> messages,
        IReadOnlyList<LmToolDefinition> tools,
        int maximumOutputTokens,
        bool requireToolCall,
        string? requiredToolName,
        LmChatResult? nativeResult,
        string initialReason,
        CancellationToken cancellationToken)
    {
        var instruction = CreateCodingRepairInstruction(tools, requireToolCall, requiredToolName);
        var state = CreateCodingToolArgumentState(messages)
            + "\n\n[GO_TOOL_REPAIR_REASON]\nDie vorherigen Argumente waren ungültig: "
            + initialReason;
        var baseMessages = new[]
        {
            new LmChatMessage("system", instruction),
            new LmChatMessage("user", state),
        };
        LmChatResult? accumulatedUsage = nativeResult;
        var validationError = initialReason;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            LmChatMessage[] currentMessages;
            if (attempt == 1)
            {
                currentMessages = baseMessages;
            }
            else
            {
                currentMessages =
                [
                    .. baseMessages,
                    new LmChatMessage(
                        "user",
                        "Die vorherige Aktion war ungültig: " + validationError + "\n"
                        + "Korrigiere ausschließlich das JSON-Objekt. Ändere weder Auftrag noch Workspacezustand."),
                ];
            }

            var body = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["model"] = modelId,
                ["messages"] = CreateFlattenedCodingChatMessages(currentMessages),
                ["stream"] = false,
                ["max_tokens"] = Math.Clamp(Math.Min(maximumOutputTokens, 16_384), 256, 16_384),
                ["temperature"] = 0.0,
                ["top_p"] = 1.0,
            };

            LmChatResult repair;
            try
            {
                // Deliberately omit the native `tools` field. This path is a
                // bounded tool-fixer for providers whose native argument parser
                // has already terminated. It is never used for normal routing.
                repair = await CompletePlainCodingChatAsync(
                    modelId,
                    body,
                    "tool_repair",
                    TimeSpan.FromMinutes(5),
                    cancellationToken,
                    maximumAttempts: 1).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                attempt < 2
                && !cancellationToken.IsCancellationRequested
                && IsRecoverableCodingToolFailure(exception))
            {
                validationError = exception is LmStudioGenerationTerminatedException terminated
                    ? terminated.ProviderCode
                    : exception.GetType().Name;
                continue;
            }

            accumulatedUsage = accumulatedUsage is null
                ? repair
                : CombineUsage(accumulatedUsage, repair);
            if (repair.ToolCalls.Count > 0
                && TryNormalizeCodingAgentResult(
                    repair,
                    tools,
                    requireToolCall,
                    requiredToolName,
                    out var nativeRepair,
                    out validationError))
            {
                LogCodingToolRepairCompleted(attempt, modelId);
                return accumulatedUsage with
                {
                    Content = nativeRepair.Content,
                    ToolCalls = nativeRepair.ToolCalls,
                };
            }
            if (TryParseCodingRepairEnvelope(
                    repair.Content,
                    tools,
                    requireToolCall,
                    requiredToolName,
                    out var repairedResult,
                    out validationError))
            {
                LogCodingToolRepairCompleted(attempt, modelId);
                return accumulatedUsage with
                {
                    Content = repairedResult.Content,
                    ToolCalls = repairedResult.ToolCalls,
                };
            }
        }

        throw new InvalidDataException(
            $"LM Studio returned no valid coding action after one host-side correction: {validationError}");
    }

    private static bool IsRecoverableCodingToolFailure(Exception exception) => exception is
        LmStudioGenerationTerminatedException or
        JsonException or
        InvalidDataException
        || exception is HttpRequestException http
            && (http.StatusCode is null || IsTransientProviderStatus(http.StatusCode.Value));

    private static bool TryNormalizeCodingAgentResult(
        LmChatResult result,
        IReadOnlyList<LmToolDefinition> tools,
        bool requireToolCall,
        string? requiredToolName,
        out LmChatResult normalized,
        out string error)
    {
        normalized = result;
        error = string.Empty;
        if (result.ToolCalls.Count > 1)
        {
            error = "parallel tool calls are disabled; exactly one call is allowed per coding round";
            return false;
        }
        if (result.ToolCalls.Count == 1)
        {
            var call = result.ToolCalls[0];
            var tool = tools.FirstOrDefault(candidate => string.Equals(candidate.Name, call.Name, StringComparison.Ordinal));
            if (tool is null)
            {
                error = $"unknown tool '{call.Name}'";
                return false;
            }
            if (!string.IsNullOrWhiteSpace(requiredToolName)
                && !string.Equals(requiredToolName, call.Name, StringComparison.Ordinal))
            {
                error = $"expected required tool '{requiredToolName}', received '{call.Name}'";
                return false;
            }
            if (!TryValidateJsonAgainstSchema(call.Arguments, tool.Parameters, out error))
            {
                return false;
            }

            normalized = result with
            {
                Content = null,
                ToolCalls =
                [
                    call with
                    {
                        Id = string.IsNullOrWhiteSpace(call.Id) ? $"call_{Guid.NewGuid():N}" : call.Id,
                    },
                ],
            };
            return true;
        }

        if (requireToolCall)
        {
            // Some OpenAI-compatible providers return a plain argument object
            // despite a single required native schema. Accept it only when the
            // schema makes the target unambiguous.
            if (tools.Count == 1
                && TryExtractJsonObject(result.Content, out var arguments, out _)
                && TryValidateJsonAgainstSchema(arguments, tools[0].Parameters, out error))
            {
                normalized = result with
                {
                    Content = null,
                    ToolCalls = [new LmToolCall($"call_{Guid.NewGuid():N}", tools[0].Name, arguments)],
                };
                return true;
            }

            error = "a tool call is required, but the provider returned no valid tool call";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(result.Content))
        {
            normalized = result with { ToolCalls = [] };
            return true;
        }

        error = "the provider returned neither a tool call nor a final answer";
        return false;
    }

    private static bool TryParseCodingRepairEnvelope(
        string? content,
        IReadOnlyList<LmToolDefinition> tools,
        bool requireToolCall,
        string? requiredToolName,
        out LmChatResult result,
        out string error)
    {
        result = new LmChatResult(null, [], 0, 0);
        if (!TryExtractJsonObject(content, out var envelope, out error))
        {
            return false;
        }

        var kind = envelope.TryGetProperty("kind", out var kindElement)
            && kindElement.ValueKind == JsonValueKind.String
                ? kindElement.GetString()
                : null;
        if (string.Equals(kind, "final", StringComparison.Ordinal))
        {
            if (requireToolCall)
            {
                error = "a final answer is not allowed in this required-tool round";
                return false;
            }
            var finalContent = envelope.TryGetProperty("content", out var contentElement)
                && contentElement.ValueKind == JsonValueKind.String
                    ? contentElement.GetString()
                    : null;
            if (string.IsNullOrWhiteSpace(finalContent))
            {
                error = "the final action contains no content";
                return false;
            }
            result = new LmChatResult(finalContent, [], 0, 0);
            error = string.Empty;
            return true;
        }

        var name = envelope.TryGetProperty("name", out var nameElement)
            && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;
        if (!string.Equals(kind, "tool", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(name))
        {
            // A one-tool repair may return the raw argument object. This remains
            // deterministic because no other operation can match the request.
            if (requireToolCall
                && tools.Count == 1
                && TryValidateJsonAgainstSchema(envelope, tools[0].Parameters, out error))
            {
                result = new LmChatResult(
                    null,
                    [new LmToolCall($"call_{Guid.NewGuid():N}", tools[0].Name, envelope)],
                    0,
                    0);
                return true;
            }

            error = "the repair response must use kind 'tool' or 'final'";
            return false;
        }

        var tool = tools.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
        if (tool is null)
        {
            error = $"unknown tool '{name}'";
            return false;
        }
        if (!string.IsNullOrWhiteSpace(requiredToolName)
            && !string.Equals(requiredToolName, name, StringComparison.Ordinal))
        {
            error = $"expected required tool '{requiredToolName}', received '{name}'";
            return false;
        }
        if (!envelope.TryGetProperty("arguments", out var arguments)
            || arguments.ValueKind != JsonValueKind.Object)
        {
            error = "the tool action contains no arguments object";
            return false;
        }
        if (!TryValidateJsonAgainstSchema(arguments, tool.Parameters, out error))
        {
            return false;
        }

        result = new LmChatResult(
            null,
            [new LmToolCall($"call_{Guid.NewGuid():N}", tool.Name, arguments.Clone())],
            0,
            0);
        error = string.Empty;
        return true;
    }

    private static string CreateCodingRepairInstruction(
        IReadOnlyList<LmToolDefinition> tools,
        bool requireToolCall,
        string? requiredToolName)
    {
        var catalog = new StringBuilder();
        foreach (var tool in tools)
        {
            var entry = $"\nTOOL {tool.Name}\nPurpose: {tool.Description}\nSchema: {tool.Parameters.GetRawText()}\n";
            if (catalog.Length + entry.Length <= MaximumCodingRecoverySchemaCharacters)
            {
                catalog.Append(entry);
            }
            else
            {
                catalog.Append("\nTOOL ").Append(tool.Name).Append("\nPurpose: ").Append(tool.Description)
                    .Append("\nSchema: omitted from the compact first repair pass\n");
            }
        }

        return $$$"""
            You repair exactly one interrupted coding-agent action. Do not solve the task in prose and do not emit Markdown.
            Return exactly one JSON object in one of these forms:
            {"kind":"tool","name":"allowed.tool","arguments":{}}
            {"kind":"final","content":"concise final answer"}
            Tool names must match the catalog exactly. Arguments must match that tool's JSON schema. Never invent a tool.
            {{{(requireToolCall ? "A tool action is mandatory; kind 'final' is forbidden." : "Use kind 'final' only when no further workspace action is needed.")}}}
            {{{(!string.IsNullOrWhiteSpace(requiredToolName) ? $"The only permitted tool is '{requiredToolName}'." : string.Empty)}}}
            AVAILABLE TOOLS:
            {{{catalog}}}
            """;
    }

    private async Task<(string Name, LmChatResult Usage)> SelectCodingToolNameAsync(
        string modelId,
        IReadOnlyList<LmChatMessage> messages,
        IReadOnlyList<LmToolDefinition> tools,
        bool requireToolCall,
        CancellationToken cancellationToken)
    {
        // Tool routing uses a tiny text-only JSON response instead of LM Studio's
        // native function-call or structured-output channel. Historical function
        // calls are flattened to text as well: sending
        // assistant.tool_calls or tool-role messages without the matching
        // current schemas makes LM Studio enter its native tool channel during
        // this supposedly tool-free phase. Responses requests also remained in
        // LM Studio's generation channel after the short tool name had already
        // been produced. A bounded text-only request avoids both native tool
        // parsing and an unterminated Responses item. A second normal chat
        // response receives exactly one native schema in an otherwise isolated
        // two-message request; GO still parses and validates the returned JSON
        // locally before any operation can run.
        var selectionMessages = CreateCodingToolSelectionMessages(
            messages,
            tools,
            allowFinalAnswer: !requireToolCall);
        LmChatResult? accumulatedUsage = null;
        string? lastContent = null;
        Exception? lastProviderFailure = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var currentMessages = attempt == 1
                ? selectionMessages
                : CreateCodingToolSelectionRecoveryMessages(
                    selectionMessages,
                    messages,
                    providerFailure: lastProviderFailure is not null);
            LmChatResult selection;
            try
            {
                selection = await CompleteCodingToolSelectionViaChatCompletionsAsync(
                    modelId,
                    currentMessages,
                    tools,
                    allowFinalAnswer: !requireToolCall,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                !cancellationToken.IsCancellationRequested
                && IsRecoverableCodingRouterFailure(exception))
            {
                lastProviderFailure = exception;
                continue;
            }
            lastContent = selection.Content;
            accumulatedUsage = accumulatedUsage is null
                ? selection
                : CombineUsage(accumulatedUsage, selection);
            if (TryParseCodingToolSelection(lastContent, tools, !requireToolCall, out var selectedName))
            {
                return (selectedName, accumulatedUsage);
            }
        }

        if (lastProviderFailure is not null
            && TrySelectSafeCodingToolAfterRouterFailure(messages, tools, out var recoveryTool))
        {
            LogCodingToolRouterRecovery(recoveryTool, lastProviderFailure.GetType().Name);
            return (
                recoveryTool,
                accumulatedUsage ?? new LmChatResult(null, [], 0, 0));
        }

        var selectionHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(lastContent ?? string.Empty)));
        throw new InvalidOperationException(
            $"LM Studio returned an ambiguous coding tool selection (length {lastContent?.Length ?? 0}, sha256 {selectionHash}).",
            lastProviderFailure);
    }

    private async Task<LmChatResult> CompleteCodingToolSelectionViaChatCompletionsAsync(
        string modelId,
        LmChatMessage[] messages,
        IReadOnlyList<LmToolDefinition> tools,
        bool allowFinalAnswer,
        CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = modelId,
            ["messages"] = CreateFlattenedCodingChatMessages(messages),
            ["stream"] = false,
            ["max_tokens"] = 8,
            ["temperature"] = 0.0,
            ["top_p"] = 1.0,
        };

        return await CompletePlainCodingChatAsync(
            modelId,
            body,
            "tool_selection",
            TimeSpan.FromSeconds(45),
            cancellationToken,
            maximumAttempts: 1).ConfigureAwait(false);
    }

    private async Task<LmChatResult> CompleteCodingToolArgumentsViaChatCompletionsAsync(
        string modelId,
        IReadOnlyList<LmChatMessage> messages,
        LmToolDefinition tool,
        int maximumOutputTokens,
        CancellationToken cancellationToken)
    {
        var argumentInstruction = $$"""
            Interner Coding-Aufruf:
            Die ausgewählte Operation ist {{tool.Name}}.
            Zweck: {{tool.Description}}
            Rufe ausschließlich die eine bereitgestellte Funktion mit den zum Kontext passenden Argumenten auf.
            Erzeuge keine zweite Funktion, kein Markdown und keine Erklärung.
            Das native Funktionsschema ist verbindlich.
            """;
        var argumentMessages = new[]
        {
            new LmChatMessage("system", argumentInstruction),
            new LmChatMessage("user", CreateCodingToolArgumentState(messages)),
        };
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = modelId,
            ["messages"] = CreateFlattenedCodingChatMessages(argumentMessages),
            ["stream"] = false,
            ["max_tokens"] = ResolveCodingToolArgumentTokenLimit(tool.Name, maximumOutputTokens),
            ["temperature"] = 0.0,
            ["top_p"] = 1.0,
            // The router has already selected exactly one operation. Sending
            // only that schema gives LM Studio's Qwen template a bounded native
            // function-call channel instead of asking it to infer a function
            // envelope from prose. The full agent policy and historical tool
            // roles remain deliberately absent from this isolated request.
            ["tools"] = new[]
            {
                new
                {
                    type = "function",
                    function = new
                    {
                        name = tool.Name,
                        description = tool.Description,
                        parameters = tool.Parameters,
                    },
                },
            },
            ["tool_choice"] = "required",
            ["parallel_tool_calls"] = false,
        };

        LmChatResult? accumulatedUsage = null;
        string lastValidationError = "empty response";
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            if (attempt > 1)
            {
                var correction = new LmChatMessage(
                    "user",
                    $"Die vorherigen Argumente waren ungültig ({lastValidationError}). "
                    + "Gib jetzt ausschließlich ein einziges JSON-Objekt zurück, das exakt dem angegebenen Schema entspricht.");
                body["messages"] = CreateFlattenedCodingChatMessages([.. argumentMessages, correction]);
            }

            var completion = await CompletePlainCodingChatAsync(
                modelId,
                body,
                "tool_arguments",
                TimeSpan.FromMinutes(5),
                cancellationToken).ConfigureAwait(false);
            accumulatedUsage = accumulatedUsage is null
                ? completion
                : CombineUsage(accumulatedUsage, completion);
            if (completion.ToolCalls.Count == 1)
            {
                var nativeCall = completion.ToolCalls[0];
                if (!string.Equals(nativeCall.Name, tool.Name, StringComparison.Ordinal))
                {
                    lastValidationError = $"unexpected tool name {nativeCall.Name}";
                    continue;
                }
                if (!TryValidateJsonAgainstSchema(nativeCall.Arguments, tool.Parameters, out var nativeValidationError))
                {
                    lastValidationError = nativeValidationError;
                    continue;
                }

                return accumulatedUsage with
                {
                    Content = null,
                    ToolCalls = [nativeCall],
                };
            }
            if (completion.ToolCalls.Count > 1 || string.IsNullOrWhiteSpace(completion.Content))
            {
                lastValidationError = "no JSON content";
                continue;
            }
            if (!TryExtractJsonObject(completion.Content, out var arguments, out var parseError))
            {
                lastValidationError = parseError;
                continue;
            }
            if (!TryValidateJsonAgainstSchema(arguments, tool.Parameters, out var validationError))
            {
                lastValidationError = validationError;
                continue;
            }

            var call = new LmToolCall(
                $"call_{Guid.NewGuid():N}",
                tool.Name,
                arguments);
            return accumulatedUsage with
            {
                Content = null,
                ToolCalls = [call],
            };
        }

        throw new InvalidDataException(
            $"LM Studio returned invalid arguments for coding tool '{tool.Name}' after correction: {lastValidationError}");
    }

    private static int ResolveCodingToolArgumentTokenLimit(string toolName, int maximumOutputTokens)
    {
        var preferredLimit = toolName is
            "fs.writeText" or
            "fs.replaceText" or
            "fs.proposePatch" or
            "fs.proposeCreate"
            ? 8_192
            : toolName is "process.run" or "proof.lean"
                ? 2_048
                : 1_024;
        return Math.Clamp(Math.Min(maximumOutputTokens, preferredLimit), 64, 16_384);
    }

    private static bool TryExtractJsonObject(
        string? content,
        out JsonElement value,
        out string error)
    {
        value = default;
        error = "empty response";
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        for (var start = content.IndexOf('{'); start >= 0; start = content.IndexOf('{', start + 1))
        {
            var depth = 0;
            var inString = false;
            var escaped = false;
            for (var index = start; index < content.Length; index++)
            {
                var current = content[index];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (current == '\\')
                    {
                        escaped = true;
                    }
                    else if (current == '"')
                    {
                        inString = false;
                    }
                    continue;
                }

                if (current == '"')
                {
                    inString = true;
                    continue;
                }
                if (current == '{')
                {
                    depth++;
                    continue;
                }
                if (current != '}')
                {
                    continue;
                }

                depth--;
                if (depth != 0)
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(content[start..(index + 1)]);
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        break;
                    }
                    value = document.RootElement.Clone();
                    error = string.Empty;
                    return true;
                }
                catch (JsonException exception)
                {
                    error = exception.Message;
                    break;
                }
            }
        }

        return false;
    }

    private static bool TryValidateJsonAgainstSchema(
        JsonElement value,
        JsonElement schema,
        out string error) =>
        TryValidateJsonAgainstSchema(value, schema, "$", out error);

    private static bool TryValidateJsonAgainstSchema(
        JsonElement value,
        JsonElement schema,
        string path,
        out string error)
    {
        error = string.Empty;
        var expectedType = schema.TryGetProperty("type", out var typeElement)
            && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString()
                : null;
        if (!MatchesJsonType(value, expectedType))
        {
            error = $"{path} must be {expectedType ?? "a valid JSON value"}.";
            return false;
        }

        if (schema.TryGetProperty("enum", out var enumElement)
            && enumElement.ValueKind == JsonValueKind.Array
            && !enumElement.EnumerateArray().Any(candidate => JsonElement.DeepEquals(candidate, value)))
        {
            error = $"{path} is not one of the allowed values.";
            return false;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = schema.TryGetProperty("properties", out var propertyElement)
                && propertyElement.ValueKind == JsonValueKind.Object
                    ? propertyElement
                    : default;
            if (schema.TryGetProperty("required", out var requiredElement)
                && requiredElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var required in requiredElement.EnumerateArray())
                {
                    var name = required.GetString();
                    if (!string.IsNullOrWhiteSpace(name) && !value.TryGetProperty(name, out _))
                    {
                        error = $"{path} is missing required property '{name}'.";
                        return false;
                    }
                }
            }

            var rejectUnknown = schema.TryGetProperty("additionalProperties", out var additionalElement)
                && additionalElement.ValueKind == JsonValueKind.False;
            foreach (var property in value.EnumerateObject())
            {
                if (properties.ValueKind != JsonValueKind.Object
                    || !properties.TryGetProperty(property.Name, out var propertySchema))
                {
                    if (rejectUnknown)
                    {
                        error = $"{path} contains unknown property '{property.Name}'.";
                        return false;
                    }
                    continue;
                }
                if (!TryValidateJsonAgainstSchema(
                        property.Value,
                        propertySchema,
                        $"{path}.{property.Name}",
                        out error))
                {
                    return false;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            if (schema.TryGetProperty("minItems", out var minItems)
                && minItems.TryGetInt32(out var minimum)
                && value.GetArrayLength() < minimum)
            {
                error = $"{path} contains fewer than {minimum} items.";
                return false;
            }
            if (schema.TryGetProperty("maxItems", out var maxItems)
                && maxItems.TryGetInt32(out var maximum)
                && value.GetArrayLength() > maximum)
            {
                error = $"{path} contains more than {maximum} items.";
                return false;
            }
            if (schema.TryGetProperty("items", out var itemSchema))
            {
                var itemIndex = 0;
                foreach (var item in value.EnumerateArray())
                {
                    if (!TryValidateJsonAgainstSchema(item, itemSchema, $"{path}[{itemIndex}]", out error))
                    {
                        return false;
                    }
                    itemIndex++;
                }
            }
        }

        return true;
    }

    private static bool MatchesJsonType(JsonElement value, string? expectedType) => expectedType switch
    {
        null => true,
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true,
    };

    private async Task<LmChatResult> CompletePlainCodingChatAsync(
        string modelId,
        Dictionary<string, object?> body,
        string operation,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        int maximumAttempts = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumAttempts, 3);

        Exception? lastException = null;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCancellation.CancelAfter(timeout);
            try
            {
                using var request = await CreateRequestAsync(
                    HttpMethod.Post,
                    "v1/chat/completions",
                    body,
                    attemptCancellation.Token).ConfigureAwait(false);
                using var response = await _httpClient.SendAsync(
                    request,
                    attemptCancellation.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var errorPayload = await response.Content.ReadAsStringAsync(
                        attemptCancellation.Token).ConfigureAwait(false);
                    var transientFailure = IsLmStudioChannelTermination(errorPayload)
                        || IsTransientProviderStatus(response.StatusCode);
                    if (attempt < maximumAttempts && transientFailure)
                    {
                        LogChatCompletionsAtomicRetry($"{operation}_terminated", attempt + 1, modelId);
                        await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    throw new HttpRequestException(
                        $"LM Studio structured coding operation '{operation}' returned HTTP {(int)response.StatusCode}.",
                        inner: null,
                        response.StatusCode);
                }

                using var document = JsonDocument.Parse(
                    await response.Content.ReadAsStreamAsync(attemptCancellation.Token).ConfigureAwait(false));
                return ParseAtomicChatCompletion(document.RootElement);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                lastException = new TimeoutException(
                    $"LM Studio did not finish structured coding operation '{operation}' within {timeout.TotalSeconds:0} seconds.",
                    exception);
                if (attempt < maximumAttempts)
                {
                    LogChatCompletionsAtomicRetry($"{operation}_timeout", attempt + 1, modelId);
                    continue;
                }
            }
            catch (HttpRequestException exception) when (
                attempt < maximumAttempts
                && !cancellationToken.IsCancellationRequested
                && (exception.StatusCode is null || IsTransientProviderStatus(exception.StatusCode.Value)))
            {
                lastException = exception;
                LogChatCompletionsAtomicRetry($"{operation}_transport", attempt + 1, modelId);
                await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException exception) when (
                attempt < maximumAttempts
                && !cancellationToken.IsCancellationRequested)
            {
                lastException = exception;
                LogChatCompletionsAtomicRetry($"{operation}_invalid_json", attempt + 1, modelId);
                await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        throw lastException
            ?? new TimeoutException($"LM Studio did not finish structured coding operation '{operation}'.");
    }

    private static object[] CreateFlattenedCodingInput(
        LmChatMessage[] messages,
        int primarySystemMessageIndex)
    {
        var toolNamesByCallId = messages
            .SelectMany(static message => message.ToolCalls ?? [])
            .GroupBy(static call => call.Id, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Last().Name, StringComparer.Ordinal);
        var input = new List<object>(messages.Length);
        for (var messageIndex = 0; messageIndex < messages.Length; messageIndex++)
        {
            var message = messages[messageIndex];
            if (messageIndex == primarySystemMessageIndex)
            {
                continue;
            }

            if (message.ToolCalls is { Count: > 0 })
            {
                var completedCalls = string.Join(
                    ", ",
                    message.ToolCalls.Select(static call => call.Name).Distinct(StringComparer.Ordinal));
                input.Add(new
                {
                    role = "assistant",
                    content = string.Join(
                        Environment.NewLine,
                        new[]
                        {
                            message.Content,
                            $"[GO_PREVIOUS_TOOL_CALLS] {completedCalls}",
                        }.Where(static value => !string.IsNullOrWhiteSpace(value))),
                });
                continue;
            }

            if (!string.IsNullOrWhiteSpace(message.ToolCallId))
            {
                toolNamesByCallId.TryGetValue(message.ToolCallId, out var toolName);
                input.Add(new
                {
                    role = "user",
                    content = $"[GO_PREVIOUS_TOOL_RESULT tool={toolName ?? "unknown"}]\n{message.Content ?? string.Empty}",
                });
                continue;
            }

            if (string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase))
            {
                input.Add(new
                {
                    role = "user",
                    content = "[GO_RUNTIME_GUIDANCE]\n" + (message.Content ?? string.Empty),
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

    private static object[] CreateFlattenedCodingChatMessages(LmChatMessage[] messages)
    {
        var primarySystemMessageIndex = FindPrimarySystemMessageIndex(messages);
        var result = new List<object>(messages.Length + 1);
        if (primarySystemMessageIndex >= 0)
        {
            result.Add(new
            {
                role = "system",
                content = messages[primarySystemMessageIndex].Content ?? string.Empty,
            });
        }

        result.AddRange(CreateFlattenedCodingInput(messages, primarySystemMessageIndex));
        return result.ToArray();
    }

    private static string? ReadResponsesText(JsonElement root)
    {
        if (root.TryGetProperty("output_text", out var directText)
            && directText.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(directText.GetString()))
        {
            return directText.GetString();
        }
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var text = new StringBuilder();
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var partText)
                    && partText.ValueKind == JsonValueKind.String)
                {
                    text.Append(partText.GetString());
                }
                else if (part.TryGetProperty("output_text", out var outputText)
                    && outputText.ValueKind == JsonValueKind.String)
                {
                    text.Append(outputText.GetString());
                }
            }
        }

        return text.Length == 0 ? null : text.ToString();
    }

    private static LmChatResult CombineUsage(LmChatResult first, LmChatResult second) => second with
    {
        InputTokens = checked(first.InputTokens + second.InputTokens),
        OutputTokens = checked(first.OutputTokens + second.OutputTokens),
        HadReasoning = first.HadReasoning || second.HadReasoning,
        ReasoningTokens = checked(first.ReasoningTokens + second.ReasoningTokens),
    };

    private static LmChatMessage[] CreateCodingToolSelectionMessages(
        IReadOnlyList<LmChatMessage> messages,
        IReadOnlyList<LmToolDefinition> tools,
        bool allowFinalAnswer)
    {
        var descriptions = string.Join(
            Environment.NewLine,
            tools.Select(static (tool, index) => $"{index + 1}: {tool.Name} - {tool.Description}"));
        var finalChoice = tools.Count + 1;

        var routingInstruction = $$"""
            Interne Klassifikationsphase für den nächsten Coding-Schritt:
            Wähle genau eine Kennziffer aus der folgenden Zuordnung.
            Erzeuge noch keine Argumente, keinen Funktionsaufruf, kein JSON, keine Erklärung und keinen Reasoningtext.
            Antworte ausschließlich mit der dezimalen Kennziffer, beispielsweise 2.
            {{descriptions}}
            {{(allowFinalAnswer ? $"{finalChoice}: Die Aufgabe ist abgeschlossen; es ist keine weitere Aktion nötig." : "Eine Abschlussauswahl ist in dieser Phase nicht zulässig.")}}
            """;
        return
        [
            new LmChatMessage("system", routingInstruction),
            new LmChatMessage("user", CreateCompactCodingRouterState(messages)),
        ];
    }

    private static LmChatMessage[] CreateCodingToolSelectionRecoveryMessages(
        LmChatMessage[] selectionMessages,
        IReadOnlyList<LmChatMessage> sourceMessages,
        bool providerFailure)
    {
        var latestUserRequest = sourceMessages
            .LastOrDefault(static message =>
                string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(message.ToolCallId))?
            .Content
            ?? "Kein separater Nutzerauftrag vorhanden.";
        return
        [
            selectionMessages[0],
            new LmChatMessage(
                "user",
                "[GO_CURRENT_USER_REQUEST]\n"
                + TruncateCodingRouterText(latestUserRequest, MaximumCodingRouterUserCharacters)
                + "\n\n[GO_ROUTER_RECOVERY]\n"
                + (providerFailure
                    ? "Die vorherige Klassifikationsanfrage wurde vom Provider technisch beendet. "
                    : "Die vorherige Werkzeugauswahl war nicht eindeutig. ")
                + "Wähle ohne Wiederholung der Analyse ausschließlich die Kennziffer des nächsten sicheren Workspace-Schritts."),
        ];
    }

    private static bool IsRecoverableCodingRouterFailure(Exception exception) => exception is
        TimeoutException or
        HttpRequestException or
        LmStudioGenerationTerminatedException;

    private static bool TrySelectSafeCodingToolAfterRouterFailure(
        IReadOnlyList<LmChatMessage> messages,
        IReadOnlyList<LmToolDefinition> tools,
        out string toolName)
    {
        toolName = string.Empty;
        if (tools.Count == 1)
        {
            toolName = tools[0].Name;
            return true;
        }

        var previouslyRequested = messages
            .SelectMany(static message => message.ToolCalls ?? [])
            .Select(static call => call.Name)
            .ToHashSet(StringComparer.Ordinal);
        string[] safeRecoveryOrder =
        [
            ClientToolNames.WorkspaceMap,
            ClientToolNames.FileSystemList,
            ClientToolNames.FileSystemFindFiles,
        ];
        foreach (var candidate in safeRecoveryOrder)
        {
            if (!previouslyRequested.Contains(candidate)
                && tools.Any(tool => string.Equals(tool.Name, candidate, StringComparison.Ordinal)))
            {
                toolName = candidate;
                return true;
            }
        }

        return false;
    }

    private static string CreateCompactCodingRouterState(IReadOnlyList<LmChatMessage> messages)
    {
        var callsById = messages
            .SelectMany(static message => message.ToolCalls ?? [])
            .GroupBy(static call => call.Id, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var latestUserMessage = messages
            .LastOrDefault(static message =>
                string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(message.ToolCallId));
        var entries = new List<string>();
        foreach (var message in messages.TakeLast(18))
        {
            if (ReferenceEquals(message, latestUserMessage)
                || string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (message.ToolCalls is { Count: > 0 })
            {
                foreach (var call in message.ToolCalls)
                {
                    entries.Add(TruncateCodingRouterText(
                        $"[GO_PREVIOUS_TOOL_CALLS] {call.Name} arguments={call.Arguments.GetRawText()}",
                        MaximumCodingRouterEntryCharacters));
                }
                continue;
            }

            if (!string.IsNullOrWhiteSpace(message.ToolCallId))
            {
                callsById.TryGetValue(message.ToolCallId, out var call);
                var toolName = call?.Name ?? "unknown";
                entries.Add(
                    $"[GO_PREVIOUS_TOOL_RESULT tool={toolName}]\n"
                    + CreateCompactCodingToolResult(toolName, message.Content));
                continue;
            }

            if (string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(message.Content))
            {
                entries.Add(TruncateCodingRouterText(
                    $"[GO_PREVIOUS_ASSISTANT_STATE]\n{message.Content}",
                    MaximumCodingRouterEntryCharacters));
            }
        }

        var historyLength = entries.Sum(static entry => entry.Length + Environment.NewLine.Length);
        while (entries.Count > 0 && historyLength > MaximumCodingRouterHistoryCharacters)
        {
            historyLength -= entries[0].Length + Environment.NewLine.Length;
            entries.RemoveAt(0);
        }

        var userRequest = TruncateCodingRouterText(
            latestUserMessage?.Content ?? "Kein separater Nutzerauftrag vorhanden.",
            MaximumCodingRouterUserCharacters);
        var recentState = entries.Count == 0
            ? "Noch keine Werkzeugausführung."
            : string.Join(Environment.NewLine, entries);
        return $"""
            [GO_CURRENT_USER_REQUEST]
            {userRequest}

            [GO_RECENT_TOOL_STATE]
            {recentState}
            """;
    }

    private static string CreateCodingToolArgumentState(IReadOnlyList<LmChatMessage> messages)
    {
        var callsById = messages
            .SelectMany(static message => message.ToolCalls ?? [])
            .GroupBy(static call => call.Id, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var latestUserMessage = messages
            .LastOrDefault(static message =>
                string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(message.ToolCallId));
        LmChatMessage? repositoryMessage = null;
        for (var index = 1; index < messages.Count; index++)
        {
            if (string.Equals(messages[index].Role, "user", StringComparison.OrdinalIgnoreCase)
                && string.Equals(messages[index - 1].Role, "system", StringComparison.OrdinalIgnoreCase)
                && messages[index - 1].Content?.Contains("Repositorykarte", StringComparison.OrdinalIgnoreCase) == true)
            {
                repositoryMessage = messages[index];
                break;
            }
        }

        var entries = new List<string>();
        foreach (var message in messages.TakeLast(30))
        {
            if (ReferenceEquals(message, latestUserMessage)
                || ReferenceEquals(message, repositoryMessage)
                || string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (message.ToolCalls is { Count: > 0 })
            {
                foreach (var call in message.ToolCalls)
                {
                    entries.Add(TruncateCodingArgumentText(
                        $"[GO_PREVIOUS_TOOL_CALLS] {call.Name} arguments={call.Arguments.GetRawText()}",
                        4_000));
                }
                continue;
            }

            if (!string.IsNullOrWhiteSpace(message.ToolCallId))
            {
                callsById.TryGetValue(message.ToolCallId, out var call);
                entries.Add(TruncateCodingArgumentText(
                    $"[GO_PREVIOUS_TOOL_RESULT tool={call?.Name ?? "unknown"}]\n{message.Content ?? string.Empty}",
                    MaximumCodingArgumentEntryCharacters));
                continue;
            }

            if (string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(message.Content))
            {
                entries.Add(TruncateCodingArgumentText(
                    $"[GO_PREVIOUS_ASSISTANT_STATE]\n{message.Content}",
                    4_000));
            }
        }

        var historyLength = entries.Sum(static entry => entry.Length + Environment.NewLine.Length);
        while (entries.Count > 0 && historyLength > MaximumCodingArgumentHistoryCharacters)
        {
            historyLength -= entries[0].Length + Environment.NewLine.Length;
            entries.RemoveAt(0);
        }

        var userRequest = TruncateCodingArgumentText(
            latestUserMessage?.Content ?? "Kein separater Nutzerauftrag vorhanden.",
            MaximumCodingArgumentUserCharacters);
        var repositoryContext = repositoryMessage is null
            ? "Keine separate Repositorykarte vorhanden."
            : TruncateCodingArgumentText(
                repositoryMessage.Content ?? string.Empty,
                MaximumCodingArgumentRepositoryCharacters);
        var recentState = entries.Count == 0
            ? "Noch keine Werkzeugausführung."
            : string.Join(Environment.NewLine, entries);
        return $"""
            [GO_CURRENT_USER_REQUEST]
            {userRequest}

            [GO_REPOSITORY_CONTEXT]
            {repositoryContext}

            [GO_RECENT_TOOL_STATE]
            {recentState}
            """;
    }

    private static string CreateCompactCodingToolResult(string toolName, string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return "Leeres Ergebnis.";
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (string.Equals(toolName, "web.fetch", StringComparison.Ordinal)
                && root.ValueKind == JsonValueKind.Object)
            {
                var summary = new StringBuilder("Abruf abgeschlossen");
                AppendCompactJsonProperty(summary, root, "success");
                AppendCompactJsonProperty(summary, root, "url");
                AppendCompactJsonProperty(summary, root, "mediaType");
                AppendCompactJsonProperty(summary, root, "errorCode");
                AppendCompactJsonProperty(summary, root, "message");
                if (TryGetPropertyIgnoreCase(root, "content", out var fetchedContent)
                    && fetchedContent.ValueKind == JsonValueKind.String)
                {
                    summary.Append("; contentCharacters=")
                        .Append(fetchedContent.GetString()?.Length ?? 0);
                }
                return TruncateCodingRouterText(summary.ToString(), MaximumCodingRouterEntryCharacters);
            }

            if (string.Equals(toolName, "web.search", StringComparison.Ordinal)
                && root.ValueKind == JsonValueKind.Object)
            {
                var summary = new StringBuilder("Suche abgeschlossen");
                AppendCompactJsonProperty(summary, root, "provider");
                if (TryGetPropertyIgnoreCase(root, "results", out var results)
                    && results.ValueKind == JsonValueKind.Array)
                {
                    var resultCount = results.GetArrayLength();
                    summary.Append("; results=").Append(resultCount);
                    foreach (var result in results.EnumerateArray().Take(5))
                    {
                        if (TryGetPropertyIgnoreCase(result, "url", out var url)
                            && url.ValueKind == JsonValueKind.String)
                        {
                            summary.AppendLine().Append("- ").Append(url.GetString());
                        }
                    }
                }
                return TruncateCodingRouterText(summary.ToString(), MaximumCodingRouterEntryCharacters);
            }
        }
        catch (JsonException)
        {
            // Client tools may return plain text. It is still useful to the router,
            // but never allowed to expand the name-only routing prompt without bound.
        }

        return TruncateCodingRouterText(content, MaximumCodingRouterEntryCharacters);
    }

    private static void AppendCompactJsonProperty(
        StringBuilder summary,
        JsonElement root,
        string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(root, propertyName, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return;
        }

        summary.Append("; ").Append(propertyName).Append('=').Append(
            value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText());
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement root,
        string propertyName,
        out JsonElement value)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string TruncateCodingRouterText(string value, int maximumCharacters)
    {
        if (value.Length <= maximumCharacters)
        {
            return value;
        }

        const string marker = "\n[GO_ROUTER_TEXT_TRUNCATED]";
        return string.Concat(value.AsSpan(0, maximumCharacters - marker.Length), marker);
    }

    private static string TruncateCodingArgumentText(string value, int maximumCharacters)
    {
        if (value.Length <= maximumCharacters)
        {
            return value;
        }

        const string marker = "\n[GO_ARGUMENT_CONTEXT_TRUNCATED]";
        return string.Concat(value.AsSpan(0, maximumCharacters - marker.Length), marker);
    }

    private static bool TryParseCodingToolSelection(
        string? content,
        IReadOnlyList<LmToolDefinition> tools,
        bool allowFinalAnswer,
        out string selectedName)
    {
        selectedName = string.Empty;
        var normalized = content?.Trim().Trim('`', '"', '\'');
        if (int.TryParse(normalized?.TrimEnd('.', ')'), out var selectedIndex))
        {
            if (selectedIndex >= 1 && selectedIndex <= tools.Count)
            {
                selectedName = tools[selectedIndex - 1].Name;
                return true;
            }
            if (allowFinalAnswer && selectedIndex == tools.Count + 1)
            {
                selectedName = CodingToolFinalSelection;
                return true;
            }
        }
        if (allowFinalAnswer
            && (string.Equals(normalized, CodingToolFinalSelection, StringComparison.Ordinal)
                || ContainsStandaloneCodingToolName(normalized ?? string.Empty, CodingToolFinalSelection)))
        {
            selectedName = CodingToolFinalSelection;
            return true;
        }
        if (!string.IsNullOrWhiteSpace(normalized)
            && tools.Any(tool => string.Equals(tool.Name, normalized, StringComparison.Ordinal)))
        {
            selectedName = normalized;
            return true;
        }
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var candidates = tools
            .Select(static tool => tool.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (allowFinalAnswer)
        {
            candidates.Add(CodingToolFinalSelection);
        }
        var matches = candidates
            .Where(candidate => ContainsStandaloneCodingToolName(content, candidate))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (matches.Length == 1)
        {
            selectedName = matches[0];
            return true;
        }

        return false;
    }

    private static bool ContainsStandaloneCodingToolName(string value, string candidate)
    {
        for (var searchOffset = 0; searchOffset < value.Length;)
        {
            var index = value.IndexOf(candidate, searchOffset, StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }
            var beforeIsNameCharacter = index > 0 && IsCodingToolNameCharacter(value[index - 1]);
            var end = index + candidate.Length;
            var afterIsNameCharacter = end < value.Length && IsCodingToolNameCharacter(value[end]);
            if (!beforeIsNameCharacter && !afterIsNameCharacter)
            {
                return true;
            }
            searchOffset = index + candidate.Length;
        }

        return false;
    }

    private static bool IsCodingToolNameCharacter(char value) =>
        char.IsLetterOrDigit(value) || value is '_' or '-';

    private async Task<LmChatResult> CompleteChatViaChatCompletionsAsync(
        string modelId,
        IReadOnlyList<LmChatMessage> messages,
        IReadOnlyList<LmToolDefinition> tools,
        int maximumOutputTokens,
        string modelRole,
        string? reasoningEffort,
        bool requireToolCall,
        string? requiredToolName,
        CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = modelId,
            ["messages"] = CreateChatCompletionMessages(messages),
            ["stream"] = false,
            ["max_tokens"] = Math.Clamp(maximumOutputTokens, 1, 65_536),
        };
        ApplySamplingProfile(
            body,
            modelId,
            includeReasoning: false,
            modelRole,
            reasoningEffort,
            maximumOutputTokens);
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
            body["tool_choice"] = requireToolCall ? "required" : "auto";
            body["parallel_tool_calls"] = false;
        }

        LogAtomicToolSelection(
            tools.Count,
            requiredToolName ?? (requireToolCall ? "<any>" : "<none>"));

        // A coding round is recovered by the host-side schema fixer after the
        // first native channel failure. Replaying the identical native request
        // repeatedly only repeats the provider parser failure and delays useful
        // recovery. General chat keeps its conservative transport retries.
        var maximumAttempts = string.Equals(modelRole, "code", StringComparison.OrdinalIgnoreCase)
            ? 1
            : 3;
        Exception? lastException = null;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            try
            {
                using var request = await CreateRequestAsync(
                    HttpMethod.Post,
                    "v1/chat/completions",
                    body,
                    cancellationToken).ConfigureAwait(false);
                using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var errorPayload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    var statusCode = response.StatusCode;
                    var transientChannelError = IsLmStudioChannelTermination(errorPayload);
                    var retryableProviderFailure = transientChannelError || IsTransientProviderStatus(statusCode);
                    if (attempt < maximumAttempts && retryableProviderFailure)
                    {
                        LogChatCompletionsAtomicRetry(
                            transientChannelError ? "channel_error" : $"http_{(int)statusCode}",
                            attempt + 1,
                            modelId);
                        await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (retryableProviderFailure)
                    {
                        throw new LmStudioGenerationTerminatedException(
                            transientChannelError
                                ? "chat_completions_channel_error"
                                : $"chat_completions_http_{(int)statusCode}",
                            new HttpRequestException(
                                transientChannelError
                                    ? $"LM Studio Chat Completions returned HTTP {(int)statusCode} after a terminated predict channel."
                                    : $"LM Studio Chat Completions returned transient HTTP {(int)statusCode}.",
                                inner: null,
                                statusCode));
                    }

                    throw new HttpRequestException(
                        $"LM Studio Chat Completions returned HTTP {(int)statusCode}.",
                        inner: null,
                        statusCode);
                }

                using var document = JsonDocument.Parse(
                    await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
                return ParseAtomicChatCompletion(document.RootElement);
            }
            catch (LmStudioGenerationTerminatedException exception) when (
                attempt < maximumAttempts
                && !cancellationToken.IsCancellationRequested)
            {
                lastException = exception;
                LogChatCompletionsAtomicRetry(exception.ProviderCode, attempt + 1, modelId);
                await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception) when (
                attempt < maximumAttempts
                && !cancellationToken.IsCancellationRequested
                && (exception.StatusCode is null || IsTransientProviderStatus(exception.StatusCode.Value)))
            {
                lastException = exception;
                LogChatCompletionsAtomicRetry(
                    exception.StatusCode is { } statusCode ? $"http_{(int)statusCode}" : "transport_error",
                    attempt + 1,
                    modelId);
                await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException exception) when (
                attempt < maximumAttempts
                && !cancellationToken.IsCancellationRequested)
            {
                lastException = exception;
                LogChatCompletionsAtomicRetry("invalid_json", attempt + 1, modelId);
                await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        throw lastException
            ?? new LmStudioGenerationTerminatedException("chat_completions_channel_error");
    }

    private static void ValidateRequiredToolChoice(
        IReadOnlyList<LmToolDefinition> tools,
        bool requireToolCall,
        string? requiredToolName)
    {
        if (string.IsNullOrWhiteSpace(requiredToolName))
        {
            return;
        }

        if (!requireToolCall)
        {
            throw new ArgumentException(
                "A named tool choice requires requireToolCall=true.",
                nameof(requiredToolName));
        }

        if (tools.Count != 1
            || !string.Equals(tools[0].Name, requiredToolName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The required tool '{requiredToolName}' must be the only supplied tool schema.",
                nameof(requiredToolName));
        }
    }

    private static LmChatResult ParseAtomicChatCompletion(JsonElement root)
    {
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
                if (string.IsNullOrWhiteSpace(name)
                    || string.IsNullOrWhiteSpace(argumentsText))
                {
                    throw new JsonException("LM Studio returned an incomplete Chat Completions tool call.");
                }

                using var argumentsDocument = JsonDocument.Parse(argumentsText);
                calls.Add(new LmToolCall(
                    string.IsNullOrWhiteSpace(id) ? $"call_{Guid.NewGuid():N}" : id,
                    name,
                    argumentsDocument.RootElement.Clone()));
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

    private static bool IsLmStudioChannelTermination(string payload) =>
        payload.Contains("terminated", StringComparison.OrdinalIgnoreCase)
        || payload.Contains("channel error", StringComparison.OrdinalIgnoreCase)
        || payload.Contains("ERR_HTTP_HEADERS_SENT", StringComparison.OrdinalIgnoreCase)
        || payload.Contains("error in channel handler", StringComparison.OrdinalIgnoreCase);

    private static object[] CreateChatCompletionMessages(IReadOnlyList<LmChatMessage> messages)
    {
        var result = new List<object>(messages.Count);
        var primarySystemMessageIndex = FindPrimarySystemMessageIndex(messages);
        for (var messageIndex = 0; messageIndex < messages.Count; messageIndex++)
        {
            var message = messages[messageIndex];
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

            if (messageIndex != primarySystemMessageIndex
                && string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new
                {
                    role = "user",
                    content = "[GO_RUNTIME_GUIDANCE]\n" + (message.Content ?? string.Empty),
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

    private static void ApplySamplingProfile(
        Dictionary<string, object?> body,
        string modelId,
        bool includeReasoning,
        string modelRole,
        string? requestedReasoningEffort,
        int? maximumOutputTokens)
    {
        var reasoningProfile = ModelReasoningProfiles.Resolve(modelId, modelRole);
        var reasoningEffort = reasoningProfile.Resolve(requestedReasoningEffort);
        if (string.Equals(modelRole, "code", StringComparison.OrdinalIgnoreCase)
            && CodingModelCatalog.TryGet(modelId, out var codingProfile))
        {
            if (string.Equals(codingProfile.SamplingProfile, "gpt-oss-coder", StringComparison.Ordinal))
            {
                // OpenAI recommends temperature=1 and top_p=1 for gpt-oss.
                // High reasoning is reserved for the coding role; the same
                // physical model keeps the existing low-effort general profile.
                body["temperature"] = 1.0;
                body["top_p"] = 1.0;
                if (includeReasoning && reasoningEffort is not null)
                {
                    body["reasoning"] = new { effort = reasoningEffort };
                }
                else if (!includeReasoning && reasoningEffort is not null)
                {
                    body["reasoning_effort"] = reasoningEffort;
                }
                return;
            }

            if (string.Equals(codingProfile.SamplingProfile, "qwen38-coder", StringComparison.Ordinal))
            {
                // LM Studio exposes this GGUF's real public reasoning options as
                // on/off. When enabled, a hard llama.cpp thinking budget reserves
                // half of the response for the required tool call or final answer.
                body["temperature"] = 1.0;
                body["top_p"] = 0.95;
                body["top_k"] = 20;
                body["min_p"] = 0.0;
                body["presence_penalty"] = 0.0;
                body["repetition_penalty"] = 1.0;
                ApplyQwen38Reasoning(
                    body,
                    includeReasoning,
                    reasoningEffort,
                    maximumOutputTokens,
                    reserveAgentAnswerBudget: true);
                return;
            }

            // Qwen3-Coder-Next keeps its established non-reasoning agent profile.
            body["temperature"] = 1.0;
            body["top_p"] = 0.95;
            if (string.Equals(codingProfile.SamplingProfile, "qwen-coder", StringComparison.Ordinal))
            {
                body["top_k"] = 40;
            }
            return;
        }

        if (string.Equals(reasoningProfile.Family, ModelReasoningProfiles.Qwen38Family, StringComparison.Ordinal))
        {
            body["temperature"] = 1.0;
            body["top_p"] = 0.95;
            body["top_k"] = 20;
            body["min_p"] = 0.0;
            body["presence_penalty"] = 0.0;
            body["repetition_penalty"] = 1.0;
            ApplyQwen38Reasoning(
                body,
                includeReasoning,
                reasoningEffort,
                maximumOutputTokens,
                reserveAgentAnswerBudget: false);
            return;
        }
        else
        {
            body["temperature"] = 0.2;
        }
        if (includeReasoning && reasoningEffort is not null)
        {
            body["reasoning"] = new { effort = reasoningEffort };
        }
        else if (!includeReasoning && reasoningEffort is not null)
        {
            body["reasoning_effort"] = reasoningEffort;
        }
    }

    private static void ApplyQwen38Reasoning(
        Dictionary<string, object?> body,
        bool includeReasoning,
        string? reasoningEffort,
        int? maximumOutputTokens,
        bool reserveAgentAnswerBudget)
    {
        if (string.Equals(reasoningEffort, "off", StringComparison.OrdinalIgnoreCase))
        {
            // The OpenAI Responses schema calls the disabled state `none`, while
            // LM Studio advertises the same model switch as `off`.
            if (includeReasoning)
            {
                body["reasoning"] = new { effort = "none" };
            }
            else
            {
                // LM Studio's Chat Completions surface exposes the model's
                // native switch as on/off, not the Responses API none value.
                body["reasoning_effort"] = "off";
            }
            return;
        }

        // Do not send effort=on: it is not part of the OpenAI Responses enum.
        // Omitting the field activates LM Studio's advertised Qwen default without
        // the misleading low/medium/high fallback warning.
        if (reserveAgentAnswerBudget && maximumOutputTokens is > 1)
        {
            body["thinking_budget_tokens"] = ResolveCodingReasoningBudget(maximumOutputTokens.Value);
        }
    }

    internal static int ResolveCodingReasoningBudget(int maximumOutputTokens)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumOutputTokens, 2);
        return Math.Min(4_096, Math.Max(1, maximumOutputTokens / 2));
    }

    private sealed class PendingFunctionCall
    {
        private PendingFunctionCall(
            int outputIndex,
            string? itemId,
            string? callId,
            string? name,
            string? arguments)
        {
            OutputIndex = outputIndex;
            ItemId = itemId;
            CallId = callId;
            Name = name;
            Arguments = new StringBuilder(arguments ?? string.Empty);
        }

        public int OutputIndex { get; }

        public string? ItemId { get; private set; }

        public string? CallId { get; private set; }

        public string? Name { get; private set; }

        public StringBuilder Arguments { get; }

        public static PendingFunctionCall FromItem(JsonElement item, int outputIndex) => new(
            outputIndex,
            ReadString(item, "id"),
            ReadString(item, "call_id") ?? ReadString(item, "id"),
            ReadString(item, "name"),
            ReadString(item, "arguments"));

        public bool Matches(JsonElement streamEvent)
        {
            var eventItemId = ReadString(streamEvent, "item_id");
            if (!string.IsNullOrWhiteSpace(eventItemId) && !string.IsNullOrWhiteSpace(ItemId))
            {
                return string.Equals(eventItemId, ItemId, StringComparison.Ordinal);
            }
            if (streamEvent.TryGetProperty("output_index", out var outputIndex)
                && outputIndex.TryGetInt32(out var eventOutputIndex))
            {
                return eventOutputIndex == OutputIndex;
            }
            return true;
        }

        public void FillMissingMetadata(JsonElement item)
        {
            ItemId ??= ReadString(item, "id");
            CallId ??= ReadString(item, "call_id") ?? ItemId;
            Name ??= ReadString(item, "name");
            if (Arguments.Length == 0 && ReadString(item, "arguments") is { Length: > 0 } arguments)
            {
                Arguments.Append(arguments);
            }
        }
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

public sealed class LmStudioGenerationTerminatedException(
    string providerCode,
    Exception? innerException = null)
    : HttpRequestException($"LM Studio terminated generation ({providerCode}).", innerException)
{
    public string ProviderCode { get; } = string.IsNullOrWhiteSpace(providerCode)
        ? "unknown"
        : providerCode.Trim();
}
