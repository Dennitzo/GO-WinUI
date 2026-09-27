using System.Text.Json;

namespace GoAi.Contracts;

public enum RunMode
{
    Auto,
    General,
    Coding,
}

public enum ConversationProfile
{
    General,
    Audiobook,
    ContextPreparation,
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
    string? PreferredGeneralModelId = null,
    DocumentContextDescriptor? DocumentContext = null,
    SessionContextDescriptor? SessionContext = null,
    ConversationProfile? ConversationProfile = null,
    string? ReasoningEffort = null,
    string? PreferredCodingModelId = null,
    CodingRunOptions? CodingOptions = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? WorkspacePath = null,
    bool DeepResearch = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ToolDescriptor>? ClientTools = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DeepResearchOptions? ResearchOptions = null);

public enum DeepResearchProfile
{
    Auto,
    Web,
    ScientificEvidence,
    SystematicReview,
    ScopingReview,
    LiteratureUpdate,
    ReplicationAudit,
    OpenProblem,
    MathematicalInvestigation,
}

public static class DeepResearchProfileNames
{
    public static IReadOnlyList<string> All { get; } =
    [
        "auto", "web", "scientificEvidence", "systematicReview", "scopingReview",
        "literatureUpdate", "replicationAudit", "openProblem", "mathematicalInvestigation",
    ];

    public static DeepResearchProfile Parse(string? value) => value switch
    {
        "web" => DeepResearchProfile.Web,
        "scientificEvidence" => DeepResearchProfile.ScientificEvidence,
        "systematicReview" => DeepResearchProfile.SystematicReview,
        "scopingReview" => DeepResearchProfile.ScopingReview,
        "literatureUpdate" => DeepResearchProfile.LiteratureUpdate,
        "replicationAudit" => DeepResearchProfile.ReplicationAudit,
        "openProblem" => DeepResearchProfile.OpenProblem,
        "mathematicalInvestigation" => DeepResearchProfile.MathematicalInvestigation,
        _ => DeepResearchProfile.Auto,
    };

    public static string ToProtocolName(DeepResearchProfile value) =>
        JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
}

public enum ResearchAutonomyLevel
{
    ReadOnlyResearch,
    CodingWorkspaceResearch,
    SandboxResearch,
}

public enum ResearchVerificationLevel
{
    Standard,
    MultiPath,
    FormalWherePossible,
}

public sealed record DeepResearchOptions(
    DeepResearchProfile Profile = DeepResearchProfile.Auto,
    string? ProjectId = null,
    long? ProtocolVersion = null,
    string? ResumeCheckpointId = null,
    ResearchAutonomyLevel AutonomyLevel = ResearchAutonomyLevel.ReadOnlyResearch,
    ResearchVerificationLevel VerificationLevel = ResearchVerificationLevel.MultiPath,
    int? MaximumWorks = null,
    int? MaximumFullTexts = null,
    IReadOnlyList<string>? PreferredLanguages = null,
    DateTimeOffset? UpdateSince = null);

public sealed record CodingRunOptions(
    bool UseWorkingState = true,
    string ReasoningPolicy = "maximum",
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? WorkspacePath = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool ContinueSessionContext = false);

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
    public const string ModelGeneration = "model.generation";
    public const string ModelFallback = "model.fallback";
    public const string ProviderFallback = "provider.fallback";
    public const string ContextChanged = "context.changed";
    public const string TextDelta = "text.delta";
    public const string ReasoningDelta = "reasoning.delta";
    public const string ServerToolStarted = "server_tool.started";
    public const string ServerToolCompleted = "server_tool.completed";
    public const string ClientToolProposed = "client_tool.proposed";
    public const string RunWaitingForClient = "run.waiting_for_client";
    public const string ArtifactCreated = "artifact.created";
    public const string RunCompleted = "run.completed";
    public const string RunFailed = "run.failed";
    public const string RunCancelled = "run.cancelled";
    public const string CodingMetrics = "coding.metrics";
    public const string ResearchProfileSelected = "research.profile.selected";
    public const string ResearchProblemInterpreted = "research.problem.interpreted";
    public const string ResearchProtocolUpdated = "research.protocol.updated";
    public const string ResearchPlanUpdated = "research.plan.updated";
    public const string ResearchSearchCompleted = "research.search.completed";
    public const string ResearchWorkResolved = "research.work.resolved";
    public const string ResearchScreeningUpdated = "research.screening.updated";
    public const string ResearchEvidenceExtracted = "research.evidence.extracted";
    public const string ResearchHypothesisUpdated = "research.hypothesis.updated";
    public const string ResearchDerivationUpdated = "research.derivation.updated";
    public const string ResearchExperimentUpdated = "research.experiment.updated";
    public const string ResearchVerificationUpdated = "research.verification.updated";
    public const string ResearchClaimUpdated = "research.claim.updated";
    public const string ResearchCheckpointCreated = "research.checkpoint.created";
    public const string ResearchReportCompleted = "research.report.completed";
    public const string ResearchWarning = "research.warning";
}

public sealed record ResearchProgressEvent(
    string ProjectId,
    string State,
    long Revision,
    int Completed,
    int Total,
    DateTimeOffset Timestamp);

public sealed record TextDeltaEvent(string Delta, int? ReplaceFrom = null, string? AgentId = null,
    int Round = 0, string Phase = "main", string? State = null);

/// <summary>Provider reasoning is separate from answer text. A new attempt replaces its round/phase at offset zero.</summary>
public sealed record ReasoningDeltaEvent(string Delta, int Round, string Phase = "main", int? ReplaceFrom = null, string? State = null,
    string? AgentId = null);

public sealed record ModelSelectedEvent(string ModelId, string Role, bool IsFallback = false);

public sealed record ModelLoadingEvent(
    string ModelId,
    string State,
    int RequestedContextLength,
    int EffectiveContextLength);

public sealed record ModelGenerationEvent(
    string State,
    string? ToolName = null,
    int? ArgumentCharacters = null,
    double? PromptProgress = null,
    int? PromptTokens = null,
    int? ProcessedPromptTokens = null,
    int? GeneratedTokens = null,
    double? TokensPerSecond = null,
    int? CurrentTokens = null,
    int? Attempt = null,
    string? FailureKind = null,
    bool? ToolArgumentsJsonComplete = null,
    int? ContentCharacters = null,
    bool? FinishObserved = null,
    int? ElapsedSeconds = null,
    int? CachedPromptTokens = null,
    string? Message = null);

/// <summary>Measured values only; unavailable provider counters remain null.</summary>
public sealed record ModelTurnMetrics(
    double? RuntimeQueueMilliseconds = null,
    double? TokenCountingMilliseconds = null,
    double? PromptMilliseconds = null,
    double? GenerationMilliseconds = null,
    double? TimeToFirstTokenMilliseconds = null,
    double? TotalMilliseconds = null,
    int? PromptEvaluatedTokens = null,
    int? CachedPromptTokens = null,
    int? ReasoningTokens = null,
    int? InputTokens = null,
    int? OutputTokens = null);

public sealed record CodingTurnMetricsEvent(
    long Round,
    string Phase,
    ModelTurnMetrics Metrics,
    string? ReasoningEffort = null,
    double? QueueMilliseconds = null);

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
    long InputTokens,
    long OutputTokens,
    IReadOnlyList<string>? ArtifactIds = null);

public sealed record RunFailedEvent(string ErrorCode, string Message, bool Retryable);
