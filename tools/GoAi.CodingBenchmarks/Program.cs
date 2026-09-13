using GoAi.Client;
using GoAi.Contracts;
using GoWinUI.Core.Coding;
using System.Text.Json;

namespace GoAi.CodingBenchmarks;

internal sealed record BenchmarkOptions(string Output, string Python, string? Model, Uri Runtime,
    IReadOnlyList<BenchmarkVariant> Variants, IReadOnlyList<string> Tasks, int Repetitions, int TimeoutSeconds,
    bool Resume, bool VerifyFixtures)
{
    internal static BenchmarkOptions Parse(string[] arguments)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Length; index++)
        {
            var name = arguments[index];
            if (name is "--resume" or "--verify-fixtures") { if (!flags.Add(name)) throw new ArgumentException("Duplicate flag: " + name); continue; }
            if (name is not ("--output" or "--python" or "--model" or "--runtime" or "--variants" or "--tasks" or "--repetitions" or "--run-timeout-seconds")
                || index + 1 >= arguments.Length || !fields.TryAdd(name, arguments[++index])) throw new ArgumentException("Unknown, duplicate or incomplete argument: " + name);
        }
        var output = fields.GetValueOrDefault("--output") ?? throw new ArgumentException("--output must name an isolated absolute artifact directory.");
        var python = fields.GetValueOrDefault("--python") ?? throw new ArgumentException("--python must name an existing absolute Python executable.");
        if (!Path.IsPathFullyQualified(output) || !Path.IsPathFullyQualified(python) || !File.Exists(python)) throw new ArgumentException("Absolute output and existing Python paths are required.");
        var runtime = new Uri(fields.GetValueOrDefault("--runtime") ?? "http://127.0.0.1:8081/");
        if (!runtime.IsLoopback || runtime.Scheme != "http" || runtime.UserInfo.Length > 0) throw new ArgumentException("The benchmark runtime must be a local HTTP llama endpoint.");
        var ids = fields.GetValueOrDefault("--variants")?.Split(',') ?? ["working-state"];
        var variants = ids.Select(id => BenchmarkVariant.All.SingleOrDefault(value => value.Id == id) ?? throw new ArgumentException("Unknown variant: " + id)).ToArray();
        if (variants.Length != 1 || variants[0].Id != "working-state")
            throw new ArgumentException("Coding now always uses working-state with maximum reasoning. Other live variants have been removed.");
        var tasks = fields.GetValueOrDefault("--tasks")?.Split(',') ?? BenchmarkTasks.All.Select(static value => value.Id).ToArray();
        if (tasks.Distinct(StringComparer.Ordinal).Count() != tasks.Length || tasks.Any(id => BenchmarkTasks.All.All(value => value.Id != id))) throw new ArgumentException("Unknown or duplicate task ID.");
        var repetitions = int.Parse(fields.GetValueOrDefault("--repetitions") ?? "3", System.Globalization.CultureInfo.InvariantCulture);
        var timeout = int.Parse(fields.GetValueOrDefault("--run-timeout-seconds") ?? "900", System.Globalization.CultureInfo.InvariantCulture);
        if (repetitions is < 1 or > 3 || timeout is < 30 or > 86400) throw new ArgumentException("Repetitions must be 1..3; harness timeout 30..86400 seconds.");
        var verify = flags.Contains("--verify-fixtures");
        var model = fields.GetValueOrDefault("--model");
        if (!verify && (string.IsNullOrWhiteSpace(model) || Environment.GetEnvironmentVariable("GO_AI_CODING_BENCHMARK_LIVE") != "1"))
            throw new ArgumentException("Live inference requires --model and the explicit GO_AI_CODING_BENCHMARK_LIVE=1 opt-in.");
        return new(Path.GetFullPath(output), Path.GetFullPath(python), model, runtime, variants, tasks, repetitions, timeout, flags.Contains("--resume"), verify);
    }
}

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("GO coding benchmark: --output ABS --python ABS --model coding/ID [--variants working-state] [--tasks 01-add] [--repetitions 1] [--resume].\n"
                + "Live opt-in: GO_AI_CODING_BENCHMARK_LIVE=1. Use --verify-fixtures without --model for isolated oracle/qualification checks only.");
            return 0;
        }
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler interrupt = (_, eventArgs) => { eventArgs.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += interrupt;
        try
        {
            var options = BenchmarkOptions.Parse(args);
            BenchmarkStorage.RejectLinkedParents(options.Output);
            if (options.VerifyFixtures) return await VerifyFixturesAsync(options, stop.Token).ConfigureAwait(false);
            return await new BenchmarkRunner(options).RunAsync(stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            Console.Error.WriteLine("Benchmark interrupted. Completed results and pending-run receipts are preserved; restart with the same options and --resume.");
            return 130;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.Error.WriteLine(exception.ToString());
            return 1;
        }
        finally { Console.CancelKeyPress -= interrupt; }
    }

    private static async Task<int> VerifyFixturesAsync(BenchmarkOptions options, CancellationToken token)
    {
        if (Directory.Exists(options.Output)) throw new IOException("Fixture verification requires a new output directory.");
        Directory.CreateDirectory(options.Output);
        var results = new List<object>();
        foreach (var task in BenchmarkTasks.All)
        {
            var brokenDirectory = Path.Combine(options.Output, task.Id + "-broken");
            var correctDirectory = Path.Combine(options.Output, task.Id + "-reference");
            var broken = await BenchmarkStorage.PrepareFixtureAsync(brokenDirectory, task, reference: false, token).ConfigureAwait(false);
            var correct = await BenchmarkStorage.PrepareFixtureAsync(correctDirectory, task, reference: true, token).ConfigureAwait(false);
            var before = await RunOracleAsync(options.Python, brokenDirectory, broken, token).ConfigureAwait(false);
            var after = await RunOracleAsync(options.Python, correctDirectory, correct, token).ConfigureAwait(false);
            var passed = before.GetProperty("exitCode").GetInt32() != 0 && BenchmarkOracleReceipt.IsPassed(after, task,
                BenchmarkStorage.FileDigest(Path.Combine(correctDirectory, "independent_oracle.py")) == BenchmarkStorage.Digest(task.Oracle));
            results.Add(new { task = task.Id, passed, brokenResult = before, referenceResult = after });
            await BenchmarkStorage.SaveAsync(Path.Combine(options.Output, "fixture-checks.json"), results, token).ConfigureAwait(false);
            if (!passed) throw new InvalidOperationException("Fixture/oracle precondition failed: " + task.Id);
        }
        var checks = BenchmarkSelfChecks.Run();
        await BenchmarkStorage.SaveAsync(Path.Combine(options.Output, "qualification-unit-checks.json"), new { syntheticUnitDataOnly = true, checks }, token).ConfigureAwait(false);
        var storageChecks = await BenchmarkStorageSelfChecks.RunAsync(options.Output, token).ConfigureAwait(false);
        await BenchmarkStorage.SaveAsync(Path.Combine(options.Output, "storage-unit-checks.json"), new { modelRequests = 0, checks = storageChecks }, token).ConfigureAwait(false);
        Console.WriteLine("12/12 buggy fixtures rejected and 12/12 reference implementations accepted by independent Python oracles. Qualification/schedule unit checks passed. No model request was made.");
        return 0;
    }

    internal static Task<JsonElement> RunOracleAsync(string python, string directory, string workspace, CancellationToken token) =>
        new LocalCodingToolExecutor(workspace).ExecuteAsync(ClientToolNames.CodingCommand,
            JsonSerializer.SerializeToElement(new { executable = python, arguments = new[] { "-B", Path.Combine(directory, "independent_oracle.py"), workspace }, timeoutSeconds = 30 }), token);
}
