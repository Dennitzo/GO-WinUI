using System.Text;
using System.Text.Json;
using GoAi.Contracts;
using GoWinUI.App.Services;
using GoWinUI.App.ViewModels;
using GoWinUI.Core.Coding;
using GoWinUI.Core.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace GoWinUI.Tests;

public sealed class CodingRunEvidenceTests : IAsyncLifetime
{
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "go-evidence-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _session = Guid.NewGuid();
    private string Data => Path.Combine(_temporary, "data");
    private string Workspace => Path.Combine(_temporary, "project");
    private static readonly JsonElement EmptyArguments = JsonSerializer.SerializeToElement(new { });
    private static readonly string[] ProbeCommandArguments = ["-NoProfile", "-NonInteractive", "-File", "probe.ps1"];
    private static readonly string[] HistoricalTestListing = ["tests/test_cli.py"];

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Workspace);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task FullMiddleOutputSurvivesRestartAndIdempotentJournalCompletionWithoutReexecution()
    {
        var store = new CodingRunEvidenceStore(Data, _session, "run-first");
        string id;
        const string middle = "EXACT_MIDDLE_DIAGNOSTIC 日本語";
        var original = new string('a', 20_000) + middle + new string('z', 20_000);
        using (var capture = store.BeginStep("proposal-command", "coding.command", JsonSerializer.SerializeToElement(new { executable = "test" })))
        {
            id = capture.Reference.EvidenceId;
            capture.Append("stdout", original);
            capture.Append("stderr", "WARN: separate stream");
            // A capture killed before the result arrives still retains already-flushed output.
        }
        var reopened = new CodingRunEvidenceStore(Data, _session, "run-first");
        var result = JsonSerializer.SerializeToElement(new { exitCode = 7 });
        var recorded = await reopened.RecordAsync("proposal-command", "coding.command", EmptyArguments, result);
        var repeated = await reopened.RecordAsync("proposal-command", "coding.command", EmptyArguments, result);
        Assert.Equal(id, recorded.EvidenceId);
        Assert.Equal(recorded.StoredBytes, repeated.StoredBytes);
        var page = await reopened.ReadOutputAsync(id, offset: 20_000, maximumCharacters: middle.Length);
        Assert.Equal(middle, page.Text);
        Assert.Equal(original.Length, page.StoredCharacters);
        Assert.True(page.HasMore);
        Assert.False(page.Truncated);
        Assert.Equal("WARN: separate stream", (await reopened.ReadOutputAsync(id, "stderr")).Text);
        Assert.Equal(result.GetRawText(), (await reopened.ReadOutputAsync(id, "result")).Text);
        var found = await reopened.SearchRunEvidenceAsync("MIDDLE_DIAGNOSTIC");
        Assert.Equal(id, Assert.Single(found.Matches).EvidenceId);
        Assert.Contains(middle, found.Matches[0].Text, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Workspace));
    }

    [Fact]
    public async Task StreamChunksPreserveSplitSurrogatePairsAndPagingMakesForwardProgress()
    {
        var store = new CodingRunEvidenceStore(Data, _session, "run-unicode");
        using var capture = store.BeginStep("unicode", "coding.command", EmptyArguments);
        capture.Append("stdout", "start\uD83D");
        capture.Append("stdout", "\uDE80end");
        var page = await store.ReadOutputAsync(capture.Reference.EvidenceId, offset: 5, maximumCharacters: 1);
        Assert.Equal("🚀", page.Text);
        Assert.Equal(7, page.NextOffset);
        Assert.Equal("start🚀end", (await store.ReadOutputAsync(capture.Reference.EvidenceId)).Text);
    }

    [Fact]
    public async Task BothByteQuotasRemainBoundedAcrossStepsAndRestartAndMarkMissingTailsHonestly()
    {
        const long stepLimit = 12_288;
        const long runLimit = 24_576;
        var store = new CodingRunEvidenceStore(Data, _session, "run-quota", stepLimit, runLimit);
        using (var first = store.BeginStep("one", "coding.command", EmptyArguments))
        {
            first.Append("stdout", new string('界', 20_000));
            Assert.True(first.Reference.Truncated);
            Assert.True(first.Reference.StoredBytes <= stepLimit);
            Assert.DoesNotContain('�', (await store.ReadOutputAsync(first.Reference.EvidenceId)).Text);
        }
        store = new CodingRunEvidenceStore(Data, _session, "run-quota", stepLimit, runLimit);
        using (var second = store.BeginStep("two", "coding.command", EmptyArguments))
        {
            second.Append("stdout", new string('x', 20_000));
            Assert.True(second.Reference.Truncated);
        }
        using var last = store.BeginStep("three", "coding.command", EmptyArguments);
        Assert.True(last.Reference.Truncated);
        var bytes = Directory.EnumerateFiles(Path.Combine(Data, "Cache", "CodingRuns", "run-quota"))
            .Sum(path => new FileInfo(path).Length);
        Assert.True(bytes <= runLimit, $"Actual stored bytes {bytes} exceeded run quota {runLimit}.");
    }

    [Fact]
    public async Task EvidenceIdsNeverSelectPathsOrReadAnotherRunOrSessionAndCleanupOnlyRemovesTheDeletedSession()
    {
        var a = new CodingRunEvidenceStore(Data, _session, "run-a");
        var reference = await a.RecordAsync("step", "coding.read", EmptyArguments, JsonSerializer.SerializeToElement(new { text = "PRIVATE_A" }));
        var otherSession = Guid.NewGuid();
        var b = new CodingRunEvidenceStore(Data, otherSession, "run-b");
        var other = await b.RecordAsync("step", "coding.read", EmptyArguments, JsonSerializer.SerializeToElement(new { text = "PRIVATE_B" }));
        await Assert.ThrowsAsync<FileNotFoundException>(() => b.ReadOutputAsync(reference.EvidenceId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new CodingRunEvidenceStore(Data, otherSession, "run-a").ReadOutputAsync(reference.EvidenceId));
        await Assert.ThrowsAsync<ArgumentException>(() => a.ReadOutputAsync("../scope.json"));
        await Assert.ThrowsAsync<ArgumentException>(() => a.ReadOutputAsync(reference.EvidenceId, "../result"));
        Assert.Empty((await b.SearchRunEvidenceAsync("PRIVATE_A")).Matches);
        await CodingRunEvidenceStore.DeleteSessionAsync(Data, _session);
        Assert.False(Directory.Exists(Path.Combine(Data, "Cache", "CodingRuns", "run-a")));
        Assert.Contains("PRIVATE_B", (await b.ReadOutputAsync(other.EvidenceId, "result")).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NewTurnCanReadExactPreviousRunReferenceAfterRestartWithoutImportingOldOutputs()
    {
        var previous = new CodingRunEvidenceStore(Data, _session, "run-previous");
        var original = new string('a', 9000) + "HISTORICAL_MIDDLE" + new string('z', 9000);
        var reference = await previous.RecordAsync("step", "coding.list", EmptyArguments,
            JsonSerializer.SerializeToElement(new { text = original }));
        var current = new CodingRunEvidenceStore(Data, _session, "run-current");

        // Reading history can be the first tool call, before this run has a cache.
        var page = await current.ReadOutputAsync(reference.EvidenceId, "result", offset: 9000, maximumCharacters: 100);
        Assert.Contains("HISTORICAL_MIDDLE", page.Text, StringComparison.Ordinal);
        Assert.Equal("run-previous", page.SourceRunId);
        Assert.True(page.Historical);
        Assert.Contains("kein Nachweis des aktuellen Dateistands", page.Notice, StringComparison.Ordinal);
        Assert.True(page.HasMore);
        Assert.False(Directory.Exists(Path.Combine(Data, "Cache", "CodingRuns", "run-current")));
        Assert.Empty((await current.SearchRunEvidenceAsync("HISTORICAL_MIDDLE")).Matches);
        Assert.Equal(page, await new CodingRunEvidenceStore(Data, _session, "run-current")
            .ReadOutputAsync(reference.EvidenceId, "result", offset: 9000, maximumCharacters: 100));

        var currentReference = await current.RecordAsync("step", "coding.read", EmptyArguments,
            JsonSerializer.SerializeToElement(new { text = "CURRENT_ONLY" }));
        var currentPage = await current.ReadOutputAsync(currentReference.EvidenceId, "result");
        Assert.False(currentPage.Historical);
        Assert.Equal("run-current", currentPage.SourceRunId);
        Assert.Null(currentPage.Notice);
        Assert.Empty((await current.SearchRunEvidenceAsync("HISTORICAL_MIDDLE")).Matches);
    }

    [Fact]
    public async Task BrokerReadsSameSessionHistoryButMissingOrForeignReferencesHaveSpecificNonRetryableFailure()
    {
        var previous = new CodingRunEvidenceStore(Data, _session, "run-previous");
        var reference = await previous.RecordAsync("step", "coding.list", EmptyArguments,
            JsonSerializer.SerializeToElement(new { entries = HistoricalTestListing }));
        var current = new CodingRunEvidenceStore(Data, _session, "run-command");
        var broker = new LocalToolBroker(null!, null!, null!, null!);
        var proposal = Proposal("coding.readOutput", new { evidenceId = reference.EvidenceId, stream = "result", maximumCharacters = 6000 });

        var read = await broker.ExecuteAsync(proposal, _session, null, Workspace, evidenceStore: current);
        Assert.Equal("completed", read.Status);
        Assert.True(read.Result.GetProperty("historical").GetBoolean());
        Assert.Equal("run-previous", read.Result.GetProperty("sourceRunId").GetString());
        Assert.Contains("tests/test_cli.py", read.Result.GetProperty("text").GetString(), StringComparison.Ordinal);

        var foreignSession = Guid.NewGuid();
        var foreignCurrent = new CodingRunEvidenceStore(Data, foreignSession, "run-other-current");
        var foreignProposal = proposal with { RunId = "run-other-current" };
        var rejected = await broker.ExecuteAsync(foreignProposal, foreignSession, null, Workspace, evidenceStore: foreignCurrent);
        Assert.Equal("failed", rejected.Status);
        Assert.Equal("client.evidence_unavailable", rejected.ErrorCode);
        Assert.False(rejected.Result.GetProperty("retryable").GetBoolean());
        Assert.DoesNotContain("tests/test_cli.py", JsonSerializer.Serialize(rejected), StringComparison.Ordinal);
        Assert.DoesNotContain("run-previous", JsonSerializer.Serialize(rejected), StringComparison.Ordinal);

        var missing = await broker.ExecuteAsync(Proposal("coding.readOutput", new { evidenceId = "ev-00000000000000000000000000000000" }),
            _session, null, Workspace, evidenceStore: current);
        Assert.Equal("client.evidence_unavailable", missing.ErrorCode);
        Assert.False(missing.Result.GetProperty("available").GetBoolean());
        Assert.Contains("coding.searchHistory", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HistoricalLookupRejectsCopiedScopeAndHonorsCancellation()
    {
        var previous = new CodingRunEvidenceStore(Data, _session, "run-previous");
        var reference = await previous.RecordAsync("step", "coding.read", EmptyArguments,
            JsonSerializer.SerializeToElement(new { text = "HISTORICAL_SECRET" }));
        var scopePath = Path.Combine(Data, "Cache", "CodingRuns", "run-previous", "scope.json");
        await File.WriteAllTextAsync(scopePath, JsonSerializer.Serialize(new { sessionId = _session, rootRunId = "run-forged" }));
        var current = new CodingRunEvidenceStore(Data, _session, "run-current");
        await Assert.ThrowsAsync<FileNotFoundException>(() => current.ReadOutputAsync(reference.EvidenceId, "result"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => current.ReadOutputAsync(reference.EvidenceId,
            cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task EvidenceSymlinkCannotReadOrWriteOutsideTheCache()
    {
        var store = new CodingRunEvidenceStore(Data, _session, "run-link");
        var reference = await store.RecordAsync("step", "coding.read", EmptyArguments, JsonSerializer.SerializeToElement(new { text = "original" }));
        var outside = Path.Combine(Workspace, "private.txt");
        await File.WriteAllTextAsync(outside, "PRIVATE_OUTSIDE");
        var link = Path.Combine(Data, "Cache", "CodingRuns", "run-link", reference.EvidenceId + ".stdout.txt");
        try { File.CreateSymbolicLink(link, outside); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException) { return; }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.ReadOutputAsync(reference.EvidenceId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new CodingRunEvidenceStore(Data, _session, "run-current")
            .ReadOutputAsync(reference.EvidenceId));
        Assert.Equal("PRIVATE_OUTSIDE", await File.ReadAllTextAsync(outside));
        File.Delete(link);
    }

    [Fact]
    public async Task RealCommandKeepsBoundedLiveHeadTailButItsMiddleCanBeReadAndSearchedViaTheBroker()
    {
        var script = """
            [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
            [IO.File]::AppendAllText((Join-Path $PSScriptRoot 'execution-count.txt'), '1')
            [Console]::Write(('a' * 10000) + 'MID_COMMAND_EVIDENCE' + ('z' * 10000))
            [Console]::Error.Write(('b' * 8000) + 'MID_ERROR_EVIDENCE' + ('y' * 8000))
            Start-Sleep -Milliseconds 1200
            exit 7
            """;
        await File.WriteAllTextAsync(Path.Combine(Workspace, "probe.ps1"), script, new UTF8Encoding(true));
        var broker = new LocalToolBroker(null!, null!, null!, null!);
        var store = new CodingRunEvidenceStore(Data, _session, "run-command");
        var progress = new List<CodingCommandProgress>();
        var command = Proposal("coding.command", new { executable = "powershell.exe",
            arguments = ProbeCommandArguments, timeoutSeconds = 15 }, ToolRiskClass.Process);
        var result = await broker.ExecuteAsync(command, _session, null, Workspace,
            commandProgress: value => { progress.Add(value); return Task.CompletedTask; }, evidenceStore: store);
        Assert.Equal("failed", result.Status);
        Assert.Equal(7, result.Result.GetProperty("exitCode").GetInt32());
        Assert.DoesNotContain("MID_COMMAND_EVIDENCE", result.Result.GetProperty("stdout").GetString()!, StringComparison.Ordinal);
        Assert.NotEmpty(progress);
        Assert.All(progress, value => Assert.True(value.Stdout.Length <= 4000 && value.Stderr.Length <= 2000));
        var id = result.Result.GetProperty("evidence").GetProperty("evidenceId").GetString()!;
        var search = await broker.ExecuteAsync(Proposal("coding.searchRunEvidence", new { query = "MID_ERROR_EVIDENCE" }),
            _session, null, Workspace, evidenceStore: store);
        Assert.Equal("completed", search.Status);
        Assert.Single(search.Result.GetProperty("matches").EnumerateArray());
        var read = await broker.ExecuteAsync(Proposal("coding.readOutput", new { evidenceId = id, offset = 10000, maximumCharacters = 20 }),
            _session, null, Workspace, evidenceStore: new CodingRunEvidenceStore(Data, _session, "run-command"));
        Assert.StartsWith("MID_COMMAND_EVIDENCE", read.Result.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Equal("1", await File.ReadAllTextAsync(Path.Combine(Workspace, "execution-count.txt")));
        var rejected = await broker.ExecuteAsync(Proposal("coding.readOutput", new { evidenceId = id }),
            Guid.NewGuid(), null, Workspace, evidenceStore: store);
        Assert.Equal("failed", rejected.Status);
    }

    [Fact]
    public void BrokerEvidenceSchemasRejectExtraScopeFieldsAndMutationRisk()
    {
        const string id = "ev-0123456789abcdef0123456789abcdef";
        LocalToolBroker.ValidateProposal(Proposal("coding.readOutput", new { evidenceId = id, stream = "result", maximumCharacters = 32000 }));
        Assert.Throws<InvalidDataException>(() => LocalToolBroker.ValidateProposal(Proposal("coding.readOutput", new { evidenceId = id, runId = "other" })));
        Assert.Throws<InvalidDataException>(() => LocalToolBroker.ValidateProposal(Proposal("coding.readOutput", new { evidenceId = id, maximumCharacters = 32001 })));
        Assert.Throws<InvalidDataException>(() => LocalToolBroker.ValidateProposal(Proposal("coding.searchRunEvidence", new { query = "text", maximumResults = 21 })));
        Assert.Throws<InvalidDataException>(() => LocalToolBroker.ValidateProposal(Proposal("coding.readOutput", new { evidenceId = id }, ToolRiskClass.Process)));
    }

    [Fact]
    public async Task EvidenceStorageFailureAfterCommittedWritePreservesAppliedReceiptAndAddsWarning()
    {
        var broker = new LocalToolBroker(null!, null!, null!, null!);
        var store = new CodingRunEvidenceStore(Data, _session, "run-command");
        var proposal = Proposal("coding.write", new { path = "committed.txt", content = "written once" }, ToolRiskClass.LocalMutation);
        var result = await broker.ExecuteAsync(proposal, _session, null, Workspace, commandProgress: async progress =>
        {
            if (JsonSerializer.Deserialize<JsonElement>(progress.OutputJson!).GetProperty("phase").GetString() != "applied") return;
            Assert.Equal("written once", await File.ReadAllTextAsync(Path.Combine(Workspace, "committed.txt")));
            // A directory in place of the upcoming result file creates a real
            // filesystem error after the atomic project mutation has committed.
            var cache = Path.Combine(Data, "Cache", "CodingRuns", "run-command");
            var metadata = Assert.Single(Directory.EnumerateFiles(cache, "ev-*.json"));
            Directory.CreateDirectory(Path.Combine(cache, Path.GetFileNameWithoutExtension(metadata) + ".result.txt"));
        }, evidenceStore: store);

        Assert.Equal("completed", result.Status);
        Assert.True(result.Result.GetProperty("success").GetBoolean());
        Assert.True(result.Result.GetProperty("applied").GetBoolean());
        Assert.Equal("written once", await File.ReadAllTextAsync(Path.Combine(Workspace, "committed.txt")));
        Assert.True(result.Result.GetProperty("evidence").GetProperty("truncated").GetBoolean());
        Assert.Contains("Speicherzugriff", result.Result.GetProperty("evidence").GetProperty("notice").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpfrontEvidenceStorageFailurePreventsMutation()
    {
        var broker = new LocalToolBroker(null!, null!, null!, null!);
        var store = new CodingRunEvidenceStore(Data, _session, "run-command");
        Directory.CreateDirectory(Path.Combine(Data, "Cache", "CodingRuns", "run-command", "scope.json"));
        var result = await broker.ExecuteAsync(Proposal("coding.write", new { path = "never.txt", content = "must not be written" }, ToolRiskClass.LocalMutation),
            _session, null, Workspace, evidenceStore: store);

        Assert.Equal("failed", result.Status);
        Assert.False(File.Exists(Path.Combine(Workspace, "never.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommittedSessionDeletionCleansOnlyItsEvidenceAndRetainsPinnedSessions(bool bulkDelete)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var retained = await chats.CreateSessionAsync("Pinned retained");
        await chats.SetPinnedAsync(retained.Id, true);
        var target = await chats.CreateSessionAsync("Delete target");
        var other = await chats.CreateSessionAsync("Other session");
        foreach (var session in new[] { retained, target, other })
        {
            var evidence = new CodingRunEvidenceStore(environment.Directory, session.Id, "run-" + session.Id.ToString("N"));
            await evidence.RecordAsync("step", "coding.read", EmptyArguments, JsonSerializer.SerializeToElement(new { text = "fixture evidence" }));
        }
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        await settings.UpdateAsync(current => current with { ActiveSessionId = retained.Id });
        var activity = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
        var coordinator = new AssistantCoordinator(chats, environment.Get<IWorkflowRepository>(), environment.Get<IDocumentIngestor>(),
            environment.Get<IContextAssembler>(), environment.Get<IPromptTriggerRepository>(), environment.Get<IAssistantAttachmentRepository>(),
            environment.Get<IChatArtifactRepository>(), environment.Get<IConversationSnapshotRepository>(), null, settings, activity);

        await coordinator.HandleAsync(new(AssistantWebBridge.ProtocolVersion, bulkDelete ? "session.clear" : "session.delete",
            "evidence-lifecycle", JsonSerializer.SerializeToElement(new { sessionId = target.Id })), (_, _, _) => Task.CompletedTask);

        var cache = Path.Combine(environment.Directory, "Cache", "CodingRuns");
        Assert.False(Directory.Exists(Path.Combine(cache, "run-" + target.Id.ToString("N"))));
        Assert.True(Directory.Exists(Path.Combine(cache, "run-" + retained.Id.ToString("N"))));
        Assert.True((await chats.GetSessionAsync(retained.Id))!.IsPinned);
        Assert.Equal(!bulkDelete, Directory.Exists(Path.Combine(cache, "run-" + other.Id.ToString("N"))));
    }

    private static ToolProposal Proposal(string name, object arguments, ToolRiskClass risk = ToolRiskClass.ReadOnly) => new(
        "proposal-" + Guid.NewGuid().ToString("N"), "run-command", name, JsonSerializer.SerializeToElement(arguments), risk,
        "Werkzeugbelege prüfen", DateTimeOffset.UtcNow.AddMinutes(5));

    public Task DisposeAsync()
    {
        var path = Path.GetFullPath(_temporary);
        if (!string.Equals(Path.GetDirectoryName(path), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(path).StartsWith("go-evidence-tests-", StringComparison.Ordinal)
            || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Unsafe test cleanup path.");
        Directory.Delete(path, recursive: true);
        return Task.CompletedTask;
    }
}
