using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace GoAi.Server.Core.Models;

internal interface ILmStudioNativeAgentClient
{
    Task<LmStudioModelList> GetModelsAsync(CancellationToken cancellationToken = default);

    Task<LmStudioModelPreparation> LoadModelAsync(
        string modelId,
        int contextLength,
        bool isEmbedding,
        CancellationToken cancellationToken = default);

    Task<int> UnloadModelsAsync(
        IReadOnlyCollection<string> identifiers,
        bool unloadAll,
        IReadOnlyCollection<string>? preserveModelIds = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IReadOnlyList<double>>> CreateEmbeddingsAsync(
        string modelId,
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default);

    Task<string> AnalyzeImagesAsync(
        string modelId,
        string prompt,
        IReadOnlyList<string> imagePaths,
        CancellationToken cancellationToken = default);

    Task<LmChatResult> CompleteAsync(
        LmStudioNativeAgentRequest request,
        CancellationToken cancellationToken = default);
}

internal sealed record LmStudioNativeAgentRequest(
    string ModelId,
    IReadOnlyList<LmChatMessage> Messages,
    IReadOnlyList<LmToolDefinition> Tools,
    int MaximumOutputTokens,
    bool RequireToolCall,
    string? RequiredToolName,
    LmStudioNativeSampling Sampling,
    Func<LmStudioNativeAgentProgress, CancellationToken, ValueTask>? Progress = null);

public sealed record LmStudioNativeAgentProgress(
    string State,
    string? ToolName = null,
    int? ArgumentCharacters = null,
    double? PromptProgress = null);

internal sealed record LmStudioNativeSampling(
    double? Temperature = null,
    double? TopP = null,
    int? TopK = null,
    double? MinP = null,
    double? RepeatPenalty = null);

internal sealed partial class LmStudioNativeAgentClient : ILmStudioNativeAgentClient, IDisposable
{
    private const string WorkerRelativePath = "workers\\lmstudio-native-agent\\worker.cjs";
    private const string SdkRelativePath = "node_modules\\@lmstudio\\sdk\\dist\\index.cjs";
    private readonly GoAiServerOptions _options;
    private readonly DpapiSecretStore _secretStore;
    private readonly ILogger<LmStudioNativeAgentClient> _logger;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private Process? _process;
    private Task? _standardErrorDrain;
    private bool _disposed;

    public LmStudioNativeAgentClient(
        IOptions<GoAiServerOptions> options,
        DpapiSecretStore secretStore,
        ILogger<LmStudioNativeAgentClient> logger)
    {
        _options = options.Value;
        _secretStore = secretStore;
        _logger = logger;
    }

    public async Task<LmStudioModelList> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        var result = await ExecuteCommandAsync(
            "models",
            new Dictionary<string, object?>(),
            cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<LmStudioModelList>(result.GetRawText(), _jsonOptions)
            ?? throw new JsonException("The LM Studio SDK returned no model catalog.");
    }

    public async Task<LmStudioModelPreparation> LoadModelAsync(
        string modelId,
        int contextLength,
        bool isEmbedding,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentOutOfRangeException.ThrowIfLessThan(contextLength, 2_048);
        var result = await ExecuteCommandAsync(
            "loadModel",
            new Dictionary<string, object?>
            {
                ["modelId"] = modelId,
                ["contextLength"] = contextLength,
                ["isEmbedding"] = isEmbedding,
            },
            cancellationToken).ConfigureAwait(false);
        var instanceId = ReadString(result, "instanceId")
            ?? throw new JsonException("The LM Studio SDK returned no model instance identifier.");
        var wasAlreadyLoaded = result.TryGetProperty("wasAlreadyLoaded", out var reused)
            && reused.ValueKind == JsonValueKind.True;
        return new LmStudioModelPreparation(instanceId, wasAlreadyLoaded);
    }

    public async Task<int> UnloadModelsAsync(
        IReadOnlyCollection<string> identifiers,
        bool unloadAll,
        IReadOnlyCollection<string>? preserveModelIds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identifiers);
        var result = await ExecuteCommandAsync(
            "unloadModels",
            new Dictionary<string, object?>
            {
                ["identifiers"] = identifiers,
                ["unloadAll"] = unloadAll,
                ["preserveModelIds"] = preserveModelIds ?? [],
            },
            cancellationToken).ConfigureAwait(false);
        return ReadInt32(result, "unloaded");
    }

    public async Task<IReadOnlyList<IReadOnlyList<double>>> CreateEmbeddingsAsync(
        string modelId,
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(inputs);
        var result = await ExecuteCommandAsync(
            "embeddings",
            new Dictionary<string, object?>
            {
                ["modelId"] = modelId,
                ["inputs"] = inputs,
            },
            cancellationToken).ConfigureAwait(false);
        if (!result.TryGetProperty("embeddings", out var embeddings)
            || embeddings.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("The LM Studio SDK returned no embeddings.");
        }
        return embeddings.EnumerateArray()
            .Select(static embedding => (IReadOnlyList<double>)embedding
                .EnumerateArray()
                .Select(static number => number.GetDouble())
                .ToArray())
            .ToArray();
    }

