using GoWinUI.Infrastructure;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GoWinUI.App.Services;

public sealed record CodingDiffSnapshot(
    string Diff,
    int FileCount,
    int AddedLines,
    int DeletedLines,
    bool IsTruncated);

/// <summary>
/// Captures an immutable Git tree before a coding run and compares later workspace
/// states with that tree. A private index is used, so the user's staging area and
/// existing dirty worktree are never changed.
/// </summary>
public sealed partial class CodingDiffService
{
    private const int MaximumDiffCharacters = 2_000_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _stateDirectory;

    public CodingDiffService(GoInfrastructureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _stateDirectory = Path.Combine(options.DataDirectory, "CodingRuns");
    }

    public async Task<bool> BeginAsync(
        Guid runId,
        string? workspacePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            return false;
        }

        var runDirectory = RunDirectory(runId);
        var metadataPath = Path.Combine(runDirectory, "baseline.json");
        if (File.Exists(metadataPath))
        {
            return true;
        }

        try
        {
            var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
            var repositoryRoot = await ResolveRepositoryRootAsync(workspace, cancellationToken).ConfigureAwait(false);
            if (repositoryRoot is null || !IsWithin(repositoryRoot, workspace))
            {
                return false;
            }

            Directory.CreateDirectory(runDirectory);
            var pathSpec = Path.GetRelativePath(repositoryRoot, workspace).Replace('\\', '/');
            var tree = await CaptureTreeAsync(repositoryRoot, pathSpec, runDirectory, cancellationToken).ConfigureAwait(false);
            if (tree is null)
            {
                return false;
            }

            var metadata = new BaselineMetadata(repositoryRoot, workspace, pathSpec, tree, DateTimeOffset.UtcNow);
            var temporaryPath = metadataPath + ".tmp";
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(metadata, JsonOptions),
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, metadataPath, overwrite: true);
            PruneOldRuns(runId);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    public async Task<CodingDiffSnapshot?> RefreshAsync(
        Guid runId,
        string? workspacePath,
        CancellationToken cancellationToken = default)
    {
        var runDirectory = RunDirectory(runId);
        var metadataPath = Path.Combine(runDirectory, "baseline.json");
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        try
        {
            var metadata = JsonSerializer.Deserialize<BaselineMetadata>(
                await File.ReadAllTextAsync(metadataPath, cancellationToken).ConfigureAwait(false),
                JsonOptions);
            if (metadata is null
                || string.IsNullOrWhiteSpace(workspacePath)
                || !PathsEqual(metadata.WorkspacePath, workspacePath)
                || !Directory.Exists(metadata.RepositoryRoot))
            {
                return null;
            }

            var currentTree = await CaptureTreeAsync(
                metadata.RepositoryRoot,
                metadata.PathSpec,
                runDirectory,
                cancellationToken).ConfigureAwait(false);
            if (currentTree is null)
            {
                return null;
            }

            var result = await RunGitAsync(
                metadata.RepositoryRoot,
                [
                    "diff", "--no-ext-diff", "--no-color", "--no-textconv",
                    "--find-renames", "--find-copies", "--unified=3",
                    metadata.BaselineTree, currentTree, "--", metadata.PathSpec,
                ],
                environment: null,
                cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                return null;
            }

            var normalized = result.StandardOutput.ReplaceLineEndings("\n");
            var truncated = normalized.Length > MaximumDiffCharacters;
            if (truncated)
            {
                var boundary = normalized.LastIndexOf('\n', MaximumDiffCharacters - 1);
                normalized = normalized[..(boundary > 0 ? boundary + 1 : MaximumDiffCharacters)]
                    + "\n[Git-Diff wurde für die Chatdarstellung gekürzt.]\n";
            }

            var lines = normalized.Split('\n');
            return new(
                normalized,
                DiffHeaderRegex().Count(normalized),
                lines.Count(static line => line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal)),
                lines.Count(static line => line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal)),
                truncated);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    internal static bool PathsEqual(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        StringComparison.OrdinalIgnoreCase);

    private static async Task<string?> ResolveRepositoryRootAsync(string workspace, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            workspace,
            ["rev-parse", "--show-toplevel"],
            environment: null,
            cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput)
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(result.StandardOutput.Trim()))
            : null;
    }

    private static async Task<string?> CaptureTreeAsync(
        string repositoryRoot,
        string pathSpec,
        string runDirectory,
        CancellationToken cancellationToken)
    {
        var indexPath = Path.Combine(runDirectory, $"index-{Guid.NewGuid():N}");
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["GIT_INDEX_FILE"] = indexPath,
        };
        try
        {
            var readTree = await RunGitAsync(
                repositoryRoot,
                ["read-tree", "HEAD"],
                environment,
                cancellationToken).ConfigureAwait(false);
            if (readTree.ExitCode != 0)
            {
                readTree = await RunGitAsync(
                    repositoryRoot,
                    ["read-tree", "--empty"],
                    environment,
                    cancellationToken).ConfigureAwait(false);
            }
            if (readTree.ExitCode != 0) return null;

            var add = await RunGitAsync(
                repositoryRoot,
                ["add", "-A", "--", pathSpec],
                environment,
                cancellationToken).ConfigureAwait(false);
            if (add.ExitCode != 0) return null;

            var writeTree = await RunGitAsync(
                repositoryRoot,
                ["write-tree"],
                environment,
                cancellationToken).ConfigureAwait(false);
            return writeTree.ExitCode == 0 && GitObjectIdRegex().IsMatch(writeTree.StandardOutput.Trim())
                ? writeTree.StandardOutput.Trim()
                : null;
        }
        finally
        {
            TryDelete(indexPath);
            TryDelete(indexPath + ".lock");
        }
    }

    private static async Task<GitResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                if (pair.Value is null) startInfo.Environment.Remove(pair.Key);
                else startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) return new(-1, string.Empty, "Git konnte nicht gestartet werden.");
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        return new(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private string RunDirectory(Guid runId) => Path.Combine(_stateDirectory, runId.ToString("N"));

    private static bool IsWithin(string root, string path)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return string.Equals(normalizedRoot, normalizedPath, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private void PruneOldRuns(Guid currentRunId)
    {
        try
        {
            if (!Directory.Exists(_stateDirectory)) return;
            var cutoff = DateTime.UtcNow.AddDays(-7);
            foreach (var directory in Directory.EnumerateDirectories(_stateDirectory))
            {
                if (string.Equals(Path.GetFileName(directory), currentRunId.ToString("N"), StringComparison.OrdinalIgnoreCase)) continue;
                if (Directory.GetLastWriteTimeUtc(directory) < cutoff) Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Diff cleanup is best effort and must never interrupt an AI run.
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    [GeneratedRegex("^diff --git ", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex DiffHeaderRegex();

    [GeneratedRegex("^[0-9a-f]{40,64}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GitObjectIdRegex();

    private sealed record BaselineMetadata(
        string RepositoryRoot,
        string WorkspacePath,
        string PathSpec,
        string BaselineTree,
        DateTimeOffset CreatedAt);

    private sealed record GitResult(int ExitCode, string StandardOutput, string StandardError);
}
