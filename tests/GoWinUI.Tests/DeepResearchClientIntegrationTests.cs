using GoAi.Contracts;
using GoWinUI.App.Services;
using GoWinUI.App.ViewModels;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using System.Text.Json;

namespace GoWinUI.Tests;

public sealed class DeepResearchClientIntegrationTests
{
    private const string GeneralModel = "fixture/general-model";
    private const string CodingModel = "fixture/coding-model";
    private const string CaptureError = "fixture.request_captured";
    private static readonly bool?[] ResearchSelections = [true, false, null];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComposerDeepResearchSelectionReachesTheGatewayWithoutChangingSessionModeOrWorkspace(bool coding)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var documents = environment.Get<IDocumentIngestor>();
        var session = await chats.CreateSessionAsync(
            "Recherche im bestehenden Projekt",
            coding ? ChatMode.Coding : ChatMode.General);
        var workspace = Path.Combine(environment.Directory, "workspace");
        Directory.CreateDirectory(workspace);
        await chats.SetCodingWorkspacePathAsync(session.Id, workspace);
        await environment.Get<ISettingsStore>().SaveAsync(new AppSettings
        {
            IsAiConnectionEnabled = true,
            GoAiServerUrl = "http://127.0.0.1:65000",
            ActiveSessionId = session.Id,
            SelectedModel = GeneralModel,
            SelectedCodingModel = CodingModel,
        });
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var requests = new List<RunRequest>();
        using var connection = new GoAiConnectionService(settings, NullLogger<GoAiConnectionService>.Instance,
            () => new RequestCaptureHandler(requests, catalogAvailableWhileRuntimeOffline: !coding));
        var broker = new LocalToolBroker(connection, documents, null!, chats);
        using var microphone = new MicrophoneTranscriptionService(connection, settings,
            NullLogger<MicrophoneTranscriptionService>.Instance);
        var recent = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
        using var service = new GoAiAssistantService(connection, chats, environment.Get<IAssistantAttachmentRepository>(),
            environment.Get<IChatArtifactRepository>(), environment.Get<IGoAiRunRepository>(),
            environment.Get<IClientToolExecutionRepository>(), environment.Get<IBinaryObjectStore>(), documents,
            new DocumentContextPreparationService(documents), new SessionContextPreparationService(chats),
            broker, null!, microphone, settings, recent, NullLogger<GoAiAssistantService>.Instance);
        var coordinator = new AssistantCoordinator(chats, environment.Get<IWorkflowRepository>(), documents,
            environment.Get<IContextAssembler>(), environment.Get<IPromptTriggerRepository>(),
            environment.Get<IAssistantAttachmentRepository>(), environment.Get<IChatArtifactRepository>(),
            environment.Get<IConversationSnapshotRepository>(), service, settings, recent, microphone);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Exercise selection, deselection and an ordinary prompt without the field
        // in the same session. The fixture records real serialized requests and
        // ends them before inference; it never reaches a gateway or audio device.
        foreach (var selection in ResearchSelections)
        {
            const string prompt = "Vergleiche zwei Speicherkonzepte und erläutere die Unterschiede.";
            var payload = new Dictionary<string, object?>
            {
                ["sessionId"] = session.Id,
                ["prompt"] = prompt,
            };
            if (selection.HasValue) payload["deepResearch"] = selection.Value;
            if (selection == true) payload["deepResearchProfile"] = "mathematicalInvestigation";
            var before = requests.Count;
            await coordinator.HandleAsync(new WebBridgeEnvelope(AssistantWebBridge.ProtocolVersion,
                    "chat.send", Guid.NewGuid().ToString("D"), JsonSerializer.SerializeToElement(payload)),
                static (_, _, _) => Task.CompletedTask, deadline.Token);

            Assert.Equal(before + 1, requests.Count);
            var sent = requests[^1];
            Assert.Equal(selection == true, sent.DeepResearch);
            if (selection == true)
            {
                Assert.Equal(DeepResearchProfile.MathematicalInvestigation, sent.ResearchOptions?.Profile);
                Assert.Equal($"research-{session.Id:N}", sent.ResearchOptions?.ProjectId);
                Assert.Equal(coding ? ResearchAutonomyLevel.CodingWorkspaceResearch : ResearchAutonomyLevel.ReadOnlyResearch,
                    sent.ResearchOptions?.AutonomyLevel);
                Assert.Equal(ResearchVerificationLevel.MultiPath, sent.ResearchOptions?.VerificationLevel);
            }
            else Assert.Null(sent.ResearchOptions);
            Assert.Equal(session.Id.ToString("D"), sent.SessionId);
            Assert.Contains("web.search", sent.AllowedServerTools!);
            Assert.Contains("web.fetch", sent.AllowedServerTools!);
            Assert.Contains("documentIo", sent.ClientCapabilities!);
            Assert.DoesNotContain("document-agent", sent.ClientCapabilities!);
            if (coding)
            {
                Assert.Equal(RunMode.Coding, sent.Mode);
                Assert.Equal(CodingModel, sent.PreferredCodingModelId);
                Assert.Null(sent.PreferredGeneralModelId);
                Assert.Equal(workspace, sent.CodingOptions?.WorkspacePath);
                Assert.Contains("coding", sent.ClientCapabilities!);
                Assert.Equal(prompt, sent.Messages[^1].Content[0].Text);
            }
            else
            {
                Assert.Equal(selection == true ? RunMode.General : RunMode.Auto, sent.Mode);
                Assert.Equal(GeneralModel, sent.PreferredGeneralModelId);
                Assert.Null(sent.PreferredCodingModelId);
                Assert.Null(sent.CodingOptions);
            }
            var stored = await chats.GetSessionAsync(session.Id, deadline.Token);
            Assert.Equal(workspace, stored?.CodingWorkspacePath);
            Assert.Equal(coding ? ChatMode.Coding : ChatMode.General, stored?.ChatMode);
            Assert.Null(stored?.PersistentExtensionActionId);
            var messages = await chats.ListMessagesAsync(session.Id, deadline.Token);
            Assert.Equal(MessageStatus.Failed, messages[^1].Status);
            Assert.Equal(CaptureError, messages[^1].Error);
        }
    }

    private sealed class RequestCaptureHandler(
        List<RunRequest> requests,
        bool catalogAvailableWhileRuntimeOffline = false) : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Json = GoAiProtocol.CreateJsonOptions();
        private int _modelStatusRequests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            var now = DateTimeOffset.UtcNow;
            if (path == "/v1/models/status")
            {
                // The catalog remains authoritative for request budgeting while the native
                // process is unloaded or restarting. A first prompt must still reach the run
                // endpoint so the gateway can load the selected model on demand.
                var providerReachable = !catalogAvailableWhileRuntimeOffline
                    || Interlocked.Increment(ref _modelStatusRequests) > 1;
                return Response(new ModelStatusSnapshot(providerReachable, "fixture", Models(), now,
                    providerReachable ? null : "modelRuntime.unreachable",
                    providerReachable ? null : "fixture runtime is intentionally offline"));
            }
            if (path == "/v1/models/coding")
                return Response(new CodingModelCatalogResponse(Models().Where(static model => model.Role == "coding").ToArray(),
                    "fixture", true, null, now));
            if (path == "/v1/capabilities")
                return Response(new CapabilitySnapshot(GoAiProtocol.Version, "fixture", [],
                    ["coding.updatePlan", "web.search", "web.fetch"],
                    [ClientToolNames.CodingReadOutput, ClientToolNames.CodingSearchRunEvidence],
                    new Dictionary<string, long>(), [], true, GoAiProtocol.UploadChunkSize,
                    SupportsCodingSessionContext: true));
            if (path == "/v1/runs" && request.Method == HttpMethod.Post)
            {
                var sent = JsonSerializer.Deserialize<RunRequest>(await request.Content!.ReadAsStringAsync(token), Json)!;
                requests.Add(sent);
                var runId = "run-research-fixture-" + requests.Count;
                return Response(new RunAccepted(runId, RunState.Running, now, $"/v1/runs/{runId}/events"));
            }
            if (path.StartsWith("/v1/runs/run-research-fixture-", StringComparison.Ordinal)
                && path.EndsWith("/events", StringComparison.Ordinal))
            {
                var runId = path.Split('/')[3];
                var item = new RunEvent(1, runId, RunEventTypes.RunFailed, now,
                    JsonSerializer.SerializeToElement(new RunFailedEvent(CaptureError, CaptureError, false), Json));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("data: " + JsonSerializer.Serialize(item, Json) + "\n\n",
                        Encoding.UTF8, "text/event-stream"),
                };
            }
            throw new InvalidOperationException("Unexpected request in non-inference fixture: " + request.Method + " " + path);
        }

        private static ModelRuntimeStatus[] Models() =>
        [
            new(GeneralModel, "general", true, true, "loaded", 32_768),
            new(CodingModel, "coding", true, true, "loaded", 32_768),
        ];

        private static HttpResponseMessage Response(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value, Json), Encoding.UTF8, "application/json"),
        };
    }
}
