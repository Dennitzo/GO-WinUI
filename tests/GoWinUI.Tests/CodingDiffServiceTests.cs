using GoWinUI.App.Services;
using GoWinUI.Infrastructure;
using System.Text;

namespace GoWinUI.Tests;

public sealed class CodingDiffServiceTests
{
    [Fact]
    public async Task PlainWorkspaceUsesDirectMutationDiffsWithoutCreatingAGitRepository()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"go-coding-diff-plain-{Guid.NewGuid():N}");
        var root = Path.Combine(testRoot, "workspace");
        var state = Path.Combine(testRoot, "state");
        Directory.CreateDirectory(root);
        try
        {
            var runId = Guid.NewGuid();
            var service = new CodingDiffService(new GoInfrastructureOptions { DataDirectory = state });
            Assert.True(await service.BeginAsync(runId, root));
            Assert.False(Directory.Exists(Path.Combine(root, ".git")));

            await service.RecordMutationAsync(
                runId,
                "proposal-edit",
                Text("existing.cs", "baseline\n"),
                Text("existing.cs", "baseline\nchanged after start\n"));
            await service.RecordMutationAsync(
                runId,
                "proposal-create",
                Missing("created.cs"),
                Text("created.cs", "new file\n"));

            var snapshot = Assert.IsType<CodingDiffSnapshot>(await service.RefreshAsync(runId, root));
            Assert.Equal(2, snapshot.FileCount);
            Assert.Contains("+changed after start", snapshot.Diff, StringComparison.Ordinal);
            Assert.Contains("created.cs", snapshot.Diff, StringComparison.Ordinal);
            Assert.DoesNotContain("+.git", snapshot.Diff, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(root, ".git")));
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task ReplayedProposalIsRecordedExactlyOnce()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"go-coding-diff-idempotent-{Guid.NewGuid():N}");
        var root = Path.Combine(testRoot, "workspace");
        var state = Path.Combine(testRoot, "state");
        Directory.CreateDirectory(root);
        try
        {
            var runId = Guid.NewGuid();
            var service = new CodingDiffService(new GoInfrastructureOptions { DataDirectory = state });
            Assert.True(await service.BeginAsync(runId, root));
            var before = Text("chapter.md", "# Kapitel\n");
            var after = Text("chapter.md", "# Kapitel\n\nInhalt\n");

            await service.RecordMutationAsync(runId, "proposal-1", before, after);
            await service.RecordMutationAsync(runId, "proposal-1", before, after);

            var snapshot = Assert.IsType<CodingDiffSnapshot>(await service.RefreshAsync(runId, root));
            Assert.Equal(1, snapshot.FileCount);
            Assert.Equal(1, Count(snapshot.Diff, "diff --git "));
            Assert.Equal(2, snapshot.AddedLines);
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task ExistingRepositoryAndIndexRemainUntouched()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"go-coding-diff-git-{Guid.NewGuid():N}");
        var root = Path.Combine(testRoot, "workspace");
        var state = Path.Combine(testRoot, "state");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var marker = Path.Combine(root, ".git", "index.marker");
        await File.WriteAllTextAsync(marker, "unchanged", new UTF8Encoding(false));
        try
        {
            var runId = Guid.NewGuid();
            var service = new CodingDiffService(new GoInfrastructureOptions { DataDirectory = state });
            Assert.True(await service.BeginAsync(runId, root));
            await service.RecordMutationAsync(
                runId,
                "proposal-rename",
                Text("old.cs", "class Old {}\n"),
                Text("new.cs", "class New {}\n"));

            var snapshot = Assert.IsType<CodingDiffSnapshot>(await service.RefreshAsync(runId, root));
            Assert.Contains("a/old.cs", snapshot.Diff, StringComparison.Ordinal);
            Assert.Contains("b/new.cs", snapshot.Diff, StringComparison.Ordinal);
            Assert.Equal("unchanged", await File.ReadAllTextAsync(marker));
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    private static CodingMutationState Text(string path, string text) =>
        new(path, true, false, text, Encoding.UTF8.GetByteCount(text));

    private static CodingMutationState Missing(string path) => new(path, false, false, null, 0);

    private static int Count(string source, string value)
    {
        var count = 0;
        for (var offset = 0; (offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0; offset += value.Length) count++;
        return count;
    }

    private static void DeleteTestRoot(string testRoot)
    {
        if (!Directory.Exists(testRoot)) return;
        foreach (var file in Directory.EnumerateFiles(testRoot, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(testRoot, recursive: true);
    }
}
