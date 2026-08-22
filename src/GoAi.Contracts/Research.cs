namespace GoAi.Contracts;

public sealed record WebSearchRequest(string Query, int MaximumResults = 10, string? Language = "de-DE");

public sealed record WebSearchResponse(
    string Query,
    IReadOnlyList<WebSearchResult> Results,
    string Provider,
    bool IsFallback,
    DateTimeOffset RetrievedAt);

public sealed record WebSearchResult(
    string Title,
    string Url,
    string? Snippet,
    string? Source = null,
    DateTimeOffset? PublishedAt = null,
    string? ThumbnailUrl = null,
    string? Duration = null);

public sealed record WebFetchRequest(string Url);

public sealed record WebFetchResponse(
    string Url,
    string MediaType,
    string Content,
    bool IsUntrusted,
    DateTimeOffset RetrievedAt,
    IReadOnlyList<string> RedirectChain);

public sealed record SpeechRequest(
    string Text,
    string? Voice = "de-DE-Female",
    string? Format = "wav",
    double Speed = 1.0);

public sealed record TranscriptionRequest(
    string UploadId,
    string? Language = null);

public sealed record TranscriptionSegment(
    double Start,
    double End,
    string Text,
    string? Speaker = null,
    IReadOnlyList<TranscriptionWord>? Words = null);

public sealed record TranscriptionWord(
    double Start,
    double End,
    string Text,
    double Probability = 1.0);

public sealed record TranscriptionResponse(
    string Text,
    string Language,
    double LanguageProbability,
    IReadOnlyList<TranscriptionSegment> Segments,
    string Provider);

public enum LiveCaptionMode
{
    Transcribe,
    TranslateToEnglish,
}

public enum LiveCaptionProfile
{
    Captions,
    Dictation,
}

public sealed record LiveCaptionSessionRequest(
    string? Language = "de",
    LiveCaptionMode Mode = LiveCaptionMode.Transcribe,
    int SampleRate = GoAiProtocol.LiveCaptionSampleRate,
    int Channels = 1,
    int WindowMilliseconds = 4_000,
    int OverlapMilliseconds = 500,
    LiveCaptionProfile Profile = LiveCaptionProfile.Captions);

public sealed record LiveCaptionChunkMetadata(
    string TurnId,
    long Revision,
    int WindowStartMilliseconds,
    bool IsFinal);

public sealed record LiveCaptionSessionSnapshot(
    string SessionId,
    string State,
    LiveCaptionMode Mode,
    string? Language,
    int SampleRate,
    int Channels,
    int WindowMilliseconds,
    int OverlapMilliseconds,
    long NextSequence,
    string Transcript,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ExpiresAt,
    LiveCaptionProfile Profile = LiveCaptionProfile.Captions);

public sealed record LiveCaptionChunkResponse(
    string SessionId,
    long Sequence,
    string Text,
    string Transcript,
    string Language,
    double LanguageProbability,
    IReadOnlyList<TranscriptionSegment> Segments,
    bool IsFinal,
    string Provider,
    DateTimeOffset CreatedAt,
    string? TurnId = null,
    long Revision = 0,
    string? StableText = null,
    string? ProvisionalText = null);

public sealed record SpeechResponse(
    ArtifactDescriptor Artifact,
    string Provider);

public enum SpeechContentProfile
{
    Prepared,
    Audiobook,
}

public static class SpeechProviderIds
{
    public const string SupertonicF5Cuda = "supertonic-3-f5-cuda";
}

public enum SpeechAlignmentStatus
{
    NotRequested,
    Deterministic,
    Failed,
}

public sealed record SpeechSessionRequest(
    SpeechContentProfile Profile = SpeechContentProfile.Prepared,
    string Language = "de");

public sealed record SpeechSessionSnapshot(
    string SessionId,
    string State,
    SpeechContentProfile Profile,
    string Provider,
    bool GeneralModelEjected,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SpeechParagraphRequest(
    string Text,
    int ParagraphIndex,
    double Speed = 1.0,
    IReadOnlyList<SpeechParagraphPart>? Parts = null);

public sealed record SpeechParagraphPart(
    int SegmentIndex,
    string Text,
    double Speed = 1.0,
    int PauseBeforeMilliseconds = 0,
    int PauseAfterMilliseconds = 0);

public sealed record SpeechParagraphTiming(
    int SegmentIndex,
    double StartSeconds,
    double EndSeconds);

public sealed record SpeechParagraphResponse(
    ArtifactDescriptor Artifact,
    string Provider,
    int ParagraphIndex,
    double DurationSeconds,
    int SampleRate,
    IReadOnlyList<SpeechParagraphTiming>? Timings = null,
    SpeechAlignmentStatus AlignmentStatus = SpeechAlignmentStatus.NotRequested,
    double? AlignmentConfidence = null);

public sealed record MediaJobRequest(
    string UploadId,
    string? Prompt = null,
    IReadOnlyDictionary<string, string>? Options = null,
    IReadOnlyList<MediaTimeWindow>? DetailWindows = null);

public sealed record MediaTimeWindow(double Start, double End);

public sealed record ImageGenerationRequest(
    string Prompt,
    int Width = 1024,
    int Height = 1024,
    int? Seed = null,
    int Count = 1);
