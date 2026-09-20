using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Coding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GoAi.Server.Core.Models;

/// <summary>
/// Connects every model role to the native Windows llama.cpp router.
/// GO remains the only tool executor; the local Unsloth catalog owns model paths.
/// </summary>
public sealed partial class ModelRuntimeClient : IDisposable
{
    private static readonly Action<ILogger, Exception?> LogRouterUnavailable = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(4101, "ModelRouterUnavailable"),
        "The native llama model server is unavailable.");
    private static readonly Action<ILogger, Exception?> LogInferenceRetry = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(4102, "ModelInferenceRetry"),
        "Transient model inference failure; retrying before any tool is executed.");
    private static readonly Action<ILogger, int, string, int, string, int, bool, Exception?> LogStreamingAttempt =
        LoggerMessage.Define<int, string, int, string, int, bool>(
            LogLevel.Warning,
            new EventId(4103, "ModelStreamingAttemptIncomplete"),
            "Model inference attempt {Attempt}/3 ended ({FailureKind}); fragments={GeneratedFragments}, tool={ToolName}, argumentCharacters={ArgumentCharacters}, argumentJsonComplete={ArgumentJsonComplete}.");
    private static readonly TimeSpan StatusCacheDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ModelLoadTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ModelTurnTimeout = TimeSpan.FromMinutes(20);
    private readonly HttpClient _httpClient;
    private readonly GoAiServerOptions _options;
    private readonly ILogger<ModelRuntimeClient> _logger;
    private readonly SemaphoreSlim _modelGate = new(1, 1);
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, RuntimeModel> _runtimeCatalog = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _modelTransitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _sessionCacheModels = new(StringComparer.Ordinal);
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
            if (_cachedStatus is not null && DateTimeOffset.UtcNow < _cacheExpiresAt) return _cachedStatus;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var ownsModelGate = false;
        try
        {
            ownsModelGate = await _modelGate.WaitAsync(0, timeout.Token).ConfigureAwait(false);
            // A model operation can take minutes. Do not wait behind it or query
            // a child that is being removed; the router remains independently checkable.
            var models = ownsModelGate
                ? await GetRuntimeModelsAsync(timeout.Token).ConfigureAwait(false)
                : ApplyTransitionStatus(await ReadRuntimeCatalogAsync(timeout.Token).ConfigureAwait(false));
            var status = new ModelStatusSnapshot(true, _options.ModelRuntimeUri.ToString(), BuildRuntimeStatuses(models), DateTimeOffset.UtcNow);
            // A transition snapshot must not outlive the operation that produced it.
            return ownsModelGate ? Cache(status) : status;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException)
        {
            LogRouterUnavailable(_logger, exception);
            return Cache(new ModelStatusSnapshot(false, _options.ModelRuntimeUri.ToString(), GetLastReachableModels(), DateTimeOffset.UtcNow,
                exception is OperationCanceledException ? "modelRuntime.timeout"
                    : exception is JsonException ? "modelRuntime.invalidResponse" : "modelRuntime.unreachable",
                DescribeModelRuntimeFailure(exception)));
        }
        finally { if (ownsModelGate) _modelGate.Release(); }
    }

    private string DescribeModelRuntimeFailure(Exception exception)
    {
        var endpoint = _options.ModelRuntimeUri.GetComponents(UriComponents.SchemeAndServer, UriFormat.Unescaped);
        if (exception is OperationCanceledException)
            return $"Windows llama.cpp ({endpoint}) antwortete nicht innerhalb von 5 Sekunden.";
        var detail = string.Concat(exception.Message.Where(static character => !char.IsControl(character)));
        if (detail.Length > 1_000) detail = detail[..1_000];
        return exception is JsonException
            ? $"Windows llama.cpp ({endpoint}) lieferte einen ungültigen Modellstatus: {detail}"
            : $"Windows llama.cpp ({endpoint}) ist nicht erreichbar oder lieferte einen HTTP-Fehler: {detail}";
    }

    public async Task<string> EnsureModelLoadedAsync(
        string modelId,
        int contextLength,
        CancellationToken cancellationToken = default) =>
        (await EnsureModelPreparedAsync(modelId, contextLength, null, cancellationToken).ConfigureAwait(false)).InstanceId;

    internal async Task<ModelPreparation> EnsureModelPreparedAsync(
        string modelId, int contextLength, Func<CancellationToken, Task>? loadingStarted,
        CancellationToken cancellationToken = default)
    {
        var turnGate = await AcquireTurnAsync(modelId, cancellationToken).ConfigureAwait(false);
        try { return await PrepareNativeModelAsync(modelId, contextLength, loadingStarted, cancellationToken).ConfigureAwait(false); }
        finally { turnGate.Release(); }
    }

    private async Task<ModelPreparation> PrepareNativeModelAsync(
        string modelId, int contextLength, Func<CancellationToken, Task>? loadingStarted, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        await _modelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ModelLoadTimeout);
            var runtimeModels = await GetRuntimeModelsAsync(timeout.Token).ConfigureAwait(false);
            var selected = ResolveInstalledModel(runtimeModels, modelId)
                ?? throw new FileNotFoundException($"Das lokale Unsloth-Modell '{modelId}' ist nicht installiert.");
            if (contextLength > selected.MaximumContextLength)
                throw new ModelContextLengthException(selected.Id, contextLength, selected.MaximumContextLength);
            if (selected.State is "loaded" or "sleeping") return new ModelPreparation(selected.Id, WasAlreadyLoaded: true, selected.LoadedContextLength);
            BeginModelTransition(runtimeModels.Where(model => model.Id != selected.Id && model.State is "loaded" or "loading" or "sleeping"), selected.Id);
            if (loadingStarted is not null) await loadingStarted(cancellationToken).ConfigureAwait(false);
            // Change residency only when the selected model actually needs loading. A resumed tool round reuses it.
            foreach (var loaded in runtimeModels.Where(model => model.Id != selected.Id && model.State is "loaded" or "loading" or "sleeping"))
                await UnloadRuntimeInstanceAsync(loaded.Id, timeout.Token).ConfigureAwait(false);
            try { await LoadRuntimeModelAsync(selected.Id, timeout.Token, selected.ManagedGpuPlacement).ConfigureAwait(false); }
            catch (Exception exception) when (IsTransientInferenceFailure(exception) && !timeout.IsCancellationRequested)
            {
                // A dropped load response can follow a successful native allocation. Reconcile once before retrying anything.
                var recovered = (await GetRuntimeModelsAsync(timeout.Token).ConfigureAwait(false)).FirstOrDefault(model => model.Id == selected.Id);
                if (recovered?.State is not ("loaded" or "sleeping" or "loading")) throw;
            }
            while (true)
            {
                var current = (await GetRuntimeModelsAsync(timeout.Token).ConfigureAwait(false)).FirstOrDefault(model => model.Id == selected.Id);
                if (current?.State is "loaded" or "sleeping")
                {
                    InvalidateStatus();
                    return new ModelPreparation(selected.Id, WasAlreadyLoaded: false, current.LoadedContextLength);
                }
                if (current?.State == "failed") throw new InvalidOperationException($"Das native Modell '{selected.Id}' konnte nicht geladen werden. Runtime-Logs und freien GPU-Speicher prüfen.");
                await Task.Delay(TimeSpan.FromSeconds(1), timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Das Modell '{modelId}' wurde nicht innerhalb von 5 Minuten geladen.", exception);
        }
        finally { EndModelTransition(); _modelGate.Release(); }
    }

    public async Task<bool> UnloadModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        var turnGate = await AcquireTurnAsync(null, cancellationToken).ConfigureAwait(false);
        try
        {
            await _modelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var model = ResolveInstalledModel(await GetRuntimeModelsAsync(cancellationToken).ConfigureAwait(false), modelId);
                if (model is null || model.State is not ("loaded" or "loading" or "sleeping")) return false;
                BeginModelTransition([model], null);
                await UnloadRuntimeInstanceAsync(model.Id, cancellationToken).ConfigureAwait(false);
                return true;
            }
            finally { EndModelTransition(); _modelGate.Release(); }
        }
        finally { turnGate.Release(); }
    }

    public Task UnloadAllModelsAsync(CancellationToken cancellationToken = default) => UnloadModelsExceptAsync([], cancellationToken);

    public async Task UnloadModelsExceptAsync(IReadOnlyCollection<string> preservedModelIds, CancellationToken cancellationToken = default)
    {
        var turnGate = await AcquireTurnAsync(null, cancellationToken).ConfigureAwait(false);
        try
        {
            await _modelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var models = await GetRuntimeModelsAsync(cancellationToken).ConfigureAwait(false);
                var preserved = preservedModelIds.Select(id => ResolveInstalledModel(models, id)?.Id).Where(static id => id is not null).ToHashSet(StringComparer.Ordinal);
                var unloading = models.Where(model => model.State is "loaded" or "loading" or "sleeping" && !preserved.Contains(model.Id)).ToArray();
                BeginModelTransition(unloading, null);
                foreach (var model in unloading)
                    await UnloadRuntimeInstanceAsync(model.Id, cancellationToken).ConfigureAwait(false);
            }
            finally { EndModelTransition(); _modelGate.Release(); }
        }
        finally { turnGate.Release(); }
    }

    public async Task<LmChatResult> CompleteChatAsync(
        string modelId,
        IReadOnlyList<LmChatMessage> messages,
        IReadOnlyList<LmToolDefinition> tools,
        int? maximumOutputTokens = null,
        string modelRole = "general",
        string? reasoningEffort = null,
        bool requireToolCall = false,
        string? requiredToolName = null,
        int? requiredContextLength = null,
        Func<ModelRuntimeProgress, CancellationToken, ValueTask>? nativeProgress = null,
        bool structuredToolOnly = false,
        string? sessionCacheKey = null,
        CancellationToken cancellationToken = default)
    {
        ValidateToolChoice(tools, requireToolCall, requiredToolName);
        var coding = string.Equals(modelRole, "coding", StringComparison.Ordinal);
        if (coding && !IsCodingModel(modelId))
        {
            throw new ArgumentException("Coding requires an installed model from the Coding model catalog.", nameof(modelId));
        }
        var turnClock = Stopwatch.StartNew();
        var turnGate = await AcquireTurnAsync(modelId, cancellationToken).ConfigureAwait(false);
        var queueMilliseconds = turnClock.Elapsed.TotalMilliseconds;
        string? preparedInstanceId = null;
        int? evaluatedPromptTokens = null;
        try
        {
            // The preset chooses the model's maximum that fits. A request limit is
            // an upper bound; it must not require a larger allocation after fitting.
            var preparation = await PrepareNativeModelAsync(modelId, 0, null, cancellationToken).ConfigureAwait(false);
            preparedInstanceId = preparation.InstanceId;
            await UpdateSessionCacheAsync("prepare", preparation.InstanceId, sessionCacheKey, cancellationToken).ConfigureAwait(false);
            var context = requiredContextLength is { } requested
                ? Math.Min(requested, preparation.ContextLength) : preparation.ContextLength;
            if (nativeProgress is not null)
            {
                await nativeProgress(new ModelRuntimeProgress("generationStarted"), cancellationToken).ConfigureAwait(false);
            }

            var body = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["model"] = preparation.InstanceId,
                ["messages"] = PrepareLanguageBoundMessages(messages)
                    .Select(ToOpenAiMessage)
                    .ToArray(),
                // Stream progress and text immediately. A tool is executed only
                // after its complete payload has been buffered and validated.
                ["stream"] = true,
                ["return_progress"] = true,
                ["cache_prompt"] = true,
                // Snapshot restore/save and the serialized turn gate own slot 0.
                // Do not let llama.cpp choose another slot after a restart.
                ["id_slot"] = 0,
                ["stream_options"] = new { include_usage = true },
                ["parallel_tool_calls"] = coding,
            };
            ApplyModelSampling(body, modelId);
            ApplyReasoningSettings(body, modelId, modelRole, reasoningEffort);
            if (coding)
            {
                body["return_progress"] = true;
                body["sse_ping_interval"] = 5;
                body["cache_prompt"] = true;
            }
            if (tools.Count > 0)
            {
                body["tools"] = tools.Select(tool => new
                {
                    type = "function",
                    function = new
                    {
                        name = ToTransportToolName(tool.Name),
                        description = tool.Description,
                        parameters = PrepareToolParameters(modelId, tool),
                    },
                }).ToArray();
                // native llama's OpenAI-compatible endpoint accepts auto/required/none.
                // The host uses "required" whenever the current agent protocol
                // mandates one structured action. Schema validation still decides
                // which of the supplied tools and arguments are acceptable.
                body["tool_choice"] = requireToolCall || requiredToolName is { Length: > 0 }
                    ? "required"
                    : "auto";
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ModelTurnTimeout);
            var stallProgress = new NativeInferenceStallProgress(() => timeout.CancelAfter(ModelTurnTimeout));
            async ValueTask ReportProgressAsync(ModelRuntimeProgress progress, CancellationToken token)
            {
                if (progress.PromptTokens is > 0) evaluatedPromptTokens = progress.PromptTokens;
                if (nativeProgress is not null) await nativeProgress(progress, token).ConfigureAwait(false);
            }
            async ValueTask ProgressWithDeadlineAsync(ModelRuntimeProgress progress, CancellationToken token)
            {
                stallProgress.Observe(progress);
                await ReportProgressAsync(progress, token).ConfigureAwait(false);
            }
            var transportToolNames = tools.ToDictionary(
                static tool => ToTransportToolName(tool.Name),
                static tool => tool.Name,
                StringComparer.Ordinal);
            var result = await CompleteStreamingChatWithBoundedRetryAsync(
                body,
                transportToolNames,
                ProgressWithDeadlineAsync,
                structuredToolOnly,
                context,
                maximumOutputTokens,
                timeout.Token).ConfigureAwait(false);
            if (nativeProgress is not null)
            {
                await nativeProgress(
                    new ModelRuntimeProgress(
                        "tokenProgress",
                        PromptProgress: 1,
                        PromptTokens: result.Metrics?.InputTokens,
                        ProcessedPromptTokens: result.Metrics?.InputTokens,
                        CachedPromptTokens: result.Metrics?.CachedPromptTokens,
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
            // Persist every completed round, including tool calls. Long coding
            // runs must survive a process restart before their final response.
            if (!string.IsNullOrEmpty(sessionCacheKey))
                await UpdateSessionCacheAsync("save", preparation.InstanceId, sessionCacheKey, cancellationToken).ConfigureAwait(false);
            return result with { Metrics = (result.Metrics ?? new ModelTurnMetrics()) with
            {
                RuntimeQueueMilliseconds = queueMilliseconds,
                TotalMilliseconds = turnClock.Elapsed.TotalMilliseconds,
            } };
        }
        catch (ReasoningLoopDetectedException)
        {
            // Dispose/cancel only this inference request. Preserve its native
            // prefix just as for a deliberate interruption, without replaying it.
            var exactTail = await SaveInterruptedSessionCacheAsync(preparedInstanceId, sessionCacheKey,
                modelId.Contains("deepseek-v4", StringComparison.OrdinalIgnoreCase) ? evaluatedPromptTokens : null).ConfigureAwait(false);
            if (exactTail is not null && nativeProgress is not null)
                await nativeProgress(new ModelRuntimeProgress("interruptedNativeTail", ContentDelta: exactTail,
                    ReasoningDelta: ResolveReasoningEffort(modelId, modelRole, reasoningEffort) == "none" ? "off" : "on"),
                    CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var exactTail = await SaveInterruptedSessionCacheAsync(preparedInstanceId, sessionCacheKey,
                modelId.Contains("deepseek-v4", StringComparison.OrdinalIgnoreCase) ? evaluatedPromptTokens : null).ConfigureAwait(false);
            if (exactTail is not null && nativeProgress is not null)
                await nativeProgress(new ModelRuntimeProgress("interruptedNativeTail", ContentDelta: exactTail,
                    ReasoningDelta: ResolveReasoningEffort(modelId, modelRole, reasoningEffort) == "none" ? "off" : "on"),
                    CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ModelGenerationTerminatedException("model_stall_timeout", exception);
        }
        finally
        {
            turnGate.Release();
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
        var turnGate = await AcquireTurnAsync(null, cancellationToken).ConfigureAwait(false);
        try
        {
        var preparation = await PrepareNativeModelAsync(
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
        finally { turnGate.Release(); }
    }

    public Task<string> AnalyzeImagesAsync(
        string modelId,
        string prompt,
        IReadOnlyList<string> imagePaths,
        CancellationToken cancellationToken = default) =>
        AnalyzeImagesAsync(modelId, prompt, imagePaths, reasoningEffort: null, cancellationToken);

    public async Task<string> AnalyzeImagesAsync(
        string modelId,
        string prompt,
        IReadOnlyList<string> imagePaths,
        string? reasoningEffort,
        CancellationToken cancellationToken = default)
    {
        if (imagePaths.Count is < 1 or > 48)
        {
            throw new ArgumentOutOfRangeException(nameof(imagePaths));
        }
        var turnGate = await AcquireTurnAsync(modelId, cancellationToken).ConfigureAwait(false);
        try
        {
        var preparation = await PrepareNativeModelAsync(
            modelId,
            0,
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
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = preparation.InstanceId,
            ["stream"] = true,
            ["return_progress"] = true,
            ["stream_options"] = new { include_usage = true },
            ["messages"] = new object[]
            {
                new { role = "system", content = CodingAgentPolicy.ReasoningLanguagePrompt + "\n\n" + Policies.GeneralAgentPolicies.VisionAnalysisSystemPrompt },
                new { role = "user", content = content.ToArray() },
            },
        };
        await UpdateSessionCacheAsync("prepare", preparation.InstanceId, null, cancellationToken).ConfigureAwait(false);
        ApplyReasoningSettings(body, preparation.InstanceId, "vision", reasoningEffort);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ModelTurnTimeout);
        var stallProgress = new NativeInferenceStallProgress(() => timeout.CancelAfter(ModelTurnTimeout));
        ValueTask ObserveVisionProgressAsync(ModelRuntimeProgress progress, CancellationToken token)
        {
            stallProgress.Observe(progress);
            return ValueTask.CompletedTask;
        }
        var result = await CompleteStreamingChatWithBoundedRetryAsync(body,
            new Dictionary<string, string>(StringComparer.Ordinal), ObserveVisionProgressAsync,
            structuredToolOnly: false, preparation.ContextLength, maximumOutputTokens: null,
            timeout.Token).ConfigureAwait(false);
        var text = result.Content;
        return string.IsNullOrWhiteSpace(text)
            ? throw new JsonException("Das Vision-Modell lieferte keine Textantwort.")
            : text;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ModelGenerationTerminatedException("model_stall_timeout", exception);
        }
        finally { turnGate.Release(); }
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
        var models = await ReadRuntimeCatalogAsync(cancellationToken).ConfigureAwait(false);
        var reconciled = false;
        for (var index = 0; index < models.Length; index++)
        {
            if (models[index].State is not ("loaded" or "sleeping")) continue;
            var modelId = models[index].Id;
            try
            {
                using var props = await _httpClient.GetAsync("props?model=" + Uri.EscapeDataString(modelId), cancellationToken).ConfigureAwait(false);
                props.EnsureSuccessStatusCode();
                using var properties = await JsonDocument.ParseAsync(await props.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
                var loadedContext = ReadLoadedContextLength(properties.RootElement);
                var reasoning = ReadReasoningMetadata(properties.RootElement);
                models[index] = models[index] with { LoadedContextLength = Math.Min(loadedContext, models[index].MaximumContextLength),
                    ReasoningEfforts = reasoning?.SupportedEfforts ?? models[index].ReasoningEfforts,
                    DefaultReasoningEffort = reasoning is null ? models[index].DefaultReasoningEffort : reasoning.DefaultEffort,
                    ReasoningFamily = reasoning?.Family ?? models[index].ReasoningFamily };
            }
            catch (Exception exception) when (!reconciled && !cancellationToken.IsCancellationRequested
                && exception is HttpRequestException or JsonException)
            {
                // A native model switch can remove the child after /models but
                // before /props. Reconcile once, and only accept a demonstrated
                // residency change; a broken still-resident child remains an error.
                var refreshed = await ReadRuntimeCatalogAsync(cancellationToken).ConfigureAwait(false);
                if (refreshed.FirstOrDefault(model => model.Id == modelId)?.State is "loaded" or "sleeping") throw;
                models = refreshed;
                reconciled = true;
                index = -1;
            }
        }
        RememberRuntimeModels(models);
        return models;
    }

    private async Task<RuntimeModel[]> ReadRuntimeCatalogAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync("v1/models?reload=1", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        return ReadRuntimeModels(document.RootElement, _options.GeneralContextLength, _options.VisionContextLength, _options.EmbeddingContextLength);
    }

    private void BeginModelTransition(IEnumerable<RuntimeModel> unloadingModels, string? loadingModelId)
    {
        lock (_cacheLock)
        {
            _modelTransitions.Clear();
            foreach (var model in unloadingModels) _modelTransitions[model.Id] = "unloading";
            if (loadingModelId is not null) _modelTransitions[loadingModelId] = "loading";
            _cachedStatus = null;
            _cacheExpiresAt = default;
        }
    }

    private void EndModelTransition()
    {
        lock (_cacheLock)
        {
            _modelTransitions.Clear();
            _cachedStatus = null;
            _cacheExpiresAt = default;
        }
    }

    private RuntimeModel[] ApplyTransitionStatus(RuntimeModel[] models)
    {
        lock (_cacheLock)
        {
            for (var index = 0; index < models.Length; index++)
            {
                var model = models[index];
                var state = model.State;
                if (_modelTransitions.TryGetValue(model.Id, out var transition)
                    && (transition == "loading" || state is "loaded" or "sleeping" or "loading"))
                    state = transition;
                var knownContext = _runtimeCatalog.TryGetValue(model.Id, out var known)
                    ? known.LoadedContextLength : 0;
                models[index] = model with
                {
                    State = state,
                    InstanceId = state is "loaded" or "sleeping" ? model.InstanceId : null,
                    // Reuse only a previously observed effective context. The
                    // nominal catalog maximum remains provisional during loading.
                    LoadedContextLength = state is "loaded" or "sleeping" ? knownContext : 0,
                };
            }
        }
        return models;
    }

    private async Task LoadRuntimeModelAsync(string modelId, CancellationToken cancellationToken, bool managedGpuPlacement = false)
    {
        var path = managedGpuPlacement
            ? new UriBuilder(_options.ModelRuntimeUri) { Port = _options.ModelRuntimeUri.Port + 1, Path = "/models/load", Query = "" }.Uri.AbsoluteUri
            : "models/load";
        using var response = await SendJsonAsync(HttpMethod.Post, path, new { model = modelId }, cancellationToken,
            bufferContent: managedGpuPlacement).ConfigureAwait(false);
    }

    private async Task UnloadRuntimeInstanceAsync(string modelId, CancellationToken cancellationToken)
    {
        await UpdateSessionCacheAsync("save", modelId, null, cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(1));
        using var response = await SendJsonAsync(HttpMethod.Post, "models/unload", new { model = modelId }, timeout.Token).ConfigureAwait(false);
        // The router acknowledges an unload before the child has exited. Keep
        // the model gate until its residency actually changes; otherwise the
        // next GPU placement request rejects the still-running previous model.
        while ((await ReadRuntimeCatalogAsync(timeout.Token).ConfigureAwait(false))
            .Any(model => model.Id == modelId && model.State is "loaded" or "loading" or "sleeping"))
            await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token).ConfigureAwait(false);
    }

    private async Task<string?> UpdateSessionCacheAsync(string operation, string model, string? sessionKey, CancellationToken cancellationToken,
        int? interruptedPromptTokens = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_cacheLock)
        {
            // The supervisor can outlive this client/gateway and still own a
            // resident session. Every managed-runtime turn must detach/save it
            // before a stateless prompt, including the first turn after restart.
            // A bare legacy router advertises no supervisor; keep its stateless
            // path unchanged unless this client already used session persistence.
            var managedRuntime = _runtimeCatalog.TryGetValue(model, out var catalogModel) && catalogModel.ManagedGpuPlacement;
            if (sessionKey is null && !managedRuntime && !_sessionCacheModels.Contains(model)) return null;
            if (sessionKey is not null && operation == "prepare") _sessionCacheModels.Add(model);
        }
        // The native cache is an optimization. Older portable runtimes and incompatible
        // cache files must fall back to the authoritative persisted conversation.
        var path = new UriBuilder(_options.ModelRuntimeUri)
        { Port = _options.ModelRuntimeUri.Port + 1, Path = "/sessions/" + operation, Query = "" }.Uri.AbsoluteUri;
        // Once dispatched, retain the caller's turn gate until the control response
        // returns. Cancelling an HTTP waiter does not cancel llama's slot operation.
        // Prepare may save an outgoing slot and restore another (120 s each).
        // This bounded deadline covers both operations; it is not a guarantee that
        // an unresponsive native process has stopped work after a transport failure.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        string? generatedTail = null;
        try
        {
            using var response = await SendJsonAsync(HttpMethod.Post, path, new { model, sessionKey, promptTokens = interruptedPromptTokens }, timeout.Token,
                bufferContent: true).ConfigureAwait(false);
            using var result = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false),
                cancellationToken: timeout.Token).ConfigureAwait(false);
            if (result.RootElement.ValueKind != JsonValueKind.Object
                || !result.RootElement.TryGetProperty("status", out var value) || value.ValueKind != JsonValueKind.String)
                throw new JsonException("Native session cache response requires a text status.");
            var status = value.GetString();
            string? detail = null;
            if (result.RootElement.TryGetProperty("detail", out var reason))
            {
                if (reason.ValueKind != JsonValueKind.String)
                    throw new JsonException("Native session cache response detail must be text when present.");
                detail = reason.GetString();
            }
            LogSessionCache(_logger, operation, (status ?? "unknown") + (detail is null ? "" : ": " + detail), null);
            if (status == "saved" && interruptedPromptTokens is > 0
                && result.RootElement.TryGetProperty("generatedTail", out var tail) && tail.ValueKind == JsonValueKind.String)
                generatedTail = tail.GetString();
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException)
        {
            LogSessionCache(_logger, operation, "unavailable", exception);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return generatedTail;
    }

    private static readonly Action<ILogger, string, string, Exception?> LogSessionCache = LoggerMessage.Define<string, string>(
        LogLevel.Information, new EventId(4110, "NativeSessionCache"), "Native session cache {Operation}: {Status}.");

    private async Task<LmChatResult> CompleteStreamingChatWithBoundedRetryAsync(
        Dictionary<string, object?> body,
        IReadOnlyDictionary<string, string> transportToolNames,
        Func<ModelRuntimeProgress, CancellationToken, ValueTask>? nativeProgress,
        bool structuredToolOnly,
        int contextLength,
        int? maximumOutputTokens,
        CancellationToken cancellationToken,
        string? completionEndpoint = null)
    {
        Exception? last = null;
        string? protocolRepairTool = null;
        double tokenCountingMilliseconds = 0;
        const int maximumAttempts = 3;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            var requestPhase = "token_counting";
            try
            {
                var unbudgetedBody = protocolRepairTool is null
                    ? body
                    : CreateToolProtocolRepairBody(body, protocolRepairTool);
                var countingClock = Stopwatch.StartNew();
                Dictionary<string, object?> requestBody;
                try { requestBody = await ApplyTokenBudgetAsync(unbudgetedBody, contextLength, maximumOutputTokens, nativeProgress, cancellationToken).ConfigureAwait(false); }
                finally { tokenCountingMilliseconds += countingClock.Elapsed.TotalMilliseconds; }
                requestPhase = "generation";
                var requestClock = Stopwatch.StartNew();
                using var response = await SendJsonAsync(
                    HttpMethod.Post,
                    completionEndpoint ?? "v1/chat/completions",
                    requestBody,
                    cancellationToken).ConfigureAwait(false);
                var result = await ParseStreamingChatResponseAsync(
                    response,
                    transportToolNames,
                    nativeProgress,
                    structuredToolOnly,
                    body.TryGetValue("parallel_tool_calls", out var parallelCalls) && parallelCalls is true
                        ? CodingRunBudget.MaximumNativeCallsPerTurn : 1,
                    requestClock, cancellationToken,
                    new ReasoningLoopGuard(TimeSpan.FromMinutes(Math.Clamp(_options.ReasoningOnlyTimeoutMinutes, 1, 1440)))).ConfigureAwait(false);
                return result with { Metrics = (result.Metrics ?? new ModelTurnMetrics()) with { TokenCountingMilliseconds = tokenCountingMilliseconds } };
            }
            catch (ReasoningLoopDetectedException exception)
            {
                if (nativeProgress is not null)
                    await nativeProgress(new ModelRuntimeProgress("reasoningGuardStopped",
                        FailureKind: exception.FailureKind, ContentCharacters: exception.ReasoningCharacters),
                        CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (Exception exception) when (
                IsTransientInferenceFailure(exception)
                && !cancellationToken.IsCancellationRequested)
            {
                last = exception;
                var incomplete = exception as IncompleteStreamingChatException;
                var snapshot = incomplete?.Snapshot ?? StreamingAttemptSnapshot.Empty;
                var failureKind = incomplete?.FailureKind ?? "transport";
                protocolRepairTool = string.Equals(failureKind, "invalid_tool_json", StringComparison.Ordinal)
                    ? snapshot.ToolName ?? "das ausgewählte Werkzeug"
                    : null;
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
                    if (incomplete is null) throw new ModelProviderRequestException(requestPhase, maximumAttempts, exception);
                    throw new ModelGenerationTerminatedException("transport_retry_exhausted", exception);
                }
                LogInferenceRetry(_logger, exception);
                await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
        throw new ModelGenerationTerminatedException("transport_retry_exhausted", last);
    }

    internal static IReadOnlyDictionary<string, object?> CreateToolProtocolRepairBody(
        IReadOnlyDictionary<string, object?> body,
        string toolName)
    {
        var repaired = body.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        if (!repaired.TryGetValue("messages", out var value)
            || value is not System.Collections.IEnumerable source)
        {
            return repaired;
        }

        var messages = new List<object>();
        foreach (var item in source)
        {
            if (item is not null)
            {
                messages.Add(item);
            }
        }
        messages.Add(new
        {
            role = "system",
            content = $"Der vorherige Toolaufruf für '{toolName}' endete vor einem vollständigen JSON-Objekt. Wiederhole genau einen vollständigen und kompakten Toolaufruf. Begrenze große Textargumente auf höchstens 12.000 Unicode-Zeichen und schließe alle JSON-Felder sowie den Toolaufruf vollständig; eine kleinere lauffähige Arbeitsversion ist besser als abgeschnittene Ausgabe.",
        });
        repaired["messages"] = messages.ToArray();
        return repaired;
    }

    internal static async Task<LmChatResult> ParseStreamingChatResponseAsync(
        HttpResponseMessage response,
        IReadOnlyDictionary<string, string> transportToolNames,
        Func<ModelRuntimeProgress, CancellationToken, ValueTask>? nativeProgress,
        bool structuredToolOnly,
        int maximumToolCalls,
        Stopwatch requestClock,
        CancellationToken cancellationToken,
        ReasoningLoopGuard? reasoningGuard = null)
    {
        reasoningGuard ??= new ReasoningLoopGuard();
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                // Some providers ignore stream=true. Async parsing remains bound
                // by the caller's native inactivity deadline, including blocked reads.
                await using var jsonStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(jsonStream, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (document.RootElement.TryGetProperty("choices", out var choices)
                    && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
                    && choices[0].TryGetProperty("message", out var message)
                    && ReadReasoningDelta(message) is { Length: > 0 } reasoning)
                {
                    if (nativeProgress is not null)
                        await nativeProgress(new ModelRuntimeProgress("reasoningDelta", ReasoningDelta: reasoning), cancellationToken).ConfigureAwait(false);
                    reasoningGuard.ObserveReasoning(reasoning, requestClock.Elapsed);
                }
                return ParseChatResult(document.RootElement, transportToolNames, structuredToolOnly, maximumToolCalls);
            }
            catch (JsonException exception)
            {
                throw new IncompleteStreamingChatException(
                    "invalid_response_json",
                    StreamingAttemptSnapshot.Empty,
                    exception);
            }
        }

        using var reasoningDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var readToken = reasoningDeadline.Token;
        await using var stream = await response.Content.ReadAsStreamAsync(readToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: false);
        var accumulator = new StreamingChatAccumulator(structuredToolOnly, maximumToolCalls, requestClock);
        var eventData = new StringBuilder();
        var progressClock = Stopwatch.StartNew();
        var reasoningClock = Stopwatch.StartNew();
        var pendingReasoning = new StringBuilder();
        var reasoningReported = false;
        var lastReportedFragments = 0;

        try
        {
            while (await reader.ReadLineAsync(readToken).ConfigureAwait(false) is { } line)
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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && reasoningDeadline.IsCancellationRequested)
        {
            throw reasoningGuard.WatchdogFailure();
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException
            && exception is not OutOfMemoryException
            && exception is not ReasoningLoopDetectedException
            && exception is not IncompleteStreamingChatException)
        {
            throw new IncompleteStreamingChatException(
                exception is JsonException ? "invalid_sse_json" : "stream_read_error",
                accumulator.CreateSnapshot(transportToolNames),
                exception);
        }
        finally
        {
            // Preserve the tail even when the provider ends, disconnects or is
            // cancelled before the next display interval. This is journal data,
            // not another inference operation.
            await FlushReasoningAsync(CancellationToken.None).ConfigureAwait(false);
        }

        if (!accumulator.Done && !accumulator.FinishObserved)
        {
            // native llama can close its engine channel after it has already sent a
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
        try
        {
            return accumulator.Build(transportToolNames);
        }
        catch (JsonException exception)
        {
            throw new IncompleteStreamingChatException(
                "invalid_tool_json",
                accumulator.CreateSnapshot(transportToolNames),
                exception);
        }

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
            if (nativeProgress is not null
                && chunk.RootElement.TryGetProperty("prompt_progress", out var promptProgress)
                && promptProgress.ValueKind == JsonValueKind.Object)
            {
                var total = promptProgress.TryGetProperty("total", out var totalValue) && totalValue.TryGetInt32(out var totalTokens) ? totalTokens : 0;
                var processed = promptProgress.TryGetProperty("processed", out var processedValue) && processedValue.TryGetInt32(out var processedTokens) ? processedTokens : 0;
                int? cached = promptProgress.TryGetProperty("cache", out var cacheValue) && cacheValue.TryGetInt32(out var cachedTokens) && cachedTokens >= 0 ? cachedTokens : null;
                await nativeProgress(new ModelRuntimeProgress("promptProcessing",
                    PromptProgress: total > 0 ? Math.Clamp((double)processed / total, 0, 1) : null,
                    PromptTokens: total > 0 ? total : null,
                    ProcessedPromptTokens: processed, CachedPromptTokens: cached), cancellationToken).ConfigureAwait(false);
            }
            var contentDelta = accumulator.Add(chunk.RootElement);
            // Capture every provider fragment, but limit durable UI updates to a
            // readable cadence. Token-progress throttling must never discard text.
            if (nativeProgress is not null && accumulator.LastReasoningDelta is { Length: > 0 } reasoningDelta)
            {
                pendingReasoning.Append(reasoningDelta);
            }
            if (accumulator.LastReasoningDelta is { Length: > 0 } observedReasoning)
                reasoningGuard.ObserveReasoning(observedReasoning, requestClock.Elapsed);
            reasoningGuard.ObserveResponseProgress(contentDelta);
            reasoningGuard.ObserveResponseProgress(accumulator.LastToolDelta);
            if (reasoningGuard.Remaining(requestClock.Elapsed) is { } remaining)
            {
                reasoningGuard.ThrowIfWatchdogExpired(requestClock.Elapsed);
                // The timer interrupts a silent/blocked stream as well as an
                // actively reasoning model. Empty events cannot extend it.
                reasoningDeadline.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1));
            }
            else reasoningDeadline.CancelAfter(Timeout.InfiniteTimeSpan);
            if (!reasoningReported || reasoningClock.Elapsed >= TimeSpan.FromMilliseconds(180)
                || !string.IsNullOrEmpty(contentDelta) || accumulator.FinishObserved)
                await FlushReasoningAsync(cancellationToken).ConfigureAwait(false);
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

        async Task FlushReasoningAsync(CancellationToken token)
        {
            if (nativeProgress is null || pendingReasoning.Length == 0) return;
            var delta = pendingReasoning.ToString();
            pendingReasoning.Clear();
            reasoningReported = true;
            reasoningClock.Restart();
            await nativeProgress(new ModelRuntimeProgress("reasoningDelta", ReasoningDelta: delta), token).ConfigureAwait(false);
        }
    }

    private static string? ReadReasoningDelta(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object) return null;
        // Native llama.cpp normally uses reasoning_content. Some compatible
        // templates use reasoning instead; prefer one field to avoid duplication.
        if (message.TryGetProperty("reasoning_content", out var content)
            && content.ValueKind == JsonValueKind.String && content.GetString() is { Length: > 0 } text)
            return text;
        return message.TryGetProperty("reasoning", out var alternate) && alternate.ValueKind == JsonValueKind.String
            ? alternate.GetString() : null;
    }

    private sealed class StreamingChatAccumulator(bool structuredToolOnly, int maximumToolCalls, Stopwatch requestClock)
    {
        private readonly ModelTurnMeasurement _measurement = new(requestClock);
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
        public string? LastReasoningDelta { get; private set; }
        public string? LastToolDelta { get; private set; }

        public StreamingAttemptSnapshot CreateSnapshot(IReadOnlyDictionary<string, string> transportToolNames)
        {
            var first = _toolCalls.OrderBy(static item => item.Key).Select(static item => item.Value).FirstOrDefault();
            var transportName = first?.Name.ToString();
            var logicalName = string.IsNullOrWhiteSpace(transportName)
                ? null
                : TryResolveTransportToolName(transportName, transportToolNames, out var resolvedName)
                    ? resolvedName
                    : transportName;
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
            LastReasoningDelta = null;
            LastToolDelta = null;
            _measurement.Observe(root);
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
            if (!string.IsNullOrEmpty(contentDelta)) _measurement.ObserveGeneratedFragment();
            if (ReadReasoningDelta(delta) is { Length: > 0 } reasoningText
                && (_reasoningContent.Length > 0 || !string.IsNullOrWhiteSpace(reasoningText)))
            {
                LastReasoningDelta = reasoningText;
                _reasoningContent.Append(reasoningText);
                HadReasoning = true;
                GeneratedFragments++;
                _measurement.ObserveGeneratedFragment();
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
                var nameFragment = AppendString(function, "name", target.Name);
                var argumentsFragment = AppendString(function, "arguments", target.Arguments);
                LastToolDelta += nameFragment + argumentsFragment;
                if (!string.IsNullOrEmpty(nameFragment) || !string.IsNullOrEmpty(argumentsFragment)) _measurement.ObserveGeneratedFragment();
            }
            return contentDelta;
        }

        public LmChatResult Build(IReadOnlyDictionary<string, string> transportToolNames)
        {
            if (_toolCalls.Count > maximumToolCalls)
            {
                throw new JsonException($"Der Modellturn darf höchstens {maximumToolCalls} Toolaufrufe liefern.");
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
                var logicalName = TryResolveTransportToolName(name, transportToolNames, out var resolvedName)
                    ? resolvedName
                    : name;
                calls.Add(new LmToolCall(
                    string.IsNullOrWhiteSpace(item.Id) ? $"call-{Guid.NewGuid():N}" : item.Id,
                    logicalName,
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
                ReasoningTokens, _measurement.Build(), ReasoningContent: _reasoningContent.ToString());
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
                    ReasoningTokens, _measurement.Build(), ReasoningContent: _reasoningContent.ToString());
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
            $"native llama beendete den Streaming-Turn unvollständig ({failureKind}).",
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
        CancellationToken cancellationToken,
        bool bufferContent = false)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = bufferContent
                ? new StringContent(JsonSerializer.Serialize(body, _json), Encoding.UTF8, "application/json")
                : JsonContent.Create(body, options: _json),
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
                $"Windows llama.cpp returned HTTP {(int)response.StatusCode}: {detail}",
                null,
                response.StatusCode);
        }
        return response;
    }

    private static LmChatResult ParseChatResult(
        JsonElement root,
        IReadOnlyDictionary<string, string> transportToolNames,
        bool structuredToolOnly = false,
        int maximumToolCalls = 1)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            throw new JsonException("Die native llama-Antwort enthält keine Auswahl.");
        }
        var message = choices[0].GetProperty("message");
        var calls = new List<LmToolCall>();
        if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
        {
            if (toolCalls.GetArrayLength() > maximumToolCalls)
            {
                throw new JsonException($"Der Modellturn darf höchstens {maximumToolCalls} Toolaufrufe liefern.");
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
                var logicalName = TryResolveTransportToolName(name, transportToolNames, out var resolvedName)
                    ? resolvedName
                    : name;
                calls.Add(new LmToolCall(
                    item.TryGetProperty("id", out var id) && !string.IsNullOrWhiteSpace(id.GetString())
                        ? id.GetString()!
                        : $"call-{Guid.NewGuid():N}",
                    logicalName,
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
        var reasoningContent = ReadReasoningDelta(message);
        var hasReasoningContent = !string.IsNullOrWhiteSpace(reasoningContent);
        if (calls.Count == 0
            && string.IsNullOrWhiteSpace(content)
            && hasReasoningContent
            && TryParseReasoningToolCall(
                reasoningContent!,
                transportToolNames,
                out var reasoningCall))
        {
            calls.Add(reasoningCall);
        }
        var measurement = new ModelTurnMeasurement(Stopwatch.StartNew());
        measurement.Observe(root);
        return new LmChatResult(
            structuredToolOnly ? null : content,
            calls,
            inputTokens,
            outputTokens,
            hasReasoningContent || reasoningTokens > 0,
            reasoningTokens, measurement.Build(), ReasoningContent: reasoningContent);
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
        // The same arguments in a later turn are a new operation. A content hash
        // would merge its evidence with an earlier success/failure after compaction.
        call = new LmToolCall($"call-reasoning-{Guid.NewGuid():N}", logicalName, arguments);
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
        return TryResolveTransportToolName(generatedName, transportToolNames, out logicalName);
    }

    internal static bool TryResolveTransportToolName(
        string generatedName,
        IReadOnlyDictionary<string, string> transportToolNames,
        out string logicalName)
    {
        logicalName = string.Empty;
        if (string.IsNullOrWhiteSpace(generatedName))
        {
            return false;
        }
        if (transportToolNames.TryGetValue(generatedName, out logicalName!))
        {
            return true;
        }

        var logicalMatches = transportToolNames.Values
            .Where(candidate => string.Equals(candidate, generatedName, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        if (logicalMatches.Length == 1)
        {
            logicalName = logicalMatches[0];
            return true;
        }

        // Some native llama chat templates expose the complete registered function
        // name to the model but emit the readable part without GO's collision
        // hash. Accept that normalization only when it identifies one schema
        // unambiguously; otherwise the ordinary catalog validation must reject it.
        var aliasMatches = transportToolNames
            .Where(pair => string.Equals(
                RemoveTransportHashSuffix(pair.Key),
                generatedName,
                StringComparison.Ordinal))
            .Select(static pair => pair.Value)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        if (aliasMatches.Length == 1)
        {
            logicalName = aliasMatches[0];
            return true;
        }

        return false;
    }

    private static string RemoveTransportHashSuffix(string transportName)
    {
        var separator = transportName.LastIndexOf('_');
        if (separator <= 0 || transportName.Length - separator - 1 != 8)
        {
            return transportName;
        }
        var suffix = transportName[(separator + 1)..];
        return suffix.All(static character => char.IsAsciiHexDigit(character))
            ? transportName[..separator]
            : transportName;
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
                content = message.Content ?? string.Empty,
                reasoning_content = message.ReasoningContent ?? string.Empty,
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
        if (message.Role == "assistant" && !string.IsNullOrEmpty(message.ReasoningContent))
            return new { role = "assistant", content = message.Content ?? string.Empty, reasoning_content = message.ReasoningContent };
        return new { role = NormalizeRole(message.Role), content = message.Content ?? string.Empty };
    }

    internal static IReadOnlyList<LmChatMessage> NormalizeMessageOrderForNativeRuntime(
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

                // Qwen's native llama templates require every system instruction
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
        if (!NativeModelCatalog.TryGet(modelId, out var profile)
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
        var profile = ResolveRuntimeReasoningProfile(modelId, modelRole);
        var explicitEffort = string.Equals(requestedEffort?.Trim(), ModelReasoningProfiles.Automatic, StringComparison.OrdinalIgnoreCase)
            ? null : requestedEffort?.Trim();
        if (!string.IsNullOrWhiteSpace(explicitEffort) && !profile.Supports(explicitEffort))
        {
            var supported = profile.SupportedEfforts.Count == 0
                ? "keine steuerbare Stufe"
                : string.Join(", ", profile.SupportedEfforts);
            throw new InvalidOperationException(
                $"reasoningEffort '{explicitEffort}' wird vom ausgewählten Modell nicht unterstützt ({supported}).");
        }
        var effort = profile.Resolve(explicitEffort);
        if (string.IsNullOrWhiteSpace(effort))
        {
            return;
        }

        if (profile.Family == "llama-toggle")
        {
            body["chat_template_kwargs"] = new Dictionary<string, object?> { ["enable_thinking"] = effort != "none" };
            if (effort == "none") body["reasoning_effort"] = "none";
            return;
        }

        if (string.Equals(profile.Family, "llama-native", StringComparison.Ordinal)
            || string.Equals(profile.Family, ModelReasoningProfiles.GptOssFamily, StringComparison.Ordinal))
        {
            body["reasoning_effort"] = string.Equals(effort, "none", StringComparison.OrdinalIgnoreCase)
                ? "none"
                : effort;
            if (profile.Family == "llama-native")
                body["chat_template_kwargs"] = new Dictionary<string, object?> {
                    ["enable_thinking"] = effort != "none",
                    ["reasoning_strength"] = effort
                };
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

    internal static RuntimeModel[] ReadRuntimeModels(JsonElement root, int textContextLength = 32_768, int visionContextLength = 32_768, int embeddingContextLength = 8_192)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new JsonException("Der native llama-Router lieferte keinen gültigen Modellkatalog.");
        var models = new List<RuntimeModel>();
        foreach (var item in data.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
            if (id is null || !(id.StartsWith("coding/", StringComparison.Ordinal) || id.StartsWith("vision/", StringComparison.Ordinal) || id.StartsWith("embedding/", StringComparison.Ordinal))) continue;
            var embedding = id.StartsWith("embedding/", StringComparison.Ordinal);
            var vision = id.StartsWith("vision/", StringComparison.Ordinal)
                || (item.TryGetProperty("tags", out var visionTags) && visionTags.ValueKind == JsonValueKind.Array
                    && visionTags.EnumerateArray().Any(tag => tag.ValueKind == JsonValueKind.String && tag.GetString() == "go-vision:projector"));
            var fallback = embedding ? embeddingContextLength : vision ? visionContextLength : textContextLength;
            var context = ReadNominalContextLength(item, id, fallback);
            var state = "unloaded";
            if (item.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Object)
            {
                state = status.TryGetProperty("failed", out var failed) && failed.ValueKind == JsonValueKind.True ? "failed"
                    : status.TryGetProperty("value", out var value) ? value.GetString() ?? "unloaded" : "unloaded";
            }
            var name = id[(id.IndexOf('/') + 1)..];
            var hash = name.LastIndexOf('~');
            if (hash > 0) name = name[..hash];
            var reasoning = embedding ? new ModelReasoningProfile(ModelReasoningProfiles.UnknownFamily, ["none"], "none")
                : ReadReasoningMetadata(item) ?? ModelReasoningProfiles.Resolve(id, vision ? "vision" : "general");
            models.Add(new RuntimeModel(id, embedding ? "embedding" : "llm", state, state is "loaded" or "sleeping" ? id : null,
                context, state is "loaded" or "sleeping" ? context : 0, name, null, null, !embedding, vision, reasoning.SupportedEfforts, reasoning.DefaultEffort, reasoning.Family,
                item.TryGetProperty("tags", out var gpuTags) && gpuTags.ValueKind == JsonValueKind.Array
                    && gpuTags.EnumerateArray().Any(tag => tag.ValueKind == JsonValueKind.String && tag.GetString() == "go-gpu-policy:single-preferred-v1")));
        }
        return [.. models];
    }

    private static RuntimeModel? ResolveInstalledModel(IReadOnlyList<RuntimeModel> models, string requestedId) =>
        models.FirstOrDefault(model => string.Equals(model.Id, requestedId, StringComparison.OrdinalIgnoreCase))
        ?? models.Where(model => LegacyModelMatches(model.Id, requestedId)).OrderByDescending(static model => model.State is "loaded" or "sleeping").ThenBy(static model => model.Id, StringComparer.Ordinal).FirstOrDefault();

    internal static ModelRuntimeStatus? ResolveModelStatus(IReadOnlyList<ModelRuntimeStatus> models, string requestedId, string role) =>
        models.Where(model => model.Downloaded && string.Equals(model.Role, role, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(model => string.Equals(model.Id, requestedId, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(static model => model.Loaded)
            .ThenBy(static model => model.Id, StringComparer.Ordinal)
            .FirstOrDefault(model => string.Equals(model.Id, requestedId, StringComparison.OrdinalIgnoreCase) || LegacyModelMatches(model.Id, requestedId));

    private static bool LegacyModelMatches(string installedId, string requestedId)
    {
        // An explicit native preset ID is exact: never silently switch its model or quantization.
        if (requestedId.StartsWith("coding/", StringComparison.OrdinalIgnoreCase) || requestedId.StartsWith("vision/", StringComparison.OrdinalIgnoreCase) || requestedId.StartsWith("embedding/", StringComparison.OrdinalIgnoreCase)) return false;
        var alias = requestedId[(requestedId.LastIndexOf('/') + 1)..].ToLowerInvariant().Replace("text-embedding-", "", StringComparison.Ordinal);
        var name = installedId[(installedId.IndexOf('/') + 1)..];
        var hash = name.LastIndexOf('~');
        if (hash > 0) name = name[..hash];
        static string Normalize(string value) => string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        var expected = Normalize(alias);
        if (expected.Length < 4 || !Normalize(name).Contains(expected, StringComparison.Ordinal)) return false;
        var desiredPrefix = requestedId.Contains("embedding", StringComparison.OrdinalIgnoreCase) || expected == "bgem3" ? "embedding/"
            : requestedId.Contains("-vl-", StringComparison.OrdinalIgnoreCase) ? "vision/" : "coding/";
        return installedId.StartsWith(desiredPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static ModelRuntimeStatus[] BuildRuntimeStatuses(RuntimeModel[] runtimeModels)
    {
        var statuses = new List<ModelRuntimeStatus>(runtimeModels.Length * 2);
        foreach (var model in runtimeModels)
        {
            var context = model.LoadedContextLength > 0 ? model.LoadedContextLength
                : model.MaximumContextLength > 0 ? model.MaximumContextLength : 2_048;
            var displayName = string.IsNullOrWhiteSpace(model.Quantization)
                ? model.DisplayName ?? model.Id
                : $"{model.DisplayName ?? model.Id} · {model.Quantization}";
            var loaded = model.State is "loaded" or "sleeping";
            if (string.Equals(model.Type, "embedding", StringComparison.OrdinalIgnoreCase))
            {
                statuses.Add(CreateRuntimeStatus(model, "embedding", context, displayName, loaded));
                continue;
            }

            if (model.SupportsVision) statuses.Add(CreateRuntimeStatus(model, "vision", context, displayName, loaded));
            if (!model.Id.StartsWith("vision/", StringComparison.Ordinal))
            {
                statuses.Add(CreateRuntimeStatus(model, "general", context, displayName, loaded));
                statuses.Add(CreateRuntimeStatus(model, "coding", context, displayName, loaded));
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
        lock (_cacheLock) { model = ResolveInstalledModel(_runtimeCatalog.Values.ToArray(), modelId)!; return model is not null; }
    }

    private ModelReasoningProfile ResolveRuntimeReasoningProfile(string modelId, string role) =>
        TryGetRuntimeModel(modelId, out var model)
            ? ResolveRuntimeReasoningProfile(model, role)
            : ModelReasoningProfiles.Resolve(modelId, role);

    private static ModelReasoningProfile ResolveRuntimeReasoningProfile(RuntimeModel model, string role) =>
        new(model.ReasoningFamily ?? ModelReasoningProfiles.Resolve(model.Id, role).Family,
            model.ReasoningEfforts, model.DefaultReasoningEffort);

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

    private sealed class TurnLease(SemaphoreSlim first)
    {
        public void Release() { first.Release(); }
    }

    private async Task<TurnLease> AcquireTurnAsync(string? modelId, CancellationToken token)
    {
        await _turnGate.WaitAsync(token);
        return new TurnLease(_turnGate);
    }

    private static Uri EnsureTrailingSlash(Uri uri) => uri.AbsoluteUri.EndsWith('/')
        ? uri
        : new Uri(uri.AbsoluteUri + "/", UriKind.Absolute);

    public void Dispose()
    {
        _modelGate.Dispose();
        _turnGate.Dispose();
    }

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
        string? DefaultReasoningEffort,
        string? ReasoningFamily = null,
        bool ManagedGpuPlacement = false);
}
