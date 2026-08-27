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

public sealed record AgentPhaseChangedEvent(
    CodingAgentPhase Phase,
    string Detail,
    int Sequence);

public sealed record AgentActionStartedEvent(
    string ActionId,
    int Sequence,
    string Tool,
    string Operation,
    string? Target);

public sealed record AgentObservationCommittedEvent(
    string ActionId,
    bool Succeeded,
    string? ErrorCode = null,
    string? Message = null);

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

public enum CodingStepKind
{
    Capture,
    ResearchSearch,
    ResearchFetch,
    ResearchSummarize,
    WorkspaceInspect,
    Mutation,
    Verification,
    Finish,
}

public enum CodingStepStatus
{
    Pending,
    Running,
    WaitingForClient,
    Completed,
    Failed,
    Blocked,
}

public sealed record StepContextMetrics(
    int EstimatedInputTokens,
    int MaximumInputTokens,
    int OutputTokens,
    int MaximumOutputTokens);

public sealed record CodingStepSnapshot(
    string StepId,
    int Sequence,
    CodingStepKind Kind,
    CodingStepStatus Status,
    string Goal,
    string? Tool = null,
    string? Target = null,
    int Attempt = 0,
    StepContextMetrics? Context = null,
    string? Detail = null);

public sealed record ResearchBrief(
    string Url,
    string Query,
    string Summary,
    IReadOnlyList<string>? Facts = null,
    IReadOnlyList<string>? Formulas = null,
    IReadOnlyList<string>? Units = null,
    IReadOnlyList<string>? Limitations = null);

public sealed record CodingStepChangedEvent(
    CodingStepSnapshot Step,
    string? Transition = null);
