using GoAi.Contracts;
using GoAi.Server.Core.Runs;

namespace GoAi.Server.Tests;

public sealed class VideoAudioAnalysisTests
{
    [Fact]
    public void FusionContextCombinesVisionAndTimedSpeechWithoutLosingTheUserRequest()
    {
        var transcription = new TranscriptionResponse(
            "Die Pumpe startet. Der Druck steigt.",
            "de",
            0.99,
            [
                new(1.25, 3.5, "Die Pumpe startet.", "Person 1"),
                new(8, 10.125, "Der Druck steigt."),
            ],
            "faster-whisper-large-v3");

        var messages = AgentToolExecutor.BuildVideoAndAudioFusionMessages(
            "Bewerte den Anlagenstart.",
            "Das Manometer steigt von zwei auf drei bar.",
            transcription);

        Assert.Equal(2, messages.Count);
        var user = messages.Single(static message => message.Role == "user").Content;
        Assert.Contains("Bewerte den Anlagenstart", user, StringComparison.Ordinal);
        Assert.Contains("Das Manometer steigt", user, StringComparison.Ordinal);
        Assert.Contains("[00:01.250–00:03.500] Person 1: Die Pumpe startet.", user, StringComparison.Ordinal);
        Assert.Contains("[00:08.000–00:10.125] Der Druck steigt.", user, StringComparison.Ordinal);
    }
}
