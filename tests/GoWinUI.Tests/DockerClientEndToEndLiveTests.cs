using GoAi.Client;
using GoAi.Contracts;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GoWinUI.Tests;

/// <summary>
/// Opt-in End-to-End-Abnahme des Docker-Stacks über exakt die Client- und
/// Tool-Broker-Pfade, die auch GO-WinUI verwendet. Der Test ist bewusst
/// domänenneutral; Zielserver, Modell und freigegebener Workspace kommen aus
/// Umgebungsvariablen.
/// </summary>
public sealed class DockerClientEndToEndLiveTests
{
    private static readonly JsonSerializerOptions ProtocolJson = GoAiProtocol.CreateJsonOptions();

    [Fact]
    [Trait("Category", "Live")]
    public async Task PersistedRoleModelsSurviveGeneralCodingGeneralClientSwitch()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("GO_AI_RUN_MODEL_SWITCH_LIVE"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var workspaceRoot = Environment.GetEnvironmentVariable("GO_AI_LIVE_WORKSPACE")?.Trim();
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
        {
            throw new DirectoryNotFoundException("GO_AI_LIVE_WORKSPACE muss auf einen vorhandenen Testordner zeigen.");
        }

        const string generalModel = "gpt-oss-120b";
        const string codingModel = "qwen3-coder-next";
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(60));

        var firstGeneral = await ExecuteGeneralMathRunAsync(
            generalModel,
            "Antworte exakt mit: General eins bereit.",
            timeout.Token,
            requireMathTool: false);
        Assert.Equal(generalModel, firstGeneral.Snapshot.SelectedModel, ignoreCase: true);

        var workspace = Path.Combine(
            Path.GetFullPath(workspaceRoot),
            $"go-model-switch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(
            Path.Combine(workspace, "README.md"),
            "# Modellwechsel-Smoke\n\nDieser Workspace darf nur analysiert werden.\n",
            new UTF8Encoding(false),
            timeout.Token);
        var sessionId = $"go-model-switch-{Guid.NewGuid():N}";
        await using (var harness = await CodingAgentLiveTestHarness.CreateAsync(
                         "model-switch",
                         workspace,
                         codingModel,
                         sessionId,
                         timeout.Token))
        {
            var coding = await harness.ExecuteAsync(
                sessionId,
                "Analysiere ausschließlich den vorhandenen Workspace und schließe ohne Dateiänderung mit einer kurzen Bestandsbeschreibung ab.",
                "model-switch",
                timeout.Token);
            Assert.Equal(codingModel, coding.Run.SelectedModel, ignoreCase: true);
            Assert.Equal(RunState.Completed, coding.Run.State);
        }

