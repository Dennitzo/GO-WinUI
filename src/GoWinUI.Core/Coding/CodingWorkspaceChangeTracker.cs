using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GoWinUI.Core.Coding;

public sealed record CodingWorkspaceFileChange(string Path, int AddedLines, int RemovedLines,
    string Diff, bool DiffTruncated, string Kind, bool IsBinary = false);

public sealed record CodingWorkspaceChangesSnapshot(IReadOnlyList<CodingWorkspaceFileChange> Files,
    bool IsPartial, string? Notice, int ScannedFiles, DateTimeOffset UpdatedAt);

/// <summary>Compares observed workspace bytes with an immutable, persisted pre-run baseline.</summary>
public sealed class CodingWorkspaceChangeTracker : IDisposable
{
    public const int MaximumFiles = 5_000;
    public const int MaximumFileBytes = 2 * 1024 * 1024;
    public const int MaximumDiffCharacters = 240_000;
    public const int MaximumPathCharacters = 500_000;
    public const int MaximumRelativePathCharacters = 1_024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".venv", "venv", "node_modules", "bin", "obj", "__pycache__", ".next", "dist",
        ".pytest_cache", ".mypy_cache", ".ruff_cache", ".tox", ".nox", ".cache", "htmlcov", "unsloth-tmp",
    };
    private readonly string _root;
    private readonly string _storage;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, CachedFile> _cache = new(Paths);
    private readonly Dictionary<string, CachedDiff> _diffs = new(Paths);
    private Baseline? _baseline;
    private string? _unavailable;
    private CodingWorkspaceChangesSnapshot? _last;
    private bool _disposed;

    public CodingWorkspaceChangeTracker(string workspaceRoot, string storageDirectory, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        _storage = Path.TrimEndingDirectorySeparator(Path.GetFullPath(storageDirectory));
        _clock = timeProvider ?? TimeProvider.System;
        if (Paths.Equals(_root, _storage)) throw new ArgumentException("Der Vergleichsspeicher muss getrennt vom Projektstamm liegen.", nameof(storageDirectory));
        RejectLinks(_root);
        RejectLinks(_storage);
    }

    public Task<CodingWorkspaceChangesSnapshot> InitializeAsync(CancellationToken cancellationToken = default) => InitializeCoreAsync(true, cancellationToken);

    public Task<CodingWorkspaceChangesSnapshot> LoadExistingAsync(CancellationToken cancellationToken = default) => InitializeCoreAsync(false, cancellationToken);

    private async Task<CodingWorkspaceChangesSnapshot> InitializeCoreAsync(bool captureIfMissing, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_baseline is not null || _unavailable is not null) return await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
            var manifest = Path.Combine(_storage, "baseline.json");
            RejectLinks(_storage);
            if (File.Exists(manifest))
            {
                try
                {
                    RejectLinks(manifest);
                    if (new FileInfo(manifest).Length > 8 * 1024 * 1024) throw new InvalidDataException("Der gespeicherte Startzustand ist zu groß.");
                    _baseline = JsonSerializer.Deserialize<Baseline>(await File.ReadAllTextAsync(manifest, cancellationToken).ConfigureAwait(false), Json)
                        ?? throw new InvalidDataException("Der gespeicherte Startzustand ist leer.");
                    ValidateBaseline(_baseline);
                }
                catch (Exception exception) when (IsFileError(exception) || exception is JsonException or ArgumentException)
                {
                    _baseline = null;
                    _unavailable = "Der ursprüngliche Dateistand ist nicht lesbar oder gehört zu einem anderen Projekt. Es wurde kein neuer Startzustand angenommen.";
                }
            }
            else if (!captureIfMissing || File.Exists(Path.Combine(_storage, "capture-started")))
                _unavailable = "Der Dateistand vom Beginn dieses AI-Laufs fehlt oder wurde nicht vollständig gespeichert. Nettoänderungen sind nicht verlässlich bestimmbar.";
            else
            {
                if (!Directory.Exists(_root)) throw new DirectoryNotFoundException("Der Coding-Projektordner existiert nicht.");
                Directory.CreateDirectory(_storage);
                RejectLinks(_storage);
                // If capture is interrupted, resume must never silently use later workspace bytes as its baseline.
                await using (var marker = new FileStream(Path.Combine(_storage, "capture-started"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await marker.FlushAsync(cancellationToken).ConfigureAwait(false);
                var scan = await ScanAsync(cancellationToken).ConfigureAwait(false);
                var entries = new List<BaselineFile>();
                foreach (var pair in scan.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var file = pair.Value;
                    entries.Add(new(pair.Key, file.Hash, file.IsBinary, checked((int)file.Stamp.Length)));
                }
                var baseline = new Baseline(1, _root, entries, scan.Skipped.ToArray(), scan.EnumerationComplete, scan.Notices.ToArray(), scan.ScannedFiles);
                var temporary = Path.Combine(_storage, "baseline-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(baseline, Json), cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Move(temporary, manifest, overwrite: false);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                _baseline = baseline;
                return Publish([], scan.IsPartial, JoinNotices(scan.Notices), scan.ScannedFiles);
            }
            return await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<CodingWorkspaceChangesSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_baseline is null && _unavailable is null) throw new InvalidOperationException("Der Startzustand muss vor dem Vergleich initialisiert werden.");
            return await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<CodingWorkspaceChangesSnapshot> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        if (_baseline is null) return Publish([], true, _unavailable, 0);
        var scan = await ScanAsync(cancellationToken).ConfigureAwait(false);
        var notices = new HashSet<string>(_baseline.Notices.Concat(scan.Notices), StringComparer.Ordinal);
        var baselineFiles = _baseline.Files.ToDictionary(static file => file.Path, Paths);
        var skippedBaseline = _baseline.Skipped.ToHashSet(Paths);
        var changes = new List<CodingWorkspaceFileChange>();
        var remainingDiff = MaximumDiffCharacters;
        var changedPathCharacters = 0;
        var partial = !_baseline.EnumerationComplete || _baseline.Skipped.Count > 0 || scan.IsPartial;
        foreach (var path in baselineFiles.Keys.Union(scan.Files.Keys, Paths).Order(Paths))
        {
            cancellationToken.ThrowIfCancellationRequested();
            baselineFiles.TryGetValue(path, out var original);
            scan.Files.TryGetValue(path, out var current);
            if (original is null && (!_baseline.EnumerationComplete || skippedBaseline.Contains(path))) continue;
            if (current is null && !IsConfirmedMissing(path)) { partial = true; continue; }
            if (original?.Hash == current?.Hash) { _diffs.Remove(path); continue; }
            if (changes.Count == MaximumFiles || changedPathCharacters + path.Length > MaximumPathCharacters)
            {
                partial = true;
                notices.Add("Die Änderungsliste erreicht die Grenze von 5.000 Dateien oder 500.000 Pfadzeichen; weitere Änderungen werden nicht vollständig angezeigt.");
                break;
            }
            var kind = original is null ? "added" : current is null ? "deleted" : "modified";
            var binary = original?.IsBinary == true || current?.IsBinary == true;
            CodingWorkspaceFileChange change;
            if (binary)
            {
                change = new(path, 0, 0, string.Empty, true, kind, true);
                notices.Add("Binärdateiänderungen werden gezählt; für sie sind keine Zeilenzahlen oder Textdiffs verfügbar.");
                partial = true;
            }
            else
            {
                try
                {
                    if (_diffs.TryGetValue(path, out var cached) && cached.Hash == current?.Hash && cached.Kind == kind && cached.Allowance == remainingDiff)
                        change = cached.Change;
                    else
                    {
                        var before = original is null ? null : await ReadBlobAsync(original, cancellationToken).ConfigureAwait(false);
                        var after = current is null ? null : await ReadBlobAsync(
                            new BaselineFile(path, current.Hash, current.IsBinary, checked((int)current.Stamp.Length)), cancellationToken).ConfigureAwait(false);
                        var diff = CodingUnifiedDiff.Create(path, before, after, cancellationToken);
                        var text = diff.Text;
                        var truncated = diff.Truncated;
                        if (text.Length > remainingDiff) { text = string.Empty; truncated = true; }
                        change = new(path, diff.AddedLines, diff.RemovedLines, text, truncated, kind);
                        _diffs[path] = new(current?.Hash, kind, remainingDiff, change);
                    }
                    remainingDiff -= change.Diff.Length;
                    if (change.DiffTruncated) { partial = true; notices.Add("Die Diffvorschau wurde am Größenlimit gekürzt; Datei- und Zeilenzahlen bleiben erhalten."); }
                }
                catch (Exception exception) when (IsFileError(exception))
                {
                    partial = true;
                    notices.Add("Ein gespeicherter Datei-Startzustand ist nicht mehr lesbar; betroffene Änderungen können nicht zuverlässig verglichen werden.");
                    continue;
                }
            }
            changes.Add(change);
            changedPathCharacters += path.Length;
        }
        foreach (var path in _diffs.Keys.Except(changes.Select(static change => change.Path), Paths).ToArray()) _diffs.Remove(path);
        return Publish(changes.ToArray(), partial, JoinNotices(notices), scan.ScannedFiles);
    }

    private async Task<Scan> ScanAsync(CancellationToken cancellationToken)
    {
        var scan = new Scan();
        var pending = new Stack<string>();
        pending.Push(_root);
        var candidates = new List<(string FullPath, string Relative)>();
        var pathCharacters = 0;
        var directories = 0;
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++directories > 10_000) { scan.Incomplete("Der Vergleich erreicht die Grenze von 10.000 Verzeichnissen."); break; }
            string[] entries;
            try
            {
                RejectLinks(directory);
                entries = Directory.EnumerateFileSystemEntries(directory).Take(MaximumFiles + 1).ToArray();
                if (entries.Length > MaximumFiles) scan.Incomplete("Ein Verzeichnis enthält mehr als 5.000 Einträge; die Erfassung ist begrenzt.");
                Array.Sort(entries, Paths);
            }
            catch (Exception exception) when (IsFileError(exception)) { scan.Incomplete("Ein Projektverzeichnis ist nicht lesbar oder wurde während der Erfassung geändert."); continue; }
            var children = new List<string>();
            foreach (var fullPath in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsUnder(fullPath, _storage)) continue;
                var relative = Path.GetRelativePath(_root, fullPath).Replace('\\', '/');
                if (relative.Length > MaximumRelativePathCharacters)
                { scan.Incomplete("Dateipfade über 1.024 Zeichen werden nicht erfasst."); continue; }
                try
                {
                    var attributes = File.GetAttributes(fullPath);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint)) { scan.Incomplete("Symlinks und Verzeichnisverknüpfungen werden nicht verfolgt."); continue; }
                    if (attributes.HasFlag(FileAttributes.Directory))
                    {
                        if (!IsIgnored(Path.GetFileName(fullPath))) children.Add(fullPath);
                        else scan.Notices.Add("Build-, Abhängigkeits- und Cacheverzeichnisse sowie .git werden ausgelassen.");
                        continue;
                    }
                    if (++scan.ScannedFiles > MaximumFiles) { scan.ScannedFiles = MaximumFiles; scan.Incomplete("Der Vergleich ist auf 5.000 Dateien begrenzt."); goto ReadCandidates; }
                    if (pathCharacters + relative.Length > MaximumPathCharacters)
                    { scan.Incomplete("Die Dateierfassung erreicht die Grenze von 500.000 gesamten Pfadzeichen."); goto ReadCandidates; }
                    pathCharacters += relative.Length;
                    candidates.Add((fullPath, relative));
                }
                catch (Exception exception) when (IsFileError(exception)) { scan.Incomplete("Einzelne Projektdateien sind nicht lesbar oder wurden während der Erfassung geändert."); }
            }
            foreach (var child in children.OrderByDescending(DirectoryPriority).ThenByDescending(static path => path, Paths)) pending.Push(child);
        }
        ReadCandidates:
        // Reserve the bounded content budget for code before datasets and generated artifacts.
        // Metadata enumeration does not retain file contents and remains independently bounded.
        var originalPaths = _baseline?.Files.Select(static file => file.Path).ToHashSet(Paths);
        foreach (var (fullPath, relative) in candidates.OrderBy(item => originalPaths?.Contains(item.Relative) == true ? 0 : 1)
            .ThenBy(item => FilePriority(item.FullPath)).ThenBy(item => item.Relative, Paths))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                    var info = new FileInfo(fullPath);
                    if (info.Length > MaximumFileBytes) { scan.Skip(relative, "Dateien über 2 MiB werden nicht in den Textvergleich aufgenommen."); continue; }
                    var stamp = new Stamp(info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks);
                    if (_cache.TryGetValue(relative, out var cached) && cached.Stamp == stamp && _clock.GetUtcNow() - cached.CheckedAt < TimeSpan.FromMinutes(1))
                    { scan.Files[relative] = cached; continue; }
                    var content = await ReadBoundedAsync(fullPath, cancellationToken).ConfigureAwait(false);
                    info.Refresh();
                    if (stamp != new Stamp(info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks))
                    { scan.Skip(relative, "Eine Datei wurde während der Erfassung geändert; sie wird beim nächsten Vergleich erneut geprüft."); continue; }
                    var file = new CachedFile(stamp, Convert.ToHexString(SHA256.HashData(content)), IsBinary(content), _clock.GetUtcNow());
                    // Keep file contents on disk, so removing the total snapshot quota
                    // does not retain an entire large workspace in application memory.
                    if (!file.IsBinary) await WriteBlobAsync(file.Hash, content, cancellationToken).ConfigureAwait(false);
                    _cache[relative] = file;
                    scan.Files[relative] = file;
                }
                catch (Exception exception) when (IsFileError(exception)) { scan.Incomplete("Einzelne Projektdateien sind nicht lesbar oder wurden während der Erfassung geändert."); }
        }
        return FinishScan(scan);
    }

    private Scan FinishScan(Scan scan)
    {
        foreach (var path in _cache.Keys.Except(scan.Files.Keys, Paths).ToArray()) _cache.Remove(path);
        return scan;
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken cancellationToken)
    {
        RejectLinks(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192, useAsync: true);
        if (stream.Length > MaximumFileBytes) throw new InvalidDataException("Die Datei überschreitet das Größenlimit.");
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
            throw new InvalidDataException("Die Datei wurde während der Erfassung vergrößert.");
        return bytes;
    }

    private async Task WriteBlobAsync(string hash, byte[] content, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(_storage, "blobs");
        Directory.CreateDirectory(directory);
        RejectLinks(directory);
        var target = Path.Combine(directory, hash);
        if (File.Exists(target)) { RejectLinks(target); return; }
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, content, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<byte[]> ReadBlobAsync(BaselineFile file, CancellationToken cancellationToken)
    {
        var bytes = await ReadBoundedAsync(Path.Combine(_storage, "blobs", file.Hash), cancellationToken).ConfigureAwait(false);
        if (bytes.Length != file.Length || Convert.ToHexString(SHA256.HashData(bytes)) != file.Hash)
            throw new InvalidDataException("Der ursprüngliche Dateiinhalt stimmt nicht mehr mit seinem Hash überein.");
        return bytes;
    }

    private void ValidateBaseline(Baseline baseline)
    {
        if (baseline.Version != 1 || !Paths.Equals(baseline.WorkspaceRoot, _root) || baseline.Files is null || baseline.Skipped is null || baseline.Notices is null
            || baseline.Files.Count > MaximumFiles || baseline.Files.Any(static file => file is null)
            || baseline.Files.Sum(static file => (long)(file.Path?.Length ?? 0)) + baseline.Skipped.Sum(static path => (long)(path?.Length ?? 0)) > MaximumPathCharacters)
            throw new InvalidDataException("Der gespeicherte Startzustand passt nicht zum Projekt.");
        var paths = new HashSet<string>(Paths);
        foreach (var file in baseline.Files)
        {
            if (!paths.Add(file.Path) || !ValidRelative(file.Path) || file.Length is < 0 or > MaximumFileBytes
                || file.Hash is null || file.Hash.Length != 64 || file.Hash.Any(static value => !char.IsAsciiHexDigit(value)))
                throw new InvalidDataException("Der Startzustand enthält einen ungültigen Dateieintrag.");
            if (!file.IsBinary)
            {
                var blob = Path.Combine(_storage, "blobs", file.Hash);
                RejectLinks(blob);
                if (!File.Exists(blob) || new FileInfo(blob).Length != file.Length) throw new InvalidDataException("Ein ursprünglicher Dateiinhalt fehlt.");
            }
        }
        if (baseline.Skipped.Any(static path => !ValidRelative(path))) throw new InvalidDataException("Ungültiger ausgelassener Dateipfad.");
    }

    private bool IsConfirmedMissing(string relative)
    {
        try
        {
            var path = Path.Combine(_root, relative);
            RejectLinks(path);
            // A real directory at the same path no longer represents the captured file.
            return File.GetAttributes(path).HasFlag(FileAttributes.Directory);
        }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return Directory.Exists(_root); }
        catch (Exception exception) when (IsFileError(exception)) { return false; }
    }

    private CodingWorkspaceChangesSnapshot Publish(IReadOnlyList<CodingWorkspaceFileChange> files, bool partial, string? notice, int scanned)
    {
        if (_last is not null && _last.IsPartial == partial && _last.Notice == notice && _last.ScannedFiles == scanned && _last.Files.SequenceEqual(files)) return _last;
        return _last = new(files, partial, notice, scanned, _clock.GetUtcNow());
    }

    private static string? JoinNotices(IEnumerable<string> notices)
    {
        var text = string.Join(" ", notices.Order(StringComparer.Ordinal));
        return text.Length == 0 ? null : text;
    }

    private static bool IsBinary(byte[] bytes)
    {
        if (bytes.Contains((byte)0)) return true;
        try { _ = Utf8.GetCharCount(bytes); return false; }
        catch (DecoderFallbackException) { return true; }
    }

    private static bool ValidRelative(string path) => !string.IsNullOrWhiteSpace(path) && path.Length <= MaximumRelativePathCharacters && !Path.IsPathRooted(path)
        && !path.Contains(':') && !path.Any(char.IsControl) && !path.Split('/', '\\').Any(static part => part is ".." or "." or "");
    private static bool IsUnder(string path, string parent) => Paths.Equals(path, parent) || path.StartsWith(parent + Path.DirectorySeparatorChar, PathComparison);
    private static bool IsIgnored(string name) => Ignored.Contains(name) || name.EndsWith(".egg-info", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("pytest-of-", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("pytest-", StringComparison.OrdinalIgnoreCase) && name.Length > 7 && name.AsSpan(7).IndexOfAnyExceptInRange('0', '9') < 0;
    private static int DirectoryPriority(string path) => Path.GetFileName(path).ToLowerInvariant() switch
    { "src" or "source" or "app" or "lib" => 0, "tests" or "test" or "docs" or "scripts" or "tools" => 1,
      "artifacts" or "dataset" or "datasets" or "memory" or "logs" or "data" => 3,
      _ => File.Exists(Path.Combine(path, "__init__.py")) ? 0 : 2 };
    private static int FilePriority(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".py" or ".cs" or ".fs" or ".vb" or ".js" or ".jsx" or ".ts" or ".tsx" or ".go" or ".rs"
        or ".c" or ".h" or ".cpp" or ".hpp" or ".java" or ".kt" or ".swift" or ".rb" or ".php"
        or ".html" or ".css" or ".scss" or ".vue" or ".svelte" or ".xaml" or ".razor"
        or ".ps1" or ".sh" or ".cmd" or ".bat" or ".sql" => 0,
        ".toml" or ".yaml" or ".yml" or ".xml" or ".csproj" or ".props" or ".targets" or ".sln" or ".md" => 1,
        ".jsonl" or ".csv" or ".gz" or ".zip" or ".sqlite3" or ".db" or ".gguf" or ".safetensors" => 3,
        _ => 2,
    };
    private static bool IsFileError(Exception exception) => exception is IOException or InvalidDataException or UnauthorizedAccessException or System.Security.SecurityException;
    private static void RejectLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new UnauthorizedAccessException("Symlinks werden für den Dateivergleich nicht verfolgt.");
    }

    public void Dispose() { if (_disposed) return; _disposed = true; _gate.Dispose(); }

    private sealed record Baseline(int Version, string WorkspaceRoot, IReadOnlyList<BaselineFile> Files,
        IReadOnlyList<string> Skipped, bool EnumerationComplete, IReadOnlyList<string> Notices, int ScannedFiles);
    private sealed record BaselineFile(string Path, string Hash, bool IsBinary, int Length);
    private readonly record struct Stamp(long Length, long ModifiedTicks, long CreatedTicks);
    private sealed record CachedFile(Stamp Stamp, string Hash, bool IsBinary, DateTimeOffset CheckedAt);
    private sealed record CachedDiff(string? Hash, string Kind, int Allowance, CodingWorkspaceFileChange Change);
    private sealed class Scan
    {
        internal Dictionary<string, CachedFile> Files { get; } = new(Paths);
        internal HashSet<string> Skipped { get; } = new(Paths);
        internal HashSet<string> Notices { get; } = new(StringComparer.Ordinal);
        internal int ScannedFiles { get; set; }
        internal bool EnumerationComplete { get; set; } = true;
        internal bool IsPartial => !EnumerationComplete || Skipped.Count > 0;
        internal void Incomplete(string notice) { EnumerationComplete = false; Notices.Add(notice); }
        internal void Skip(string path, string notice) { Skipped.Add(path); Notices.Add(notice); }
    }
}
