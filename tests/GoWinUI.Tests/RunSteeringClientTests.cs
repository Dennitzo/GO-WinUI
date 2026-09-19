using GoAi.Contracts;
using GoWinUI.App.Services;
using GoWinUI.App.ViewModels;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace GoWinUI.Tests;

public sealed class RunSteeringClientTests
{
    [Theory]
    [InlineData("Erste\r\nZweite\rDritte")]
    [InlineData("Vorher\r\nGO_SESSION_TITLE: Versteckt\r\nNachher\r\n")]
    [InlineData("**GO\\_SESSION\\_TITLE:** Titel\n\nAntwort")]
    [InlineData("Sichtbar **GO_SESSION_TITLE: Titel**\n\n\nEnde")]
    [InlineData("\n# GOSESSIONTITLE: Titel\n\nText")]
    [InlineData("GO_SESSION_TITLE:\u00a0Titel\r\nAntwort")]
    public void GatewaySteeringOffsetsUseExactlyTheDesktopDurableText(string text)
    {
        Assert.Equal(GoWinUI.Core.Chat.ChatContentSanitizer.Sanitize(text), RunVisibleText.Canonicalize(text));
    }

    [Theory]
    [InlineData(MessageStatus.Cancelled)]
    [InlineData(MessageStatus.Failed)]
    public void ConfirmedUserSteeringSurvivesAnUnsuccessfulAssistantRun(MessageStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        var message = new ChatMessage(Guid.NewGuid(), Guid.NewGuid(), ChatRole.Assistant, "Teilantwort", status, now, now,
            ToolSteps: [new("steering-one", "assistant.steering", "completed", "Neue verbindliche Richtung",
                InputJson: "{\"sequence\":1}", ContentOffset: 4)]);
        var expanded = GoAiAssistantService.ExpandSteeringHistory([message]);
        var user = Assert.Single(expanded, item => item.Role == ChatRole.User);
        Assert.Equal(MessageStatus.Completed, user.Status);
        Assert.Equal("Neue verbindliche Richtung", Assert.Single(GoAiAssistantService.BuildHistoryMessages([message], 1000)).Content[0].Text);
        Assert.Contains(SessionContextPreparationService.SelectEligibleHistory([message]), item => item.Id == user.Id);
    }

