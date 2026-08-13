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
    string? PendingToolCallId = null);
