using GoAi.Client;
using GoAi.Contracts;
using GoWinUI.Core.Coding;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GoAi.CodingBenchmarks;

internal sealed record BenchmarkManifest(int Version, string Model, string Python, string Runtime, string DefinitionSha256, string SourceSha256,
    IReadOnlyList<string> Variants, IReadOnlyList<string> Tasks, int Repetitions, int HarnessTimeoutSeconds,
    string ResearchMode, string ResearchUrl, DateTimeOffset CreatedAt);

internal sealed class BenchmarkJob
{
    public string? RunId { get; set; }
    public Guid SessionId { get; set; } = Guid.NewGuid();
    public long Cursor { get; set; }
    public double ElapsedMilliseconds { get; set; }
    public bool Resumed { get; set; }
    public bool UncertainExecution { get; set; }
    public bool ActualEdit { get; set; }
    public bool SourceRead { get; set; }
    public bool VerificationExecuted { get; set; }
    public bool SearchEvidence { get; set; }
    public bool OutputReadEvidence { get; set; }
    public bool ResearchVerified { get; set; }
    public bool Completed { get; set; }
    public bool Terminal { get; set; }
    public int ToolCalls { get; set; }
    public string? Error { get; set; }
    public HashSet<string> ProcessedProposals { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> ReadSourcePaths { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> DiagnosticEvidenceIds { get; set; } = new(StringComparer.Ordinal);
    public List<CodingTurnMetricsEvent> ModelTurns { get; set; } = [];
    public Dictionary<string, BenchmarkToolStart> ToolStarts { get; set; } = new(StringComparer.Ordinal);
    public List<BenchmarkToolTiming> ToolTimings { get; set; } = [];
}

internal sealed class BenchmarkRunner(BenchmarkOptions options)
{
    private static readonly JsonSerializerOptions ProtocolJson = GoAiProtocol.CreateJsonOptions();
    private static readonly string[] VerificationArguments = ["-B", "-m", "unittest", "-v"];
    private static readonly string[] DiagnosticArguments = ["-B", "diagnose.py"];
    private static readonly string[] ResearchTools = ["web.search", "web.fetch"];
    private static readonly string[] FileTools = ["coding.list", "coding.search", "coding.read", "coding.edit", "coding.write"];
    private static readonly string[] RuntimePropertyNames = ["model_path", "total_slots", "default_generation_settings", "chat_template", "build_info"];
    private string _definitionFingerprint = string.Empty;
    private string _sourceFingerprint = string.Empty;

    internal async Task<int> RunAsync(CancellationToken stop)
    {
        PrepareManifest();
        using var ownership = new FileStream(Path.Combine(options.Output, ".benchmark.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        using var guardedStop = CancellationTokenSource.CreateLinkedTokenSource(stop);
        await using var conflictGuard = await BenchmarkConflictGuard.StartAsync(options.Output, options.Runtime, guardedStop).ConfigureAwait(false);
        stop = guardedStop.Token;
        // No native runtime launch or model replacement occurs in this host setup.
        await using var host = await BenchmarkHost.StartAsync(options.Output, options.Runtime, stop).ConfigureAwait(false);
        using var http = new HttpClient { BaseAddress = host.Address, Timeout = Timeout.InfiniteTimeSpan };
        using var client = new GoAiClient(http, "go-isolated-coding-benchmark");
        var catalog = await client.GetCodingModelsAsync(stop).ConfigureAwait(false);
        if (!catalog.RuntimeReachable || !catalog.Models.Any(model => model.Id == options.Model))
            throw new InvalidOperationException("Selected native model is unavailable: " + catalog.Message);
        await BenchmarkStorage.SaveAsync(Path.Combine(options.Output, "selected-model.json"), catalog.Models.Single(model => model.Id == options.Model), stop).ConfigureAwait(false);
        var results = LoadResults();
        foreach (var (repetition, task, variant) in BenchmarkResults.Schedule(options.Variants)
            .Where(item => item.Repetition <= options.Repetitions && options.Tasks.Contains(item.Task.Id, StringComparer.Ordinal)))
        {
            stop.ThrowIfCancellationRequested();
            var id = $"{task.Id}--r{repetition}--{variant.Id}";
            if (results.Any(result => result.JobId == id)) continue;
            Console.WriteLine($"[{DateTimeOffset.UtcNow:O}] {id}: start; model={options.Model}");
            var result = await RunJobAsync(client, id, task, variant, repetition, stop).ConfigureAwait(false);
            results.Add(result);
            await FlushResultsAsync(results, stop).ConfigureAwait(false);
            Console.WriteLine($"{id}: quality={result.QualityPassed}; elapsed={result.ElapsedMilliseconds / 1000:F1}s; calls={result.ToolCalls}; error={result.Error}");
        }
        await FlushResultsAsync(results, stop).ConfigureAwait(false);
        Console.WriteLine("Measured results saved. Qualification is false for incomplete pilot coverage; see qualification.json for the explicit gate.");
        return results.All(static result => result.QualityPassed) ? 0 : 2;
    }

    private void PrepareManifest()
    {
        var path = Path.Combine(options.Output, "manifest.json");
        if (Directory.Exists(options.Output) && !File.Exists(path)) throw new IOException("Refusing a pre-existing directory without a benchmark manifest.");
        Directory.CreateDirectory(options.Output);
        var definition = BenchmarkStorage.Digest(JsonSerializer.Serialize(BenchmarkTasks.All, BenchmarkStorage.JsonLine)
            + JsonSerializer.Serialize(BenchmarkVariant.All, BenchmarkStorage.JsonLine) + "prompt-v1");
        _definitionFingerprint = definition;
        _sourceFingerprint = BenchmarkStorage.Digest(string.Join("\n", new[] { typeof(BenchmarkRunner).Assembly.Location,
            typeof(GoAi.Server.Core.Runs.RunProcessor).Assembly.Location, typeof(LocalCodingToolExecutor).Assembly.Location,
            typeof(GoAiClient).Assembly.Location, typeof(RunRequest).Assembly.Location, options.Python }.Select(BenchmarkStorage.FileDigest)));
        var expected = new BenchmarkManifest(1, options.Model!, options.Python, options.Runtime.AbsoluteUri, definition, _sourceFingerprint,
            options.Variants.Select(static value => value.Id).ToArray(), options.Tasks, options.Repetitions, options.TimeoutSeconds,
            "Frozen search-index transport; actual public HTTPS fetch. This is NOT live SearXNG search. Source availability may vary and is recorded in events.",
            BenchmarkTasks.ResearchUrl, DateTimeOffset.UtcNow);
        if (File.Exists(path))
        {
            if (!options.Resume) throw new IOException("Existing benchmark requires --resume; a fresh benchmark requires a new output directory.");
            var stored = BenchmarkStorage.Read<BenchmarkManifest>(path) ?? throw new InvalidDataException("Benchmark manifest is missing.");
            if (JsonSerializer.Serialize(stored with { CreatedAt = expected.CreatedAt }, BenchmarkStorage.JsonLine)
                != JsonSerializer.Serialize(expected, BenchmarkStorage.JsonLine))
                throw new InvalidDataException("Resume refuses changed model, interpreter, fixture definitions, variants or limits.");
        }
        else
        {
            if (options.Resume) throw new IOException("--resume requires the original manifest and artifacts.");
            File.WriteAllText(path, JsonSerializer.Serialize(expected, BenchmarkStorage.Json), new UTF8Encoding(false));
        }
        Directory.CreateDirectory(Path.Combine(options.Output, "jobs"));
    }

    private List<BenchmarkRunResult> LoadResults() => Directory.EnumerateFiles(Path.Combine(options.Output, "jobs"), "result.json", SearchOption.AllDirectories)
        .Select(path => BenchmarkStorage.Read<BenchmarkRunResult>(path) ?? throw new InvalidDataException("Invalid result: " + path)).ToList();

    private async Task FlushResultsAsync(List<BenchmarkRunResult> results, CancellationToken token)
    {
        await BenchmarkStorage.SaveAsync(Path.Combine(options.Output, "results.json"), results, token).ConfigureAwait(false);
        var qualification = options.Variants.Where(variant => variant.Id != "reference"
            && (variant.Id != "working-state" || options.Variants.Any(static item => item.Id == "reference")))
            .Select(variant => BenchmarkResults.Qualify(variant.Id, results)).ToArray();
        await BenchmarkStorage.SaveAsync(Path.Combine(options.Output, "qualification.json"), qualification, token).ConfigureAwait(false);
    }

    private async Task<BenchmarkRunResult> RunJobAsync(GoAiClient client, string id, BenchmarkTask task,
        BenchmarkVariant variant, int repetition, CancellationToken stop)
    {
        var directory = Path.Combine(options.Output, "jobs", id);
        var jobFile = Path.Combine(directory, "job.json");
        string workspace;
        BenchmarkJob job;
        if (File.Exists(jobFile))
        {
            if (!options.Resume) throw new IOException("A pending job requires --resume.");
            job = BenchmarkStorage.Read<BenchmarkJob>(jobFile) ?? throw new InvalidDataException("Invalid pending job.");
            job.Resumed = true;
            workspace = Path.Combine(directory, "workspace");
            BenchmarkStorage.RejectLinkedParents(workspace);
        }
        else
        {
            workspace = await BenchmarkStorage.PrepareFixtureAsync(directory, task, reference: false, stop).ConfigureAwait(false);
            job = new BenchmarkJob { ResearchVerified = !task.Research, SearchEvidence = !task.RequiresSearch, OutputReadEvidence = !task.RequiresOutputRead };
            await BenchmarkStorage.SaveAsync(jobFile, job, stop).ConfigureAwait(false);
        }
        Directory.CreateDirectory(Path.Combine(directory, "receipts"));
        var prompt = CreatePrompt(task);
        var request = new RunRequest(GoAiProtocol.Version, RunMode.Coding,
            [new("user", [new("text", prompt)])], ClientCapabilities: ["coding", "coding.evidence"], SessionId: job.SessionId.ToString(),
            AllowedServerTools: task.Research ? ResearchTools : [], PreferredCodingModelId: options.Model, CodingOptions: variant.Options);
        await BenchmarkStorage.SaveAsync(Path.Combine(directory, "request.json"), request, stop).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var remaining = Math.Max(1, options.TimeoutSeconds * 1000.0 - job.ElapsedMilliseconds);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(remaining));
        var token = deadline.Token;
        var elapsed = Stopwatch.StartNew();
        var priorElapsed = job.ElapsedMilliseconds;
        try
        {
            if (!job.Terminal)
            {
                var accepted = await client.CreateRunAsync(request, "coding-benchmark-" + id, token).ConfigureAwait(false);
                if (job.RunId is not null && job.RunId != accepted.RunId) throw new InvalidDataException("Resume returned a different server run.");
                job.RunId = accepted.RunId;
                await SaveJobAsync().ConfigureAwait(false);
                var store = new CodingRunEvidenceStore(Path.Combine(options.Output, "client"), job.SessionId, job.RunId);
                while (!job.Terminal)
                {
                    await foreach (var item in client.StreamRunEventsAsync(job.RunId, job.Cursor, token).ConfigureAwait(false))
                    {
                        if (item.Id <= job.Cursor) continue;
                        await File.AppendAllTextAsync(Path.Combine(directory, "events.jsonl"), JsonSerializer.Serialize(item, BenchmarkStorage.JsonLine) + "\n", token).ConfigureAwait(false);
                        if (item.Type == RunEventTypes.ClientToolProposed)
                        {
                            var proposal = item.Data.Deserialize<ToolProposal>(ProtocolJson) ?? throw new InvalidDataException("Missing tool proposal.");
                            var toolResult = await ExecuteProposalAsync(proposal, job, task, workspace, directory, store, token).ConfigureAwait(false);
                            await client.SubmitClientToolResultAsync(job.RunId, toolResult, token).ConfigureAwait(false);
                            if (!job.ToolTimings.Any(timing => timing.Id == proposal.ProposalId))
                            {
                                var acknowledged = DateTimeOffset.UtcNow;
                                job.ToolTimings.Add(new(proposal.ProposalId, proposal.Name, "client-proposal-to-http-ack", item.CreatedAt,
                                    acknowledged, Math.Max(0, (acknowledged - item.CreatedAt).TotalMilliseconds),
                                    Integer(toolResult.Result, "elapsedMilliseconds")));
                            }
                        }
                        else if (item.Type == RunEventTypes.CodingMetrics)
                        {
                            var metrics = item.Data.Deserialize<CodingTurnMetricsEvent>(ProtocolJson);
                            if (metrics is not null) job.ModelTurns.Add(metrics);
                        }
                        else if (item.Type == RunEventTypes.ServerToolStarted && String(item.Data, "callId") is { } startedId)
                            job.ToolStarts.TryAdd(startedId, new(String(item.Data, "tool") ?? "unknown", item.CreatedAt));
                        else if (item.Type == RunEventTypes.ServerToolCompleted)
                        {
                            var name = String(item.Data, "tool");
                            if (String(item.Data, "callId") is { } finishedId && !job.ToolTimings.Any(timing => timing.Id == finishedId))
                            {
                                var start = job.ToolStarts.GetValueOrDefault(finishedId);
                                job.ToolTimings.Add(new(finishedId, name ?? "unknown", "server-events", start?.At, item.CreatedAt,
                                    start is null ? null : Math.Max(0, (item.CreatedAt - start.At).TotalMilliseconds), null));
                            }
                            if (task.Research && name == "web.fetch" && Boolean(item.Data, "success") && item.Data.TryGetProperty("result", out var fetched)
                                && Boolean(fetched, "found") && String(fetched, "url")?.StartsWith(BenchmarkTasks.ResearchUrl, StringComparison.Ordinal) == true
                                && fetched.TryGetProperty("matches", out var matches) && matches.ValueKind == JsonValueKind.Array && matches.GetArrayLength() > 0)
                                job.ResearchVerified = true;
                        }
                        else if (item.Type == RunEventTypes.RunCompleted) { job.Completed = true; job.Terminal = true; }
                        else if (item.Type is RunEventTypes.RunCancelled or RunEventTypes.RunFailed)
                        { job.Terminal = true; job.Error = String(item.Data, "message") ?? String(item.Data, "errorCode") ?? item.Type; }
                        job.Cursor = item.Id;
                        await SaveJobAsync().ConfigureAwait(false);
                        if (job.Terminal) break;
                    }
                    if (!job.Terminal) await Task.Delay(200, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            job.Error = "benchmark.harness_timeout";
            job.Terminal = true;
            if (job.RunId is not null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await client.CancelRunAsync(job.RunId, cleanup.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            job.ElapsedMilliseconds = priorElapsed + elapsed.Elapsed.TotalMilliseconds;
            await BenchmarkStorage.SaveAsync(jobFile, job, CancellationToken.None).ConfigureAwait(false);
        }
        stop.ThrowIfCancellationRequested();
        // The oracle runs independently, outside the measured agent duration and outside its workspace.
        var oraclePath = Path.Combine(directory, "independent_oracle.py");
        var oracleBefore = File.Exists(oraclePath) && BenchmarkStorage.FileDigest(oraclePath) == BenchmarkStorage.Digest(task.Oracle);
        var independent = oracleBefore ? await Program.RunOracleAsync(options.Python, directory, workspace, stop).ConfigureAwait(false)
            : JsonSerializer.SerializeToElement(new { success = false, exitCode = (int?)null, stdout = "", stderr = "Independent oracle was changed; execution refused." });
        var oracleAfter = File.Exists(oraclePath) && BenchmarkStorage.FileDigest(oraclePath) == BenchmarkStorage.Digest(task.Oracle);
        var protectedFiles = task.ExpectedFiles.Where(item => !task.MutablePaths.Contains(item.Key, StringComparer.Ordinal))
            .All(item => File.Exists(Path.Combine(workspace, item.Key)) && File.ReadAllText(Path.Combine(workspace, item.Key)) == item.Value)
            && Directory.EnumerateFiles(workspace, "*", SearchOption.AllDirectories)
                .All(path => task.ExpectedFiles.ContainsKey(Path.GetRelativePath(workspace, path).Replace('\\', '/')));
        var sourceHashes = task.MutablePaths.ToDictionary(static path => path, path =>
            File.Exists(Path.Combine(workspace, path)) ? BenchmarkStorage.FileDigest(Path.Combine(workspace, path)) : "missing", StringComparer.Ordinal);
        var changed = task.RequiredChangedPaths.All(path => sourceHashes[path] != "missing" && sourceHashes[path] != BenchmarkStorage.Digest(task.ExpectedFiles[path]));
        var actualSource = BenchmarkStorage.Digest(JsonSerializer.Serialize(sourceHashes, BenchmarkStorage.JsonLine));
        var runtimeFingerprint = await ReadRuntimeFingerprintAsync(directory, stop).ConfigureAwait(false);
        var result = new BenchmarkRunResult(id, task.Id, variant.Id, repetition, options.Model!, job.RunId ?? "not-created",
            job.Completed, BenchmarkOracleReceipt.IsPassed(independent, task, oracleBefore && oracleAfter), protectedFiles,
            job.ActualEdit && job.SourceRead && changed, job.VerificationExecuted,
            job.ResearchVerified, job.Resumed, job.UncertainExecution,
            job.ElapsedMilliseconds, job.ToolCalls, job.Error, independent.GetRawText(), actualSource, job.ModelTurns,
            _definitionFingerprint, _sourceFingerprint, runtimeFingerprint, job.SearchEvidence, job.OutputReadEvidence);
        await BenchmarkStorage.SaveAsync(Path.Combine(directory, "result.json"), result, stop).ConfigureAwait(false);
        await BenchmarkStorage.SaveAsync(Path.Combine(directory, "timings.json"), BenchmarkTimings.Summarize(job.ModelTurns, job.ToolTimings), stop).ConfigureAwait(false);
        return result;

        Task SaveJobAsync()
        {
            job.ElapsedMilliseconds = priorElapsed + elapsed.Elapsed.TotalMilliseconds;
            return BenchmarkStorage.SaveAsync(jobFile, job, token);
        }
    }

    private async Task<ClientToolResult> ExecuteProposalAsync(ToolProposal proposal, BenchmarkJob job, BenchmarkTask task,
        string workspace, string directory, CodingRunEvidenceStore store, CancellationToken token)
    {
        var key = BenchmarkStorage.Digest(proposal.ProposalId);
        var receiptFile = Path.Combine(directory, "receipts", key + ".json");
        var attemptFile = Path.Combine(directory, "receipts", key + ".attempt");
        ClientToolResult result;
        if (File.Exists(receiptFile)) result = BenchmarkStorage.Read<ClientToolResult>(receiptFile)!;
        else if (File.Exists(attemptFile))
        {
            job.UncertainExecution = true;
            result = new(proposal.ProposalId, "failed", JsonSerializer.SerializeToElement(new { success = false, outcomeUnknown = true }),
                "benchmark.execution_uncertain", "Interrupted local attempt has no durable receipt; it will not be executed again.");
            await BenchmarkStorage.SaveAsync(receiptFile, result, token).ConfigureAwait(false);
        }
        else
        {
            if (job.ToolCalls >= 512) throw new InvalidOperationException("The benchmark fixture exceeded its 512-client-tool harness bound.");
            await File.WriteAllTextAsync(attemptFile, JsonSerializer.Serialize(proposal, BenchmarkStorage.JsonLine), token).ConfigureAwait(false);
            try
            {
                ValidateProposal(proposal, task, workspace);
                JsonElement value;
                var arguments = proposal.Arguments;
                if (proposal.Name == ClientToolNames.CodingReadOutput)
                    value = JsonSerializer.SerializeToElement(await store.ReadOutputAsync(arguments.GetProperty("evidenceId").GetString()!,
                        String(arguments, "stream") ?? "stdout", Integer(arguments, "offset") ?? 0,
                        Integer(arguments, "maximumCharacters") ?? 4000, token).ConfigureAwait(false), ProtocolJson);
                else if (proposal.Name == ClientToolNames.CodingSearchRunEvidence)
                    value = JsonSerializer.SerializeToElement(await store.SearchRunEvidenceAsync(arguments.GetProperty("query").GetString()!,
                        Integer(arguments, "maximumResults") ?? 8, token).ConfigureAwait(false), ProtocolJson);
                else
                {
                    using var capture = store.BeginStep(proposal.ProposalId, proposal.Name, arguments);
                    var executor = new LocalCodingToolExecutor(workspace, progress => File.AppendAllTextAsync(Path.Combine(directory, "tool-progress.jsonl"),
                        JsonSerializer.Serialize(new { proposalId = proposal.ProposalId, at = DateTimeOffset.UtcNow, progress }, BenchmarkStorage.JsonLine) + "\n", token), capture);
                    value = await executor.ExecuteAsync(proposal.Name, arguments, token).ConfigureAwait(false);
                }
                var reference = await store.RecordAsync(proposal.ProposalId, proposal.Name, arguments, value, token).ConfigureAwait(false);
                var enriched = JsonNode.Parse(value.GetRawText())!.AsObject();
                enriched["evidence"] = JsonSerializer.SerializeToNode(reference, ProtocolJson);
                value = JsonSerializer.SerializeToElement(enriched, ProtocolJson);
                var success = !value.TryGetProperty("success", out var successful) || successful.ValueKind != JsonValueKind.False;
                result = new(proposal.ProposalId, success ? "completed" : "failed", value,
                    success ? null : "benchmark.actual_tool_failed");
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                result = new(proposal.ProposalId, "failed", JsonSerializer.SerializeToElement(new { success = false }),
                    "benchmark.tool_rejected_or_failed", exception.Message);
            }
            await BenchmarkStorage.SaveAsync(receiptFile, result, token).ConfigureAwait(false);
        }
        if (result.ProposalId != proposal.ProposalId) throw new InvalidDataException("A durable receipt belongs to a different proposal.");
        if (job.ProcessedProposals.Add(proposal.ProposalId))
        {
            job.ToolCalls++;
            if (proposal.Name == ClientToolNames.CodingCommand && proposal.Arguments.TryGetProperty("arguments", out var commandArguments)
                && commandArguments.ValueKind == JsonValueKind.Array && commandArguments.EnumerateArray()
                    .Select(static value => value.GetString()).SequenceEqual(DiagnosticArguments)
                && Integer(result.Result, "exitCode") is not null
                && result.Result.TryGetProperty("evidence", out var diagnosticReference) && String(diagnosticReference, "evidenceId") is { } evidenceId)
                job.DiagnosticEvidenceIds.Add(evidenceId);
            if (result.Status == "completed")
            {
                if (proposal.Name == ClientToolNames.CodingRead && String(proposal.Arguments, "path") is { } readPath)
                {
                    var relative = Path.GetRelativePath(workspace, Path.GetFullPath(Path.Combine(workspace, readPath))).Replace('\\', '/');
                    job.ReadSourcePaths.Add(relative);
                    job.SourceRead = task.RequiredChangedPaths.All(job.ReadSourcePaths.Contains);
                }
                if (proposal.Name is ClientToolNames.CodingEdit or ClientToolNames.CodingWrite && Boolean(result.Result, "applied")) job.ActualEdit = true;
                if (proposal.Name == ClientToolNames.CodingCommand && Integer(result.Result, "exitCode") == 0
                    && proposal.Arguments.GetProperty("arguments").EnumerateArray().Select(static value => value.GetString()).SequenceEqual(VerificationArguments)) job.VerificationExecuted = true;
                if (proposal.Name == ClientToolNames.CodingSearch && result.Result.TryGetProperty("matches", out var hits)
                    && hits.ValueKind == JsonValueKind.Array && hits.GetArrayLength() > 0) job.SearchEvidence = true;
                if (proposal.Name == ClientToolNames.CodingReadOutput && task.DiagnosticMarker is { } marker
                    && String(result.Result, "evidenceId") is { } sourceEvidenceId && job.DiagnosticEvidenceIds.Contains(sourceEvidenceId)
                    && String(result.Result, "stream") == "stdout"
                    && String(result.Result, "text")?.Contains(marker, StringComparison.Ordinal) == true) job.OutputReadEvidence = true;
            }
        }
        return result;
    }

    private void ValidateProposal(ToolProposal proposal, BenchmarkTask task, string workspace)
    {
        if (proposal.Name is ClientToolNames.CodingReadOutput or ClientToolNames.CodingSearchRunEvidence) return;
        if (proposal.Name == ClientToolNames.CodingCommand)
        {
            var args = proposal.Arguments;
            if (!string.Equals(String(args, "executable"), options.Python, StringComparison.OrdinalIgnoreCase)
                || !args.TryGetProperty("arguments", out var values) || values.ValueKind != JsonValueKind.Array
                || !(values.EnumerateArray().Select(static value => value.GetString()).SequenceEqual(VerificationArguments)
                    || task.RequiresOutputRead && values.EnumerateArray().Select(static value => value.GetString()).SequenceEqual(DiagnosticArguments))
                || !string.Equals(Path.GetFullPath(Path.Combine(workspace, String(args, "workingDirectory") ?? ".")), workspace, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Only the exact documented Python verification commands are allowed in this fixture.");
            return;
        }
        if (!FileTools.Contains(proposal.Name, StringComparer.Ordinal)) throw new UnauthorizedAccessException("This fixture permits only its documented local file and evidence tools.");
        if (proposal.Name is ClientToolNames.CodingEdit or ClientToolNames.CodingWrite)
        {
            var path = String(proposal.Arguments, "path") ?? throw new ArgumentException("Mutation path missing.");
            if (!task.MutablePaths.Any(allowed => string.Equals(Path.GetFullPath(Path.Combine(workspace, path)),
                Path.Combine(workspace, allowed.Replace('/', Path.DirectorySeparatorChar)), StringComparison.OrdinalIgnoreCase)))
                throw new UnauthorizedAccessException("Only this task's declared source modules may be edited; tests and other fixture files are immutable.");
        }
    }

    private string CreatePrompt(BenchmarkTask task) =>
        "Repariere in diesem isolierten Python-Projekt ausschließlich die zum Vertrag gehörenden Implementierungsmodule: " + task.Requirement
        + " Lies den aktuellen Quelltext und die vorhandenen Tests. Sichere gezielte Änderungen mit dem gelesenen SHA-256 ab. "
        + "Erhalte die Eingabedaten und alle anderen Dateien. Der Ordner ist kein Git-Repository. "
        + "Plane kurz; nutze einen angebotenen gespeicherten Arbeitsplan mit belegten Ergebnissen und passenden Arbeitsphasen. "
        + (task.RequiresSearch ? "Finde die zuständigen Implementierungen mit coding.search anhand der Vertragsbegriffe, bevor du die gefundenen Module bearbeitest. " : string.Empty)
        + "Führe nach der Änderung genau den verfügbaren Interpreter " + JsonSerializer.Serialize(options.Python)
        + " mit arguments=[\"-B\",\"-m\",\"unittest\",\"-v\"] im Projektstamm aus. Dieser Interpreter ist vom Harness bereits als vorhanden bestätigt. "
        + (task.RequiresOutputRead ? "Starte zusätzlich denselben Interpreter mit arguments=[\"-B\",\"diagnose.py\"]. Die lange Ausgabe enthält einen wichtigen mittigen Diagnosebeleg; "
            + "lies diesen tatsächlichen Beleg mit coding.readOutput aus dem Laufarchiv, gegebenenfalls nach coding.searchRunEvidence. " : string.Empty)
        + "Keine anderen Programme, Installationen, Shell-Aufrufe oder Änderungen der Tests. Berichte die tatsächlich ausgeführten Tests kurz. "
        + (task.Research ? "Nutze für die Recherche die angebotene eingefrorene Suchindex-Fixture und lies die echte Originalquelle " + BenchmarkTasks.ResearchUrl
            + ". Die Suche ist ein Benchmark-Fixture, keine aktuelle SearXNG-Suche; kennzeichne dies in der Antwort. " : "Öffentliche Webrecherche ist für diese vollständig spezifizierte Aufgabe nicht erforderlich. ")
        + "Führe die Aufgabe selbst aus.";

    private async Task<string?> ReadRuntimeFingerprintAsync(string directory, CancellationToken token)
    {
        using var http = new HttpClient { BaseAddress = options.Runtime, Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            using var response = await http.GetAsync("props?model=" + Uri.EscapeDataString(options.Model!), token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            var fields = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var name in RuntimePropertyNames)
                if (document.RootElement.TryGetProperty(name, out var value)) fields[name] = value.Clone();
            if (!fields.ContainsKey("total_slots") || !fields.ContainsKey("default_generation_settings")) return null;
            if (fields.TryGetValue("model_path", out var pathValue) && pathValue.ValueKind == JsonValueKind.String
                && pathValue.GetString() is { } modelPath && File.Exists(modelPath))
            {
                var match = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(modelPath), @"^(.*)-\d{5}-of-\d{5}\.gguf$",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                var files = match.Success ? Directory.GetFiles(Path.GetDirectoryName(modelPath)!, match.Groups[1].Value + "-*-of-*.gguf") : [modelPath];
                fields["modelFileMetadataNotContentHashes"] = JsonSerializer.SerializeToElement(files.Order(StringComparer.Ordinal).Select(path =>
                { var info = new FileInfo(path); return new { path, info.Length, info.LastWriteTimeUtc }; }), BenchmarkStorage.JsonLine);
            }
            await BenchmarkStorage.SaveAsync(Path.Combine(directory, "runtime-properties.json"), fields, token).ConfigureAwait(false);
            return BenchmarkStorage.Digest(JsonSerializer.Serialize(fields, BenchmarkStorage.JsonLine));
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException && !token.IsCancellationRequested)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "runtime-properties-error.txt"), exception.Message, token).ConfigureAwait(false);
            return null;
        }
    }

    private static string? String(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static bool Boolean(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;
    private static int? Integer(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number) ? number : null;
}
