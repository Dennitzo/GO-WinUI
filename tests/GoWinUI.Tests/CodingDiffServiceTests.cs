using GoWinUI.App.Services;
using GoWinUI.Infrastructure;
using System.Diagnostics;
using System.Text;

namespace GoWinUI.Tests;

public sealed class CodingDiffServiceTests
{
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
            if (Directory.Exists(testRoot))
            {
                foreach (var file in Directory.EnumerateFiles(testRoot, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(testRoot, recursive: true);
            }
        }
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
