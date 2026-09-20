using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GoAi.Contracts;
using GoWinUI.App.Services;
using GoWinUI.App.ViewModels;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using GoWinUI.Infrastructure;
using GoWinUI.Infrastructure.Repositories;
using GoWinUI.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace GoWinUI.Tests;

public sealed class MediaArtifactAnchorIntegrationTests
{
    private const string RunId = "run-artifact-reconnect";
    private const string FirstStep = "image-call-before-reconnect";
    private const string NextStep = "later-tool-call";
    private const string Prefix = "Das erste Bild wird geprüft.";
    private const string Tail = " Danach folgt die nächste Aktion.";

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ReattachedStreamUsesDurableArtifactAnchorInsteadOfTheLastObservedTool(bool coding, bool laterToolAlreadyStarted)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var runs = environment.Get<IGoAiRunRepository>();
        var session = await chats.CreateSessionAsync("Persistente Bildposition");
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "workspace")).FullName;
        if (coding) await chats.SetCodingWorkspacePathAsync(session.Id, workspace, activateCoding: true);
        var turn = await chats.AddTurnAsync(session.Id, "Analysiere das Bild und arbeite danach weiter.");
        await chats.UpdateMessageAsync(turn.AssistantMessage.Id, Prefix, MessageStatus.Streaming);
        await chats.SaveToolStepAsync(turn.AssistantMessage.Id, new(FirstStep, "media.analyze", "completed",
            "Erstes Bild geprüft", ContentOffset: Prefix.Length));
        var now = DateTimeOffset.UtcNow;
        var run = await runs.CreateAsync(new(Guid.NewGuid(), session.Id, turn.AssistantMessage.Id,
            coding ? PromptTriggerAction.Coding : null, Guid.NewGuid().ToString("N"), RunId, 7, "running",
            "fixture-model", null, now, now, WorkspacePath: coding ? workspace : null));
        await environment.Get<ISettingsStore>().SaveAsync(new AppSettings { IsAiConnectionEnabled = true,
            GoAiServerUrl = "http://127.0.0.1:65000", ActiveSessionId = session.Id });
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var fixture = new Fixture(coding, laterToolAlreadyStarted);
        using var connection = new GoAiConnectionService(settings, NullLogger<GoAiConnectionService>.Instance,
            () => new Handler(fixture));
        var recent = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
        using var service = new GoAiAssistantService(connection, chats, environment.Get<IAssistantAttachmentRepository>(),
            environment.Get<IChatArtifactRepository>(), runs, environment.Get<IClientToolExecutionRepository>(),
            environment.Get<IBinaryObjectStore>(), environment.Get<IDocumentIngestor>(), null!, null!, null!, null!, null!,
            settings, recent, NullLogger<GoAiAssistantService>.Instance);
        var updates = new ConcurrentQueue<GoAiAssistantUpdate>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await service.ResumePendingAsync(update => { updates.Enqueue(update); return Task.CompletedTask; }, timeout.Token);

        Assert.DoesNotContain(updates, update => update.Kind == GoAiAssistantUpdateKind.Failed);
        Assert.Equal("completed", (await runs.GetAsync(run.Id))!.State);
        var completed = Assert.Single(updates, update => update.Kind == GoAiAssistantUpdateKind.Completed);
        Assert.Equal(Prefix + Tail, completed.Message.Content);
        Assert.All(updates.Where(update => update.Kind == GoAiAssistantUpdateKind.ArtifactsChanged), update =>
            Assert.Equal(FirstStep, Assert.Single(update.Artifacts!).StepId));
        Assert.Equal(FirstStep, Assert.Single(completed.Artifacts!).StepId);
        Assert.Contains(completed.Message.ToolSteps!, step => step.Id == NextStep && step.Status == "completed");
        Assert.Contains(completed.Message.ToolSteps!, step => step.Id == FirstStep && step.ContentOffset == Prefix.Length);

        await using var database = new SqliteDatabase(new GoInfrastructureOptions { DataDirectory = environment.Directory },
            NullLogger<SqliteDatabase>.Instance);
        await database.InitializeAsync();
        var restored = (await new SqliteConversationSnapshotRepository(database).GetAsync(session.Id))!;
        var savedMessage = Assert.Single(restored.Messages, message => message.Id == turn.AssistantMessage.Id);
        var savedArtifact = Assert.Single(restored.Artifacts[turn.AssistantMessage.Id]);
        Assert.Equal(FirstStep, savedArtifact.StepId);
        Assert.Equal(Prefix + Tail, savedMessage.Content);
        Assert.Equal(FirstStep, savedMessage.ToolSteps![0].Id);
        Assert.Equal(NextStep, savedMessage.ToolSteps[1].Id);
        Assert.NotEqual(savedMessage.ToolSteps[^1].Id, savedArtifact.StepId);

        var coordinator = new AssistantCoordinator(chats, environment.Get<IWorkflowRepository>(), environment.Get<IDocumentIngestor>(),
            environment.Get<IContextAssembler>(), environment.Get<IPromptTriggerRepository>(), environment.Get<IAssistantAttachmentRepository>(),
            environment.Get<IChatArtifactRepository>(), environment.Get<IConversationSnapshotRepository>(), service, settings, recent);
        var snapshot = JsonSerializer.SerializeToElement(await coordinator.BuildSnapshotAsync(), JsonSerializerOptions.Web);
        var shown = Assert.Single(snapshot.GetProperty("messages").EnumerateArray(),
            message => message.GetProperty("id").GetGuid() == turn.AssistantMessage.Id);
        Assert.Equal(FirstStep, Assert.Single(shown.GetProperty("artifacts").EnumerateArray()).GetProperty("stepId").GetString());
        Assert.Single(fixture.Requests, request => request.EndsWith("/events", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Requests, request => request.StartsWith("POST ", StringComparison.Ordinal));
    }

    private sealed class Fixture(bool coding, bool laterToolAlreadyStarted)
    {
        internal bool Coding { get; } = coding;
        internal bool LaterToolAlreadyStarted { get; } = laterToolAlreadyStarted;
        internal ConcurrentQueue<string> Requests { get; } = new();
        internal byte[] Bytes { get; } = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jhV0AAAAASUVORK5CYII=");
    }

    private sealed class Handler(Fixture fixture) : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Json = GoAiProtocol.CreateJsonOptions();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            fixture.Requests.Enqueue(request.Method + " " + path);
            if (path.EndsWith("/events", StringComparison.Ordinal))
            {
                Assert.Equal("7", Assert.Single(request.Headers.GetValues(GoAiHeaders.LastEventId)));
                var descriptor = new ArtifactDescriptor("render-thumb", "render.png", "image/png", fixture.Bytes.Length,
                    Convert.ToHexStringLower(SHA256.HashData(fixture.Bytes)), DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow.AddDays(1), StepId: FirstStep);
                var events = new List<(string Type, object Data)>();
                var nextStart = (RunEventTypes.ServerToolStarted, (object)new { callId = NextStep, tool = "web.search", target = "Weitere Quellen" });
                if (fixture.LaterToolAlreadyStarted) events.Add(nextStart);
                events.Add((RunEventTypes.ArtifactCreated, descriptor));
                // A duplicate artifact notification must remain one durable preview.
                events.Add((RunEventTypes.ArtifactCreated, descriptor));
                if (!fixture.LaterToolAlreadyStarted) events.Add(nextStart);
                events.Add((RunEventTypes.ServerToolCompleted, new { callId = NextStep, tool = "web.search", success = true, result = new { count = 1 } }));
                events.Add((RunEventTypes.TextDelta, new TextDeltaEvent(Tail)));
                events.Add((RunEventTypes.RunCompleted, new RunCompletedEvent(null, "fixture-model", 20, 5)));
                var stream = string.Concat(events.Select((item, index) => "data: " + JsonSerializer.Serialize(new RunEvent(8 + index,
                    RunId, item.Type, DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(item.Data, Json)), Json) + "\n\n"));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") });
            }
            if (path == "/v1/artifacts/render-thumb") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent(fixture.Bytes) });
            if (path == "/v1/runs/" + RunId) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(JsonSerializer.Serialize(new RunSnapshot(RunId, RunState.Completed,
                    fixture.Coding ? RunMode.Coding : RunMode.General, "fixture-model", null, 20,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), Json), Encoding.UTF8, "application/json") });
            throw new InvalidOperationException("Unexpected fixture request: " + request.Method + " " + path);
        }
    }
}
