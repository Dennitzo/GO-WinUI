using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace GoAi.Server.Core.Models;

/// <summary>
/// Connects the gateway to the private llama.cpp router. GO remains the only
/// tool executor; this runtime produces text, embeddings and native tool calls.
/// </summary>
public sealed class ModelRuntimeClient : IDisposable
{
    private static readonly Action<ILogger, Exception?> LogRouterUnavailable = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(4101, "ModelRouterUnavailable"),
        "The private llama.cpp model router is unavailable.");
    private static readonly Action<ILogger, Exception?> LogInferenceRetry = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(4102, "ModelInferenceRetry"),
        "Transient llama.cpp inference failure; retrying once before any tool is executed.");
    private static readonly TimeSpan StatusCacheDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ModelLoadTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ModelTurnTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan ModelTransitionPollInterval = TimeSpan.FromMilliseconds(500);
    private readonly HttpClient _httpClient;
    private readonly GoAiServerOptions _options;
    private readonly ILogger<ModelRuntimeClient> _logger;
    private readonly SemaphoreSlim _modelGate = new(1, 1);
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly object _cacheLock = new();
    private ModelStatusSnapshot? _cachedStatus;
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
            using var response = await _httpClient.GetAsync("models", cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
            var runtimeModels = ReadRuntimeModels(document.RootElement);
            var models = ConfiguredModels().Select(definition =>
            {
                var runtime = runtimeModels.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, definition.Id, StringComparison.OrdinalIgnoreCase));
                var state = runtime?.State ?? "unloaded";
                return new ModelRuntimeStatus(
                    definition.Id,
                    definition.Role,
                    runtime is not null,
                    string.Equals(state, "loaded", StringComparison.OrdinalIgnoreCase),
                    state,
                    definition.ContextTokens,
                    definition.DisplayName);
            }).ToArray();
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
                ConfiguredModels().Select(definition => new ModelRuntimeStatus(
                    definition.Id,
                    definition.Role,
                    false,
                    false,
                    "unreachable",
                    definition.ContextTokens,
                    definition.DisplayName)).ToArray(),
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
        var definition = ConfiguredModels().FirstOrDefault(candidate =>
            string.Equals(candidate.Id, modelId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Das konfigurierte Modell '{modelId}' ist im Docker-Modellkatalog nicht vorhanden.");
        if (contextLength > definition.ContextTokens)
        {
            throw new ModelContextLengthException(modelId, contextLength, definition.ContextTokens);
        }

        await _modelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ModelLoadTimeout);
            var operationToken = timeout.Token;
            var status = await GetUncachedStatusAsync(operationToken).ConfigureAwait(false);
            var selected = status.Models.First(candidate =>
                string.Equals(candidate.Id, modelId, StringComparison.OrdinalIgnoreCase));
            if (!selected.Downloaded)
            {
                throw new FileNotFoundException(
                    $"Das gepinnte Docker-Modell '{modelId}' ist unter /models nicht installiert.");
            }
            if (selected.Loaded)
            {
                return new ModelPreparation(modelId, WasAlreadyLoaded: true);
            }

            if (loadingStarted is not null)
            {
                await loadingStarted(cancellationToken).ConfigureAwait(false);
            }
            var loadAlreadyInProgress = string.Equals(
                selected.State,
                "loading",
                StringComparison.OrdinalIgnoreCase);
            if (!loadAlreadyInProgress)
            {
                var competingModels = status.Models
                    .Where(candidate => !string.Equals(
                        candidate.Id,
                        modelId,
                        StringComparison.OrdinalIgnoreCase))
                    .Where(ShouldRequestModelUnload)
                    .Select(candidate => candidate.Id)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                foreach (var loaded in competingModels)
                {
                    await PostModelOperationAsync("models/unload", loaded, operationToken).ConfigureAwait(false);
                }
                await WaitForModelSlotsToBeReleasedAsync(competingModels, operationToken).ConfigureAwait(false);

                await PostModelOperationAsync("models/load", modelId, operationToken).ConfigureAwait(false);
            }
            InvalidateStatus();
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), operationToken).ConfigureAwait(false);
                var refreshed = await GetUncachedStatusAsync(operationToken).ConfigureAwait(false);
                var current = refreshed.Models.First(candidate =>
                    string.Equals(candidate.Id, modelId, StringComparison.OrdinalIgnoreCase));
                if (current.Loaded)
                {
                    return new ModelPreparation(modelId, WasAlreadyLoaded: false);
                }
                if (string.Equals(current.State, "failed", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"llama.cpp konnte das Modell '{modelId}' nicht laden.");
                }
            }
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
            var status = await GetUncachedStatusAsync(cancellationToken).ConfigureAwait(false);
            if (!status.Models.Any(candidate => IsModelSlotOccupied(candidate)
                    && string.Equals(candidate.Id, modelId, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
            await PostModelOperationAsync("models/unload", modelId, cancellationToken).ConfigureAwait(false);
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
            var status = await GetUncachedStatusAsync(cancellationToken).ConfigureAwait(false);
            foreach (var model in status.Models
                         .Where(candidate => ShouldRequestModelUnload(candidate) && !preserved.Contains(candidate.Id))
                         .Select(candidate => candidate.Id)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                await PostModelOperationAsync("models/unload", model, cancellationToken).ConfigureAwait(false);
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
        Func<ModelRuntimeProgress, CancellationToken, ValueTask>? nativeProgress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateToolChoice(tools, requireToolCall, requiredToolName);
        var context = string.Equals(modelRole, "code", StringComparison.OrdinalIgnoreCase)
            && CodingModelCatalog.TryGet(modelId, out var codingProfile)
                ? codingProfile.ContextLength
                : _options.GeneralContextLength;
        await EnsureModelLoadedAsync(modelId, context, cancellationToken).ConfigureAwait(false);
        await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (nativeProgress is not null)
            {
                await nativeProgress(new ModelRuntimeProgress("generationStarted"), cancellationToken).ConfigureAwait(false);
            }

            var body = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["model"] = modelId,
                ["messages"] = messages.Select(ToOpenAiMessage).ToArray(),
                ["stream"] = false,
                ["max_tokens"] = Math.Clamp(maximumOutputTokens, 1, 65_536),
                ["parallel_tool_calls"] = false,
            };
            ApplyModelSampling(body, modelId);
            var effort = ModelReasoningProfiles.ResolveEffort(modelId, modelRole, reasoningEffort);
            if (!string.IsNullOrWhiteSpace(effort))
            {
                body["reasoning_effort"] = effort;
            }
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
                // llama.cpp accepts the string choices auto/required/none. In the
                // second catalog stage only the selected schema is present, so
                // "required" deterministically selects that one tool without the
                // unsupported OpenAI object-shaped tool_choice form.
                body["tool_choice"] = requireToolCall || requiredToolName is { Length: > 0 }
                    ? "required"
                    : "auto";
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ModelTurnTimeout);
            using var progressCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var progressTask = nativeProgress is null
                ? Task.CompletedTask
                : MonitorInferenceProgressAsync(modelId, nativeProgress, progressCancellation.Token);
            try
            {
                using var response = await SendInferenceWithSingleRetryAsync(body, timeout.Token).ConfigureAwait(false);
                using var document = JsonDocument.Parse(
                    await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false));
                var result = ParseChatResult(document.RootElement);
                if (nativeProgress is not null && result.ToolCalls.Count > 0)
                {
                    var call = result.ToolCalls[0];
                    await nativeProgress(
                        new ModelRuntimeProgress("toolSelected", call.Name, call.Arguments.GetRawText().Length),
                        cancellationToken).ConfigureAwait(false);
                }
                return result;
            }
            finally
            {
                progressCancellation.Cancel();
                try
                {
                    await progressTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (progressCancellation.IsCancellationRequested)
                {
                }
            }
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
        await EnsureModelLoadedAsync(modelId, _options.EmbeddingContextLength, cancellationToken).ConfigureAwait(false);
        using var response = await SendJsonAsync(
            HttpMethod.Post,
            "v1/embeddings",
            new { model = modelId, input = inputs },
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
        await EnsureModelLoadedAsync(modelId, _options.VisionContextLength, cancellationToken).ConfigureAwait(false);
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
                model = modelId,
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

    private async Task<ModelStatusSnapshot> GetUncachedStatusAsync(CancellationToken cancellationToken)
    {
        InvalidateStatus();
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!status.ProviderReachable)
        {
            throw new HttpRequestException("Der private llama.cpp-Modellrouter ist nicht erreichbar.");
        }
        return status;
    }

    private async Task PostModelOperationAsync(string path, string modelId, CancellationToken cancellationToken)
    {
        using var response = await SendJsonAsync(
            HttpMethod.Post,
            path,
            new { model = modelId },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task WaitForModelSlotsToBeReleasedAsync(
        string[] modelIds,
        CancellationToken cancellationToken)
    {
        if (modelIds.Length == 0)
        {
            return;
        }

        var pending = new HashSet<string>(modelIds, StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var status = await GetUncachedStatusAsync(cancellationToken).ConfigureAwait(false);
            if (!status.Models.Any(candidate =>
                    pending.Contains(candidate.Id)
                    && IsModelSlotOccupied(candidate)))
            {
                return;
            }

            await Task.Delay(ModelTransitionPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool ShouldRequestModelUnload(ModelRuntimeStatus model) =>
        model.Loaded
        || string.Equals(model.State, "loading", StringComparison.OrdinalIgnoreCase);

    private static bool IsModelSlotOccupied(ModelRuntimeStatus model) =>
        ShouldRequestModelUnload(model)
        || string.Equals(model.State, "unloading", StringComparison.OrdinalIgnoreCase);

    private async Task<HttpResponseMessage> SendInferenceWithSingleRetryAsync(
        object body,
        CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                return await SendJsonAsync(
                    HttpMethod.Post,
                    "v1/chat/completions",
                    body,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                attempt == 1
                && exception is HttpRequestException or IOException
                && !cancellationToken.IsCancellationRequested)
            {
                last = exception;
                LogInferenceRetry(_logger, exception);
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
        }
        throw new ModelGenerationTerminatedException("transport_retry_exhausted", last);
    }

    private async Task MonitorInferenceProgressAsync(
        string modelId,
        Func<ModelRuntimeProgress, CancellationToken, ValueTask> progress,
        CancellationToken cancellationToken)
    {
        var previousPromptTokens = -1;
        var previousProcessedPromptTokens = -1;
        var previousGeneratedTokens = -1;
        var previousGeneratedAt = Stopwatch.GetTimestamp();
        await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await _httpClient.GetAsync(
                    $"slots?model={Uri.EscapeDataString(modelId)}",
                    cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    using var document = JsonDocument.Parse(
                        await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
                    var sample = ReadActiveSlotProgress(document.RootElement);
                    if (sample is not null
                        && (sample.PromptTokens != previousPromptTokens
                            || sample.ProcessedPromptTokens != previousProcessedPromptTokens
                            || sample.GeneratedTokens != previousGeneratedTokens))
                    {
                        double? tokensPerSecond = null;
                        var now = Stopwatch.GetTimestamp();
                        if (previousGeneratedTokens >= 0
                            && sample.GeneratedTokens is { } generated
                            && generated > previousGeneratedTokens)
                        {
                            var elapsedSeconds = Stopwatch.GetElapsedTime(previousGeneratedAt, now).TotalSeconds;
                            if (elapsedSeconds > 0)
                            {
                                tokensPerSecond = (generated - previousGeneratedTokens) / elapsedSeconds;
                            }
                        }

                        previousPromptTokens = sample.PromptTokens ?? -1;
                        previousProcessedPromptTokens = sample.ProcessedPromptTokens ?? -1;
                        previousGeneratedTokens = sample.GeneratedTokens ?? -1;
                        previousGeneratedAt = now;
                        await progress(sample with { TokensPerSecond = tokensPerSecond }, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }
            catch (Exception exception) when (
                (exception is not OperationCanceledException and not OutOfMemoryException)
                && !cancellationToken.IsCancellationRequested)
            {
                // Progress is advisory. A temporarily unavailable /slots endpoint
                // must never fail or retry the actual model turn.
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    internal static ModelRuntimeProgress? ReadActiveSlotProgress(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var slot in root.EnumerateArray())
        {
            if (!slot.TryGetProperty("is_processing", out var processing)
                || processing.ValueKind != JsonValueKind.True)
            {
                continue;
            }

            var contextTokens = ReadOptionalInt(slot, "n_prompt_tokens");
            var processedPromptTokens = ReadOptionalInt(slot, "n_prompt_tokens_processed");
            var cachedPromptTokens = ReadOptionalInt(slot, "n_prompt_tokens_cache") ?? 0;
            int? generatedTokens = null;
            if (slot.TryGetProperty("next_token", out var nextToken)
                && nextToken.ValueKind == JsonValueKind.Array
                && nextToken.GetArrayLength() > 0)
            {
                generatedTokens = ReadOptionalInt(nextToken[0], "n_decoded");
            }

            // llama.cpp's n_prompt_tokens grows with n_decoded once generation
            // starts. Subtracting the decoded output keeps the displayed prompt
            // size stable and aligned with usage.prompt_tokens.
            int? promptTokens = contextTokens is { } total
                ? Math.Max(0, total - Math.Max(0, generatedTokens ?? 0))
                : null;
            if (promptTokens is > 0 && processedPromptTokens is >= 0 && cachedPromptTokens > 0)
            {
                processedPromptTokens = Math.Min(promptTokens.Value, processedPromptTokens.Value + cachedPromptTokens);
            }

            // llama.cpp logs this counter as `n_tokens` while the prompt is
            // evaluated. Current router builds do not always expose that exact
            // property through /slots, so reconstruct the same monotonically
            // increasing value from evaluated prompt/cache tokens plus decoded
            // output. This keeps non-reasoning models visibly active while
            // n_decoded is still zero.
            var currentTokens = ReadOptionalInt(slot, "n_tokens")
                ?? (processedPromptTokens is { } processed
                    ? Math.Max(0, processed) + Math.Max(0, generatedTokens ?? 0)
                    : contextTokens);

            double? promptProgress = promptTokens is > 0 && processedPromptTokens is >= 0
                ? Math.Clamp(processedPromptTokens.Value / (double)promptTokens.Value, 0d, 1d)
                : null;
            return new ModelRuntimeProgress(
                "tokenProgress",
                PromptProgress: promptProgress,
                PromptTokens: promptTokens,
                ProcessedPromptTokens: processedPromptTokens,
                GeneratedTokens: generatedTokens,
                CurrentTokens: currentTokens);
        }
        return null;
    }

    private static int? ReadOptionalInt(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out var property)
        && property.TryGetInt32(out var result)
            ? result
            : null;

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
                $"llama.cpp returned HTTP {(int)response.StatusCode}: {detail}",
                null,
                response.StatusCode);
        }
        return response;
    }

    private static LmChatResult ParseChatResult(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            throw new JsonException("Die llama.cpp-Antwort enthält keine Auswahl.");
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
                    name,
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
        return new LmChatResult(content, calls, inputTokens, outputTokens, reasoningTokens > 0, reasoningTokens);
    }

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
                    function = new { name = call.Name, arguments = call.Arguments.GetRawText() },
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

    private static string NormalizeRole(string role) => role.ToLowerInvariant() switch
    {
        "system" => "system",
        "assistant" => "assistant",
        "tool" => "tool",
        _ => "user",
    };

    private IReadOnlyList<ModelDefinition> ConfiguredModels() =>
    [
        new(_options.GeneralModelId, "general", _options.GeneralContextLength, "gpt-oss-120b"),
        .. CodingModelCatalog.Models.Select(static profile => new ModelDefinition(
            profile.Id,
            "code",
            profile.ContextLength,
            profile.DisplayName)),
        new(_options.VisionModelId, "vision", _options.VisionContextLength, "Qwen3-VL"),
        new(_options.EmbeddingModelId, "embedding", _options.EmbeddingContextLength, "BGE-M3"),
    ];

    private static RuntimeModel[] ReadRuntimeModels(JsonElement root)
    {
        var array = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                ? data
                : root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array
                    ? models
                    : default;
        if (array.ValueKind != JsonValueKind.Array)
            throw new JsonException("Der llama.cpp-Router lieferte keinen Modellkatalog.");
        return array.EnumerateArray().Select(static item =>
        {
            var id = item.TryGetProperty("id", out var idValue) ? idValue.GetString()
                : item.TryGetProperty("model", out var modelValue) ? modelValue.GetString()
                : null;
            var state = "unloaded";
            if (item.TryGetProperty("status", out var status))
            {
                state = status.ValueKind == JsonValueKind.String
                    ? status.GetString() ?? state
                    : status.TryGetProperty("value", out var value) ? value.GetString() ?? state : state;
            }
            return new RuntimeModel(id ?? string.Empty, state);
        }).Where(static model => model.Id.Length > 0).ToArray();
    }

    private ModelStatusSnapshot Cache(ModelStatusSnapshot status)
    {
        lock (_cacheLock)
        {
            _cachedStatus = status;
            _cacheExpiresAt = DateTimeOffset.UtcNow.Add(StatusCacheDuration);
        }
        return status;
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

    private sealed record ModelDefinition(string Id, string Role, int ContextTokens, string DisplayName);
    private sealed record RuntimeModel(string Id, string State);
}
