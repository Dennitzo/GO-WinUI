using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GoWinUI.Core.Coding;

/// <summary>
/// Runs delegated commands in an independent source copy and publishes only owned files.
/// This prevents ordinary build and file collisions; native processes are not an OS sandbox.
/// </summary>
public sealed class CodingDelegatedWorkspace
{
    private static readonly HashSet<string> Generated = new(StringComparer.OrdinalIgnoreCase)
    { ".git", ".vs", ".venv", "venv", "node_modules", "bin", "obj", "__pycache__", ".next", "dist", ".cache", "artifacts", "sandbox" };
    private readonly string _source;
    private readonly string[] _scopes;
    private readonly string _cacheRoot;
    private const string OwnerMarker = "go.delegated-workspace.v1";
    internal const int RetainedCopyLimit = 16;
    internal const long RetainedByteLimit = 8L * 1024 * 1024 * 1024;
    internal static readonly TimeSpan RetainedAge = TimeSpan.FromDays(7);
    public string WorkspacePath { get; }

    public CodingDelegatedWorkspace(string source, string runId, string agentId, IEnumerable<string> writePaths, string? cacheRoot = null)
    {
        _source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        _scopes = writePaths.Select(NormalizeScope).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (_scopes.Length > 32) throw new ArgumentException("Höchstens 32 Schreibbereiche zuweisen.", nameof(writePaths));
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_source.ToUpperInvariant() + "\n" + runId + "\n" + agentId)));
        _cacheRoot = Path.GetFullPath(cacheRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GO", "AgentWorkspaces"));
        WorkspacePath = Path.GetFullPath(Path.Combine(_cacheRoot, identity, "workspace"));
        var sourcePrefix = Path.EndsInDirectorySeparator(_source) ? _source : _source + Path.DirectorySeparatorChar;
        if (WorkspacePath.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase) || WorkspacePath.Equals(_source, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Die Subagent-Kopie muss außerhalb des Originalprojekts liegen.");
    }

    public static string NormalizeScope(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.StartsWith('/') || path.StartsWith('\\') || path.Any(char.IsControl))
            throw new ArgumentException("Nur relative Subagent-Dateibereiche sind zulässig.", nameof(path));
        var directory = path.EndsWith('/') || path.EndsWith('\\');
        var parts = path.Replace('\\', '/').Split('/').Where(p => p is not ("" or ".")).ToArray();
        if (parts.Length == 0 || parts.Any(p => p == ".." || p.Equals(".git", StringComparison.OrdinalIgnoreCase)
                || p.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0 || p.EndsWith('.') || p.EndsWith(' ') || IsDeviceName(p)))
            throw new ArgumentException("Ungültiger Subagent-Dateibereich.", nameof(path));
        return string.Join('/', parts).ToLowerInvariant() + (directory ? "/" : "");
    }

    public static bool IsOwned(string path, IEnumerable<string> scopes)
    {
        var normalized = NormalizeScope(path).TrimEnd('/');
        return scopes.Select(NormalizeScope).Any(scope => normalized.Equals(scope, StringComparison.OrdinalIgnoreCase)
            || scope.EndsWith('/') && normalized.StartsWith(scope, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<JsonElement> ExecuteCommandAsync(JsonElement arguments, Func<CodingCommandProgress, Task>? progress = null,
        CodingRunEvidenceStore.CodingEvidenceCapture? evidence = null, CancellationToken cancellationToken = default)
    {
        // Reject explicit references to the original project. This is a mistake guard,
        // not a security boundary against arbitrary native code.
        foreach (var value in arguments.EnumerateObject())
        {
            IEnumerable<string?> strings = value.Value.ValueKind == JsonValueKind.Array ? value.Value.EnumerateArray().Select(v => v.GetString()) :
                value.Value.ValueKind == JsonValueKind.String ? new string?[] { value.Value.GetString() } : Array.Empty<string?>();
            if (strings.Any(text => text?.Replace('\\', '/').Contains(_source.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) == true))
                throw new UnauthorizedAccessException("Subagent-Befehle müssen relative Pfade in ihrer isolierten Projektkopie verwenden.");
        }
        RejectLinks(_source);
        RejectLinks(WorkspacePath);
        await using var workspaceLease = await AcquireWorkspaceAsync(cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(WorkspacePath);
        await ReplaceFileAsync(Path.Combine(Path.GetDirectoryName(WorkspacePath)!, "owner.json"),
            JsonSerializer.SerializeToUtf8Bytes(new { schema = OwnerMarker }), cancellationToken).ConfigureAwait(false);
        CleanupInactiveCopies(_cacheRoot);
        try
        {
        // A Git worktree's .git file points outside its checkout. Never reuse a
        // copied or process-created pointer as part of a later delegated command.
        if (File.Exists(Path.Combine(WorkspacePath, ".git")))
            throw new UnauthorizedAccessException("Die Subagent-Kopie enthält einen unzulässigen externen Git-Verweis.");
        var sourceFiles = Files(_source, cancellationToken).ToDictionary(path => Relative(_source, path), StringComparer.OrdinalIgnoreCase);
        if (sourceFiles.Values.Sum(path => new FileInfo(path).Length) > 512L * 1024 * 1024)
            throw new InvalidOperationException("Die Subagent-Quellkopie überschreitet 512 MiB. Begrenze den Workspace oder delegiere Dateiwerkzeuge.");
        var baseline = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relative, source) in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false);
            baseline[relative] = Hash(bytes);
            var target = Resolve(WorkspacePath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await ReplaceFileAsync(target, bytes, cancellationToken).ConfigureAwait(false);
        }
        // Remove obsolete source files from earlier commands; preserve dependencies/build outputs.
        foreach (var copy in Files(WorkspacePath, cancellationToken))
            if (!sourceFiles.ContainsKey(Relative(WorkspacePath, copy))) File.Delete(copy);

        var result = await new LocalCodingToolExecutor(WorkspacePath, progress, evidence)
            .ExecuteAsync("coding.command", arguments, cancellationToken).ConfigureAwait(false);
        if (result.TryGetProperty("success", out var success) && !success.GetBoolean())
            return WithTransferReceipt(result, [], [], "Der Befehl ist fehlgeschlagen; seine Änderungen bleiben ausschließlich in der Arbeitskopie.");
        var produced = Files(WorkspacePath, cancellationToken, rejectLinks: true).ToDictionary(path => Relative(WorkspacePath, path), StringComparer.OrdinalIgnoreCase);
        if (produced.Values.Sum(path => new FileInfo(path).Length) > 512L * 1024 * 1024)
            throw new InvalidOperationException("Die Subagent-Quelldateien überschreiten nach dem Befehl 512 MiB. Es werden keine Änderungen übernommen; die Kopie bleibt zur Prüfung erhalten.");
        var changed = new List<(string Relative, byte[]? Bytes)>();
        var withheld = new List<string>();
        foreach (var relative in baseline.Keys.Union(produced.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var bytes = produced.TryGetValue(relative, out var output) ? await File.ReadAllBytesAsync(output, cancellationToken).ConfigureAwait(false) : null;
            if ((bytes is null ? null : Hash(bytes)) == baseline.GetValueOrDefault(relative)) continue;
            if (!IsOwned(relative, _scopes)) { withheld.Add(relative); continue; }
            changed.Add((relative, bytes));
        }
        // Check every original before publishing any file; never overwrite newer parent edits.
        foreach (var (relative, _) in changed)
        {
            var target = Resolve(_source, relative);
            var current = File.Exists(target) ? Hash(await File.ReadAllBytesAsync(target, cancellationToken).ConfigureAwait(false)) : null;
            if (current != baseline.GetValueOrDefault(relative))
                throw new IOException("Subagent-Konflikt: Die Originaldatei wurde inzwischen geändert: " + relative + ". Die Kopie bleibt erhalten: " + WorkspacePath);
        }
        var publication = await PublishAsync(changed, baseline, cancellationToken).ConfigureAwait(false);
        return WithTransferReceipt(result, publication.PublishedPaths, withheld, publication.Error,
            publication.RolledBackPaths, publication.ConflictPaths, publication.RecoveryFiles);
        }
        finally
        {
            // This copy remains leased until the returned receipt has been built.
            // Other inactive copies are bounded; a live command is never evicted.
            CleanupInactiveCopies(_cacheRoot);
        }
    }

    private JsonElement WithTransferReceipt(JsonElement result, IEnumerable<string> published, IEnumerable<string> withheld, string? message,
        IReadOnlyList<string>? rolledBack = null, IReadOnlyList<string>? conflicts = null, IReadOnlyDictionary<string, string>? recoveryFiles = null)
    {
        var receipt = JsonNode.Parse(result.GetRawText())!.AsObject();
        if (rolledBack is not null && message is not null) receipt["success"] = false;
        receipt["delegation"] = JsonSerializer.SerializeToNode(new { isolatedWorkspace = WorkspacePath,
            publishedPaths = published, withheldPaths = withheld, message,
            rolledBackPaths = rolledBack ?? [], rollbackConflictPaths = conflicts ?? [],
            recoveryFiles,
            publicationState = rolledBack is null ? "not_published" : message is null ? "completed" : conflicts?.Count > 0 ? "partial" : "rolled_back",
            isolation = "source-copy-with-scoped-publish", operatingSystemSandbox = false });
        return JsonSerializer.SerializeToElement(receipt);
    }

    private async Task<FileStream> AcquireWorkspaceAsync(CancellationToken token)
    {
        var lockPath = LeasePath(_cacheRoot, Path.GetDirectoryName(WorkspacePath)!);
        RejectLinks(lockPath);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            RejectLinks(lockPath);
            try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33)
            {
                // A second broker instance or app process may replay the same child.
                // Never refresh its copy while the first command is still using it.
                await Task.Delay(100, token).ConfigureAwait(false);
            }
        }
    }

    internal sealed record PublicationReceipt(IReadOnlyList<string> PublishedPaths, IReadOnlyList<string> RolledBackPaths,
        IReadOnlyList<string> ConflictPaths, string? Error = null, IReadOnlyDictionary<string, string>? RecoveryFiles = null);
    private sealed record StagedFile(string Relative, string Target, string? Replacement, string? Backup, string? OriginalHash, string? NewHash);

    internal async Task<PublicationReceipt> PublishAsync(IReadOnlyList<(string Relative, byte[]? Bytes)> changes,
        IReadOnlyDictionary<string, string> baseline, CancellationToken token, Action<string>? published = null)
    {
        var staged = new List<StagedFile>();
        var temporaryFiles = new List<string>();
        var createdDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var (relative, bytes) in changes.OrderBy(change => change.Relative, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                var target = Resolve(_source, relative);
                var original = File.Exists(target) ? await File.ReadAllBytesAsync(target, token).ConfigureAwait(false) : null;
                var originalHash = original is null ? null : Hash(original);
                if (originalHash != baseline.GetValueOrDefault(relative)) throw new IOException("Subagent-Konflikt vor der Veröffentlichung: " + relative);
                var directory = Path.GetDirectoryName(target)!;
                for (var missing = directory; !Directory.Exists(missing); missing = Path.GetDirectoryName(missing)!) createdDirectories.Add(missing);
                Directory.CreateDirectory(directory);
                async Task<string?> StageAsync(byte[]? content, string suffix)
                {
                    if (content is null) return null;
                    // Adjacent staging guarantees a same-volume atomic rename.
                    var path = Path.Combine(directory, ".go-agent-" + Guid.NewGuid().ToString("N") + suffix);
                    temporaryFiles.Add(path);
                    await File.WriteAllBytesAsync(path, content, token).ConfigureAwait(false);
                    return path;
                }
                staged.Add(new(relative, target, await StageAsync(bytes, ".publish").ConfigureAwait(false),
                    await StageAsync(original, ".rollback").ConfigureAwait(false), originalHash, bytes is null ? null : Hash(bytes)));
            }
            token.ThrowIfCancellationRequested();
            var committed = new List<StagedFile>();
            try
            {
                foreach (var file in staged)
                {
                    // Cancellation after the first rename cannot strand an
                    // unreported partial commit. Finish, or roll back on IO.
                    if (committed.Count == 0) token.ThrowIfCancellationRequested();
                    if (await CurrentHashAsync(file.Target).ConfigureAwait(false) != file.OriginalHash)
                        throw new IOException("Subagent-Konflikt bei der Veröffentlichung: " + file.Relative);
                    RejectLinks(file.Target);
                    if (file.Replacement is null) File.Delete(file.Target);
                    else File.Move(file.Replacement, file.Target, overwrite: true);
                    committed.Add(file);
                    published?.Invoke(file.Relative);
                }
                return new(committed.Select(file => file.Relative).ToArray(), [], []);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException || error is OperationCanceledException && committed.Count > 0)
            {
                var rolledBack = new List<string>();
                var conflicts = new List<string>();
                var recovery = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in committed.AsEnumerable().Reverse())
                {
                    try
                    {
                        // A parent/editor may have changed the published file
                        // already. Such a later change is never overwritten.
                        if (await CurrentHashAsync(file.Target).ConfigureAwait(false) != file.NewHash)
                            throw new IOException("Datei wurde nach der Veröffentlichung erneut geändert.");
                        RejectLinks(file.Target);
                        if (file.Backup is null) File.Delete(file.Target);
                        else File.Move(file.Backup, file.Target, overwrite: true);
                        rolledBack.Add(file.Relative);
                    }
                    catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException)
                    {
                        conflicts.Add(file.Relative);
                        if (file.Backup is not null)
                        {
                            temporaryFiles.Remove(file.Backup);
                            recovery[file.Relative] = file.Backup;
                        }
                    }
                }
                return new(committed.Select(file => file.Relative).Except(rolledBack, StringComparer.OrdinalIgnoreCase).ToArray(),
                    rolledBack, conflicts, "Veröffentlichung fehlgeschlagen: " + error.Message
                        + (conflicts.Count == 0 ? " Bereits übernommene Dateien wurden zurückgesetzt."
                            : " Einige Dateien konnten nicht zurückgesetzt werden; veröffentlichte Pfade, Konflikte und Wiederherstellungsdateien sind im Beleg enthalten."), recovery);
            }
        }
        finally
        {
            foreach (var temporary in temporaryFiles)
                try { RejectLinks(temporary); File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            foreach (var directory in createdDirectories.OrderByDescending(path => path.Length))
                try { RejectLinks(directory); if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task<string?> CurrentHashAsync(string target)
    {
        RejectLinks(target);
        return File.Exists(target) ? Hash(await File.ReadAllBytesAsync(target, CancellationToken.None).ConfigureAwait(false)) : null;
    }

    private static string LeasePath(string root, string entry) => Path.Combine(root, ".leases", Path.GetFileName(entry) + ".lock");

    /// <summary>
    /// Retain at most 16 inactive copies / 8 GiB and at most seven days, oldest
    /// first. Active file leases always win over the budget. The current copy is
    /// leased during both cleanup passes and survives until a later command.
    /// </summary>
    internal static int CleanupInactiveCopies(string cacheRoot, int maximumCopies = RetainedCopyLimit,
        long maximumBytes = RetainedByteLimit, DateTimeOffset? now = null)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheRoot));
        var deleted = 0;
        try
        {
            RejectLinks(root);
            if (!Directory.Exists(root)) return 0;
            var leases = Path.Combine(root, ".leases");
            RejectLinks(leases);
            Directory.CreateDirectory(leases);
            var cleanupLock = Path.Combine(leases, "retention.lock");
            RejectLinks(cleanupLock);
            using var cleanupLease = new FileStream(cleanupLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var candidates = new List<(string Path, DateTime LastUsed, long Bytes)>();
            foreach (var entry in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(entry);
                if (name.Length != 64 || !name.All(char.IsAsciiHexDigit)) continue;
                try
                {
                    RejectLinks(entry);
                    var marker = Path.Combine(entry, "owner.json");
                    RejectLinks(marker);
                    if (File.Exists(marker))
                    {
                        if (new FileInfo(marker).Length > 1024) continue;
                        using var ownership = JsonDocument.Parse(File.ReadAllText(marker));
                        if (ownership.RootElement.ValueKind != JsonValueKind.Object
                            || !ownership.RootElement.TryGetProperty("schema", out var schema)
                            || schema.ValueKind != JsonValueKind.String || schema.GetString() != OwnerMarker) continue;
                        candidates.Add((entry, File.GetLastWriteTimeUtc(marker), RetentionSize(entry)));
                    }
                    else if (IsLegacyEntry(entry))
                    {
                        // Recognize only the exact previous on-disk layout.
                        // Unknown directories remain outside our ownership.
                        candidates.Add((entry, Directory.GetLastWriteTimeUtc(Path.Combine(entry, "workspace")), RetentionSize(entry)));
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
            }
            var count = candidates.Count;
            var totalBytes = candidates.Sum(entry => entry.Bytes);
            var cutoff = (now ?? DateTimeOffset.UtcNow).UtcDateTime - RetainedAge;
            foreach (var candidate in candidates.OrderBy(entry => entry.LastUsed))
            {
                if (candidate.LastUsed >= cutoff && count <= maximumCopies && totalBytes <= maximumBytes) continue;
                var leasePath = LeasePath(root, candidate.Path);
                try
                {
                    RejectLinks(leasePath);
                    using (var lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    {
                        // Old app builds kept their lease inside the entry.
                        var legacy = Path.Combine(candidate.Path, "execution.lock");
                        RejectLinks(legacy);
                        using (var legacyLease = File.Exists(legacy)
                            ? new FileStream(legacy, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null)
                        {
                            var marker = Path.Combine(candidate.Path, "owner.json");
                            if (!File.Exists(marker))
                            {
                                // Adoption happens only while both lease types
                                // are held. Partial cleanup stays recognizable.
                                File.WriteAllText(marker, JsonSerializer.Serialize(new { schema = OwnerMarker }));
                                File.SetLastWriteTimeUtc(marker, candidate.LastUsed);
                            }
                            // Keep the old lease held while deleting contents.
                            // Its own file and entry survive until the handle is
                            // closed; an old app can never start mid-cleanup.
                            DeleteRetentionTree(root, candidate.Path, candidate.Path, legacyLease is null ? null : legacy);
                        }
                        if (Directory.Exists(candidate.Path))
                        {
                            File.Delete(legacy);
                            File.Delete(Path.Combine(candidate.Path, "owner.json"));
                            Directory.Delete(candidate.Path, recursive: false);
                        }
                        totalBytes -= candidate.Bytes;
                        count--;
                        deleted++;
                    }
                    File.Delete(leasePath);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return deleted;
    }

    private static bool IsLegacyEntry(string entry)
    {
        var legacy = Path.Combine(entry, "execution.lock");
        var workspace = Path.Combine(entry, "workspace");
        RejectLinks(legacy);
        RejectLinks(workspace);
        if (!File.Exists(legacy) || new FileInfo(legacy).Length != 0 || !Directory.Exists(workspace)) return false;
        return Directory.EnumerateFileSystemEntries(entry).All(path => path.Equals(legacy, StringComparison.OrdinalIgnoreCase)
            || path.Equals(workspace, StringComparison.OrdinalIgnoreCase));
    }

    private static long RetentionSize(string directory)
    {
        long bytes = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
            bytes += (attributes & FileAttributes.Directory) != 0 ? RetentionSize(path) : new FileInfo(path).Length;
        }
        return bytes;
    }

    private static void DeleteRetentionTree(string cacheRoot, string entry, string directory, string? preservedLease = null)
    {
        var absolute = Path.GetFullPath(directory);
        if (!entry.StartsWith(cacheRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !(absolute.Equals(entry, StringComparison.OrdinalIgnoreCase) || absolute.StartsWith(entry + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException("Die Cachebereinigung darf den eigenen Kopierbereich nicht verlassen.");
        RejectLinks(directory);
        // Keep the ownership receipt until the rest is gone, so a locked file
        // cannot leave an unrecognizable, permanently uncollectable directory.
        foreach (var path in Directory.EnumerateFileSystemEntries(directory).OrderBy(path => Path.GetFileName(path) == "owner.json" ? 1 : 0))
        {
            if (path.Equals(preservedLease, StringComparison.OrdinalIgnoreCase)
                || preservedLease is not null && directory.Equals(entry, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(path).Equals("owner.json", StringComparison.OrdinalIgnoreCase)) continue;
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if ((attributes & FileAttributes.ReparsePoint) != 0) Directory.Delete(path, recursive: false);
                else DeleteRetentionTree(cacheRoot, entry, path, preservedLease);
            }
            else File.Delete(path);
        }
        if (preservedLease is null || !directory.Equals(entry, StringComparison.OrdinalIgnoreCase))
            Directory.Delete(directory, recursive: false);
    }

    private IEnumerable<string> Files(string root, CancellationToken token, bool rejectLinks = false)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            RejectLinks(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (Path.GetFileName(path).Equals(".git", StringComparison.OrdinalIgnoreCase)) continue;
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if (rejectLinks) throw new UnauthorizedAccessException("Ein Prozess hat einen Link in der Subagent-Kopie hinterlassen. Änderungen werden nicht übernommen: " + Relative(root, path));
                    continue;
                }
                if ((attributes & FileAttributes.Directory) == 0) { yield return path; continue; }
                var relative = Relative(root, path) + "/";
                if (!Generated.Contains(Path.GetFileName(path)) || _scopes.Any(scope => scope.StartsWith(relative, StringComparison.OrdinalIgnoreCase))) pending.Push(path);
            }
        }
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool IsDeviceName(string part)
    {
        var name = part.Split('.')[0].ToLowerInvariant();
        return name is "con" or "prn" or "aux" or "nul"
            || (name.Length == 4 && (name.StartsWith("com", StringComparison.Ordinal) || name.StartsWith("lpt", StringComparison.Ordinal))
                && name[3] is >= '1' and <= '9');
    }

    private static async Task ReplaceFileAsync(string target, byte[] bytes, CancellationToken token)
    {
        RejectLinks(target);
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, ".go-agent-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false);
            RejectLinks(target);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static string Resolve(string root, string relative)
    {
        _ = NormalizeScope(relative);
        var target = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Der Subagent-Pfad verlässt den Workspace.");
        RejectLinks(target);
        return target;
    }
    private static void RejectLinks(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Links/Junctions sind im Subagent-Dateitransfer nicht zulässig.");
    }
}
