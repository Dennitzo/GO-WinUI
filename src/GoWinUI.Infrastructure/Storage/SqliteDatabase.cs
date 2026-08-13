using System.Globalization;
using System.Text;
using System.Text.Json;
using GoWinUI.Core.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace GoWinUI.Infrastructure.Storage;

public sealed class SqliteDatabase : IGoDatabase, IAsyncDisposable
{
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
            await ApplyMigrationOneAsync(connection, cancellationToken).ConfigureAwait(false);
            await ApplyMigrationTwoAsync(connection, cancellationToken).ConfigureAwait(false);
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
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 5,
        };
        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task ConfigureConnectionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL; PRAGMA journal_mode=WAL;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
