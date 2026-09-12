using GoAi.Client;
using GoAi.Contracts;
using GoWinUI.App.Services;
using GoWinUI.Core.Coding;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using GoWinUI.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace GoWinUI.Tests;

/// <summary>Explicit opt-in: real model proposals execute only two fixed, read-only PowerShell fixture invocations.</summary>
public sealed class CodingPowerShellLiveTests(ITestOutputHelper output)
{
    private const string DefaultModel = "coding/Qwen3.8-27B-UD-Q8_K_XL~86704cb9d896";
    private static readonly JsonSerializerOptions Json = GoAiProtocol.CreateJsonOptions();
    private static readonly JsonSerializerOptions EvidenceJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] SuccessArguments = ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", "probe.ps1", "-Mode", "Success"];
    private static readonly string[] FailureArguments = ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", "probe.ps1", "-Mode", "Failure"];

    [Fact]
    [Trait("Category", "Live")]
    public async Task ModelReadsPowerShellScriptExecutesBothModesAndPersistsActualTerminalResults()
    {
        if (Environment.GetEnvironmentVariable("GO_AI_POWERSHELL_LIVE") != "1") return;
        await using var history = await HistoryEnvironment.CreateAsync();
        var identifier = Guid.NewGuid().ToString("N");
        var fixture = Path.Combine(history.Directory, "TestArtifacts", "powershell-" + identifier);
        var workspace = Path.Combine(fixture, "workspace");
        Directory.CreateDirectory(workspace);
        var marker = "GO-POWERSHELL-" + identifier;
        var scriptPath = Path.Combine(workspace, "probe.ps1");
        var inputPath = Path.Combine(workspace, "probe-input.txt");
        var script = CreateProbeScript(marker);
        byte[] scriptBytes = [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(script)];
        var inputBytes = Encoding.UTF8.GetBytes(marker + "\n");
        await File.WriteAllBytesAsync(scriptPath, scriptBytes);
        await File.WriteAllBytesAsync(inputPath, inputBytes);
        var scriptHash = SHA256.HashData(scriptBytes);
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        Assert.True(File.Exists(executable), "The installed Windows PowerShell executable is required for this opt-in test.");
        var prompt = CreatePrompt(executable);
        var chats = history.Chats;
        var session = await chats.CreateSessionAsync("PowerShell-Werkzeugtest");
        await chats.SetPersistentToolActionAsync(session.Id, PersistentToolAction.Coding);
        await chats.SetCodingWorkspacePathAsync(session.Id, workspace);
        var turn = await chats.AddTurnAsync(session.Id, prompt);
        var assistantId = turn.AssistantMessage.Id;
        var model = Environment.GetEnvironmentVariable("GO_AI_LIVE_CODING_MODEL") ?? DefaultModel;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        using var http = new HttpClient
        {
            BaseAddress = new Uri((Environment.GetEnvironmentVariable("GO_AI_SERVER_URL") ?? "http://127.0.0.1:8080").TrimEnd('/') + "/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var client = new GoAiClient(http, "go-powershell-live-" + identifier);
        // The regular broker automatically authorizes valid tools; this harness has no approval seam.
        var broker = new LocalToolBroker(null!, null!, null!, null!, chats);
        var completedTools = new Dictionary<string, (ToolProposal Proposal, ClientToolResult Result)>(StringComparer.Ordinal);
        var commands = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var response = new StringBuilder();
        var progress = new HashSet<string>(StringComparer.Ordinal);
        var readScript = false;
        var terminal = false;
        string? runId = null;
        RunFailedEvent? failure = null;
        try
        {
            var accepted = await client.CreateRunAsync(new RunRequest(GoAiProtocol.Version, RunMode.Coding,
                [new("user", [new("text", prompt)])], ClientCapabilities: ["coding"],
                Limits: new RunLimits(4_096, 32_768, 720), AllowedServerTools: [], PreferredCodingModelId: model),
                "powershell-live-" + identifier, timeout.Token);
            runId = accepted.RunId;
            output.WriteLine($"PowerShell run={runId}; model={model}; session={session.Id}; workspace={workspace}; marker={marker}");
            long cursor = 0;
            for (var connection = 0; !terminal && connection < 12; connection++)
            {
                await foreach (var item in client.StreamRunEventsAsync(runId, cursor, timeout.Token))
                {
                    if (item.Type == RunEventTypes.ModelGeneration && progress.Count < 64)
                    {
                        var state = item.Data.Deserialize<ModelGenerationEvent>(Json)?.State;
                        if (state is not null) progress.Add(state);
                    }
                    else if (item.Type == RunEventTypes.TextDelta)
                    {
                        var delta = item.Data.Deserialize<TextDeltaEvent>(Json)?.Delta ?? string.Empty;
                        response.Append(delta.AsSpan(0, Math.Min(delta.Length, 8_000 - response.Length)));
                    }
                    else if (item.Type == RunEventTypes.ClientToolProposed)
                    {
                        var proposal = item.Data.Deserialize<ToolProposal>(Json) ?? throw new InvalidDataException("Missing live tool proposal.");
                        Assert.Equal(runId, proposal.RunId);
                        if (completedTools.TryGetValue(proposal.ProposalId, out var existing))
                        {
                            Assert.Equal(existing.Proposal.Name, proposal.Name);
                            Assert.True(JsonElement.DeepEquals(existing.Proposal.Arguments, proposal.Arguments));
                            await client.SubmitClientToolResultAsync(runId, existing.Result, timeout.Token);
                        }
                        else
                        {
                            Assert.True(completedTools.Count < 12, "PowerShell acceptance exceeded its 12-proposal budget.");
                            var input = GoAiAssistantService.FormatToolInputDetail(proposal);
                            await chats.SaveToolStepAsync(assistantId, new(proposal.ProposalId, proposal.Name, "running", input), timeout.Token);
                            ClientToolResult result;
                            try
                            {
                                var mode = ValidateFixtureTool(proposal, executable);
                                if (mode is not null)
                                {
                                    if (!readScript) throw new InvalidOperationException("Read probe.ps1 before executing it.");
                                    if (commands.ContainsKey(mode)) throw new InvalidOperationException("Each authorized script mode may execute only once.");
                                    Assert.Equal(scriptHash, SHA256.HashData(await File.ReadAllBytesAsync(scriptPath, timeout.Token)));
                                }
                                result = await broker.ExecuteAsync(proposal, session.Id, assistantId, workspace, cancellationToken: timeout.Token);
                                var value = result.Result;
                                if (proposal.Name == ClientToolNames.CodingRead && value.GetProperty("path").GetString() == "probe.ps1") readScript = true;
                                if (mode is not null) commands.Add(mode, value.Clone());
                            }
                            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
                            {
                                result = new(proposal.ProposalId, "failed", JsonSerializer.SerializeToElement(new { success = false }),
                                    "coding.powershell_fixture_rejected", exception.Message);
                            }
                            completedTools.Add(proposal.ProposalId, (proposal, result));
                            var detail = input + "\n\nErgebnis:\n" + GoAiAssistantService.FormatClientToolResultDetail(result);
                            await chats.SaveToolStepAsync(assistantId, new(proposal.ProposalId, proposal.Name,
                                GoAiAssistantService.GetToolResultStatus(result), detail), timeout.Token);
                            output.WriteLine($"{proposal.Name}: {result.Status}; {result.Result.GetRawText()}");
                            await client.SubmitClientToolResultAsync(runId, result, timeout.Token);
                        }
                    }
                    else if (item.Type == RunEventTypes.RunFailed)
                    {
                        failure = item.Data.Deserialize<RunFailedEvent>(Json);
                        terminal = true;
                    }
                    else if (item.Type is RunEventTypes.RunCompleted or RunEventTypes.RunCancelled) terminal = true;
                    cursor = item.Id;
                    if (terminal) break;
                }
                if (!terminal) await Task.Delay(250, timeout.Token);
            }
            var snapshot = await client.GetRunAsync(runId, timeout.Token);
            Assert.True(snapshot.State == RunState.Completed, $"PowerShell agent ended {snapshot.State}: {failure?.Message ?? snapshot.ErrorCode}");
            Assert.True(readScript, "The model did not inspect the unchanged fixture script.");
            Assert.Equal(2, commands.Count);
            AssertTerminalResult(commands["Success"], "Success", 0, marker, workspace);
            AssertTerminalResult(commands["Failure"], "Failure", 7, marker, workspace);
            Assert.Equal(scriptBytes, await File.ReadAllBytesAsync(scriptPath, timeout.Token));
            Assert.Equal(inputBytes, await File.ReadAllBytesAsync(inputPath, timeout.Token));
            Assert.Equal(2, Directory.GetFiles(workspace, "*", SearchOption.AllDirectories).Length);
            var answer = response.ToString();
            Assert.False(string.IsNullOrWhiteSpace(answer), "No visible model answer was streamed.");
            Assert.Contains("7", answer, StringComparison.Ordinal);
            Assert.Matches(new Regex("fehler|fehlgeschlagen|absichtlich|erwartet|failure", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), answer);
            await chats.UpdateMessageAsync(assistantId, answer, MessageStatus.Completed, cancellationToken: timeout.Token);
            var persisted = await history.Snapshots.GetAsync(session.Id, timeout.Token);
            var stored = Assert.Single(persisted!.Messages, message => message.Id == assistantId);
            var commandSteps = stored.ToolSteps!.Where(step => step.Tool == ClientToolNames.CodingCommand).ToArray();
            Assert.Equal(2, commandSteps.Length);
            Assert.Contains(commandSteps, step => step.Status == "completed" && step.Detail!.Contains("STDOUT|Success|" + marker, StringComparison.Ordinal));
            Assert.Contains(commandSteps, step => step.Status == "failed" && step.Detail!.Contains("STDERR|Failure|" + marker, StringComparison.Ordinal));
            foreach (var completed in completedTools.Values)
            {
                var step = Assert.Single(stored.ToolSteps!, step => step.Id == completed.Proposal.ProposalId);
                Assert.Equal(GoAiAssistantService.FormatToolInputDetail(completed.Proposal) + "\n\nErgebnis:\n"
                    + GoAiAssistantService.FormatClientToolResultDetail(completed.Result), step.Detail);
                // Display fidelity is separate from the unchanged tool-to-model result budget.
                Assert.True(completed.Result.Result.GetRawText().Length <= LocalCodingToolExecutor.MaximumOutputCharacters);
                Assert.InRange(step.Detail!.Length, 1, AssistantToolStep.MaximumDetailCharacters);
            }
            Assert.InRange(stored.Content.Length, 1, 8_000);
            output.WriteLine("PowerShell acceptance completed; final answer=" + answer);
        }
        catch
        {
            var pending = await chats.GetMessageAsync(assistantId, CancellationToken.None);
            foreach (var step in (pending?.ToolSteps ?? []).Where(step => step.Status == "running"))
                await chats.SaveToolStepAsync(assistantId, step with { Status = timeout.IsCancellationRequested ? "cancelled" : "failed" });
            await chats.UpdateMessageAsync(assistantId, response.ToString(), MessageStatus.Failed,
                "Der opt-in PowerShell-Akzeptanztest wurde nicht vollständig bestanden.", CancellationToken.None);
            throw;
        }
        finally
        {
            if (runId is not null && !terminal)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await client.CancelRunAsync(runId, cleanup.Token); }
                catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException) { output.WriteLine("Cancel cleanup: " + exception.Message); }
            }
            await File.WriteAllTextAsync(Path.Combine(fixture, "evidence.json"), JsonSerializer.Serialize(new
            {
                runId, model, sessionId = session.Id, workspace, marker, scriptSha256 = Convert.ToHexStringLower(scriptHash),
                tools = completedTools.Values.Select(value => new { value.Proposal, value.Result }), progress, answer = response.ToString(),
            }, EvidenceJson));
            output.WriteLine($"History session={session.Id}; persistent={history.Persistent}; evidence={Path.Combine(fixture, "evidence.json")}");
        }
    }

    private static void AssertTerminalResult(JsonElement result, string mode, int exitCode, string marker, string workspace)
    {
        Assert.Equal(exitCode, result.GetProperty("exitCode").GetInt32());
        Assert.Equal(exitCode == 0, result.GetProperty("success").GetBoolean());
        Assert.False(result.GetProperty("timedOut").GetBoolean());
        Assert.Contains($"STDOUT|{mode}|{marker}|Grüße € 日本語", result.GetProperty("stdout").GetString()!, StringComparison.Ordinal);
        Assert.Contains("CWD|" + workspace, result.GetProperty("stdout").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"STDERR|{mode}|{marker}|Diagnose äöü", result.GetProperty("stderr").GetString()!, StringComparison.Ordinal);
        Assert.InRange(result.GetProperty("stdout").GetString()!.Length, 1, 4_000);
        Assert.InRange(result.GetProperty("stderr").GetString()!.Length, 1, 2_000);
        Assert.True(result.GetRawText().Length <= LocalCodingToolExecutor.MaximumOutputCharacters);
    }

    private static string? ValidateFixtureTool(ToolProposal proposal, string executable)
    {
        if (proposal.ExpiresAt <= DateTimeOffset.UtcNow) throw new InvalidOperationException("The tool proposal expired.");
        var args = proposal.Arguments;
        if (proposal.Name == ClientToolNames.CodingCommand)
        {
            if (proposal.RiskClass != ToolRiskClass.Process || args.GetProperty("executable").GetString() != executable
                || args.TryGetProperty("workingDirectory", out var cwd) && cwd.GetString() is not ("." or "./")
                || !args.TryGetProperty("timeoutSeconds", out var seconds) || !seconds.TryGetInt32(out var limit) || limit != 30
                || args.EnumerateObject().Any(property => property.Name is not ("executable" or "arguments" or "workingDirectory" or "timeoutSeconds")))
                throw new UnauthorizedAccessException("Only the specified PowerShell executable in the fixture root is authorized.");
            var actual = args.GetProperty("arguments").EnumerateArray().Select(value => value.GetString()).ToArray();
            if (actual.SequenceEqual(SuccessArguments, StringComparer.Ordinal)) return "Success";
            if (actual.SequenceEqual(FailureArguments, StringComparer.Ordinal)) return "Failure";
            throw new UnauthorizedAccessException("Only the two exact probe.ps1 argument arrays are authorized; no inline commands, other scripts or mutations.");
        }
        if (proposal.RiskClass != ToolRiskClass.ReadOnly || proposal.Name is not (ClientToolNames.CodingRead or ClientToolNames.CodingList or ClientToolNames.CodingSearch))
            throw new UnauthorizedAccessException("Only fixture inspection and the two named script executions are authorized.");
        var path = args.TryGetProperty("path", out var pathValue) ? pathValue.GetString() : ".";
        if (proposal.Name == ClientToolNames.CodingRead && path is not ("probe.ps1" or "./probe.ps1" or "probe-input.txt" or "./probe-input.txt"))
            throw new UnauthorizedAccessException("Only the two fixture files may be read.");
        if (proposal.Name != ClientToolNames.CodingRead && path is not ("." or "./"))
            throw new UnauthorizedAccessException("Only the fixture root may be enumerated or searched.");
        return null;
    }

    private static string CreatePrompt(string executable) =>
        "Prüfe die vorhandene PowerShell-Probe in diesem temporären Projekt. Lies zuerst probe.ps1 mit coding.read. "
        + "Führe danach genau zwei coding.command-Aufrufe aus: zuerst Mode Success, danach Mode Failure. "
        + "Beide Aufrufe sind ausdrücklich für diesen Test autorisiert. executable muss exakt " + JsonSerializer.Serialize(executable)
        + " sein; workingDirectory ist '.', timeoutSeconds ist 30. Die erlaubten Argumentlisten sind exakt "
        + JsonSerializer.Serialize(SuccessArguments) + " und " + JsonSerializer.Serialize(FailureArguments) + ". "
        + "Starte ausschließlich dieses unveränderte Skript mit diesen Argumenten, keine anderen Programme oder Inline-Befehle. "
        + "Ändere oder erzeuge keine Dateien. Das Skript liest nur probe-input.txt, gibt Unicode, Arbeitsordner und Marker auf stdout/stderr aus "
        + "und beendet sich einmal mit Exitcode 0 und einmal absichtlich mit Exitcode 7. Wiederhole den erwarteten Fehler nicht. "
        + "Fasse ausschließlich die tatsächlichen Werkzeugergebnisse kurz auf Deutsch zusammen: beide Exitcodes, "
        + "den absichtlich fehlgeschlagenen zweiten Prozess, stdout/stderr und den überprüften Arbeitsordner. Behaupte keinen Erfolg für Exitcode 7.";

    private static string CreateProbeScript(string marker) => $$"""
        param([ValidateSet('Success','Failure')][string] $Mode)
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
        $OutputEncoding = [Console]::OutputEncoding
        $marker = '{{marker}}'
        $inputText = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'probe-input.txt') -Raw -Encoding UTF8
        if ($inputText -cne ($marker + "`n")) { [Console]::Error.WriteLine('UNEXPECTED_FIXTURE_CONTENT'); exit 19 }
        [Console]::WriteLine("STDOUT|$Mode|$marker|Grüße € 日本語")
        [Console]::Error.WriteLine("STDERR|$Mode|$marker|Diagnose äöü")
        [Console]::WriteLine('CWD|' + (Get-Location).Path)
        if ($Mode -eq 'Failure') { exit 7 }
        exit 0
        """;

    private sealed class HistoryEnvironment : IAsyncDisposable
    {
        private readonly TestEnvironment? _temporary;
        private readonly ServiceProvider? _services;
        private HistoryEnvironment(TestEnvironment temporary)
        {
            _temporary = temporary;
            Directory = temporary.Directory;
            Chats = temporary.Get<IChatRepository>();
            Snapshots = temporary.Get<IConversationSnapshotRepository>();
        }
        private HistoryEnvironment(string directory, ServiceProvider services)
        {
            Directory = directory;
            _services = services;
            Chats = services.GetRequiredService<IChatRepository>();
            Snapshots = services.GetRequiredService<IConversationSnapshotRepository>();
        }
        public string Directory { get; }
        public IChatRepository Chats { get; }
        public IConversationSnapshotRepository Snapshots { get; }
        public bool Persistent => _services is not null;
        public static async Task<HistoryEnvironment> CreateAsync()
        {
            var profile = Environment.GetEnvironmentVariable("GO_AI_POWERSHELL_CHAT_PROFILE");
            if (string.IsNullOrWhiteSpace(profile)) return new(await TestEnvironment.CreateAsync());
            if (!Path.IsPathFullyQualified(profile) || !System.IO.Directory.Exists(profile))
                throw new ArgumentException("GO_AI_POWERSHELL_CHAT_PROFILE must be an existing absolute GO DataDirectory.");
            var root = Path.GetFullPath(profile);
            var collection = new ServiceCollection();
            collection.AddLogging();
            collection.AddGoInfrastructure(options => options.DataDirectory = root);
            var services = collection.BuildServiceProvider(validateScopes: true);
            try
            {
                await services.GetRequiredService<IGoDatabase>().InitializeAsync();
                return new(root, services);
            }
            catch { await services.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            if (_temporary is not null) await _temporary.DisposeAsync();
            if (_services is not null) await _services.DisposeAsync();
            // An explicitly selected real profile, its chat and TestArtifacts are always preserved.
        }
    }
}
