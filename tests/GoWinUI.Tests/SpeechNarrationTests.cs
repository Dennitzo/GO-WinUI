using GoAi.Contracts;
using GoWinUI.App.Services;

namespace GoWinUI.Tests;

public sealed class SpeechNarrationTests
{
    [Theory]
    [InlineData("bzw.", "beziehungsweise")]
    [InlineData("z. B.", "zum Beispiel")]
    [InlineData("z.B.", "zum Beispiel")]
    [InlineData("d. h.", "das heißt")]
    [InlineData("ggf.", "gegebenenfalls")]
    public void IncrementalAbbreviationsStayTogetherUntilTheActualSentenceEnds(string abbreviation, string spoken)
    {
        var buffer = new SpeechStreamingTextBuffer();
        var prefix = "Die Eingabetaste " + abbreviation + " ";
        for (var length = 1; length <= prefix.Length; length++)
            Assert.Empty(buffer.Take(prefix[..length]));
        var sentence = prefix + "der Knopf sendet die Nachricht. ";
        Assert.Equal("Die Eingabetaste " + spoken + " der Knopf sendet die Nachricht.", Assert.Single(buffer.Take(sentence)));
        Assert.Empty(buffer.Take(sentence, complete: true));
    }

    [Fact]
    public void AbbreviationBeforeAToolBoundaryIsExpandedWithoutAFalseSentenceEnd()
    {
        var buffer = new SpeechStreamingTextBuffer();
        Assert.Empty(buffer.Take("Ich prüfe ggf.", flushSentence: true));
        Assert.Equal("Ich prüfe gegebenenfalls den Knopf.", Assert.Single(buffer.Take("Ich prüfe ggf. den Knopf.", flushSentence: true)));
    }

    [Fact]
    public void AbbreviationsDoNotSplitManualPlaybackOrModifyPathsAndDecimals()
    {
        Assert.Single(SpeechSourceSegmentation.SplitForPlayback("Die Seite bzw. das Formular ist bereit."));
        Assert.Equal("dashboard.html und Version 3.14", GermanSpeechAbbreviations.Expand("dashboard.html und Version 3.14"));
        Assert.Equal("zum Beispiel beziehungsweise das heißt", GermanSpeechAbbreviations.Expand("z. B. bzw. d. h."));
    }

    [Fact]
    public void ScreenshotFindingAsNarrativeStreamsWithoutTechnicalIdentifiers()
    {
        const string narration = "Alle 4 gewünschten Funktionen sind bereits vorhanden. "
            + "Die Bereiche stehen untereinander und nutzen die volle Fensterbreite. "
            + "Die Eingabetaste sendet die Nachricht; zusammen mit der Umschalttaste fügt sie einen Zeilenumbruch ein. "
            + "Auch der Senden-Knopf und das automatische Scrollen sind vorhanden. ";
        var buffer = new SpeechStreamingTextBuffer();
        var chunks = new List<string>();
        for (var length = 1; length <= narration.Length; length++) chunks.AddRange(buffer.Take(narration[..length]));
        chunks.AddRange(buffer.Take(narration, complete: true));
        Assert.Equal(4, chunks.Count);
        Assert.StartsWith("Alle vier gewünschten Funktionen", chunks[0], StringComparison.Ordinal);
        var prepared = string.Join(" ", chunks.Select(MicrophoneTranscriptionService.PrepareSpeechText));
        Assert.DoesNotContain("4", prepared, StringComparison.Ordinal);
        Assert.DoesNotContain("SHA", prepared, StringComparison.Ordinal);
        Assert.DoesNotContain("requestSubmit", prepared, StringComparison.Ordinal);
        Assert.EndsWith("automatische Scrollen sind vorhanden.", prepared, StringComparison.Ordinal);
    }
}
