using System.Buffers.Binary;
using GoWinUI.App.Services;
using NAudio.Wave;

namespace GoWinUI.Tests;

public sealed class SpeechPlaybackOnsetTests
{
    [Theory]
    [InlineData(44_100, 1)]
    [InlineData(48_000, 2)]
    public void NewOutputHasWarmupBeforeEverySpeechSampleAndNoPauseBetweenBufferedChunks(int sampleRate, int channels)
    {
        var format = new WaveFormat(sampleRate, 16, channels);
        var buffer = new BufferedWaveProvider(format) { ReadFully = false };
        var first = Enumerable.Range(0, format.BlockAlign * 160).Select(index => (byte)(index % 127 + 1)).ToArray();
        var next = Enumerable.Range(0, format.BlockAlign * 90).Select(index => (byte)(255 - index % 127)).ToArray();
        var prefixLength = MicrophoneTranscriptionService.PrimeSpeechPlaybackBuffer(buffer);
        buffer.AddSamples(first, 0, first.Length);
        buffer.AddSamples(next, 0, next.Length);

        Assert.Equal(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(prefixLength / (double)format.AverageBytesPerSecond));
        Assert.Equal(0, prefixLength % format.BlockAlign);
        using var captured = new MemoryStream();
        var fragment = new byte[format.BlockAlign * 73];
        int read;
        while ((read = buffer.Read(fragment, 0, fragment.Length)) > 0) captured.Write(fragment, 0, read);
        var actual = captured.ToArray();
        Assert.Equal(prefixLength + first.Length + next.Length, actual.Length);
        Assert.All(actual.Take(prefixLength), sample => Assert.Equal(0, sample));
        Assert.Equal(first, actual.AsSpan(prefixLength, first.Length).ToArray());
        Assert.Equal(next, actual.AsSpan(prefixLength + first.Length).ToArray());
    }

    [Fact]
    public void AudibleWaveValidationAndConversionDoNotConsumeTheStartOfSpeech()
    {
        var format = new WaveFormat(44_100, 16, 1);
        var path = Path.Combine(Path.GetTempPath(), $"go-speech-onset-{Guid.NewGuid():N}.wav");
        var source = new byte[format.AverageBytesPerSecond / 4];
        for (var offset = 0; offset < source.Length; offset += sizeof(short))
        {
            var value = (short)(12_000 * Math.Cos(offset / 2.0 * 2 * Math.PI * 440 / format.SampleRate));
            BinaryPrimitives.WriteInt16LittleEndian(source.AsSpan(offset), value);
        }
        try
        {
            using (var writer = new WaveFileWriter(path, format)) writer.Write(source, 0, source.Length);
            var actual = MicrophoneTranscriptionService.ReadPlaybackPcm16(path);
            Assert.Equal(source.Length, actual.Length);
            for (var offset = 0; offset < source.Length; offset += sizeof(short))
            {
                var expectedSample = BinaryPrimitives.ReadInt16LittleEndian(source.AsSpan(offset));
                var actualSample = BinaryPrimitives.ReadInt16LittleEndian(actual.AsSpan(offset));
                Assert.InRange(Math.Abs(expectedSample - actualSample), 0, 1);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void WarmupCannotBeInsertedAfterSpeechAlreadyEnteredTheBuffer()
    {
        var buffer = new BufferedWaveProvider(new WaveFormat(44_100, 16, 1));
        buffer.AddSamples(new byte[8], 0, 8);
        Assert.Throws<InvalidOperationException>(() => MicrophoneTranscriptionService.PrimeSpeechPlaybackBuffer(buffer));
        Assert.Equal(8, buffer.BufferedBytes);
    }
}
