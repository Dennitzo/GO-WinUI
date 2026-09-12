namespace GoWinUI.Core.Models;

public enum ChatRole { System, User, Assistant }
public enum MessageStatus { Pending, Streaming, Completed, Cancelled, Failed, Interrupted }
public enum PersistentToolAction { BricsCad, Audiobook, Coding }
public enum MessageContentProfile { General, Audiobook }
public enum SessionContextProfile { General, Audiobook }
public enum ProjectStatus { Active, Archived }
public enum AssetCategory { Pdf, Drawing, Image, Meeting, Other, Cpdb, Ifc }
public enum AppTheme { System, Light, Dark }
public enum WindowDisplayState { Normal, Maximized }
public sealed record ChatSession(
    Guid Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Guid? SelectedWorkflowId = null,
    string Draft = "",
    bool IsPinned = false,
    DateTimeOffset? PinnedAt = null,
    PersistentToolAction? PersistentToolAction = null,
    long ConversationRevision = 0,
    string? CodingWorkspacePath = null);

public sealed record ChatMessage(
    Guid Id,
    Guid SessionId,
    ChatRole Role,
    string Content,
    MessageStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Error = null,
    ToolExecutionInfo? ToolExecution = null,
    string? ContextSummary = null,
    MessageContentProfile ContentProfile = MessageContentProfile.General,
    long Revision = 1,
    IReadOnlyList<AssistantToolStep>? ToolSteps = null);

public sealed record AssistantToolStep(
    string Id,
    string Tool,
    string Status,
    string? Detail = null,
    string? PreviewHtml = null,
    string? InputJson = null,
    string? OutputJson = null,
    string? Explanation = null,
    int? ContentOffset = null,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null,
    DateTimeOffset? UpdatedAt = null)
{
    // Display/storage limit only. Tool collection and model-context budgets are independent.
    public const int MaximumDetailCharacters = 2_097_152;
    public const int MaximumStructuredJsonCharacters = 2_097_152;
    public const int MaximumExplanationCharacters = 2_000;

    public static AssistantToolStep Merge(AssistantToolStep? previous, AssistantToolStep incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (previous is null) return incoming;
        if (incoming.Status == "running" && previous.Status != "running") return previous;
        if (previous.UpdatedAt is { } previousAt
            && (incoming.UpdatedAt is { } incomingAt && incomingAt < previousAt
                || incoming.UpdatedAt is null)) return previous;
        return incoming with
        {
            InputJson = previous.InputJson ?? incoming.InputJson,
            OutputJson = incoming.OutputJson ?? previous.OutputJson,
            Explanation = previous.Explanation ?? incoming.Explanation,
            ContentOffset = incoming.ContentOffset ?? previous.ContentOffset,
            StartedAt = previous.StartedAt ?? incoming.StartedAt,
            CompletedAt = incoming.Status == "running" ? null : incoming.CompletedAt ?? previous.CompletedAt,
            UpdatedAt = incoming.UpdatedAt ?? previous.UpdatedAt,
            Detail = incoming.Detail ?? previous.Detail,
            PreviewHtml = incoming.PreviewHtml ?? previous.PreviewHtml,
        };
    }
}

public sealed record ChatTurn(ChatMessage UserMessage, ChatMessage AssistantMessage);

public sealed record ConversationSnapshot(
    ChatSession Session,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyDictionary<Guid, IReadOnlyList<ChatArtifact>> Artifacts);

public sealed record SessionContextPreparation(
    string CacheKey,
    Guid SessionId,
    string HistoryRevision,
    string ModelId,
    int ContextBudget,
    Guid ThroughMessageId,
    int MessageCount,
    string PreparedText,
    DateTimeOffset CreatedAt,
    SessionContextProfile Profile = SessionContextProfile.General);

public sealed record ToolExecutionInfo(
    string Tool,
    string Context,
    string Status,
    string? Detail = null,
    string? Provider = null);

public sealed record WorkflowDefinition(
    Guid Id,
    string Slug,
    string Title,
    string Description,
    string Domain,
    string ContextSummary,
    string ContentJson,
    bool IsBuiltIn,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string>? Tags = null)
{
    public IReadOnlyList<string> EffectiveTags => Tags ?? Array.Empty<string>();
}

