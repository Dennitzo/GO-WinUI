using GoWinUI.Core.Coding;
using System.Diagnostics;

namespace GoWinUI.Tests;

public sealed class CodingGitChangeTrackerTests
{
    [Fact]
    public async Task GitDiffTracksNewEditedDeletedAndRevertedFilesWithoutChangingUserIndex()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "workspace")).FullName;
        await File.WriteAllTextAsync(Path.Combine(workspace, "source.txt"), "before\n");
        await File.WriteAllTextAsync(Path.Combine(workspace, "deleted.txt"), "deleted\n");
        await CodingWorkspaceGit.EnsureRepositoryAsync(workspace);
        await GitAsync(workspace, ["add", "source.txt"]);
        var index = await File.ReadAllBytesAsync(Path.Combine(workspace, ".git", "index"));
        using var tracker = new CodingGitChangeTracker(workspace, Path.Combine(environment.Directory, "run"));
        Assert.Empty((await tracker.InitializeAsync()).Files);
        await File.WriteAllTextAsync(Path.Combine(workspace, "source.txt"), "after\n");
        await File.WriteAllTextAsync(Path.Combine(workspace, "new file.txt"), "new\n");
        File.Delete(Path.Combine(workspace, "deleted.txt"));
        var changes = await tracker.RefreshAsync();
        Assert.False(changes.IsPartial);
        Assert.Equal(3, changes.Files.Count);
        var edit = Assert.Single(changes.Files, file => file.Path == "source.txt");
        Assert.Equal(1, edit.AddedLines);
        Assert.Equal(1, edit.RemovedLines);
        Assert.Contains("diff --git", edit.Diff);
        Assert.Contains("-before", edit.Diff);
        Assert.Contains("+after", edit.Diff);
        Assert.Equal(index, await File.ReadAllBytesAsync(Path.Combine(workspace, ".git", "index")));
        await File.WriteAllTextAsync(Path.Combine(workspace, "source.txt"), "before\n");
        await File.WriteAllTextAsync(Path.Combine(workspace, "deleted.txt"), "deleted\n");
        File.Delete(Path.Combine(workspace, "new file.txt"));
        Assert.Empty((await tracker.RefreshAsync()).Files);
    }

    [Fact]
    public async Task RestartPreservesRunStartAndNextRunStartsEmpty()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "workspace")).FullName;
        var storage = Path.Combine(environment.Directory, "run");
        var path = Path.Combine(workspace, "source.txt");
        await File.WriteAllTextAsync(path, "original\n");
        using (var initial = new CodingGitChangeTracker(workspace, storage)) await initial.InitializeAsync();
        await File.WriteAllTextAsync(path, "changed\n");
        using (var resumed = new CodingGitChangeTracker(workspace, storage))
            Assert.Contains("-original", Assert.Single((await resumed.LoadExistingAsync()).Files).Diff);
        using var next = new CodingGitChangeTracker(workspace, Path.Combine(environment.Directory, "next"));
        Assert.Empty((await next.InitializeAsync()).Files);
    }

    [Fact]
    public async Task LargeWorkspaceAndTextFilesHaveNoOldSnapshotLimits()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "workspace")).FullName;
        await using (var large = File.Create(Path.Combine(workspace, "data.bin"))) large.SetLength(65L * 1024 * 1024);
        var path = Path.Combine(workspace, "large.txt");
        await File.WriteAllTextAsync(path, string.Concat(Enumerable.Repeat("content line\n", 200_000)));
        using var tracker = new CodingGitChangeTracker(workspace, Path.Combine(environment.Directory, "run"));
        Assert.False((await tracker.InitializeAsync()).IsPartial);
        await File.AppendAllTextAsync(path, "new final line\n");
        var changes = await tracker.RefreshAsync();
        Assert.False(changes.IsPartial);
        var file = Assert.Single(changes.Files);
        Assert.Equal(1, file.AddedLines);
        Assert.Contains("+new final line", file.Diff);
    }

    private static async Task GitAsync(string workspace, string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = workspace, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
    }
}
