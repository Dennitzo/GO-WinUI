using GoAi.Contracts;
using GoAi.Server.Core.Models;
using System.Text.Json;

namespace GoAi.Server.Core.Runs;

internal sealed record GeneralSessionContextSnapshot(AgentRunCheckpoint Checkpoint, RunRequest Request, string VisibleResponse,
    bool WasInterrupted = false);

internal static class GeneralSessionContext
{
    internal static bool TryContinue(GeneralSessionContextSnapshot previous, RunRequest request,
        IReadOnlyList<LmChatMessage> initial, out IReadOnlyList<LmChatMessage> messages)
    {
        messages = initial;
        if (!previous.Checkpoint.PreserveSessionPromptPrefix
            || (!previous.WasInterrupted && (previous.Checkpoint.ActiveToolCalls is { Count: > 0 }
                || previous.Checkpoint.PendingProposalId is not null || string.IsNullOrWhiteSpace(previous.VisibleResponse)))
            || request.Mode == RunMode.Coding || previous.Request.Mode == RunMode.Coding
            || string.IsNullOrWhiteSpace(request.SessionId) || request.SessionId != previous.Request.SessionId
            || !SameScope(previous.Request, request)) return false;

        // The client's visible conversation is authoritative. Edited, deleted,
        // compacted or branched history must not silently regain older messages.
        var historical = RunProcessor.CreateInitialMessages(previous.Request, "general", [])
            .Where(message => message.Role != "system").ToArray();
        var current = initial.Where(message => message.Role != "system").ToArray();
        // Stop during reasoning can leave no visible assistant message at all.
        // Do not invent one, and never recover hidden history over a user edit.
        var hasResponse = !string.IsNullOrWhiteSpace(previous.VisibleResponse);
        if (current.Length != historical.Length + (hasResponse ? 2 : 1) || current[^1].Role != "user"
            || (hasResponse && (current[^2].Role != "assistant" || current[^2].Content != previous.VisibleResponse))) return false;
        for (var index = 0; index < historical.Length; index++)
            if (historical[index].Role != current[index].Role || historical[index].Content != current[index].Content) return false;

        var saved = previous.Checkpoint.Messages;
        if (saved.Count == 0 || (!previous.WasInterrupted
            && (saved[^1].Role != "assistant" || saved[^1].ToolCalls is { Count: > 0 }))) return false;
        // Keep the current policy. Everything after the previous system prefix
        // is chronological provider history, including reasoning and tool results.
        var continued = initial.Where(message => message.Role == "system").ToList();
        var pending = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in saved.SkipWhile(message => message.Role == "system"))
        {
            if (message.Role != "tool") ClosePending();
            continued.Add(message);
            foreach (var call in message.ToolCalls ?? []) pending.Add(call.Id);
            if (message.Role == "tool" && message.ToolCallId is { } id) pending.Remove(id);
        }
        ClosePending();
        continued.Add(current[^1]);
        messages = continued;
        return true;

        void ClosePending()
        {
            foreach (var id in pending)
                continued.Add(new LmChatMessage("tool", "{\"status\":\"interrupted\",\"outcomeUnknown\":true,\"message\":\"Historischer Aufruf ohne gespeichertes Ergebnis; Zustand prüfen, nicht automatisch erneut ausführen.\"}", ToolCallId: id));
            pending.Clear();
        }
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
