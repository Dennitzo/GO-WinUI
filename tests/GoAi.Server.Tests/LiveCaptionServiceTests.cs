using GoAi.Server.Core.Audio;
using GoAi.Contracts;
using System.Buffers.Binary;
using System.Text;

namespace GoAi.Server.Tests;

public sealed class LiveCaptionServiceTests
{
    [Theory]
    [InlineData("de")]
    [InlineData("de-DE")]
    [InlineData("de_DE")]
    [InlineData("Deutsch")]
    [InlineData("German")]
    public void GermanCaptionsBypassGeneralAiTranslation(string language)
    {
        Assert.False(LiveCaptionService.RequiresGermanTranslation(
            language,
            0.95,
            "Die Lüftungsanlage ist aktiv und der Volumenstrom bleibt stabil."));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ko")]
    [InlineData("fr-FR")]
    [InlineData(null)]
    public void ForeignOrUnknownCaptionsStillTranslateToGerman(string? language)
    {
        Assert.True(LiveCaptionService.RequiresGermanTranslation(
            language,
            0.95,
            "Today we discuss the largest questions about the world."));
    }

    [Fact]
    public void UncertainGermanDetectionStillUsesGeneralAiTranslation()
    {
        Assert.True(LiveCaptionService.RequiresGermanTranslation(
            "de",
            0.62,
            "The current window is not reliably German."));
    }

    [Fact]
    public void ClearlyEnglishTextOverridesAStaleGermanLanguageDecision()
    {
        Assert.True(LiveCaptionService.RequiresGermanTranslation(
            "de",
            0.96,
            "Well today we are going to answer the biggest questions about the world and how it works."));
    }

    [Fact]
    public void GermanWithEnglishTechnicalTermsDoesNotRunThroughTranslation()
    {
        Assert.False(LiveCaptionService.RequiresGermanTranslation(
            "de",
            0.96,
            "Die Anlage nutzt einen Cloud Service und das aktuelle Building Information Model."));
    }

    [Fact]
    public void ValidatesBoundedPcm16MonoWaveWindows()
    {
        var wave = CreateWave(sampleRate: 16_000, channels: 1, durationMilliseconds: 4_000);

        var info = LiveCaptionService.ValidateWave(wave, 16_000, 1, 5_000);

        Assert.Equal(16_000, info.SampleRate);
        Assert.Equal(1, info.Channels);
        Assert.Equal(16, info.BitsPerSample);
        Assert.Equal(4_000, info.DurationMilliseconds, precision: 3);
    }

    [Fact]
    public void RejectsUnresampledSystemAudio()
    {
        var stereo48Khz = CreateWave(sampleRate: 48_000, channels: 2, durationMilliseconds: 1_000);

        Assert.Throws<InvalidDataException>(() =>
            LiveCaptionService.ValidateWave(stereo48Khz, 16_000, 1, 5_000));
    }

    [Fact]
    public void RemovesOnlyTheRepeatedOverlapPrefix()
    {
        var previous = "Die Lüftungsanlage ist aktiv und liefert Außenluft.";
        var current = "ist aktiv und liefert Außenluft. Der Volumenstrom bleibt stabil.";

        var unique = LiveCaptionService.RemoveRepeatedPrefix(previous, current);

        Assert.Equal("Der Volumenstrom bleibt stabil.", unique);
        Assert.Equal("Neue Aussage ohne Überlappung.", LiveCaptionService.RemoveRepeatedPrefix(previous, "Neue Aussage ohne Überlappung."));
    }

    [Fact]
    public void PreservesSpeakerChangesWhileRemovingOverlappingWords()
    {
        IReadOnlyList<TranscriptionSegment> segments =
        [
            new(0, 1, "liefert Außenluft.", "Person 1"),
            new(1, 2, "Der Volumenstrom bleibt stabil.", "Person 2"),
        ];

        var unique = LiveCaptionService.RemoveRepeatedSegments(
            "Die Anlage liefert Außenluft.",
            segments);

        var remaining = Assert.Single(unique);
        Assert.Equal("Person 2", remaining.Speaker);
        Assert.Equal("Der Volumenstrom bleibt stabil.", remaining.Text);
        Assert.Equal(
            "Person 1: Guten Morgen.\nPerson 2: Hallo zusammen.",
            LiveCaptionService.FormatDialogueChunk(
            [
                new(0, 1, "Guten Morgen.", "Person 1"),
                new(1, 2, "Hallo zusammen.", "Person 2"),
            ]).Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static byte[] CreateWave(int sampleRate, int channels, int durationMilliseconds)
    {
        var dataLength = checked(sampleRate * channels * 2 * durationMilliseconds / 1_000);
        var wave = new byte[44 + dataLength];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wave, 0);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(4, 4), wave.Length - 8);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(wave, 8);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(22, 2), checked((ushort)channels));
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(28, 4), sampleRate * channels * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(32, 2), checked((ushort)(channels * 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(34, 2), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(wave, 36);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(40, 4), dataLength);
        return wave;
    }
}
