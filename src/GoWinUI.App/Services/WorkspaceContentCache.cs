using Microsoft.Data.Sqlite;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TreeSitter;

namespace GoWinUI.App.Services;

public sealed record WorkspaceCachedRead(
    string FileId,
    string EvidenceId,
    string Path,
    string Sha256,
    long WorkspaceRevision,
    int StartLine,
    int EndLine,
    int TotalLines,
    string Text,
    bool Truncated,
    bool CacheHit);

public sealed record WorkspaceCodeIndexMatch(
    string EvidenceId,
    string FileId,
    string Path,
    string Sha256,
    long WorkspaceRevision,
    string Kind,
    string Name,
    int Line,
    int EndLine,
    string? Target = null);

public sealed record WorkspaceOrientationContext(
    string RepositoryRevision,
    long WorkspaceRevision,
    bool IsEmpty,
    IReadOnlyList<WorkspaceCachedRead> Evidence,
    IReadOnlyList<string> ProjectProfiles,
    bool CacheHit = false);

public sealed record WorkspaceSearchDocument(
    string Path,
    string Sha256,
    string Text,
    long WorkspaceRevision,
    double Rank);

/// <summary>
/// Local, content-addressed source cache for coding workspaces. Full source text
/// never leaves the client unless an explicit client tool observation requests it.
/// </summary>
public sealed class WorkspaceContentCache : IDisposable
{
    private const long MaximumDatabaseContentBytes = 5L * 1024 * 1024 * 1024;
    private const int MaximumStoredFileBytes = 4 * 1024 * 1024;
    private static readonly Regex TokenRegex = new(
        @"[\p{L}\p{N}_]{2,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));
    private static readonly Regex DependencyRegex = new(
        "(?:^|\\n)\\s*(?:using|import|from|require|use|open|include|#include)\\s*(?:\\(|<|[\\\"'])*(?<target>[A-Za-z0-9_./:@-]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(500));
    private static readonly Regex FallbackSymbolRegex = new(
        @"(?m)^\s*(?:(?:public|private|protected|internal|static|sealed|abstract|async|export)\s+)*(?<kind>class|struct|interface|enum|record|def|theorem|lemma|namespace|module|function|fn|func|type|trait)\s+(?<name>[\p{L}_][\p{L}\p{N}_']*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(500));
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Lazy<bool> TreeSitterRuntimeAvailable = new(ProbeTreeSitterRuntime);

    private readonly string _databasePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, (string RevisionHash, long Revision, bool Complete)> _synchronized = new(StringComparer.Ordinal);
    private bool _initialized;
    private bool _disposed;

    public WorkspaceContentCache(string cacheDirectory)
    {
        Directory.CreateDirectory(cacheDirectory);
        _databasePath = Path.Combine(cacheDirectory, "workspace-content-v2.sqlite3");
    }

    public async Task<long> SynchronizeAsync(
        WorkspaceIndexSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_synchronized.TryGetValue(snapshot.WorkspaceFingerprint, out var synchronized)
            && synchronized.Complete
            && string.Equals(synchronized.RevisionHash, snapshot.RevisionFingerprint, StringComparison.Ordinal))
        {
            return synchronized.Revision;
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_synchronized.TryGetValue(snapshot.WorkspaceFingerprint, out synchronized)
                && synchronized.Complete
                && string.Equals(synchronized.RevisionHash, snapshot.RevisionFingerprint, StringComparison.Ordinal))
            {
                return synchronized.Revision;
            }
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var previousRevisionHash = await ScalarStringAsync(
                connection,
                transaction,
                "SELECT revision_hash FROM workspace_roots WHERE root_id = $root_id;",
                cancellationToken,
                ("$root_id", snapshot.WorkspaceFingerprint)).ConfigureAwait(false);
            var previousRevision = await ScalarLongAsync(
                connection,
                transaction,
                "SELECT revision FROM workspace_roots WHERE root_id = $root_id;",
                cancellationToken,
                ("$root_id", snapshot.WorkspaceFingerprint)).ConfigureAwait(false);
            var revision = string.Equals(previousRevisionHash, snapshot.RevisionFingerprint, StringComparison.Ordinal)
                ? Math.Max(1, previousRevision)
                : Math.Max(1, previousRevision + 1);

            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO workspace_roots(root_id, root_path, revision, revision_hash, indexed_at, last_access_at)
                VALUES($root_id, $root_path, $revision, $revision_hash, $now, $now)
                ON CONFLICT(root_id) DO UPDATE SET
                    root_path = excluded.root_path,
                    revision = excluded.revision,
                    revision_hash = excluded.revision_hash,
                    indexed_at = excluded.indexed_at,
                    last_access_at = excluded.last_access_at;
                """,
                cancellationToken,
                ("$root_id", snapshot.WorkspaceFingerprint),
                ("$root_path", snapshot.Root),
                ("$revision", (object)revision),
                ("$revision_hash", snapshot.RevisionFingerprint),
                ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())).ConfigureAwait(false);

            var known = await ReadKnownFilesAsync(
                connection,
                transaction,
                snapshot.WorkspaceFingerprint,
                cancellationToken).ConfigureAwait(false);
            var currentPaths = snapshot.Entries.Select(static entry => entry.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var indexingComplete = true;
            foreach (var removed in known.Keys.Where(path => !currentPaths.Contains(path)).ToArray())
            {
                await RemovePathAsync(connection, transaction, snapshot.WorkspaceFingerprint, removed, cancellationToken).ConfigureAwait(false);
            }

            foreach (var entry in snapshot.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileId = CreateStableId("file", snapshot.WorkspaceFingerprint, entry.Path.ToLowerInvariant());
                var contentAvailable = !entry.IsBinary
                    && entry.ContentHashComplete
                    && entry.Length <= MaximumStoredFileBytes;
                var unchanged = known.TryGetValue(entry.Path, out var knownFile)
                    && string.Equals(knownFile.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase)
                    && (!contentAvailable || knownFile.ContentIndexed && knownFile.LineMapIndexed);
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO workspace_files(root_id, path, file_id, sha256, length, updated_at, language, is_binary, content_available, last_access_at)
                    VALUES($root_id, $path, $file_id, $sha256, $length, $updated_at, $language, $is_binary, $content_available, $now)
                    ON CONFLICT(root_id, path) DO UPDATE SET
                        file_id = excluded.file_id,
                        sha256 = excluded.sha256,
                        length = excluded.length,
                        updated_at = excluded.updated_at,
                        language = excluded.language,
                        is_binary = excluded.is_binary,
                        content_available = excluded.content_available,
                        last_access_at = excluded.last_access_at;
                    """,
                    cancellationToken,
                    ("$root_id", snapshot.WorkspaceFingerprint),
                    ("$path", entry.Path),
                    ("$file_id", fileId),
                    ("$sha256", entry.Sha256),
                    ("$length", entry.Length),
                    ("$updated_at", entry.UpdatedAt.ToUnixTimeMilliseconds()),
                    ("$language", entry.Language),
                    ("$is_binary", entry.IsBinary ? 1 : 0),
                    ("$content_available", contentAvailable ? 1 : 0),
                    ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())).ConfigureAwait(false);
                if (unchanged)
                {
                    continue;
                }

                await RemoveIndexedContentAsync(
                    connection,
                    transaction,
                    snapshot.WorkspaceFingerprint,
                    entry.Path,
                    cancellationToken).ConfigureAwait(false);
                if (!contentAvailable)
                {
                    continue;
                }

                var fullPath = Path.Combine(snapshot.Root, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                string text;
                try
                {
                    text = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
                {
                    indexingComplete = false;
                    continue;
                }
                if (text.Contains('\0'))
                {
                    indexingComplete = false;
                    continue;
                }

                var lineCount = CountLines(text);
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO workspace_contents(sha256, content, line_count, byte_length, last_access_at)
                    VALUES($sha256, $content, $line_count, $byte_length, $now)
                    ON CONFLICT(sha256) DO UPDATE SET last_access_at = excluded.last_access_at;
                    """,
                    cancellationToken,
                    ("$sha256", entry.Sha256),
                    ("$content", text),
                    ("$line_count", lineCount),
                    ("$byte_length", Encoding.UTF8.GetByteCount(text)),
                    ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())).ConfigureAwait(false);
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO workspace_line_maps(sha256, offsets)
                    VALUES($sha256, $offsets)
                    ON CONFLICT(sha256) DO UPDATE SET offsets = excluded.offsets;
                    """,
                    cancellationToken,
                    ("$sha256", entry.Sha256),
                    ("$offsets", SerializeLineOffsets(text))).ConfigureAwait(false);
                await ExecuteAsync(
                    connection,
                    transaction,
                    "INSERT INTO workspace_fts(root_id, path, sha256, content) VALUES($root_id, $path, $sha256, $content);",
                    cancellationToken,
                    ("$root_id", snapshot.WorkspaceFingerprint),
                    ("$path", entry.Path),
                    ("$sha256", entry.Sha256),
                    ("$content", text)).ConfigureAwait(false);

                foreach (var symbol in ExtractSymbols(entry.Language, text))
                {
                    await ExecuteAsync(
                        connection,
                        transaction,
                        "INSERT INTO workspace_symbols(root_id, path, sha256, name, kind, line, end_line) VALUES($root_id, $path, $sha256, $name, $kind, $line, $end_line);",
                        cancellationToken,
                        ("$root_id", snapshot.WorkspaceFingerprint),
                        ("$path", entry.Path),
                        ("$sha256", entry.Sha256),
                        ("$name", symbol.Name),
                        ("$kind", symbol.Kind),
                        ("$line", symbol.Line),
                        ("$end_line", symbol.EndLine)).ConfigureAwait(false);
                }
                foreach (var dependency in ExtractDependencies(text))
                {
                    await ExecuteAsync(
                        connection,
                        transaction,
                        "INSERT INTO workspace_edges(root_id, path, sha256, target, kind, line) VALUES($root_id, $path, $sha256, $target, 'import', $line);",
                        cancellationToken,
                        ("$root_id", snapshot.WorkspaceFingerprint),
                        ("$path", entry.Path),
                        ("$sha256", entry.Sha256),
                        ("$target", dependency.Target),
                        ("$line", dependency.Line)).ConfigureAwait(false);
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            await PruneAsync(connection, snapshot.WorkspaceFingerprint, cancellationToken).ConfigureAwait(false);
            _synchronized[snapshot.WorkspaceFingerprint] = (snapshot.RevisionFingerprint, revision, indexingComplete);
            return revision;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WorkspaceCachedRead?> ReadAsync(
        WorkspaceIndexSnapshot snapshot,
        string relativePath,
        int startLine,
        int? endLine,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        await SynchronizeAsync(snapshot, cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT f.file_id, f.path, f.sha256, c.content, c.line_count, r.revision, m.offsets
                FROM workspace_files f
                JOIN workspace_roots r ON r.root_id = f.root_id
                JOIN workspace_contents c ON c.sha256 = f.sha256
                JOIN workspace_line_maps m ON m.sha256 = f.sha256
                WHERE f.root_id = $root_id AND f.path = $path COLLATE NOCASE AND f.content_available = 1;
                """;
            command.Parameters.AddWithValue("$root_id", snapshot.WorkspaceFingerprint);
            command.Parameters.AddWithValue("$path", NormalizeRelativePath(relativePath));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
            var fileId = reader.GetString(0);
            var path = reader.GetString(1);
            var sha = reader.GetString(2);
            var content = reader.GetString(3);
            var totalLines = reader.GetInt32(4);
            var revision = reader.GetInt64(5);
            var lineOffsets = DeserializeLineOffsets((byte[])reader[6]);
            var first = Math.Clamp(startLine, 1, Math.Max(1, totalLines));
            var last = Math.Clamp(endLine ?? totalLines, first, Math.Max(first, totalLines));
            var selected = ReadLineRange(content, lineOffsets, first, last, totalLines);
            var truncated = false;
            if (selected.Length > maximumCharacters)
            {
                selected = selected[..maximumCharacters];
                truncated = true;
            }
            return new WorkspaceCachedRead(
                fileId,
                CreateStableId(
                    "evidence",
                    snapshot.WorkspaceFingerprint,
                    path.ToLowerInvariant(),
                    sha,
                    first.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    last.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                path,
                sha,
                revision,
                first,
                last,
                totalLines,
                selected,
                truncated,
                CacheHit: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<WorkspaceCodeIndexMatch>> QueryAsync(
        WorkspaceIndexSnapshot snapshot,
        string operation,
        string query,
        string? path,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        var revision = await SynchronizeAsync(snapshot, cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            var pathFilter = string.IsNullOrWhiteSpace(path)
                ? string.Empty
                : " AND s.path LIKE $path_prefix ESCAPE '\\' ";
            if (operation.Equals("symbols", StringComparison.OrdinalIgnoreCase))
            {
                command.CommandText =
                    $"""
                    SELECT f.file_id, s.path, s.sha256, s.kind, s.name, s.line, s.end_line
                    FROM workspace_symbols s
                    JOIN workspace_files f ON f.root_id = s.root_id AND f.path = s.path
                    WHERE s.root_id = $root_id AND s.name LIKE $query ESCAPE '\' COLLATE NOCASE {pathFilter}
                    ORDER BY CASE WHEN s.name = $exact COLLATE NOCASE THEN 0 ELSE 1 END, s.path, s.line
                    LIMIT $maximum;
                    """;
            }
            else if (operation.Equals("references", StringComparison.OrdinalIgnoreCase))
            {
                pathFilter = string.IsNullOrWhiteSpace(path)
                    ? string.Empty
                    : " AND e.path LIKE $path_prefix ESCAPE '\\' ";
                command.CommandText =
                    $"""
                    SELECT f.file_id, e.path, e.sha256, e.kind, e.target, e.line, e.line
                    FROM workspace_edges e
                    JOIN workspace_files f ON f.root_id = e.root_id AND f.path = e.path
                    WHERE e.root_id = $root_id AND e.target LIKE $query ESCAPE '\' COLLATE NOCASE {pathFilter}
                    ORDER BY e.path, e.line
                    LIMIT $maximum;
                    """;
            }
            else
            {
                throw new InvalidDataException("workspace.index.query unterstützt nur symbols oder references.");
            }
            command.Parameters.AddWithValue("$root_id", snapshot.WorkspaceFingerprint);
            command.Parameters.AddWithValue("$query", $"%{EscapeLike(query)}%");
            command.Parameters.AddWithValue("$exact", query);
            command.Parameters.AddWithValue("$maximum", Math.Clamp(maximumResults, 1, 500));
            if (!string.IsNullOrWhiteSpace(path))
            {
                command.Parameters.AddWithValue("$path_prefix", EscapeLike(NormalizeRelativePath(path)) + "%");
            }
            var results = new List<WorkspaceCodeIndexMatch>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var fileId = reader.GetString(0);
                var resultPath = reader.GetString(1);
                var sha = reader.GetString(2);
                var kind = reader.GetString(3);
                var name = reader.GetString(4);
                var line = reader.GetInt32(5);
                var lastLine = reader.GetInt32(6);
                results.Add(new WorkspaceCodeIndexMatch(
                    CreateStableId(
                        "evidence",
                        snapshot.WorkspaceFingerprint,
                        resultPath.ToLowerInvariant(),
                        sha,
                        operation,
                        name,
                        line.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    fileId,
                    resultPath,
                    sha,
                    revision,
                    kind,
                    name,
                    line,
                    lastLine,
                    operation.Equals("references", StringComparison.OrdinalIgnoreCase) ? name : null));
            }
            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WorkspaceOrientationContext> BuildOrientationAsync(
        WorkspaceIndexSnapshot snapshot,
        string prompt,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var revision = await SynchronizeAsync(snapshot, cancellationToken).ConfigureAwait(false);
        if (snapshot.Entries.Count == 0)
        {
            return new WorkspaceOrientationContext(
                snapshot.RevisionFingerprint,
                revision,
                true,
                Array.Empty<WorkspaceCachedRead>(),
                Array.Empty<string>(),
                CacheHit: false);
        }

        var tokens = TokenRegex.Matches(prompt)
            .Select(static match => match.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static value => value.Length)
            .Take(12)
            .ToArray();
        var promptHash = CreateStableId("prompt", prompt.Trim().ToLowerInvariant());
        var ranked = await TryReadRetrievalAsync(
            snapshot.WorkspaceFingerprint,
            snapshot.RevisionFingerprint,
            promptHash,
            cancellationToken).ConfigureAwait(false);
        var cacheHit = ranked is not null;
        ranked ??= await RankOrientationAsync(snapshot, tokens, cancellationToken).ConfigureAwait(false);
        var evidence = new List<WorkspaceCachedRead>();
        var remaining = Math.Clamp(maximumCharacters, 1_000, 12_000);
        foreach (var candidate in ranked)
        {
            if (remaining <= 0)
            {
                break;
            }
            var read = await ReadAsync(
                snapshot,
                candidate.Path,
                candidate.StartLine,
                candidate.EndLine,
                Math.Min(remaining, 3_000),
                cancellationToken).ConfigureAwait(false);
            if (read is null)
            {
                continue;
            }
            evidence.Add(read);
            remaining -= read.Text.Length;
        }
        if (!cacheHit)
        {
            await SaveRetrievalAsync(
                snapshot.WorkspaceFingerprint,
                snapshot.RevisionFingerprint,
                promptHash,
                ranked,
                cancellationToken).ConfigureAwait(false);
        }
        return new WorkspaceOrientationContext(
            snapshot.RevisionFingerprint,
            revision,
            false,
            evidence,
            DetectProfiles(snapshot.Entries),
            cacheHit);
    }

    public async Task<IReadOnlyList<WorkspaceSearchDocument>> FindSearchDocumentsAsync(
        WorkspaceIndexSnapshot snapshot,
        IReadOnlyList<string> queries,
        string? relativeRoot,
        int maximumDocuments,
        CancellationToken cancellationToken)
    {
        var revision = await SynchronizeAsync(snapshot, cancellationToken).ConfigureAwait(false);
        var tokens = queries
            .SelectMany(query => TokenRegex.Matches(query).Select(static match => match.Value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static token => token.Length)
            .Take(24)
            .ToArray();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            var results = new List<WorkspaceSearchDocument>();
            if (tokens.Length > 0)
            {
                await using var fts = connection.CreateCommand();
                fts.CommandText =
                    """
                    SELECT f.path, f.sha256, c.content, bm25(workspace_fts)
                    FROM workspace_fts
                    JOIN workspace_files f
                      ON f.root_id = workspace_fts.root_id AND f.path = workspace_fts.path
                    JOIN workspace_contents c ON c.sha256 = f.sha256
                    WHERE workspace_fts MATCH $match
                      AND workspace_fts.root_id = $root_id
                      AND ($path_prefix = '' OR f.path LIKE $path_prefix ESCAPE '\')
                    ORDER BY bm25(workspace_fts), f.path
                    LIMIT $maximum;
                    """;
                fts.Parameters.AddWithValue("$match", BuildFtsQuery(tokens));
                fts.Parameters.AddWithValue("$root_id", snapshot.WorkspaceFingerprint);
                fts.Parameters.AddWithValue(
                    "$path_prefix",
                    string.IsNullOrWhiteSpace(relativeRoot)
                        ? string.Empty
                        : EscapeLike(NormalizeRelativePath(relativeRoot)) + "%");
                fts.Parameters.AddWithValue("$maximum", Math.Clamp(maximumDocuments, 1, 2_000));
                await using var reader = await fts.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    results.Add(new WorkspaceSearchDocument(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        revision,
                        reader.GetDouble(3)));
                }
            }
            if (results.Count > 0 || queries.Count == 0)
            {
                return results;
            }

            // FTS tokenization intentionally ignores punctuation. This bounded
            // exact fallback preserves searches for identifiers and operators.
            await using var exact = connection.CreateCommand();
            var predicates = new List<string>();
            for (var index = 0; index < Math.Min(queries.Count, 16); index++)
            {
                var parameter = "$query" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                predicates.Add($"instr(lower(c.content), lower({parameter})) > 0");
                exact.Parameters.AddWithValue(parameter, queries[index]);
            }
            exact.CommandText =
                $"""
                SELECT f.path, f.sha256, c.content
                FROM workspace_files f
                JOIN workspace_contents c ON c.sha256 = f.sha256
                WHERE f.root_id = $root_id
                  AND ($path_prefix = '' OR f.path LIKE $path_prefix ESCAPE '\')
                  AND ({string.Join(" OR ", predicates)})
                ORDER BY f.path
                LIMIT $maximum;
                """;
            exact.Parameters.AddWithValue("$root_id", snapshot.WorkspaceFingerprint);
            exact.Parameters.AddWithValue(
                "$path_prefix",
                string.IsNullOrWhiteSpace(relativeRoot)
                    ? string.Empty
                    : EscapeLike(NormalizeRelativePath(relativeRoot)) + "%");
            exact.Parameters.AddWithValue("$maximum", Math.Clamp(maximumDocuments, 1, 2_000));
            await using var exactReader = await exact.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await exactReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(new WorkspaceSearchDocument(
                    exactReader.GetString(0),
                    exactReader.GetString(1),
                    exactReader.GetString(2),
                    revision,
                    0));
            }
            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<OrientationSelection>> RankOrientationAsync(
        WorkspaceIndexSnapshot snapshot,
        string[] tokens,
        CancellationToken cancellationToken)
    {
        var changedPaths = (snapshot.ChangedPaths ?? Array.Empty<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = snapshot.Entries
            .Where(static entry => !entry.IsBinary && entry.Length <= MaximumStoredFileBytes)
            .ToDictionary(
                static entry => entry.Path,
                entry => new RankedFile(
                    entry,
                    Math.Max(1, OrientationPriority(entry, tokens))
                    + (changedPaths.Contains(entry.Path) ? 55 : 0)),
                StringComparer.OrdinalIgnoreCase);
        var bestSymbolLines = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            if (tokens.Length > 0)
            {
                await using var fts = connection.CreateCommand();
                fts.CommandText =
                    """
                    SELECT path, bm25(workspace_fts)
                    FROM workspace_fts
                    WHERE workspace_fts MATCH $match AND root_id = $root_id
                    ORDER BY bm25(workspace_fts), path
                    LIMIT 128;
                    """;
                fts.Parameters.AddWithValue("$match", BuildFtsQuery(tokens));
                fts.Parameters.AddWithValue("$root_id", snapshot.WorkspaceFingerprint);
                await using (var reader = await fts.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    var rank = 0;
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        if (candidates.TryGetValue(reader.GetString(0), out var candidate))
                        {
                            candidate.Score += Math.Max(25, 140 - rank);
                            candidate.FtsRank = reader.GetDouble(1);
                        }
                        rank++;
                    }
                }

                await using var symbols = connection.CreateCommand();
                var symbolPredicates = new List<string>();
                for (var index = 0; index < tokens.Length; index++)
                {
                    var parameter = "$symbol" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    symbolPredicates.Add($"name LIKE {parameter} ESCAPE '\\' COLLATE NOCASE");
                    symbols.Parameters.AddWithValue(parameter, "%" + EscapeLike(tokens[index]) + "%");
                }
                symbols.CommandText =
                    $"""
                    SELECT path, line, name
                    FROM workspace_symbols
                    WHERE root_id = $root_id AND ({string.Join(" OR ", symbolPredicates)})
                    ORDER BY path, line
                    LIMIT 256;
                    """;
                symbols.Parameters.AddWithValue("$root_id", snapshot.WorkspaceFingerprint);
                await using var symbolReader = await symbols.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await symbolReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var path = symbolReader.GetString(0);
                    if (candidates.TryGetValue(path, out var candidate))
                    {
                        candidate.Score += tokens.Any(token => string.Equals(token, symbolReader.GetString(2), StringComparison.OrdinalIgnoreCase))
                            ? 100
                            : 60;
                        bestSymbolLines.TryAdd(path, symbolReader.GetInt32(1));
                    }
                }
            }

            var stemPaths = candidates.Keys
                .GroupBy(static path => Path.GetFileNameWithoutExtension(path).ToLowerInvariant(), StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);
            await using var edges = connection.CreateCommand();
            edges.CommandText = "SELECT target FROM workspace_edges WHERE root_id = $root_id;";
            edges.Parameters.AddWithValue("$root_id", snapshot.WorkspaceFingerprint);
            await using var edgeReader = await edges.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await edgeReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var target = edgeReader.GetString(0).Replace('\\', '/').Replace('.', '/');
                var stem = Path.GetFileNameWithoutExtension(target).ToLowerInvariant();
                if (!stemPaths.TryGetValue(stem, out var paths))
                {
                    continue;
                }
                foreach (var path in paths)
                {
                    candidates[path].Score += 4;
                    candidates[path].InboundReferences++;
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        return candidates.Values
            .OrderByDescending(static item => item.Score + Math.Min(40, item.InboundReferences * 2))
            .ThenBy(static item => item.FtsRank)
            .ThenBy(static item => item.Entry.Path, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .Select(item =>
            {
                var symbolLine = bestSymbolLines.GetValueOrDefault(item.Entry.Path);
                return symbolLine > 0
                    ? new OrientationSelection(item.Entry.Path, Math.Max(1, symbolLine - 4), symbolLine + 36)
                    : new OrientationSelection(item.Entry.Path, 1, 80);
            })
            .ToList();
    }

    private async Task<List<OrientationSelection>?> TryReadRetrievalAsync(
        string rootId,
        string revisionHash,
        string promptHash,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT selection_json
                FROM workspace_retrievals
                WHERE root_id = $root_id AND revision_hash = $revision_hash AND prompt_hash = $prompt_hash;
                """;
            command.Parameters.AddWithValue("$root_id", rootId);
            command.Parameters.AddWithValue("$revision_hash", revisionHash);
            command.Parameters.AddWithValue("$prompt_hash", promptHash);
            var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }
            await using var touch = connection.CreateCommand();
            touch.CommandText =
                "UPDATE workspace_retrievals SET last_access_at = $now WHERE root_id = $root_id AND revision_hash = $revision_hash AND prompt_hash = $prompt_hash;";
            touch.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            touch.Parameters.AddWithValue("$root_id", rootId);
            touch.Parameters.AddWithValue("$revision_hash", revisionHash);
            touch.Parameters.AddWithValue("$prompt_hash", promptHash);
            await touch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<List<OrientationSelection>>(json, JsonOptions);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SaveRetrievalAsync(
        string rootId,
        string revisionHash,
        string promptHash,
        IReadOnlyList<OrientationSelection> selections,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO workspace_retrievals(root_id, revision_hash, prompt_hash, selection_json, created_at, last_access_at)
                VALUES($root_id, $revision_hash, $prompt_hash, $selection_json, $now, $now)
                ON CONFLICT(root_id, revision_hash, prompt_hash) DO UPDATE SET
                    selection_json = excluded.selection_json,
                    last_access_at = excluded.last_access_at;
                DELETE FROM workspace_retrievals
                WHERE rowid IN (
                    SELECT rowid FROM workspace_retrievals
                    WHERE root_id = $root_id
                    ORDER BY last_access_at DESC
                    LIMIT -1 OFFSET 512
                );
                """;
            command.Parameters.AddWithValue("$root_id", rootId);
            command.Parameters.AddWithValue("$revision_hash", revisionHash);
            command.Parameters.AddWithValue("$prompt_hash", promptHash);
            command.Parameters.AddWithValue("$selection_json", JsonSerializer.Serialize(selections, JsonOptions));
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string BuildFtsQuery(IEnumerable<string> tokens) => string.Join(
        " OR ",
        tokens.Select(static token => "\"" + token.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _gate.Dispose();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (!_initialized)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = Schema;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        return connection;
    }

    private static async Task PruneAsync(
        SqliteConnection connection,
        string activeRootId,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            transaction: null,
            "DELETE FROM workspace_contents WHERE sha256 NOT IN (SELECT DISTINCT sha256 FROM workspace_files WHERE content_available = 1);",
            cancellationToken).ConfigureAwait(false);
        var total = await ScalarLongAsync(
            connection,
            transaction: null,
            "SELECT COALESCE(SUM(byte_length), 0) FROM workspace_contents;",
            cancellationToken).ConfigureAwait(false);
        if (total <= MaximumDatabaseContentBytes)
        {
            return;
        }
        await using var roots = connection.CreateCommand();
        roots.CommandText = "SELECT root_id FROM workspace_roots WHERE root_id <> $active ORDER BY last_access_at ASC;";
        roots.Parameters.AddWithValue("$active", activeRootId);
        var removable = new List<string>();
        await using (var reader = await roots.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                removable.Add(reader.GetString(0));
            }
        }
        foreach (var rootId in removable)
        {
            await ExecuteAsync(
                connection,
                transaction: null,
                "DELETE FROM workspace_roots WHERE root_id = $root_id; DELETE FROM workspace_fts WHERE root_id = $root_id;",
                cancellationToken,
                ("$root_id", rootId)).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                transaction: null,
                "DELETE FROM workspace_contents WHERE sha256 NOT IN (SELECT DISTINCT sha256 FROM workspace_files WHERE content_available = 1);",
                cancellationToken).ConfigureAwait(false);
            total = await ScalarLongAsync(
                connection,
                transaction: null,
                "SELECT COALESCE(SUM(byte_length), 0) FROM workspace_contents;",
                cancellationToken).ConfigureAwait(false);
            if (total <= MaximumDatabaseContentBytes)
            {
                break;
            }
        }
    }

    private static List<ExtractedSymbol> ExtractSymbols(string languageName, string text)
    {
        var result = new List<ExtractedSymbol>();
        if (TryGetTreeSitter(languageName, out var language, out var queryText))
        {
            try
            {
                using (language)
                using (var parser = new Parser(language))
                using (var tree = parser.Parse(text))
                using (var query = new Query(language, queryText))
                using (var cursor = query.Execute(tree!.RootNode))
                {
                    foreach (var capture in cursor.Captures)
                    {
                        var value = capture.Node.Text.Trim();
                        if (value.Length is > 0 and <= 256)
                        {
                            result.Add(new ExtractedSymbol(
                                capture.Name,
                                value,
                                capture.Node.StartPosition.Row + 1,
                                capture.Node.EndPosition.Row + 1));
                        }
                    }
                    return result;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or DllNotFoundException)
            {
                // A parser mismatch must not make the content cache unavailable.
            }
        }
        foreach (Match match in FallbackSymbolRegex.Matches(text))
        {
            var line = 1 + text.AsSpan(0, match.Index).Count('\n');
            result.Add(new ExtractedSymbol(
                match.Groups["kind"].Value,
                match.Groups["name"].Value,
                line,
                line));
        }
        return result;
    }

    private static bool TryGetTreeSitter(string languageName, out Language language, out string query)
    {
        if (!TreeSitterRuntimeAvailable.Value)
        {
            language = null!;
            query = string.Empty;
            return false;
        }

        string? grammar = null;
        query = string.Empty;
        switch (languageName)
        {
            case "C#":
                grammar = "CSharp";
                query = "(class_declaration name: (identifier) @class) (struct_declaration name: (identifier) @struct) (interface_declaration name: (identifier) @interface) (enum_declaration name: (identifier) @enum) (method_declaration name: (identifier) @method) (namespace_declaration name: (_) @namespace)";
                break;
            case "Python":
                grammar = "Python";
                query = "(class_definition name: (identifier) @class) (function_definition name: (identifier) @function)";
                break;
            case "JavaScript":
                grammar = "JavaScript";
                query = "(class_declaration name: (identifier) @class) (function_declaration name: (identifier) @function) (method_definition name: (property_identifier) @method)";
                break;
            case "TypeScript":
                grammar = "TypeScript";
                query = "(class_declaration name: (type_identifier) @class) (interface_declaration name: (type_identifier) @interface) (function_declaration name: (identifier) @function) (method_definition name: (property_identifier) @method) (type_alias_declaration name: (type_identifier) @type)";
                break;
            case "Rust":
                grammar = "Rust";
                query = "(function_item name: (identifier) @function) (struct_item name: (type_identifier) @struct) (enum_item name: (type_identifier) @enum) (trait_item name: (type_identifier) @trait)";
                break;
            case "Go":
                grammar = "Go";
                query = "(function_declaration name: (identifier) @function) (method_declaration name: (field_identifier) @method) (type_spec name: (type_identifier) @type)";
                break;
            case "C":
                grammar = "C";
                query = "(function_declarator declarator: (identifier) @function) (struct_specifier name: (type_identifier) @struct) (enum_specifier name: (type_identifier) @enum)";
                break;
            case "C++":
                grammar = "Cpp";
                query = "(function_declarator declarator: (identifier) @function) (class_specifier name: (type_identifier) @class) (struct_specifier name: (type_identifier) @struct)";
                break;
        }
        if (grammar is null)
        {
            language = null!;
            return false;
        }
        try
        {
            language = new Language(grammar);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or DllNotFoundException)
        {
            language = null!;
            return false;
        }
    }

    private static bool ProbeTreeSitterRuntime()
    {
        if (!NativeLibrary.TryLoad(
                "tree-sitter",
                typeof(WorkspaceContentCache).Assembly,
                DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories,
                out var handle))
        {
            return false;
        }

        NativeLibrary.Free(handle);
        return true;
    }

    private static IEnumerable<ExtractedDependency> ExtractDependencies(string text)
    {
        foreach (Match match in DependencyRegex.Matches(text))
        {
            var target = match.Groups["target"].Value.Trim();
            if (target.Length is > 0 and <= 512)
            {
                yield return new ExtractedDependency(target, 1 + text.AsSpan(0, match.Index).Count('\n'));
            }
        }
    }

    private static int OrientationPriority(WorkspaceIndexEntry entry, IReadOnlyList<string> tokens)
    {
        var name = Path.GetFileName(entry.Path);
        var extension = Path.GetExtension(entry.Path);
        var score = tokens.Count(token => entry.Path.Contains(token, StringComparison.OrdinalIgnoreCase)) * 20;
        if (extension is ".sln" or ".slnx" or ".csproj" or ".fsproj" or ".vbproj"
            || name is "package.json" or "pyproject.toml" or "Cargo.toml" or "go.mod" or "lakefile.toml" or "lakefile.lean")
        {
            score += 40;
        }
        if (name.StartsWith("README", StringComparison.OrdinalIgnoreCase) || name.Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase))
        {
            score += 35;
        }
        if (name.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            score += tokens.Any(token => token.Contains("test", StringComparison.OrdinalIgnoreCase)) ? 30 : 5;
        }
        return score;
    }

    private static List<string> DetectProfiles(IReadOnlyList<WorkspaceIndexEntry> entries)
    {
        var names = entries.Select(static item => Path.GetFileName(item.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extensions = entries.Select(static item => Path.GetExtension(item.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var profiles = new List<string>();
        if (extensions.Contains(".xaml") && (extensions.Contains(".csproj") || extensions.Contains(".sln"))) profiles.Add("WinUI/.NET");
        else if (extensions.Contains(".csproj") || extensions.Contains(".sln")) profiles.Add(".NET");
        if (names.Contains("package.json")) profiles.Add("Node.js/npm");
        if (names.Contains("pyproject.toml") || names.Contains("requirements.txt")) profiles.Add("Python");
        if (names.Contains("Cargo.toml")) profiles.Add("Rust/Cargo");
        if (names.Contains("go.mod") || names.Contains("go.work")) profiles.Add("Go");
        if (names.Contains("lakefile.toml") || names.Contains("lakefile.lean") || names.Contains("lean-toolchain")) profiles.Add("Lean/Lake");
        return profiles;
    }

    private static async Task<Dictionary<string, KnownWorkspaceFile>> ReadKnownFilesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string rootId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT f.path,
                   f.sha256,
                   CASE WHEN f.content_available = 1
                              AND EXISTS(SELECT 1 FROM workspace_contents c WHERE c.sha256 = f.sha256)
                        THEN 1 ELSE 0 END AS content_indexed,
                   CASE WHEN EXISTS(SELECT 1 FROM workspace_line_maps m WHERE m.sha256 = f.sha256)
                        THEN 1 ELSE 0 END AS line_map_indexed
            FROM workspace_files f
            WHERE f.root_id = $root_id;
            """;
        command.Parameters.AddWithValue("$root_id", rootId);
        var result = new Dictionary<string, KnownWorkspaceFile>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result[reader.GetString(0)] = new KnownWorkspaceFile(
                reader.GetString(1),
                reader.GetInt32(2) == 1,
                reader.GetInt32(3) == 1);
        }
        return result;
    }

    private sealed record KnownWorkspaceFile(string Sha256, bool ContentIndexed, bool LineMapIndexed);

    private static async Task RemovePathAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string rootId,
        string path,
        CancellationToken cancellationToken)
    {
        await RemoveIndexedContentAsync(connection, transaction, rootId, path, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            transaction,
            "DELETE FROM workspace_files WHERE root_id = $root_id AND path = $path;",
            cancellationToken,
            ("$root_id", rootId),
            ("$path", path)).ConfigureAwait(false);
    }

    private static async Task RemoveIndexedContentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string rootId,
        string path,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            transaction,
            """
            DELETE FROM workspace_fts WHERE root_id = $root_id AND path = $path;
            DELETE FROM workspace_symbols WHERE root_id = $root_id AND path = $path;
            DELETE FROM workspace_edges WHERE root_id = $root_id AND path = $path;
            """,
            cancellationToken,
            ("$root_id", rootId),
            ("$path", path)).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ScalarStringAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<long> ScalarLongAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? 0 : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string CreateStableId(string prefix, params string[] parts)
    {
        var value = string.Join('\n', parts);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        return $"{prefix}-{hash[..24]}";
    }

    private static string[] SplitLines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static int CountLines(string value) => BuildLineOffsets(value).Length;

    private static byte[] SerializeLineOffsets(string value)
    {
        var offsets = BuildLineOffsets(value);
        var bytes = new byte[offsets.Length * sizeof(int)];
        for (var index = 0; index < offsets.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(index * sizeof(int), sizeof(int)),
                offsets[index]);
        }
        return bytes;
    }

    private static int[] DeserializeLineOffsets(byte[] value)
    {
        if (value.Length == 0 || value.Length % sizeof(int) != 0)
        {
            throw new InvalidDataException("Der persistierte Zeilenindex ist ungültig.");
        }
        var offsets = new int[value.Length / sizeof(int)];
        for (var index = 0; index < offsets.Length; index++)
        {
            offsets[index] = BinaryPrimitives.ReadInt32LittleEndian(
                value.AsSpan(index * sizeof(int), sizeof(int)));
        }
        return offsets;
    }

    private static int[] BuildLineOffsets(string value)
    {
        var offsets = new List<int>(Math.Max(1, 1 + value.Length / 48)) { 0 };
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\r')
            {
                if (index + 1 < value.Length && value[index + 1] == '\n')
                {
                    index++;
                }
                offsets.Add(index + 1);
            }
            else if (value[index] == '\n')
            {
                offsets.Add(index + 1);
            }
        }
        return offsets.ToArray();
    }

    private static string ReadLineRange(
        string content,
        int[] offsets,
        int firstLine,
        int lastLine,
        int totalLines)
    {
        if (offsets.Length != totalLines
            || offsets.Length == 0
            || offsets[0] != 0
            || offsets.Any(offset => offset < 0 || offset > content.Length))
        {
            var lines = SplitLines(content);
            return string.Join('\n', lines[(firstLine - 1)..lastLine]);
        }

        var start = offsets[firstLine - 1];
        var end = lastLine < offsets.Length ? offsets[lastLine] : content.Length;
        var selected = content[start..end];
        if (lastLine < totalLines)
        {
            selected = selected.EndsWith("\r\n", StringComparison.Ordinal)
                ? selected[..^2]
                : selected.EndsWith('\r') || selected.EndsWith('\n')
                    ? selected[..^1]
                    : selected;
        }
        return selected.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }
    private static string NormalizeRelativePath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }
        return normalized == "." ? string.Empty : normalized;
    }
    private static string EscapeLike(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private sealed record ExtractedSymbol(string Kind, string Name, int Line, int EndLine);
    private sealed record ExtractedDependency(string Target, int Line);
    private sealed record OrientationSelection(string Path, int StartLine, int EndLine);

    private sealed class RankedFile(WorkspaceIndexEntry entry, int score)
    {
        public WorkspaceIndexEntry Entry { get; } = entry;
        public int Score { get; set; } = score;
        public int InboundReferences { get; set; }
        public double FtsRank { get; set; } = double.MaxValue;
    }

    private const string Schema =
        """
        PRAGMA journal_mode=WAL;
        PRAGMA foreign_keys=ON;
        PRAGMA synchronous=NORMAL;

        CREATE TABLE IF NOT EXISTS workspace_roots(
            root_id TEXT PRIMARY KEY,
            root_path TEXT NOT NULL,
            revision INTEGER NOT NULL,
            revision_hash TEXT NOT NULL,
            indexed_at INTEGER NOT NULL,
            last_access_at INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS workspace_contents(
            sha256 TEXT PRIMARY KEY,
            content TEXT NOT NULL,
            line_count INTEGER NOT NULL,
            byte_length INTEGER NOT NULL,
            last_access_at INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS workspace_line_maps(
            sha256 TEXT PRIMARY KEY,
            offsets BLOB NOT NULL,
            FOREIGN KEY(sha256) REFERENCES workspace_contents(sha256) ON DELETE CASCADE
        );
        CREATE TABLE IF NOT EXISTS workspace_files(
            root_id TEXT NOT NULL,
            path TEXT NOT NULL COLLATE NOCASE,
            file_id TEXT NOT NULL,
            sha256 TEXT NOT NULL,
            length INTEGER NOT NULL,
            updated_at INTEGER NOT NULL,
            language TEXT NOT NULL,
            is_binary INTEGER NOT NULL,
            content_available INTEGER NOT NULL,
            last_access_at INTEGER NOT NULL,
            PRIMARY KEY(root_id, path),
            FOREIGN KEY(root_id) REFERENCES workspace_roots(root_id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_workspace_files_sha ON workspace_files(sha256);
        CREATE TABLE IF NOT EXISTS workspace_symbols(
            root_id TEXT NOT NULL,
            path TEXT NOT NULL COLLATE NOCASE,
            sha256 TEXT NOT NULL,
            name TEXT NOT NULL COLLATE NOCASE,
            kind TEXT NOT NULL,
            line INTEGER NOT NULL,
            end_line INTEGER NOT NULL,
            FOREIGN KEY(root_id, path) REFERENCES workspace_files(root_id, path) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_workspace_symbols_lookup ON workspace_symbols(root_id, name, path);
        CREATE TABLE IF NOT EXISTS workspace_edges(
            root_id TEXT NOT NULL,
            path TEXT NOT NULL COLLATE NOCASE,
            sha256 TEXT NOT NULL,
            target TEXT NOT NULL COLLATE NOCASE,
            kind TEXT NOT NULL,
            line INTEGER NOT NULL,
            FOREIGN KEY(root_id, path) REFERENCES workspace_files(root_id, path) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_workspace_edges_lookup ON workspace_edges(root_id, target, path);
        CREATE TABLE IF NOT EXISTS workspace_retrievals(
            root_id TEXT NOT NULL,
            revision_hash TEXT NOT NULL,
            prompt_hash TEXT NOT NULL,
            selection_json TEXT NOT NULL,
            created_at INTEGER NOT NULL,
            last_access_at INTEGER NOT NULL,
            PRIMARY KEY(root_id, revision_hash, prompt_hash),
            FOREIGN KEY(root_id) REFERENCES workspace_roots(root_id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_workspace_retrievals_lru ON workspace_retrievals(root_id, last_access_at);
        CREATE VIRTUAL TABLE IF NOT EXISTS workspace_fts USING fts5(
            root_id UNINDEXED,
            path UNINDEXED,
            sha256 UNINDEXED,
            content,
            tokenize='unicode61 remove_diacritics 2'
        );
        """;
}
