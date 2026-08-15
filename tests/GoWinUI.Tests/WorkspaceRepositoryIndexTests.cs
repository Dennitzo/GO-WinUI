using GoWinUI.App.Services;
using GoWinUI.Infrastructure;

namespace GoWinUI.Tests;

public sealed class WorkspaceRepositoryIndexTests
{
    [Fact]
    public async Task IndexKeepsDotDirectoriesAndArbitraryFileTypesButIgnoresGeneratedOutputs()
    {
        var root = Path.Combine(Path.GetTempPath(), $"go-workspace-index-{Guid.NewGuid():N}");
        var data = Path.Combine(Path.GetTempPath(), $"go-workspace-data-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, ".github", "workflows"));
        Directory.CreateDirectory(Path.Combine(root, "firmware"));
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "ignored.tmp\n");
        await File.WriteAllTextAsync(Path.Combine(root, ".github", "workflows", "build.yml"), "name: Build\n");
        await File.WriteAllTextAsync(Path.Combine(root, "firmware", "main.zig"), "pub fn main() void {}\n");
        await File.WriteAllTextAsync(Path.Combine(root, "system.customlang"), "arbitrary source\n");
        await File.WriteAllTextAsync(Path.Combine(root, "ignored.tmp"), "ignored\n");
        await File.WriteAllTextAsync(Path.Combine(root, "bin", "generated.customlang"), "generated\n");
        try
        {
            using var index = new WorkspaceRepositoryIndex(new GoInfrastructureOptions { DataDirectory = data });

            var snapshot = await index.GetSnapshotAsync(root);

            Assert.Contains(snapshot.Entries, entry => entry.Path == ".gitignore");
            Assert.Contains(snapshot.Entries, entry => entry.Path == ".github/workflows/build.yml");
            Assert.Contains(snapshot.Entries, entry => entry.Path == "firmware/main.zig" && entry.Language == "ZIG");
            Assert.Contains(snapshot.Entries, entry => entry.Path == "system.customlang" && entry.Language == "CUSTOMLANG");
            Assert.DoesNotContain(snapshot.Entries, entry => entry.Path == "ignored.tmp");
            Assert.DoesNotContain(snapshot.Entries, entry => entry.Path.StartsWith("bin/", StringComparison.Ordinal));
            Assert.True(WorkspaceRepositoryIndex.MatchesGlob("system.customlang", "**/*"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            try { Directory.Delete(data, recursive: true); } catch (IOException) { }
        }
    }
}
