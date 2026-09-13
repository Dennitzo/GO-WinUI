using GoAi.Contracts;
using System.Text.Json;

namespace GoAi.CodingBenchmarks;

/// <summary>Pure deterministic test data for the qualification formula, never benchmark measurements.</summary>
internal static class BenchmarkSelfChecks
{
    private static readonly string[] RemovedVariants = ["one-specialist", "two-specialists"];

    internal static IReadOnlyList<string> Run()
    {
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Qualification self-check failed: " + name);
            checks.Add(name);
        }
        var schedule = BenchmarkResults.Schedule(BenchmarkVariant.All);
        Check(schedule.Count == 108, "full schedule has 12 × 3 × 3 runs");
        Check(schedule.Select(static item => (item.Repetition, item.Task.Id, item.Variant.Id)).Distinct().Count() == 108, "schedule contains no duplicate fixture execution");
        Check(BenchmarkVariant.All is [{ Id: "reference" }, { Id: "working-state" }, { Id: "adaptive" }],
            "only the three single-agent performance variants remain available");
        Check(schedule.Where(static item => item.Task.Id == "01-add").GroupBy(static item => item.Repetition)
            .Select(static group => string.Join(',', group.Select(static item => item.Variant.Id))).Distinct(StringComparer.Ordinal).Count() == 3,
            "variant order rotates between repetitions");
        var rows = new List<BenchmarkRunResult>();
        foreach (var task in BenchmarkTasks.All)
            for (var repetition = 1; repetition <= 3; repetition++)
            {
                rows.Add(Row(task.Id, "reference", repetition, 2000));
                rows.Add(Row(task.Id, "working-state", repetition, 1000));
                rows.Add(Row(task.Id, "adaptive", repetition, 800));
            }
        Check(BenchmarkResults.Qualify("working-state", rows).Qualified, "working state uses maximum reference baseline");
        var adaptive = BenchmarkResults.Qualify("adaptive", rows);
        Check(adaptive.Qualified && adaptive.Baseline == "working-state" && Math.Abs(adaptive.GlobalMedianSpeedupFraction!.Value - 0.2) < 0.0001,
            "adaptive uses working-state baseline and global median speedup");
        Check(!BenchmarkResults.Qualify("adaptive", rows.Where(static row => row.Repetition == 1).ToArray()).Qualified,
            "incomplete pilot never qualifies");
        Check(!BenchmarkResults.Qualify("adaptive", rows.Select(static row => row.Variant == "adaptive" ? row with { ElapsedMilliseconds = 900 } : row).ToArray()).Qualified,
            "ten percent adaptive gain fails despite faster than legacy reference");
        Check(!BenchmarkResults.Qualify("adaptive", ReplaceFirst(rows, row => row with { OraclePassed = false })).Qualified,
            "one independent oracle failure rejects qualification");
        Check(!BenchmarkResults.Qualify("adaptive", ReplaceFirst(rows, row => row with { Model = "different-model" })).Qualified,
            "model mismatch across variants rejects qualification");
        Check(!BenchmarkResults.Qualify("adaptive", rows.Select(static row => row.Variant == "adaptive" ? row with { Model = "different-model" } : row).ToArray()).Qualified,
            "uniform but different candidate model rejects qualification");
        Check(!BenchmarkResults.Qualify("adaptive", ReplaceFirst(rows, row => row with { RuntimeFingerprint = "different-context-or-slots" })).Qualified,
            "changed runtime context or slot profile rejects qualification");
        Check(!BenchmarkResults.Qualify("adaptive", ReplaceFirst(rows, row => row with { DefinitionFingerprint = "changed-fixture" })).Qualified,
            "changed fixture definitions reject qualification");
        Check(!BenchmarkResults.Qualify("adaptive", ReplaceFirst(rows, row => row with { SourceFingerprint = "changed-server" })).Qualified,
            "changed server or interpreter build rejects qualification");
        Check(!BenchmarkResults.Qualify("adaptive", ReplaceFirst(rows, row => row with { Resumed = true })).Qualified,
            "resumed timing is preserved but not qualified");
        Check(!BenchmarkResults.Qualify("adaptive", ReplaceFirst(rows, row => row with { UncertainExecution = true })).Qualified,
            "uncertain local execution cannot qualify");
        Check(BenchmarkResults.Qualify("adaptive", rows.Select(static row => row.Variant == "adaptive" ? row with { ElapsedMilliseconds = 850 } : row).ToArray()).Qualified,
            "exact fifteen percent improvement qualifies without rounding loss");
        Check(!BenchmarkResults.Qualify("adaptive", rows.Select(static row => row.Variant == "adaptive" ? row with { ElapsedMilliseconds = 850.0001 } : row).ToArray()).Qualified,
            "slightly below fifteen percent does not qualify");
        Check(!BenchmarkResults.Qualify("adaptive", rows.Append(Row("unknown-task", "adaptive", 1, 1)).ToArray()).Qualified,
            "unknown task rows cannot bias the global median");
        foreach (var removed in RemovedVariants)
        {
            var rejected = false;
            try { _ = BenchmarkResults.Qualify(removed, rows); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "removed variant cannot qualify: " + removed);
            rejected = false;
            try
            {
                _ = BenchmarkOptions.Parse(["--verify-fixtures", "--output", Path.GetFullPath("unused-benchmark-self-check"),
                    "--python", typeof(BenchmarkSelfChecks).Assembly.Location, "--variants", "working-state," + removed]);
            }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "removed variant is rejected by the command line: " + removed);
        }
        var taskFixture = BenchmarkTasks.All[0];
        Check(!BenchmarkOracleReceipt.IsPassed(JsonSerializer.SerializeToElement(new { exitCode = 0, stdout = "" }), taskFixture, true),
            "exit zero without the independent oracle report does not pass");
        var oracleJson = JsonSerializer.Serialize(new { oracle = "independent-python-contract-v2", passed = true,
            cases = taskFixture.Cases.Count, failures = Array.Empty<string>() });
        var actualShape = JsonSerializer.SerializeToElement(new { exitCode = 0, stdout = oracleJson });
        Check(BenchmarkOracleReceipt.IsPassed(actualShape, taskFixture, true)
            && !BenchmarkOracleReceipt.IsPassed(actualShape, taskFixture, false), "modified oracle source never passes even with a plausible receipt");
        var unknown = JsonSerializer.Deserialize<ModelTurnMetrics>(JsonSerializer.Serialize(new ModelTurnMetrics(), BenchmarkStorage.JsonLine), BenchmarkStorage.JsonLine)!;
        Check(unknown.InputTokens is null && unknown.ReasoningTokens is null && unknown.PromptMilliseconds is null,
            "missing native counters remain unknown rather than zero");
        Check(BenchmarkTimings.Aggregate([null, null]) is { MeasuredCount: 0, MissingCount: 2, SumMeasured: null, MedianMeasured: null },
            "missing timing aggregates stay unknown");
        return checks;
    }

    private static BenchmarkRunResult[] ReplaceFirst(List<BenchmarkRunResult> rows, Func<BenchmarkRunResult, BenchmarkRunResult> update)
    {
        var result = rows.ToArray();
        var index = Array.FindIndex(result, static row => row.Variant == "adaptive");
        result[index] = update(result[index]);
        return result;
    }

    private static BenchmarkRunResult Row(string task, string variant, int repetition, double duration) => new(
        JobId: task + "-" + variant + "-" + repetition, TaskId: task, Variant: variant, Repetition: repetition,
        Model: "synthetic-unit-model", RunId: "synthetic-unit-run", Completed: true, OraclePassed: true,
        ProtectedFilesUnchanged: true, ActualEdit: true, VerificationExecuted: true, ResearchVerified: true,
        Resumed: false, UncertainExecution: false,
        ElapsedMilliseconds: duration, ToolCalls: 3, Error: null, OracleOutput: "SYNTHETIC QUALIFICATION UNIT DATA ONLY",
        SourceSha256: "synthetic-result-content", ModelTurns: Array.Empty<CodingTurnMetricsEvent>(),
        DefinitionFingerprint: "synthetic-fixture-v1", SourceFingerprint: "synthetic-build-v1", RuntimeFingerprint: "synthetic-runtime-v1");
}