    [Theory]
    [InlineData(false, true, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(false, true, true, true)]
    [InlineData(true, true, true, true)]
    public async Task SteeringPreservesTheRunningMessageToolsAndRunIdentityAndSurvivesReload(
        bool coding, bool reconnect, bool replace, bool metadata)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var runs = environment.Get<IGoAiRunRepository>();
        var session = await chats.CreateSessionAsync("Umlenken");
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "workspace")).FullName;
        if (coding) await chats.SetCodingWorkspacePathAsync(session.Id, workspace, activateCoding: true);
        var turn = await chats.AddTurnAsync(session.Id, "Ursprünglicher Auftrag");
        var rawPrefix = metadata ? "GO_SESSION_TITLE: Versteckt\r\nAlt.\r\nZweite." : "Alt. ";
        var visiblePrefix = RunVisibleText.Canonicalize(rawPrefix);
        var rawTail = metadata ? "\r\nNeu." : "Neu.";
        await chats.UpdateMessageAsync(turn.AssistantMessage.Id, reconnect ? visiblePrefix : string.Empty, MessageStatus.Streaming);
        await chats.SaveToolStepAsync(turn.AssistantMessage.Id, new("existing-read", "coding.read", "completed", "Bereits geprüft"));
        await chats.SaveDraftAsync(session.Id, "Nur die Änderung erklären.");
        var now = DateTimeOffset.UtcNow;
        var local = await runs.CreateAsync(new(Guid.NewGuid(), session.Id, turn.AssistantMessage.Id,
            coding ? PromptTriggerAction.Coding : null, Guid.NewGuid().ToString("N"), "run-steer", 0, "running",
            "fixture", null, now, now, WorkspacePath: coding ? workspace : null));
        await environment.Get<ISettingsStore>().SaveAsync(new AppSettings { IsAiConnectionEnabled = true,
            GoAiServerUrl = "http://127.0.0.1:65000" });
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var shared = new State(session.Id, coding)
        {
            InitialDelta = reconnect ? null : rawPrefix,
            VisibleTextOffset = visiblePrefix.Length,
            Continuation = new TextDeltaEvent(replace ? rawPrefix + rawTail : rawTail, replace ? 0 : null)
        };
        using var connection = new GoAiConnectionService(settings, NullLogger<GoAiConnectionService>.Instance,
            () => new Handler(shared));
        var recent = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
        using var service = new GoAiAssistantService(connection, chats, environment.Get<IAssistantAttachmentRepository>(),
            environment.Get<IChatArtifactRepository>(), runs, environment.Get<IClientToolExecutionRepository>(),
            environment.Get<IBinaryObjectStore>(), environment.Get<IDocumentIngestor>(), null!, null!, null!, null!, null!,
            settings, recent, NullLogger<GoAiAssistantService>.Instance);
        var updates = new ConcurrentQueue<GoAiAssistantUpdate>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var resume = service.ResumePendingAsync(update => { updates.Enqueue(update); return Task.CompletedTask; }, deadline.Token);
        await shared.StreamReady.Task.WaitAsync(deadline.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SteerAsync(Guid.NewGuid(), "Fremd", "wrong", "run-steer", deadline.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SteerAsync(session.Id, "Anderer Lauf", "wrong-run", "stale-run", deadline.Token));
        var accepted = await service.SteerAsync(session.Id, "Nur die Änderung erklären.", "input-one", "run-steer", deadline.Token);
        await resume;

        Assert.Equal("run-steer", accepted.RunId);
        Assert.Equal("completed", (await runs.GetAsync(local.Id))!.State);
        Assert.Equal(turn.AssistantMessage.Id, (await runs.GetAsync(local.Id))!.AssistantMessageId);
        var restored = (await chats.GetMessageAsync(turn.AssistantMessage.Id))!;
        Assert.Equal(RunVisibleText.Canonicalize(rawPrefix + rawTail), restored.Content);
        Assert.Equal(MessageStatus.Completed, restored.Status);
        Assert.Contains(restored.ToolSteps!, step => step.Id == "existing-read" && step.Detail == "Bereits geprüft");
        var input = Assert.Single(restored.ToolSteps!, step => step.Tool == "assistant.steering");
        Assert.Equal(visiblePrefix.Length, input.ContentOffset);
        Assert.All(updates.Where(update => update.ToolStep?.Tool == "assistant.steering"), update =>
            Assert.Equal(visiblePrefix.Length, update.ToolStep!.ContentOffset));
        Assert.Equal("Nur die Änderung erklären.", input.Detail);
        Assert.Equal("completed", input.Status);
        Assert.Equal("", (await chats.GetSessionAsync(session.Id))!.Draft);
        Assert.Single(shared.Requests, request => request.StartsWith("POST ", StringComparison.Ordinal));
        Assert.Contains("POST /v1/runs/run-steer/steer", shared.Requests);
        Assert.DoesNotContain(updates, update => update.Kind is GoAiAssistantUpdateKind.Cancelled or GoAiAssistantUpdateKind.Failed);
        var history = GoAiAssistantService.BuildHistoryMessages(await chats.ListMessagesAsync(session.Id), 10_000);
        Assert.Equal(["user", "assistant", "user", "assistant"], history.Select(message => message.Role));
        Assert.Equal(["Ursprünglicher Auftrag", visiblePrefix, "Nur die Änderung erklären.", rawTail.Replace("\r\n", "\n", StringComparison.Ordinal)],
            history.Select(message => string.Concat(message.Content.Select(part => part.Text))));

        await chats.SaveDraftAsync(session.Id, "Ein neuer Entwurf nach Abschluss");
        var duplicate = await service.SteerAsync(session.Id, "Nur die Änderung erklären.", "input-one", "run-steer", deadline.Token);
        Assert.True(duplicate.Duplicate);
        Assert.Equal(accepted.RunId, duplicate.RunId);
        Assert.Equal("Ein neuer Entwurf nach Abschluss", (await chats.GetSessionAsync(session.Id))!.Draft);
        Assert.DoesNotContain(shared.Requests, request => request == "POST /v1/runs" || request.EndsWith("/cancel", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AcknowledgingAnEarlierPromptCannotEraseANewerDraftOrAnotherSessionsDraft()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var first = await chats.CreateSessionAsync("A");
        var other = await chats.CreateSessionAsync("B");
        await chats.SaveDraftAsync(first.Id, "Neue Eingabe");
        await chats.SaveDraftAsync(other.Id, "Alter Prompt");
        await chats.ClearDraftIfMatchesAsync(first.Id, "Alter Prompt");
        Assert.Equal("Neue Eingabe", (await chats.GetSessionAsync(first.Id))!.Draft);
        Assert.Equal("Alter Prompt", (await chats.GetSessionAsync(other.Id))!.Draft);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task ResumeHonorsAnAlreadyCancelledMessageWithoutChangingItsHistoryOrToolReceipts(
        bool workspaceExists, bool cancelFails)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var runs = environment.Get<IGoAiRunRepository>();
        var session = await chats.CreateSessionAsync("Abgebrochener Auftrag");
        var workspace = Path.Combine(environment.Directory, "workspace");
        if (workspaceExists) Directory.CreateDirectory(workspace);
        var turn = await chats.AddTurnAsync(session.Id, "Auftrag vor dem Neustart");
        await chats.UpdateMessageAsync(turn.AssistantMessage.Id, "Bereits sichtbare Teilantwort.", MessageStatus.Cancelled, "Vom Benutzer abgebrochen.");
        await chats.SaveToolStepAsync(turn.AssistantMessage.Id, new("unfinished-tool", "coding.write", "running",
            "Unveränderter Werkzeugbeleg", InputJson: "{\"path\":\"old.txt\"}", ContentOffset: 7));
        var before = await ReadMessageRowAsync();
        var sessionBefore = JsonSerializer.Serialize(await chats.GetSessionAsync(session.Id));
        var now = DateTimeOffset.UtcNow;
        var run = await runs.CreateAsync(new(Guid.NewGuid(), session.Id, turn.AssistantMessage.Id,
            PromptTriggerAction.Coding, Guid.NewGuid().ToString("N"), "run-already-cancelled", 41, "running",
            "fixture", null, now, now, WorkspacePath: workspace));
        await environment.Get<ISettingsStore>().SaveAsync(new AppSettings
            { IsAiConnectionEnabled = true, GoAiServerUrl = "http://127.0.0.1:65000" });
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var requests = new ConcurrentQueue<string>();
        using var connection = new GoAiConnectionService(settings, NullLogger<GoAiConnectionService>.Instance,
            () => new CancelHandler(requests, cancelFails));
        var recent = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
        using var service = new GoAiAssistantService(connection, chats, environment.Get<IAssistantAttachmentRepository>(),
            environment.Get<IChatArtifactRepository>(), runs, environment.Get<IClientToolExecutionRepository>(),
            environment.Get<IBinaryObjectStore>(), environment.Get<IDocumentIngestor>(), null!, null!, null!, null!, null!,
            settings, recent, NullLogger<GoAiAssistantService>.Instance);
        var updates = new ConcurrentQueue<GoAiAssistantUpdate>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await service.ResumePendingAsync(update => { updates.Enqueue(update); return Task.CompletedTask; }, deadline.Token);
        await service.ResumePendingAsync(update => { updates.Enqueue(update); return Task.CompletedTask; }, deadline.Token);

        var restoredRun = (await runs.GetAsync(run.Id))!;
        Assert.Equal("cancelled", restoredRun.State);
        Assert.Equal("client.run_already_cancelled", restoredRun.ErrorCode);
        Assert.Equal(run.ServerRunId, restoredRun.ServerRunId);
        Assert.Equal(run.LastEventId, restoredRun.LastEventId);
        Assert.Empty(await runs.ListResumableAsync());
        Assert.Equal(before, await ReadMessageRowAsync());
        Assert.Equal(sessionBefore, JsonSerializer.Serialize(await chats.GetSessionAsync(session.Id)));
        Assert.Empty(updates);
        Assert.Equal("POST /v1/runs/run-already-cancelled/cancel", Assert.Single(requests));
        Assert.False(service.IsRunning);
        Assert.Null(service.ActiveRunId);
        Assert.Null(service.ActiveSessionId);

        async Task<string> ReadMessageRowAsync()
        {
            await using var database = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = environment.Get<IGoDatabase>().DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await database.OpenAsync();
            await using var command = database.CreateCommand();
            command.CommandText = "SELECT * FROM chat_messages WHERE id=$id;";
            command.Parameters.AddWithValue("$id", turn.AssistantMessage.Id.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            return JsonSerializer.Serialize(values);
        }
    }

    private sealed class CancelHandler(ConcurrentQueue<string> requests, bool cancelFails) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Enqueue(request.Method + " " + request.RequestUri!.AbsolutePath);
            if (cancelFails) throw new HttpRequestException("Gateway ist während der Wiederherstellung offline.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    private sealed class State(Guid sessionId, bool coding)
    {
        internal Guid SessionId { get; } = sessionId;
        internal bool Coding { get; } = coding;
        internal string? InitialDelta { get; init; }
        internal int VisibleTextOffset { get; init; } = 5;
        internal TextDeltaEvent Continuation { get; init; } = new("Neu.");
        internal ConcurrentQueue<string> Requests { get; } = new();
        internal TaskCompletionSource StreamReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<RunSteeringRequest> Steered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Handler(State shared) : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Json = GoAiProtocol.CreateJsonOptions();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            shared.Requests.Enqueue(request.Method + " " + path);
            if (path == "/v1/capabilities") return Response(new { protocolVersion = "1.0", supportsRunSteering = true });
            if (path.EndsWith("/steer", StringComparison.Ordinal))
            {
                var input = JsonSerializer.Deserialize<RunSteeringRequest>(await request.Content!.ReadAsStringAsync(cancellationToken), Json)!;
                Assert.Equal(shared.SessionId.ToString("D"), input.SessionId);
                var first = shared.Steered.TrySetResult(input);
                return Response(new RunSteeringAccepted("run-steer", input.InputId, 1, first ? "accepted" : "applied", !first));
            }
            if (path.EndsWith("/events", StringComparison.Ordinal))
            {
                shared.StreamReady.TrySetResult();
                var input = await shared.Steered.Task.WaitAsync(cancellationToken);
                var events = new List<(string Type, object Data)>();
                if (shared.InitialDelta is { } prefix) events.Add((RunEventTypes.TextDelta, new TextDeltaEvent(prefix)));
                events.Add((RunSteeringEventTypes.Accepted, new RunSteeringEvent(input.InputId, 1, input.SessionId, input.Text)));
                events.Add((RunSteeringEventTypes.Applied, new RunSteeringEvent(input.InputId, 1, input.SessionId, input.Text, shared.VisibleTextOffset)));
                events.Add((RunEventTypes.TextDelta, shared.Continuation));
                events.Add((RunEventTypes.RunCompleted, new RunCompletedEvent(null, "fixture", 20, 5)));
                var stream = string.Concat(events.Select((value, index) => "data: " + JsonSerializer.Serialize(new RunEvent(index + 1,
                    "run-steer", value.Type, DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(value.Data, Json)), Json) + "\n\n"));
                return new(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") };
            }
            if (path == "/v1/runs/run-steer") return Response(new RunSnapshot("run-steer", RunState.Completed,
                shared.Coding ? RunMode.Coding : RunMode.General, "fixture", null, 4, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            throw new InvalidOperationException("Unexpected fixture request: " + request.Method + " " + path);
        }

        private static HttpResponseMessage Response(object value) => new(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(value, Json), Encoding.UTF8, "application/json") };
    }
}
