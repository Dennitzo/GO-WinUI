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
    VoiceInput,
    LiveCaptions,
    LiveTranslation,
    Audiobook,
    Coding,
    DocumentCreate,
    Extension,
    PlanMode,
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
    DateTimeOffset UpdatedAt,
    string? ExtensionActionId = null);

public sealed record PromptTriggerMatch(
    PromptTrigger Trigger,
    string OriginalPrompt,
    string RemainingPrompt,
    bool DeepResearch = false,
    string DeepResearchProfile = "auto");

public static class DeepResearchProfiles
{
    public const string Auto = "auto";
    public const string Web = "web";
    public const string ScientificEvidence = "scientificEvidence";
    public const string SystematicReview = "systematicReview";
    public const string ScopingReview = "scopingReview";
    public const string LiteratureUpdate = "literatureUpdate";
    public const string ReplicationAudit = "replicationAudit";
    public const string OpenProblem = "openProblem";
    public const string MathematicalInvestigation = "mathematicalInvestigation";

    public static bool IsValid(string? value) => value is
        Auto or Web or ScientificEvidence or SystematicReview or ScopingReview
        or LiteratureUpdate or ReplicationAudit or OpenProblem or MathematicalInvestigation;
}

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
    IReadOnlyDictionary<string, string>? Metadata = null,
    string? StepId = null);

public sealed record GeneratedDocument(
    Guid Id,
    Guid SessionId,
    string FileName,
    string Format,
    string SourceMarkdown,
    string Sha256,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

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
    DateTimeOffset UpdatedAt,
    string? WorkspacePath = null,
    string? ExtensionActionId = null);

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
