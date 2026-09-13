using GoAi.Contracts;

namespace GoAi.CodingBenchmarks;

internal sealed record BenchmarkToolStart(string Tool, DateTimeOffset At);
internal sealed record BenchmarkToolTiming(string Id, string Tool, string Scope, DateTimeOffset? StartedAt,
    DateTimeOffset CompletedAt, double? WallMilliseconds, double? ActualProcessMilliseconds);
internal sealed record BenchmarkMeasuredAggregate(int MeasuredCount, int MissingCount, double? SumMeasured, double? MedianMeasured);

internal static class BenchmarkTimings
{
    internal static object Summarize(IReadOnlyList<CodingTurnMetricsEvent> model, IReadOnlyList<BenchmarkToolTiming> tools) => new
    {
        note = "SumMeasured covers available observations only; missing native counters stay unknown. Client wall time includes SSE transport, fixture dispatch/wait and HTTP result acknowledgment. Server wall time is started-to-completed event time. Command time is the actual executor receipt, separate from transport.",
        runtimeQueueMilliseconds = Aggregate(model.Select(static turn => turn.Metrics.RuntimeQueueMilliseconds)),
        gatewayGpuQueueMilliseconds = Aggregate(model.Select(static turn => turn.QueueMilliseconds)),
        modelRequestTotalMilliseconds = Aggregate(model.Select(static turn => turn.Metrics.TotalMilliseconds)),
        contextCompactionMilliseconds = Aggregate(model.Where(static turn => turn.Phase == "summarization")
            .Select(static turn => turn.Metrics.TotalMilliseconds)),
        agentModelMilliseconds = Aggregate(model.Where(static turn => turn.Phase != "summarization")
            .Select(static turn => turn.Metrics.TotalMilliseconds)),
        tokenCountingMilliseconds = Aggregate(model.Select(static turn => turn.Metrics.TokenCountingMilliseconds)),
        promptMilliseconds = Aggregate(model.Select(static turn => turn.Metrics.PromptMilliseconds)),
        generationMilliseconds = Aggregate(model.Select(static turn => turn.Metrics.GenerationMilliseconds)),
        timeToFirstTokenMilliseconds = Aggregate(model.Select(static turn => turn.Metrics.TimeToFirstTokenMilliseconds)),
        inputTokens = Aggregate(model.Select(static turn => (double?)turn.Metrics.InputTokens)),
        cachedPromptTokens = Aggregate(model.Select(static turn => (double?)turn.Metrics.CachedPromptTokens)),
        reasoningTokens = Aggregate(model.Select(static turn => (double?)turn.Metrics.ReasoningTokens)),
        outputTokens = Aggregate(model.Select(static turn => (double?)turn.Metrics.OutputTokens)),
        serverToolWallMilliseconds = Aggregate(tools.Where(static tool => tool.Scope == "server-events").Select(static tool => tool.WallMilliseconds)),
        clientToolWallMilliseconds = Aggregate(tools.Where(static tool => tool.Scope == "client-proposal-to-http-ack").Select(static tool => tool.WallMilliseconds)),
        actualProcessMilliseconds = Aggregate(tools.Where(static tool => tool.Tool == ClientToolNames.CodingCommand).Select(static tool => tool.ActualProcessMilliseconds)),
        modelTurns = model,
        toolCalls = tools,
    };

    internal static BenchmarkMeasuredAggregate Aggregate(IEnumerable<double?> values)
    {
        var all = values.ToArray();
        var measured = all.Where(static value => value is not null && double.IsFinite(value.Value)).Select(static value => value!.Value).ToArray();
        return new(measured.Length, all.Length - measured.Length, measured.Length > 0 ? measured.Sum() : null,
            measured.Length > 0 ? BenchmarkResults.Median(measured) : null);
    }
}
