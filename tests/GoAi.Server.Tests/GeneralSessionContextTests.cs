using GoAi.Contracts;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runs;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class GeneralSessionContextTests
{
    [Theory]
    [InlineData("", "  Ich prüfe gerade die Kennung.\n")]
    [InlineData("Die Kennung", "Ich habe die Notizen geprüft.")]
    public async Task InterruptedNativeReasoningIsRetainedWithoutInventingAVisibleAssistantReply(string content, string reasoning)
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var request = Request();
        var old = (await repository.CreateAsync(request, null)).Snapshot.RunId;
        var native = ModelRuntimeClient.PrepareLanguageBoundMessages(RunProcessor.CreateInitialMessages(request, "general", []));
        await repository.SaveCheckpointAsync(old, new(native, 0, 0, 0, 0, VisibleTextLength: 0,
            StreamingTurnStartEventId: 0, PreserveSessionPromptPrefix: true));
        await repository.AppendEventAsync(old, RunProcessor.InterruptedTurnEventType,
            new { turnStartEventId = 0, content, reasoningContent = reasoning });
        await repository.CancelAsync(old);
        var next = request with { Messages = [.. request.Messages, new("user", [new("text", "Antworte jetzt kurz.")])] };
        var run = (await repository.CreateAsync(next, null)).Snapshot.RunId;
        var snapshot = await repository.GetGeneralSessionContextAsync(run, next);
        Assert.NotNull(snapshot);
        Assert.Empty(snapshot.VisibleResponse);
        Assert.True(GeneralSessionContext.TryContinue(snapshot, next, RunProcessor.CreateInitialMessages(next, "general", []), out var continued));
        var assistant = Assert.Single(continued, message => message.Role == "assistant");
        Assert.Equal(content, assistant.Content);
        Assert.Equal(reasoning, assistant.ReasoningContent);
        Assert.Null(assistant.ToolCalls);
        var interrupted = Assert.Single(continued, message => message.Content?.StartsWith("GO_INTERRUPTED_MODEL_TURN", StringComparison.Ordinal) == true);
        Assert.Equal("user", interrupted.Role);
        Assert.Contains("keinen erfolgreichen Werkzeugaufruf", interrupted.Content, StringComparison.Ordinal);
        Assert.Equal(native, ModelRuntimeClient.PrepareLanguageBoundMessages(continued).Take(native.Count));
    }

    [Fact]
    public async Task InterruptedTailFromAnotherTurnCannotBeSplicedIntoCurrentCheckpoint()
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var request = Request();
        var old = (await repository.CreateAsync(request, null)).Snapshot.RunId;
        var native = ModelRuntimeClient.PrepareLanguageBoundMessages(RunProcessor.CreateInitialMessages(request, "general", []));
        await repository.SaveCheckpointAsync(old, new(native, 0, 0, 0, 0, VisibleTextLength: 0,
            StreamingTurnStartEventId: 0, PreserveSessionPromptPrefix: true));
        await repository.AppendEventAsync(old, RunProcessor.InterruptedTurnEventType,
            new { turnStartEventId = 99, content = "Falscher Turn", reasoningContent = "Fremder Zwischenstand" });
        await repository.CancelAsync(old);
        var next = request with { Messages = [.. request.Messages, new("user", [new("text", "Weiter.")])] };
        var run = (await repository.CreateAsync(next, null)).Snapshot.RunId;
        var snapshot = await repository.GetGeneralSessionContextAsync(run, next);
        Assert.NotNull(snapshot);
        Assert.Equal(native, snapshot.Checkpoint.Messages);
    }

    [Theory]
    [InlineData(RunState.Cancelled)]
    [InlineData(RunState.Failed)]
    [InlineData(RunState.Interrupted)]
    public async Task InterruptedGeneralRunWithoutVisibleAnswerRetainsItsExactNativeInput(RunState state)
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var request = Request();
        var old = (await repository.CreateAsync(request, null)).Snapshot.RunId;
        var native = ModelRuntimeClient.PrepareLanguageBoundMessages(RunProcessor.CreateInitialMessages(request, "general", []));
        await repository.SaveCheckpointAsync(old, new(native, 0, 0, 0, 0, PreserveSessionPromptPrefix: true));
        await repository.UpdateStateAsync(old, state);
        var next = request with { Messages = [.. request.Messages, new("user", [new("text", "Weiter, aber antworte kurz.")])] };
        var run = (await repository.CreateAsync(next, null)).Snapshot.RunId;
        var snapshot = await new RunRepository(context.Database, new RunEventNotifier()).GetGeneralSessionContextAsync(run, next);
        Assert.NotNull(snapshot);
        Assert.True(snapshot.WasInterrupted);
        Assert.Empty(snapshot.VisibleResponse);
        Assert.True(GeneralSessionContext.TryContinue(snapshot, next, RunProcessor.CreateInitialMessages(next, "general", []), out var continued));
        var prepared = ModelRuntimeClient.PrepareLanguageBoundMessages(continued);
        Assert.Equal(native, prepared.Take(native.Count));
        Assert.DoesNotContain(prepared, message => message.Role == "assistant");
    }

    [Fact]
    public void InterruptedToolIsClosedAsUnknownAndCannotBeReplayedAsAPendingAction()
    {
        var request = Request();
        var tool = new LmToolCall("pending-write", "workspace.write", JsonSerializer.SerializeToElement(new { path = "file.txt" }));
        var native = ModelRuntimeClient.PrepareLanguageBoundMessages(RunProcessor.CreateInitialMessages(request, "general", []));
        var checkpoint = new AgentRunCheckpoint([.. native, new("assistant", null, ToolCalls: [tool])],
            1, 1, 100, 10, ActiveToolCalls: [tool], PendingProposalId: "proposal", PreserveSessionPromptPrefix: true);
        var snapshot = new GeneralSessionContextSnapshot(checkpoint, request, "", WasInterrupted: true);
        var next = request with { Messages = [.. request.Messages, new("user", [new("text", "Prüfe das Ergebnis.")])] };
        Assert.True(GeneralSessionContext.TryContinue(snapshot, next, RunProcessor.CreateInitialMessages(next, "general", []), out var continued));
        var receipt = Assert.Single(continued, message => message.Role == "tool");
        Assert.Equal(tool.Id, receipt.ToolCallId);
        Assert.True(JsonDocument.Parse(receipt.Content!).RootElement.GetProperty("outcomeUnknown").GetBoolean());
        Assert.Equal(native, ModelRuntimeClient.PrepareLanguageBoundMessages(continued).Take(native.Count));
    }

    [Fact]
    public async Task InterruptedPublishedTextIsRestoredOnceAfterCheckpointBoundary()
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var request = Request();
        var old = (await repository.CreateAsync(request, null)).Snapshot.RunId;
        var native = ModelRuntimeClient.PrepareLanguageBoundMessages(RunProcessor.CreateInitialMessages(request, "general", []));
        await repository.SaveCheckpointAsync(old, new(native, 0, 0, 0, 0, VisibleTextLength: 0,
            StreamingTurnStartEventId: 0, PreserveSessionPromptPrefix: true));
        await repository.AppendEventAsync(old, RunEventTypes.TextDelta, new TextDeltaEvent("Gespeichert."));
        await repository.CancelAsync(old);
        var next = FollowUp(request);
        var run = (await repository.CreateAsync(next, null)).Snapshot.RunId;
        var snapshot = await repository.GetGeneralSessionContextAsync(run, next);
        Assert.NotNull(snapshot);
        Assert.True(GeneralSessionContext.TryContinue(snapshot, next, RunProcessor.CreateInitialMessages(next, "general", []), out var continued));
        Assert.Equal("Gespeichert.", Assert.Single(continued, message => message.Role == "assistant").Content);
        Assert.Equal(native, ModelRuntimeClient.PrepareLanguageBoundMessages(continued).Take(native.Count));
        var edited = next with { Messages = [next.Messages[0], new("assistant", [new("text", "Geändert.")]), next.Messages[^1]] };
        Assert.False(GeneralSessionContext.TryContinue(snapshot, edited, RunProcessor.CreateInitialMessages(edited, "general", []), out _));
    }

    private static RunRequest Request() => new(GoAiProtocol.Version, RunMode.General,
        [new("user", [new("text", "Merke dir den Projektnamen Eiche.")])], SessionId: "session-a");

    private static RunRequest FollowUp(RunRequest request) => request with
    {
        Messages = [.. request.Messages, new("assistant", [new("text", "Gespeichert.")]),
            new("user", [new("text", "Wie heißt das Projekt?")])],
    };

    private static GeneralSessionContextSnapshot Snapshot(RunRequest request)
    {
        var native = ModelRuntimeClient.PrepareLanguageBoundMessages(RunProcessor.CreateInitialMessages(request, "general", []));
        return new(new([.. native, new("assistant", "Gespeichert.", ReasoningContent: "Der Projektname ist eindeutig.")],
            1, 0, 100, 10, PreserveSessionPromptPrefix: true), request, "Gespeichert.");
    }

    [Theory]
    [InlineData(RunMode.General, "model-one")]
    [InlineData(RunMode.General, "model-two")]
    [InlineData(RunMode.Auto, "model-two")]
    public void ExactVisibleHistoryPreservesNativePrefixAcrossModelSelectionAndProcessSerialization(RunMode mode, string model)
    {
        var old = Request();
        var snapshot = Snapshot(old);
        // Simulate persisted checkpoint/request recovery without shared objects.
        snapshot = JsonSerializer.Deserialize<GeneralSessionContextSnapshot>(JsonSerializer.Serialize(snapshot))!;
        var next = FollowUp(old) with { Mode = mode, PreferredGeneralModelId = model, ReasoningEffort = "high" };
        var initial = RunProcessor.CreateInitialMessages(next, "general", []);
        Assert.True(GeneralSessionContext.TryContinue(snapshot, next, initial, out var messages));
        var native = ModelRuntimeClient.PrepareLanguageBoundMessages(messages);
        Assert.Equal(snapshot.Checkpoint.Messages, native.Take(snapshot.Checkpoint.Messages.Count));
        Assert.Equal("Der Projektname ist eindeutig.", native.First(m => m.Role == "assistant").ReasoningContent);
        Assert.Equal("Wie heißt das Projekt?", messages[^1].Content);
    }

    [Theory]
    [InlineData("edited")]
    [InlineData("response")]
    [InlineData("deleted")]
    [InlineData("session")]
    [InlineData("tools")]
    [InlineData("documents")]
    [InlineData("uploads")]
    [InlineData("profile")]
    [InlineData("compacted")]
    public void ChangedClientHistoryOrScopeFallsBackWithoutRestoringHiddenContext(string change)
    {
        var old = Request();
        var next = FollowUp(old);
        next = change switch
        {
            "edited" => next with { Messages = [new("user", [new("text", "Ein anderer Auftrag.")]), .. next.Messages.Skip(1)] },
            "response" => next with { Messages = [next.Messages[0], new("assistant", [new("text", "Bearbeitet.")]), next.Messages[^1]] },
            "deleted" => next with { Messages = [next.Messages[^1]] },
            "session" => next with { SessionId = "session-b" },
            "tools" => next with { AllowedServerTools = ["web.search"] },
            "documents" => next with { DocumentContext = new(DocumentContextMode.Full, "revision", 1, 1, 20, 1) },
            "uploads" => next with { UploadIds = ["new-upload"] },
            "profile" => next with { ConversationProfile = ConversationProfile.ContextPreparation },
            "compacted" => next with { SessionContext = new("revision", 20, 3, 10, PreparedByAi: true) },
            _ => next,
        };
        var initial = RunProcessor.CreateInitialMessages(next, "general", []);
        Assert.False(GeneralSessionContext.TryContinue(Snapshot(old), next, initial, out var messages));
        Assert.Same(initial, messages);
    }

    [Fact]
    public void CurrentSystemPolicyReplacesHistoricalPolicyWhileToolResultsStayChronological()
    {
        var old = Request();
        var snapshot = Snapshot(old);
        var tool = new LmToolCall("old-call", "web.search", JsonSerializer.SerializeToElement(new { query = "Eiche" }));
        snapshot = snapshot with { Checkpoint = snapshot.Checkpoint with
        {
            Messages = [.. snapshot.Checkpoint.Messages.SkipLast(1), new("assistant", null, [tool]),
                new("tool", "Historische Fundstelle", ToolCallId: tool.Id), snapshot.Checkpoint.Messages[^1]],
        } };
        var next = FollowUp(old);
        var initial = RunProcessor.CreateInitialMessages(next, "general", []);
        initial[0] = new("system", "Aktuelle verbindliche Policy");
        Assert.True(GeneralSessionContext.TryContinue(snapshot, next, initial, out var messages));
        Assert.Equal("Aktuelle verbindliche Policy", Assert.Single(messages, m => m.Role == "system").Content);
        Assert.Contains(messages, m => m.Role == "tool" && m.Content == "Historische Fundstelle");
    }

    [Fact]
    public async Task CompletedGeneralHistorySurvivesCheckpointDeletionAndRepositoryRestartButIsSessionIsolated()
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var request = Request();
        var old = (await repository.CreateAsync(request, null)).Snapshot.RunId;
        await repository.SaveCheckpointAsync(old, Snapshot(request).Checkpoint);
        await repository.AppendEventAsync(old, RunEventTypes.TextDelta, new TextDeltaEvent("GespeicX"));
        await repository.AppendEventAsync(old, RunEventTypes.TextDelta, new TextDeltaEvent("hert.", ReplaceFrom: 7));
        Assert.True(await repository.FinalizeConversationAsync(old, new(null, "model-one", 100, 10)));
        Assert.Null(await repository.GetCheckpointAsync(old));
        var next = FollowUp(request);
        var run = (await repository.CreateAsync(next, null)).Snapshot.RunId;
        var reopened = new RunRepository(context.Database, new RunEventNotifier());
        var snapshot = await reopened.GetGeneralSessionContextAsync(run, next);
        Assert.NotNull(snapshot);
        Assert.Equal("Gespeichert.", snapshot.VisibleResponse);
        Assert.True(GeneralSessionContext.TryContinue(snapshot, next, RunProcessor.CreateInitialMessages(next, "general", []), out _));
        Assert.Null(await reopened.GetGeneralSessionContextAsync(run, next with { SessionId = "other" }));
        Assert.Null(await reopened.GetGeneralSessionContextAsync(run, next with { Mode = RunMode.Coding }));
    }
}
