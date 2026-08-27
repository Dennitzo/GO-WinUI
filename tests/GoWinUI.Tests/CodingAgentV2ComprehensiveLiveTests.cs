using GoAi.Contracts;
using System.Globalization;
using System.Text;

namespace GoWinUI.Tests;

/// <summary>
/// Opt-in, domain-neutral acceptance test for Coding Agent V2. It exercises the
/// same gateway, model, workspace index, and local tool broker as GO-WinUI.
/// The generated workspace is intentionally retained for manual inspection.
/// </summary>
public sealed class CodingAgentV2ComprehensiveLiveTests
{
    private static readonly string[] FacadeTools =
    [
        "workspace.inspect",
        "workspace.change",
        "execution.run",
        "research.query",
        "artifact.process",
        "task.finish",
    ];

    [Fact]
    [Trait("Category", "Live")]
    public async Task EmptyWorkspaceCompletesMultipleRunsThroughEveryV2Facade()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("GO_AI_RUN_CODING_V2_COMPREHENSIVE_LIVE"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var workspaceRoot = Environment.GetEnvironmentVariable("GO_AI_LIVE_WORKSPACE")?.Trim();
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            throw new InvalidOperationException("GO_AI_LIVE_WORKSPACE muss auf einen freigegebenen Testordner zeigen.");
        }

