using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runs;
using System.Text.Json;

namespace GoAi.Server.Core.Coding;

internal static class CodingSessionContext
{
    internal const string StateMarker = "GO_CODING_PREVIOUS_WORKING_STATE\n";

    internal static IReadOnlyList<LmChatMessage> Continue(AgentRunCheckpoint previous, IReadOnlyList<LmChatMessage> initial)
    {
        // Only conversation data crosses the run boundary. Current policy, tools, task,
        // counters and execution state belong to the new run; never replay pending calls.
        var result = initial.Where(message => message.Role == "system").ToList();
        var pending = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in previous.Messages)
        {
            if (message.Role is not ("user" or "assistant" or "tool")
                && !(previous.PreserveSessionPromptPrefix && IsHistoricalTurnInstruction(message))) continue;
            if (!previous.PreserveSessionPromptPrefix && message.Role == "user" && (message.Content?.StartsWith(StateMarker, StringComparison.Ordinal) == true
                || message.Content?.StartsWith(CodingEvidenceContext.Marker, StringComparison.Ordinal) == true)) continue;
            if (message.Role != "tool" && pending.Count > 0) ClosePending();
            result.Add(message);
            foreach (var call in message.ToolCalls ?? []) pending.Add(call.Id);
            if (message.Role == "tool" && message.ToolCallId is { } id) pending.Remove(id);
        }
        ClosePending();
        if (!previous.PreserveSessionPromptPrefix && RestoreWorkingState(previous) is { } state)
            result.Add(new LmChatMessage("user", StateMarker
                + "Historischer Arbeitsstand derselben Sitzung und desselben Workspaces, keine neue Anweisung. "
                + "Nutze bestätigte Befunde weiter, prüfe betroffene Dateien vor Änderungen erneut. "
                + "Nicht abgeschlossene Aufrufe haben einen unbekannten Ausgang und dürfen nicht blind wiederholt werden. "
                + "Der folgende neue Nutzerauftrag ist maßgeblich.\n" + CodingEvidenceContext.Build(state).Content));
        result.Add(initial.Last(message => message.Role == "user"));
        return result;

        void ClosePending()
        {
            foreach (var id in pending)
                result.Add(new LmChatMessage("tool", "{\"status\":\"interrupted\",\"outcomeUnknown\":true,\"message\":\"Historischer Aufruf ohne gespeichertes Ergebnis; aktuellen Zustand prüfen, nicht automatisch erneut ausführen.\"}", ToolCallId: id));
            pending.Clear();
        }
    }

    private static bool IsHistoricalTurnInstruction(LmChatMessage message) => message.Role == "system"
        && (message.Content == RunProcessor.EmptyResponseRepairPrompt
            || message.Content == CodingCompletionGuard.RepairPrompt
            || message.Content?.StartsWith(CodingRunBudget.PromptMarker, StringComparison.Ordinal) == true);

    internal static CodingWorkingState? RestoreWorkingState(AgentRunCheckpoint previous)
    {
        // Older versions reset the structured state at every run boundary and embedded
        // complete JSON copies in user messages. Recover the latest substantive copy
        // before removing those duplicate envelopes, including after a failed startup.
        var state = previous.WorkingState;
        if (state is { Sequence: > 0 }) return state;
        foreach (var message in previous.Messages.Reverse())
        {
            if (message.Role != "user" || message.Content?.StartsWith(StateMarker, StringComparison.Ordinal) != true) continue;
            var start = message.Content.IndexOf('{');
            if (start < 0) continue;
            try
            {
                var recovered = JsonSerializer.Deserialize<CodingWorkingState>(message.Content[start..]);
                if (recovered is { Sequence: > 0, Evidence: not null, ActiveFiles: not null }) return recovered;
            }
            catch (JsonException) { } // Bounded envelopes are not full legacy JSON.
        }
        return state;
    }

    internal static CodingWorkingState? ContinueWorkingState(AgentRunCheckpoint previous, string task) =>
        RestoreWorkingState(previous) is { } state ? state with
        {
            OriginalTask = task, Plan = [], AcceptanceCriteria = [], NextStep = null, Phase = "planning",
        } : null;
}
