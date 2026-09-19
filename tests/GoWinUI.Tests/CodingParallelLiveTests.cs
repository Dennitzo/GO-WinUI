using GoAi.Client;
using GoAi.Contracts;
using GoWinUI.App.Services;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Xunit.Abstractions;

namespace GoWinUI.Tests;

/// <summary>Opt-in functional acceptance with real dual-model inference and actual workspace writes. No speed comparison.</summary>
public sealed class CodingParallelLiveTests(ITestOutputHelper output)
{
    private static readonly string[] FileNames = ["main.txt", "secondary.txt"];
    private static readonly JsonSerializerOptions EvidenceJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    [Fact]
    [Trait("Category", "Live")]
    public async Task MainAndSecondaryModelsWriteTheirOwnFilesAndJoin()
    {
        if (Environment.GetEnvironmentVariable("GO_AI_PARALLEL_LIVE") != "1") return;
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Path.Combine(environment.Directory, "parallel-workspace");
        Directory.CreateDirectory(workspace);
        var marker = Guid.NewGuid().ToString("N");
        var mainText = "MAIN-" + marker;
        var secondaryText = "SECONDARY-" + marker;
        await File.WriteAllTextAsync(Path.Combine(workspace, "verify-secondary.ps1"),
            "$ErrorActionPreference='Stop'; if ((Get-Content secondary.txt -Raw).Trim() -ne '" + secondaryText
            + "') { throw 'Falscher Marker' }; Write-Output 'SUBAGENT-TEST-PASSED'");
        var model = Environment.GetEnvironmentVariable("GO_AI_LIVE_CODING_MODEL") ?? "coding/Qwen3.8-27B-UD-Q8_K_XL~86704cb9d896";
        var server = Environment.GetEnvironmentVariable("GO_AI_SERVER_URL") ?? "http://127.0.0.1:8080";
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Paralleler funktionaler Akzeptanztest");
        await chats.SetCodingWorkspacePathAsync(session.Id, workspace);
        var prompt = $"Führe diese kleine echte Parallelaufgabe aus. Starte zuerst mit coding.agentStart einen Subagenten, "
            + $"der ausschließlich secondary.txt mit dem exakten Inhalt {secondaryText} erstellt und anschließend selbst liest. "
            + "Der Subagent muss außerdem mit coding.command powershell.exe und arguments=[\"-NoProfile\",\"-NonInteractive\",\"-File\",\"verify-secondary.ps1\"] den vorhandenen Test in seiner isolierten Kopie ausführen. "
            + "Weise writePaths=[\"secondary.txt\"] zu. "
            + $"Erstelle nach dem Start selbst main.txt mit dem exakten Inhalt {mainText}. "
            + "Nutze als Hauptagent coding.write und keine eigenen Terminalbefehle. Nutze danach coding.agentWait und lies beide Dateien vollständig mit coding.read. "
            + "Prüfe die beiden Marker und bestätige nur bei erfolgreicher Prüfung den Abschluss. Keine Recherche und keine weiteren Dateien.";
        var turn = await chats.AddTurnAsync(session.Id, prompt);
        using var settings = new SettingsCoordinator(new ConnectionSettings(server));
        await settings.InitializeAsync();
        using var connection = new GoAiConnectionService(settings, NullLogger<GoAiConnectionService>.Instance);
        var broker = new LocalToolBroker(connection, null!, environment.Get<IDocumentIngestor>(), null!, chats);
        using var http = new HttpClient { BaseAddress = new Uri(server.TrimEnd('/') + "/"), Timeout = Timeout.InfiniteTimeSpan };
        using var client = new GoAiClient(http, "parallel-live-" + marker);
        // This is an emergency cleanup deadline, never a latency assertion or benchmark.
        using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(25));
        var token = cleanupDeadline.Token;
        var json = GoAiProtocol.CreateJsonOptions();
        var events = new List<JsonElement>();
        var completed = new Dictionary<string, (ToolProposal Proposal, ClientToolResult Result)>();
        string? runId = null;
        var terminal = false;
        var started = false;
        var joined = false;
        var childCompleted = false;
        try
        {
            var caps = await client.GetCapabilitiesAsync(token);
            Assert.True(caps.SupportsParallelCoding, "Deploy parallel gateway before running this acceptance.");
            Assert.True(caps.SupportsIsolatedSubagents, "Deploy the gateway with isolated subagent support before this acceptance.");
            var accepted = await client.CreateRunAsync(new RunRequest(GoAiProtocol.Version, RunMode.Coding,
                [new("user", [new("text", prompt)])], ClientCapabilities: ["coding", "coding-isolated-subagents"],
                SessionId: session.Id.ToString("D"), PreferredCodingModelId: model,
                CodingOptions: new(WorkspacePath: workspace, ParallelModelId: model), ReasoningEffort: "low"), "parallel-" + marker, token);
            runId = accepted.RunId;
            output.WriteLine("Parallel run=" + runId);
            long cursor = 0;
            while (!terminal)
            {
                await foreach (var item in client.StreamRunEventsAsync(runId, cursor, token))
                {
                    events.Add(JsonSerializer.SerializeToElement(new { item.Id, item.Type, item.Data }));
                    if (item.Type == RunEventTypes.ClientToolProposed)
                    {
                        var proposal = item.Data.Deserialize<ToolProposal>(json)!;
                        Assert.Equal(runId, proposal.RunId);
                        if (!completed.TryGetValue(proposal.ProposalId, out var previous))
                        {
                            LocalToolBroker.ValidateProposal(proposal);
                            Assert.True(proposal.Name is ClientToolNames.CodingRead or ClientToolNames.CodingWrite or ClientToolNames.CodingList or ClientToolNames.CodingGitDiff or ClientToolNames.CodingCommand,
                                "Unexpected fixture tool: " + proposal.Name);
                            if (proposal.Name == ClientToolNames.CodingCommand) Assert.NotNull(proposal.ExecutionScope);
                            if (proposal.Name is ClientToolNames.CodingRead or ClientToolNames.CodingWrite)
                                Assert.True(FileNames.Contains(proposal.Arguments.GetProperty("path").GetString())
                                    || proposal.Name == ClientToolNames.CodingRead && proposal.Arguments.GetProperty("path").GetString() == "verify-secondary.ps1");
                            var result = await broker.ExecuteAsync(proposal, session.Id, turn.AssistantMessage.Id, workspace, cancellationToken: token);
                            previous = (proposal, result);
                            completed.Add(proposal.ProposalId, previous);
                            output.WriteLine(proposal.Summary + " | " + proposal.Name + " | " + result.Status);
                            Assert.Equal("completed", result.Status);
                        }
                        await client.SubmitClientToolResultAsync(runId, previous.Result, token);
                    }
                    if (item.Type is RunEventTypes.ServerToolStarted or RunEventTypes.ServerToolCompleted && item.Data.TryGetProperty("tool", out var tool))
                    {
                        started |= tool.GetString() == "coding.agentStart";
                        joined |= tool.GetString() == "coding.agentWait" && item.Type == RunEventTypes.ServerToolCompleted;
                        childCompleted |= tool.GetString() == "coding.agentStart" && item.Type == RunEventTypes.ServerToolCompleted
                            && item.Data.TryGetProperty("target", out var target) && target.GetString() == "Subagent GPU1"
                            && item.Data.TryGetProperty("success", out var success) && success.GetBoolean();
                    }
                    terminal = item.Type is RunEventTypes.RunCompleted or RunEventTypes.RunFailed or RunEventTypes.RunCancelled;
                    cursor = item.Id;
                    if (terminal) break;
                }
            }
            var snapshot = await client.GetRunAsync(runId, token);
            Assert.Equal(RunState.Completed, snapshot.State);
            Assert.True(started && joined && childCompleted, "Subagent must start, complete successfully and be joined.");
            Assert.Contains(completed.Values, c => c.Proposal.Name == ClientToolNames.CodingCommand
                && c.Proposal.ExecutionScope is not null && c.Result.Result.GetProperty("stdout").GetString()!.Contains("SUBAGENT-TEST-PASSED", StringComparison.Ordinal)
                && c.Result.Result.TryGetProperty("delegation", out _));
            Assert.Equal(mainText, (await File.ReadAllTextAsync(Path.Combine(workspace, "main.txt"), token)).Trim());
            Assert.Equal(secondaryText, (await File.ReadAllTextAsync(Path.Combine(workspace, "secondary.txt"), token)).Trim());
            Assert.Contains(completed.Values, c => c.Proposal.Name == ClientToolNames.CodingWrite && c.Proposal.Summary.StartsWith("Subagent GPU1", StringComparison.Ordinal)
                && c.Proposal.Arguments.GetProperty("path").GetString() == "secondary.txt");
            Assert.Contains(completed.Values, c => c.Proposal.Name == ClientToolNames.CodingWrite && !c.Proposal.Summary.StartsWith("Subagent GPU1", StringComparison.Ordinal)
                && c.Proposal.Arguments.GetProperty("path").GetString() == "main.txt");
            foreach (var path in FileNames)
                Assert.Contains(completed.Values, c => c.Proposal.Name == ClientToolNames.CodingRead && !c.Proposal.Summary.StartsWith("Subagent GPU1", StringComparison.Ordinal)
                    && c.Proposal.Arguments.GetProperty("path").GetString() == path);
        }
        finally
        {
            if (runId is not null && !terminal)
            {
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await client.CancelRunAsync(runId, cancel.Token);
            }
            var evidenceRoot = Environment.GetEnvironmentVariable("GO_AI_PARALLEL_EVIDENCE") ?? Path.Combine(Path.GetTempPath(), "go-parallel-acceptance");
            Directory.CreateDirectory(evidenceRoot);
            var evidence = Path.Combine(evidenceRoot, marker + ".json");
            await File.WriteAllTextAsync(evidence, JsonSerializer.Serialize(new { runId, model, workspace, started, joined, childCompleted,
                mainText, secondaryText, events, tools = completed.Values.Select(c => new { c.Proposal, c.Result }) }, EvidenceJson));
            output.WriteLine("Evidence: " + evidence);
        }
    }
    private sealed class ConnectionSettings(string server) : ISettingsStore
    {
        public string SettingsPath => "in-memory parallel acceptance";
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppSettings { GoAiServerUrl = server });
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No profile writes.");
    }
}
