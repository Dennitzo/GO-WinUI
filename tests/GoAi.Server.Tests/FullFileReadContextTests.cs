using GoAi.Server.Core.Coding;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runs;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class FullFileReadContextTests
{
    private static LmChatMessage[] Messages(int length) =>
    [
        new("system", "Policy"), new("user", "Read the file"),
        new("assistant", new string('h', 100_000)),
        new("assistant", "Read", ToolCalls: [new LmToolCall("read", "coding.read", JsonSerializer.SerializeToElement(new { path = "code.cs" }))]),
        new("tool", new string('f', length), ToolCallId: "read"),
    ];

    [Fact]
    public void PlannerCompactsOlderHistoryButPreservesFreshRead()
    {
        var messages = Messages(30_000);
        var plan = ContextPlanner.Prepare(messages, 32768, null);
        Assert.Equal(messages[^1].Content, plan.Messages[^1].Content);
        Assert.True(plan.WasCompacted);
    }

    [Fact]
    public void FreshReadTooLargeFailsExplicitlyInsteadOfTruncating()
    {
        var messages = Messages(120_000);
        var error = Assert.Throws<CodingReadContextBudgetException>(() => ContextPlanner.Prepare(messages, 32768, null));
        Assert.Contains("nicht gekürzt", error.Message);
        Assert.Equal(120_000, messages[^1].Content!.Length);
    }

    [Fact]
    public void SemanticCompactionKeepsFreshFileOutOfSummary()
    {
        var messages = Messages(60_000);
        var plan = CodingContextCompactor.Plan(messages, 32768);
        Assert.NotNull(plan);
        Assert.Contains(plan.RecentMessages, message => message.ToolCallId == "read" && message.Content == messages[^1].Content);
        var complete = CodingContextCompactor.Complete(plan, "Earlier work summarized");
        Assert.Equal(messages[^1].Content, complete[^1].Content);
        Assert.DoesNotContain(new string('f', 500), string.Join("", plan.SummaryRequest.Select(m => m.Content)));
    }
}
