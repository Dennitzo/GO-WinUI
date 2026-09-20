using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using GoAi.Contracts;
using GoWinUI.App.Services;
using GoWinUI.App.ViewModels;
using GoWinUI.BricsCad.Protocol;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace GoWinUI.Tests;

public sealed class BlenderCodingRequestTests
{
    private const string GeneralModel = "fixture/general-model";
    private const string CodingModel = "fixture/coding-model";
    private const string CaptureError = "fixture.request_captured";
    private const string FirstPrompt = "Modelliere eine kleine Forschungsstation nach Bild und Briefing.";
    private const string DocumentText = "Der Entwurf benötigt eine seitliche Luftschleuse und zwei Solarpaneele.";
    private static readonly string[] RequiredCapabilities = ["coding", "coding.evidence", "workspace", "blender", "visual-tools", "documents", "documentIo"];
    private static readonly string[] RequiredServerTools = ["web.search", "web.fetch", "web.deepResearch", "youtube.search", "media.inspect", "media.analyze", "image.generate", "speech.synthesize", "math.evaluate", "context.embed", "context.retrieve"];
    private static readonly byte[] ReferencePng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jhV0AAAAASUVORK5CYII=");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlenderChipSendsCodingRequestWithAttachmentsAndKeepsCodingForTheNextPrompt(bool deepResearch)
    {
        await using var fixture = await Fixture.CreateAsync();
        var attachments = fixture.Environment.Get<IAssistantAttachmentRepository>();
        await using var imageStream = new MemoryStream(ReferencePng, writable: false);
        var attachment = await attachments.ImportAsync(fixture.SessionId, "Grundriss.png", "image/png", imageStream);
        var document = await ImportDocumentAsync(fixture);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await fixture.SendAsync(FirstPrompt, "blender", deepResearch, deadline.Token);

        var first = Assert.Single(fixture.Requests);
        AssertCodingRequest(fixture, first, deepResearch, continueSession: false);
        Assert.Contains("blender.execute", first.Messages[^1].Content[0].Text, StringComparison.Ordinal);
        Assert.Contains(FirstPrompt, first.Messages[^1].Content[0].Text, StringComparison.Ordinal);
        AssertAttachments(fixture, first, attachment, document);

        var changed = Assert.Single(fixture.Events, item => item.Name == "session.changed").Payload;
        Assert.Equal("coding", changed.GetProperty("selectedToolAction").GetString());
        Assert.Equal(CodingModel, changed.GetProperty("model").GetString());
        Assert.Equal(CodingModel, changed.GetProperty("reasoningModelId").GetString());
        Assert.Equal("coding", changed.GetProperty("reasoningRole").GetString());
        var started = Assert.Single(fixture.Events, item => item.Name == "chat.started").Payload;
        Assert.Equal(CodingModel, started.GetProperty("model").GetString());
        Assert.Single(started.GetProperty("attachments").EnumerateArray());

        // Follow the real composer path without another explicit tool selection.
        // The non-retryable fixture terminal ends each captured run before inference.
        const string nextPrompt = "Ergänze ein Fenster am bestehenden Modell.";
        await fixture.SendAsync(nextPrompt, null, false, deadline.Token);

        Assert.Equal(2, fixture.Requests.Count);
        var next = fixture.Requests[1];
        AssertCodingRequest(fixture, next, deepResearch: false, continueSession: true);
        Assert.Contains(nextPrompt, next.Messages[^1].Content[0].Text, StringComparison.Ordinal);
        Assert.Contains(next.Messages.Take(next.Messages.Count - 1), message =>
            message.Role == "user" && message.Content.Any(part => part.Text == FirstPrompt));
        AssertAttachments(fixture, next, attachment, document);
        Assert.NotEqual(Assert.Single(first.UploadIds!), Assert.Single(next.UploadIds!));
        var session = await fixture.Environment.Get<IChatRepository>().GetSessionAsync(fixture.SessionId, deadline.Token);
        Assert.Equal(PersistentToolAction.Coding, session!.PersistentToolAction);
        Assert.Equal(fixture.Workspace, session.CodingWorkspacePath);
        Assert.Equal(attachment.Id, Assert.Single(await attachments.ListAsync(fixture.SessionId, deadline.Token)).Id);
        Assert.Equal(document.Id, Assert.Single(await fixture.Environment.Get<IDocumentIngestor>().ListAsync(fixture.SessionId, deadline.Token)).Id);
        Assert.All(fixture.Events.Where(item => item.Name == "chat.started"), item =>
            Assert.Equal(CodingModel, item.Payload.GetProperty("model").GetString()));
        var messages = await fixture.Environment.Get<IChatRepository>().ListMessagesAsync(fixture.SessionId, deadline.Token);
        Assert.Equal(CaptureError, messages[^1].Error);
        Assert.Equal(MessageStatus.Failed, messages[^1].Status);
        Assert.Empty(fixture.UnexpectedRequests);
    }

