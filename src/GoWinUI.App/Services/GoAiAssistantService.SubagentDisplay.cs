using System.Text.Json;
using GoAi.Contracts;
using GoWinUI.Core.Models;

namespace GoWinUI.App.Services;

public sealed partial class GoAiAssistantService
{
    internal static string? SubagentDisplayStepId(RunEvent item)
    {
        var agentId = StringProperty(item.Data, "agentId");
        if (string.IsNullOrWhiteSpace(agentId)) return null;
        var category = item.Type switch
        {
            RunEventTypes.ReasoningDelta => "reasoning",
            RunEventTypes.TextDelta => "narration",
            RunEventTypes.ModelGeneration or RunEventTypes.ContextChanged or RunEventTypes.CodingMetrics => "progress",
            _ => null,
        };
        if (category is null) return null;
        var round = item.Data.TryGetProperty("round", out var value) && value.TryGetInt32(out var number) ? number : 0;
        return $"child-{category}-{agentId}-{StringProperty(item.Data, "phase") ?? "subagent"}-{round}";
    }

    // Each child's stream has independent text, replay cursor and measurements.
    // Nothing here changes the parent's answer or token-progress accumulator.
    internal static AssistantToolStep? ApplySubagentDisplayEvent(AssistantToolStep? previous,
        RunEvent item, string id, int contentOffset, DateTimeOffset now)
    {
        var agentId = StringProperty(item.Data, "agentId");
        if (string.IsNullOrWhiteSpace(agentId)) return null;
        if (item.Type is RunEventTypes.ReasoningDelta or RunEventTypes.TextDelta)
        {
            var delta = item.Data.Deserialize<ReasoningDeltaEvent>(JsonOptions);
            if (delta is null) return null;
            var step = ApplyReasoningDelta(previous, delta with { AgentId = agentId }, id, item.Id, contentOffset, now);
            return step is null ? null : step with
            {
                Tool = item.Type == RunEventTypes.ReasoningDelta ? ReasoningStepTool : "assistant.narration",
            };
        }

        var saved = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (previous?.OutputJson is { } json)
        {
            try { saved = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, JsonOptions) ?? saved; }
            catch (JsonException) { }
        }
        if (saved.TryGetValue("lastEventId", out var cursor) && cursor.TryGetInt64(out var last) && item.Id <= last)
            return null;
        var part = item.Type switch
        {
            RunEventTypes.ModelGeneration => "generation",
            RunEventTypes.ContextChanged => "context",
            RunEventTypes.CodingMetrics => "metrics",
            _ => null,
        };
        if (part is null) return null;
        var values = saved.TryGetValue(part, out var priorPart) && priorPart.ValueKind == JsonValueKind.Object
            ? priorPart.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone())
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in item.Data.EnumerateObject())
            if (property.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) values[property.Name] = property.Value.Clone();
        saved[part] = JsonSerializer.SerializeToElement(values, JsonOptions);
        saved["lastEventId"] = JsonSerializer.SerializeToElement(item.Id);
        var state = StringProperty(item.Data, "state");
        var completed = item.Type == RunEventTypes.CodingMetrics || state is "generationCompleted" or "completed" or "failed" or "cancelled" or "interrupted" or "steered";
        var detail = item.Type switch
        {
            RunEventTypes.ContextChanged => "Subagent-Kontext bereit",
            RunEventTypes.CodingMetrics => "Subagent-Modellrunde abgeschlossen",
            _ => state switch
            {
                "codingLoading" => "Subagent-Modell wird geladen",
                "codingCompacting" => "Subagent-Kontext wird verdichtet",
                "providerRetryWaiting" => "Subagent wartet auf das lokale Modell",
                "generationCompleted" or "completed" => "Subagent-Modellrunde abgeschlossen",
                "failed" => "Subagent fehlgeschlagen",
                "cancelled" => "Subagent abgebrochen",
                "interrupted" => "Subagent unterbrochen",
                "steered" => "Umgeleitet",
                _ => "Subagent generiert",
            },
        };
        return new AssistantToolStep(id, "assistant.progress", state == "steered" ? "interrupted"
            : completed ? state is "failed" or "cancelled" or "interrupted" ? state : "completed" : "running",
            detail, InputJson: JsonSerializer.Serialize(new
            {
                round = item.Data.TryGetProperty("round", out var round) ? round.GetInt32() : 0,
                phase = StringProperty(item.Data, "phase") ?? "subagent",
            }, JsonOptions), OutputJson: JsonSerializer.Serialize(saved, JsonOptions),
            ContentOffset: previous?.ContentOffset ?? contentOffset, StartedAt: previous?.StartedAt ?? now,
            CompletedAt: completed ? now : null, UpdatedAt: now, AgentId: agentId);
    }
}
