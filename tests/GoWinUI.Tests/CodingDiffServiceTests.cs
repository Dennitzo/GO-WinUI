using GoWinUI.App.Services;
using GoWinUI.Infrastructure;
using System.Diagnostics;
using System.Text;

namespace GoWinUI.Tests;

public sealed class CodingDiffServiceTests
{
    [Fact]
    public async Task PlainWorkspaceIsInitializedAndDiffedWithoutStagingOrCommittingFiles()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"go-coding-diff-plain-{Guid.NewGuid():N}");
        var root = Path.Combine(testRoot, "workspace");
        var state = Path.Combine(testRoot, "state");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "existing.cs"), "baseline\n", new UTF8Encoding(false));
            Assert.False(Directory.Exists(Path.Combine(root, ".git")));
            var runId = Guid.NewGuid();
            var service = new CodingDiffService(new GoInfrastructureOptions { DataDirectory = state });

            Assert.True(await service.BeginAsync(runId, root));
            Assert.True(Directory.Exists(Path.Combine(root, ".git")));
            Assert.Equal(string.Empty, await GitAsync(root, "diff", "--cached", "--binary"));
            Assert.False(await HasGitHeadAsync(root));

            await File.WriteAllTextAsync(
                Path.Combine(root, "existing.cs"),
                "baseline\nchanged after start\n",
                new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(root, "created.cs"), "new file\n", new UTF8Encoding(false));

            var snapshot = Assert.IsType<CodingDiffSnapshot>(await service.RefreshAsync(runId, root));
            Assert.Equal(2, snapshot.FileCount);
            Assert.Contains("+changed after start", snapshot.Diff, StringComparison.Ordinal);
            Assert.Contains("created.cs", snapshot.Diff, StringComparison.Ordinal);
            Assert.DoesNotContain("+baseline", snapshot.Diff, StringComparison.Ordinal);
            Assert.Equal(string.Empty, await GitAsync(root, "diff", "--cached", "--binary"));
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task GeneratedFrameworkTreesAreExcludedFromCapturedDiff()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"go-coding-diff-generated-{Guid.NewGuid():N}");
        var root = Path.Combine(testRoot, "workspace");
        var state = Path.Combine(testRoot, "state");
        Directory.CreateDirectory(Path.Combine(root, ".lake", "packages", "Cli"));
        Directory.CreateDirectory(Path.Combine(root, ".venv", "Lib", "site-packages"));
        Directory.CreateDirectory(Path.Combine(root, "target", "debug"));
        try
        {
            // Regression: combining an explicitly ignored directory with negative Git
            // pathspecs made `git add` return exit code 1 and left normal coding runs
            // without baseline.json or any subsequent code-diff updates.
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "/.lake/\n", new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(root, "chapter.md"), "baseline\n", new UTF8Encoding(false));
            await File.WriteAllTextAsync(
                Path.Combine(root, ".lake", "packages", "Cli", "Generated.lean"),
                "theorem generated : True := by trivial\n",
                new UTF8Encoding(false));
            await File.WriteAllTextAsync(
                Path.Combine(root, ".venv", "Lib", "site-packages", "generated.py"),
                "generated = True\n",
                new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(root, "target", "debug", "generated.log"), "cache\n", new UTF8Encoding(false));

            var runId = Guid.NewGuid();
            var service = new CodingDiffService(new GoInfrastructureOptions { DataDirectory = state });
            Assert.True(await service.BeginAsync(runId, root));

            await File.WriteAllTextAsync(Path.Combine(root, "chapter.md"), "baseline\nreal change\n", new UTF8Encoding(false));
            await File.WriteAllTextAsync(
                Path.Combine(root, ".lake", "packages", "Cli", "Generated.lean"),
                "theorem generated : True := by trivial\n-- changed cache\n",
                new UTF8Encoding(false));
            await File.WriteAllTextAsync(
                Path.Combine(root, ".venv", "Lib", "site-packages", "generated.py"),
                "generated = False\n",
                new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(root, "target", "debug", "generated.log"), "changed cache\n", new UTF8Encoding(false));

            var snapshot = Assert.IsType<CodingDiffSnapshot>(await service.RefreshAsync(runId, root));
            Assert.Equal(1, snapshot.FileCount);
            Assert.Contains("chapter.md", snapshot.Diff, StringComparison.Ordinal);
            Assert.Contains("+real change", snapshot.Diff, StringComparison.Ordinal);
            Assert.DoesNotContain(".lake", snapshot.Diff, StringComparison.Ordinal);
            Assert.DoesNotContain(".venv", snapshot.Diff, StringComparison.Ordinal);
            Assert.DoesNotContain("target", snapshot.Diff, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task DiffContainsOnlyChangesMadeAfterRunBaselineAndNeverTouchesRealIndex()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"go-coding-diff-{Guid.NewGuid():N}");
        var root = Path.Combine(testRoot, "repository");
        var state = Path.Combine(testRoot, "state");
        Directory.CreateDirectory(root);
        try
        {
            await GitAsync(root, "init");
            await GitAsync(root, "config", "user.email", "go-tests@example.invalid");
            await GitAsync(root, "config", "user.name", "GO Tests");
            await GitAsync(root, "config", "core.autocrlf", "false");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.cs"), "original\n", new UTF8Encoding(false));
            await GitAsync(root, "add", "tracked.cs");
            await GitAsync(root, "commit", "-m", "baseline");

            // These changes exist before Qwen3-Coder-Next starts and must not be attributed to it.
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.cs"), "original\npreexisting dirty line\n", new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(root, "preexisting-staged.txt"), "already staged\n", new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(root, "preexisting-untracked.txt"), "already untracked\n", new UTF8Encoding(false));
            await GitAsync(root, "add", "preexisting-staged.txt");
            var stagedBefore = await GitAsync(root, "diff", "--cached", "--binary");

            var runId = Guid.NewGuid();
            var service = new CodingDiffService(new GoInfrastructureOptions { DataDirectory = state });
            Assert.True(await service.BeginAsync(runId, root));

            await File.WriteAllTextAsync(
                Path.Combine(root, "tracked.cs"),
                "original\npreexisting dirty line\nQwen UI change\n",
                new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(root, "created-by-qwen.xaml"), "<Grid />\n", new UTF8Encoding(false));

            var snapshot = Assert.IsType<CodingDiffSnapshot>(await service.RefreshAsync(runId, root));
            Assert.Equal(2, snapshot.FileCount);
            Assert.Contains("+Qwen UI change", snapshot.Diff, StringComparison.Ordinal);
            Assert.Contains("created-by-qwen.xaml", snapshot.Diff, StringComparison.Ordinal);
            Assert.DoesNotContain("+preexisting dirty line", snapshot.Diff, StringComparison.Ordinal);
            Assert.DoesNotContain("preexisting-staged.txt", snapshot.Diff, StringComparison.Ordinal);
            Assert.DoesNotContain("preexisting-untracked.txt", snapshot.Diff, StringComparison.Ordinal);
            Assert.Equal(stagedBefore, await GitAsync(root, "diff", "--cached", "--binary"));
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    private static async Task<bool> HasGitHeadAsync(string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("rev-parse");
        startInfo.ArgumentList.Add("--verify");
        startInfo.ArgumentList.Add("HEAD");
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Git konnte nicht gestartet werden.");
        await process.WaitForExitAsync();
        return process.ExitCode == 0;
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

    private static async Task<string> GitAsync(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Git konnte nicht gestartet werden.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await output;
        var stderr = await error;
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} fehlgeschlagen: {stderr}");
        return stdout.ReplaceLineEndings("\n");
    }
}
