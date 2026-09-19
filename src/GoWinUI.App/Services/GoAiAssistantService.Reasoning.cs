using System.Text.Json;
using GoAi.Contracts;
using GoWinUI.Core.Models;

namespace GoWinUI.App.Services;

public sealed partial class GoAiAssistantService
{
    internal const string ReasoningStepTool = "assistant.reasoning";

    // Reasoning is a separate display receipt, never assistant answer content or
    // an executable tool. Its durable event cursor makes reconnect replay idempotent.
    internal static AssistantToolStep? ApplyReasoningDelta(AssistantToolStep? previous,
        ReasoningDeltaEvent update, string id, long eventId, int contentOffset, DateTimeOffset now)
    {
        if (previous?.OutputJson is { } saved)
        {
            try
            {
                using var metadata = JsonDocument.Parse(saved);
                if (metadata.RootElement.ValueKind == JsonValueKind.Object
                    && metadata.RootElement.TryGetProperty("lastEventId", out var cursor)
                    && cursor.ValueKind == JsonValueKind.Number
                    && cursor.TryGetInt64(out var lastEventId) && eventId <= lastEventId) return null;
            }
            catch (JsonException) { /* A damaged old display receipt must not abort model execution. */ }
        }

        var prior = previous?.Detail ?? string.Empty;
        var offset = update.ReplaceFrom is { } replace ? Math.Clamp(replace, 0, prior.Length) : prior.Length;
        var text = prior[..offset] + (update.Delta ?? string.Empty);
        if (previous is null && text.Length == 0) return null;
        // Match the durable receipt limit without silently dropping the overflow.
        if (text.Length > AssistantToolStep.MaximumDetailCharacters)
        {
            const string marker = "\n[Reasoning-Anzeige hat ihre Speichergrenze erreicht.]";
            var end = AssistantToolStep.MaximumDetailCharacters - marker.Length;
            if (char.IsHighSurrogate(text[end - 1])) end--;
            text = text[..end] + marker;
        }
        var status = update.State == "steered" ? "interrupted"
            : update.State is "completed" or "failed" or "cancelled" or "interrupted" ? update.State : "running";
        return new AssistantToolStep(id, ReasoningStepTool, status, text,
            InputJson: JsonSerializer.Serialize(new { round = update.Round, phase = update.Phase }, JsonOptions),
            OutputJson: JsonSerializer.Serialize(new { lastEventId = eventId, state = update.State }, JsonOptions),
            ContentOffset: previous?.ContentOffset ?? contentOffset,
            StartedAt: previous?.StartedAt ?? now,
            CompletedAt: status == "running" ? null : now,
            UpdatedAt: now,
            AgentId: update.AgentId ?? previous?.AgentId);
    }
}
