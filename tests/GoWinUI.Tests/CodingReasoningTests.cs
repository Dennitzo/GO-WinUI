using System.Collections.Concurrent;
using System.Text.Json;
using GoAi.Contracts;
using GoWinUI.App.Services;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;

namespace GoWinUI.Tests;

public sealed class CodingReasoningTests
{
    private static readonly DateTimeOffset Started = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LiveDeltasPreserveMarkdownAndTheirOriginalPositionInTheAnswer()
    {
        var first = Apply(null, new("Zunächst prüfe ich `main.py`.\n\n", 3), eventId: 41, contentOffset: 12);
        var appended = Apply(first, new("- Änderung erklären\n- Prüfung ausführen 🧪", 3), eventId: 42, contentOffset: 90);

        Assert.Equal("assistant.reasoning", appended.Tool);
        Assert.Equal("running", appended.Status);
        Assert.Equal("Zunächst prüfe ich `main.py`.\n\n- Änderung erklären\n- Prüfung ausführen 🧪", appended.Detail);
        Assert.Equal(12, appended.ContentOffset);
        Assert.Equal(Started.AddSeconds(41), appended.StartedAt);
        Assert.Null(appended.CompletedAt);
        Assert.True(appended.UpdatedAt > first.UpdatedAt);
        using var input = JsonDocument.Parse(appended.InputJson!);
        Assert.Equal(3, input.RootElement.GetProperty("round").GetInt32());
        Assert.Equal("main", input.RootElement.GetProperty("phase").GetString());
        using var output = JsonDocument.Parse(appended.OutputJson!);
        Assert.Equal(42, output.RootElement.GetProperty("lastEventId").GetInt64());
    }

    [Fact]
    public void ProviderRetryReplacesPartialReasoningAndKeepsTheSameTimelineStep()
    {
        var partial = Apply(null, new("Ein verworfener Anfang", 2), eventId: 10, contentOffset: 30);
        var reset = Apply(partial, new("", 2, ReplaceFrom: 0, State: "reset"), eventId: 11, contentOffset: 60);
        var retry = Apply(reset, new("Der neue Versuch.\n", 2), eventId: 12, contentOffset: 60);
        var completed = Apply(retry, new("", 2, State: "completed"), eventId: 13, contentOffset: 60);

        Assert.Equal("", reset.Detail);
        Assert.Equal("running", reset.Status);
        Assert.Equal(partial.Id, completed.Id);
        Assert.Equal("Der neue Versuch.\n", completed.Detail);
        Assert.Equal("completed", completed.Status);
        Assert.Equal(partial.StartedAt, completed.StartedAt);
        Assert.Equal(partial.ContentOffset, completed.ContentOffset);
        Assert.NotNull(completed.CompletedAt);
        Assert.DoesNotContain("verworfener", completed.Detail);
    }

    [Fact]
    public void ReplayedAndOutOfOrderEventsDoNotDuplicateTextOrReopenACompletedStep()
    {
        var current = Apply(null, new("Einmal anzeigen.", 1), eventId: 50);
        Assert.Null(GoAiAssistantService.ApplyReasoningDelta(current, new("Einmal anzeigen.", 1), current.Id, 50, 0, Started.AddMinutes(2)));
        Assert.Null(GoAiAssistantService.ApplyReasoningDelta(current, new("Alter Stand", 1), current.Id, 49, 0, Started.AddMinutes(2)));

        var complete = Apply(current, new("", 1, State: "completed"), eventId: 51);
        Assert.Null(GoAiAssistantService.ApplyReasoningDelta(complete, new("", 1, State: "completed"), complete.Id, 51, 0, Started.AddMinutes(3)));
        Assert.Null(GoAiAssistantService.ApplyReasoningDelta(complete, new("Einmal anzeigen.", 1), complete.Id, 50, 0, Started.AddMinutes(3)));
        Assert.Equal("Einmal anzeigen.", complete.Detail);
    }

