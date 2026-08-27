using GoWinUI.Infrastructure;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace GoWinUI.App.Services;

public sealed record CodingDiffSnapshot(
    string Diff,
    int FileCount,
    int AddedLines,
    int DeletedLines,
    bool IsTruncated);

public sealed record CodingMutationState(
    string Path,
    bool Exists,
    bool IsBinary,
    string? Text,
    long Length);

/// <summary>
/// Persists only direct mutation diffs. It never indexes the workspace, snapshots
/// repository contents, initializes Git, or stores complete source files.
/// </summary>
public sealed class CodingDiffService : IDisposable
{
    private const int MaximumDiffCharacters = 2_000_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _stateDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);

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
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath)) return false;

        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
        var runDirectory = RunDirectory(runId);
        var metadataPath = MetadataPath(runId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(runDirectory);
            if (File.Exists(metadataPath))
            {
                var existing = JsonSerializer.Deserialize<RunMetadata>(
                    await File.ReadAllTextAsync(metadataPath, cancellationToken).ConfigureAwait(false),
                    JsonOptions);
                return existing is not null && PathsEqual(existing.WorkspacePath, workspace);
            }

            var temporary = metadataPath + ".tmp";
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(new RunMetadata(workspace, DateTimeOffset.UtcNow), JsonOptions),
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporary, metadataPath, overwrite: true);
            PruneOldRuns(runId);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordMutationAsync(
        Guid runId,
        string proposalId,
        CodingMutationState before,
        CodingMutationState after,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalId);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (!File.Exists(MetadataPath(runId))) return;

        var entry = await CreateEntryAsync(proposalId, before, after, cancellationToken).ConfigureAwait(false);
        if (entry is null) return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await ReadEntriesAsync(runId, cancellationToken).ConfigureAwait(false);
            if (entries.Any(value => string.Equals(value.ProposalId, proposalId, StringComparison.Ordinal))) return;
            var json = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;
            await File.AppendAllTextAsync(EntriesPath(runId), json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CodingDiffSnapshot?> RefreshAsync(
        Guid runId,
        string? workspacePath,
        CancellationToken cancellationToken = default)
    {
        var metadataPath = MetadataPath(runId);
        if (!File.Exists(metadataPath) || string.IsNullOrWhiteSpace(workspacePath)) return null;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var metadata = JsonSerializer.Deserialize<RunMetadata>(
                await File.ReadAllTextAsync(metadataPath, cancellationToken).ConfigureAwait(false),
                JsonOptions);
            if (metadata is null || !PathsEqual(metadata.WorkspacePath, workspacePath)) return null;

            var entries = await ReadEntriesAsync(runId, cancellationToken).ConfigureAwait(false);
            var builder = new StringBuilder();
            var added = 0;
            var deleted = 0;
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var truncated = false;
            foreach (var entry in entries)
            {
                var separator = builder.Length == 0 ? string.Empty : "\n";
                if (builder.Length + separator.Length + entry.Diff.Length > MaximumDiffCharacters)
                {
                    truncated = true;
                    break;
                }
                builder.Append(separator).Append(entry.Diff.TrimEnd()).Append('\n');
                added += entry.AddedLines;
                deleted += entry.DeletedLines;
                paths.Add(entry.AfterPath ?? entry.BeforePath ?? "(unbekannt)");
            }
            if (truncated) builder.Append("\n[Änderungsdiff wurde für die Chatdarstellung gekürzt.]\n");
            return new(builder.ToString(), paths.Count, added, deleted, truncated);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static bool PathsEqual(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        StringComparison.OrdinalIgnoreCase);

    private static async Task<MutationEntry?> CreateEntryAsync(
        string proposalId,
        CodingMutationState before,
        CodingMutationState after,
        CancellationToken cancellationToken)
    {
        if (!before.Exists && !after.Exists) return null;
        if (before.Exists == after.Exists
            && string.Equals(before.Path, after.Path, StringComparison.OrdinalIgnoreCase)
            && string.Equals(before.Text, after.Text, StringComparison.Ordinal)
            && before.IsBinary == after.IsBinary
            && before.Length == after.Length)
        {
            return null;
        }

        var beforePath = before.Exists ? NormalizePath(before.Path) : null;
        var afterPath = after.Exists ? NormalizePath(after.Path) : null;
        string diff;
        int added;
        int deleted;
        if (before.IsBinary || after.IsBinary || (before.Exists && before.Text is null) || (after.Exists && after.Text is null))
        {
            diff = $"diff --git a/{beforePath ?? afterPath} b/{afterPath ?? beforePath}\n"
                + $"Binary files {(beforePath is null ? "/dev/null" : "a/" + beforePath)} and {(afterPath is null ? "/dev/null" : "b/" + afterPath)} differ\n";
            added = 0;
            deleted = 0;
        }
        else
        {
            diff = await CreateTextDiffAsync(beforePath, before.Text ?? string.Empty, afterPath, after.Text ?? string.Empty, cancellationToken).ConfigureAwait(false);
            var lines = diff.Split('\n');
            added = lines.Count(static line => line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal));
            deleted = lines.Count(static line => line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal));
        }
        return new MutationEntry(proposalId, beforePath, afterPath, diff, added, deleted, DateTimeOffset.UtcNow);
    }

    private static async Task<string> CreateTextDiffAsync(
        string? beforePath,
        string before,
        string? afterPath,
        string after,
        CancellationToken cancellationToken)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "GO-Coding-Diff", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var beforeFile = Path.Combine(tempRoot, "before.txt");
        var afterFile = Path.Combine(tempRoot, "after.txt");
        try
        {
            await File.WriteAllTextAsync(beforeFile, before, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(afterFile, after, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            var generated = await RunNoIndexDiffAsync(tempRoot, cancellationToken).ConfigureAwait(false);
            return RewriteHeader(generated, beforePath, afterPath, before, after);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task<string> RunNoIndexDiffAsync(string workingDirectory, CancellationToken cancellationToken)
    {
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
        foreach (var argument in new[] { "diff", "--no-index", "--no-ext-diff", "--no-color", "--no-textconv", "--unified=3", "--", "before.txt", "after.txt" })
        {
            startInfo.ArgumentList.Add(argument);
        }
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return string.Empty;
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return await output.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return string.Empty;
        }
    }

    private static string RewriteHeader(string generated, string? beforePath, string? afterPath, string before, string after)
    {
        var normalized = generated.ReplaceLineEndings("\n");
        var hunk = normalized.IndexOf("@@ ", StringComparison.Ordinal);
        var body = hunk >= 0 ? normalized[hunk..] : CreateWholeFileHunk(before, after);
        var oldLabel = beforePath is null ? "/dev/null" : "a/" + beforePath;
        var newLabel = afterPath is null ? "/dev/null" : "b/" + afterPath;
        return $"diff --git a/{beforePath ?? afterPath} b/{afterPath ?? beforePath}\n--- {oldLabel}\n+++ {newLabel}\n{body.TrimEnd()}\n";
    }

    private static string CreateWholeFileHunk(string before, string after)
    {
        var oldLines = SplitLines(before);
        var newLines = SplitLines(after);
        var builder = new StringBuilder($"@@ -1,{oldLines.Length} +1,{newLines.Length} @@\n");
        foreach (var line in oldLines) builder.Append('-').Append(line).Append('\n');
        foreach (var line in newLines) builder.Append('+').Append(line).Append('\n');
        return builder.ToString();
    }

    private static string[] SplitLines(string value) => string.IsNullOrEmpty(value)
        ? []
        : value.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n', StringSplitOptions.None);

    private async Task<List<MutationEntry>> ReadEntriesAsync(Guid runId, CancellationToken cancellationToken)
    {
        var path = EntriesPath(runId);
        if (!File.Exists(path)) return [];
        var result = new List<MutationEntry>();
        foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var entry = JsonSerializer.Deserialize<MutationEntry>(line, JsonOptions);
            if (entry is not null) result.Add(entry);
        }
        return result;
    }

    private string RunDirectory(Guid runId) => Path.Combine(_stateDirectory, runId.ToString("N"));
    private string MetadataPath(Guid runId) => Path.Combine(RunDirectory(runId), "metadata.json");
    private string EntriesPath(Guid runId) => Path.Combine(RunDirectory(runId), "changes.jsonl");

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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');

    public void Dispose()
    {
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed record RunMetadata(string WorkspacePath, DateTimeOffset CreatedAt);
    private sealed record MutationEntry(
        string ProposalId,
        string? BeforePath,
        string? AfterPath,
        string Diff,
        int AddedLines,
        int DeletedLines,
        DateTimeOffset CreatedAt);
}
