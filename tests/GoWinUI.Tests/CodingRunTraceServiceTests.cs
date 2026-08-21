using GoAi.Contracts;
using GoWinUI.App.Services;
using GoWinUI.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace GoWinUI.Tests;

public sealed class CodingRunTraceServiceTests
{
    private static readonly string[] PhysicsSmokeArguments = ["physics_solver.py", "--smoke"];
    private static readonly string[] DotNetTestArguments = ["test"];

    [Fact]
    public async Task TraceIsPersistedAndReloadedByAssistantMessage()
    {
        var root = Path.Combine(Path.GetTempPath(), "GO-CodingTraceTests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new GoInfrastructureOptions { DataDirectory = root };
            var localRunId = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            var messageId = Guid.NewGuid();
            var first = new CodingRunTraceService(options, NullLogger<CodingRunTraceService>.Instance);

            await first.StartAsync(localRunId, sessionId, messageId, @"C:\Workspace\Demo");
            await first.AppendAsync(
                localRunId,
                "run-123",
                sessionId,
                messageId,
                "tool",
                "completed",
                "Datei gelesen",
                "Aktion erfolgreich abgeschlossen.",
                ClientToolNames.FileSystemReadText,
                "src/MainWindow.xaml",
                42,
                7);

            var reloaded = new CodingRunTraceService(options, NullLogger<CodingRunTraceService>.Instance);
            var entries = reloaded.GetForMessage(messageId);

            Assert.Equal(2, entries.Count);
            Assert.Equal("Coding-Lauf gestartet", entries[0].Title);
            Assert.Equal("src/MainWindow.xaml", entries[1].Target);
            Assert.Equal(42, entries[1].DurationMilliseconds);
            Assert.True(File.Exists(Path.Combine(root, "CodingRuns", "Traces", $"{messageId:N}.jsonl")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TraceRetainsEveryEntryWhileOnlyTheWebViewViewportIsLimited()
    {
        var root = Path.Combine(Path.GetTempPath(), "GO-CodingTraceTests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new GoInfrastructureOptions { DataDirectory = root };
            var localRunId = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            var messageId = Guid.NewGuid();
            var first = new CodingRunTraceService(options, NullLogger<CodingRunTraceService>.Instance);

            await first.StartAsync(localRunId, sessionId, messageId, @"C:\Workspace\Demo");
            for (var index = 0; index < 1_010; index++)
            {
                await first.AppendAsync(
                    localRunId,
                    "run-all-entries",
                    sessionId,
                    messageId,
                    "tool",
                    "completed",
                    $"Schritt {index + 1}");
            }

            var reloaded = new CodingRunTraceService(options, NullLogger<CodingRunTraceService>.Instance);
            var entries = reloaded.GetForMessage(messageId);

            Assert.Equal(1_011, entries.Count);
            Assert.Equal("Coding-Lauf gestartet", entries[0].Title);
            Assert.Equal("Schritt 1010", entries[^1].Title);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void TraceExtractsOnlyACompactToolTargetAndNeverTheWriteContent()
    {
        var proposal = Proposal(
            ClientToolNames.FileSystemWriteText,
            ToolRiskClass.LocalMutation,
            new
            {
                path = "src/App.xaml.cs",
                content = "SECRET_SOURCE_CONTENT",
            });

        var target = CodingRunTraceService.ExtractTarget(proposal);

        Assert.Equal("src/App.xaml.cs", target);
        Assert.DoesNotContain("SECRET", target, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PowerShellConsoleIsPersistedAndReloadedWithOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), "GO-CodingTraceTests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new GoInfrastructureOptions { DataDirectory = root };
            var localRunId = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            var messageId = Guid.NewGuid();
            var proposal = Proposal(
                ClientToolNames.ProcessRun,
                ToolRiskClass.Process,
                new
                {
                    executable = @".venv\Scripts\python.exe",
                    arguments = PhysicsSmokeArguments,
                    purpose = "start",
                    startMode = "smoke",
                });
            var result = new ClientToolResult(
                proposal.ProposalId,
                "completed",
                JsonSerializer.SerializeToElement(new
                {
                    exitCode = 0,
                    standardOutput = "E0 = 0.499992\nSmoke-Test erfolgreich.",
                    standardError = string.Empty,
                }),
                null,
                null);
            var first = new CodingRunTraceService(options, NullLogger<CodingRunTraceService>.Instance);

            await first.StartAsync(localRunId, sessionId, messageId, @"C:\Workspace\Demo");
            await first.AppendAsync(
                localRunId,
                "run-console",
                sessionId,
                messageId,
                "tool",
                "running",
                "Programm wird gestartet",
                processConsole: CodingRunTraceService.CreateProcessConsole(proposal));
            await first.AppendAsync(
                localRunId,
                "run-console",
                sessionId,
                messageId,
                "tool",
                "completed",
                "Programmstart abgeschlossen",
                processConsole: CodingRunTraceService.CreateProcessConsole(proposal, result));

            var reloaded = new CodingRunTraceService(options, NullLogger<CodingRunTraceService>.Instance);
            var console = reloaded.GetForMessage(messageId)[^1].ProcessConsole;

            Assert.NotNull(console);
            Assert.Equal("completed", console.Status);
            Assert.Equal(0, console.ExitCode);
            Assert.Contains("physics_solver.py --smoke", console.Command, StringComparison.Ordinal);
            Assert.Contains("Smoke-Test erfolgreich", console.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void TestProcessCreatesAPowerShellConsole()
    {
        var proposal = Proposal(
            ClientToolNames.ProcessRun,
            ToolRiskClass.Process,
            new { executable = "dotnet", arguments = DotNetTestArguments, purpose = "test" });

        var console = CodingRunTraceService.CreateProcessConsole(proposal);

        Assert.NotNull(console);
        Assert.Equal("dotnet test", console.Command);
        Assert.Equal("test", console.Purpose);
        Assert.Equal("running", console.Status);
    }

    [Fact]
    public void PowerShellConsoleFiltersBenignGitLineEndingWarningsButKeepsErrors()
    {
        const string input = "warning: in the working copy of 'einstein_engine.py', LF will be replaced by CRLF the next time Git touches it\n"
            + "fatal: test execution failed";

        var filtered = CodingRunTraceService.FilterConsoleNoise(input);

        Assert.Equal("fatal: test execution failed", filtered);
        Assert.Null(CodingRunTraceService.FilterConsoleNoise(
            "warning: in the working copy of 'test.py', CRLF will be replaced by LF the next time Git touches it"));
    }

    [Theory]
    [InlineData("git.status", "git status --short", "inspect")]
    [InlineData("git.diff", "git diff --no-ext-diff", "inspect")]
    [InlineData("dotnet.build", "dotnet build Demo.sln --nologo", "build")]
    [InlineData("dotnet.test", "dotnet test Demo.sln --nologo", "test")]
    [InlineData("repository.verify", "GO-Preset repository.verify Demo.sln", "verify")]
    public void EveryProcessPresetCreatesAPowerShellConsole(string preset, string expectedCommand, string expectedPurpose)
    {
        var proposal = Proposal(
            ClientToolNames.ProcessRunPreset,
            ToolRiskClass.Process,
            new { preset, workspace = @"C:\Workspace\Demo", target = "Demo.sln" });

        var console = CodingRunTraceService.CreateProcessConsole(proposal);

        Assert.NotNull(console);
        Assert.Equal(expectedCommand, console.Command);
        Assert.Equal(expectedPurpose, console.Purpose);
        Assert.Equal(@"C:\Workspace\Demo", console.WorkingDirectory);
    }

    [Fact]
    public async Task NormalPortableCodingToolsAreAuthorizedWithoutAConfirmationDialog()
    {
        using var confirmation = new ToolConfirmationService(null!);
        var tools = new (string Name, ToolRiskClass Risk)[]
        {
            (ClientToolNames.WorkspaceMap, ToolRiskClass.ReadOnly),
            (ClientToolNames.FileSystemReadText, ToolRiskClass.ReadOnly),
            (ClientToolNames.FileSystemReadMany, ToolRiskClass.ReadOnly),
            (ClientToolNames.FileSystemSearch, ToolRiskClass.ReadOnly),
            (ClientToolNames.FileSystemWriteText, ToolRiskClass.LocalMutation),
            (ClientToolNames.FileSystemReplaceText, ToolRiskClass.LocalMutation),
            (ClientToolNames.FileSystemMove, ToolRiskClass.LocalMutation),
            (ClientToolNames.FileSystemProposePatch, ToolRiskClass.LocalMutation),
            (ClientToolNames.FileSystemProposeCreate, ToolRiskClass.LocalMutation),
            (ClientToolNames.FileSystemProposeDelete, ToolRiskClass.LocalMutation),
            (ClientToolNames.ProcessRunPreset, ToolRiskClass.Process),
            (ClientToolNames.ProcessRun, ToolRiskClass.Process),
        };

        foreach (var (name, risk) in tools)
        {
            Assert.True(await confirmation.ConfirmAsync(Proposal(name, risk, new { path = "src/App.xaml.cs" })));
        }
    }

    [Fact]
    public void WebViewRendersTheLiveCodingTraceExpandedAndAcceptsHostUpdates()
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));
        var bridge = File.ReadAllText(Path.Combine(webRoot, "bridge.js"));
        var styles = File.ReadAllText(Path.Combine(webRoot, "styles.css"));

        Assert.Contains("case \"chat.codingTrace\"", app, StringComparison.Ordinal);
        Assert.Contains("case \"chat.codeDiff\"", app, StringComparison.Ordinal);
        Assert.Contains("currentCodingRun: null", app, StringComparison.Ordinal);
        Assert.Contains("function ensureCurrentCodingRun(payload)", app, StringComparison.Ordinal);
        Assert.Contains("function mergeCodingTraceEntries(message, entries)", app, StringComparison.Ordinal);
        Assert.Contains("const liveRun = state.currentCodingRun?.sessionId === state.activeSessionId", app, StringComparison.Ordinal);
        Assert.Contains("Array.isArray(payload.entries)", app, StringComparison.Ordinal);
        Assert.Contains("state.currentCodingRun.status = state.codingCampaign.status === \"faulted\" ? \"failed\" : \"cancelled\"", app, StringComparison.Ordinal);
        Assert.Contains("state.codingCampaign = { ...state.codingCampaign, status: \"stopping\" }", app, StringComparison.Ordinal);
        Assert.Contains("post(\"campaign.stop\", { sessionId: state.activeSessionId })", app, StringComparison.Ordinal);
        Assert.Contains("\"campaign.list\", \"campaign.select\", \"campaign.run\", \"campaign.stop\"", bridge, StringComparison.Ordinal);
        Assert.Contains("function createCodingTrace(message, force = false)", app, StringComparison.Ordinal);
        Assert.Contains("function createPowerShellPanel(message, force = false)", app, StringComparison.Ordinal);
        Assert.Contains("function createCodeDiff(message, force = false)", app, StringComparison.Ordinal);
        Assert.Contains("PowerShell", app, StringComparison.Ordinal);
        Assert.DoesNotContain("Inline-Konsole", app, StringComparison.Ordinal);
        Assert.Contains("if (!force && !visibleEntries.length) return null;", app, StringComparison.Ordinal);
        Assert.Contains("if (!force && !entries.length) return null;", app, StringComparison.Ordinal);
        Assert.DoesNotContain("if (!consoles.length) return null;", app, StringComparison.Ordinal);
        Assert.Contains("Warte auf einen Terminalbefehl", app, StringComparison.Ordinal);
        Assert.Contains("Noch keine Codeänderungen", app, StringComparison.Ordinal);
        Assert.Contains("createCodingTrace(panelMessage, true)", app, StringComparison.Ordinal);
        Assert.Contains("createPowerShellPanel(panelMessage, true)", app, StringComparison.Ordinal);
        Assert.Contains("createCodeDiff(panelMessage, true)", app, StringComparison.Ordinal);
        Assert.Contains("function createCurrentCodingPanels()", app, StringComparison.Ordinal);
        Assert.Contains("elements.messageList.append(currentCodingPanels)", app, StringComparison.Ordinal);
        Assert.DoesNotContain("body.classList.add(\"message-body--coding\")", app, StringComparison.Ordinal);
        Assert.Contains("Coding-Ablauf", app, StringComparison.Ordinal);
        Assert.Contains("details.open = !state.closedCodingTraces.has", app, StringComparison.Ordinal);
        Assert.Contains("details.open = !state.closedPowerShellPanels.has", app, StringComparison.Ordinal);
        Assert.Contains("function collapseCompletedCodingPanels(message, force = false)", app, StringComparison.Ordinal);
        Assert.Contains("collapseCompletedCodingPanels(payload.message, true)", app, StringComparison.Ordinal);
        Assert.Contains("details.open = !state.closedCodeDiffs.has", app, StringComparison.Ordinal);
        Assert.Contains("for (const entry of visibleEntries)", app, StringComparison.Ordinal);
        Assert.Contains("Coding-Modell wird geladen", app, StringComparison.Ordinal);
        Assert.Contains("list.scrollTop = list.scrollHeight", app, StringComparison.Ordinal);
        Assert.Contains("function attachCodingPanelMaximize(details, summary, label, panelKind)", app, StringComparison.Ordinal);
        Assert.Contains("codingPanelMaximizeIcon", app, StringComparison.Ordinal);
        Assert.Contains("state.maximizedCodingPanelKind === panelKind", app, StringComparison.Ordinal);
        Assert.Contains("captureMaximizedCodingPanelScroll();", app, StringComparison.Ordinal);
        Assert.Contains("overflow: auto !important", styles, StringComparison.Ordinal);
        Assert.Contains("position: sticky", styles, StringComparison.Ordinal);
        Assert.Contains("if (shouldSuppressMessageInChat(message)) continue;", app, StringComparison.Ordinal);
        Assert.Contains("if (message?.suppressInChat) return true;", app, StringComparison.Ordinal);
        Assert.Contains("filterPowerShellOutput(item.standardError)", app, StringComparison.Ordinal);
        Assert.DoesNotContain("message.codingTrace.splice", app, StringComparison.Ordinal);
        Assert.Contains("\"chat.codeDiff\", \"chat.codingTrace\"", bridge, StringComparison.Ordinal);
        Assert.Contains(".message-coding-trace", styles, StringComparison.Ordinal);
        Assert.Contains("--coding-panel-viewport-height: 210px", styles, StringComparison.Ordinal);
        Assert.Contains(".message-coding-trace__list { box-sizing: border-box; height: var(--coding-panel-viewport-height); max-height: var(--coding-panel-viewport-height)", styles, StringComparison.Ordinal);
        Assert.Contains(".message-coding-powershell", styles, StringComparison.Ordinal);
        Assert.Contains(".message-coding-powershell__body { box-sizing: border-box; height: var(--coding-panel-viewport-height); max-height: var(--coding-panel-viewport-height)", styles, StringComparison.Ordinal);
        Assert.Contains(".message-coding-panels { --coding-panel-viewport-height: 210px; display: grid", styles, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: repeat(2, minmax(0, 1fr))", styles, StringComparison.Ordinal);
        Assert.Contains(".message-coding-panels > .message-code-diff { grid-column: 1 / -1; }", styles, StringComparison.Ordinal);
        Assert.Contains(".coding-panel--maximized", styles, StringComparison.Ordinal);
        Assert.Contains(".current-coding-workspace", styles, StringComparison.Ordinal);
        Assert.Contains("opacity: 1; pointer-events: auto", styles, StringComparison.Ordinal);

        var currentPanels = app[app.IndexOf("function createCurrentCodingPanels()", StringComparison.Ordinal)..];
        var traceIndex = currentPanels.IndexOf("createCodingTrace(panelMessage, true)", StringComparison.Ordinal);
        var powerShellIndex = currentPanels.IndexOf("createPowerShellPanel(panelMessage, true)", StringComparison.Ordinal);
        var diffIndex = currentPanels.IndexOf("createCodeDiff(panelMessage, true)", StringComparison.Ordinal);
        Assert.True(traceIndex >= 0 && traceIndex < powerShellIndex && powerShellIndex < diffIndex);
    }

    private static ToolProposal Proposal(string name, ToolRiskClass risk, object arguments) => new(
        $"proposal-{Guid.NewGuid():N}",
        "run-test",
        name,
        JsonSerializer.SerializeToElement(arguments),
        risk,
        "Testaktion",
        DateTimeOffset.UtcNow.AddMinutes(5));
}
