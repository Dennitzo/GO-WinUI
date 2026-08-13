using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.Json;

namespace GoAi.Server.Core.Data;

public sealed class GoAiDatabase : IDisposable
{
    private readonly GoAiServerOptions _options;
    private readonly JsonSerializerOptions _jsonOptions = GoAiProtocol.CreateJsonOptions();
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private volatile bool _initialized;

    public GoAiDatabase(IOptions<GoAiServerOptions> options)
    {
        _options = options.Value;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_options.DatabasePath)!);
            Directory.CreateDirectory(_options.UploadDirectory);
            Directory.CreateDirectory(_options.ArtifactDirectory);
            Directory.CreateDirectory(_options.WorkerArtifactDirectory);
            Directory.CreateDirectory(_options.SecretDirectory);
            Directory.CreateDirectory(_options.LogDirectory);

            await using var connection = await OpenConnectionCoreAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = Schema;
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await ValidateDatabaseAsync(connection, cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await OpenConnectionCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    public JsonSerializerOptions JsonOptions => _jsonOptions;

    public static string FormatTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    public void Dispose()
    {
        _initializeGate.Dispose();
    }

    private async Task<SqliteConnection> OpenConnectionCoreAsync(CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        };
        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task ValidateDatabaseAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            var result = Convert.ToString(
                await integrity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"GO AI SQLite integrity_check failed: {result}");
            }
        }

        await using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await foreignKeys.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("GO AI SQLite foreign_key_check failed.");
        }
    }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS schema_migrations (
            version INTEGER PRIMARY KEY,
            applied_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS api_keys (
            key_id TEXT PRIMARY KEY,
            key_hash BLOB NOT NULL,
            name TEXT NOT NULL,
            created_at TEXT NOT NULL,
            revoked_at TEXT NULL,
            last_used_at TEXT NULL
        );
        CREATE TABLE IF NOT EXISTS runs (
            run_id TEXT PRIMARY KEY,
            idempotency_key TEXT NULL UNIQUE,
            state TEXT NOT NULL,
            mode TEXT NOT NULL,
            selected_model TEXT NULL,
            session_title TEXT NULL,
            request_json TEXT NOT NULL,
            error_code TEXT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS run_events (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            run_id TEXT NOT NULL,
            event_type TEXT NOT NULL,
            data_json TEXT NOT NULL,
            created_at TEXT NOT NULL,
            FOREIGN KEY(run_id) REFERENCES runs(run_id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_run_events_run_id_id ON run_events(run_id, id);
        CREATE TABLE IF NOT EXISTS client_tool_results (
            proposal_id TEXT PRIMARY KEY,
            run_id TEXT NOT NULL,
            result_json TEXT NOT NULL,
            created_at TEXT NOT NULL,
            FOREIGN KEY(run_id) REFERENCES runs(run_id) ON DELETE CASCADE
        );
        CREATE TABLE IF NOT EXISTS client_tool_proposals (
            proposal_id TEXT PRIMARY KEY,
            run_id TEXT NOT NULL,
            name TEXT NOT NULL,
            proposal_json TEXT NOT NULL,
            expires_at TEXT NOT NULL,
            created_at TEXT NOT NULL,
            FOREIGN KEY(run_id) REFERENCES runs(run_id) ON DELETE CASCADE
        );
        CREATE TABLE IF NOT EXISTS run_checkpoints (
            run_id TEXT PRIMARY KEY,
            checkpoint_json TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            FOREIGN KEY(run_id) REFERENCES runs(run_id) ON DELETE CASCADE
        );
        CREATE TABLE IF NOT EXISTS uploads (
            upload_id TEXT PRIMARY KEY,
            file_name TEXT NOT NULL,
            media_type TEXT NOT NULL,
            total_length INTEGER NOT NULL,
            total_sha256 TEXT NOT NULL,
            chunk_size INTEGER NOT NULL,
            chunk_count INTEGER NOT NULL,
            state TEXT NOT NULL,
            created_at TEXT NOT NULL,
            expires_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS upload_chunks (
            upload_id TEXT NOT NULL,
            chunk_index INTEGER NOT NULL,
            chunk_length INTEGER NOT NULL,
            chunk_sha256 TEXT NOT NULL,
            file_path TEXT NOT NULL,
            created_at TEXT NOT NULL,
            PRIMARY KEY(upload_id, chunk_index),
            FOREIGN KEY(upload_id) REFERENCES uploads(upload_id) ON DELETE CASCADE
        );
        CREATE TABLE IF NOT EXISTS artifacts (
            artifact_id TEXT PRIMARY KEY,
            file_name TEXT NOT NULL,
            media_type TEXT NOT NULL,
            total_length INTEGER NOT NULL,
            sha256 TEXT NOT NULL,
            file_path TEXT NOT NULL,
            metadata_json TEXT NULL,
            created_at TEXT NOT NULL,
            expires_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS gpu_leases (
            lease_id TEXT PRIMARY KEY,
            run_id TEXT NULL,
            workload TEXT NOT NULL,
            state TEXT NOT NULL,
            created_at TEXT NOT NULL,
            acquired_at TEXT NULL,
            released_at TEXT NULL
        );
        INSERT OR IGNORE INTO schema_migrations(version, applied_at)
        VALUES(1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
        PRAGMA user_version = 1;
        """;
}
