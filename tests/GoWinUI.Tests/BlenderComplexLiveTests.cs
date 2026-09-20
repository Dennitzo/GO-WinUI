using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using GoAi.Client;
using GoAi.Contracts;
using GoWinUI.App.Services;
using GoWinUI.Core.Coding;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit.Abstractions;
using ContentPart = GoAi.Contracts.ContentPart;

namespace GoWinUI.Tests;

/// <summary>
/// Opt-in acceptance using the real local model, broker, document ingestion,
/// image uploads and Blender. No scene-building script or model answer is supplied.
/// A separate Blender process audits the saved geometry and injects one disclosed
/// visibility defect into a copy to test actual vision-led repair on the next turn.
/// </summary>
public sealed class BlenderComplexLiveTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] OriginalComponents = ["Habitat", "Airlock", "Greenhouse", "Solar", "Rover", "Connector"];
    private static readonly string[] OrthogonalViews = ["front", "right", "top", "back", "left"];
    private const string SteeringComponentPrefix = "Weather";
    private const string SteeringPrompt = "Ergänze zusätzlich in einer späteren Etappe einen separaten kleinen Wetter-Sensor an einer sinnvoll frei stehenden Position. "
        + "Seine Mesh-Bauteile sollen mit Weather beginnen. Gestalte ihn passend zur Station. "
        + "Prüfe zuerst den gerade gespeicherten Zwischenstand wie geplant; erhalte die bisherigen Bauteile und alle ursprünglichen Anforderungen.";
    private static readonly string[] BriefingParagraphs =
    [
        "MARSSTATION AURORA - Entwurfsbriefing",
        "Entwirf eine glaubwürdige, modulare Forschungsstation für vier Menschen. Du hast gestalterische Freiheit bei Formen, Details, Farben und Materialien. Maßstab: Meter; ungefähr 26 x 20 Meter Gesamtanlage, Habitat etwa 6 x 5 x 4 Meter. Es geht um ein editierbares visuelles 3D-Modell, nicht um einen statischen Nachweis.",
        "Das beigefügte Bild ist ein grober Lageplan, keine Vorlage fertiger 3D-Geometrie. Bild rechts entspricht +X, Bild oben +Y. Die räumliche Anordnung aus dem Bild soll erkennbar bleiben. Legende: heller Kreis = Habitat; orangefarbener Kasten = Luftschleuse; grüner Kasten = Gewächshaus; dunkelblaue gerasterte Rechtecke = erste Solargruppe; kleines dunkles Fahrzeug = Rover; helle Streifen = begehbare Verbindungen.",
        "Habitat mit Wandverkleidung, Fenstern und Dachdetails; Luftschleuse mit Türrahmen und erkennbarer Zugangsstufe; verbundenes Gewächshaus mit Glasflächen, Tragrahmen und Pflanzenbeeten; Solargruppe mit getrennten Paneelen und tragenden Stützen; Rover mit Karosserie, mindestens vier Rädern, Kabine und Antenne. Verbindungen müssen die Module räumlich erreichen. Boden, Beleuchtung und Kamera sinnvoll selbst gestalten.",
        "Modellorganisation: aussagekräftige Mesh-Namen beginnen mit Habitat, Airlock, Greenhouse, Solar, Rover oder Connector. Diese Präfixe erleichtern spätere Änderungen. Keine zusammengefügte Einheitsmasse. Mindestens 30 sinnvoll unterschiedliche Mesh-Bauteile und mindestens sechs Materialien; Detailgrad und räumliche Lesbarkeit sind wichtiger als Polygonzahl.",
        "Halte Anforderungen, Annahmen, Maße und Designentscheidungen im Projekt fest. Verwende Blender-scaffold als technische Hilfe und entwickle den Entwurf selbst in mindestens drei sichtbaren, überschaubaren Etappen. Jede Etappe erhält eine neue kleine Python-Datei und eine eigene gespeicherte Szenenrevision; baue auf dem jeweils geprüften Zwischenstand auf. Prüfe jeden Zwischenstand geometrisch und mit mindestens einem tatsächlichen Renderbild durch Vision, bevor du weiterbaust. Die fertige Szene braucht eine Perspektive und mindestens zwei geeignete orthogonale Ansichten, von denen mindestens zwei durch Vision geprüft werden. Transparenz, erkennbare Verbindungsgänge, nicht verdeckte Solarpaneele und vollständig sichtbarer Rover sind Prüfkriterien. Bei konkreten Befunden korrigieren und erneut prüfen.",
    ];
    private const string DefaultModel = "coding/DeepSeek-V4-Flash-Vision-Exp-UD-IQ1_S~31b467a216d0";

    [Fact]
    [Trait("Category", "Live")]
    public async Task LocalAiBuildsAndVisuallyRepairsComplexStationAcrossTurns()
    {
        if (Environment.GetEnvironmentVariable("GO_BLENDER_COMPLEX_LIVE") != "1") return;
        var blender = WorkspaceToolService.FindBlender();
        Assert.False(string.IsNullOrEmpty(blender), "Install Blender or set GO_BLENDER_EXECUTABLE before this opt-in test.");
        var evidence = Path.GetFullPath(Path.Combine(Environment.GetEnvironmentVariable("GO_BLENDER_LIVE_EVIDENCE")
            ?? Path.Combine(Path.GetTempPath(), "go-blender-complex-live"), DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8]));
        var workspace = Path.Combine(evidence, "workspace");
        Directory.CreateDirectory(workspace);
        output.WriteLine("Blender acceptance evidence: " + evidence);
        var server = Environment.GetEnvironmentVariable("GO_AI_SERVER_URL") ?? "http://127.0.0.1:8080";
        var model = Environment.GetEnvironmentVariable("GO_AI_BLENDER_MODEL") ?? DefaultModel;
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Marsstation: Anhang, Entwurf und visuelle Reparatur");
        await chats.SetCodingWorkspacePathAsync(session.Id, workspace);
        using var settings = new SettingsCoordinator(new ConnectionSettings(server));
        await settings.InitializeAsync();
        using var connection = new GoAiConnectionService(settings, NullLogger<GoAiConnectionService>.Instance);
        using var exporter = new DocumentPdfExporter(NullLogger<DocumentPdfExporter>.Instance);
        var documentTools = new LocalDocumentToolService(environment.Get<IGeneratedDocumentRepository>(), environment.Get<IDocumentIngestor>(),
            environment.Get<IChatArtifactRepository>(), environment.Get<IBinaryObjectStore>(), chats, environment.Get<IDocumentFileCodec>(), exporter);
        var broker = new LocalToolBroker(connection, null!, environment.Get<IDocumentIngestor>(), documentTools, chats);
        using var http = new HttpClient { BaseAddress = new Uri(server.TrimEnd('/') + "/"), Timeout = Timeout.InfiniteTimeSpan };
        using var client = new GoAiClient(http, "blender-complex-" + session.Id.ToString("N"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromHours(4));
        var token = deadline.Token;
        var turns = new List<TurnEvidence>();
        var audits = new List<JsonElement>();
        string? failure = null;
        var passed = false;
        try
        {
            await SaveJsonAsync(Path.Combine(evidence, "preflight.json"), new
            {
                server, model, blender, sessionId = session.Id,
                gateway = await http.GetStringAsync("v1/health/ready", token),
                models = await http.GetStringAsync("v1/models/status", token),
                gpu = await http.GetStringAsync("v1/gpu/status", token),
            });
            var documentPath = Path.Combine(workspace, "Stationsbriefing.docx");
            CreateBriefing(documentPath);
            await using (var document = File.OpenRead(documentPath))
            {
                var imported = await environment.Get<IDocumentIngestor>().ImportAsync(session.Id, Path.GetFileName(documentPath), document, token);
                Assert.True(imported.Success && imported.HasExtractableText, imported.Error);
            }
            var referencePath = Path.Combine(workspace, "Grundriss.png");
            await CreateReferenceImageAsync(referencePath, token);
            var referenceSha256 = await HashFileContentAsync(referencePath, token);
            var upload = await client.UploadFileAsync(referencePath, "image/png", cancellationToken: token);
            await SaveJsonAsync(Path.Combine(evidence, "reference-upload.json"), new
            {
                uploadId = upload.UploadId, fileName = Path.GetFileName(referencePath), path = referencePath,
                sha256 = referenceSha256, bytes = new FileInfo(referencePath).Length, mediaType = "image/png",
            });
            var firstPrompt = "Baue mir eine detailreiche kleine Mars-Forschungsstation in Blender nach dem angehängten Stationsbriefing "
                + "und dem Bild. Triff die gestalterischen Entscheidungen selbst. Lies das Dokument und analysiere das Bild zuerst. "
                + "Arbeite schrittweise mit kleinen Scripts, zeige und prüfe jeden Zwischenstand in Blender. "
                + "Prüfe den Entwurf aus mehreren Ansichten und behebe sichtbare Probleme. Liefere die fertige station-v1.blend im Workspace-Hauptordner; "
                + "verwende für Zwischenstände andere Namen. Dokumentiere deine Annahmen und Entwurfsentscheidungen. Keine Websuche oder Paketinstallation.";
            var history = new List<RunMessage>();
            var first = await RunTurnAsync(1, firstPrompt,
                [new("upload", UploadId: upload.UploadId, MediaType: "image/png", FileName: "Grundriss.png")], [upload.UploadId]);
            Assert.Contains(first.Tools, t => (t.Proposal.Name is ClientToolNames.DocumentRead or ClientToolNames.DocumentsReadPages
                or ClientToolNames.CodingSearchKnowledge) && t.Result.Status == "completed"
                && t.Result.Result.GetRawText().Contains("MARSSTATION", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(first.Tools, t => IsBlenderOperation(t, "scaffold"));
            Assert.True(SuccessfulVision(first).Length >= 3, "Reference plus at least two real render views must be analyzed.");
            var firstStageIndex = first.Events.FindIndex(e => IsBlenderProposal(e, "stage"));
            Assert.True(firstStageIndex > 0, "The design must be built in small managed stages.");
            Assert.Contains(first.Events.Take(firstStageIndex), IsSuccessfulVision);
            Assert.Contains(first.Tools, t => t.Proposal.Name == ClientToolNames.CodingRead
                && t.Result.Status == "completed" && ProposalIndex(first, t.Proposal) < firstStageIndex
                && t.Proposal.Arguments.GetProperty("path").GetString()!.EndsWith("MODELING_GUIDE.md", StringComparison.OrdinalIgnoreCase));
            // A model may legitimately upload the same reference again with image.input.
            // Accept content identity captured at that successful upload, not only the
            // independently generated ID of the original client attachment.
            var referenceUploads = first.Tools.Where(t => t.Proposal.Name == WorkspaceTools.ImageInput
                    && t.Result.Status == "completed" && Operation(t.Proposal) == "file"
                    && SameWorkspacePath(workspace, t.Proposal.Arguments.GetProperty("path").GetString()!, referencePath)
                    && t.SourceSha256 == referenceSha256)
                .Select(t => t.Result.Result.GetProperty("uploadId").GetString()).Append(upload.UploadId).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(referenceSha256, await HashFileContentAsync(referencePath, token));
            Assert.Contains(SuccessfulVision(first), e => referenceUploads.Contains(AnalyzedUpload(first, e)));
            await AssertStageChainAsync(first, workspace, minimumStages: 3, initialScene: null, initialSha256: null, token);
            await AssertSteeringAsync(first, workspace, token);
            await AssertModelReviewAsync(first, workspace, token);
            var version1 = Path.Combine(workspace, "station-v1.blend");
            var version1Hash = await HashAsync(version1, token);
            var original = await AuditAsync(blender!, version1, Path.Combine(evidence, "audit-v1"), null, token);
            audits.Add(original);
            AssertStationGeometry(original);
            AssertSteeredGeometry(original);

            // Deliberate test fixture defect, never attributed to the model: preserve v1,
            // create a separate working copy and hide its existing rover from renders.
            var brokenPath = Path.Combine(workspace, "repair-input.blend");
            var broken = await AuditAsync(blender!, version1, Path.Combine(evidence, "fault-injection"), brokenPath, token);
            audits.Add(broken);
            var brokenHash = await HashAsync(brokenPath, token);
            Assert.Equal(version1Hash, await HashAsync(version1, token));
            Assert.All(Meshes(broken).Where(o => Name(o).StartsWith("Rover", StringComparison.OrdinalIgnoreCase)),
                o => Assert.True(o.GetProperty("hiddenRender").GetBoolean()));
            var secondPrompt = "Arbeite an repair-input.blend weiter. Das ist eine separate Arbeitskopie der Station mit einem Sichtbarkeitsfehler: "
                + "Der Rover ist im Renderbild verschwunden. Rendere und analysiere die Arbeitskopie zuerst, finde und behebe die Ursache in der Szene. "
                + "Ergänze außerdem einen Landepad, einen Kommunikationsmast und eine zweite Solargruppe. Wähle passende Details selbst, "
                + "erhalte die vorhandenen Bauteile und ihre Positionen. Benenne neue Meshes mit LandingPad, Comms beziehungsweise SolarBeta als Präfix. "
                + "Speichere station-v2.blend im Workspace-Hauptordner; erhalte station-v1.blend und repair-input.blend unverändert. "
                + "Arbeite auch hier schrittweise in mindestens zwei kleinen Etappen, zeige und prüfe jeden Zwischenstand in Blender. "
                + "Prüfe die neue Version wieder aus mehreren Ansichten. "
                + "Zeige deine konkreten Befunde und Korrekturen. Keine Websuche oder Paketinstallation.";
            // Keep the same attachment scope so persisted tool/reasoning context can continue.
            var second = await RunTurnAsync(2, secondPrompt, [], [upload.UploadId]);
            await AssertStageChainAsync(second, workspace, minimumStages: 2, initialScene: brokenPath, initialSha256: brokenHash, token);
            await AssertModelReviewAsync(second, workspace, token);
            Assert.True(SuccessfulVision(second).Length >= 3, "The defective view and at least two corrected views must be analyzed.");
            var repairIndex = second.Events.FindIndex(e => IsBlenderProposal(e, "stage"));
            Assert.True(repairIndex > 0, "Repair requires a managed Blender stage.");
            Assert.Contains(second.Events.Take(repairIndex), e => IsBlenderProposal(e, "render"));
            Assert.Contains(second.Events.Take(repairIndex), IsSuccessfulVision);
            Assert.Contains(second.Events.Skip(repairIndex + 1), IsSuccessfulVision);
            Assert.Equal(version1Hash, await HashAsync(version1, token));
            Assert.Equal(brokenHash, await HashAsync(brokenPath, token));
            var version2 = Path.Combine(workspace, "station-v2.blend");
            Assert.NotEqual(version1Hash, await HashAsync(version2, token));
            var revised = await AuditAsync(blender!, version2, Path.Combine(evidence, "audit-v2"), null, token);
            audits.Add(revised);
            AssertStationGeometry(revised);
            AssertSteeredGeometry(revised);
            var revisedMeshes = Meshes(revised).ToDictionary(Name, StringComparer.Ordinal);
            foreach (var item in Meshes(original).Where(o => OriginalComponents.Any(prefix => Name(o).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                || Name(o).StartsWith(SteeringComponentPrefix, StringComparison.OrdinalIgnoreCase)))
            {
                Assert.True(revisedMeshes.TryGetValue(Name(item), out var kept), "Existing component was lost: " + Name(item));
                Assert.Equal(item.GetProperty("geometrySha256").GetString(), kept.GetProperty("geometrySha256").GetString());
            }
            foreach (var prefix in new[] { "LandingPad", "Comms", "SolarBeta" })
                Assert.Contains(Meshes(revised), o => Name(o).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && o.GetProperty("vertices").GetInt32() >= 8 && !o.GetProperty("hiddenRender").GetBoolean());
            Assert.All(Meshes(revised).Where(o => Name(o).StartsWith("Rover", StringComparison.OrdinalIgnoreCase)),
                o => Assert.False(o.GetProperty("hiddenRender").GetBoolean()));
            passed = true;

            async Task<TurnEvidence> RunTurnAsync(int number, string prompt, IReadOnlyList<ContentPart> attachments, IReadOnlyList<string> uploads)
            {
                var turn = await chats.AddTurnAsync(session.Id, prompt, cancellationToken: token);
                var evidenceTurn = new TurnEvidence(number, prompt) { UploadIds = uploads.ToArray() };
                turns.Add(evidenceTurn);
                history.Add(new("user", [new("text", prompt), .. attachments]));
                var request = new RunRequest(GoAiProtocol.Version, RunMode.General, history.ToArray(),
                    UploadIds: uploads, ClientCapabilities: ["coding", "coding.evidence", "workspace", "documentIo", "documents", "visual-tools", "blender"],
                    SessionId: session.Id.ToString("D"), PreferredGeneralModelId: model,
                    ReasoningEffort: Environment.GetEnvironmentVariable("GO_AI_BLENDER_REASONING") ?? "none",
                    Limits: new RunLimits(TimeoutSeconds: 0), AllowedServerTools: ["media.analyze", "media.inspect", "math.evaluate"]);
                await SaveJsonAsync(Path.Combine(evidence, "request-" + number + ".json"), request);
                var accepted = await client.CreateRunAsync(request,
                    "blender-complex-" + session.Id.ToString("N") + "-" + number, token);
                evidenceTurn.RunId = accepted.RunId;
                var runEvidence = new CodingRunEvidenceStore(evidence, session.Id, accepted.RunId);
                await SaveJsonAsync(Path.Combine(evidence, "turn-" + number + ".json"), evidenceTurn);
                output.WriteLine($"Turn {number}: {accepted.RunId}; model={model}; workspace={workspace}");
                var completed = new Dictionary<string, ToolEvidence>(StringComparer.Ordinal);
                var answer = new StringBuilder();
                var terminal = false;
                long cursor = 0;
                try
                {
                    while (!terminal)
                    {
                        await foreach (var item in client.StreamRunEventsAsync(accepted.RunId, cursor, token))
                        {
                            cursor = item.Id;
                            evidenceTurn.Events.Add(item);
                            await File.AppendAllTextAsync(Path.Combine(evidence, "events-" + number + ".jsonl"),
                                JsonSerializer.Serialize(item, GoAiProtocol.CreateJsonOptions()) + Environment.NewLine, token);
                            if (evidenceTurn.Steering is { } steeringUpdate
                                && (item.Type is RunSteeringEventTypes.Accepted or RunSteeringEventTypes.Applied)
                                && item.Data.GetProperty("inputId").GetString() == steeringUpdate.Request.InputId)
                            {
                                if (item.Type == RunSteeringEventTypes.Accepted) steeringUpdate.AcceptedEventId = item.Id;
                                else
                                {
                                    steeringUpdate.Applied = item.Data.Deserialize<RunSteeringEvent>(GoAiProtocol.CreateJsonOptions());
                                    steeringUpdate.AppliedEventId = item.Id;
                                }
                                await SaveJsonAsync(Path.Combine(evidence, "steering-" + number + ".json"), steeringUpdate);
                            }
                            if (item.Type == RunEventTypes.TextDelta)
                            {
                                var delta = item.Data.Deserialize<TextDeltaEvent>(GoAiProtocol.CreateJsonOptions())!;
                                if (string.IsNullOrEmpty(delta.AgentId))
                                {
                                    if (delta.ReplaceFrom is { } replaceFrom)
                                    {
                                        Assert.InRange(replaceFrom, 0, answer.Length);
                                        answer.Length = replaceFrom;
                                    }
                                    answer.Append(delta.Delta);
                                }
                            }
                            if (item.Type == RunEventTypes.ClientToolProposed)
                            {
                                var proposal = item.Data.Deserialize<ToolProposal>(GoAiProtocol.CreateJsonOptions())!;
                                if (!completed.TryGetValue(proposal.ProposalId, out var done))
                                {
                                    var result = await broker.ExecuteAsync(proposal, session.Id, turn.AssistantMessage.Id, workspace,
                                        evidenceStore: runEvidence, cancellationToken: token);
                                    var sourceSha256 = result.Status == "completed" && proposal.Name == WorkspaceTools.ImageInput
                                        && Operation(proposal) == "file"
                                        ? await HashFileContentAsync(WorkspaceFilePath.Resolve(workspace, proposal.Arguments.GetProperty("path").GetString()!), token)
                                        : null;
                                    done = new(proposal, result, sourceSha256);
                                    completed.Add(proposal.ProposalId, done);
                                    evidenceTurn.Tools.Add(done);
                                    await SaveJsonAsync(Path.Combine(evidence, "tools-" + number + ".json"), evidenceTurn.Tools);
                                    output.WriteLine($"{number}: {proposal.Name} {Operation(proposal)} => {result.Status} {result.Message}");
                                }
                                if (number == 1 && evidenceTurn.Steering is null && IsBlenderOperation(done, "stage")
                                    && done.Result.Result.GetProperty("success").GetBoolean())
                                {
                                    var scenePath = done.Result.Result.GetProperty("outputPath").GetString()!;
                                    var sceneHash = await HashAsync(WorkspaceFilePath.Resolve(workspace, scenePath), token);
                                    Assert.Equal(done.Result.Result.GetProperty("sceneSha256").GetString(), sceneHash);
                                    var steering = new SteeringEvidence(new RunSteeringRequest(session.Id.ToString("D"),
                                        "weather-" + session.Id.ToString("N"), SteeringPrompt), proposal.ProposalId, item.Id,
                                        scenePath, sceneHash, done.Result.Result.GetProperty("reportPath").GetString()!);
                                    evidenceTurn.Steering = steering;
                                    var steeringEvidencePath = Path.Combine(evidence, "steering-" + number + ".json");
                                    await SaveJsonAsync(steeringEvidencePath, steering);
                                    // The native stage has completed. Queue the new instruction while
                                    // the gateway still awaits its receipt, then publish that receipt.
                                    // This exercises a real in-flight steering boundary without racing
                                    // finalization or interrupting a Blender child halfway through saving.
                                    steering.Accepted = await client.SteerRunAsync(accepted.RunId, steering.Request, token);
                                    await SaveJsonAsync(steeringEvidencePath, steering);
                                    Assert.Equal(accepted.RunId, steering.Accepted.RunId);
                                    Assert.Equal(steering.Request.InputId, steering.Accepted.InputId);
                                    Assert.Equal("accepted", steering.Accepted.State);
                                    Assert.False(steering.Accepted.Duplicate);
                                    output.WriteLine($"{number}: steering accepted after saved stage: {steering.Accepted.InputId}, sequence={steering.Accepted.Sequence}");
                                }
                                await client.SubmitClientToolResultAsync(accepted.RunId, done.Result, token);
                            }
                            if (item.Type is RunEventTypes.RunCompleted or RunEventTypes.RunFailed or RunEventTypes.RunCancelled)
                            {
                                terminal = true;
                                Assert.True(item.Type == RunEventTypes.RunCompleted, item.Data.GetRawText());
                            }
                        }
                    }
                    evidenceTurn.Answer = RunVisibleText.Canonicalize(answer.ToString());
                    await chats.UpdateMessageAsync(turn.AssistantMessage.Id, evidenceTurn.Answer, MessageStatus.Completed, cancellationToken: token);
                    AppendSteeredHistory(history, evidenceTurn);
                    return evidenceTurn;
                }
                finally
                {
                    if (!terminal)
                    {
                        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                        try { await client.CancelRunAsync(accepted.RunId, cancellation.Token); }
                        catch (Exception exception) { evidenceTurn.CancellationError = exception.Message; }
                    }
                    evidenceTurn.Answer = RunVisibleText.Canonicalize(answer.ToString());
                    await SaveJsonAsync(Path.Combine(evidence, "turn-" + number + ".json"), evidenceTurn);
                }
            }
        }
        catch (Exception exception)
        {
            failure = exception.ToString();
            throw;
        }
        finally
        {
            await SaveJsonAsync(Path.Combine(evidence, "acceptance.json"), new
            {
                passed, failure, model, server, blender, workspace, sessionId = session.Id,
                turns, audits, note = "Geometry and persistence are mechanically checked. Vision opinions are saved verbatim; artistic quality still requires human review.",
            });
        }
    }

    private static async Task AssertSteeringAsync(TurnEvidence turn, string workspace, CancellationToken token)
    {
        var steering = Assert.IsType<SteeringEvidence>(turn.Steering);
        var receipt = Assert.IsType<RunSteeringAccepted>(steering.Accepted);
        Assert.True(receipt.Sequence > 0);
        var accepted = Assert.Single(turn.Events, e => IsSteeringEvent(e, RunSteeringEventTypes.Accepted, steering.Request.InputId));
        var applied = Assert.Single(turn.Events, e => IsSteeringEvent(e, RunSteeringEventTypes.Applied, steering.Request.InputId));
        Assert.True(accepted.Id > steering.AfterStageEventId);
        Assert.True(applied.Id > accepted.Id);
        Assert.Equal(steering.AcceptedEventId, accepted.Id);
        Assert.Equal(steering.AppliedEventId, applied.Id);
        Assert.All(new[] { accepted, applied }, item =>
        {
            Assert.Equal(turn.RunId, item.RunId);
            var details = item.Data.Deserialize<RunSteeringEvent>(GoAiProtocol.CreateJsonOptions())!;
            Assert.Equal(receipt.Sequence, details.Sequence);
            Assert.Equal(steering.Request.SessionId, details.SessionId);
            Assert.Equal(steering.Request.Text, details.Text);
        });
        Assert.Contains(turn.Tools, t => IsBlenderOperation(t, "stage")
            && turn.Events[ProposalIndex(turn, t.Proposal)].Id > applied.Id);
        Assert.Equal(steering.AfterSceneSha256, await HashAsync(WorkspaceFilePath.Resolve(workspace, steering.AfterScenePath), token));
        using var before = JsonDocument.Parse(await File.ReadAllTextAsync(WorkspaceFilePath.Resolve(workspace, steering.AfterReportPath), token));
        Assert.DoesNotContain(before.RootElement.GetProperty("objects").EnumerateArray(),
            item => Name(item).StartsWith(SteeringComponentPrefix, StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertSteeredGeometry(JsonElement audit) => Assert.Contains(Meshes(audit), item =>
        Name(item).StartsWith(SteeringComponentPrefix, StringComparison.OrdinalIgnoreCase)
        && item.GetProperty("vertices").GetInt32() >= 8 && item.GetProperty("faces").GetInt32() > 0
        && !item.GetProperty("hiddenRender").GetBoolean()
        && item.GetProperty("dimensions").EnumerateArray().All(value => value.GetDouble() > 0));

    private static bool IsSteeringEvent(RunEvent item, string type, string inputId) => item.Type == type
        && item.Data.TryGetProperty("inputId", out var actual) && actual.GetString() == inputId;

    private static void AppendSteeredHistory(List<RunMessage> history, TurnEvidence turn)
    {
        var visible = turn.Answer!;
        var segmentStart = 0;
        foreach (var item in turn.Events.Where(e => e.Type == RunSteeringEventTypes.Applied))
        {
            var steering = item.Data.Deserialize<RunSteeringEvent>(GoAiProtocol.CreateJsonOptions())!;
            Assert.NotNull(steering.VisibleTextOffset);
            var offset = steering.VisibleTextOffset.Value;
            Assert.InRange(offset, segmentStart, visible.Length);
            var segment = visible[segmentStart..offset];
            if (!string.IsNullOrWhiteSpace(segment)) history.Add(new("assistant", [new("text", segment)]));
            history.Add(new("user", [new("text", steering.Text)]));
            segmentStart = offset;
        }
        var finalSegment = visible[segmentStart..];
        if (!string.IsNullOrWhiteSpace(finalSegment)) history.Add(new("assistant", [new("text", finalSegment)]));
    }

    private static async Task AssertStageChainAsync(TurnEvidence turn, string workspace, int minimumStages,
        string? initialScene, string? initialSha256, CancellationToken token)
    {
        var stages = turn.Tools.Where(t => IsBlenderOperation(t, "stage")).ToArray();
        Assert.True(stages.Length >= minimumStages, $"Turn {turn.Number} needs at least {minimumStages} successful visible stages.");
        var scriptPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scenePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var previousScene = initialScene;
        var previousHash = initialSha256;
        var previousPreviewRevision = 0;
        var reviewedStages = 0;
        int? previewProcessId = null;
        foreach (var stage in stages)
        {
            var args = stage.Proposal.Arguments;
            var result = stage.Result.Result;
            Assert.True(result.GetProperty("success").GetBoolean());
            var valid = result.GetProperty("valid").GetBoolean();
            Assert.Equal("stage", result.GetProperty("operation").GetString());
            Assert.Equal(args.GetProperty("label").GetString(), result.GetProperty("label").GetString());
            var script = WorkspaceFilePath.Resolve(workspace, args.GetProperty("path").GetString()!);
            Assert.True(scriptPaths.Add(script), "Each stage needs its own small script: " + script);
            Assert.True(SameWorkspacePath(workspace, script, result.GetProperty("scriptPath").GetString()!));
            Assert.InRange((await File.ReadAllTextAsync(script, token)).Length, 1, WorkspaceTools.MaximumBlenderStageScriptCharacters);
            Assert.Equal(args.GetProperty("expectedSha256").GetString()!.ToLowerInvariant(), await HashFileContentAsync(script, token));
            if (previousScene is not null)
            {
                Assert.True(args.TryGetProperty("baseScene", out var baseScene), "Every continuation stage must load the previously reviewed revision.");
                Assert.True(SameWorkspacePath(workspace, previousScene, baseScene.GetString()!));
                Assert.True(SameWorkspacePath(workspace, previousScene, result.GetProperty("baseScene").GetString()!));
                Assert.Equal(previousHash, args.GetProperty("baseSceneSha256").GetString()!.ToLowerInvariant());
                Assert.Equal(previousHash, await HashAsync(previousScene, token));
            }
            else
                Assert.False(args.TryGetProperty("baseScene", out _), "The first construction stage must create its own design.");

            var scene = WorkspaceFilePath.Resolve(workspace, args.GetProperty("outputPath").GetString()!);
            Assert.True(scenePaths.Add(scene), "Stages must preserve earlier saved revisions: " + scene);
            Assert.True(SameWorkspacePath(workspace, scene, result.GetProperty("outputPath").GetString()!));
            var sceneHash = await HashAsync(scene, token);
            Assert.Equal(sceneHash, result.GetProperty("sceneSha256").GetString());
            var reportPath = WorkspaceFilePath.Resolve(workspace, result.GetProperty("reportPath").GetString()!);
            using (var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath, token)))
            {
                Assert.Equal(valid, report.RootElement.GetProperty("valid").GetBoolean());
                if (valid)
                {
                    Assert.True(result.GetProperty("inspectionCompleted").GetBoolean());
                    Assert.True(report.RootElement.GetProperty("inspectionCompleted").GetBoolean());
                    Assert.True(report.RootElement.GetProperty("counts").GetProperty("meshObjects").GetInt32() > 0);
                }
                else Assert.NotEmpty(report.RootElement.GetProperty("issues").EnumerateArray());
            }

            // The service opens Blender automatically. Inspect its acknowledged GUI state,
            // not an optional explicit preview tool call or an assistant's claim of visibility.
            var preview = result.GetProperty("preview");
            Assert.True(preview.GetProperty("success").GetBoolean());
            Assert.Equal("ready", preview.GetProperty("state").GetString());
            Assert.True(SameWorkspacePath(workspace, scene, preview.GetProperty("path").GetString()!));
            var revision = preview.GetProperty("revision").GetInt32();
            Assert.True(revision > previousPreviewRevision, "Blender must acknowledge each new visible stage.");
            previousPreviewRevision = revision;
            var processId = preview.GetProperty("processId").GetInt32();
            Assert.True(processId > 0);
            if (previewProcessId is not null) Assert.Equal(previewProcessId.Value, processId);
            previewProcessId = processId;
            previousScene = scene;
            previousHash = sceneHash;

            // A saved draft may reveal a structural defect which prevents rendering.
            // Keep that evidence and allow its repair, but do not count it as an
            // accepted stage or demand a vision result for an image that cannot exist.
            if (!valid) continue;

            var stageIndex = ProposalIndex(turn, stage.Proposal);
            var nextStageIndex = turn.Events.FindIndex(stageIndex + 1, e => IsBlenderProposal(e, "stage"));
            if (nextStageIndex < 0) nextStageIndex = turn.Events.Count;
            var renders = turn.Tools.Where(t => IsBlenderOperation(t, "render")
                && ProposalIndex(turn, t.Proposal) > stageIndex && ProposalIndex(turn, t.Proposal) < nextStageIndex
                && SameWorkspacePath(workspace, t.Proposal.Arguments.GetProperty("path").GetString()!, scene)).ToArray();
            Assert.NotEmpty(renders);
            Assert.All(renders, render =>
            {
                Assert.Equal(sceneHash, render.Proposal.Arguments.GetProperty("expectedSha256").GetString()!.ToLowerInvariant());
                Assert.Equal(sceneHash, render.Result.Result.GetProperty("sourceSha256").GetString());
            });
            var images = await VerifyRenderFilesAsync(renders, workspace, token);
            var uploads = turn.Tools.Where(t => t.Proposal.Name == WorkspaceTools.ImageInput && t.Result.Status == "completed"
                    && Operation(t.Proposal) == "file" && ProposalIndex(turn, t.Proposal) > stageIndex
                    && ProposalIndex(turn, t.Proposal) < nextStageIndex
                    && images.TryGetValue(WorkspaceFilePath.Resolve(workspace, t.Proposal.Arguments.GetProperty("path").GetString()!), out var hash)
                    && hash == t.SourceSha256)
                .Select(t => t.Result.Result.GetProperty("uploadId").GetString()).ToHashSet(StringComparer.Ordinal);
            Assert.Contains(turn.Events.Skip(stageIndex + 1).Take(nextStageIndex - stageIndex - 1),
                e => IsSuccessfulVision(e) && uploads.Contains(AnalyzedUpload(turn, e)));
            reviewedStages++;
        }
        Assert.True(reviewedStages >= minimumStages, $"Turn {turn.Number} needs at least {minimumStages} valid stages with real visual review.");
        Assert.True(stages[^1].Result.Result.GetProperty("valid").GetBoolean(), "The final deliverable must pass its structural audit.");
        Assert.True(SameWorkspacePath(workspace, previousScene!, "station-v" + turn.Number + ".blend"),
            "The final deliverable must be the last saved and reviewed stage.");
    }

    private static async Task AssertModelReviewAsync(TurnEvidence turn, string workspace, CancellationToken token)
    {
        var expectedRevision = "station-v" + turn.Number + ".blend";
        Assert.Contains(turn.Tools, t => IsBlenderOperation(t, "stage")
            && SameWorkspacePath(workspace, t.Proposal.Arguments.GetProperty("outputPath").GetString()!, expectedRevision)
            && t.Result.Result.GetProperty("valid").GetBoolean());
        var renders = turn.Tools.Where(t => IsBlenderOperation(t, "render")
            && SameWorkspacePath(workspace, t.Proposal.Arguments.GetProperty("path").GetString()!, expectedRevision)).ToArray();
        Assert.NotEmpty(renders);
        var images = renders.SelectMany(t => t.Result.Result.GetProperty("images").EnumerateArray()).ToArray();
        Assert.True(images.Length >= 3, "The model must produce multiple actual views with the managed Blender renderer.");
        var renderedViews = images.Select(image => image.GetProperty("view").GetString()).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("perspective", renderedViews);
        Assert.True(OrthogonalViews.Count(renderedViews.Contains) >= 2, "Review needs two suitable orthogonal views in addition to the overview.");
        var renderFiles = await VerifyRenderFilesAsync(renders, workspace, token);
        Assert.True(renderFiles.Values.Distinct(StringComparer.Ordinal).Count() >= 3, "Three filenames containing the same pixels are not three views.");
        Assert.All(SuccessfulVision(turn), e =>
        {
            var result = e.Data.GetProperty("result");
            Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("analysis").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("visionModelId").GetString()));
        });
        var analyzedUploads = SuccessfulVision(turn).Select(e => AnalyzedUpload(turn, e)).ToHashSet(StringComparer.Ordinal);
        Assert.True(turn.Tools.Where(t => t.Proposal.Name == WorkspaceTools.ImageInput && t.Result.Status == "completed"
            && Operation(t.Proposal) == "file" && renderFiles.TryGetValue(WorkspaceFilePath.Resolve(workspace, t.Proposal.Arguments.GetProperty("path").GetString()!), out var hash)
            && t.SourceSha256 == hash
            && analyzedUploads.Contains(t.Result.Result.GetProperty("uploadId").GetString()))
            .Select(t => Path.GetFullPath(Path.Combine(workspace, t.Proposal.Arguments.GetProperty("path").GetString()!))).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2,
            "At least two distinct render images of the final saved revision must actually be analyzed.");
    }

    private static async Task<Dictionary<string, string>> VerifyRenderFilesAsync(IEnumerable<ToolEvidence> renders, string workspace, CancellationToken token)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in renders.SelectMany(t => t.Result.Result.GetProperty("images").EnumerateArray()))
        {
            var path = WorkspaceFilePath.Resolve(workspace, image.GetProperty("path").GetString()!);
            Assert.True(new FileInfo(path).Length > 1_024, "Missing or implausibly small render: " + path);
            var bytes = await File.ReadAllBytesAsync(path, token);
            Assert.True(bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "Invalid PNG: " + path);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            Assert.Equal(image.GetProperty("sha256").GetString(), hash);
            files[path] = hash;
        }
        Assert.NotEmpty(files);
        return files;
    }

    private static int ProposalIndex(TurnEvidence turn, ToolProposal proposal) => turn.Events.FindIndex(e =>
        e.Type == RunEventTypes.ClientToolProposed && e.Data.GetProperty("proposalId").GetString() == proposal.ProposalId);

    private static void AssertStationGeometry(JsonElement audit)
    {
        var meshes = Meshes(audit);
        Assert.True(meshes.Length >= 30, "A complex station requires component geometry, not a small group of placeholder primitives.");
        Assert.True(meshes.Sum(o => o.GetProperty("vertices").GetInt32()) >= 1_000);
        Assert.True(audit.GetProperty("materialCount").GetInt32() >= 6);
        Assert.All(meshes, o => Assert.Equal(0, o.GetProperty("nonFiniteVertices").GetInt32()));
        foreach (var prefix in OriginalComponents)
            Assert.Contains(meshes, o => Name(o).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && o.GetProperty("vertices").GetInt32() >= 8 && !o.GetProperty("hiddenRender").GetBoolean());
        // The briefing supplies the color legend, while only the uploaded image supplies this arrangement.
        static double MeanX(IEnumerable<JsonElement> objects) => objects.Average(o => o.GetProperty("center")[0].GetDouble());
        var habitatX = MeanX(meshes.Where(o => Name(o).StartsWith("Habitat", StringComparison.OrdinalIgnoreCase)));
        Assert.True(MeanX(meshes.Where(o => Name(o).StartsWith("Greenhouse", StringComparison.OrdinalIgnoreCase))) > habitatX + 2,
            "The greenhouse must follow the image reference to the habitat's right (+X).");
        Assert.True(MeanX(meshes.Where(o => Name(o).StartsWith("Solar", StringComparison.OrdinalIgnoreCase)
            && !Name(o).StartsWith("SolarBeta", StringComparison.OrdinalIgnoreCase))) < habitatX - 2,
            "The original solar group must follow the image reference to the left (-X).");
    }

    private static async Task<JsonElement> AuditAsync(string blender, string scene, string directory, string? brokenCopy, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        var reportPath = Path.Combine(directory, "geometry.json");
        var script = "import bpy, hashlib, json, math\nfrom mathutils import Vector\n"
            + "bpy.ops.wm.open_mainfile(filepath=" + JsonSerializer.Serialize(scene) + ")\n"
            + (brokenCopy is null ? "" : "for obj in bpy.data.objects:\n    if obj.type == 'MESH' and obj.name.lower().startswith('rover'):\n        obj.hide_render = True\n"
                + "bpy.ops.wm.save_as_mainfile(filepath=" + JsonSerializer.Serialize(brokenCopy) + ")\n")
            + """
            objects = []
            for obj in bpy.context.scene.objects:
                if obj.type != 'MESH':
                    continue
                vertices = [list(obj.matrix_world @ vertex.co) for vertex in obj.data.vertices]
                finite = [p for p in vertices if all(math.isfinite(v) for v in p)]
                bounds = [list(obj.matrix_world @ Vector(corner)) for corner in obj.bound_box]
                center = [(min(p[i] for p in bounds) + max(p[i] for p in bounds)) / 2 for i in range(3)]
                geometry = {'vertices': vertices, 'faces': [list(face.vertices) for face in obj.data.polygons]}
                objects.append({'name': obj.name, 'vertices': len(vertices), 'faces': len(obj.data.polygons),
                    'center': center, 'dimensions': list(obj.dimensions), 'hiddenRender': obj.hide_render,
                    'nonFiniteVertices': len(vertices) - len(finite),
                    'geometrySha256': hashlib.sha256(json.dumps(geometry, sort_keys=True).encode()).hexdigest()})
            result = {'objects': objects, 'materialCount': len(bpy.data.materials), 'source': bpy.data.filepath}
            """
            + "\nwith open(" + JsonSerializer.Serialize(reportPath) + ", 'w', encoding='utf-8') as stream:\n    json.dump(result, stream, indent=2)\n";
        var scriptPath = Path.Combine(directory, "audit.py");
        await File.WriteAllTextAsync(scriptPath, script, token);
        var start = new ProcessStartInfo(blender) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory };
        foreach (var argument in new[] { "--background", "--factory-startup", "--disable-autoexec", "--python-exit-code", "1", "--python", scriptPath })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        await File.WriteAllTextAsync(Path.Combine(directory, "stdout.log"), await stdout, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(directory, "stderr.log"), await stderr, CancellationToken.None);
        Assert.Equal(0, process.ExitCode);
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath, token));
        return report.RootElement.Clone();
    }

    private static void CreateBriefing(string path)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        main.Document = new Document(new Body(BriefingParagraphs.Select(text => new Paragraph(new Run(new Text(text))))));
    }

    private static async Task CreateReferenceImageAsync(string path, CancellationToken token)
    {
        const int width = 800, height = 600;
        var pixels = new byte[width * height * 4];
        void Box(int x, int y, int w, int h, byte red, byte green, byte blue)
        {
            for (var yy = Math.Max(0, y); yy < Math.Min(height, y + h); yy++)
                for (var xx = Math.Max(0, x); xx < Math.Min(width, x + w); xx++)
                { var p = (yy * width + xx) * 4; pixels[p] = blue; pixels[p + 1] = green; pixels[p + 2] = red; pixels[p + 3] = 255; }
        }
        void Circle(int x, int y, int radius, byte red, byte green, byte blue)
        {
            for (var dy = -radius; dy <= radius; dy++)
            { var half = (int)Math.Sqrt(radius * radius - dy * dy); Box(x - half, y + dy, half * 2 + 1, 1, red, green, blue); }
        }
        Box(0, 0, width, height, 191, 117, 87);
        for (var x = 0; x < width; x += 40) Box(x, 0, 1, height, 176, 105, 77);
        for (var y = 0; y < height; y += 40) Box(0, y, width, 1, 176, 105, 77);
        Box(370, 265, 260, 35, 220, 219, 207);
        Box(365, 285, 35, 155, 220, 219, 207);
        Circle(385, 270, 99, 69, 80, 92);
        Circle(385, 270, 89, 229, 232, 226);
        Circle(385, 270, 66, 173, 191, 197);
        Circle(385, 270, 48, 229, 232, 226);
        Box(553, 200, 178, 155, 34, 69, 55);
        Box(562, 209, 160, 137, 93, 167, 117);
        for (var x = 586; x < 720; x += 29) Box(x, 209, 4, 137, 192, 215, 207);
        Box(562, 276, 160, 5, 192, 215, 207);
        Box(333, 382, 103, 91, 70, 66, 61);
        Box(342, 391, 85, 73, 238, 149, 45);
        Box(354, 458, 61, 17, 231, 230, 218);
        for (var y = 159; y <= 310; y += 95)
        {
            Box(63, y, 180, 72, 25, 42, 63);
            for (var x = 75; x < 236; x += 27) Box(x, y + 8, 20, 57, 48, 88, 144);
            Box(65, y + 34, 176, 3, 122, 148, 176);
        }
        Box(568, 433, 93, 50, 222, 223, 215);
        Box(625, 440, 24, 35, 76, 143, 170);
        for (var x = 576; x < 661; x += 65)
        { Circle(x, 433, 12, 31, 36, 43); Circle(x, 483, 12, 31, 36, 43); }
        Box(610, 410, 3, 26, 47, 59, 67);
        using var encoded = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, encoded);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, width, height, 96, 96, pixels);
        await encoder.FlushAsync().AsTask(token);
        encoded.Seek(0);
        using var input = encoded.AsStreamForRead();
        await using var target = File.Create(path);
        await input.CopyToAsync(target, token);
    }

    private static string Name(JsonElement item) => item.GetProperty("name").GetString()!;
    private static bool SameWorkspacePath(string workspace, string first, string second) =>
        string.Equals(Path.GetFullPath(Path.Combine(workspace, first)), Path.GetFullPath(Path.Combine(workspace, second)), StringComparison.OrdinalIgnoreCase);
    private static JsonElement[] Meshes(JsonElement audit) => audit.GetProperty("objects").EnumerateArray().ToArray();
    private static string? Operation(ToolProposal proposal) => proposal.Arguments.TryGetProperty("operation", out var operation) ? operation.GetString() : null;
    private static bool IsBlenderOperation(ToolEvidence tool, string operation) => tool.Proposal.Name == WorkspaceTools.Blender
        && Operation(tool.Proposal) == operation && tool.Result.Status == "completed";
    private static bool IsBlenderProposal(RunEvent item, string operation) => item.Type == RunEventTypes.ClientToolProposed
        && item.Data.TryGetProperty("name", out var name) && name.GetString() == WorkspaceTools.Blender
        && item.Data.GetProperty("arguments").GetProperty("operation").GetString() == operation;
    private static bool IsSuccessfulVision(RunEvent item) => item.Type == RunEventTypes.ServerToolCompleted
        && item.Data.TryGetProperty("tool", out var tool) && tool.GetString() == "media.analyze"
        && item.Data.TryGetProperty("success", out var success) && success.GetBoolean();
    private static RunEvent[] SuccessfulVision(TurnEvidence turn) => turn.Events.Where(IsSuccessfulVision).ToArray();
    private static string? AnalyzedUpload(TurnEvidence turn, RunEvent completed)
    {
        var callId = completed.Data.GetProperty("toolCallId").GetString();
        var started = turn.Events.FirstOrDefault(e => e.Type == RunEventTypes.ServerToolStarted
            && e.Data.TryGetProperty("toolCallId", out var id) && id.GetString() == callId);
        return started?.Data.GetProperty("arguments").GetProperty("uploadId").GetString();
    }
    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        Assert.True(File.Exists(path), "Required saved Blender revision is missing: " + path);
        Assert.True(new FileInfo(path).Length > 1_000);
        return await HashFileContentAsync(path, token);
    }
    private static async Task<string> HashFileContentAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
    }
    private static Task SaveJsonAsync(string path, object value) => File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, Json));
    private sealed record ToolEvidence(ToolProposal Proposal, ClientToolResult Result, string? SourceSha256 = null);
    private sealed class SteeringEvidence(RunSteeringRequest request, string afterProposalId, long afterStageEventId,
        string afterScenePath, string afterSceneSha256, string afterReportPath)
    {
        public RunSteeringRequest Request { get; } = request;
        public DateTimeOffset RequestedAtUtc { get; } = DateTimeOffset.UtcNow;
        public string AfterProposalId { get; } = afterProposalId;
        public long AfterStageEventId { get; } = afterStageEventId;
        public string AfterScenePath { get; } = afterScenePath;
        public string AfterSceneSha256 { get; } = afterSceneSha256;
        public string AfterReportPath { get; } = afterReportPath;
        public RunSteeringAccepted? Accepted { get; set; }
        public long? AcceptedEventId { get; set; }
        public RunSteeringEvent? Applied { get; set; }
        public long? AppliedEventId { get; set; }
    }
    private sealed class TurnEvidence(int number, string prompt)
    {
        public int Number { get; } = number;
        public string Prompt { get; } = prompt;
        public string? RunId { get; set; }
        public string? Answer { get; set; }
        public SteeringEvidence? Steering { get; set; }
        public string? CancellationError { get; set; }
        public IReadOnlyList<string> UploadIds { get; init; } = [];
        public List<RunEvent> Events { get; } = [];
        public List<ToolEvidence> Tools { get; } = [];
    }
    private sealed class ConnectionSettings(string server) : ISettingsStore
    {
        public string SettingsPath => "in-memory Blender acceptance";
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppSettings { GoAiServerUrl = server });
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No user-profile writes.");
    }
}
