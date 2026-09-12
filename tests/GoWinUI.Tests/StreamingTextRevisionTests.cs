using GoAi.Contracts;
using GoWinUI.App.Services;

namespace GoWinUI.Tests;

public sealed class StreamingTextRevisionTests
{
    [Fact]
    public void RetryCorrectionReplacesTheAuthoritativeNarrationWithoutDuplicatingEarlierTurns()
    {
        const string previous = "Erste Datei geprüft.\n\n";
        var content = GoAiAssistantService.ApplyTextDelta(previous, new TextDeltaEvent("Ich prüfe die nächst"));
        content = GoAiAssistantService.ApplyTextDelta(content, new TextDeltaEvent(previous + "Jetzt prüfe ich die Tests.", ReplaceFrom: 0));
        Assert.Equal(previous + "Jetzt prüfe ich die Tests.", content);
        Assert.Equal(content, GoAiAssistantService.ApplyTextDelta(content, new TextDeltaEvent(content, ReplaceFrom: 0)));
        Assert.Equal(content + " Fertig.", GoAiAssistantService.ApplyTextDelta(content, new TextDeltaEvent(" Fertig.")));
    }

    [Fact]
    public void FullRevisionIsIndependentOfSanitizedReplayOffsetsAndPreservesUnicode()
    {
        const string authoritative = "\nGrüße 日本語 😀\n\nNächster Schritt.";
        var content = GoAiAssistantService.ApplyTextDelta("Grüße 日本語 😀", new TextDeltaEvent(authoritative, ReplaceFrom: 0));
        Assert.Equal(authoritative, content);
        Assert.Throws<InvalidDataException>(() => GoAiAssistantService.ApplyTextDelta(content, new TextDeltaEvent("bad", ReplaceFrom: 3)));
        Assert.Equal(content, GoAiAssistantService.ApplyTextDelta(content, null));
    }
}
