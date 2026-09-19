using GoAi.Contracts;
using System.Diagnostics;
using System.Text.Json;

namespace GoAi.Server.Core.Models;

/// <summary>Provider measurements remain nullable: fragments are never reported as tokens.</summary>
internal sealed class ModelTurnMeasurement(Stopwatch requestClock)
{
    private ModelTurnMetrics _metrics = new();

    public void Observe(JsonElement root)
    {
        if (root.TryGetProperty("prompt_progress", out var progress) && progress.ValueKind == JsonValueKind.Object)
        {
            // Native prefill frames carry real measurements even when an older
            // server omits the final OpenAI usage block. Never infer a cache hit
            // from the requested cache flag or a successfully restored file.
            _metrics = _metrics with
            {
                InputTokens = Integer(progress, "total") ?? _metrics.InputTokens,
                CachedPromptTokens = Integer(progress, "cache") ?? _metrics.CachedPromptTokens,
            };
        }
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            _metrics = _metrics with
            {
                InputTokens = Integer(usage, "prompt_tokens") ?? Integer(usage, "input_tokens") ?? _metrics.InputTokens,
                OutputTokens = Integer(usage, "completion_tokens") ?? Integer(usage, "output_tokens") ?? _metrics.OutputTokens,
                ReasoningTokens = NestedInteger(usage, "completion_tokens_details", "reasoning_tokens") ?? _metrics.ReasoningTokens,
                CachedPromptTokens = NestedInteger(usage, "prompt_tokens_details", "cached_tokens") ?? _metrics.CachedPromptTokens,
            };
        }
        if (root.TryGetProperty("timings", out var timings) && timings.ValueKind == JsonValueKind.Object)
        {
            _metrics = _metrics with
            {
                PromptMilliseconds = Number(timings, "prompt_ms") ?? _metrics.PromptMilliseconds,
                GenerationMilliseconds = Number(timings, "predicted_ms") ?? _metrics.GenerationMilliseconds,
                PromptEvaluatedTokens = Integer(timings, "prompt_n") ?? _metrics.PromptEvaluatedTokens,
            };
        }
    }

    public void ObserveGeneratedFragment()
    {
        _metrics = _metrics with { TimeToFirstTokenMilliseconds = _metrics.TimeToFirstTokenMilliseconds ?? requestClock.Elapsed.TotalMilliseconds };
    }

    public ModelTurnMetrics Build() => _metrics;

    private static int? NestedInteger(JsonElement parent, string name, string field) =>
        parent.TryGetProperty(name, out var nested) && nested.ValueKind == JsonValueKind.Object ? Integer(nested, field) : null;

    private static int? Integer(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= 0 ? number : null;

    private static double? Number(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) && number >= 0 ? number : null;
}
