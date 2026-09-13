using GoAi.Server.Core.Coding;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runs;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class CodingToolReceiptTests
{
    [Fact]
    public void BoundedFailureRetainsOutcomeAndArchiveReferenceWithoutWorkingState()
    {
        var evidenceId = "ev-" + new string('a', 32);
        var raw = JsonSerializer.Serialize(new
        {
            status = "failed", errorCode = "client.coding_tool_failed",
            result = new { success = false, exitCode = 7, stdout = new string('x', 11950), stderr = "actual failure",
                evidence = new { evidenceId, tool = "coding.command", storedBytes = 12050, truncated = false } },
        });
        Assert.True(raw.Length > CodingLoopGuard.MaximumToolResultCharacters);
        var bounded = CodingLoopGuard.BoundToolResult(raw);
        var receipt = JsonSerializer.Deserialize<JsonElement>(bounded);
        Assert.True(bounded.Length <= CodingLoopGuard.MaximumToolResultCharacters);
        Assert.Equal("failed", receipt.GetProperty("status").GetString());
        Assert.False(receipt.GetProperty("result").GetProperty("success").GetBoolean());
        Assert.Equal(7, receipt.GetProperty("result").GetProperty("exitCode").GetInt32());
        Assert.Equal(evidenceId, receipt.GetProperty("result").GetProperty("evidence").GetProperty("evidenceId").GetString());
        var first = new LmToolCall("first", "coding.command", JsonSerializer.SerializeToElement(new { executable = "python", arguments = "diagnose.py" }));
        var second = first with { Id = "second" };
        LmChatMessage[] messages = [new("assistant", ToolCalls: [first]), new("tool", bounded, ToolCallId: first.Id),
            new("assistant", ToolCalls: [second]), new("tool", bounded, ToolCallId: second.Id)];
        Assert.Throws<AgentRunLimitException>(() => CodingLoopGuard.ThrowIfRepeatedFailure(messages, first with { Id = "third" }));
        Assert.Equal(11950, JsonSerializer.Deserialize<JsonElement>(raw).GetProperty("result").GetProperty("stdout").GetString()!.Length);
    }

    [Fact]
    public void UnicodeOutputPageKeepsRealPagingAndDoesNotInventExecutionSuccess()
    {
        var evidenceId = "ev-" + new string('b', 32);
        var raw = JsonSerializer.Serialize(new { status = "completed", result = new
        {
            evidenceId, stream = "stdout", offset = 4000, nextOffset = 12000, hasMore = true,
            storedCharacters = 90000, truncated = false, text = new string('界', 8000),
        } });
        var bounded = CodingLoopGuard.BoundToolResult(raw);
        var result = JsonSerializer.Deserialize<JsonElement>(bounded).GetProperty("result");
        Assert.True(bounded.Length <= CodingLoopGuard.MaximumToolResultCharacters);
        Assert.Equal(evidenceId, result.GetProperty("evidenceId").GetString());
        Assert.Equal(4000, result.GetProperty("offset").GetInt32());
        Assert.Equal(12000, result.GetProperty("nextOffset").GetInt32());
        Assert.True(result.GetProperty("hasMore").GetBoolean());
        Assert.False(result.GetProperty("truncated").GetBoolean());
        Assert.False(result.TryGetProperty("success", out _));
        Assert.False(result.TryGetProperty("exitCode", out _));
    }

    [Fact]
    public void AppliedFileReceiptKeepsExactHashAndUnknownOutcomeAcrossUnicodeBudget()
    {
        var hash = new string('c', 64);
        var raw = JsonSerializer.Serialize(new { status = "failed", outcomeUnknown = true,
            result = new { applied = true, path = "source.cs", sha256 = hash, diff = new string('\u0001', 10000) } });
        var bounded = CodingLoopGuard.BoundToolResult(raw);
        var receipt = JsonSerializer.Deserialize<JsonElement>(bounded);
        Assert.True(bounded.Length <= CodingLoopGuard.MaximumToolResultCharacters);
        Assert.True(receipt.GetProperty("outcomeUnknown").GetBoolean());
        Assert.True(receipt.GetProperty("result").GetProperty("applied").GetBoolean());
        Assert.Equal(hash, receipt.GetProperty("result").GetProperty("sha256").GetString());
        Assert.Equal("source.cs", receipt.GetProperty("result").GetProperty("path").GetString());
    }

    [Fact]
    public void OverlargeMetadataIsMarkedIncompleteAndSmallReceiptRemainsExact()
    {
        const string small = "{\"status\":\"failed\",\"result\":{\"exitCode\":7}}";
        Assert.Equal(small, CodingLoopGuard.BoundToolResult(small));
        var bounded = CodingLoopGuard.BoundToolResult(JsonSerializer.Serialize(new { status = "failed",
            result = new { exitCode = 7, path = new string('界', 20000), stdout = new string('x', 14000) } }));
        var result = JsonSerializer.Deserialize<JsonElement>(bounded);
        Assert.True(bounded.Length <= CodingLoopGuard.MaximumToolResultCharacters);
        Assert.True(result.GetProperty("metadataTruncated").GetBoolean());
        Assert.Equal("failed", result.GetProperty("status").GetString());
        Assert.Equal(7, result.GetProperty("result").GetProperty("exitCode").GetInt32());
        Assert.False(result.GetProperty("result").TryGetProperty("path", out _));
    }
}