    [Theory]
    [InlineData(false, true, "Workspace")]
    [InlineData(true, false, "Coding-AI-Modell")]
    public async Task BlenderRequiresTheCodingWorkspaceAndModelInsteadOfFallingBackToGeneral(
        bool workspaceAvailable, bool codingModelSelected, string errorFragment)
    {
        await using var fixture = await Fixture.CreateAsync(workspaceAvailable, codingModelSelected);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await fixture.SendAsync(FirstPrompt, "blender", false, deadline.Token);

        Assert.Empty(fixture.Requests);
        var messages = await fixture.Environment.Get<IChatRepository>().ListMessagesAsync(fixture.SessionId, deadline.Token);
        Assert.Equal(MessageStatus.Failed, messages[^1].Status);
        Assert.Contains(errorFragment, messages[^1].Error, StringComparison.Ordinal);
        Assert.Empty(fixture.UnexpectedRequests);
    }

    private static void AssertCodingRequest(Fixture fixture, RunRequest request, bool deepResearch, bool continueSession)
    {
        Assert.Equal(RunMode.Coding, request.Mode);
        Assert.Equal(CodingModel, request.PreferredCodingModelId);
        Assert.Null(request.PreferredGeneralModelId);
        Assert.Equal(fixture.SessionId.ToString("D"), request.SessionId);
        Assert.Equal(deepResearch, request.DeepResearch);
        Assert.Equal("none", request.ReasoningEffort);
        Assert.Equal(0, request.Limits!.TimeoutSeconds);
        Assert.Equal(65_536, request.Limits.MaximumContextTokens);
        Assert.Null(request.Limits.MaximumOutputTokens);
        Assert.Equal(fixture.Workspace, request.CodingOptions!.WorkspacePath);
        Assert.Equal(continueSession, request.CodingOptions.ContinueSessionContext);
        Assert.True(request.CodingOptions.UseWorkingState);
        Assert.Equal("maximum", request.CodingOptions.ReasoningPolicy);
        Assert.All(RequiredCapabilities, capability => Assert.Contains(capability, request.ClientCapabilities!));
        Assert.All(RequiredServerTools, tool => Assert.Contains(tool, request.AllowedServerTools!));
        Assert.DoesNotContain("document-agent", request.ClientCapabilities!);
    }

