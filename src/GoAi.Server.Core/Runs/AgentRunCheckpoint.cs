using GoAi.Server.Core.Models;
using GoAi.Server.Core.Coding;

namespace GoAi.Server.Core.Runs;

public sealed record AgentRunCheckpoint(
    IReadOnlyList<LmChatMessage> Messages,
    long RoundCount,
    long ToolCallCount,
    long InputTokens,
    long OutputTokens,
    IReadOnlyList<LmToolCall>? ActiveToolCalls = null,
    int NextToolIndex = 0,
    string? PendingProposalId = null,
    string? PendingToolCallId = null,
    string? SelectedToolName = null,
    int RequiredToolCallRetryCount = 0,
    bool BudgetWarningIssued = false,
    bool HtmlRenderUsed = false,
    long CompactionCount = 0,
    int? VisibleTextLength = null,
    long? StreamingTurnStartEventId = null,
    CodingWorkingState? WorkingState = null,
    long? ActiveCallRound = null,
    bool ActiveCallsReadOnly = false,
    int EmptyResponseRetryCount = 0,
    int IncompleteResponseRetryCount = 0,
    bool PreserveSessionPromptPrefix = false,
    bool WorkingStatePromptIncluded = false,
    long AppliedSteeringSequence = 0,
    bool DeepResearchCompleted = false);
