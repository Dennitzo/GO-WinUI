using GoAi.Contracts;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runs;
using System.Text.Json;

namespace GoAi.Server.Core.Coding;

public static class CodingLoopGuard
{
    public const int MaximumToolResultCharacters = 12_000;
    private static readonly string[] ReceiptMetadataFields =
    [
        "status", "success", "errorCode", "exitCode", "timedOut", "outcomeUnknown", "applied",
        "sha256", "originalSha256", "evidenceId", "stream", "offset", "nextOffset", "hasMore",
        "storedCharacters", "storedBytes", "truncated", "tool", "provider", "isFallback", "found",
        "path", "nextLine", "startLine", "endLine", "totalLines",
    ];
    private static readonly string[] ReceiptNestedFields = ["result", "evidence"];

    public static string BoundToolResult(string value)
    {
        if (value.Length <= MaximumToolResultCharacters) return value;
        var metadataBudget = 6000;
        var metadataTruncated = false;
        Dictionary<string, object?> bounded = [];
        try
        {
            using var document = JsonDocument.Parse(value);
            bounded = CopyReceiptMetadata(document.RootElement, 0, ref metadataBudget, ref metadataTruncated);
        }
        catch (JsonException) { }
        bounded["truncated"] = true;
        bounded["originalCharacters"] = value.Length;
        bounded["message"] = "Die Ausgabe wurde begrenzt. Nutze vorhandene Beleg-IDs mit coding.readOutput oder lies gezielt kleinere Ausschnitte. Vorschau und Ende sind unvollständiger Ausgabetext.";
        if (metadataTruncated) bounded["metadataTruncated"] = true;
        var previewCharacters = 1000;
        var tailCharacters = 400;
        while (true)
        {
            bounded["preview"] = value[..previewCharacters];
            bounded["tail"] = value[(value.Length - tailCharacters)..];
            var serialized = JsonSerializer.Serialize(bounded);
            if (serialized.Length <= MaximumToolResultCharacters) return serialized;
            previewCharacters /= 2;
            tailCharacters /= 2;
        }
    }

    private static Dictionary<string, object?> CopyReceiptMetadata(JsonElement source, int depth, ref int budget, ref bool truncated)
    {
        Dictionary<string, object?> result = [];
        if (source.ValueKind != JsonValueKind.Object) return result;
        foreach (var name in ReceiptMetadataFields)
        {
            if (!source.TryGetProperty(name, out var field) || field.ValueKind is not
                (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)) continue;
            var serialized = JsonSerializer.Serialize(field);
            var cost = serialized.Length + name.Length + 8;
            if (serialized.Length > (name == "path" ? 2048 : 512) || cost > budget) { truncated = true; continue; }
            result[name] = field.Clone();
            budget -= cost;
        }
        if (depth < 2)
        {
            foreach (var name in ReceiptNestedFields)
            {
                if (!source.TryGetProperty(name, out var nested) || nested.ValueKind != JsonValueKind.Object) continue;
                result[name] = CopyReceiptMetadata(nested, depth + 1, ref budget, ref truncated);
            }
        }
        return result;
    }

    public static void ThrowIfRepeatedFailure(IReadOnlyList<LmChatMessage> messages, LmToolCall candidate, CodingWorkingState? workingState = null)
    {
        if (workingState is not null)
        {
            var argumentHash = CodingWorkingStateReducer.ArgumentHash(candidate.Arguments);
            if (workingState.Failures.Any(failure => failure.Tool == candidate.Name && failure.ArgumentHash == argumentHash
                && failure.EnvironmentRevision == workingState.EnvironmentRevision && failure.Count >= 2))
                throw new AgentRunLimitException($"'{candidate.Name}' wurde im unveränderten Arbeitszustand bereits zweimal mit denselben Argumenten erfolglos aufgerufen. Dieser Aufruf wurde nicht erneut ausgeführt. Prüfe den vorhandenen Fehlerbeleg mit coding.readOutput oder coding.searchRunEvidence, korrigiere Befehl, Pfad oder Umgebung und setze den Nutzerauftrag mit einem anderen Ansatz fort. Der Lauf bleibt aktiv.");
            return;
        }
        var matchingIds = messages.SelectMany(static message => message.ToolCalls ?? [])
            .Where(call => call.Name == candidate.Name && JsonElement.DeepEquals(call.Arguments, candidate.Arguments))
            .Select(static call => call.Id).ToHashSet(StringComparer.Ordinal);
        var failures = messages.Count(message => message.Role == "tool"
            && message.ToolCallId is not null && matchingIds.Contains(message.ToolCallId)
            && IsFailure(message.Content));
        if (failures >= 2)
            throw new AgentRunLimitException($"'{candidate.Name}' wurde bereits zweimal mit identischen Argumenten erfolglos aufgerufen. Dieser Aufruf wurde nicht erneut ausgeführt. Lies den vorhandenen Fehlerbeleg, korrigiere die Argumente oder wähle ein anderes Werkzeug und setze den Nutzerauftrag fort. Der Lauf bleibt aktiv.");
    }

    public static void ThrowIfRenderAlreadyUsed(IReadOnlyList<LmChatMessage> messages, LmToolCall candidate)
    {
        if (candidate.Name != ClientToolNames.CodingRenderHtml) return;
        var renderCalls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            foreach (var call in message.ToolCalls ?? [])
                if (call.Name == ClientToolNames.CodingRenderHtml) renderCalls.Add(call.Id);
            if (message.Role == "tool" && message.ToolCallId is { } completedId && renderCalls.Contains(completedId))
                throw new ArgumentException("coding.renderHtml darf höchstens einmal pro Lauf ausgeführt werden; die vorhandene Vorschau bleibt erhalten.");
        }
    }

    private static bool IsFailure(string? content)
    {
        if (string.IsNullOrEmpty(content)) return false;
        try
        {
            using var document = JsonDocument.Parse(content);
            return IsFailure(document.RootElement);
        }
        catch (JsonException) { return false; }
    }

    private static bool IsFailure(JsonElement value) => CodingWorkingStateReducer.IsFailedResult(value);
}
