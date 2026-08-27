using GoAi.Client;
using GoAi.Contracts;
using GoWinUI.App.Services;
using GoWinUI.BricsCad.Protocol;
using GoWinUI.Core.Models;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GoWinUI.Tests;

internal sealed record CodingAgentLiveRunObservation(
    RunSnapshot Run,
    RunFailedEvent? Failure,
    IReadOnlyList<string> ToolNames,
    IReadOnlyList<AgentActionStartedEvent> AgentActions,
    IReadOnlyList<AgentObservationCommittedEvent> AgentObservations,
    IReadOnlyList<AgentMessageCompletedEvent> AgentMessages,
    IReadOnlyList<string> MutationTools,
    IReadOnlySet<string> VerificationPurposes,
    string VisibleText,
    string LogPath);

/// <summary>
/// Gemeinsame Laufzeit für echte Coding-Agent-Tests. Jeder Lauf wird live auf
/// der Konsole und parallel als strukturiertes JSONL protokolliert. Große
/// Datei-Schreibinhalte werden nur über Länge und SHA-256 erfasst, während
/// Prozessausgaben und Fehler für die nachträgliche Diagnose erhalten bleiben.
/// </summary>
internal sealed class CodingAgentLiveTestHarness : IAsyncDisposable
{
    private static readonly JsonSerializerOptions ProtocolJson = GoAiProtocol.CreateJsonOptions();
    private readonly string scenario;
    private readonly string workspace;
    private readonly string modelId;
    private readonly string modelDisplayName;
    private readonly HttpClient http;
    private readonly GoAiClient client;
    private readonly ToolConfirmationService confirmation;
    private readonly BricsCadBridgeHost bricsCad;
    private readonly LocalToolBroker broker;
    private readonly CodingLiveTestLog log;
    private bool disposed;