    private static void AssertAttachments(Fixture fixture, RunRequest request, AssistantAttachment attachment, StoredDocument document)
    {
        var uploadId = Assert.Single(request.UploadIds!);
        var image = Assert.Single(request.Messages[^1].Content, part => part.Type == "upload");
        Assert.Equal(uploadId, image.UploadId);
        Assert.Equal(attachment.FileName, image.FileName);
        Assert.Equal(attachment.ContentType, image.MediaType);
        var upload = fixture.Uploads[uploadId];
        Assert.Equal(ReferencePng, upload.Bytes);
        Assert.Equal(attachment.Sha256, upload.Manifest.Sha256, ignoreCase: true);
        Assert.True(upload.Completed);

        var prompt = request.Messages[^1].Content[0].Text!;
        Assert.Contains("documents.list", prompt, StringComparison.Ordinal);
        Assert.Contains("documents.readPages", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(DocumentText, prompt, StringComparison.Ordinal);
        using var catalog = JsonDocument.Parse(prompt[(prompt.LastIndexOf('\n') + 1)..]);
        var entry = Assert.Single(catalog.RootElement.EnumerateArray());
        Assert.Equal(document.Id, entry.GetProperty("documentId").GetGuid());
        Assert.Equal(document.FileName, entry.GetProperty("fileName").GetString());
        Assert.Equal(document.PageCount, entry.GetProperty("pageCount").GetInt32());
    }

    private static async Task<StoredDocument> ImportDocumentAsync(Fixture fixture)
    {
        using var content = new MemoryStream();
        using (var word = WordprocessingDocument.Create(content, DocumentFormat.OpenXml.WordprocessingDocumentType.Document, autoSave: true))
            word.AddMainDocumentPart().Document = new Document(new Body(new Paragraph(new Run(new Text(DocumentText)))));
        await using var input = new MemoryStream(content.ToArray(), writable: false);
        var documents = fixture.Environment.Get<IDocumentIngestor>();
        var imported = await documents.ImportAsync(fixture.SessionId, "Designbrief.docx", input);
        Assert.True(imported.Success, imported.Error);
        Assert.True(imported.HasExtractableText);
        Assert.Contains(DocumentText, Assert.Single(await documents.ReadPagesAsync(imported.Document!.Id)).Text, StringComparison.Ordinal);
        return imported.Document;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SettingsCoordinator _settings;
        private readonly GoAiConnectionService _connection;
        private readonly BricsCadBridgeHost _bricsCad;
        private readonly MicrophoneTranscriptionService _microphone;
        private readonly GoAiAssistantService _service;
        private readonly AssistantCoordinator _coordinator;

        private Fixture(TestEnvironment environment, Guid sessionId, string workspace, SettingsCoordinator settings)
        {
            Environment = environment;
            SessionId = sessionId;
            Workspace = workspace;
            _settings = settings;
            _connection = new(settings, NullLogger<GoAiConnectionService>.Instance, () => new Handler(this));
            _bricsCad = new BricsCadBridgeHost();
            var documents = environment.Get<IDocumentIngestor>();
            var chats = environment.Get<IChatRepository>();
            var broker = new LocalToolBroker(_connection, _bricsCad, documents, null!, chats);
            _microphone = new(_connection, settings, NullLogger<MicrophoneTranscriptionService>.Instance);
            var recent = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
            _service = new(_connection, chats, environment.Get<IAssistantAttachmentRepository>(),
                environment.Get<IChatArtifactRepository>(), environment.Get<IGoAiRunRepository>(),
                environment.Get<IClientToolExecutionRepository>(), environment.Get<IBinaryObjectStore>(), documents,
                new DocumentContextPreparationService(documents), new SessionContextPreparationService(chats),
                broker, null!, _microphone, settings, recent, NullLogger<GoAiAssistantService>.Instance);
            _coordinator = new(chats, environment.Get<IWorkflowRepository>(), documents, environment.Get<IContextAssembler>(),
                environment.Get<IPromptTriggerRepository>(), environment.Get<IAssistantAttachmentRepository>(),
                environment.Get<IChatArtifactRepository>(), environment.Get<IConversationSnapshotRepository>(),
                _service, settings, recent, _microphone);
        }

        internal TestEnvironment Environment { get; }
        internal Guid SessionId { get; }
        internal string Workspace { get; }
        internal List<RunRequest> Requests { get; } = [];
        internal Dictionary<string, UploadedReference> Uploads { get; } = [];
        internal ConcurrentQueue<(string Name, JsonElement Payload)> Events { get; } = new();
        internal ConcurrentQueue<string> UnexpectedRequests { get; } = new();

        internal static async Task<Fixture> CreateAsync(bool workspaceAvailable = true, bool codingModelSelected = true)
        {
            var environment = await TestEnvironment.CreateAsync();
            var chats = environment.Get<IChatRepository>();
            var session = await chats.CreateSessionAsync("Blender als Coding-Projekt");
            var workspace = Path.Combine(environment.Directory, "workspace");
            if (workspaceAvailable)
            {
                Directory.CreateDirectory(workspace);
                await chats.SetCodingWorkspacePathAsync(session.Id, workspace, activateCoding: false);
            }
            await environment.Get<ISettingsStore>().SaveAsync(new AppSettings
            {
                IsAiConnectionEnabled = true,
                GoAiServerUrl = "http://127.0.0.1:65000",
                ActiveSessionId = session.Id,
                SelectedModel = GeneralModel,
                SelectedCodingModel = codingModelSelected ? CodingModel : null,
                ReasoningEffortsByModel = new Dictionary<string, string>
                {
                    [GoAiAssistantService.ReasoningKey(GeneralModel, "general")] = "high",
                    [GoAiAssistantService.ReasoningKey(CodingModel, "coding")] = "none",
                },
            });
            var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
            await settings.InitializeAsync();
            return new Fixture(environment, session.Id, workspace, settings);
        }

        internal Task SendAsync(string prompt, string? action, bool deepResearch, CancellationToken token) =>
            _coordinator.HandleAsync(new WebBridgeEnvelope(AssistantWebBridge.ProtocolVersion, "chat.send",
                    Guid.NewGuid().ToString("D"), JsonSerializer.SerializeToElement(new
                    {
                        sessionId = SessionId, prompt, toolAction = action, deepResearch,
                    })),
                (name, payload, _) =>
                {
                    Events.Enqueue((name, JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web)));
                    return Task.CompletedTask;
                }, token);

        public async ValueTask DisposeAsync()
        {
            _service.Dispose();
            _microphone.Dispose();
            await _bricsCad.DisposeAsync();
            _connection.Dispose();
            _settings.Dispose();
            await Environment.DisposeAsync();
        }
    }

