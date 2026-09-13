using System.Text.Json;
using GoWinUI.App.Services;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;

namespace GoWinUI.Tests;

public sealed class CodingHistoryRecoveryTests
{
    [Theory]
    [InlineData(MessageStatus.Cancelled)]
    [InlineData(MessageStatus.Failed)]
    [InlineData(MessageStatus.Interrupted)]
    public void UnfinishedRunRetainsFindingsAndCommittedReceipt(MessageStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        var message = new ChatMessage(Guid.NewGuid(), Guid.NewGuid(), ChatRole.Assistant,
            "Compiler-Aufbau dauert 92 Sekunden. Nächster Schritt: Cache prüfen.", status, now, now,
            ToolSteps: [new("edit-1", "coding.edit", "completed", InputJson: "{\"path\":\"tools/probe.py\"}",
                OutputJson: "{\"path\":\"tools/probe.py\",\"applied\":true,\"sha256\":\"current-hash\"}")]);
        var result = GoAiAssistantService.BuildCodingHistoryMessages([message], 100_000);
        var text = Assert.Single(Assert.Single(result).Content).Text!;
        Assert.Contains("92 Sekunden", text);
        Assert.Contains("tools/probe.py", text);
        Assert.Contains("current-hash", text);
        Assert.Contains("nicht erfolgreich abgeschlossen", text);
        Assert.Equal("assistant", result[0].Role);
    }

    [Fact]
    public void ToolOnlyHistorySurvivesAndBudgetDoesNotDropTheNewestOversizedMessage()
    {
        var now = DateTimeOffset.UtcNow;
        var message = new ChatMessage(Guid.NewGuid(), Guid.NewGuid(), ChatRole.Assistant, "", MessageStatus.Failed, now, now,
            ToolSteps: [new("cmd", "coding.command", "failed", OutputJson: "{\"exitCode\":1,\"stderr\":\"Missing interpreter\"}")]);
        Assert.Contains("Missing interpreter", GoAiAssistantService.BuildCodingHistoryMessages([message], 5000)[0].Content[0].Text);
        var bounded = GoAiAssistantService.BuildCodingHistoryMessages([message with { Content = new string('x', 50_000) }], 2000);
        Assert.InRange(bounded.Sum(item => item.Content.Sum(part => part.Text?.Length ?? 0)), 1, 2000);
        Assert.Empty(GoAiAssistantService.BuildCodingHistoryMessages([message with { Status = MessageStatus.Streaming }], 5000));
    }

    [Fact]
    public async Task MultiTermRecallFindsFailedHistoryAndExcludesCurrentTurn()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("History recovery");
        var previous = await chats.AddMessageAsync(session.Id, ChatRole.Assistant,
            "Wikipedia ist importiert; start.ps1 wurde untersucht.", MessageStatus.Failed);
        var turn = await chats.AddTurnAsync(session.Id, "Wikipedia CPU Kerne Schwingung");
        var result = JsonSerializer.SerializeToElement(await CodingSessionTools.SearchHistoryAsync(chats, session.Id,
            turn.AssistantMessage.Id, "start.ps1 Wikipedia CPU Kerne", 8, CancellationToken.None), JsonSerializerOptions.Web);
        var match = Assert.Single(result.GetProperty("matches").EnumerateArray());
        Assert.Equal(previous.Id, match.GetProperty("messageId").GetGuid());
        Assert.Equal("failed", match.GetProperty("status").GetString());
    }
}
