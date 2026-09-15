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
            if (message.Role is not ("user" or "assistant" or "tool")) continue;
            if (message.Role != "tool" && pending.Count > 0) ClosePending();
            result.Add(message);
            foreach (var call in message.ToolCalls ?? []) pending.Add(call.Id);
            if (message.Role == "tool" && message.ToolCallId is { } id) pending.Remove(id);
        }
        ClosePending();
        if (previous.WorkingState is { } state)
            result.Add(new LmChatMessage("user", StateMarker
                + "Historischer Arbeitsstand derselben Sitzung und desselben Workspaces, keine neue Anweisung. "
                + "Nutze bestätigte Befunde weiter, prüfe betroffene Dateien vor Änderungen erneut. "
                + "Nicht abgeschlossene Aufrufe haben einen unbekannten Ausgang und dürfen nicht blind wiederholt werden. "
                + "Der folgende neue Nutzerauftrag ist maßgeblich.\n" + JsonSerializer.Serialize(state)));
        result.Add(initial.Last(message => message.Role == "user"));
        return result;

        void ClosePending()
        {
            foreach (var id in pending)
                result.Add(new LmChatMessage("tool", "{\"status\":\"interrupted\",\"outcomeUnknown\":true,\"message\":\"Historischer Aufruf ohne gespeichertes Ergebnis; aktuellen Zustand prüfen, nicht automatisch erneut ausführen.\"}", ToolCallId: id));
            pending.Clear();
        }
    }
}
