using GoAi.Contracts;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runs;
using System.Text.Json;

namespace GoAi.Server.Core.Coding;

public static class CodingLoopGuard
{
    public const int MaximumToolResultCharacters = 12_000;

    public static string BoundToolResult(string value)
    {
        if (value.Length <= MaximumToolResultCharacters) return value;
        return JsonSerializer.Serialize(new
        {
            truncated = true,
            originalCharacters = value.Length,
            preview = value[..1_000],
            tail = value[^400..],
            message = "Die Ausgabe wurde begrenzt. Lies gezielt kleinere Ausschnitte oder verwende einen präziseren Suchbegriff.",
        });
    }

    public static void ThrowIfRepeatedFailure(IReadOnlyList<LmChatMessage> messages, LmToolCall candidate)
    {
        var matchingIds = messages.SelectMany(static message => message.ToolCalls ?? [])
            .Where(call => call.Name == candidate.Name && JsonElement.DeepEquals(call.Arguments, candidate.Arguments))
            .Select(static call => call.Id).ToHashSet(StringComparer.Ordinal);
        var failures = messages.Count(message => message.Role == "tool"
            && message.ToolCallId is not null && matchingIds.Contains(message.ToolCallId)
            && IsFailure(message.Content));
        if (failures >= 2)
            throw new AgentRunLimitException($"Coding hat '{candidate.Name}' zweimal mit identischen Argumenten erfolglos aufgerufen. Der Lauf wurde ohne weitere Wiederholung gestoppt.");
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

    private static bool IsFailure(JsonElement value) => value.ValueKind == JsonValueKind.Object
        && (value.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String && status.GetString() is "failed" or "rejected" or "cancelled"
            || value.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False
            || value.TryGetProperty("exitCode", out var exitCode) && exitCode.TryGetInt32(out var code) && code != 0
            || value.TryGetProperty("result", out var result) && IsFailure(result));
}