    public async Task<string> AnalyzeImagesAsync(
        string modelId,
        string prompt,
        IReadOnlyList<string> imagePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentNullException.ThrowIfNull(imagePaths);
        var result = await ExecuteCommandAsync(
            "analyzeImages",
            new Dictionary<string, object?>
            {
                ["modelId"] = modelId,
                ["prompt"] = prompt,
                ["imagePaths"] = imagePaths,
            },
            cancellationToken).ConfigureAwait(false);
        return ReadString(result, "content") is { Length: > 0 } content
            ? content
            : throw new JsonException("The LM Studio SDK vision model returned no text response.");
    }

    public async Task<LmChatResult> CompleteAsync(
        LmStudioNativeAgentRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var token = await _secretStore.ReadLmStudioTokenAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException(
                    "Fuer den nativen LM-Studio-SDK-Kanal ist kein API-Schluessel hinterlegt.");
            }

            var process = EnsureProcess();
            var requestId = Guid.NewGuid().ToString("N");
            var payload = new
            {
                type = "predict",
                id = requestId,
                baseUrl = _options.LmStudioUri.ToString(),
                apiToken = token,
                modelId = request.ModelId,
                messages = request.Messages,
                tools = request.Tools,
                maximumOutputTokens = request.MaximumOutputTokens,
                requireToolCall = request.RequireToolCall,
                requiredToolName = request.RequiredToolName,
                sampling = request.Sampling,
            };
            await process.StandardInput.WriteLineAsync(
                JsonSerializer.Serialize(payload, _jsonOptions).AsMemory(),
                cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);

            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    throw new LmStudioNativeAgentException(
                        "sidecar_exited",
                        "Der native LM-Studio-SDK-Kanal wurde unerwartet beendet.");
                }
                if (!TryParseMessage(line, requestId, out var message))
                {
                    continue;
                }

                var type = ReadString(message, "type");
                switch (type)
                {
                    case "ready":
                        await ReportProgressAsync(
                            request,
                            new LmStudioNativeAgentProgress("sidecarReady"),
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case "requestAccepted":
                    case "clientConnecting":
                    case "clientReady":
                    case "modelReady":
                        await ReportProgressAsync(
                            request,
                            new LmStudioNativeAgentProgress(type),
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case "promptProcessingProgress":
                        await ReportProgressAsync(
                            request,
                            new LmStudioNativeAgentProgress(
                                "promptProcessing",
                                PromptProgress: ReadDouble(message, "progress")),
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case "generationStarted":
                        await ReportProgressAsync(
                            request,
                            new LmStudioNativeAgentProgress("generationStarted"),
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case "toolCallGenerationStart":
                        LogToolGenerationStarted();
                        await ReportProgressAsync(
                            request,
                            new LmStudioNativeAgentProgress("toolCallGenerationStart"),
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case "toolCallGenerationNameReceived":
                        var toolName = ReadString(message, "name") ?? "<unknown>";
                        LogToolNameGenerated(toolName);
                        await ReportProgressAsync(
                            request,
                            new LmStudioNativeAgentProgress(
                                "toolCallGenerationNameReceived",
                                ToolName: toolName),
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case "toolCallGenerationArgumentFragmentGenerated":
                        // Argument text may contain workspace data. It is deliberately
                        // neither logged nor forwarded before LM Studio validates it;
                        // only its cumulative size is observable.
                        await ReportProgressAsync(
                            request,
                            new LmStudioNativeAgentProgress(
                                "toolCallGenerationArguments",
                                ArgumentCharacters: ReadInt32(message, "characterCount")),
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case "toolCallGenerationEnd":
                        LogToolGenerationCompleted();
                        await ReportProgressAsync(
                            request,
                            new LmStudioNativeAgentProgress(
                                "toolCallGenerationEnd",
                                ToolName: ReadString(message, "name"),
                                ArgumentCharacters: ReadInt32(message, "characterCount")),
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case "toolCallGenerationFailed":
                        LogToolGenerationFailed();
                        await ReportProgressAsync(
                            request,
                            new LmStudioNativeAgentProgress("toolCallGenerationFailed"),
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case "transportRetry":
                        await ReportProgressAsync(
                            request,
                            new LmStudioNativeAgentProgress(
                                "transportRetry",
                                ToolName: ReadString(message, "toolName")),
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case "result":
                        return ParseResult(message);
                    case "error":
                        throw ParseError(message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            ResetProcess();
            throw;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            ResetProcess();
            throw new LmStudioNativeAgentException(
                "sidecar_protocol_error",
                "Der native LM-Studio-SDK-Kanal lieferte keine gueltige Antwort.",
                innerException: exception);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task<JsonElement> ExecuteCommandAsync(
        string commandType,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var token = await _secretStore.ReadLmStudioTokenAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException(
                    "Fuer den nativen LM-Studio-SDK-Kanal ist kein API-Schluessel hinterlegt.");
            }

            var process = EnsureProcess();
            var requestId = Guid.NewGuid().ToString("N");
            var payload = arguments.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.Ordinal);
            payload["type"] = commandType;
            payload["id"] = requestId;
            payload["baseUrl"] = _options.LmStudioUri.ToString();
            payload["apiToken"] = token;
            await process.StandardInput.WriteLineAsync(
                JsonSerializer.Serialize(payload, _jsonOptions).AsMemory(),
                cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);

            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    throw new LmStudioNativeAgentException(
                        "sidecar_exited",
                        "Der native LM-Studio-SDK-Kanal wurde unerwartet beendet.");
                }
                if (!TryParseMessage(line, requestId, out var message))
                {
                    continue;
                }
                switch (ReadString(message, "type"))
                {
                    case "result":
                        return message;
                    case "error":
                        throw ParseError(message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            ResetProcess();
            throw;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            ResetProcess();
            throw new LmStudioNativeAgentException(
                "sidecar_protocol_error",
                "Der native LM-Studio-SDK-Kanal lieferte keine gueltige Antwort.",
                innerException: exception);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        ResetProcess();
        _requestGate.Dispose();
    }

    private Process EnsureProcess()
    {
        if (_process is { HasExited: false })
        {
            return _process;
        }

        ResetProcess();
        var workerPath = ResolveWorkerPath();
        var workerDirectory = Path.GetDirectoryName(workerPath)
            ?? throw new InvalidOperationException("Der Pfad des LM-Studio-SDK-Workers ist ungueltig.");
        var sdkPath = Path.Combine(workerDirectory, SdkRelativePath);
        if (!File.Exists(sdkPath))
        {
            throw new InvalidOperationException(
                $"Die native LM-Studio-SDK-Abhaengigkeit fehlt: {sdkPath}. Fuehre windows\\build-ai-server.ps1 aus.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveNodeExecutable(workerDirectory),
            WorkingDirectory = workerDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        startInfo.ArgumentList.Add(workerPath);
        startInfo.Environment["NO_COLOR"] = "1";
        startInfo.Environment["NODE_NO_WARNINGS"] = "1";
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Der native LM-Studio-SDK-Worker konnte nicht gestartet werden.");
        _process = process;
        _standardErrorDrain = DrainStandardErrorAsync(process);
        return process;
    }

    private async Task DrainStandardErrorAsync(Process process)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is not null)
            {
                // SDK diagnostics can contain model output. Keep only the fact that
                // a diagnostic occurred, never its content.
                LogDiagnosticLine();
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            LogDiagnosticStreamClosed();
        }
    }

    private bool TryParseMessage(string line, string requestId, out JsonElement message)
    {
        message = default;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
            var id = ReadString(root, "id");
            if (id is null
                && string.Equals(ReadString(root, "type"), "ready", StringComparison.Ordinal))
            {
                message = root.Clone();
                return true;
            }
            if (!string.Equals(id, requestId, StringComparison.Ordinal))
            {
                LogStaleEventIgnored();
                return false;
            }
            message = root.Clone();
            return true;
        }
        catch (JsonException)
        {
            LogNonProtocolOutputIgnored();
            return false;
        }
    }

    private static LmChatResult ParseResult(JsonElement message)
    {
        var calls = new List<LmToolCall>();
        if (message.TryGetProperty("toolCalls", out var toolCalls)
            && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in toolCalls.EnumerateArray())
            {
                var id = ReadString(call, "id") ?? $"call_{Guid.NewGuid():N}";
                var name = ReadString(call, "name")
                    ?? throw new JsonException("Native LM Studio tool call contains no name.");
                if (!call.TryGetProperty("arguments", out var arguments)
                    || arguments.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException("Native LM Studio tool call contains no argument object.");
                }
                calls.Add(new LmToolCall(id, name, arguments.Clone()));
            }
        }

        return new LmChatResult(
            ReadString(message, "content"),
            calls,
            ReadInt32(message, "inputTokens"),
            ReadInt32(message, "outputTokens"),
            message.TryGetProperty("hadReasoning", out var reasoning)
                && reasoning.ValueKind == JsonValueKind.True);
    }

    private static LmStudioNativeAgentException ParseError(JsonElement message)
    {
        var code = ReadString(message, "code") ?? "native_sdk_error";
        var rawContent = ReadString(message, "rawContent");
        var toolName = ReadString(message, "toolName");
        var providerMessage = SanitizeProviderMessage(ReadString(message, "message"));
        var userMessage = code switch
        {
            "tool_generation_failed" =>
                "LM Studio konnte den nativen Toolaufruf nicht vollstaendig erzeugen.",
            "cancelled" => "Der native LM-Studio-SDK-Lauf wurde abgebrochen.",
            "busy" => "Der native LM-Studio-SDK-Kanal verarbeitet bereits einen Auftrag.",
            _ => "Der native LM-Studio-SDK-Kanal konnte den Auftrag nicht abschliessen.",
        };
        return new LmStudioNativeAgentException(
            code,
            string.IsNullOrWhiteSpace(providerMessage)
                ? userMessage
                : $"{userMessage} SDK-Diagnose: {providerMessage}",
            rawContent,
            toolName);
    }

    private static string? SanitizeProviderMessage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        const int maximumCharacters = 512;
        var normalized = string.Join(
            ' ',
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maximumCharacters
            ? normalized
            : normalized[..maximumCharacters];
    }

    private static string ResolveWorkerPath()
    {
        var configured = Environment.GetEnvironmentVariable("GO_AI_LM_STUDIO_NATIVE_AGENT_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        foreach (var root in EnumerateCandidateRoots())
        {
            var candidate = Path.Combine(root, WorkerRelativePath);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }
        throw new FileNotFoundException(
            "Der native LM-Studio-SDK-Worker wurde nicht gefunden.",
            WorkerRelativePath);
    }

    private static IEnumerable<string> EnumerateCandidateRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var initial in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var current = new DirectoryInfo(Path.GetFullPath(initial));
            for (var depth = 0; current is not null && depth < 10; depth++, current = current.Parent)
            {
                if (seen.Add(current.FullName))
                {
                    yield return current.FullName;
                }
            }
        }
    }

    private static string ResolveNodeExecutable(string workerDirectory)
    {
        var bundled = Path.Combine(workerDirectory, "node.exe");
        if (File.Exists(bundled))
        {
            return bundled;
        }
        var configured = Environment.GetEnvironmentVariable("GO_AI_NODE_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var conventional = Path.Combine(programFiles, "nodejs", "node.exe");
        return File.Exists(conventional) ? conventional : "node.exe";
    }

    private void ResetProcess()
    {
        var process = _process;
        _process = null;
        _standardErrorDrain = null;
        if (process is null)
        {
            return;
        }
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            LogSidecarAlreadyStopped();
        }
        finally
        {
            process.Dispose();
        }
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ReadInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var parsed)
            ? parsed
            : 0;

    private static double ReadDouble(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.TryGetDouble(out var parsed)
            ? parsed
            : 0;

    private static ValueTask ReportProgressAsync(
        LmStudioNativeAgentRequest request,
        LmStudioNativeAgentProgress progress,
        CancellationToken cancellationToken) =>
        request.Progress is null
            ? ValueTask.CompletedTask
            : request.Progress(progress, cancellationToken);

    [LoggerMessage(LogLevel.Debug, "Native LM Studio tool generation started.")]
    private partial void LogToolGenerationStarted();

    [LoggerMessage(LogLevel.Information, "Native LM Studio tool name generated: {ToolName}")]
    private partial void LogToolNameGenerated(string toolName);

    [LoggerMessage(LogLevel.Debug, "Native LM Studio tool generation completed.")]
    private partial void LogToolGenerationCompleted();

    [LoggerMessage(LogLevel.Warning, "Native LM Studio tool generation reported an invalid call.")]
    private partial void LogToolGenerationFailed();

    [LoggerMessage(LogLevel.Debug, "Native LM Studio sidecar emitted a diagnostic line.")]
    private partial void LogDiagnosticLine();

    [LoggerMessage(LogLevel.Debug, "Native LM Studio sidecar diagnostic stream closed.")]
    private partial void LogDiagnosticStreamClosed();

    [LoggerMessage(LogLevel.Debug, "Ignored stale native LM Studio sidecar event.")]
    private partial void LogStaleEventIgnored();

    [LoggerMessage(LogLevel.Debug, "Ignored non-protocol output from native LM Studio sidecar.")]
    private partial void LogNonProtocolOutputIgnored();

    [LoggerMessage(LogLevel.Debug, "Native LM Studio sidecar was already stopped.")]
    private partial void LogSidecarAlreadyStopped();
}

internal sealed class LmStudioNativeAgentException : Exception
{
    public LmStudioNativeAgentException(
        string code,
        string message,
        string? rawContent = null,
        string? toolName = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = string.IsNullOrWhiteSpace(code) ? "native_sdk_error" : code;
        RawContent = rawContent;
        ToolName = string.IsNullOrWhiteSpace(toolName) ? null : toolName;
    }

    public string Code { get; }

    public string? RawContent { get; }

    public string? ToolName { get; }
}
