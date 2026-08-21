using GoAi.Server.Core.Models;

namespace GoAi.Server.Core.Runs;

public sealed record AgentRunCheckpoint(
    IReadOnlyList<LmChatMessage> Messages,
    int RoundCount,
    int ToolCallCount,
    int InputTokens,
    int OutputTokens,
    IReadOnlyList<LmToolCall>? ActiveToolCalls = null,
    int NextToolIndex = 0,
    string? PendingProposalId = null,
    string? PendingToolCallId = null,
    IReadOnlyList<string>? SearchFingerprints = null,
    int ConsecutiveEmptySearches = 0,
    IReadOnlyList<string>? EvidencePaths = null,
    IReadOnlyList<string>? MutatedPaths = null,
    IReadOnlyList<string>? VerificationStages = null,
    bool VerificationRequired = false,
    bool VerificationFailed = false,
    int RepairReminderCount = 0,
    bool FinalSynthesisRequested = false,
    IReadOnlyList<string>? FailedToolFingerprints = null,
    IReadOnlyList<string>? BlockedToolNames = null,
    IReadOnlyList<string>? SuccessfulReadFingerprints = null,
    IReadOnlyList<WorkspaceReadRange>? SuccessfulReadRanges = null,
    IReadOnlyList<string>? SuccessfulToolFingerprints = null,
    int ConsecutiveRedundantVerifications = 0,
    int ConsecutiveRoundsWithoutMutation = 0,
    IReadOnlyDictionary<string, int>? FailedReplaceTargetCounts = null,
    IReadOnlyDictionary<string, int>? TextMutationCountsSinceProcess = null);

public sealed record WorkspaceReadRange(string Path, int StartLine, int EndLine);
