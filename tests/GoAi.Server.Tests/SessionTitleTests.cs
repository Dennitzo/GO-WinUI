using GoAi.Server.Core.Runs;

namespace GoAi.Server.Tests;

public sealed class SessionTitleTests
{
    [Fact]
    public void InstructionHeavyFallbackKeepsTheActualTechnicalSubject()
    {
        var title = RunProcessor.SanitizeTitle(
            string.Empty,
            "Beschreibe in genau einem kurzen deutschen Satz, wie du einen .NET-Buildfehler im TGA-Projekt zuerst eingrenzt.");

        Assert.Equal("NET-Buildfehler TGA-Projekt eingrenzt", title);
    }

    [Fact]
    public void ValidModelGeneratedTitleRemainsAuthoritative()
    {
        var title = RunProcessor.SanitizeTitle(
            "Buildfehler im TGA-Projekt eingrenzen",
            "Beschreibe den Fehler.");

        Assert.Equal("Buildfehler im TGA-Projekt eingrenzen", title);
    }
}
