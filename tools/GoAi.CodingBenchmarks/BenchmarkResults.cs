using GoAi.Contracts;

namespace GoAi.CodingBenchmarks;

internal sealed record BenchmarkVariant(string Id, CodingRunOptions Options)
{
    internal static readonly IReadOnlyList<BenchmarkVariant> All =
    [
        new("reference", new(false, "maximum")),
        new("working-state", new(true, "maximum")),
        new("adaptive", new(true, "adaptive")),
    ];
}

internal sealed record BenchmarkRunResult(string JobId, string TaskId, string Variant, int Repetition,
    string Model, string RunId, bool Completed, bool OraclePassed, bool ProtectedFilesUnchanged,
    bool ActualEdit, bool VerificationExecuted, bool ResearchVerified,
    bool Resumed, bool UncertainExecution, double ElapsedMilliseconds,
    int ToolCalls, string? Error, string OracleOutput, string SourceSha256,
    IReadOnlyList<CodingTurnMetricsEvent> ModelTurns, string DefinitionFingerprint, string SourceFingerprint,
    string? RuntimeFingerprint, bool SearchEvidence = true, bool OutputReadEvidence = true)
{
    public bool QualityPassed => Completed && OraclePassed && ProtectedFilesUnchanged && ActualEdit && VerificationExecuted
        && ResearchVerified && !UncertainExecution && SearchEvidence && OutputReadEvidence;
}

internal sealed record BenchmarkTaskComparison(string Task, bool Complete, bool NoQualityRegression,
    double? ReferenceMedianMilliseconds, double? CandidateMedianMilliseconds, double? SpeedupFraction);
internal sealed record BenchmarkQualification(string Variant, string Baseline, bool Complete, bool NoQualityRegression,
    double? BaselineMedianMilliseconds, double? CandidateMedianMilliseconds, double? GlobalMedianSpeedupFraction,
    double? MedianTaskSpeedupFraction, bool Qualified, string Rule, IReadOnlyList<BenchmarkTaskComparison> Tasks);

internal static class BenchmarkResults
{
    internal const int Repetitions = 3;
    internal const string Rule = "All 12 tasks × 3 repetitions complete with passing independent oracles, preserved fixtures, real tool evidence, identical model/definition/source/runtime fingerprints and no resumed/uncertain timing samples; global median run time improves >= 15%. working-state compares with reference; adaptive compares with working-state. Paired task medians are reported separately.";

    internal static IReadOnlyList<(int Repetition, BenchmarkTask Task, BenchmarkVariant Variant)> Schedule(IReadOnlyList<BenchmarkVariant> variants)
    {
        var result = new List<(int, BenchmarkTask, BenchmarkVariant)>();
        for (var repetition = 0; repetition < Repetitions; repetition++)
            foreach (var task in BenchmarkTasks.All)
            {
                var taskIndex = BenchmarkTasks.All.ToList().IndexOf(task);
                var order = Enumerable.Range(0, variants.Count).Select(index => variants[(index + repetition + taskIndex) % variants.Count]).ToArray();
                if ((taskIndex + repetition) % 2 == 1) Array.Reverse(order);
                foreach (var variant in order) result.Add((repetition + 1, task, variant));
            }
        return result;
    }

    internal static BenchmarkQualification Qualify(string candidate, IReadOnlyList<BenchmarkRunResult> results)
    {
        if (candidate is not ("working-state" or "adaptive"))
            throw new ArgumentException("Only working-state and adaptive are supported qualification candidates.", nameof(candidate));
        var baseline = candidate == "working-state" ? "reference" : "working-state";
        var comparisons = BenchmarkTasks.All.Select(task =>
        {
            var reference = results.Where(item => item.TaskId == task.Id && item.Variant == baseline).ToArray();
            var compared = results.Where(item => item.TaskId == task.Id && item.Variant == candidate).ToArray();
            var complete = ExactRepetitions(reference) && ExactRepetitions(compared);
            var quality = complete && SameExperiment(reference.Concat(compared)) && reference.All(static item => item.QualityPassed && !item.Resumed)
                && compared.All(static item => item.QualityPassed && !item.Resumed);
            double? before = complete ? Median(reference.Select(static item => item.ElapsedMilliseconds)) : null;
            double? after = complete ? Median(compared.Select(static item => item.ElapsedMilliseconds)) : null;
            return new BenchmarkTaskComparison(task.Id, complete, quality, before, after, before > 0 ? 1 - after / before : null);
        }).ToArray();
        var experimentRows = results.Where(item => item.Variant == baseline || item.Variant == candidate).ToArray();
        var complete = comparisons.All(static item => item.Complete) && experimentRows.Length == BenchmarkTasks.All.Count * Repetitions * 2
            && experimentRows.All(row => BenchmarkTasks.All.Any(task => task.Id == row.TaskId));
        var quality = comparisons.All(static item => item.NoQualityRegression) && SameExperiment(experimentRows);
        double? speedup = complete ? Median(comparisons.Select(static item => item.SpeedupFraction ?? double.NegativeInfinity)) : null;
        double? beforeMedian = complete ? Median(experimentRows.Where(item => item.Variant == baseline).Select(static item => item.ElapsedMilliseconds)) : null;
        double? afterMedian = complete ? Median(experimentRows.Where(item => item.Variant == candidate).Select(static item => item.ElapsedMilliseconds)) : null;
        double? globalSpeedup = beforeMedian > 0 ? 1 - afterMedian / beforeMedian : null;
        return new(candidate, baseline, complete, quality, beforeMedian, afterMedian, globalSpeedup, speedup,
            complete && quality && afterMedian <= beforeMedian * 0.85, Rule, comparisons);
    }

    private static bool ExactRepetitions(BenchmarkRunResult[] rows) => rows.Length == Repetitions
        && rows.Select(static item => item.Repetition).Order().SequenceEqual(Enumerable.Range(1, Repetitions))
        && rows.Select(static item => item.Model).Distinct(StringComparer.Ordinal).Count() == 1
        && rows.All(static item => double.IsFinite(item.ElapsedMilliseconds) && item.ElapsedMilliseconds > 0);

    private static bool SameExperiment(IEnumerable<BenchmarkRunResult> rows)
    {
        var materialized = rows.ToArray();
        return materialized.Length > 0 && materialized.All(static row => !string.IsNullOrWhiteSpace(row.RuntimeFingerprint)
            && !string.IsNullOrWhiteSpace(row.DefinitionFingerprint) && !string.IsNullOrWhiteSpace(row.SourceFingerprint))
            && materialized.Select(static row => (row.Model, row.DefinitionFingerprint, row.SourceFingerprint, row.RuntimeFingerprint)).Distinct().Count() == 1;
    }

    internal static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0) throw new ArgumentException("No measured values were supplied.", nameof(values));
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
    }
}
