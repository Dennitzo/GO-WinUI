using GoWinUI.App.Services;
using GoWinUI.Core.Models;

namespace GoWinUI.Tests;

public sealed class StoppedSessionHistoryTests
{
    [Theory]
    [InlineData(MessageStatus.Cancelled)]
    [InlineData(MessageStatus.Interrupted)]
    [InlineData(MessageStatus.Failed)]
    public void GeneralHistoryRetainsRealPartialAssistantTextInChronologicalOrder(MessageStatus status)
    {
        var user = Message(ChatRole.User, "Prüfe mein Projekt.", MessageStatus.Completed, 0);
        var partial = Message(ChatRole.Assistant, "Die erste Datei ist geprüft.", status, 1) with { Error = "Verbindung unterbrochen." };
        var next = Message(ChatRole.User, "Setze die Prüfung fort.", MessageStatus.Completed, 2);
        var selected = SessionContextPreparationService.SelectEligibleHistory([next, partial, user]);
        Assert.Equal([user.Id, partial.Id, next.Id], selected.Select(message => message.Id));
        Assert.Equal(partial.Content, selected[1].Content);
    }

    [Theory]
    [InlineData("Der GO-AI-Auftrag konnte nicht abgeschlossen werden.", "HTTP 503")]
    [InlineData("Der Serverlauf wurde durch einen Serverneustart unterbrochen.", "Der Serverlauf wurde durch einen Serverneustart unterbrochen.")]
    [InlineData("  Projektordner fehlt.  ", "Projektordner fehlt.")]
    public void GeneralHistoryNeverTreatsClientFailurePlaceholdersAsModelAnswers(string content, string error)
    {
        var user = Message(ChatRole.User, "Prüfe mein Projekt.", MessageStatus.Completed, 0);
        var failure = Message(ChatRole.Assistant, content, MessageStatus.Failed, 1) with { Error = error };
        var emptyStopped = Message(ChatRole.Assistant, "  ", MessageStatus.Cancelled, 2);
        var unsubmittedUser = Message(ChatRole.User, "Nicht abgeschickter Auftrag", MessageStatus.Cancelled, 3);
        var next = Message(ChatRole.User, "Versuche es erneut.", MessageStatus.Completed, 4);
        var selected = SessionContextPreparationService.SelectEligibleHistory([user, failure, emptyStopped, unsubmittedUser, next]);
        Assert.Equal([user.Id, next.Id], selected.Select(message => message.Id));
    }

    [Fact]
    public void CompletedAnswerMayLegitimatelyQuoteTheFailurePlaceholder()
    {
        var answer = Message(ChatRole.Assistant, "Der GO-AI-Auftrag konnte nicht abgeschlossen werden.", MessageStatus.Completed, 0);
        Assert.Equal(answer, Assert.Single(SessionContextPreparationService.SelectEligibleHistory([answer])));
    }

    private static ChatMessage Message(ChatRole role, string content, MessageStatus status, int second) => new(
        Guid.NewGuid(), Guid.Empty, role, content, status,
        DateTimeOffset.UnixEpoch.AddSeconds(second), DateTimeOffset.UnixEpoch.AddSeconds(second));
}
