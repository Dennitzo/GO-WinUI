using System.Globalization;
using GoWinUI.Core.Memory;
using Microsoft.Data.Sqlite;

namespace GoWinUI.Infrastructure.Memory;

public sealed class SqliteProjectMemoryStore : IProjectMemoryStore, IAsyncDisposable
{
    private const int SchemaVersion = 1;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _initialized;

    public SqliteProjectMemoryStore(ProjectMemoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SharedDataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DatabaseFileName);
        if (!string.Equals(Path.GetFileName(options.DatabaseFileName), options.DatabaseFileName, StringComparison.Ordinal)
            || Path.IsPathRooted(options.DatabaseFileName))
        {
            throw new ArgumentException("Der Dateiname des Projektgedächtnisses darf keinen Pfad enthalten.", nameof(options));
        }

        DatabasePath = Path.Combine(Path.GetFullPath(options.SharedDataDirectory), options.DatabaseFileName);
    }

    public string DatabasePath { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _initialized) == 1) return;
        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized == 1) return;
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            SQLitePCL.Batteries_V2.Init();
            await using var connection = await OpenConnectionCoreAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);

            await using (var wal = connection.CreateCommand())
            {
                wal.CommandText = "PRAGMA journal_mode=WAL;";
                await wal.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS project_memory_metadata(
                    schema_version INTEGER NOT NULL
                );

                INSERT INTO project_memory_metadata(schema_version)
                SELECT 1 WHERE NOT EXISTS(SELECT 1 FROM project_memory_metadata);

                CREATE TABLE IF NOT EXISTS project_memory_entries(
                    id TEXT PRIMARY KEY,
                    project_key TEXT NOT NULL,
                    project_id TEXT NULL,
                    kind TEXT NOT NULL CHECK(kind IN ('requirement','decision','milestone','error_resolution','verification')),
                    content TEXT NOT NULL,
                    source TEXT NOT NULL,
                    version INTEGER NOT NULL CHECK(version > 0),
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    last_confirmed_at TEXT NULL,
                    is_pinned INTEGER NOT NULL DEFAULT 0 CHECK(is_pinned IN (0,1))
                );

                CREATE INDEX IF NOT EXISTS ix_project_memory_scope_updated
                ON project_memory_entries(project_key,is_pinned DESC,updated_at DESC);

                CREATE TABLE IF NOT EXISTS project_memory_settings(
                    project_key TEXT PRIMARY KEY,
                    project_id TEXT NULL,
                    auto_capture_enabled INTEGER NOT NULL DEFAULT 1 CHECK(auto_capture_enabled IN (0,1)),
                    updated_at TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            command.CommandText = "SELECT schema_version FROM project_memory_metadata LIMIT 1;";
            var storedVersion = Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
            if (storedVersion != SchemaVersion)
            {
                throw new InvalidOperationException(
                    $"Nicht unterstützte Version des Projektgedächtnisses: {storedVersion}. Erwartet: {SchemaVersion}.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _initialized, 1);
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public async Task<bool> CheckIntegrityAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        var result = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<ProjectMemoryEntry>> ListAsync(
        ProjectMemoryScope scope,
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        scope = ValidateScope(scope);
        var normalizedSearch = search?.Trim() ?? string.Empty;
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,project_key,project_id,kind,content,source,version,created_at,updated_at,last_confirmed_at,is_pinned
            FROM project_memory_entries
            WHERE project_key=$project_key
              AND ($search='' OR instr(lower(content),lower($search))>0 OR instr(lower(source),lower($search))>0)
            ORDER BY is_pinned DESC,updated_at DESC,id;
            """;
        command.Parameters.AddWithValue("$project_key", scope.ProjectKey);
        command.Parameters.AddWithValue("$search", normalizedSearch);
        var result = new List<ProjectMemoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadEntry(reader));
        }

        return result;
    }

    public async Task<ProjectMemoryEntry?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadEntryAsync(connection, null, id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProjectMemoryEntry> CreateAsync(ProjectMemoryDraft draft, CancellationToken cancellationToken = default)
    {
        var validated = ValidateDraft(draft);
        return await WriteAsync(async (connection, transaction, token) =>
        {
            var created = CreateEntry(validated);
            await InsertAsync(connection, transaction, created, token).ConfigureAwait(false);
            return created;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProjectMemoryEntry?> CreateIfAutoCaptureEnabledAsync(
        ProjectMemoryDraft draft,
        CancellationToken cancellationToken = default)
    {
        var validated = ValidateDraft(draft);
        return await WriteAsync<ProjectMemoryEntry?>(async (connection, transaction, token) =>
        {
            if (!await IsAutoCaptureEnabledAsync(connection, transaction, validated.Scope.ProjectKey, token).ConfigureAwait(false))
            {
                return null;
            }

            var created = CreateEntry(validated);
            await InsertAsync(connection, transaction, created, token).ConfigureAwait(false);
            return created;
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<ProjectMemoryEntry> UpdateAsync(
        Guid id,
        long expectedVersion,
        ProjectMemoryUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ValidateMutableFields(update.Content, update.Source);
        return MutateAsync(
            id,
            expectedVersion,
            "kind=$kind,content=$content,source=$source,last_confirmed_at=$confirmed",
            command =>
            {
                command.Parameters.AddWithValue("$kind", ToStorage(update.Kind));
                command.Parameters.AddWithValue("$content", update.Content.Trim());
                command.Parameters.AddWithValue("$source", update.Source.Trim());
                command.Parameters.AddWithValue("$confirmed", ToDbValue(update.LastConfirmedAt));
            },
            cancellationToken);
    }

    public Task<ProjectMemoryEntry> SetPinnedAsync(
        Guid id,
        long expectedVersion,
        bool isPinned,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            id,
            expectedVersion,
            "is_pinned=$is_pinned",
            command => command.Parameters.AddWithValue("$is_pinned", isPinned ? 1 : 0),
            cancellationToken);

    public Task<ProjectMemoryEntry> ConfirmAsync(
        Guid id,
        long expectedVersion,
        DateTimeOffset? confirmedAt = null,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            id,
            expectedVersion,
            "last_confirmed_at=$confirmed",
            command => command.Parameters.AddWithValue("$confirmed", ToDbValue(confirmedAt ?? DateTimeOffset.UtcNow)),
            cancellationToken);

    public async Task DeleteAsync(Guid id, long expectedVersion, CancellationToken cancellationToken = default)
    {
        EnsureExpectedVersion(expectedVersion);
        await WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM project_memory_entries WHERE id=$id AND version=$version;";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$version", expectedVersion);
            if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
            {
                throw new ProjectMemoryVersionConflictException(id);
            }
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProjectMemorySettings> GetSettingsAsync(
        ProjectMemoryScope scope,
        CancellationToken cancellationToken = default)
    {
        scope = ValidateScope(scope);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT project_id,auto_capture_enabled,updated_at
            FROM project_memory_settings WHERE project_key=$project_key;
            """;
        command.Parameters.AddWithValue("$project_key", scope.ProjectKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new ProjectMemorySettings(scope.ProjectKey, scope.ProjectId, true, DateTimeOffset.UnixEpoch);
        }

        return new ProjectMemorySettings(
            scope.ProjectKey,
            reader.IsDBNull(0) ? scope.ProjectId : reader.GetString(0),
            reader.GetInt64(1) == 1,
            ReadDate(reader.GetString(2)));
    }

    public Task<ProjectMemorySettings> SetAutoCaptureAsync(
        ProjectMemoryScope scope,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        scope = ValidateScope(scope);
        return WriteAsync(async (connection, transaction, token) =>
        {
            var now = DateTimeOffset.UtcNow;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO project_memory_settings(project_key,project_id,auto_capture_enabled,updated_at)
                VALUES($project_key,$project_id,$enabled,$updated_at)
                ON CONFLICT(project_key) DO UPDATE SET
                    project_id=excluded.project_id,
                    auto_capture_enabled=excluded.auto_capture_enabled,
                    updated_at=excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$project_key", scope.ProjectKey);
            command.Parameters.AddWithValue("$project_id", (object?)scope.ProjectId ?? DBNull.Value);
            command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
            command.Parameters.AddWithValue("$updated_at", ToDb(now));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            return new ProjectMemorySettings(scope.ProjectKey, scope.ProjectId, enabled, now);
        }, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _initializationGate.Dispose();
        _writeGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<ProjectMemoryEntry> MutateAsync(
        Guid id,
        long expectedVersion,
        string assignments,
        Action<SqliteCommand> addParameters,
        CancellationToken cancellationToken)
    {
        EnsureExpectedVersion(expectedVersion);
        return await WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                UPDATE project_memory_entries
                SET {assignments},version=version+1,updated_at=$updated_at
                WHERE id=$id AND version=$expected_version;
                """;
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$expected_version", expectedVersion);
            command.Parameters.AddWithValue("$updated_at", ToDb(DateTimeOffset.UtcNow));
            addParameters(command);
            if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
            {
                throw new ProjectMemoryVersionConflictException(id);
            }

            return await ReadEntryAsync(connection, transaction, id, token).ConfigureAwait(false)
                ?? throw new ProjectMemoryVersionConflictException(id);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> WriteAsync<T>(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionCoreAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await action(connection, transaction, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return result;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var connection = await OpenConnectionCoreAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private async Task<SqliteConnection> OpenConnectionCoreAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task ConfigureConnectionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=30000; PRAGMA synchronous=NORMAL;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ProjectMemoryDraft ValidateDraft(ProjectMemoryDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var scope = ValidateScope(draft.Scope);
        ValidateMutableFields(draft.Content, draft.Source);
        return draft with
        {
            Scope = scope,
            Content = draft.Content.Trim(),
            Source = draft.Source.Trim(),
            LastConfirmedAt = draft.LastConfirmedAt?.ToUniversalTime(),
        };
    }

    private static ProjectMemoryScope ValidateScope(ProjectMemoryScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return ProjectMemoryScope.Parse(scope.ProjectKey, scope.ProjectId);
    }

    private static void ValidateMutableFields(string content, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (content.Trim().Length > 100_000)
            throw new ArgumentOutOfRangeException(nameof(content), "Ein Gedächtniseintrag darf höchstens 100.000 Zeichen enthalten.");
        if (source.Trim().Length > 2_000)
            throw new ArgumentOutOfRangeException(nameof(source), "Die Quellenangabe darf höchstens 2.000 Zeichen enthalten.");
    }

    private static void EnsureExpectedVersion(long expectedVersion)
    {
        if (expectedVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedVersion), "Die erwartete Version muss positiv sein.");
    }

    private static ProjectMemoryEntry CreateEntry(ProjectMemoryDraft draft)
    {
        var now = DateTimeOffset.UtcNow;
        return new ProjectMemoryEntry(
            Guid.NewGuid(),
            draft.Scope.ProjectKey,
            draft.Scope.ProjectId,
            draft.Kind,
            draft.Content,
            draft.Source,
            1,
            now,
            now,
            draft.LastConfirmedAt,
            false);
    }

    private static async Task InsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProjectMemoryEntry entry,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO project_memory_entries(
                id,project_key,project_id,kind,content,source,version,created_at,updated_at,last_confirmed_at,is_pinned)
            VALUES($id,$project_key,$project_id,$kind,$content,$source,$version,$created,$updated,$confirmed,$is_pinned);
            """;
        command.Parameters.AddWithValue("$id", entry.Id.ToString("D"));
        command.Parameters.AddWithValue("$project_key", entry.ProjectKey);
        command.Parameters.AddWithValue("$project_id", (object?)entry.ProjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$kind", ToStorage(entry.Kind));
        command.Parameters.AddWithValue("$content", entry.Content);
        command.Parameters.AddWithValue("$source", entry.Source);
        command.Parameters.AddWithValue("$version", entry.Version);
        command.Parameters.AddWithValue("$created", ToDb(entry.CreatedAt));
        command.Parameters.AddWithValue("$updated", ToDb(entry.UpdatedAt));
        command.Parameters.AddWithValue("$confirmed", ToDbValue(entry.LastConfirmedAt));
        command.Parameters.AddWithValue("$is_pinned", entry.IsPinned ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> IsAutoCaptureEnabledAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string projectKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT auto_capture_enabled FROM project_memory_settings WHERE project_key=$project_key;";
        command.Parameters.AddWithValue("$project_key", projectKey);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null || value is DBNull || Convert.ToInt64(value, CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<ProjectMemoryEntry?> ReadEntryAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id,project_key,project_id,kind,content,source,version,created_at,updated_at,last_confirmed_at,is_pinned
            FROM project_memory_entries WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadEntry(reader) : null;
    }

    private static ProjectMemoryEntry ReadEntry(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        FromStorage(reader.GetString(3)),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetInt64(6),
        ReadDate(reader.GetString(7)),
        ReadDate(reader.GetString(8)),
        reader.IsDBNull(9) ? null : ReadDate(reader.GetString(9)),
        reader.GetInt64(10) == 1);

    private static string ToStorage(ProjectMemoryKind kind) => kind switch
    {
        ProjectMemoryKind.Requirement => "requirement",
        ProjectMemoryKind.Decision => "decision",
        ProjectMemoryKind.Milestone => "milestone",
        ProjectMemoryKind.ErrorResolution => "error_resolution",
        ProjectMemoryKind.Verification => "verification",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static ProjectMemoryKind FromStorage(string value) => value switch
    {
        "requirement" => ProjectMemoryKind.Requirement,
        "decision" => ProjectMemoryKind.Decision,
        "milestone" => ProjectMemoryKind.Milestone,
        "error_resolution" => ProjectMemoryKind.ErrorResolution,
        "verification" => ProjectMemoryKind.Verification,
        _ => throw new InvalidDataException($"Unbekannter Gedächtnistyp '{value}'."),
    };

    private static string ToDb(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static object ToDbValue(DateTimeOffset? value) => value.HasValue ? ToDb(value.Value) : DBNull.Value;
    private static DateTimeOffset ReadDate(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
