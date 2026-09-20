using System.Text.Json;
using GoAi.Client;
using GoAi.Contracts;
using GoWinUI.App.Services;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace GoWinUI.Tests;

/// <summary>Opt-in real model, Windows, vision, document and Blender acceptance.</summary>
public sealed class WorkspaceToolsLiveTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [Fact]
    [Trait("Category", "Live")]
    public async Task GeneralAndCodingUseVisualDocumentAndBlenderTools()
    {
        if (Environment.GetEnvironmentVariable("GO_WORKSPACE_TOOLS_LIVE") != "1") return;
        foreach (var mode in new[] { RunMode.General, RunMode.Coding })
            foreach (var scenario in new[] { "document", "blender", "visual" })
            {
                var selectedMode = Environment.GetEnvironmentVariable("GO_WORKSPACE_LIVE_MODE");
                var selectedScenario = Environment.GetEnvironmentVariable("GO_WORKSPACE_LIVE_SCENARIO");
                if (selectedMode is not null && !mode.ToString().Equals(selectedMode, StringComparison.OrdinalIgnoreCase)) continue;
                if (selectedScenario is not null && scenario != selectedScenario) continue;
                await RunAsync(mode, scenario);
            }
    }

    private async Task RunAsync(RunMode mode, string scenario)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var marker = "GO" + Guid.NewGuid().ToString("N")[..10];
        var evidenceRoot = Environment.GetEnvironmentVariable("GO_WORKSPACE_LIVE_EVIDENCE")
            ?? Path.Combine(Path.GetTempPath(), "go-workspace-live");
        var workspace = Path.GetFullPath(Path.Combine(evidenceRoot, mode + "-" + scenario + "-" + marker));
        Directory.CreateDirectory(workspace);
        var prompt = scenario switch
        {
            "document" => $"Erstelle mit document.create Bericht.docx. Erstelle zunächst den Abschnitt Ziel mit dem Text {marker}. Lies das erstellte Dokument dann mit document.read und ergänze anschließend einen zweiten Abschnitt Ergebnis mit dem Text PRUEFUNG-OK. Prüfe durch erneutes Lesen, dass beide Texte enthalten sind. Liefere das fertige DOCX als Dokumentartefakt. Keine Websuche und keine Delegation.",
            "blender" => "Erstelle in Blender eine einfache 3D-Szene mit einem blauen Würfel auf einer hellgrauen Fläche, Kamera und Licht. Speichere scene.blend und rendere render.png im Workspace. Erstelle das nötige Python-Skript selbst im Workspace und führe es mit dem Blender-Werkzeug aus. Analysiere das Renderbild mit Bild analysieren und prüfe die Farbe und Sichtbarkeit. Öffne die fertige scene.blend in der Blender-Oberfläche. Keine Websuche und keine Paketinstallation.",
            _ => $"Erstelle index.html als kleine lokale Webanwendung mit dem Fenstertitel {marker}, weißem Hintergrund und einer auffälligen roten Statuskarte mit Text FEHLER. Starte die Anwendung in einem eigenen Fenster, erfasse einen Screenshot genau dieses Fensters und analysiere ihn mit Bild analysieren. Ändere erst danach anhand des sichtbaren Befunds die rote Karte in eine grüne mit Text BEREIT. Öffne die aktualisierte Anwendung erneut, erfasse sie erneut und prüfe das neue Bild. "
                + "Verwende dafür die angebotenen Workspace-, Fenster- und Bildwerkzeuge. Keine Websuche, kein manuelles Raten anhand des Quellcodes und keine Browserautomation per Terminal.",
        };
        var server = Environment.GetEnvironmentVariable("GO_AI_SERVER_URL") ?? "http://127.0.0.1:8080";
        var model = Environment.GetEnvironmentVariable("GO_AI_LIVE_CODING_MODEL") ?? "coding/Qwen3.8-27B-UD-Q8_K_XL~86704cb9d896";
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync(mode + " " + scenario);
        await chats.SetCodingWorkspacePathAsync(session.Id, workspace);
        var turn = await chats.AddTurnAsync(session.Id, prompt);
        using var settings = new SettingsCoordinator(new ConnectionSettings(server));
        await settings.InitializeAsync();
        using var connection = new GoAiConnectionService(settings, NullLogger<GoAiConnectionService>.Instance);
        using var exporter = new DocumentPdfExporter(NullLogger<DocumentPdfExporter>.Instance);
        var documentTools = new LocalDocumentToolService(environment.Get<IGeneratedDocumentRepository>(), environment.Get<IDocumentIngestor>(),
            environment.Get<IChatArtifactRepository>(), environment.Get<IBinaryObjectStore>(), chats, environment.Get<IDocumentFileCodec>(), exporter);
        var broker = new LocalToolBroker(connection, null!, environment.Get<IDocumentIngestor>(), documentTools, chats);
        using var http = new HttpClient { BaseAddress = new Uri(server.TrimEnd('/') + "/"), Timeout = Timeout.InfiniteTimeSpan };
        using var client = new GoAiClient(http, "workspace-live-" + marker);
        // Text/vision transitions may reload large local models. Keep a hard
        // bound without mistaking those measured load times for tool failure.
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(45));
        var token = deadline.Token;
        var events = new List<RunEvent>();
        var results = new Dictionary<string, (ToolProposal Proposal, ClientToolResult Result)>();
        string? runId = null;
        var terminal = false;
        var passed = false;
        try
        {
            var accepted = await client.CreateRunAsync(new RunRequest(GoAiProtocol.Version, mode,
                [new("user", [new("text", prompt)])], ClientCapabilities: ["coding", "coding.evidence", "workspace", "documentIo", "documents", "visual-tools", "blender"],
                SessionId: session.Id.ToString("D"), PreferredGeneralModelId: model, PreferredCodingModelId: mode == RunMode.Coding ? model : null,
                CodingOptions: mode == RunMode.Coding ? new(WorkspacePath: workspace) : null,
                // The selected model's advertised default also supports DeepSeek
                // (on/none); Qwen-specific effort names must not leak into this gate.
                AllowedServerTools: ["media.analyze", "media.inspect", "web.search", "web.fetch", "image.generate", "math.evaluate"]),
                "workspace-" + marker, token);
            runId = accepted.RunId;
            await File.WriteAllTextAsync(Path.Combine(workspace, "run.json"), JsonSerializer.Serialize(new { mode, scenario, prompt, runId }, Json), token);
            output.WriteLine(mode + " " + scenario + " " + runId + " workspace=" + workspace);
            long cursor = 0;
            while (!terminal)
            {
                await foreach (var item in client.StreamRunEventsAsync(runId, cursor, token))
                {
                    events.Add(item);
                    cursor = item.Id;
                    if (item.Type == RunEventTypes.ClientToolProposed)
                    {
                        var proposal = item.Data.Deserialize<ToolProposal>(GoAiProtocol.CreateJsonOptions())!;
                        if (!results.TryGetValue(proposal.ProposalId, out var completed))
                        {
                            var result = await broker.ExecuteAsync(proposal, session.Id, turn.AssistantMessage.Id, workspace, cancellationToken: token);
                            completed = (proposal, result);
                            results.Add(proposal.ProposalId, completed);
                            await File.WriteAllTextAsync(Path.Combine(workspace, "tools.json"), JsonSerializer.Serialize(
                                results.Values.Select(t => new { t.Proposal, t.Result }), Json), token);
                            output.WriteLine(proposal.Name + " => " + result.Status + " " + result.Message);
                        }
                        await client.SubmitClientToolResultAsync(runId, completed.Result, token);
                    }
                    if (item.Type is RunEventTypes.RunCompleted or RunEventTypes.RunFailed or RunEventTypes.RunCancelled)
                    {
                        terminal = true;
                        Assert.Equal(RunEventTypes.RunCompleted, item.Type);
                    }
                }
            }
            var tools = results.Values.ToArray();
            if (scenario == "document")
            {
                Assert.True(tools.Count(t => t.Proposal.Name == ClientToolNames.DocumentCreate && t.Result.Status == "completed") >= 2);
                Assert.True(tools.Count(t => t.Proposal.Name == ClientToolNames.DocumentRead && t.Result.Status == "completed") >= 2);
                var artifacts = await environment.Get<IChatArtifactRepository>().ListForMessageAsync(turn.AssistantMessage.Id);
                var docx = artifacts.Where(a => a.FileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(a => int.Parse(a.Metadata!["documentRevision"], System.Globalization.CultureInfo.InvariantCulture)).First();
                var path = Path.Combine(workspace, "Bericht.docx");
                await using (var stream = File.Create(path)) await environment.Get<IBinaryObjectStore>().ExportAsync(docx.BlobId, stream, token);
                var text = string.Join("\n", await environment.Get<IDocumentFileCodec>().ReadAsync(path, token));
                Assert.Contains(marker, text);
                Assert.Contains("PRUEFUNG-OK", text);
            }
            else
            {
                Assert.Contains(events, e => e.Type == RunEventTypes.ServerToolCompleted && e.Data.TryGetProperty("tool", out var name) && name.GetString() == "media.analyze"
                    && (!e.Data.TryGetProperty("success", out var success) || success.GetBoolean()));
                if (scenario == "blender")
                {
                    Assert.True(new FileInfo(Path.Combine(workspace, "scene.blend")).Length > 1000);
                    Assert.True(new FileInfo(Path.Combine(workspace, "render.png")).Length > 100);
                    Assert.Contains(tools, t => t.Proposal.Name == WorkspaceTools.Blender && t.Proposal.Arguments.GetProperty("operation").GetString() == "open" && t.Result.Status == "completed");
                }
                else
                {
                    Assert.Contains("BEREIT", await File.ReadAllTextAsync(Path.Combine(workspace, "index.html"), token));
                    Assert.True(tools.Count(t => t.Proposal.Name == WorkspaceTools.ImageInput && t.Proposal.Arguments.GetProperty("operation").GetString() == "capture" && t.Result.Status == "completed") >= 2);
                    var analyses = events.Where(e => e.Type == RunEventTypes.ServerToolCompleted
                        && e.Data.TryGetProperty("tool", out var name) && name.GetString() == "media.analyze")
                        .Select(e => e.Data.GetProperty("result").GetProperty("analysis").GetString() ?? "").ToArray();
                    Assert.True(analyses.Length >= 2);
                    Assert.Contains(analyses, text => text.Contains("FEHLER", StringComparison.OrdinalIgnoreCase));
                    Assert.Contains(analyses, text => text.Contains("BEREIT", StringComparison.OrdinalIgnoreCase));
                    var firstAnalysis = events.FindIndex(e => e.Type == RunEventTypes.ServerToolCompleted
                        && e.Data.TryGetProperty("tool", out var name) && name.GetString() == "media.analyze");
                    Assert.Contains(events.Skip(firstAnalysis + 1), e => e.Type == RunEventTypes.ClientToolProposed
                        && e.Data.TryGetProperty("name", out var name) && name.GetString() is "coding.write" or "coding.edit"
                        && e.Data.GetProperty("arguments").GetProperty("path").GetString() == "index.html");
                }
            }
            passed = true;
        }
        finally
        {
            if (runId is not null && !terminal)
            {
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await client.CancelRunAsync(runId, cancel.Token);
            }
            await File.WriteAllTextAsync(Path.Combine(workspace, "acceptance.json"), JsonSerializer.Serialize(new
                { mode, scenario, prompt, runId, passed, events, tools = results.Values.Select(t => new { t.Proposal, t.Result }) }, Json));
        }
    }

    private sealed class ConnectionSettings(string server) : ISettingsStore
    {
        public string SettingsPath => "in-memory workspace acceptance";
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppSettings { GoAiServerUrl = server });
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No user-profile writes.");
    }
}
