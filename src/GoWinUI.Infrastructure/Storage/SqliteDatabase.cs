using System.Globalization;
using System.Text;
using System.Text.Json;
using GoWinUI.Core.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace GoWinUI.Infrastructure.Storage;

public sealed class SqliteDatabase : IGoDatabase, IAsyncDisposable
{
    public const int CurrentSchemaVersion = 24;
    private static readonly Action<ILogger, string, Exception?> DatabaseInitialized = LoggerMessage.Define<string>(
        LogLevel.Information, new EventId(1000, nameof(DatabaseInitialized)), "SQLite-Datenbank {DatabasePath} wurde initialisiert.");
    private static readonly Action<ILogger, string?, Exception?> IntegrityCheckFailed = LoggerMessage.Define<string?>(
        LogLevel.Error, new EventId(1001, nameof(IntegrityCheckFailed)), "SQLite-Integritätsprüfung fehlgeschlagen: {Result}");
    private readonly GoInfrastructureOptions _options;
    private readonly ILogger<SqliteDatabase> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _initialized;

    public SqliteDatabase(GoInfrastructureOptions options, ILogger<SqliteDatabase> logger)
    {
        _options = options;
        _logger = logger;
        DatabasePath = Path.Combine(options.DataDirectory, options.DatabaseFileName);
    }

