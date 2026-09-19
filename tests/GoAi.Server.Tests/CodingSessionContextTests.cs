using GoAi.Server.Core.Coding;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runs;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class CodingSessionContextTests
{
    [Fact]
    public void CompletedNativePromptIsAnUnchangedPrefixOfRepeatedFollowUps()
    {
        var state = CodingWorkingState.Create("Inspect project");
        var initial = new[] { new LmChatMessage("system", "Current policy"), new LmChatMessage("user", "Same request") };
        var effective = ModelRuntimeClient.PrepareLanguageBoundMessages(RunProcessor.WithWorkingState(
            [.. initial, new("system", CodingCompletionGuard.RepairPrompt), new("system", RunProcessor.EmptyResponseRepairPrompt)], state));
        var checkpoint = new AgentRunCheckpoint([.. effective, new("assistant", "Verified result", ReasoningContent: "Belegte Analyse")],
            1, 0, 0, 0, WorkingState: state, PreserveSessionPromptPrefix: true);
        for (var turn = 0; turn < 3; turn++)
        {
            var continued = CodingSessionContext.Continue(checkpoint, initial);
            var prepared = ContextPlanner.Prepare(RunProcessor.WithWorkingState(continued, state), 32768,
                null, preserveConversationPrefix: true);
            var native = ModelRuntimeClient.PrepareLanguageBoundMessages(prepared.Messages);
            Assert.Equal(checkpoint.Messages, native.Take(checkpoint.Messages.Count));
            Assert.False(prepared.WasCompacted);
            Assert.Equal(turn + 2, native.Count(m => m.Role == "user" && m.Content == "Same request"));
            Assert.Equal("Belegte Analyse", native.First(m => m.Role == "assistant").ReasoningContent);
            checkpoint = checkpoint with { Messages = [.. native, new("assistant", "Next result")] };
        }
    }

    [Fact]
    public void ReasoningInPreservedNativeHistoryCountsTowardCompactionBudget()
    {
        LmChatMessage[] messages = [new("system", "Policy"), new("user", "Previous task"),
            new("assistant", "Done", ReasoningContent: new string('x', 100_000)),
            new("user", "Current task"), new("user", "GO-Laufanweisung zur Sprache: Deutsch")];
        Assert.True(ContextPlanner.EstimateTokens(messages) > 32768);
        var plan = CodingContextCompactor.Plan(messages, 32768);
        Assert.NotNull(plan);
        Assert.Equal("Current task", plan.CurrentRequest.Content);
        var reduced = ContextPlanner.Prepare(messages, 32768, null, preserveConversationPrefix: true);
        Assert.True(reduced.WasCompacted);
        Assert.Contains(reduced.Messages, m => m.Content == "Done");
    }

    [Fact]
    public void LargeLegacyStateIsRecoveredOnceWithoutInflatingTheConversation()
    {
        var state = CodingWorkingState.Create("Previous task") with
        {
            Sequence = 224,
            Evidence = Enumerable.Range(0, 224).Select(i => new CodingToolEvidence("call-" + i,
                "coding.read", true, "source.cs", null, null, new string('x', 1600), i)).ToArray(),
            Facts = [new("Verified finding", ["call-1"])],
        };
        var legacy = new LmChatMessage("user", CodingSessionContext.StateMarker + "Historical data\n" + JsonSerializer.Serialize(state));
        var receipt = new LmChatMessage("assistant", "Verified conclusion");
        var previous = new AgentRunCheckpoint([new("user", "Original task"), receipt, legacy, legacy],
            0, 0, 0, 0, WorkingState: CodingWorkingState.Create("Failed startup"));
        for (var run = 0; run < 5; run++)
        {
            LmChatMessage[] initial = [new("system", "Current policy"), new("user", "Follow-up " + run)];
            var continued = CodingSessionContext.Continue(previous, initial);
            var carried = CodingSessionContext.ContinueWorkingState(previous, "Follow-up " + run)!;
            Assert.Equal(224, carried.Evidence.Count);
            Assert.Equal("Verified finding", Assert.Single(carried.Facts).Text);
            Assert.Equal("Follow-up " + run, carried.OriginalTask);
            Assert.Contains(receipt, continued);
            Assert.True(Assert.Single(continued, m => m.Content?.StartsWith(CodingSessionContext.StateMarker, StringComparison.Ordinal) == true).Content!.Length < 13000);
            Assert.Null(CodingContextCompactor.Plan(continued, 32768, carried));
            previous = previous with { Messages = continued, WorkingState = carried };
        }
    }

    [Fact]
    public void PendingHistoricalCallIsClosedAsUnknownWithoutReplayingOrPromotingInstructions()
    {
        var call = new LmToolCall("old-call", "coding.edit", JsonSerializer.SerializeToElement(new { path = "file" }));
        var old = new AgentRunCheckpoint([new("system", "Obsolete instructions"), new("user", "Old task"),
            new("assistant", null, ToolCalls: [call])], 99, 99, 99, 99, ActiveToolCalls: [call]);
        var result = CodingSessionContext.Continue(old, [new("system", "Current policy"), new("assistant", "Fallback"), new("user", "New task")]);
        Assert.Equal("Current policy", Assert.Single(result, m => m.Role == "system").Content);
        Assert.Equal("New task", result[^1].Content);
        var receipt = Assert.Single(result, m => m.Role == "tool");
        Assert.Equal(call.Id, receipt.ToolCallId);
        Assert.True(JsonDocument.Parse(receipt.Content!).RootElement.GetProperty("outcomeUnknown").GetBoolean());
        Assert.DoesNotContain(result, m => m.Content == "Fallback");
        Assert.Single(old.ActiveToolCalls!);
    }
}