    private sealed class UploadedReference(UploadManifest manifest)
    {
        internal UploadManifest Manifest { get; } = manifest;
        internal byte[] Bytes { get; set; } = [];
        internal bool Completed { get; set; }
    }

    private sealed class Handler(Fixture fixture) : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Json = GoAiProtocol.CreateJsonOptions();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            var now = DateTimeOffset.UtcNow;
            if (path == "/v1/models/status") return Response(new ModelStatusSnapshot(true, "fixture", Models(), now));
            if (path == "/v1/models/coding")
                return Response(new CodingModelCatalogResponse(Models().Where(static model => model.Role == "coding").ToArray(), "fixture", true, null, now));
            if (path == "/v1/capabilities")
                return Response(new CapabilitySnapshot(GoAiProtocol.Version, "fixture", [],
                    ["coding.updatePlan", .. RequiredServerTools],
                    [ClientToolNames.CodingReadOutput, ClientToolNames.CodingSearchRunEvidence],
                    new Dictionary<string, long>(), [], true, GoAiProtocol.UploadChunkSize, SupportsCodingSessionContext: true));
            if (path == "/v1/uploads" && request.Method == HttpMethod.Post)
            {
                var manifest = JsonSerializer.Deserialize<UploadManifest>(await request.Content!.ReadAsStringAsync(token), Json)!;
                var uploadId = "upload-fixture-" + fixture.Uploads.Count.ToString(CultureInfo.InvariantCulture);
                fixture.Uploads.Add(uploadId, new(manifest));
                return Response(new UploadCreated(uploadId, manifest.ChunkSize, manifest.ChunkCount!.Value, [], now.AddHours(1)));
            }
            if (path.StartsWith("/v1/uploads/", StringComparison.Ordinal))
            {
                var segments = path.Split('/');
                var uploadId = segments[3];
                var upload = fixture.Uploads[uploadId];
                if (request.Method == HttpMethod.Put && segments[4] == "chunks")
                {
                    var bytes = await request.Content!.ReadAsByteArrayAsync(token);
                    upload.Bytes = bytes;
                    var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
                    Assert.Equal(hash, Assert.Single(request.Headers.GetValues("X-Chunk-SHA256")));
                    return Response(new UploadChunkReceipt(uploadId, int.Parse(segments[5], CultureInfo.InvariantCulture), hash, bytes.LongLength, true));
                }
                if (request.Method == HttpMethod.Post && segments[4] == "complete")
                {
                    upload.Completed = true;
                    return Response(new UploadCompleted(uploadId, upload.Manifest.FileName, upload.Manifest.MediaType,
                        upload.Manifest.Length, upload.Manifest.Sha256, now.AddHours(1)));
                }
                if (request.Method == HttpMethod.Delete) return new(HttpStatusCode.NoContent);
            }
            if (path == "/v1/runs" && request.Method == HttpMethod.Post)
            {
                fixture.Requests.Add(JsonSerializer.Deserialize<RunRequest>(await request.Content!.ReadAsStringAsync(token), Json)!);
                var runId = "run-blender-fixture-" + fixture.Requests.Count.ToString(CultureInfo.InvariantCulture);
                return Response(new RunAccepted(runId, RunState.Running, now, $"/v1/runs/{runId}/events"));
            }
            if (path.StartsWith("/v1/runs/run-blender-fixture-", StringComparison.Ordinal) && path.EndsWith("/events", StringComparison.Ordinal))
            {
                var item = new RunEvent(1, path.Split('/')[3], RunEventTypes.RunFailed, now,
                    JsonSerializer.SerializeToElement(new RunFailedEvent(CaptureError, CaptureError, false), Json));
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent("data: " + JsonSerializer.Serialize(item, Json) + "\n\n", Encoding.UTF8, "text/event-stream"),
                };
            }
            var unexpected = request.Method + " " + path;
            fixture.UnexpectedRequests.Enqueue(unexpected);
            throw new InvalidOperationException("Unexpected request in non-inference fixture: " + unexpected);
        }

        private static ModelRuntimeStatus[] Models() =>
        [
            new(GeneralModel, "general", true, true, "loaded", 8_192,
                SupportsTools: true, SupportsVision: true, ReasoningEfforts: ["none", "high"], DefaultReasoningEffort: "high"),
            new(CodingModel, "coding", true, true, "loaded", 65_536,
                SupportsTools: true, SupportsVision: true, ReasoningEfforts: ["none", "high"], DefaultReasoningEffort: "high"),
        ];

        private static HttpResponseMessage Response(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value, Json), Encoding.UTF8, "application/json"),
        };
    }
}
