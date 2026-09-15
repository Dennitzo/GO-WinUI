using GoAi.Server.Core.Coding;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runs;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class CodingSessionContextTests
{
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
