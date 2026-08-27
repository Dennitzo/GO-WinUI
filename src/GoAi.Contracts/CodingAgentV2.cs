using System.Text.Json;
using System.Text.Json.Serialization;

namespace GoAi.Contracts;

public enum CodingAgentPhase
{
    Orienting,
    Editing,
    Verifying,
    Finishing,
    Blocked,
    Completed,
}

public enum CodingWorkCycleStage
{
    Orienting,
    Researching,
    Implementing,
    Verifying,
    ReadyToFinish,
}

public enum CodingTaskKind
{
    Analysis,
    Change,
    Creation,
    Execution,
    Research,
}

public enum AgentMessagePhase
{
    Commentary,
    [JsonStringEnumMemberName("final_answer")]
    FinalAnswer,
}

public enum AgentMessageOrigin
{
    Model,
    Host,
}

public sealed record WorkspaceFileRef(
    string FileId,
    string Path,
    string Sha256,
    string WorkspaceRevision,
    long Length,
    string Language,
    int? StartLine = null,
    int? EndLine = null);

public sealed record EvidenceRef(
    string EvidenceId,
    string Kind,
    string Summary,
    string? Path = null,
    string? Sha256 = null,
    string? WorkspaceRevision = null,
    DateTimeOffset? CreatedAt = null);

public sealed record AgentActionEnvelope(
    string ActionId,
    int Sequence,
    string Tool,
    string Operation,
    JsonElement Arguments,
    string IdempotencyKey,
    string? WorkspaceRevision,
    DateTimeOffset CreatedAt,
    string? Commentary = null,
    string? CommentaryFingerprint = null);

public sealed record AgentObservation(
    string ActionId,
    bool Succeeded,
    JsonElement Result,
    string ResultHash,
    string? ErrorCode = null,
    string? Message = null,
    string? EvidenceId = null,
    string? WorkspaceRevision = null,
    bool CacheHit = false,
    DateTimeOffset? CreatedAt = null);

public sealed record AgentChangeRecord(
    string Path,
    string? BeforeSha256,
    string? AfterSha256,
    string ActionId,
    string Summary);

public sealed record AgentVerificationRecord(
    string VerificationId,
    string Kind,
    string Target,
    bool Succeeded,
    string ActionId,
    string Summary);

public sealed record AgentResearchRecord(
    string Key,
    string Operation,
    string Target,
    string ActionId,
    string EvidenceId,
    string ResultHash,
    int ChangeGeneration,
    DateTimeOffset? CreatedAt = null,
    IReadOnlyList<string>? SourceUrls = null,
    bool SearchCoverageSatisfied = false,
    string? CoveredSearchKey = null,
    IReadOnlyList<string>? CoveredTerms = null,
    int WorkCycle = 0);

public sealed record TaskLedgerSnapshot(
    string Goal,
    CodingTaskKind TaskKind,
    CodingAgentPhase Phase,
    string WorkspaceRevision,
    IReadOnlyList<string> Constraints,
    IReadOnlyList<EvidenceRef> Evidence,
    IReadOnlyList<AgentChangeRecord> Changes,
    IReadOnlyList<AgentVerificationRecord> Verifications,
    IReadOnlyList<string> Failures,
    string NextStep,
    int ContextEpoch = 0,
    int ConsecutiveNoProgressTurns = 0,
    bool ReorientationUsed = false,
    string? LastCommentaryFingerprint = null,
    string VisibleRunMessage = "",
    int VisibleRunMessageDeltaCount = 0,
    IReadOnlyList<AgentResearchRecord>? Research = null,
    CodingWorkCycleStage WorkCycleStage = CodingWorkCycleStage.Orienting,
    int WorkCycle = 0,
    int VerifiedChangeCount = 0,
    bool ContextGapResearchAllowed = false,
    string? ActiveResearchEvidenceId = null,
    string? ActiveContextGap = null);

public sealed record ProviderConversationState(
    string Transport,
    string? ResponseId,
    string? ModelInstanceId,
    string PrefixCacheKey,
    int ContextEpoch);

public sealed record AgentPhaseChangedEvent(
    CodingAgentPhase Phase,
    string Detail,
    int Sequence,
    string WorkspaceRevision);

public sealed record AgentActionStartedEvent(
    string ActionId,
    int Sequence,
    string Tool,
    string Operation,
    string? Target,
    string WorkspaceRevision);

public sealed record AgentObservationCommittedEvent(
    string ActionId,
    bool Succeeded,
    string? ErrorCode,
    string? EvidenceId,
    bool CacheHit,
    string WorkspaceRevision);

public sealed record AgentCacheChangedEvent(
    string Cache,
    bool Hit,
    string Key,
    string WorkspaceRevision);

public sealed record AgentVerificationChangedEvent(
    string VerificationId,
    string Kind,
    string Target,
    bool Succeeded,
    string ActionId,
    string Summary,
    string WorkspaceRevision);

public sealed record AgentMessageStartedEvent(
    string ItemId,
    string RunId,
    int Sequence,
    AgentMessagePhase Phase,
    AgentMessageOrigin Origin,
    string MilestoneFingerprint);

public sealed record AgentMessageDeltaEvent(
    string ItemId,
    string RunId,
    int Sequence,
    int DeltaIndex,
    string Delta,
    AgentMessagePhase Phase,
    AgentMessageOrigin Origin,
    string MilestoneFingerprint);

public sealed record AgentMessageCompletedEvent(
    string ItemId,
    string RunId,
    int Sequence,
    AgentMessagePhase Phase,
    AgentMessageOrigin Origin,
    string Text,
    string MilestoneFingerprint,
    int DeltaCount);
