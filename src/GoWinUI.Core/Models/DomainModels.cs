using System.Collections.ObjectModel;

namespace GoWinUI.Core.Models;

public enum ChatRole { System, User, Assistant }
public enum MessageStatus { Pending, Streaming, Completed, Cancelled, Failed, Interrupted }
public enum ChatMessageVisibility { Visible, Internal }
public enum AssistantMode { General, Code }
public enum PersistentToolAction { Code, BricsCad, Audiobook }
public enum MessageContentProfile { General, Audiobook }
public enum SessionContextProfile { General, Code, Audiobook }
public enum ProjectStatus { Active, Archived }
public enum AssetCategory { Pdf, Drawing, Image, Meeting, Other, Cpdb, Ifc }
public enum AppTheme { System, Light, Dark }
public enum WindowDisplayState { Normal, Maximized }
public enum CodingCampaignStatus { Running, Faulted, Stopped }
public enum CodingCampaignPhase { Bootstrap, Iteration, Correction, Validation }

public sealed record ChatSession(
    Guid Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Guid? SelectedWorkflowId = null,
    string Draft = "",
    AssistantMode AssistantMode = AssistantMode.General,
    string? WorkspacePath = null,
    string? WorkspaceFingerprint = null,
    bool IsPinned = false,
    DateTimeOffset? PinnedAt = null,
    PersistentToolAction? PersistentToolAction = null,
    long ConversationRevision = 0);

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
    string? CodeDiff = null,
    ChatMessageVisibility Visibility = ChatMessageVisibility.Visible,
    long Revision = 1);

public sealed record ChatTurn(ChatMessage UserMessage, ChatMessage AssistantMessage);

public sealed record CodingProcessConsole(
    string OperationId,
    string Command,
    string WorkingDirectory,
    string Purpose,
    string Status,
    int? ExitCode = null,
    string? StandardOutput = null,
    string? StandardError = null);

public sealed record CodingRunTraceEntry(
    long Sequence,
    DateTimeOffset Timestamp,
    string Stage,
    string Status,
    string Title,
    string? Detail = null,
    string? Tool = null,
    string? Target = null,
    long? DurationMilliseconds = null,
    long? ServerEventId = null,
    CodingProcessConsole? ProcessConsole = null);

public sealed record CodingRunSnapshot(
    Guid Id,
    Guid LocalRunId,
    string? ServerRunId,
    Guid SessionId,
    Guid? MessageId,
    string Status,
    string? CodeDiff,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    long Revision,
    IReadOnlyList<CodingRunTraceEntry> Entries);

public sealed record ConversationSnapshot(
    ChatSession Session,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyDictionary<Guid, IReadOnlyList<ChatArtifact>> Artifacts,
    CodingRunSnapshot? CodingRun);

public sealed record CodingCampaignState(
    Guid Id,
    Guid SessionId,
    string DefinitionId,
    string Title,
    string WorkspacePath,
    string WorkspaceFingerprint,
    string ModelId,
    CodingCampaignStatus Status,
    CodingCampaignPhase Phase,
    int Iteration,
    string? CurrentChallenge,
    string? LastError,
    string ValidationJson,
    int RestartCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CodingCampaignIteration(
    Guid Id,
    Guid CampaignId,
    int Iteration,
    CodingCampaignPhase Phase,
    string Challenge,
    Guid? AssistantMessageId,
    string Status,
    string? Error,
    string ValidationJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

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

public sealed record LmModel(string Id, string? DisplayName = null, int? ContextLength = null);

public sealed record LmDelta(string Text, string? Reasoning = null, bool IsCompleted = false);

public sealed record ChatStreamUpdate(
    Guid SessionId,
    Guid MessageId,
    string Delta,
    string Content,
    MessageStatus Status,
    int? EstimatedContextTokens = null,
    int? ContextLimit = null,
    bool ContextWasTruncated = false,
    string? ContextNotice = null);

public sealed record LmChatMessage(ChatRole Role, string Content);

public sealed record LmChatRequest(
    string Model,
    IReadOnlyList<LmChatMessage> Messages,
    string? ReasoningEffort = null,
    int? MaxOutputTokens = null,
    bool RequireJsonObject = false);

public sealed record ContextBuildRequest(
    string SystemPrompt,
    string UserPrompt,
    IReadOnlyList<ChatMessage> History,
    WorkflowDefinition? Workflow,
    IReadOnlyList<DocumentPage> DocumentPages,
    int ContextLength);

public sealed record ContextBuildResult(
    IReadOnlyList<LmChatMessage> Messages,
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
    public const string DefaultSelectedModel = "openai/gpt-oss-20b";
    public const string DefaultSelectedCodingModel = "ud";
    public const string DefaultAccentColor = "#A970FF";
    public const string DefaultBackgroundColor = "#6B6872";
    public const int MaximumRecentActivityTextLength = 180;

    public int Version { get; init; } = 9;
    public bool IsAiConnectionEnabled { get; init; }
    public AiProviderKind AiProvider { get; init; } = AiProviderKind.GoAiServer;
    public string GoAiServerUrl { get; init; } = "https://192.168.0.67:8443";
    public string GoAiProtocolVersion { get; init; } = "1.0";
    public string? GoAiCaFingerprint { get; init; }
    public string? GoAiConnectionName { get; init; } = "GO AI Server";
    public string? LocalToolWorkspacePath { get; init; }
    public string LiveCaptionLanguage { get; init; } = "auto";
    public string LmStudioBaseUrl { get; init; } = "http://127.0.0.1:1234/v1";
    public string? SelectedModel { get; init; } = DefaultSelectedModel;
    public string SelectedCodingModel { get; init; } = DefaultSelectedCodingModel;
    public string ReasoningEffort { get; init; } = "medium";
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