        var secondGeneral = await ExecuteGeneralMathRunAsync(
            generalModel,
            "Antworte exakt mit: General zwei bereit.",
            timeout.Token,
            requireMathTool: false);
        Assert.Equal(generalModel, secondGeneral.Snapshot.SelectedModel, ignoreCase: true);
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task GeneralChatEmitsSeveralVisibleDeltasBeforeCompletion()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("GO_AI_RUN_STREAMING_LIVE"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(40));
        using var http = new HttpClient
        {
            BaseAddress = ResolveServerUrl(),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var client = new GoAiClient(http, $"go-streaming-live-{Guid.NewGuid():N}");
        var modelId = Environment.GetEnvironmentVariable("GO_AI_LIVE_GENERAL_MODEL")?.Trim();
        if (string.IsNullOrWhiteSpace(modelId))
        {
            modelId = "gpt-oss-120b";
        }

        var accepted = await client.CreateRunAsync(
            new RunRequest(
                GoAiProtocol.Version,
                RunMode.General,
                [new RunMessage("user", [new ContentPart(
                    "text",
                    "Erkläre in zwölf kurzen, nummerierten Absätzen auf Deutsch, wie ein zuverlässiger Softwaretest aufgebaut wird. "
                    + "Schreibe normalen Markdown-Fließtext ohne Werkzeuge, JSON oder technische Metadaten.")])],
                Limits: new RunLimits(2_048, 131_072, 2_400),
                SessionId: $"go-streaming-live-{Guid.NewGuid():N}",
                PreferredGeneralModelId: modelId,
                ConversationProfile: ConversationProfile.General),
            $"go-streaming-live-{Guid.NewGuid():N}",
            timeout.Token);

        var deltas = new List<(long EventId, DateTimeOffset SeenAt, string Text)>();
        long? completedEventId = null;
        DateTimeOffset? completedAt = null;
        RunFailedEvent? failure = null;
        await foreach (var item in client.StreamRunEventsAsync(
            accepted.RunId,
            cancellationToken: timeout.Token))
        {
            if (item.Type == RunEventTypes.TextDelta)
            {
                deltas.Add((
                    item.Id,
                    DateTimeOffset.UtcNow,
                    item.Data.Deserialize<TextDeltaEvent>(ProtocolJson)?.Delta ?? string.Empty));
            }
            else if (item.Type == RunEventTypes.RunCompleted)
            {
                completedEventId = item.Id;
                completedAt = DateTimeOffset.UtcNow;
            }
            else if (item.Type == RunEventTypes.RunFailed)
            {
                failure = item.Data.Deserialize<RunFailedEvent>(ProtocolJson);
            }
        }

        var snapshot = await client.GetRunAsync(accepted.RunId, timeout.Token);
        Assert.True(
            snapshot.State == RunState.Completed,
            $"Streaming-Lauf endete als {snapshot.State}: {failure?.ErrorCode ?? snapshot.ErrorCode} · {failure?.Message}");
        Assert.NotNull(completedEventId);
        Assert.NotNull(completedAt);
        Assert.True(deltas.Count >= 2, $"Erwartet wurden mehrere Live-Deltas, tatsächlich empfangen: {deltas.Count}.");
        Assert.All(deltas, delta => Assert.True(delta.EventId < completedEventId));
        Assert.True(
            completedAt - deltas[0].SeenAt >= TimeSpan.FromMilliseconds(250),
            "Das erste Textdelta traf erst gemeinsam mit dem Abschlussereignis ein.");
        Assert.False(string.IsNullOrWhiteSpace(string.Concat(deltas.Select(static delta => delta.Text))));
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task GeneralToolsCodingWebResearchAndModeSwitchUseRealClientPipeline()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("GO_AI_RUN_CLIENT_E2E_LIVE"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var workspaceRoot = Environment.GetEnvironmentVariable("GO_AI_LIVE_WORKSPACE")?.Trim();
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            throw new InvalidOperationException("GO_AI_LIVE_WORKSPACE muss für den mutierenden Live-Test gesetzt sein.");
        }

        workspaceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        if (!Directory.Exists(workspaceRoot))
        {
            throw new DirectoryNotFoundException($"Der freigegebene Live-Workspace fehlt: {workspaceRoot}");
        }

        var modelId = Environment.GetEnvironmentVariable("GO_AI_LIVE_CODING_MODEL")?.Trim();
        if (string.IsNullOrWhiteSpace(modelId))
        {
            modelId = "gpt-oss-120b";
        }

        var scenarioId = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
            + "-"
            + Guid.NewGuid().ToString("N")[..8];
        var workspace = Path.Combine(workspaceRoot, "go-client-e2e-" + scenarioId);
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(
            Path.Combine(workspace, "README.md"),
            "# GO Client E2E\n\nDieser isolierte Ordner wird durch den realen Coding-Agenten bearbeitet.\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(90));
        var firstGeneral = await ExecuteGeneralMathRunAsync(
            modelId,
            "Multipliziere siebzehn mit neunzehn. Verwende dafür zwingend genau einmal math.evaluate und nenne danach kurz das Ergebnis.",
            timeout.Token);
        Assert.Equal("gpt-oss-120b", firstGeneral.Snapshot.SelectedModel, ignoreCase: true);
        Assert.Contains("math.evaluate", firstGeneral.ServerTools);

        var sessionId = $"docker-client-e2e-{Guid.NewGuid():N}";
        await using var harness = await CodingAgentLiveTestHarness.CreateAsync(
            "docker-client-e2e",
            workspace,
            modelId,
            sessionId,
            timeout.Token);
        var coding = await harness.ExecuteAsync(
            sessionId,
            "Führe zuerst eine Websuche ausschließlich in der offiziellen Python-Dokumentation zu datetime.fromisoformat "
                + "und Zeitzonen durch und rufe mindestens eine relevante Fundstelle auf. Erstelle danach in diesem Workspace "
                + "eine kleine, eigenständige Python-Bibliothek iso_time.py. Sie muss ISO-8601-Zeitstempel robust einlesen und "
                + "als UTC-Zeitstempel mit dem Suffix Z normalisieren. Lege test_iso_time.py mit unittest-Tests für UTC, positive "
                + "und negative Offsets sowie ungültige Eingaben an. Ergänze README.md um Bedienung, Entwurfsentscheidung und die "
                + "tatsächlich abgerufene offizielle Quelle mit Titel und URL. Führe die Tests sowie eine Python-Kompilierungsprüfung "
                + "selbstständig aus und behebe Fehler, bevor du den Auftrag abschließt.",
            "docker-client-e2e",
            timeout.Token);

        CodingAgentLiveTestHarness.AssertSuccessful(
            coding,
            modelId,
            requireMutation: true,
            requireVerification: false);
        Assert.Equal(1, coding.ToolNames.Count(static name => name == "web.search"));
        // Up to one replacement source is allowed when one of the three desired
        // pages cannot be fetched or parsed. The server itself caps attempts at four.
        Assert.InRange(coding.ToolNames.Count(static name => name == "web.fetch"), 1, 4);
        Assert.Contains("test", coding.VerificationPurposes);
        Assert.Contains("build", coding.VerificationPurposes);
        Assert.True(File.Exists(Path.Combine(workspace, "iso_time.py")), "Der Agent hat iso_time.py nicht erstellt.");
        Assert.True(File.Exists(Path.Combine(workspace, "test_iso_time.py")), "Der Agent hat test_iso_time.py nicht erstellt.");

        var independentTest = await RunProcessAsync(
            workspace,
            "python",
            ["-m", "unittest", "-v"],
            timeout.Token);
        Assert.True(
            independentTest.ExitCode == 0,
            $"Die unabhängige unittest-Abnahme ist fehlgeschlagen.\nSTDOUT:\n{independentTest.StandardOutput}\nSTDERR:\n{independentTest.StandardError}");

        var secondGeneral = await ExecuteGeneralMathRunAsync(
            modelId,
            "Addiere einhundertvierundvierzig und neunundachtzig. Verwende dafür zwingend genau einmal math.evaluate und nenne danach kurz das Ergebnis.",
            timeout.Token);
        Assert.Equal(firstGeneral.Snapshot.SelectedModel, secondGeneral.Snapshot.SelectedModel, ignoreCase: true);
        Assert.Contains("math.evaluate", secondGeneral.ServerTools);

        var serverUrl = ResolveServerUrl();
        using var http = new HttpClient { BaseAddress = serverUrl, Timeout = TimeSpan.FromSeconds(30) };
        using var client = new GoAiClient(http, $"go-client-e2e-status-{Guid.NewGuid():N}");
        var status = await client.GetModelStatusAsync(timeout.Token);
        var sharedModel = Assert.Single(status.Models, item =>
            string.Equals(item.Id, "gpt-oss-120b", StringComparison.OrdinalIgnoreCase));
        Assert.True(sharedModel.Loaded, "Das gemeinsame General-/Coding-Modell ist nach dem Moduswechsel nicht mehr geladen.");
    }

    private static async Task<GeneralRunObservation> ExecuteGeneralMathRunAsync(
        string modelId,
        string prompt,
        CancellationToken cancellationToken,
        bool requireMathTool = true)
    {
        using var http = new HttpClient
        {
            BaseAddress = ResolveServerUrl(),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var client = new GoAiClient(http, $"go-client-e2e-general-{Guid.NewGuid():N}");
        var accepted = await client.CreateRunAsync(
            new RunRequest(
                GoAiProtocol.Version,
                RunMode.General,
                [new RunMessage("user", [new ContentPart("text", prompt)])],
                Limits: new RunLimits(4_096, 131_072, 3_600),
                SessionId: $"docker-client-e2e-general-{Guid.NewGuid():N}",
                AllowedServerTools: requireMathTool ? ["math.evaluate"] : [],
                PreferredGeneralModelId: modelId,
                ConversationProfile: ConversationProfile.General),
            $"docker-client-e2e-general-{Guid.NewGuid():N}",
            cancellationToken);

        var serverTools = new List<string>();
        var text = new StringBuilder();
        RunFailedEvent? failure = null;
        await foreach (var item in client.StreamRunEventsAsync(
            accepted.RunId,
            cancellationToken: cancellationToken))
        {
            if (item.Type == RunEventTypes.ServerToolCompleted
                && item.Data.TryGetProperty("tool", out var tool)
                && tool.ValueKind == JsonValueKind.String
                && tool.GetString() is { Length: > 0 } toolName)
            {
                serverTools.Add(toolName);
            }
            else if (item.Type == RunEventTypes.TextDelta)
            {
                text.Append(item.Data.Deserialize<TextDeltaEvent>(ProtocolJson)?.Delta);
            }
            else if (item.Type == RunEventTypes.RunFailed)
            {
                failure = item.Data.Deserialize<RunFailedEvent>(ProtocolJson);
            }
        }

        var snapshot = await client.GetRunAsync(accepted.RunId, cancellationToken);
        Assert.True(
            snapshot.State == RunState.Completed,
            $"General-AI-Lauf {accepted.RunId} endete als {snapshot.State}: {failure?.ErrorCode ?? snapshot.ErrorCode} · {failure?.Message}");
        Assert.False(string.IsNullOrWhiteSpace(text.ToString()));
        return new GeneralRunObservation(snapshot, serverTools, text.ToString());
    }

    private static Uri ResolveServerUrl()
    {
        var value = Environment.GetEnvironmentVariable("GO_AI_SERVER_URL")?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            value = "http://127.0.0.1:8080";
        }
        return new Uri(value.TrimEnd('/') + "/", UriKind.Absolute);
    }

    private static async Task<ProcessObservation> RunProcessAsync(
        string workingDirectory,
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new ProcessObservation(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }

    private sealed record GeneralRunObservation(
        RunSnapshot Snapshot,
        IReadOnlyList<string> ServerTools,
        string Text);

    private sealed record ProcessObservation(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