public sealed record Project(
    Guid Id,
    string Name,
    string ConstructionProject,
    string Description,
    string Notes,
    ProjectStatus Status,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ChecklistItem(
    Guid Id,
    Guid ProjectId,
    string Text,
    bool IsCompleted,
    int SortOrder,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ProjectAsset(
    Guid Id,
    Guid ProjectId,
    Guid BlobId,
    string FileName,
    string ContentType,
    AssetCategory Category,
    string? SourcePath,
    string Sha256,
    long Length,
    int SortOrder,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Title = null);

public sealed record AssetThumbnail(
    Guid AssetId,
    Guid BlobId,
    string ContentType,
    int Width,
    int Height,
    DateTimeOffset CreatedAt);

public sealed record BinaryObjectDescriptor(
    Guid Id,
    string Sha256,
    long Length,
    string ContentType,
    int ChunkCount,
    DateTimeOffset CreatedAt);

public sealed record StoredDocument(
    Guid Id,
    Guid SessionId,
    Guid BlobId,
    string FileName,
    string ContentType,
    string Sha256,
    long Length,
    int PageCount,
    DateTimeOffset CreatedAt,
    DocumentPreparationStatus PreparationStatus = DocumentPreparationStatus.Ready,
    int PreparationProgress = 100,
    bool WasReused = false,
    string? PreparationError = null);

public enum DocumentPreparationStatus
{
    Extracting,
    Preparing,
    Ready,
    Failed,
}

public sealed record DocumentContextHit(
    Guid DocumentId,
    string Sha256,
    string FileName,
    int PageNumber,
    string Text,
    double Score,
    string? ChunkId = null);

public sealed record DocumentIndexChunk(
    string Id,
    Guid DocumentId,
    string Sha256,
    string FileName,
    int PageNumber,
    string Text,
    IReadOnlyList<double>? Embedding = null);

public sealed record DocumentChunkEmbedding(
    string ChunkId,
    string ModelId,
    IReadOnlyList<double> Values);

public sealed record DocumentContextPreparation(
    string CacheKey,
    Guid SessionId,
    string CorpusRevision,
    string PromptFingerprint,
    string ModelId,
    int ContextBudget,
    string PreparedText,
    IReadOnlyList<DocumentContextHit> Evidence,
    DateTimeOffset CreatedAt);

public sealed record DocumentPage(Guid DocumentId, int PageNumber, string Text, string? FileName = null);

public sealed record DocumentIngestResult(
    StoredDocument? Document,
    bool Success,
    string? Error,
    bool HasExtractableText);

public sealed record LocalAiModel(
    string Id,
    string? DisplayName = null,
    int? ContextLength = null,
    bool SupportsTools = false,
    bool SupportsVision = false,
    IReadOnlyList<string>? ReasoningEfforts = null,
    string? DefaultReasoningEffort = null);

public sealed record LocalChatMessage(ChatRole Role, string Content);

public sealed record ContextBuildRequest(
    string SystemPrompt,
    string UserPrompt,
    IReadOnlyList<ChatMessage> History,
    WorkflowDefinition? Workflow,
    IReadOnlyList<DocumentPage> DocumentPages,
    int ContextLength);

public sealed record ContextBuildResult(
    IReadOnlyList<LocalChatMessage> Messages,
    int EstimatedTokens,
    bool WasTruncated,
    string? TruncationNotice,
    string? RequestEnvelopeJson = null,
    IReadOnlyList<string>? PolicyReferences = null,
    int MaxOutputTokens = 1_024);

public sealed record WindowPlacement(
    double X = 120,
    double Y = 80,
    double Width = 1280,
    double Height = 820,
    string? MonitorId = null,
    double SavedDpi = 96,
    WindowDisplayState State = WindowDisplayState.Normal);

public sealed record AppSettings
{
    public const int CurrentVersion = 19;
    public const string DefaultSelectedModel = "gpt-oss-120b";
    public const string DefaultAccentColor = "#A970FF";
    public const string DefaultBackgroundColor = "#6B6872";
    public const int MaximumRecentActivityTextLength = 180;

    public int Version { get; init; } = CurrentVersion;
    public bool IsAiConnectionEnabled { get; init; } = true;
    public AiProviderKind AiProvider { get; init; } = AiProviderKind.GoAiServer;
    public string GoAiServerUrl { get; init; } = "http://192.168.0.67:8080";
    public string GoAiProtocolVersion { get; init; } = "1.0";
    public string LiveCaptionLanguage { get; init; } = "auto";
    public string? SelectedModel { get; init; } = DefaultSelectedModel;
    public string? SelectedCodingModel { get; init; }
    public string? CodingWorkspacePath { get; init; }
    public bool CodingToolStepsExpanded { get; init; }
    // Legacy JSON field retained for backward-compatible deserialization. "auto"
    // uses the gateway's highest supported model-specific reasoning default.
    public string ReasoningEffort { get; init; } = "auto";
    public AppTheme Theme { get; init; } = AppTheme.System;
    public string AccentColor { get; init; } = DefaultAccentColor;
    public string BackgroundColor { get; init; } = DefaultBackgroundColor;
    public string Language { get; init; } = "de-DE";
    public WindowPlacement Window { get; init; } = new();
    public double NavigationPaneWidth { get; init; } = 320;
    public bool IsNavigationPaneOpen { get; init; } = true;
    public bool IsAssistantSessionPaneOpen { get; init; } = true;
    public string LastRoute { get; init; } = "assistant";
    public Guid? ActiveSessionId { get; init; }
    public Guid? ActiveProjectId { get; init; }
    public string? LastActivityText { get; init; }
    public DateTimeOffset? LastActivityAt { get; init; }
}

public sealed record SessionLogEntry(
    long Sequence,
    DateTimeOffset Timestamp,
    string Level,
    string Category,
    string Message,
    int EventId,
    string? Exception,
    IReadOnlyDictionary<string, string?> Properties);

public sealed record BackupResult(string Path, string Sha256, DateTimeOffset CreatedAt);

public sealed class RevisionConflictException(string entity, Guid id)
    : InvalidOperationException($"{entity} '{id}' wurde zwischenzeitlich geändert.");

public sealed class UnsupportedDocumentException(string message) : NotSupportedException(message);
