namespace GoWinUI.Core.Models;

public enum AiProviderKind
{
    GoAiServer,
}

public enum PromptTriggerAction
{
    ImageGeneration,
    Translation,
    TextToSpeech,
    Transcription,
    AudioAnalysis,
    VideoAnalysis,
    ImageAnalysis,
    WebSearch,
    YouTubeSearch,
    BricsCad,
    Code,
    VoiceInput,
    LiveCaptions,
    LiveTranslation,
}

public enum PromptTriggerMatchMode
{
    Prefix,
    Contains,
    Exact,
}

public sealed record PromptTrigger(
    Guid Id,
    PromptTriggerAction Action,
    string Phrase,
    string Description,
    PromptTriggerMatchMode MatchMode,
    bool IsEnabled,
    int Priority,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record PromptTriggerMatch(
    PromptTrigger Trigger,
    string OriginalPrompt,
    string RemainingPrompt);

public sealed record AssistantAttachment(
    Guid Id,
    Guid SessionId,
    Guid BlobId,
    string FileName,
    string ContentType,
    string Sha256,
    long Length,
    DateTimeOffset CreatedAt);

public sealed record ChatArtifact(
    Guid Id,
    Guid MessageId,
    Guid BlobId,
    string ServerArtifactId,
    string FileName,
    string ContentType,
    string Sha256,
    long Length,
    string Provider,
    DateTimeOffset CreatedAt,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record GoAiRunRecord(
    Guid Id,
    Guid SessionId,
    Guid AssistantMessageId,
    PromptTriggerAction? Action,
    string IdempotencyKey,
    string? ServerRunId,
    long LastEventId,
    string State,
    string? SelectedModel,
    string? ErrorCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ClientToolExecutionRecord(
    string ProposalId,
    Guid LocalRunId,
    string ServerRunId,
    long EventId,
    string ToolName,
    string State,
    string? ResultJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
