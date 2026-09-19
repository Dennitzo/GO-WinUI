using GoAi.Contracts;
using GoAi.Server.Core.Models;
using System.Text.Json;

namespace GoAi.Server.Core.Runs;

internal sealed record GeneralSessionContextSnapshot(AgentRunCheckpoint Checkpoint, RunRequest Request, string VisibleResponse);

internal static class GeneralSessionContext
{
    internal static bool TryContinue(GeneralSessionContextSnapshot previous, RunRequest request,
        IReadOnlyList<LmChatMessage> initial, out IReadOnlyList<LmChatMessage> messages)
    {
        messages = initial;
        if (!previous.Checkpoint.PreserveSessionPromptPrefix || previous.Checkpoint.ActiveToolCalls is { Count: > 0 }
            || previous.Checkpoint.PendingProposalId is not null || string.IsNullOrEmpty(previous.VisibleResponse)
            || request.Mode == RunMode.Coding || previous.Request.Mode == RunMode.Coding
            || string.IsNullOrWhiteSpace(request.SessionId) || request.SessionId != previous.Request.SessionId
            || !SameScope(previous.Request, request)) return false;

        // The client's visible conversation is authoritative. Edited, deleted,
        // compacted or branched history must not silently regain older messages.
        var historical = RunProcessor.CreateInitialMessages(previous.Request, "general", [])
            .Where(message => message.Role != "system").ToArray();
        var current = initial.Where(message => message.Role != "system").ToArray();
        if (current.Length != historical.Length + 2 || current[^1].Role != "user"
            || current[^2].Role != "assistant" || current[^2].Content != previous.VisibleResponse) return false;
        for (var index = 0; index < historical.Length; index++)
            if (historical[index].Role != current[index].Role || historical[index].Content != current[index].Content) return false;

        var saved = previous.Checkpoint.Messages;
        if (saved.Count == 0 || saved[^1].Role != "assistant" || saved[^1].ToolCalls is { Count: > 0 }) return false;
        // Keep the current policy. Everything after the previous system prefix
        // is chronological provider history, including reasoning and tool results.
        messages = [.. initial.Where(message => message.Role == "system"),
            .. saved.SkipWhile(message => message.Role == "system"), current[^1]];
        return true;
    }

    private static bool SameScope(RunRequest previous, RunRequest current) =>
        (previous.ConversationProfile ?? ConversationProfile.General) == (current.ConversationProfile ?? ConversationProfile.General)
        && previous.SessionContext?.PreparedByAi != true && current.SessionContext?.PreparedByAi != true
        && Same(previous.DocumentContext, current.DocumentContext)
        && Same(previous.UploadIds, current.UploadIds) && Same(previous.ArtifactIds, current.ArtifactIds)
        && Same(previous.AllowedServerTools, current.AllowedServerTools)
        && Same(previous.ClientCapabilities, current.ClientCapabilities);

    private static bool Same<T>(T previous, T current) => JsonSerializer.Serialize(previous) == JsonSerializer.Serialize(current);
}