    private CodingAgentLiveTestHarness(
        string scenario,
        string workspace,
        string modelId,
        string sessionId,
        string clientId)
    {
        this.scenario = scenario;
        this.workspace = workspace;
        this.modelId = modelId;
        modelDisplayName = modelId;

        var gatewayUrl = Environment.GetEnvironmentVariable("GO_AI_SERVER_URL")
            ?? "http://127.0.0.1:8080/";
        http = new HttpClient
        {
            BaseAddress = new Uri(gatewayUrl.TrimEnd('/') + "/", UriKind.Absolute),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client = new GoAiClient(http, clientId);
        confirmation = new ToolConfirmationService(null!);
        bricsCad = new BricsCadBridgeHost();
        broker = new LocalToolBroker(
            connection: null!,
            settings: null!,
            confirmation,
            bricsCad,
            documents: null!);
        log = new CodingLiveTestLog(scenario, workspace, modelId, sessionId);
    }

    public string LogPath => log.Path;

    public static async Task<CodingAgentLiveTestHarness> CreateAsync(
        string scenario,
        string workspace,
        string modelId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var canonicalWorkspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        if (!Directory.Exists(canonicalWorkspace))
        {
            throw new DirectoryNotFoundException($"Live-Coding-Workspace fehlt: {canonicalWorkspace}");
        }

        var clientId = $"go-live-test-{sessionId}";

        var harness = new CodingAgentLiveTestHarness(
            scenario,
            canonicalWorkspace,
            modelId,
            sessionId,
            clientId);
        try
        {
            var ready = await harness.client.GetReadyHealthAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(ready.Status, "ready", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(ready.Status, "modelNotLoaded", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(ready.Status, "modelLoading", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"GO AI Server ist nicht bereit: {ready.Status}");
            }
            harness.log.Write("scenario.started", new
            {
                protocolVersion = GoAiProtocol.Version,
                serverStatus = ready.Status,
            });
            Console.WriteLine($"Live-Protokoll: {harness.LogPath}");
            return harness;
        }
        catch
        {
            await harness.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<CodingAgentLiveRunObservation> ExecuteAsync(
        string sessionId,
        string prompt,
        string idempotencyPrefix,
        CancellationToken cancellationToken,
        Func<ToolProposal, ClientToolResult, bool>? stopAfterTool = null,
        Func<string, JsonElement, bool>? stopAfterServerTool = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var tree = WorkspaceFileSystemView.BuildTree(workspace);
        var descriptor = new WorkspaceDescriptor(
            Path.GetFileName(tree.Root),
            tree.Tree,
            tree.FileCount,
            tree.IsTruncated);
        log.Write("run.requested", new
        {
            sessionId,
            prompt,
            promptSha256 = ComputeSha256(prompt),
            listedFiles = tree.FileCount,
            tree.TextFileCount,
            tree.IsTruncated,
        });
        var modelPrompt = GoAiAssistantService.BuildCodingPrompt(prompt);

        var accepted = await client.CreateRunAsync(
            new RunRequest(
                GoAiProtocol.Version,
                RunMode.Code,
                [new RunMessage("user", [new ContentPart("text", modelPrompt)])],
                ClientCapabilities: ["code", "filesystem", "process", "pdf"],
                Limits: new RunLimits(8_192, 262_144, 14_400),
                SessionId: sessionId,
                AllowedServerTools: GoAiAssistantService.GetAllowedServerTools(
                    PromptTriggerAction.Code,
                    prompt),
                Workspace: descriptor,
                ConversationProfile: ConversationProfile.General,
                PreferredCodeModelId: modelId),
            $"{idempotencyPrefix}-{Guid.NewGuid():N}",
            cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"Coding run accepted: {accepted.RunId}");
        log.Write("run.accepted", new { accepted.RunId }, accepted.RunId);

        var toolNames = new List<string>();
        var agentActions = new List<AgentActionStartedEvent>();
        var agentObservations = new List<AgentObservationCommittedEvent>();
        var agentMessages = new List<AgentMessageCompletedEvent>();
        var agentMessageBuffers = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        var agentMessageNextDelta = new Dictionary<string, int>(StringComparer.Ordinal);
        var mutationTools = new List<string>();
        var verificationPurposes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visibleText = new StringBuilder();
        RunFailedEvent? failure = null;
        await foreach (var item in client.StreamRunEventsAsync(
            accepted.RunId,
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            switch (item.Type)
            {
                case RunEventTypes.AgentMessageStarted:
                {
                    var started = item.Data.Deserialize<AgentMessageStartedEvent>(ProtocolJson)
                        ?? throw new InvalidDataException("Der Server lieferte einen ungültigen Agentennachrichtenstart.");
                    Assert.Equal(accepted.RunId, started.RunId);
                    Assert.False(agentMessageBuffers.ContainsKey(started.ItemId), $"Doppelter Agentenstart: {started.ItemId}");
                    agentMessageBuffers[started.ItemId] = new StringBuilder();
                    agentMessageNextDelta[started.ItemId] = 0;
                    log.Write("agent.message.started", new
                    {
                        item.Id,
                        started.ItemId,
                        started.Sequence,
                        started.Phase,
                        started.Origin,
                        started.MilestoneFingerprint,
                    }, accepted.RunId);
                    break;
                }
                case RunEventTypes.AgentMessageDelta:
                {
                    var delta = item.Data.Deserialize<AgentMessageDeltaEvent>(ProtocolJson)
                        ?? throw new InvalidDataException("Der Server lieferte ein ungültiges Agentennachrichten-Delta.");
                    Assert.Equal(accepted.RunId, delta.RunId);
                    Assert.True(agentMessageBuffers.TryGetValue(delta.ItemId, out var buffer), $"Delta ohne Start: {delta.ItemId}");
                    Assert.Equal(agentMessageNextDelta[delta.ItemId], delta.DeltaIndex);
                    buffer!.Append(delta.Delta);
                    agentMessageNextDelta[delta.ItemId] = delta.DeltaIndex + 1;
                    log.Write("agent.message.delta", new
                    {
                        item.Id,
                        delta.ItemId,
                        delta.DeltaIndex,
                        deltaLength = delta.Delta.Length,
                        cumulativeLength = buffer.Length,
                    }, accepted.RunId);
                    break;
                }
                case RunEventTypes.AgentMessageCompleted:
                {
                    var completedMessage = item.Data.Deserialize<AgentMessageCompletedEvent>(ProtocolJson)
                        ?? throw new InvalidDataException("Der Server lieferte einen ungültigen Agentennachrichtenabschluss.");
                    Assert.Equal(accepted.RunId, completedMessage.RunId);
                    Assert.True(agentMessageBuffers.TryGetValue(completedMessage.ItemId, out var buffer), $"Abschluss ohne Start: {completedMessage.ItemId}");
                    Assert.Equal(completedMessage.DeltaCount, agentMessageNextDelta[completedMessage.ItemId]);
                    Assert.Equal(completedMessage.Text, buffer!.ToString());
                    agentMessages.Add(completedMessage);
                    if (completedMessage.Phase == AgentMessagePhase.FinalAnswer)
                    {
                        visibleText.Clear();
                        visibleText.Append(completedMessage.Text);
                    }
                    log.Write("agent.message.completed", new
                    {
                        item.Id,
                        completedMessage.ItemId,
                        completedMessage.Sequence,
                        completedMessage.Phase,
                        completedMessage.Origin,
                        completedMessage.DeltaCount,
                        textLength = completedMessage.Text.Length,
                        completedMessage.MilestoneFingerprint,
                    }, accepted.RunId);
                    break;
                }
                case RunEventTypes.AgentActionStarted:
                {
                    var action = item.Data.Deserialize<AgentActionStartedEvent>(ProtocolJson)
                        ?? throw new InvalidDataException("Der Server lieferte eine ungültige V2-Agentenaktion.");
                    agentActions.Add(action);
                    Console.WriteLine($"{modelDisplayName} => {action.Tool}/{action.Operation} [{action.ActionId}]");
                    log.Write("agent.action", new
                    {
                        item.Id,
                        action.ActionId,
                        action.Sequence,
                        action.Tool,
                        action.Operation,
                        action.Target,
                    }, accepted.RunId);
                    break;
                }
                case RunEventTypes.AgentObservationCommitted:
                {
                    var observation = item.Data.Deserialize<AgentObservationCommittedEvent>(ProtocolJson)
                        ?? throw new InvalidDataException("Der Server lieferte eine ungültige V2-Agentenbeobachtung.");
                    agentObservations.Add(observation);
                    log.Write("agent.observation", new
                    {
                        item.Id,
                        observation.ActionId,
                        observation.Succeeded,
                        observation.ErrorCode,
                        observation.Message,
                    }, accepted.RunId);
                    break;
                }
                case RunEventTypes.ClientToolProposed:
                {
                    var proposal = item.Data.Deserialize<ToolProposal>(ProtocolJson)
                        ?? throw new InvalidDataException("Der Server lieferte einen ungültigen Client-Toolvorschlag.");
                    toolNames.Add(proposal.Name);
                    var safeArguments = SanitizeJson(proposal.Arguments);
                    Console.WriteLine($"{modelDisplayName} -> {proposal.Name} {JsonSerializer.Serialize(safeArguments)}");
                    log.Write("tool.proposed", new
                    {
                        item.Id,
                        proposal.ProposalId,
                        proposal.Name,
                        proposal.RiskClass,
                        proposal.Summary,
                        arguments = safeArguments,
                    }, accepted.RunId);

                    var stopwatch = Stopwatch.StartNew();
                    var result = await broker.ExecuteAsync(
                        proposal,
                        workspace,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    stopwatch.Stop();
                    Console.WriteLine($"GO <- {proposal.Name}: {result.Status} {result.ErrorCode} {result.Message}");
                    log.Write("tool.completed", new
                    {
                        item.Id,
                        proposal.ProposalId,
                        proposal.Name,
                        result.Status,
                        result.ErrorCode,
                        result.Message,
                        durationMilliseconds = stopwatch.ElapsedMilliseconds,
                        result = SanitizeJson(result.Result),
                    }, accepted.RunId);
                    CodingLiveTestConsole.WriteProgramStart(proposal, result, log, accepted.RunId);
                    if (IsMutation(proposal.Name)
                        && string.Equals(result.Status, "completed", StringComparison.OrdinalIgnoreCase))
                    {
                        mutationTools.Add(proposal.Name);
                    }
                    ObserveVerification(proposal, result, verificationPurposes);
                    await client.SubmitClientToolResultAsync(
                        accepted.RunId,
                        result,
                        cancellationToken).ConfigureAwait(false);
                    if (stopAfterTool?.Invoke(proposal, result) == true)
                    {
                        log.Write("run.stop_requested_after_tool", new
                        {
                            proposal.Name,
                            result.Status,
                        }, accepted.RunId);
                        await client.CancelRunAsync(accepted.RunId, CancellationToken.None).ConfigureAwait(false);
                    }
                    break;
                }
                case RunEventTypes.TextDelta:
                {
                    var delta = item.Data.Deserialize<TextDeltaEvent>(ProtocolJson)?.Delta ?? string.Empty;
                    visibleText.Append(delta);
                    log.Write("model.text.delta", new
                    {
                        item.Id,
                        deltaLength = delta.Length,
                        cumulativeLength = visibleText.Length,
                    }, accepted.RunId);
                    break;
                }
                case RunEventTypes.ServerToolCompleted:
                {
                    var toolName = item.Data.TryGetProperty("tool", out var toolElement)
                        && toolElement.ValueKind == JsonValueKind.String
                        ? toolElement.GetString() ?? string.Empty
                        : string.Empty;
                    if (!string.IsNullOrWhiteSpace(toolName))
                    {
                        toolNames.Add(toolName);
                    }
                    log.Write("server_tool.completed", new
                    {
                        item.Id,
                        tool = toolName,
                        data = SanitizeJson(item.Data),
                    }, accepted.RunId);
                    if (!string.IsNullOrWhiteSpace(toolName)
                        && stopAfterServerTool?.Invoke(toolName, item.Data) == true)
                    {
                        log.Write("run.stop_requested_after_server_tool", new
                        {
                            tool = toolName,
                        }, accepted.RunId);
                        await client.CancelRunAsync(accepted.RunId, CancellationToken.None).ConfigureAwait(false);
                    }
                    break;
                }
                case RunEventTypes.RunFailed:
                    failure = item.Data.Deserialize<RunFailedEvent>(ProtocolJson);
                    log.Write("run.failed", new
                    {
                        item.Id,
                        failure?.ErrorCode,
                        failure?.Message,
                    }, accepted.RunId);
                    break;
                default:
                    log.Write("run.event", new
                    {
                        item.Id,
                        item.Type,
                        data = SanitizeJson(item.Data),
                    }, accepted.RunId);
                    break;
            }
        }

        var completed = await client.GetRunAsync(accepted.RunId, cancellationToken).ConfigureAwait(false);
        var output = visibleText.ToString();
        Console.WriteLine($"Run {accepted.RunId}: {completed.State}, Modell {completed.SelectedModel}, Tools {toolNames.Count}");
        Console.WriteLine(output);
        log.Write("run.finished", new
        {
            completed.State,
            completed.SelectedModel,
            completed.ErrorCode,
            toolCount = toolNames.Count,
            mutationCount = mutationTools.Count,
            verificationPurposes = verificationPurposes.Order().ToArray(),
            assistantText = output,
            assistantTextSha256 = ComputeSha256(output),
        }, accepted.RunId);
        return new CodingAgentLiveRunObservation(
            completed,
            failure,
            toolNames,
            agentActions,
            agentObservations,
            agentMessages,
            mutationTools,
            verificationPurposes,
            output,
            log.Path);
    }

    public void Record(string eventName, object? payload = null) => log.Write(eventName, payload);

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        log.Write("scenario.finished");
        log.Dispose();
        await bricsCad.DisposeAsync().ConfigureAwait(false);
        confirmation.Dispose();
        client.Dispose();
        http.Dispose();
    }

    internal static void AssertSuccessful(
        CodingAgentLiveRunObservation observation,
        string expectedModelId,
        bool requireMutation = true,
        bool requireVerification = true)
    {
        Assert.True(
            observation.Run.State == RunState.Completed,
            $"Coding-Lauf endete als {observation.Run.State}: "
                + $"{observation.Failure?.ErrorCode ?? observation.Run.ErrorCode} – {observation.Failure?.Message}. "
                + $"Protokoll: {observation.LogPath}");
        Assert.Equal(expectedModelId, observation.Run.SelectedModel, ignoreCase: true);
        if (requireMutation)
        {
            Assert.NotEmpty(observation.MutationTools);
        }
        if (requireVerification)
        {
            Assert.NotEmpty(observation.VerificationPurposes);
        }
        Assert.False(string.IsNullOrWhiteSpace(observation.VisibleText));
        var finalMessage = Assert.Single(
            observation.AgentMessages,
            static message => message.Phase == AgentMessagePhase.FinalAnswer);
        Assert.Equal(observation.VisibleText, finalMessage.Text);
        var streamedStates = observation.AgentMessages
            .Where(static message => message.Phase == AgentMessagePhase.Commentary)
            .ToArray();
        Assert.All(streamedStates, message => Assert.Equal(finalMessage.ItemId, message.ItemId));
        for (var index = 1; index < streamedStates.Length; index++)
        {
            Assert.StartsWith(streamedStates[index - 1].Text, streamedStates[index].Text, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("<tool_call", observation.VisibleText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<function=", observation.VisibleText, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMutation(string name) => name is
        ClientToolNames.FileSystemWriteText or
        ClientToolNames.FileSystemReplaceText or
        ClientToolNames.FileSystemMove or
        ClientToolNames.FileSystemProposePatch or
        ClientToolNames.FileSystemProposeCreate or
        ClientToolNames.FileSystemProposeDelete;

    private static void ObserveVerification(
        ToolProposal proposal,
        ClientToolResult result,
        HashSet<string> purposes)
    {
        if (!string.Equals(result.Status, "completed", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(result.ErrorCode)
            || result.Result.ValueKind != JsonValueKind.Object
            || !result.Result.TryGetProperty("exitCode", out var exitCode)
            || !exitCode.TryGetInt32(out var code)
            || code != 0)
        {
            return;
        }

        if (proposal.Name == ClientToolNames.ProcessRun
            && proposal.Arguments.TryGetProperty("purpose", out var purpose)
            && purpose.ValueKind == JsonValueKind.String
            && purpose.GetString() is { Length: > 0 } value)
        {
            var executable = proposal.Arguments.TryGetProperty("executable", out var executableValue)
                ? Path.GetFileName(executableValue.GetString() ?? string.Empty)
                : string.Empty;
            var commandArguments = proposal.Arguments.TryGetProperty("arguments", out var argumentValue)
                && argumentValue.ValueKind == JsonValueKind.Array
                    ? argumentValue.EnumerateArray()
                        .Where(static item => item.ValueKind == JsonValueKind.String)
                        .Select(static item => item.GetString() ?? string.Empty)
                        .ToArray()
                    : [];
            var isSmoke = proposal.Arguments.TryGetProperty("startMode", out var startMode)
                && string.Equals(startMode.GetString(), "smoke", StringComparison.OrdinalIgnoreCase);
            if (value.Equals("test", StringComparison.OrdinalIgnoreCase)
                && IsVerificationCommand(executable, commandArguments, TestCommands))
            {
                purposes.Add("test");
            }
            else if (value.Equals("build", StringComparison.OrdinalIgnoreCase)
                && (IsVerificationCommand(executable, commandArguments, BuildCommands)
                    || commandArguments.Any(IsGeneratorOrBuildScript)))
            {
                purposes.Add("build");
            }
            else if (value.Equals("start", StringComparison.OrdinalIgnoreCase) && isSmoke)
            {
                purposes.Add("start");
            }
        }
        else if (proposal.Name == ClientToolNames.ProcessRunPreset
            && proposal.Arguments.TryGetProperty("preset", out var preset)
            && preset.ValueKind == JsonValueKind.String)
        {
            switch (preset.GetString())
            {
                case "repository.verify":
                    purposes.UnionWith(["test", "build", "start"]);
                    break;
                case "repository.build":
                case "dotnet.build":
                    purposes.Add("build");
                    break;
                case "dotnet.test":
                case "code.test":
                    purposes.Add("test");
                    break;
                case "repository.start":
                case "code.run":
                    purposes.Add("start");
                    break;
                case "git.diff":
                    purposes.Add("review");
                    break;
            }
        }
    }

    private static readonly string[] TestCommands = ["test", "pytest", "ctest", "unittest", "py_compile", "--check"];
    private static readonly string[] BuildCommands =
        ["build", "publish", "package", "pack", "compile", "compileall", "check", "assemble", "dist", "bundle", "--build"];

    private static bool IsVerificationCommand(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyList<string> commandMarkers) =>
        commandMarkers.Any(marker => executable.Equals(marker, StringComparison.OrdinalIgnoreCase))
        || arguments.Any(argument => commandMarkers.Contains(argument, StringComparer.OrdinalIgnoreCase));

    private static bool IsGeneratorOrBuildScript(string argument)
    {
        var name = Path.GetFileNameWithoutExtension(argument);
        return name.StartsWith("generate", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("build", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("package", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("compile", StringComparison.OrdinalIgnoreCase);
    }

    private static object? SanitizeJson(JsonElement element, string? propertyName = null)
    {
        if (IsLargeContentProperty(propertyName) && element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString() ?? string.Empty;
            return new { omitted = true, length = value.Length, sha256 = ComputeSha256(value) };
        }
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(
                static property => property.Name,
                property => SanitizeJson(property.Value, property.Name),
                StringComparer.OrdinalIgnoreCase),
            JsonValueKind.Array => element.EnumerateArray().Select(item => SanitizeJson(item)).ToArray(),
            JsonValueKind.String => Clip(element.GetString() ?? string.Empty, 32_768),
            JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when element.TryGetDecimal(out var number) => number,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static bool IsLargeContentProperty(string? name) => name is not null
        && (name.Equals("content", StringComparison.OrdinalIgnoreCase)
            || name.Equals("newText", StringComparison.OrdinalIgnoreCase)
            || name.Equals("replacement", StringComparison.OrdinalIgnoreCase)
            || name.Equals("patch", StringComparison.OrdinalIgnoreCase));

    private static string Clip(string value, int maximumLength) => value.Length <= maximumLength
        ? value
        : value[..maximumLength] + $"\n[... {value.Length - maximumLength} Zeichen gekürzt; SHA-256 {ComputeSha256(value)}]";

    private static string ComputeSha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(character => invalid.Contains(character) ? '-' : character));
    }
}

internal sealed class CodingLiveTestLog : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object gate = new();
    private readonly StreamWriter writer;
    private readonly string scenario;
    private readonly string workspace;
    private readonly string modelId;
    private readonly string sessionId;
    private long sequence;
    private bool disposed;

    public CodingLiveTestLog(string scenario, string workspace, string modelId, string sessionId)
    {
        this.scenario = scenario;
        this.workspace = workspace;
        this.modelId = modelId;
        this.sessionId = sessionId;
        var scenarioDirectory = System.IO.Path.Combine(ResolveLogRoot(), SanitizeFileName(scenario));
        Directory.CreateDirectory(scenarioDirectory);
        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        Path = System.IO.Path.Combine(scenarioDirectory, $"{timestamp}-{SanitizeFileName(sessionId)}.jsonl");
        writer = new StreamWriter(
            new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
        var latestPath = System.IO.Path.Combine(scenarioDirectory, "latest.json");
        File.WriteAllText(
            latestPath,
            JsonSerializer.Serialize(new
            {
                scenario,
                sessionId,
                workspace,
                modelId,
                logPath = Path,
                startedAt = DateTimeOffset.Now,
            }, JsonOptions));
    }

    public string Path { get; }

    public void Write(string eventName, object? payload = null, string? runId = null)
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            var item = new
            {
                timestamp = DateTimeOffset.Now,
                sequence = ++sequence,
                scenario,
                sessionId,
                runId,
                workspace,
                modelId,
                eventName,
                payload,
            };
            writer.WriteLine(JsonSerializer.Serialize(item, JsonOptions));
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            writer.Dispose();
        }
    }

    private static string ResolveLogRoot()
    {
        var configured = Environment.GetEnvironmentVariable("GO_AI_LIVE_LOG_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return System.IO.Path.GetFullPath(configured);
        }
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "GO.slnx")))
            {
                return System.IO.Path.Combine(directory.FullName, "artifacts", "coding-live-tests");
            }
        }
        return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GO",
            "CodingLiveTests",
            "Logs");
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(character => invalid.Contains(character) ? '-' : character));
    }
}
