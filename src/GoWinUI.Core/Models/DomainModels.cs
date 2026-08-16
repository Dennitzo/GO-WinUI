using System.Collections.ObjectModel;

namespace GoWinUI.Core.Models;

public enum ChatRole { System, User, Assistant }
public enum MessageStatus { Pending, Streaming, Completed, Cancelled, Failed, Interrupted }
public enum AssistantMode { General, Code }
public enum ProjectStatus { Active, Archived }
public enum AssetCategory { Pdf, Drawing, Image, Meeting, Other }
public enum AppTheme { System, Light, Dark }
public enum WindowDisplayState { Normal, Maximized }

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
    DateTimeOffset? PinnedAt = null);

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
    string? ContextSummary = null);

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
    public const string DefaultAccentColor = "#A970FF";
    public const string DefaultBackgroundColor = "#6B6872";
    public const int MaximumRecentActivityTextLength = 180;

    public int Version { get; init; } = 6;
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