        workspaceRoot = Path.GetFullPath(workspaceRoot);
        Directory.CreateDirectory(workspaceRoot);
        var scenarioId = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
            + "-"
            + Guid.NewGuid().ToString("N")[..8];
        var workspace = Path.Combine(workspaceRoot, "coding-agent-v2-real-" + scenarioId);
        Directory.CreateDirectory(workspace);
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace));

        var modelId = Environment.GetEnvironmentVariable("GO_AI_LIVE_CODING_MODEL")?.Trim();
        if (string.IsNullOrWhiteSpace(modelId))
        {
            modelId = "qwen3.8-27b";
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromHours(3));
        var sessionId = $"coding-agent-v2-real-{Guid.NewGuid():N}";
        await using var harness = await CodingAgentLiveTestHarness.CreateAsync(
            "coding-agent-v2-comprehensive",
            workspace,
            modelId,
            sessionId,
            timeout.Token);

        var creation = await harness.ExecuteAsync(
            sessionId,
            "Der freigegebene Workspace ist anfangs leer. Untersuche ihn zuerst. Recherchiere danach mit SearXNG "
                + "in der offiziellen Python-Dokumentation das Modul decimal und rufe genau eine passende offizielle Seite auf. "
                + "Berechne außerdem deterministisch siebzehn mal neunzehn über das Mathematikwerkzeug. Erstelle anschließend "
                + "eine kleine deutsche Python-Bibliothek src/einheiten_rechner.py, die Dezimalwerte sicher addiert und Celsius "
                + "in Kelvin umrechnet. Lege Tests unter tests/test_einheiten_rechner.py sowie eine deutsche README.md mit der "
                + "verwendeten Quelle und dem berechneten Kontrollwert an. Führe unittest und py_compile aus, behebe gefundene "
                + "Fehler und schließe erst nach erfolgreichen Prüfungen ab.",
            "coding-agent-v2-create",
            timeout.Token);
        AssertCompleted(creation, modelId);
        Assert.Contains(creation.AgentActions, static action => action.Tool == "research.query" && action.Operation == "webSearch");
        Assert.Contains(creation.AgentActions, static action => action.Tool == "research.query" && action.Operation == "webFetch");
        Assert.Contains(creation.AgentActions, static action => action.Tool == "artifact.process" && action.Operation == "mathEvaluate");
        Assert.Contains(creation.AgentActions, static action => action.Tool == "workspace.change");
        Assert.Contains(creation.AgentActions, static action => action.Tool == "execution.run");
        Assert.Contains(creation.AgentActions, static action => action.Tool == "task.finish");
        Assert.True(File.Exists(Path.Combine(workspace, "src", "einheiten_rechner.py")));
        Assert.True(File.Exists(Path.Combine(workspace, "tests", "test_einheiten_rechner.py")));
        Assert.True(File.Exists(Path.Combine(workspace, "README.md")));

        var extension = await harness.ExecuteAsync(
            sessionId,
            "Analysiere die vorhandene Implementierung mit den indexgestützten Such-, Symbol- und Lesefunktionen. Erweitere "
                + "sie um eine robuste Kelvin-nach-Celsius-Umrechnung und ergänze Grenzfalltests für den absoluten Nullpunkt. "
                + "Verwende die gelesene Dateiversion für jede Änderung, führe danach wieder unittest und py_compile aus und "
                + "schließe nur mit belegten Änderungen und erfolgreichen Prüfungen ab.",
            "coding-agent-v2-extend",
            timeout.Token);
        AssertCompleted(extension, modelId);
        Assert.Contains(extension.AgentActions, static action => action.Tool == "workspace.inspect");
        Assert.Contains(extension.AgentActions, static action => action.Tool == "workspace.change");
        Assert.Contains(extension.AgentActions, static action => action.Tool == "execution.run");
        Assert.Contains(extension.AgentActions, static action => action.Tool == "task.finish");

        var verification = await harness.ExecuteAsync(
            sessionId,
            "Führe eine unabhängige Schlussabnahme des vorhandenen Projekts ohne weitere Dateiänderung durch. Nutze den "
                + "persistenten Workspaceindex gezielt statt alle Dateien erneut vollständig einzulesen. Prüfe die relevanten "
                + "Dateien und starte die vorhandenen Tests sowie die Kompilierungsprüfung. Schließe mit den konkreten Belegen "
                + "und Prüfungen ab.",
            "coding-agent-v2-verify",
            timeout.Token);
        AssertCompleted(verification, modelId);
        Assert.Empty(verification.MutationTools);
        Assert.Contains(verification.AgentActions, static action => action.Tool == "workspace.inspect");
        Assert.Contains(verification.AgentActions, static action => action.Tool == "execution.run");
        Assert.Contains(verification.AgentActions, static action => action.Tool == "task.finish");

        var allActions = creation.AgentActions
            .Concat(extension.AgentActions)
            .Concat(verification.AgentActions)
            .ToArray();
        foreach (var facade in FacadeTools)
        {
            Assert.Contains(allActions, action => action.Tool == facade);
        }
        Assert.All(
            creation.AgentObservations.Concat(extension.AgentObservations).Concat(verification.AgentObservations),
            static observation => Assert.False(string.IsNullOrWhiteSpace(observation.ActionId)));

        var independent = await RunPythonAsync(workspace, timeout.Token);
        Assert.True(independent.ExitCode == 0, independent.Output);

        await File.WriteAllTextAsync(
            Path.Combine(workspace, "LIVE_TEST_RESULT.txt"),
            $"PASS\nModel={modelId}\nRuns=3\nFacades={string.Join(',', FacadeTools)}\n",
            new UTF8Encoding(false),
            timeout.Token);
        Console.WriteLine($"Beibehaltener Live-Test-Workspace: {workspace}");
    }

    private static void AssertCompleted(CodingAgentLiveRunObservation observation, string modelId)
    {
        Assert.True(
            observation.Run.State == RunState.Completed,
            $"Run endete als {observation.Run.State}: {observation.Failure?.ErrorCode ?? observation.Run.ErrorCode} · "
                + $"{observation.Failure?.Message}. Log: {observation.LogPath}");
        Assert.Equal(modelId, observation.Run.SelectedModel, ignoreCase: true);
        Assert.False(string.IsNullOrWhiteSpace(observation.VisibleText));
        Assert.Contains(
            observation.AgentMessages,
            static message => message.Phase == AgentMessagePhase.Commentary);
        var finalMessage = Assert.Single(
            observation.AgentMessages,
            static message => message.Phase == AgentMessagePhase.FinalAnswer);
        Assert.Equal(observation.VisibleText, finalMessage.Text);
        Assert.Single(observation.AgentMessages.Select(static message => message.ItemId).Distinct(StringComparer.Ordinal));
        Assert.Equal(
            observation.AgentActions.Select(static action => action.ActionId).Distinct(StringComparer.Ordinal).Count(),
            observation.AgentActions.Count);
        Assert.Equal(
            observation.AgentObservations.Select(static item => item.ActionId).Distinct(StringComparer.Ordinal).Count(),
            observation.AgentObservations.Count);
    }

    private static async Task<(int ExitCode, string Output)> RunPythonAsync(
        string workspace,
        CancellationToken cancellationToken)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "python",
                WorkingDirectory = workspace,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("-m");
        process.StartInfo.ArgumentList.Add("unittest");
        process.StartInfo.ArgumentList.Add("discover");
        process.StartInfo.ArgumentList.Add("-s");
        process.StartInfo.ArgumentList.Add("tests");
        process.StartInfo.ArgumentList.Add("-v");
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await stdout + Environment.NewLine + await stderr);
    }
}
