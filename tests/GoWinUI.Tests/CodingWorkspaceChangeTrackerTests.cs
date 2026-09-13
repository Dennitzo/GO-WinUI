using GoWinUI.Core.Coding;
using System.Diagnostics;
using System.Text;

namespace GoWinUI.Tests;

public sealed class CodingWorkspaceChangeTrackerTests : IDisposable
{
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "go-workspace-changes-" + Guid.NewGuid().ToString("N"));
    private string Workspace => Path.Combine(_temporary, "project");
    private string Storage => Path.Combine(_temporary, "baseline");
    public CodingWorkspaceChangeTrackerTests() => Directory.CreateDirectory(Workspace);

    [Fact]
    public async Task RepeatedEditsAndRevertCompareWithRunStartWithoutGit()
    {
        await WriteAsync("source.py", "first\nkeep\n");
        using var tracker = new CodingWorkspaceChangeTracker(Workspace, Storage);
        Assert.Empty((await tracker.InitializeAsync()).Files);
        await WriteAsync("source.py", "intermediate\nkeep\n");
        var first = Assert.Single((await tracker.RefreshAsync()).Files);
        Assert.Equal(1, first.AddedLines);
        Assert.Equal(1, first.RemovedLines);
        await WriteAsync("source.py", "final\nkeep\n");
        var second = Assert.Single((await tracker.RefreshAsync()).Files);
        Assert.Equal("modified", second.Kind);
        Assert.Contains("-first", second.Diff);
        Assert.Contains("+final", second.Diff);
        Assert.DoesNotContain("intermediate", second.Diff);
        Assert.Equal(1, second.AddedLines);
        await WriteAsync("source.py", "first\nkeep\n");
        Assert.Empty((await tracker.RefreshAsync()).Files);
    }

    [Fact]
    public async Task ExternalFileCreatesDeletesAndEmptyFilesHaveCorrectNetKindsAndPatchHeaders()
    {
        await WriteAsync("old.txt", "one\ntwo\n");
        await WriteAsync("empty.txt", "");
        using var tracker = new CodingWorkspaceChangeTracker(Workspace, Storage);
        await tracker.InitializeAsync();
        File.Delete(Path.Combine(Workspace, "old.txt"));
        File.Delete(Path.Combine(Workspace, "empty.txt"));
        await WriteAsync("created.txt", "new\n");
        var snapshot = await tracker.RefreshAsync();
        Assert.Equal(3, snapshot.Files.Count);
        var removed = Assert.Single(snapshot.Files, file => file.Path == "old.txt");
        Assert.Equal("deleted", removed.Kind);
        Assert.Equal(2, removed.RemovedLines);
        Assert.Equal(0, removed.AddedLines);
        Assert.Contains("+++ /dev/null", removed.Diff);
        Assert.Contains("deleted file mode", removed.Diff);
        var empty = Assert.Single(snapshot.Files, file => file.Path == "empty.txt");
        Assert.Equal("deleted", empty.Kind);
        Assert.Contains("+++ /dev/null", empty.Diff);
        var created = Assert.Single(snapshot.Files, file => file.Path == "created.txt");
        Assert.Equal("added", created.Kind);
        Assert.Equal(1, created.AddedLines);
        Assert.Contains("--- /dev/null", created.Diff);
        File.Delete(Path.Combine(Workspace, "created.txt"));
        await WriteAsync("empty.txt", "");
        await WriteAsync("old.txt", "one\ntwo\n");
        Assert.Empty((await tracker.RefreshAsync()).Files);
    }

    [Fact]
    public async Task RestartLoadsOriginalBytesAndDoesNotAssumeCurrentWorkspaceAsBaseline()
    {
        await WriteAsync("saved.txt", "before\n");
        using (var original = new CodingWorkspaceChangeTracker(Workspace, Storage)) await original.InitializeAsync();
        var manifest = await File.ReadAllBytesAsync(Path.Combine(Storage, "baseline.json"));
        await WriteAsync("saved.txt", "after\n");
        using var resumed = new CodingWorkspaceChangeTracker(Workspace, Storage);
        var change = Assert.Single((await resumed.LoadExistingAsync()).Files);
        Assert.Contains("-before", change.Diff);
        Assert.Contains("+after", change.Diff);
        Assert.Equal(manifest, await File.ReadAllBytesAsync(Path.Combine(Storage, "baseline.json")));
    }

    [Fact]
    public async Task FileReplacedByDirectoryCountsOriginalDeletionAndNewNestedFile()
    {
        await WriteAsync("module", "original file\n");
        using var tracker = new CodingWorkspaceChangeTracker(Workspace, Storage);
        await tracker.InitializeAsync();
        File.Delete(Path.Combine(Workspace, "module"));
        await WriteAsync("module/implementation.py", "new implementation\n");
        var snapshot = await tracker.RefreshAsync();
        Assert.False(snapshot.IsPartial);
        Assert.Equal(2, snapshot.Files.Count);
        var deleted = Assert.Single(snapshot.Files, file => file.Path == "module");
        Assert.Equal("deleted", deleted.Kind);
        Assert.Equal(1, deleted.RemovedLines);
        Assert.Contains("+++ /dev/null", deleted.Diff);
        Assert.Equal("added", Assert.Single(snapshot.Files, file => file.Path == "module/implementation.py").Kind);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("interrupted")]
    [InlineData("corrupt")]
    [InlineData("null-arrays")]
    [InlineData("wrong-workspace")]
    public async Task MissingOrInvalidResumeBaselineIsExplicitlyPartialAndNeverRecaptured(string failure)
    {
        await WriteAsync("already-modified.txt", "current\n");
        if (failure != "missing") Directory.CreateDirectory(Storage);
        if (failure == "interrupted") await File.WriteAllTextAsync(Path.Combine(Storage, "capture-started"), "");
        if (failure == "corrupt") await File.WriteAllTextAsync(Path.Combine(Storage, "baseline.json"), "{}");
        if (failure == "null-arrays") await File.WriteAllTextAsync(Path.Combine(Storage, "baseline.json"),
            System.Text.Json.JsonSerializer.Serialize(new { version = 1, workspaceRoot = Workspace, files = (object?)null, skipped = (object?)null, notices = (object?)null }));
        if (failure == "wrong-workspace")
        {
            var other = Path.Combine(_temporary, "different-project");
            Directory.CreateDirectory(other);
            using var capture = new CodingWorkspaceChangeTracker(other, Storage);
            await capture.InitializeAsync();
        }
        using var tracker = new CodingWorkspaceChangeTracker(Workspace, Storage);
        var snapshot = await tracker.LoadExistingAsync();
        Assert.True(snapshot.IsPartial);
        Assert.NotEmpty(snapshot.Notice!);
        Assert.Empty(snapshot.Files);
        await WriteAsync("another.txt", "content");
        Assert.Empty((await tracker.RefreshAsync()).Files);
        if (failure == "missing") Assert.False(Directory.Exists(Storage));
    }

    [Fact]
    public async Task LargeAndBinaryFilesAreNeverReportedAsCompleteTextDiffs()
    {
        var binary = Path.Combine(Workspace, "picture.dat");
        await File.WriteAllBytesAsync(binary, [0, 1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(Workspace, "large.txt"), new byte[CodingWorkspaceChangeTracker.MaximumFileBytes + 1]);
        using var tracker = new CodingWorkspaceChangeTracker(Workspace, Storage);
        var initial = await tracker.InitializeAsync();
        Assert.True(initial.IsPartial);
        Assert.Contains("2 MiB", initial.Notice);
        await File.WriteAllBytesAsync(binary, [0, 4, 5, 6]);
        var snapshot = await tracker.RefreshAsync();
        var changed = Assert.Single(snapshot.Files);
        Assert.True(snapshot.IsPartial);
        Assert.True(changed.IsBinary);
        Assert.Equal("modified", changed.Kind);
        Assert.Equal(0, changed.AddedLines);
        Assert.Empty(changed.Diff);
        await File.WriteAllBytesAsync(binary, [0, 1, 2, 3]);
        Assert.Empty((await tracker.RefreshAsync()).Files);
    }

    [Fact]
    public async Task PollKeepsUnchangedSnapshotStableAndPeriodicAuditDetectsPreservedTimestampChange()
    {
        var clock = new ManualClock();
        await WriteAsync("same-size.txt", "old\n");
        using var tracker = new CodingWorkspaceChangeTracker(Workspace, Storage, clock);
        var original = await tracker.InitializeAsync();
        clock.Now += TimeSpan.FromSeconds(2);
        Assert.Same(original, await tracker.RefreshAsync());
        var path = Path.Combine(Workspace, "same-size.txt");
        var timestamp = File.GetLastWriteTimeUtc(path);
        await WriteAsync("same-size.txt", "new\n");
        File.SetLastWriteTimeUtc(path, timestamp);
        clock.Now += TimeSpan.FromMinutes(2);
        var changed = Assert.Single((await tracker.RefreshAsync()).Files);
        Assert.Contains("+new", changed.Diff);
        var snapshot = await tracker.RefreshAsync();
        clock.Now += TimeSpan.FromSeconds(2);
        Assert.Same(snapshot, await tracker.RefreshAsync());
        File.SetLastWriteTimeUtc(path, timestamp.AddMinutes(1));
        Assert.Same(snapshot, await tracker.RefreshAsync());
    }

    [Fact]
    public async Task GeneratedDirectoriesAndTrackerStorageAreExcludedWithoutSelfChanges()
    {
        await WriteAsync("src/main.py", "before\n");
        await WriteAsync("node_modules/pkg/code.js", "before\n");
        await WriteAsync(".pytest_cache/cache.txt", "before\n");
        using var tracker = new CodingWorkspaceChangeTracker(Workspace, Path.Combine(Workspace, "tracker-storage"));
        await tracker.InitializeAsync();
        await WriteAsync("node_modules/pkg/code.js", "after\n");
        await WriteAsync(".pytest_cache/cache.txt", "after\n");
        Assert.Empty((await tracker.RefreshAsync()).Files);
        await WriteAsync("src/main.py", "after\n");
        Assert.Equal("src/main.py", Assert.Single((await tracker.RefreshAsync()).Files).Path);
    }

    [Fact]
    public async Task FileLimitDoesNotMislabelPreviouslyUnobservedFilesAsNewOrDeleted()
    {
        for (var index = 0; index <= CodingWorkspaceChangeTracker.MaximumFiles; index++)
            await WriteAsync($"files/{index:D5}.txt", "");
        using var tracker = new CodingWorkspaceChangeTracker(Workspace, Storage);
        var baseline = await tracker.InitializeAsync();
        Assert.True(baseline.IsPartial);
        Assert.Equal(CodingWorkspaceChangeTracker.MaximumFiles, baseline.ScannedFiles);
        await WriteAsync("unknown-at-start.txt", "cannot claim this was absent at start");
        var snapshot = await tracker.RefreshAsync();
        Assert.True(snapshot.IsPartial);
        Assert.Empty(snapshot.Files);
    }

    [Fact]
    public async Task BaselineAbove64MiBRetainsAllFilesAndDetectsLaterEdits()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('x', CodingWorkspaceChangeTracker.MaximumFileBytes));
        for (var index = 0; index < 33; index++) await File.WriteAllBytesAsync(Path.Combine(Workspace, $"{index:D2}.txt"), bytes);
        using var tracker = new CodingWorkspaceChangeTracker(Workspace, Storage);
        var baseline = await tracker.InitializeAsync();
        Assert.False(baseline.IsPartial);
        await WriteAsync("32.txt", "now small");
        Assert.Equal("32.txt", Assert.Single((await tracker.RefreshAsync()).Files).Path);
    }

    [Fact]
    public async Task UnreadableAndMissingBaselineContentAreExplicitlyPartial()
    {
        await WriteAsync("locked.txt", "original");
        using (var locked = new FileStream(Path.Combine(Workspace, "locked.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var tracker = new CodingWorkspaceChangeTracker(Workspace, Storage))
            Assert.True((await tracker.InitializeAsync()).IsPartial);
        using var resumed = new CodingWorkspaceChangeTracker(Workspace, Storage);
        await WriteAsync("locked.txt", "changed");
        Assert.Empty((await resumed.LoadExistingAsync()).Files);
        Assert.True((await resumed.RefreshAsync()).IsPartial);

        var otherStorage = Path.Combine(_temporary, "other-baseline");
        using (var tracker = new CodingWorkspaceChangeTracker(Workspace, otherStorage)) await tracker.InitializeAsync();
        File.Delete(Directory.GetFiles(Path.Combine(otherStorage, "blobs"))[0]);
        using var missingBlob = new CodingWorkspaceChangeTracker(Workspace, otherStorage);
        Assert.True((await missingBlob.LoadExistingAsync()).IsPartial);
        Assert.Empty((await missingBlob.RefreshAsync()).Files);
    }

    [Fact]
    public async Task JunctionToOutsideWorkspaceIsSkippedAndNeverCreatesExternalDiffs()
    {
        var outside = Path.Combine(_temporary, "outside");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "external.txt"), "before");
        var link = Path.Combine(Workspace, "linked");
        // cmd's /c tail is a command line, not a single CRT-escaped argument.
        // Both quoted paths are generated under this test's private temporary directory.
        var start = new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/d /c mklink /J \"{link}\" \"{outside}\"",
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync();
        try
        {
            Assert.True(process.ExitCode == 0, await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync());
            using var tracker = new CodingWorkspaceChangeTracker(Workspace, Storage);
            Assert.True((await tracker.InitializeAsync()).IsPartial);
            await File.WriteAllTextAsync(Path.Combine(outside, "external.txt"), "after");
            var snapshot = await tracker.RefreshAsync();
            Assert.Empty(snapshot.Files);
            Assert.Contains("Symlinks", snapshot.Notice);
        }
        finally
        {
            if (Directory.Exists(link) && File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint))
                Directory.Delete(link, recursive: false);
        }
    }

    [Fact]
    public async Task LongPathsAndMetadataVolumeHaveExplicitCoverageLimits()
    {
        var prefix = string.Join('/', Enumerable.Repeat(new string('a', 180), 5));
        for (var index = 0; index < 560; index++) await WriteAsync($"{prefix}/{index:D5}.txt", "");
        var tooLong = prefix + "/" + new string('b', 180) + "/source.txt";
        await WriteAsync(tooLong, "too long");
        using var tracker = new CodingWorkspaceChangeTracker(Workspace, Storage);
        var snapshot = await tracker.InitializeAsync();
        Assert.True(snapshot.IsPartial);
        Assert.Contains("500.000", snapshot.Notice);
        Assert.True(new FileInfo(Path.Combine(Storage, "baseline.json")).Length < 8 * 1024 * 1024);

        var separateStorage = Path.Combine(_temporary, "long-path-baseline");
        var separateWorkspace = Path.Combine(_temporary, "long-path-project");
        var fullPath = Path.Combine(separateWorkspace, tooLong);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, "long path only");
        using var longOnly = new CodingWorkspaceChangeTracker(separateWorkspace, separateStorage);
        var longSnapshot = await longOnly.InitializeAsync();
        Assert.True(longSnapshot.IsPartial);
        Assert.Contains("1.024", longSnapshot.Notice);
        Assert.Empty(longSnapshot.Files);
    }

    [Fact]
    public async Task TotalDiffPayloadIsBoundedWhileAllChangedFilesAndLineCountsRemain()
    {
        var original = string.Concat(Enumerable.Range(0, 8000).Select(index => $"old-{index:D5}-unchanged-length\n"));
        var updated = original.Replace("old-", "new-", StringComparison.Ordinal);
        await WriteAsync("a.txt", original);
        await WriteAsync("b.txt", original);
        using var tracker = new CodingWorkspaceChangeTracker(Workspace, Storage);
        await tracker.InitializeAsync();
        await WriteAsync("a.txt", updated);
        await WriteAsync("b.txt", updated);
        var snapshot = await tracker.RefreshAsync();
        Assert.Equal(2, snapshot.Files.Count);
        Assert.All(snapshot.Files, file => { Assert.Equal(8000, file.AddedLines); Assert.Equal(8000, file.RemovedLines); });
        Assert.True(snapshot.Files.Sum(static file => file.Diff.Length) <= CodingWorkspaceChangeTracker.MaximumDiffCharacters);
        Assert.True(snapshot.IsPartial);
        Assert.Contains(snapshot.Files, file => file.DiffTruncated);
    }

    [Fact]
    public async Task CancellationBeforeCaptureDoesNotCreateAFalsePersistedBaseline()
    {
        using var tracker = new CodingWorkspaceChangeTracker(Workspace, Storage);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tracker.InitializeAsync(cancellation.Token));
        Assert.False(File.Exists(Path.Combine(Storage, "baseline.json")));
    }

    private async Task WriteAsync(string relative, string text)
    {
        var path = Path.Combine(Workspace, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));
    }

    public void Dispose()
    {
        var full = Path.GetFullPath(_temporary);
        if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid test cleanup path.");
        Directory.Delete(full, recursive: true);
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
