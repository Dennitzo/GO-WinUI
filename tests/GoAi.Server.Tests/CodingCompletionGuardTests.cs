using GoAi.Server.Core.Coding;

namespace GoAi.Server.Tests;

public sealed class CodingCompletionGuardTests
{
    [Theory]
    [InlineData("Ich erstelle zunächst einen strukturierten Arbeitsplan für diese mehrstufige Aufgabe, um die verschiedenen Teilschritte nachvollziehbar zu halten.", true)]
    [InlineData("Ich beginne mit einer Bestandsaufnahme im ausgewählten Projektordner.", true)]
    [InlineData("I will inspect the repository and implement the change.", true)]
    [InlineData("Die Änderung ist umgesetzt und geprüft.", false)]
    [InlineData("Ich kann nicht fortfahren, weil der Workspace fehlt.", false)]
    [InlineData("Ich prüfe die Rechnung: 17 * 19 = 323.", false)]
    [InlineData("Ich analysiere die Ursache: Ein leerer Indexzugriff löst den Fehler aus.", false)]
    public void DistinguishesUnexecutedAnnouncementsFromAnswers(string text, bool expected) =>
        Assert.Equal(expected, CodingCompletionGuard.IsActionAnnouncement(text));
}
