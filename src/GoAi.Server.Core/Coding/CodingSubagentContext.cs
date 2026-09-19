using GoAi.Server.Core.Models;
using System.Text.Json;

namespace GoAi.Server.Core.Coding;

/// <summary>Forks the exact immutable parent message prefix; live child work has its own persisted suffix.</summary>
internal static class CodingSubagentContext
{
    internal static IReadOnlyList<LmChatMessage> Fork(IReadOnlyList<LmChatMessage> parent, string task, IReadOnlyList<string> paths)
    {
        var messages = parent.Select(message => message with
        {
            ToolCalls = message.ToolCalls?.Select(call => call with { Arguments = call.Arguments.Clone() }).ToArray(),
        }).ToList();

        // Delegation normally starts inside an unfinished parent tool batch. Retain the
        // full prefix, then close only missing receipts without pretending they ran here.
        var pending = new Dictionary<string, LmToolCall>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message.Role == "assistant" && message.ToolCalls is { } calls)
                foreach (var call in calls) pending[call.Id] = call;
            if (message.Role == "tool" && message.ToolCallId is { } id) pending.Remove(id);
        }
        foreach (var call in pending.Values)
            messages.Add(new("tool", JsonSerializer.Serialize(new
            {
                status = "delegated_context_snapshot",
                message = "Dieser Aufruf gehört dem Hauptagenten. Sein Ergebnis lag beim Kontextabzweig noch nicht vor. Nicht erneut ausführen und nicht als Erfolgsbeleg verwenden.",
            }), ToolCallId: call.Id));

        // Append policy; never rewrite the common prefix merely to change the worker's role.
        messages.Add(new("system", CodingSubagentService.CapabilityPolicy + "\n\n"
            + CodingAgentPolicy.ReasoningLanguagePrompt + "\n\n"
            + "GO_SUBAGENT_ASSIGNMENT: Du bist der Subagent auf GPU1. Der bisherige Verlauf ist gemeinsamer Kontext. "
            + "Bearbeite ausschließlich den folgenden Teilauftrag; erledige nicht automatisch offene Aufgaben des Hauptagenten. "
            + "Keine Unterdelegation. Schreibbereiche: " + (paths.Count == 0 ? "keine" : string.Join(", ", paths))
            + ". Liefere konkrete Änderungen, tatsächliche Werkzeugbelege und offene Einschränkungen auf Deutsch."));
        messages.Add(new("user", task));
        return messages;
    }

    internal static string CacheKey(string? sessionId, string runId, string agentId) =>
        "coding-subagent/" + (string.IsNullOrWhiteSpace(sessionId) ? runId : sessionId) + "/" + agentId;
}
