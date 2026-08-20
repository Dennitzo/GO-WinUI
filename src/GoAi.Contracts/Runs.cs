using System.Text.Json;

namespace GoAi.Contracts;

public enum RunMode
{
    Auto,
    General,
    Code,
}

public enum ConversationProfile
{
    General,
    Audiobook,
}

public enum RunState
{
    Queued,
    Running,
    WaitingForClient,
    Completed,
    Failed,
    Cancelled,
    Interrupted,
}

public enum RunWorkloadKind
{
    Conversation,
    ImageGeneration,
    MediaAnalysis,
}

public enum DocumentContextMode
{
    Full,
    Prepared,
}

public sealed record RunRequest(
    string ProtocolVersion,
    RunMode Mode,
    IReadOnlyList<RunMessage> Messages,
    IReadOnlyList<string>? UploadIds = null,
    IReadOnlyList<string>? ArtifactIds = null,
    IReadOnlyList<string>? ClientCapabilities = null,
    RunLimits? Limits = null,
    string? SessionId = null,
    RunWorkload? Workload = null,
    IReadOnlyList<string>? AllowedServerTools = null,
    WorkspaceDescriptor? Workspace = null,
    string? PreferredGeneralModelId = null,
    string? PreferredCodeModelId = null,
    DocumentContextDescriptor? DocumentContext = null,
    SessionContextDescriptor? SessionContext = null,
    ConversationProfile? ConversationProfile = null);

public sealed record DocumentContextDescriptor(
    DocumentContextMode Mode,
    string CorpusRevision,
    int DocumentCount,
    int PageCount,
    int EstimatedTokens,
    int IncludedPageCount,
    bool PreparedByAi = false);

public sealed record SessionContextDescriptor(
    string HistoryRevision,
    int OriginalMessageCount,
    int IncludedMessageCount,
    int EstimatedTokens,
    bool PreparedByAi = false);

public sealed record WorkspaceDescriptor(
    string Name,
    string Fingerprint,
    string Revision,
    string RepositoryMap,
    int FileCount,
    int TextFileCount,
    long TextBytes,
    DateTimeOffset IndexedAt,
    bool IsTruncated = false);

public sealed record RunWorkload(
    RunWorkloadKind Kind,
    string? UploadId = null,
    string? Prompt = null,
    int? Width = null,
    int? Height = null,
    int? Seed = null,
    int? Count = null,
    string? OutputFormat = null,
    int? DurationSeconds = null,
    IReadOnlyDictionary<string, string>? Options = null,
    IReadOnlyList<MediaTimeWindow>? DetailWindows = null);

public sealed record RunMessage(
    string Role,
    IReadOnlyList<ContentPart> Content);

public sealed record ContentPart(
    string Type,
    string? Text = null,
    string? UploadId = null,
    string? ArtifactId = null,
    string? MediaType = null,
    string? FileName = null);

public sealed record RunLimits(
    int? MaximumOutputTokens = null,
    int? MaximumContextTokens = null,
    int? TimeoutSeconds = null);

public sealed record RunAccepted(
    string RunId,
    RunState State,
    DateTimeOffset CreatedAt,
    string EventsUrl);

public sealed record RunSnapshot(
    string RunId,
    RunState State,
    RunMode Mode,
    string? SelectedModel,
    string? SessionTitle,
    long LastEventId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ErrorCode = null);

public sealed record RunEvent(
    long Id,
    string RunId,
    string Type,
    DateTimeOffset CreatedAt,
    JsonElement Data);

public static class RunEventTypes
{
    public const string RunStarted = "run.started";
    public const string QueueChanged = "queue.changed";
    public const string ModelSelected = "model.selected";
    public const string ModelLoading = "model.loading";
    public const string ModelFallback = "model.fallback";
    public const string ProviderFallback = "provider.fallback";
    public const string ContextChanged = "context.changed";
    public const string TextDelta = "text.delta";
    public const string ServerToolStarted = "server_tool.started";
    public const string ServerToolCompleted = "server_tool.completed";
    public const string ClientToolProposed = "client_tool.proposed";
    public const string RunWaitingForClient = "run.waiting_for_client";
    public const string ArtifactCreated = "artifact.created";
    public const string RunCompleted = "run.completed";
    public const string RunFailed = "run.failed";
    public const string RunCancelled = "run.cancelled";
}

public sealed record TextDeltaEvent(string Delta);

public sealed record ModelSelectedEvent(string ModelId, string Role, bool IsFallback = false);

public sealed record ModelLoadingEvent(
    string ModelId,
    string State,
    int RequestedContextLength,
    int EffectiveContextLength);

public sealed record ContextChangedEvent(
    int EstimatedInputTokens,
    int ContextLimit,
    int LoadedFiles,
    bool WasCompacted,
    string? Detail = null,
    string ContextMode = "none",
    int DocumentTokens = 0,
    int DocumentPages = 0,
    bool PreparationCompleted = true,
    int HistoryTokens = 0,
    bool HistoryWasCompacted = false);

public sealed record QueueChangedEvent(int Position, int Waiting, string Lane = "gpu");

public sealed record RunCompletedEvent(
    string? SessionTitle,
    string? ModelId,
    int InputTokens,
    int OutputTokens,
    IReadOnlyList<string>? ArtifactIds = null);

public sealed record RunFailedEvent(string ErrorCode, string Message, bool Retryable);
