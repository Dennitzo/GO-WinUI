using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace GoWinUI.Core.Coding;

/// <summary>Persistent per-run Git trees, using an isolated repository/index outside the workspace.</summary>
public sealed class CodingGitChangeTracker : IDisposable
{
    private readonly string _workspace;
    private readonly string _storage;
    private readonly string _repository;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _baseline;
    private string? _lastTree;
    private CodingWorkspaceChangesSnapshot? _last;

    public CodingGitChangeTracker(string workspace, string storage)
    {
        _workspace = Path.GetFullPath(workspace);
        _storage = Path.GetFullPath(storage);
        _repository = Path.Combine(_storage, "git-comparison");
        foreach (var path in new[] { _workspace, _repository })
            for (string? ancestor = path; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
                if ((Directory.Exists(ancestor) || File.Exists(ancestor)) && File.GetAttributes(ancestor).HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("Verknüpfte Vergleichsverzeichnisse werden nicht verfolgt.");
        if (_storage.Equals(_workspace, StringComparison.OrdinalIgnoreCase)
            || _storage.StartsWith(Path.TrimEndingDirectorySeparator(_workspace) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Der Git-Vergleichsspeicher muss außerhalb des Workspaces liegen.");
    }

    public Task<CodingWorkspaceChangesSnapshot> InitializeAsync(CancellationToken cancellationToken = default) => StartAsync(false, cancellationToken);
    public Task<CodingWorkspaceChangesSnapshot> LoadExistingAsync(CancellationToken cancellationToken = default) => StartAsync(true, cancellationToken);

    private async Task<CodingWorkspaceChangesSnapshot> StartAsync(bool resume, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CodingWorkspaceGit.EnsureRepositoryAsync(_workspace, cancellationToken).ConfigureAwait(false);
            var manifest = Path.Combine(_storage, "git-baseline.json");
            if (File.Exists(manifest))
            {
                var saved = JsonSerializer.Deserialize<GitBaseline>(await File.ReadAllTextAsync(manifest, cancellationToken).ConfigureAwait(false))
                    ?? throw new IOException("Der Git-Ausgangsstand ist nicht lesbar.");
                if (!string.Equals(saved.Workspace, _workspace, StringComparison.OrdinalIgnoreCase)
                    || saved.Tree.Length is not (40 or 64) || !saved.Tree.All(char.IsAsciiHexDigit)) throw new IOException("Ungültiger Git-Ausgangsstand.");
                _baseline = saved.Tree;
                await GitAsync(["cat-file", "-e", _baseline + "^{tree}"], cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (resume || File.Exists(Path.Combine(_storage, "git-capture-started")))
                    throw new IOException("Für diesen älteren oder unterbrochenen Lauf fehlt der Git-Ausgangsstand. Ein neuer Lauf erfasst einen vollständigen Git-Vergleich.");
                Directory.CreateDirectory(_storage);
                await File.WriteAllTextAsync(Path.Combine(_storage, "git-capture-started"), _workspace, cancellationToken).ConfigureAwait(false);
                await GitAsync(["init", "--bare", "--quiet", _repository], cancellationToken, isolated: false).ConfigureAwait(false);
                _baseline = await CaptureTreeAsync(cancellationToken).ConfigureAwait(false);
                // A private ref keeps the original tree reachable, independently of user commits/GC.
                await GitAsync(["update-ref", "refs/go/baseline", _baseline], cancellationToken).ConfigureAwait(false);
                var temporary = manifest + ".tmp";
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new GitBaseline(_workspace, _baseline)), cancellationToken).ConfigureAwait(false);
                File.Move(temporary, manifest, overwrite: false);
                _lastTree = _baseline;
                return _last = new([], false, null, 0, DateTimeOffset.UtcNow);
            }
            return await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<CodingWorkspaceChangesSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await RefreshCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<string> CaptureTreeAsync(CancellationToken cancellationToken)
    {
        // Git applies .gitignore; unlike a normal git diff, this also captures new files.
        await GitAsync(["add", "--all", "--", "."], cancellationToken).ConfigureAwait(false);
        // Files already tracked by the user remain tracked even if subsequently ignored.
        var tracked = await GitAsync(["ls-files", "-z", "--cached", "--", "."], cancellationToken, isolated: false).ConfigureAwait(false);
        var existing = tracked.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(path => File.Exists(Path.Combine(_workspace, path))).ToArray();
        if (existing.Length > 0)
            await GitAsync(["add", "--force", "--pathspec-from-file=-", "--pathspec-file-nul"], cancellationToken,
                input: string.Join('\0', existing) + "\0").ConfigureAwait(false);
        return (await GitAsync(["write-tree"], cancellationToken).ConfigureAwait(false)).Trim();
    }

    private async Task<CodingWorkspaceChangesSnapshot> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        if (_baseline is null) throw new IOException("Der Git-Ausgangsstand fehlt.");
        var tree = await CaptureTreeAsync(cancellationToken).ConfigureAwait(false);
        if (_last is not null && tree == _lastTree) return _last;
        var stats = await GitAsync(["diff", "--no-renames", "--numstat", "-z", _baseline, tree, "--"], cancellationToken).ConfigureAwait(false);
        var states = await GitAsync(["diff", "--no-renames", "--name-status", "-z", _baseline, tree, "--"], cancellationToken).ConfigureAwait(false);
        var kinds = new Dictionary<string, string>(StringComparer.Ordinal);
        var tokens = states.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index + 1 < tokens.Length; index += 2)
            kinds[tokens[index + 1]] = tokens[index] switch { "A" => "added", "D" => "deleted", _ => "modified" };
        var files = new List<CodingWorkspaceFileChange>();
        foreach (var stat in stats.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var columns = stat.Split('\t', 3);
            if (columns.Length != 3) throw new IOException("Git hat eine ungültige Änderungsstatistik geliefert.");
            var binary = columns[0] == "-";
            var added = binary ? 0 : int.Parse(columns[0], System.Globalization.CultureInfo.InvariantCulture);
            var removed = binary ? 0 : int.Parse(columns[1], System.Globalization.CultureInfo.InvariantCulture);
            var path = columns[2];
            var diff = await GitAsync(["diff", "--no-renames", "--no-ext-diff", "--no-textconv", "--no-color", "--unified=3", _baseline, tree, "--", path], cancellationToken).ConfigureAwait(false);
            files.Add(new(path, added, removed, diff, false, kinds.GetValueOrDefault(path, "modified"), binary));
        }
        _lastTree = tree;
        return _last = new(files, false, null, files.Count, DateTimeOffset.UtcNow);
    }

    private async Task<string> GitAsync(string[] arguments, CancellationToken cancellationToken, bool isolated = true, string? input = null)
    {
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe");
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() && File.Exists(installed) ? installed : "git")
        {
            WorkingDirectory = _workspace, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input is not null,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var name in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR", "GIT_OBJECT_DIRECTORY" }) start.Environment.Remove(name);
        start.ArgumentList.Add("--literal-pathspecs");
        if (isolated)
        {
            start.ArgumentList.Add("--git-dir=" + _repository);
            start.ArgumentList.Add("--work-tree=" + _workspace);
        }
        foreach (var setting in new[] { "core.autocrlf=false", "core.quotePath=false", "core.fsmonitor=false", "core.attributesFile=", "gc.auto=0" })
        { start.ArgumentList.Add("-c"); start.ArgumentList.Add(setting); }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Git konnte nicht gestartet werden.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            if (input is not null) { await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken).ConfigureAwait(false); process.StandardInput.Close(); }
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            var result = await stdout.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("Git-Dateivergleich fehlgeschlagen: " + error.Trim());
            return result;
        }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
    }

    public void Dispose() => _gate.Dispose();
    private sealed record GitBaseline(string Workspace, string Tree);
}
