using GoAi.Server.Core.Runs;

namespace GoAi.Server.Tests;

public sealed class SessionTitleTests
{
    [Fact]
    public void InstructionHeavyFallbackKeepsTheActualTechnicalSubject()
    {
        var title = RunProcessor.SanitizeTitle(
            string.Empty,
            "Beschreibe in genau einem kurzen deutschen Satz, wie du einen .NET-Buildfehler im Software-Projekt zuerst eingrenzt.");

        Assert.Equal("NET-Buildfehler Software-Projekt eingrenzt", title);
    }

    [Fact]
    public void ValidModelGeneratedTitleRemainsAuthoritative()
    {
        var title = RunProcessor.SanitizeTitle(
            "Buildfehler im Software-Projekt eingrenzen",
            "Beschreibe den Fehler.");

        Assert.Equal("Buildfehler im Software-Projekt eingrenzen", title);
    }

    [Fact]
    public void EmptyConversationUsesANeutralTitle()
    {
        Assert.Equal("Neue Sitzung", RunProcessor.SanitizeTitle(string.Empty, string.Empty));
    }
}
