using GoAi.Server.Core.Coding;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runs;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class CodingEvidenceContextTests
{
    [Fact]
    public void CompletedLargeArgumentsShrinkButOpenCallsAndTheirOriginalJournalRemainExact()
    {
        var completed = new LmToolCall("done", "coding.write", JsonSerializer.SerializeToElement(new { path = "done.py", content = new string('x', 32_000) }));
        var pending = new LmToolCall("open", "coding.edit", JsonSerializer.SerializeToElement(new { path = "pending.py", oldText = new string('y', 32_000), newText = "replacement" }));
        var receipt = new LmChatMessage("tool", "{\"success\":true,\"sha256\":\"actual journal receipt\"}", ToolCallId: "done");
        LmChatMessage[] source = [new("user", "Perform the original task"), new("assistant", ToolCalls: [completed, pending]), receipt];
        var result = CodingEvidenceContext.CompactCompletedCalls(source);
        Assert.True(result[1].ToolCalls![0].Arguments.GetRawText().Length < 2000);
        Assert.Equal("done.py", result[1].ToolCalls![0].Arguments.GetProperty("path").GetString());
        Assert.Equal(pending.Arguments.GetRawText(), result[1].ToolCalls![1].Arguments.GetRawText());
        Assert.Equal(32_000, source[1].ToolCalls![0].Arguments.GetProperty("content").GetString()!.Length);
        Assert.Same(receipt, result[2]);
        var context = ContextPlanner.Prepare(source, 32_768, null, allowLossyCompaction: false);
        Assert.Equal(pending.Arguments.GetRawText(), context.Messages[1].ToolCalls![1].Arguments.GetRawText());
        Assert.Equal(result[1].ToolCalls![0].Arguments.GetRawText(), CodingEvidenceContext.CompactCompletedCalls(result)[1].ToolCalls![0].Arguments.GetRawText());
    }

    [Fact]
    public void AReceiptBeforeItsCallDoesNotAuthorizeArgumentCompaction()
    {
        var call = new LmToolCall("same", "coding.command", JsonSerializer.SerializeToElement(new { executable = "pwsh", arguments = new[] { new string('x', 20_000) } }));
        LmChatMessage[] source = [new("tool", "earlier unrelated receipt", ToolCallId: "same"), new("assistant", ToolCalls: [call])];
        Assert.Equal(call.Arguments.GetRawText(), CodingEvidenceContext.CompactCompletedCalls(source)[1].ToolCalls![0].Arguments.GetRawText());
    }

    [Fact]
    public void RollingSummaryPreservesAnOpenLargeToolAndRecognizesTheOriginalTaskBeforeWorkingMemory()
    {
        var task = new LmChatMessage("user", "The authoritative task remains unchanged");
        var messages = new List<LmChatMessage> { new("system", "Trusted policy"), task };
        for (var index = 0; index < 70; index++)
        {
            var call = new LmToolCall("done-" + index, "coding.read", JsonSerializer.SerializeToElement(new { path = "file-" + index }));
            messages.Add(new("assistant", ToolCalls: [call]));
            messages.Add(new("tool", "{\"content\":\"1: read evidence\"}", ToolCallId: call.Id));
        }
        var state = CodingWorkingState.Create(task.Content!);
        messages.Add(CodingEvidenceContext.Build(state));
        var open = new LmToolCall("still-open", "coding.edit", JsonSerializer.SerializeToElement(new { oldText = new string('x', 20_000), newText = "safe" }));
        messages.Add(new("assistant", ToolCalls: [open]));
        var plan = CodingContextCompactor.Plan(messages, 8192, state)!;
        Assert.NotNull(plan);
        Assert.Same(task, plan.CurrentRequest);
        var compacted = CodingContextCompactor.Complete(plan, "Confirmed prior reads. The edit remains pending.");
        Assert.Equal(open.Arguments.GetRawText(), Assert.Single(compacted.SelectMany(static message => message.ToolCalls ?? []), call => call.Id == "still-open").Arguments.GetRawText());
        Assert.DoesNotContain(compacted, message => message.Content?.StartsWith(CodingEvidenceContext.Marker, StringComparison.Ordinal) == true);
        Assert.NotNull(plan.WorkingStateMessage);
        Assert.Contains(plan.SummaryRequest, message => message.Content?.StartsWith(CodingEvidenceContext.Marker, StringComparison.Ordinal) == true);
        Assert.Contains(compacted, message => ReferenceEquals(message, task));
        Assert.Equal("Trusted policy", Assert.Single(compacted, message => message.Role == "system").Content);
    }
}