    public string DatabasePath { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _initialized) == 1)
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized == 1)
            {
                return;
            }

            Directory.CreateDirectory(_options.DataDirectory);
            SQLitePCL.Batteries_V2.Init();
            await using var connection = await OpenConnectionCoreAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            await EnableWalModeAsync(connection, cancellationToken).ConfigureAwait(false);
            await ApplyMigrationOneAsync(connection, cancellationToken).ConfigureAwait(false);
            await ApplyMigrationTwoAsync(connection, cancellationToken).ConfigureAwait(false);
            await ApplyMigrationThreeAsync(connection, cancellationToken).ConfigureAwait(false);
            await ApplyMigrationFourAsync(connection, cancellationToken).ConfigureAwait(false);
            await ApplyMigrationFiveAsync(connection, cancellationToken).ConfigureAwait(false);
            await ApplyMigrationSixAsync(connection, cancellationToken).ConfigureAwait(false);
            await ApplyMigrationSevenAsync(connection, cancellationToken).ConfigureAwait(false);
            await ApplyMigrationEightAsync(connection, cancellationToken).ConfigureAwait(false);
            await ApplyMigrationNineAsync(connection, cancellationToken).ConfigureAwait(false);
            await ApplyMigrationTenAsync(connection, cancellationToken).ConfigureAwait(false);
            await ApplyMigrationElevenAsync(connection, cancellationToken).ConfigureAwait(false);
            await ApplyMigrationTwelveAsync(connection, cancellationToken).ConfigureAwait(false);
            await ApplyMigrationThirteenAsync(connection, cancellationToken).ConfigureAwait(false);
            await ApplyMigrationFourteenAsync(connection, cancellationToken).ConfigureAwait(false);
            await ApplyMigrationFifteenAsync(connection, cancellationToken).ConfigureAwait(false);
            await ApplyMigrationSixteenAsync(connection, cancellationToken).ConfigureAwait(false);
            await ApplyMigrationSeventeenAsync(connection, cancellationToken).ConfigureAwait(false);
            await ApplyMigrationEighteenAsync(connection, cancellationToken).ConfigureAwait(false);
            await ApplyMigrationNineteenAsync(connection, cancellationToken).ConfigureAwait(false);
            await ApplyMigrationTwentyAsync(connection, cancellationToken).ConfigureAwait(false);
            await ApplyMigrationTwentyOneAsync(connection, cancellationToken).ConfigureAwait(false);
            await ApplyMigrationTwentyTwoAsync(connection, cancellationToken).ConfigureAwait(false);
            await ApplyMigrationTwentyThreeAsync(connection, cancellationToken).ConfigureAwait(false);
            await ApplyMigrationTwentyFourAsync(connection, cancellationToken).ConfigureAwait(false);
            await VerifyIntegrityAsync(connection, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _initialized, 1);
            DatabaseInitialized(_logger, DatabasePath, null);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    internal async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var connection = await OpenConnectionCoreAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    internal async Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> action, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            _writeLock.Release();
        }
    }

    internal Task WriteAsync(Func<SqliteConnection, SqliteTransaction, CancellationToken, Task> action, CancellationToken cancellationToken = default) =>
        WriteAsync(async (connection, transaction, token) =>
        {
            await action(connection, transaction, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    internal async Task<T> MaintenanceAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await RecoveryMaintenanceAsync(action, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<T> RecoveryMaintenanceAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> CheckIntegrityAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        var result = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            IntegrityCheckFailed(_logger, result, null);
            return false;
        }

        command.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return !await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    internal void MarkUninitialized() => Volatile.Write(ref _initialized, 0);

    public ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<SqliteConnection> OpenConnectionCoreAsync(CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Shared-cache mode can produce SQLITE_LOCKED_TABLE when a WebView
            // upload is read while another operation writes its binary chunks.
            // WAL already gives us concurrent readers without sharing page locks.
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30,
        };
        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task ConfigureConnectionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=30000; PRAGMA synchronous=NORMAL;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnableWalModeAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        // journal_mode changes the database header and must run only during the
        // serialized initialization path, never for every read connection.
        command.CommandText = "PRAGMA journal_mode=WAL;";
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyMigrationOneAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = MigrationOneSql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version=1;";
        var exists = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
        if (!exists)
        {
            await SeedBuiltInWorkflowAsync(command, BuiltInWorkflows.Water, cancellationToken).ConfigureAwait(false);
            await SeedBuiltInWorkflowAsync(command, BuiltInWorkflows.Heating, cancellationToken).ConfigureAwait(false);
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO schema_migrations(version, applied_at) VALUES(1, $now);";
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyMigrationTwoAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version=2;";
        var exists = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
        if (!exists)
        {
            command.CommandText = "ALTER TABLE project_assets ADD COLUMN title TEXT NULL;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            command.CommandText = "INSERT INTO schema_migrations(version, applied_at) VALUES(2, $now);";
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyMigrationThreeAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version=3;";
        var exists = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) != 0;
        if (!exists)
        {
            command.CommandText = MigrationThreeSql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            foreach (var trigger in PromptTriggerSeeds.All)
            {
                command.Parameters.Clear();
                command.CommandText = """
                    INSERT INTO prompt_triggers
                        (id, action, phrase, description, match_mode, is_enabled, priority, revision, created_at, updated_at)
                    VALUES($id, $action, $phrase, $description, 'prefix', 1, $priority, 1, $now, $now);
                    """;
                command.Parameters.AddWithValue("$id", trigger.Id.ToString("D"));
                command.Parameters.AddWithValue("$action", trigger.Action);
                command.Parameters.AddWithValue("$phrase", trigger.Phrase);
                command.Parameters.AddWithValue("$description", trigger.Description);
                command.Parameters.AddWithValue("$priority", trigger.Priority);
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            command.Parameters.Clear();
            command.CommandText = "INSERT INTO schema_migrations(version, applied_at) VALUES(3, $now);";
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyMigrationFourAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version=4;";
        var exists = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
        if (!exists)
        {
            var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            command.CommandText = """
                INSERT OR IGNORE INTO prompt_triggers
                    (id, action, phrase, description, match_mode, is_enabled, priority, revision, created_at, updated_at)
                VALUES('a1000000-0000-4000-8000-000000000016', 'voiceInput', 'Sprachsteuerung',
                       'Nimmt das Mikrofon auf und fügt die Transkription editierbar in das Promptfeld ein.',
                       'prefix', 1, 180, 1, $now, $now);
                INSERT OR IGNORE INTO prompt_triggers
                    (id, action, phrase, description, match_mode, is_enabled, priority, revision, created_at, updated_at)
                VALUES('a1000000-0000-4000-8000-000000000017', 'videoAnalysis', 'Video analysieren',
                       'Analysiert eine angehängte Videodatei oder einen bewusst aufgenommenen Bildschirmclip.',
                       'prefix', 1, 180, 1, $now, $now);
                """;
            command.Parameters.AddWithValue("$now", now);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO schema_migrations(version, applied_at) VALUES(4, $now);";
            command.Parameters.AddWithValue("$now", now);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyMigrationFiveAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version=5;";
        var exists = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) != 0;
        if (!exists)
        {
            command.CommandText = """
                CREATE TABLE client_tool_executions(
                    proposal_id TEXT PRIMARY KEY,
                    local_run_id TEXT NOT NULL REFERENCES go_ai_runs(id) ON DELETE CASCADE,
                    server_run_id TEXT NOT NULL,
                    event_id INTEGER NOT NULL CHECK(event_id>=0),
                    tool_name TEXT NOT NULL,
                    state TEXT NOT NULL CHECK(state IN ('executing','completed','submitted')),
                    result_json TEXT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                ) STRICT;
                CREATE INDEX ix_client_tool_executions_run
                    ON client_tool_executions(local_run_id, event_id);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            command.CommandText = "INSERT INTO schema_migrations(version, applied_at) VALUES(5, $now);";
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyMigrationSixAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version=6;";
        var exists = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) != 0;
        if (!exists)
        {
            command.CommandText = """
                ALTER TABLE chat_sessions
                    ADD COLUMN assistant_mode TEXT NOT NULL DEFAULT 'general'
                    CHECK(assistant_mode IN ('general','code'));
                ALTER TABLE chat_sessions ADD COLUMN workspace_path TEXT NULL;
                ALTER TABLE chat_sessions ADD COLUMN workspace_fingerprint TEXT NULL;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            command.CommandText = "INSERT INTO schema_migrations(version, applied_at) VALUES(6, $now);";
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyMigrationSevenAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version=7;";
        var exists = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
        if (!exists)
        {
            command.CommandText = "ALTER TABLE chat_sessions ADD COLUMN is_pinned INTEGER NOT NULL DEFAULT 0; ALTER TABLE chat_sessions ADD COLUMN pinned_at TEXT NULL; ALTER TABLE chat_messages ADD COLUMN tool_name TEXT NULL; ALTER TABLE chat_messages ADD COLUMN tool_context TEXT NULL; ALTER TABLE chat_messages ADD COLUMN tool_status TEXT NULL; ALTER TABLE chat_messages ADD COLUMN tool_detail TEXT NULL; ALTER TABLE chat_messages ADD COLUMN tool_provider TEXT NULL;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            command.CommandText = "INSERT INTO schema_migrations(version, applied_at) VALUES(7, $now);";
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyMigrationEightAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ApplyMarkerMigrationAsync(connection, 8, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyMigrationNineAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ApplyMarkerMigrationAsync(connection, 9, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyMigrationTenAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ApplyMarkerMigrationAsync(connection, 10, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyMigrationElevenAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version=11;";
        var exists = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
        if (!exists)
        {
            command.CommandText = """
                DELETE FROM prompt_triggers
                WHERE action IN ('video' || 'Generation', 'gif' || 'Generation');
                INSERT INTO schema_migrations(version, applied_at) VALUES(11, $now);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyMigrationTwelveAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version=12;";
        var exists = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
        if (!exists)
        {
            command.CommandText = "ALTER TABLE chat_messages ADD COLUMN context_summary TEXT NULL; INSERT INTO schema_migrations(version, applied_at) VALUES(12, $now);";
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyMigrationThirteenAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var check = connection.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version=13;";
        if (Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0)
            return;

        check.CommandText = "PRAGMA foreign_keys=OFF;";
        await check.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE project_assets_v13(
                    id TEXT PRIMARY KEY,
                    project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                    blob_id TEXT NOT NULL REFERENCES binary_objects(id) ON DELETE RESTRICT,
                    file_name TEXT NOT NULL,
                    content_type TEXT NOT NULL,
                    category TEXT NOT NULL CHECK(category IN ('pdf','drawing','image','meeting','other','cpdb','ifc')),
                    source_path TEXT NULL,
                    sha256 TEXT NOT NULL,
                    length INTEGER NOT NULL,
                    sort_order INTEGER NOT NULL,
                    revision INTEGER NOT NULL CHECK(revision>=1),
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    title TEXT NULL
                ) STRICT;
                INSERT INTO project_assets_v13(id,project_id,blob_id,file_name,content_type,category,source_path,sha256,length,sort_order,revision,created_at,updated_at,title)
                    SELECT id,project_id,blob_id,file_name,content_type,category,source_path,sha256,length,sort_order,revision,created_at,updated_at,title FROM project_assets;
                DROP TABLE project_assets;
                ALTER TABLE project_assets_v13 RENAME TO project_assets;
                CREATE INDEX ix_assets_project_order ON project_assets(project_id, sort_order);
                INSERT INTO schema_migrations(version, applied_at) VALUES(13, $now);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            check.CommandText = "PRAGMA foreign_keys=ON;";
            await check.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task ApplyMigrationFourteenAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version=14;";
        var exists = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
        if (!exists)
        {
            command.CommandText = """
                CREATE TABLE document_index_entries(
                    sha256 TEXT PRIMARY KEY CHECK(length(sha256)=64),
                    schema_version INTEGER NOT NULL,
                    model_profile TEXT NOT NULL,
                    status TEXT NOT NULL CHECK(status IN ('extracting','preparing','ready','failed')),
                    progress INTEGER NOT NULL CHECK(progress BETWEEN 0 AND 100),
                    document_map TEXT NOT NULL DEFAULT '',
                    error TEXT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                ) STRICT;
                CREATE TABLE document_index_pages(
                    sha256 TEXT NOT NULL REFERENCES document_index_entries(sha256) ON DELETE CASCADE,
                    page_number INTEGER NOT NULL CHECK(page_number>=1),
                    text TEXT NOT NULL,
                    PRIMARY KEY(sha256,page_number)
                ) STRICT;
                CREATE TABLE document_index_chunks(
                    id TEXT PRIMARY KEY,
                    sha256 TEXT NOT NULL REFERENCES document_index_entries(sha256) ON DELETE CASCADE,
                    page_number INTEGER NOT NULL CHECK(page_number>=1),
                    chunk_number INTEGER NOT NULL CHECK(chunk_number>=0),
                    text TEXT NOT NULL,
                    normalized_text TEXT NOT NULL,
                    UNIQUE(sha256,page_number,chunk_number)
                ) STRICT;
                CREATE INDEX ix_document_index_chunks_source ON document_index_chunks(sha256,page_number,chunk_number);
                CREATE TABLE document_evidence_snapshots(
                    message_id TEXT NOT NULL REFERENCES chat_messages(id) ON DELETE CASCADE,
                    sha256 TEXT NOT NULL,
                    file_name TEXT NOT NULL,
                    page_number INTEGER NOT NULL,
                    chunk_id TEXT NULL,
                    created_at TEXT NOT NULL,
                    PRIMARY KEY(message_id,sha256,file_name,page_number,chunk_id)
                ) STRICT;

                INSERT OR IGNORE INTO document_index_entries
                    (sha256,schema_version,model_profile,status,progress,document_map,error,created_at,updated_at)
                SELECT sha256,1,'local-hybrid-v1','ready',100,'',NULL,MIN(created_at),MAX(created_at)
                FROM documents GROUP BY sha256;
                INSERT OR IGNORE INTO document_index_pages(sha256,page_number,text)
                SELECT d.sha256,p.page_number,p.text
                FROM documents d JOIN document_pages p ON p.document_id=d.id
                GROUP BY d.sha256,p.page_number;
                INSERT OR IGNORE INTO document_index_chunks(id,sha256,page_number,chunk_number,text,normalized_text)
                SELECT lower(hex(randomblob(16))),d.sha256,p.page_number,0,p.text,lower(p.text)
                FROM documents d JOIN document_pages p ON p.document_id=d.id
                GROUP BY d.sha256,p.page_number;
                INSERT INTO schema_migrations(version,applied_at) VALUES(14,$now);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyMigrationFifteenAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version=15;";
        var exists = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
        if (!exists)
        {
            command.CommandText = """
                CREATE TABLE document_chunk_embeddings(
                    chunk_id TEXT NOT NULL REFERENCES document_index_chunks(id) ON DELETE CASCADE,
                    model_id TEXT NOT NULL,
                    dimensions INTEGER NOT NULL CHECK(dimensions>0),
                    vector BLOB NOT NULL,
                    created_at TEXT NOT NULL,
                    PRIMARY KEY(chunk_id,model_id)
                ) STRICT;
                CREATE INDEX ix_document_chunk_embeddings_model ON document_chunk_embeddings(model_id,chunk_id);

                CREATE TABLE document_context_preparations(
                    cache_key TEXT PRIMARY KEY CHECK(length(cache_key)=64),
                    session_id TEXT NOT NULL REFERENCES chat_sessions(id) ON DELETE CASCADE,
                    corpus_revision TEXT NOT NULL CHECK(length(corpus_revision)=64),
                    prompt_fingerprint TEXT NOT NULL CHECK(length(prompt_fingerprint)=64),
                    model_id TEXT NOT NULL,
                    context_budget INTEGER NOT NULL CHECK(context_budget>=1024),
                    prepared_text TEXT NOT NULL,
                    evidence_json TEXT NOT NULL,
                    created_at TEXT NOT NULL
                ) STRICT;
                CREATE INDEX ix_document_context_preparations_lookup
                    ON document_context_preparations(session_id,corpus_revision,prompt_fingerprint,model_id,context_budget);

                CREATE TABLE document_context_states(
                    document_id TEXT PRIMARY KEY REFERENCES documents(id) ON DELETE CASCADE,
                    session_id TEXT NOT NULL REFERENCES chat_sessions(id) ON DELETE CASCADE,
                    message_id TEXT NOT NULL REFERENCES chat_messages(id) ON DELETE CASCADE,
                    status TEXT NOT NULL CHECK(status IN ('preparing','failed')),
                    progress INTEGER NOT NULL CHECK(progress BETWEEN 0 AND 100),
                    error TEXT NULL,
                    updated_at TEXT NOT NULL
                ) STRICT;
                CREATE INDEX ix_document_context_states_session ON document_context_states(session_id,status);

                INSERT INTO schema_migrations(version,applied_at) VALUES(15,$now);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyMigrationSixteenAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version=16;";
        var exists = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
        if (!exists)
        {
            command.CommandText = """
                CREATE TABLE session_context_preparations(
                    cache_key TEXT PRIMARY KEY CHECK(length(cache_key)=64),
                    session_id TEXT NOT NULL REFERENCES chat_sessions(id) ON DELETE CASCADE,
                    history_revision TEXT NOT NULL CHECK(length(history_revision)=64),
                    model_id TEXT NOT NULL,
                    context_budget INTEGER NOT NULL CHECK(context_budget>=1024),
                    through_message_id TEXT NOT NULL REFERENCES chat_messages(id) ON DELETE CASCADE,
                    message_count INTEGER NOT NULL CHECK(message_count>0),
                    prepared_text TEXT NOT NULL,
                    created_at TEXT NOT NULL
                ) STRICT;
                CREATE INDEX ix_session_context_preparations_lookup
                    ON session_context_preparations(session_id,history_revision,model_id,context_budget);

                INSERT INTO schema_migrations(version,applied_at) VALUES(16,$now);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyMigrationSeventeenAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version=17;";
        var exists = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
        if (!exists)
        {
            command.CommandText = """
                CREATE TABLE speech_preparations(
                    cache_key TEXT PRIMARY KEY CHECK(length(cache_key)=64),
                    session_id TEXT NOT NULL REFERENCES chat_sessions(id) ON DELETE CASCADE,
                    source_message_id TEXT NULL REFERENCES chat_messages(id) ON DELETE CASCADE,
                    source_kind TEXT NOT NULL,
                    source_hash TEXT NOT NULL CHECK(length(source_hash)=64),
                    model_id TEXT NOT NULL,
                    prepared_text TEXT NOT NULL,
                    created_at TEXT NOT NULL
                ) STRICT;
                CREATE INDEX ix_speech_preparations_session_source
                    ON speech_preparations(session_id,source_message_id,source_hash,model_id);

                INSERT INTO schema_migrations(version,applied_at) VALUES(17,$now);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyMigrationEighteenAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version=18;";
        var exists = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
        if (!exists)
        {
            command.CommandText = """
                ALTER TABLE chat_sessions
                    ADD COLUMN persistent_tool_action TEXT NULL
                    CHECK(persistent_tool_action IS NULL OR persistent_tool_action IN ('code','bricscad','audiobook'));
                UPDATE chat_sessions
                SET persistent_tool_action='code'
                WHERE assistant_mode='code';

                ALTER TABLE chat_messages
                    ADD COLUMN content_profile TEXT NOT NULL DEFAULT 'general'
                    CHECK(content_profile IN ('general','audiobook'));

                ALTER TABLE session_context_preparations
                    ADD COLUMN profile TEXT NOT NULL DEFAULT 'general'
                    CHECK(profile IN ('general','code','audiobook'));

                INSERT OR IGNORE INTO prompt_triggers
                    (id,action,phrase,description,match_mode,is_enabled,priority,revision,created_at,updated_at)
                VALUES
                    ('a1000000-0000-4000-8000-000000000019','audiobook','Hörbuch erstellen',
                     'Erstellt oder lenkt ein fortlaufendes, direkt vorlesbares Hörbuchkapitel.',
                     'prefix',1,190,1,$now,$now),
                    ('a1000000-0000-4000-8000-000000000020','audiobook','Hörbuch fortsetzen',
                     'Setzt das Hörbuch dieser Sitzung mit der vorhandenen Story-Chronik fort.',
                     'prefix',1,200,1,$now,$now);

                INSERT INTO schema_migrations(version,applied_at) VALUES(18,$now);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyMigrationNineteenAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version=19;";
        var exists = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
        if (!exists)
        {
            command.CommandText = """
                ALTER TABLE speech_preparations
                    ADD COLUMN source_units_json TEXT NOT NULL DEFAULT '[]';
                ALTER TABLE speech_preparations
                    ADD COLUMN segments_json TEXT NOT NULL DEFAULT '[]';

                INSERT INTO schema_migrations(version,applied_at) VALUES(19,$now);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyMigrationTwentyAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version=20;";
        var exists = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
        if (!exists)
        {
            command.CommandText = """
                DROP TABLE IF EXISTS speech_preparations;
                INSERT INTO schema_migrations(version,applied_at) VALUES(20,$now);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyMigrationTwentyOneAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version=21;";
        var exists = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
        if (!exists)
        {
            command.CommandText = """
                ALTER TABLE chat_messages ADD COLUMN code_diff TEXT NULL;
                INSERT INTO schema_migrations(version,applied_at) VALUES(21,$now);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyMigrationTwentyTwoAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version=22;";
        var exists = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
        if (!exists)
        {
            command.CommandText = """
                CREATE TABLE coding_campaigns(
                    id TEXT PRIMARY KEY,
                    session_id TEXT NOT NULL UNIQUE REFERENCES chat_sessions(id) ON DELETE CASCADE,
                    definition_id TEXT NOT NULL,
                    title TEXT NOT NULL,
                    workspace_path TEXT NOT NULL,
                    workspace_fingerprint TEXT NOT NULL,
                    model_id TEXT NOT NULL,
                    status TEXT NOT NULL CHECK(status IN ('running','faulted','stopped')),
                    phase TEXT NOT NULL CHECK(phase IN ('bootstrap','iteration','correction','validation')),
                    iteration INTEGER NOT NULL DEFAULT 0 CHECK(iteration >= 0),
                    current_challenge TEXT NULL,
                    last_error TEXT NULL,
                    validation_json TEXT NOT NULL DEFAULT '[]',
                    restart_count INTEGER NOT NULL DEFAULT 0 CHECK(restart_count >= 0),
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE coding_campaign_iterations(
                    id TEXT PRIMARY KEY,
                    campaign_id TEXT NOT NULL REFERENCES coding_campaigns(id) ON DELETE CASCADE,
                    iteration INTEGER NOT NULL CHECK(iteration >= 0),
                    phase TEXT NOT NULL CHECK(phase IN ('bootstrap','iteration','correction','validation')),
                    challenge TEXT NOT NULL,
                    assistant_message_id TEXT NULL REFERENCES chat_messages(id) ON DELETE SET NULL,
                    status TEXT NOT NULL,
                    error TEXT NULL,
                    validation_json TEXT NOT NULL DEFAULT '[]',
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE INDEX idx_coding_campaign_iterations_campaign
                    ON coding_campaign_iterations(campaign_id, iteration, created_at);

                INSERT INTO schema_migrations(version,applied_at) VALUES(22,$now);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyMigrationTwentyThreeAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version=23;";
        var exists = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
        if (!exists)
        {
            command.CommandText = """
                CREATE TABLE coding_campaign_solution_messages(
                    campaign_id TEXT NOT NULL REFERENCES coding_campaigns(id) ON DELETE CASCADE,
                    relative_path TEXT NOT NULL,
                    content_sha256 TEXT NOT NULL CHECK(length(content_sha256)=64),
                    message_id TEXT NOT NULL REFERENCES chat_messages(id) ON DELETE CASCADE,
                    published_at TEXT NOT NULL,
                    PRIMARY KEY(campaign_id,relative_path,content_sha256)
                ) STRICT;
                CREATE INDEX idx_coding_campaign_solution_messages_message
                    ON coding_campaign_solution_messages(message_id);
                INSERT INTO schema_migrations(version,applied_at) VALUES(23,$now);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyMigrationTwentyFourAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version=24;";
        var exists = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
        if (!exists)
        {
            command.CommandText = """
                ALTER TABLE chat_messages
                    ADD COLUMN visibility TEXT NOT NULL DEFAULT 'visible'
                    CHECK(visibility IN ('visible','internal'));
                CREATE INDEX idx_chat_messages_visible_session
                    ON chat_messages(session_id,visibility,created_at);
                INSERT INTO schema_migrations(version,applied_at) VALUES(24,$now);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyMarkerMigrationAsync(SqliteConnection connection, int version, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO schema_migrations(version, applied_at) VALUES($version, $now);";
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyIntegrityAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        var result = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"SQLite integrity_check failed after migration: {result}");
        }

        command.CommandText = "PRAGMA foreign_key_check;";
        await using var violations = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await violations.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("SQLite foreign_key_check failed after migration.");
        }
    }

    private static async Task SeedBuiltInWorkflowAsync(SqliteCommand command, BuiltInWorkflow workflow, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        command.Parameters.Clear();
        command.CommandText = """
            INSERT OR IGNORE INTO workflows
                (id, slug, title, description, domain, context_summary, content_json, is_built_in, revision, created_at, updated_at)
            VALUES ($id, $slug, $title, $description, $domain, $summary, $json, 1, 1, $now, $now);
            """;
        command.Parameters.AddWithValue("$id", workflow.Id.ToString("D"));
        command.Parameters.AddWithValue("$slug", workflow.Slug);
        command.Parameters.AddWithValue("$title", workflow.Title);
        command.Parameters.AddWithValue("$description", workflow.Description);
        command.Parameters.AddWithValue("$domain", workflow.Domain);
        command.Parameters.AddWithValue("$summary", workflow.ContextSummary);
        command.Parameters.AddWithValue("$json", workflow.ContentJson);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        foreach (var tag in workflow.Tags)
        {
            command.Parameters.Clear();
            command.CommandText = "INSERT OR IGNORE INTO workflow_tags(workflow_id, tag) VALUES($id, $tag);";
            command.Parameters.AddWithValue("$id", workflow.Id.ToString("D"));
            command.Parameters.AddWithValue("$tag", tag);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private const string MigrationOneSql = """
        CREATE TABLE IF NOT EXISTS schema_migrations(
            version INTEGER PRIMARY KEY,
            applied_at TEXT NOT NULL
        ) STRICT;

        CREATE TABLE IF NOT EXISTS binary_objects(
            id TEXT PRIMARY KEY,
            sha256 TEXT NOT NULL UNIQUE CHECK(length(sha256)=64),
            length INTEGER NOT NULL CHECK(length>=0),
            content_type TEXT NOT NULL,
            chunk_count INTEGER NOT NULL CHECK(chunk_count>=0),
            created_at TEXT NOT NULL
        ) STRICT;
        CREATE TABLE IF NOT EXISTS binary_chunks(
            object_id TEXT NOT NULL REFERENCES binary_objects(id) ON DELETE CASCADE,
            chunk_index INTEGER NOT NULL CHECK(chunk_index>=0),
            data BLOB NOT NULL,
            PRIMARY KEY(object_id, chunk_index)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS workflows(
            id TEXT PRIMARY KEY,
            slug TEXT NOT NULL UNIQUE,
            title TEXT NOT NULL,
            description TEXT NOT NULL,
            domain TEXT NOT NULL,
            context_summary TEXT NOT NULL,
            content_json TEXT NOT NULL CHECK(json_valid(content_json)),
            is_built_in INTEGER NOT NULL CHECK(is_built_in IN (0,1)),
            revision INTEGER NOT NULL CHECK(revision>=1),
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        ) STRICT;
        CREATE TABLE IF NOT EXISTS workflow_tags(
            workflow_id TEXT NOT NULL REFERENCES workflows(id) ON DELETE CASCADE,
            tag TEXT NOT NULL,
            PRIMARY KEY(workflow_id, tag)
        ) STRICT;
        CREATE INDEX IF NOT EXISTS ix_workflows_title ON workflows(title COLLATE NOCASE);
        CREATE VIRTUAL TABLE IF NOT EXISTS workflow_search USING fts5(title,description,domain,context_summary,content='workflows',content_rowid='rowid');
        CREATE TRIGGER IF NOT EXISTS workflows_search_insert AFTER INSERT ON workflows BEGIN
            INSERT INTO workflow_search(rowid,title,description,domain,context_summary)
            VALUES(new.rowid,new.title,new.description,new.domain,new.context_summary);
        END;
        CREATE TRIGGER IF NOT EXISTS workflows_search_delete AFTER DELETE ON workflows BEGIN
            INSERT INTO workflow_search(workflow_search,rowid,title,description,domain,context_summary)
            VALUES('delete',old.rowid,old.title,old.description,old.domain,old.context_summary);
        END;
        CREATE TRIGGER IF NOT EXISTS workflows_search_update AFTER UPDATE ON workflows BEGIN
            INSERT INTO workflow_search(workflow_search,rowid,title,description,domain,context_summary)
            VALUES('delete',old.rowid,old.title,old.description,old.domain,old.context_summary);
            INSERT INTO workflow_search(rowid,title,description,domain,context_summary)
            VALUES(new.rowid,new.title,new.description,new.domain,new.context_summary);
        END;

        CREATE TABLE IF NOT EXISTS chat_sessions(
            id TEXT PRIMARY KEY,
            title TEXT NOT NULL,
            selected_workflow_id TEXT NULL REFERENCES workflows(id) ON DELETE SET NULL,
            draft TEXT NOT NULL DEFAULT '',
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        ) STRICT;
        CREATE INDEX IF NOT EXISTS ix_chat_sessions_updated ON chat_sessions(updated_at DESC);
        CREATE VIRTUAL TABLE IF NOT EXISTS session_search USING fts5(title,draft,content='chat_sessions',content_rowid='rowid');
        CREATE TRIGGER IF NOT EXISTS sessions_search_insert AFTER INSERT ON chat_sessions BEGIN
            INSERT INTO session_search(rowid,title,draft) VALUES(new.rowid,new.title,new.draft);
        END;
        CREATE TRIGGER IF NOT EXISTS sessions_search_delete AFTER DELETE ON chat_sessions BEGIN
            INSERT INTO session_search(session_search,rowid,title,draft) VALUES('delete',old.rowid,old.title,old.draft);
        END;
        CREATE TRIGGER IF NOT EXISTS sessions_search_update AFTER UPDATE ON chat_sessions BEGIN
            INSERT INTO session_search(session_search,rowid,title,draft) VALUES('delete',old.rowid,old.title,old.draft);
            INSERT INTO session_search(rowid,title,draft) VALUES(new.rowid,new.title,new.draft);
        END;
        CREATE TABLE IF NOT EXISTS chat_messages(
            id TEXT PRIMARY KEY,
            session_id TEXT NOT NULL REFERENCES chat_sessions(id) ON DELETE CASCADE,
            role TEXT NOT NULL CHECK(role IN ('system','user','assistant')),
            content TEXT NOT NULL,
            status TEXT NOT NULL CHECK(status IN ('pending','streaming','completed','cancelled','failed','interrupted')),
            error TEXT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        ) STRICT;
        CREATE INDEX IF NOT EXISTS ix_chat_messages_session ON chat_messages(session_id, created_at);
        CREATE TABLE IF NOT EXISTS chat_runs(
            id TEXT PRIMARY KEY,
            session_id TEXT NOT NULL REFERENCES chat_sessions(id) ON DELETE CASCADE,
            assistant_message_id TEXT NOT NULL UNIQUE REFERENCES chat_messages(id) ON DELETE CASCADE,
            model TEXT NOT NULL,
            api_endpoint TEXT NOT NULL,
            reasoning_effort TEXT NULL,
            status TEXT NOT NULL CHECK(status IN ('streaming','completed','cancelled','failed','interrupted')),
            estimated_context_tokens INTEGER NOT NULL DEFAULT 0,
            context_was_truncated INTEGER NOT NULL DEFAULT 0 CHECK(context_was_truncated IN (0,1)),
            started_at TEXT NOT NULL,
            completed_at TEXT NULL,
            error TEXT NULL
        ) STRICT;
        CREATE INDEX IF NOT EXISTS ix_chat_runs_session ON chat_runs(session_id, started_at DESC);

        CREATE TABLE IF NOT EXISTS documents(
            id TEXT PRIMARY KEY,
            session_id TEXT NOT NULL REFERENCES chat_sessions(id) ON DELETE CASCADE,
            blob_id TEXT NOT NULL REFERENCES binary_objects(id) ON DELETE RESTRICT,
            file_name TEXT NOT NULL,
            content_type TEXT NOT NULL,
            sha256 TEXT NOT NULL,
            length INTEGER NOT NULL,
            page_count INTEGER NOT NULL,
            created_at TEXT NOT NULL
        ) STRICT;
        CREATE INDEX IF NOT EXISTS ix_documents_session ON documents(session_id, created_at);
        CREATE TABLE IF NOT EXISTS document_pages(
            document_id TEXT NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
            page_number INTEGER NOT NULL CHECK(page_number>=1),
            text TEXT NOT NULL,
            PRIMARY KEY(document_id, page_number)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS projects(
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            construction_project TEXT NOT NULL,
            description TEXT NOT NULL,
            notes TEXT NOT NULL,
            status TEXT NOT NULL CHECK(status IN ('active','archived')),
            revision INTEGER NOT NULL CHECK(revision>=1),
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        ) STRICT;
        CREATE INDEX IF NOT EXISTS ix_projects_status_updated ON projects(status, updated_at DESC);
        CREATE TABLE IF NOT EXISTS checklist_items(
            id TEXT PRIMARY KEY,
            project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            text TEXT NOT NULL,
            is_completed INTEGER NOT NULL CHECK(is_completed IN (0,1)),
            sort_order INTEGER NOT NULL,
            revision INTEGER NOT NULL CHECK(revision>=1),
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        ) STRICT;
        CREATE INDEX IF NOT EXISTS ix_checklist_project_order ON checklist_items(project_id, sort_order);
        CREATE TABLE IF NOT EXISTS project_assets(
            id TEXT PRIMARY KEY,
            project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            blob_id TEXT NOT NULL REFERENCES binary_objects(id) ON DELETE RESTRICT,
            file_name TEXT NOT NULL,
            content_type TEXT NOT NULL,
            category TEXT NOT NULL CHECK(category IN ('pdf','drawing','image','meeting','other')),
            source_path TEXT NULL,
            sha256 TEXT NOT NULL,
            length INTEGER NOT NULL,
            sort_order INTEGER NOT NULL,
            revision INTEGER NOT NULL CHECK(revision>=1),
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        ) STRICT;
        CREATE INDEX IF NOT EXISTS ix_assets_project_order ON project_assets(project_id, sort_order);
        CREATE TABLE IF NOT EXISTS project_asset_thumbnails(
            asset_id TEXT PRIMARY KEY REFERENCES project_assets(id) ON DELETE CASCADE,
            blob_id TEXT NOT NULL REFERENCES binary_objects(id) ON DELETE RESTRICT,
            content_type TEXT NOT NULL,
            width INTEGER NOT NULL CHECK(width>0),
            height INTEGER NOT NULL CHECK(height>0),
            created_at TEXT NOT NULL
        ) STRICT;
        """;

    private const string MigrationThreeSql = """
        CREATE TABLE prompt_triggers(
            id TEXT PRIMARY KEY,
            action TEXT NOT NULL,
            phrase TEXT NOT NULL,
            description TEXT NOT NULL,
            match_mode TEXT NOT NULL CHECK(match_mode IN ('prefix','contains','exact')),
            is_enabled INTEGER NOT NULL CHECK(is_enabled IN (0,1)),
            priority INTEGER NOT NULL CHECK(priority BETWEEN -10000 AND 10000),
            revision INTEGER NOT NULL CHECK(revision>=1),
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            UNIQUE(action, phrase COLLATE NOCASE)
        ) STRICT;
        CREATE INDEX ix_prompt_triggers_match
            ON prompt_triggers(is_enabled, priority DESC, phrase COLLATE NOCASE);

        CREATE TABLE assistant_attachments(
            id TEXT PRIMARY KEY,
            session_id TEXT NOT NULL REFERENCES chat_sessions(id) ON DELETE CASCADE,
            blob_id TEXT NOT NULL REFERENCES binary_objects(id) ON DELETE RESTRICT,
            file_name TEXT NOT NULL,
            content_type TEXT NOT NULL,
            sha256 TEXT NOT NULL CHECK(length(sha256)=64),
            length INTEGER NOT NULL CHECK(length>=0),
            created_at TEXT NOT NULL
        ) STRICT;
        CREATE INDEX ix_assistant_attachments_session
            ON assistant_attachments(session_id, created_at);

        CREATE TABLE chat_artifacts(
            id TEXT PRIMARY KEY,
            message_id TEXT NOT NULL REFERENCES chat_messages(id) ON DELETE CASCADE,
            blob_id TEXT NOT NULL REFERENCES binary_objects(id) ON DELETE RESTRICT,
            server_artifact_id TEXT NOT NULL,
            file_name TEXT NOT NULL,
            content_type TEXT NOT NULL,
            sha256 TEXT NOT NULL CHECK(length(sha256)=64),
            length INTEGER NOT NULL CHECK(length>=0),
            provider TEXT NOT NULL,
            metadata_json TEXT NOT NULL DEFAULT '{}' CHECK(json_valid(metadata_json)),
            created_at TEXT NOT NULL,
            UNIQUE(message_id, server_artifact_id)
        ) STRICT;
        CREATE INDEX ix_chat_artifacts_message ON chat_artifacts(message_id, created_at);

        CREATE TABLE go_ai_runs(
            id TEXT PRIMARY KEY,
            session_id TEXT NOT NULL REFERENCES chat_sessions(id) ON DELETE CASCADE,
            assistant_message_id TEXT NOT NULL UNIQUE REFERENCES chat_messages(id) ON DELETE CASCADE,
            action TEXT NULL,
            idempotency_key TEXT NOT NULL UNIQUE,
            server_run_id TEXT NULL UNIQUE,
            last_event_id INTEGER NOT NULL DEFAULT 0 CHECK(last_event_id>=0),
            state TEXT NOT NULL,
            selected_model TEXT NULL,
            error_code TEXT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        ) STRICT;
        CREATE INDEX ix_go_ai_runs_resumable ON go_ai_runs(state, updated_at);
        """;
}

internal sealed record PromptTriggerSeed(
    Guid Id,
    string Action,
    string Phrase,
    string Description,
    int Priority);

internal static class PromptTriggerSeeds
{
    internal static readonly PromptTriggerSeed[] All =
    [
        new(Guid.Parse("a1000000-0000-4000-8000-000000000001"), "imageGeneration", "Erstelle ein Bild", "Erzeugt ein Bild mit dem GO AI Image Worker.", 200),
        new(Guid.Parse("a1000000-0000-4000-8000-000000000002"), "imageGeneration", "Generiere ein Bild", "Erzeugt ein Bild mit dem GO AI Image Worker.", 190),
        new(Guid.Parse("a1000000-0000-4000-8000-000000000003"), "translation", "Übersetze", "Übersetzt den folgenden Inhalt mit dem allgemeinen Modell.", 180),
        new(Guid.Parse("a1000000-0000-4000-8000-000000000004"), "textToSpeech", "Vorlesen", "Erzeugt eine Sprachausgabe des folgenden Textes.", 180),
        new(Guid.Parse("a1000000-0000-4000-8000-000000000005"), "textToSpeech", "Lies vor", "Erzeugt eine Sprachausgabe des folgenden Textes.", 170),
        new(Guid.Parse("a1000000-0000-4000-8000-000000000018"), "textToSpeech", "Lies die letzte Nachricht vor", "Liest die letzte geeignete abgeschlossene AI-Antwort vor.", 190),
        new(Guid.Parse("a1000000-0000-4000-8000-000000000006"), "transcription", "Transkribiere", "Wandelt die angehängte Audiodatei in Text um.", 180),
        new(Guid.Parse("a1000000-0000-4000-8000-000000000007"), "audioAnalysis", "Audio analysieren", "Analysiert eine angehängte Audioaufnahme.", 180),
        new(Guid.Parse("a1000000-0000-4000-8000-000000000008"), "imageAnalysis", "Bild analysieren", "Analysiert ein angehängtes Bild mit dem Vision-Modell.", 180),
        new(Guid.Parse("a1000000-0000-4000-8000-000000000009"), "webSearch", "Führe Websuche durch", "Durchsucht das Web über den GO AI Server.", 180),
        new(Guid.Parse("a1000000-0000-4000-8000-000000000010"), "webSearch", "Suche im Web", "Durchsucht das Web über den GO AI Server.", 170),
        new(Guid.Parse("a1000000-0000-4000-8000-000000000011"), "youTubeSearch", "Suche auf YouTube", "Durchsucht YouTube über den GO AI Server.", 170),
        new(Guid.Parse("a1000000-0000-4000-8000-000000000012"), "bricsCad", "In BricsCAD", "Aktiviert die typisierten BricsCAD-Werkzeuge für diesen Lauf.", 180),
        new(Guid.Parse("a1000000-0000-4000-8000-000000000013"), "code", "Code analysieren", "Routet die Aufgabe exklusiv an das ausgewählte Coding-Modell.", 170),
        new(Guid.Parse("a1000000-0000-4000-8000-000000000014"), "liveCaptions", "Untertitel", "Startet Live-Untertitel für das Windows-Systemaudio.", 180),
        new(Guid.Parse("a1000000-0000-4000-8000-000000000015"), "liveTranslation", "Live übersetzen", "Startet die Echtzeitübersetzung des Windows-Systemaudios.", 180),
        new(Guid.Parse("a1000000-0000-4000-8000-000000000019"), "audiobook", "Hörbuch erstellen", "Erstellt oder lenkt ein fortlaufendes, direkt vorlesbares Hörbuchkapitel.", 190),
        new(Guid.Parse("a1000000-0000-4000-8000-000000000020"), "audiobook", "Hörbuch fortsetzen", "Setzt das Hörbuch dieser Sitzung mit der vorhandenen Story-Chronik fort.", 200),
    ];
}

internal sealed record BuiltInWorkflow(
    Guid Id,
    string Slug,
    string Title,
    string Description,
    string Domain,
    string ContextSummary,
    string ContentJson,
    string[] Tags);

internal static class BuiltInWorkflows
{
    internal static readonly BuiltInWorkflow Water = Read(
        Guid.Parse("0e2fd00a-aa2e-5b23-9e06-3a0645a2ecad"),
        "bemessung_der_trinkwasserinstallation_nach_din_1988_300.json");

    internal static readonly BuiltInWorkflow Heating = Read(
        Guid.Parse("7ceee4f5-8c41-5ce3-9332-7ccbb7d8d3fb"),
        "heizlastberechnung_nach_din_en_12831.json");

    private static BuiltInWorkflow Read(Guid id, string fileName)
    {
        var resourceName = $"GoWinUI.Infrastructure.Resources.Workflows.{fileName}";
        using var stream = typeof(BuiltInWorkflows).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Eingebetteter Workflow '{resourceName}' fehlt.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var json = reader.ReadToEnd();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var tags = root.TryGetProperty("tags", out var tagArray) && tagArray.ValueKind == JsonValueKind.Array
            ? tagArray.EnumerateArray().Select(static tag => tag.GetString()).OfType<string>().ToArray()
            : [];
        return new(
            id,
            root.GetProperty("id").GetString() ?? throw new InvalidDataException("Workflow-ID fehlt."),
            root.GetProperty("title").GetString() ?? throw new InvalidDataException("Workflow-Titel fehlt."),
            root.GetProperty("description").GetString() ?? string.Empty,
            root.GetProperty("domain").GetString() ?? string.Empty,
            root.GetProperty("contextSummary").GetString() ?? string.Empty,
            json,
            tags);
    }
}