    [Fact]
    public async Task SteeringClosesOnlyThePreviousRoundAndSurvivesRepositoryReloadAndReplay()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Umgeleiteter Denkprozess");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "", MessageStatus.Streaming);
        var first = Apply(null, new("Bereits empfangener Gedanke.", 1), 1, id: "reasoning-main-1");
        await chats.SaveToolStepAsync(message.Id, first);
        var steered = Apply(first, new("", 1, State: "steered"), 2, 99, "reasoning-main-1");
        await chats.SaveToolStepAsync(message.Id, steered);
        await chats.SaveToolStepAsync(message.Id, first);
        var next = Apply(null, new("Ich bearbeite den neuen Auftrag.", 2), 3, id: "reasoning-main-2");
        await chats.SaveToolStepAsync(message.Id, next);

        var stored = (await chats.GetMessageAsync(message.Id))!;
        var storedSteps = Assert.IsAssignableFrom<IReadOnlyList<AssistantToolStep>>(stored.ToolSteps);
        var previousRound = storedSteps.Single(step => step.Id == first.Id);
        Assert.Equal("interrupted", previousRound.Status);
        Assert.Equal(first.Detail, previousRound.Detail);
        Assert.Equal(first.ContentOffset, previousRound.ContentOffset);
        Assert.NotNull(previousRound.CompletedAt);
        using var metadata = JsonDocument.Parse(previousRound.OutputJson!);
        Assert.Equal("steered", metadata.RootElement.GetProperty("state").GetString());
        Assert.Equal(2, metadata.RootElement.GetProperty("lastEventId").GetInt64());
        Assert.Equal("running", storedSteps.Single(step => step.Id == next.Id).Status);
        Assert.Equal(MessageStatus.Streaming, stored.Status);
        Assert.Null(GoAiAssistantService.ApplyReasoningDelta(previousRound, new(first.Detail!, 1),
            first.Id, 1, 0, Started.AddMinutes(1)));
    }

    [Fact]
    public void EmptyCompletionDoesNotCreateAnEmptyReasoningCard()
    {
        Assert.Null(GoAiAssistantService.ApplyReasoningDelta(null, new("", 1, State: "completed"),
            "reasoning-1", 10, 0, Started));
    }

    [Fact]
    public async Task NewProviderRetryCanReplaceAnAlreadyStoredCompletionWithoutReopeningOtherTools()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Reasoning provider retry");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "", MessageStatus.Streaming);
        var finished = Apply(null, new("Verworfene Antwort", 1, State: "completed"), 10);
        await chats.SaveToolStepAsync(message.Id, finished);
        var retry = Apply(finished, new("Korrigierter Versuch", 1, ReplaceFrom: 0, State: "reset"), 11);
        await chats.SaveToolStepAsync(message.Id, retry);
        await chats.SaveToolStepAsync(message.Id, finished);

        var stored = Assert.Single((await chats.GetMessageAsync(message.Id))!.ToolSteps!);
        Assert.Equal("running", stored.Status);
        Assert.Equal("Korrigierter Versuch", stored.Detail);
        Assert.Null(stored.CompletedAt);
        Assert.Equal(finished.StartedAt, stored.StartedAt);
        var realTool = finished with { Tool = "coding.edit" };
        Assert.Equal(realTool, AssistantToolStep.Merge(realTool, retry with { Tool = realTool.Tool }));
    }

    [Fact]
    public async Task ReasoningSurvivesRepositoryReloadAndRemainsOrderedWithActualToolSteps()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Live reasoning persistence");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Ich prüfe die Implementierung.\n", MessageStatus.Streaming);
        var reasoning = Apply(null, new("Ich lese die relevante Stelle.\n", 1), 1, id: "reasoning-main-1");
        await chats.SaveToolStepAsync(message.Id, reasoning);
        var tool = new AssistantToolStep("read-1", "coding.read", "completed", "Datei gelesen", ContentOffset: message.Content.Length,
            InputJson: "{\"path\":\"main.py\"}", OutputJson: "{\"sha256\":\"verified-source\"}",
            StartedAt: Started.AddSeconds(2), UpdatedAt: Started.AddSeconds(2));
        await chats.SaveToolStepAsync(message.Id, tool);
        var complete = Apply(reasoning, new("Die Bedingung ist falsch.", 1, State: "completed"), 3, 50, "reasoning-main-1");
        await chats.SaveToolStepAsync(message.Id, complete);
        var compaction = Apply(null, new("Bestätigte Befunde erhalten.", 2, Phase: "compaction"), 4, message.Content.Length,
            "reasoning-compaction-2");
        await chats.SaveToolStepAsync(message.Id, compaction);

        var reloaded = (await chats.GetMessageAsync(message.Id))!;
        Assert.Equal(message.Content, reloaded.Content);
        Assert.Equal([complete, tool, compaction], reloaded.ToolSteps);
        var snapshot = (await environment.Get<IConversationSnapshotRepository>().GetAsync(session.Id))!;
        Assert.Equal(reloaded.ToolSteps, Assert.Single(snapshot.Messages).ToolSteps);
        Assert.Null(GoAiAssistantService.ApplyReasoningDelta(reloaded.ToolSteps![0], new("Die Bedingung ist falsch.", 1),
            complete.Id, 3, 50, Started.AddMinutes(5)));
        using var compactionMetadata = JsonDocument.Parse(reloaded.ToolSteps[2].InputJson!);
        Assert.Equal(2, compactionMetadata.RootElement.GetProperty("round").GetInt32());
        Assert.Equal("compaction", compactionMetadata.RootElement.GetProperty("phase").GetString());
    }

    [Fact]
    public void StoredReasoningIsExcludedFromFutureModelHistoryWithoutCrowdingOutToolEvidence()
    {
        const string privateDraft = "UNSPOKEN_REASONING_SENTINEL";
        var reasoning = Enumerable.Range(1, 60).Select(index => new AssistantToolStep("reasoning-" + index,
            "assistant.reasoning", "completed", privateDraft, InputJson: "{\"round\":" + index + "}",
            OutputJson: "{\"lastEventId\":" + index + ",\"text\":\"" + privateDraft + "\"}"));
        var tool = new AssistantToolStep("edit", "coding.edit", "completed", "Korrektur angewendet", InputJson: "{\"path\":\"main.py\"}",
            OutputJson: "{\"applied\":true,\"sha256\":\"accepted-current-hash\"}");
        var message = Message("Die Änderung wurde geprüft.") with
        {
            Status = MessageStatus.Completed,
            ToolSteps = new[] { tool }.Concat(reasoning).ToArray(),
        };

        var history = GoAiAssistantService.BuildCodingHistoryMessages([message], 100_000);
        var text = Assert.Single(Assert.Single(history).Content).Text!;
        Assert.Contains(message.Content, text);
        Assert.Contains("accepted-current-hash", text);
        Assert.DoesNotContain(privateDraft, text);
        Assert.DoesNotContain("assistant.reasoning", text);
        Assert.DoesNotContain("reasoning-", text);
    }

    [Fact]
    public async Task StartupCleanupPreservesCancelledReasoningWithoutAnAnswerAndItsReplayCursor()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Cancelled reasoning survives restart");
        var reasoningOnly = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "", MessageStatus.Cancelled);
        var trulyEmpty = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "", MessageStatus.Failed);
        var running = Apply(null, new("Bisherige Überlegung vor dem Abbruch.", 1), 25);
        var stopped = GoAiAssistantService.CompleteOpenToolStep(running, "cancelled", null);
        Assert.Equal(running.Detail, stopped.Detail);
        Assert.Equal(running.InputJson, stopped.InputJson);
        Assert.Equal(running.OutputJson, stopped.OutputJson);
        await chats.SaveToolStepAsync(reasoningOnly.Id, stopped);

        Assert.Equal(1, await chats.DeleteEmptyTerminalMessagesAsync());
        Assert.Null(await chats.GetMessageAsync(trulyEmpty.Id));
        var stored = (await chats.GetMessageAsync(reasoningOnly.Id))!;
        Assert.Equal("", stored.Content);
        Assert.Equal(MessageStatus.Cancelled, stored.Status);
        Assert.Equal(stopped, Assert.Single(stored.ToolSteps!));
        Assert.Null(GoAiAssistantService.ApplyReasoningDelta(stored.ToolSteps![0], new("Bisherige Überlegung vor dem Abbruch.", 1),
            stopped.Id, 25, 0, Started.AddMinutes(10)));
    }

    [Fact]
    public async Task LiveReasoningIsNeverSpokenWhileActualAssistantTextKeepsStreaming()
    {
        const string reasoningText = "Dieser Denktext darf keinesfalls vorgelesen werden. ";
        const string first = "Die eigentliche Antwort wird vorgelesen. ";
        const string tail = "Auch der letzte Antwortabschnitt";
        var initial = Message();
        var spoken = new ConcurrentQueue<string>();
        using var firstSpoken = new SemaphoreSlim(0);
        using var speech = new SpeechStreamingSession(initial, (text, _) =>
        {
            spoken.Enqueue(text);
            firstSpoken.Release();
            return Task.CompletedTask;
        });

        var reasoning = Apply(null, new(reasoningText, 1), 1);
        var message = initial with { ToolSteps = [reasoning] };
        speech.Observe(new(GoAiAssistantUpdateKind.Status, message, ToolStep: reasoning));
        speech.Observe(new(GoAiAssistantUpdateKind.Delta, message));
        Assert.Empty(spoken);
        message = message with { Content = first };
        speech.Observe(new(GoAiAssistantUpdateKind.Delta, message));
        Assert.True(await firstSpoken.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(speech.Completion.IsCompleted);

        var completedReasoning = Apply(reasoning, new(reasoningText, 1, State: "completed"), 2);
        message = message with { ToolSteps = [completedReasoning] };
        speech.Observe(new(GoAiAssistantUpdateKind.Status, message, ToolStep: completedReasoning));
        speech.Observe(new(GoAiAssistantUpdateKind.Completed, message with { Content = first + tail, Status = MessageStatus.Completed }));
        await speech.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([first.Trim(), tail], spoken.ToArray());
        Assert.DoesNotContain(spoken, text => text.Contains(reasoningText, StringComparison.Ordinal));
        Assert.Null(speech.Failure);
    }

    [Fact]
    public async Task ReasoningUpdatesDoNotFlushAnUnfinishedAnswerSentenceIntoSpeech()
    {
        var initial = Message();
        var spoken = new ConcurrentQueue<string>();
        using var speech = new SpeechStreamingSession(initial, (text, _) =>
        {
            spoken.Enqueue(text);
            return Task.CompletedTask;
        });
        var message = initial with { Content = "Version 3." };
        speech.Observe(new(GoAiAssistantUpdateKind.Delta, message));
        var reasoning = Apply(null, new("Die Versionsnummer noch vervollständigen.", 1), 1);
        speech.Observe(new(GoAiAssistantUpdateKind.Status, message with { ToolSteps = [reasoning] }, ToolStep: reasoning));
        speech.Observe(new(GoAiAssistantUpdateKind.Completed,
            message with { Content = "Version 3.14 bleibt erhalten.", Status = MessageStatus.Completed, ToolSteps = [reasoning] }));
        await speech.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Version drei Komma eins vier bleibt erhalten.", Assert.Single(spoken));
        Assert.Null(speech.Failure);
    }

    private static AssistantToolStep Apply(AssistantToolStep? previous, ReasoningDeltaEvent update, long eventId,
        int contentOffset = 0, string id = "reasoning-main") => Assert.IsType<AssistantToolStep>(GoAiAssistantService.ApplyReasoningDelta(
            previous, update, id, eventId, contentOffset, Started.AddSeconds(eventId)));

    private static ChatMessage Message(string content = "") => new(Guid.NewGuid(), Guid.NewGuid(), ChatRole.Assistant,
        content, MessageStatus.Streaming, Started, Started);
}
