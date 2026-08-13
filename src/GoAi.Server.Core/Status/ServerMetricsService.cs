using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Data;
using Microsoft.Extensions.Options;

namespace GoAi.Server.Core.Status;

public sealed class ServerMetricsService
{
    private readonly GoAiDatabase _database;
    private readonly GoAiServerOptions _options;

    public ServerMetricsService(GoAiDatabase database, IOptions<GoAiServerOptions> options)
    {
        _database = database;
        _options = options.Value;
    }

    public async Task<ServerMetricsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM runs),
                (SELECT COUNT(*) FROM runs WHERE state IN ($queued, $running, $waiting)),
                (SELECT COUNT(*) FROM runs WHERE state = $completed),
                (SELECT COUNT(*) FROM runs WHERE state IN ($failed, $interrupted)),
                (SELECT COUNT(*) FROM artifacts),
                (SELECT COALESCE(SUM(total_length), 0) FROM artifacts),
                (SELECT COUNT(*) FROM uploads WHERE state = 'Complete'),
                (SELECT COALESCE(SUM(total_length), 0) FROM uploads WHERE state = 'Complete'),
                (SELECT COUNT(*) FROM api_keys WHERE revoked_at IS NULL);
            """;
        command.Parameters.AddWithValue("$queued", RunState.Queued.ToString());
        command.Parameters.AddWithValue("$running", RunState.Running.ToString());
        command.Parameters.AddWithValue("$waiting", RunState.WaitingForClient.ToString());
        command.Parameters.AddWithValue("$completed", RunState.Completed.ToString());
        command.Parameters.AddWithValue("$failed", RunState.Failed.ToString());
        command.Parameters.AddWithValue("$interrupted", RunState.Interrupted.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        var dataRoot = Path.GetPathRoot(_options.DataDirectory);
        var drive = string.IsNullOrWhiteSpace(dataRoot) ? null : new DriveInfo(dataRoot);
        var databaseBytes = GetDatabaseBytes(_options.DatabasePath);
        return new ServerMetricsSnapshot(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            databaseBytes,
            drive?.AvailableFreeSpace ?? 0,
            _options.DataDirectory);
    }

    private static long GetDatabaseBytes(string databasePath)
    {
        long total = 0;
        foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                total += new FileInfo(path).Length;
            }
        }

        return total;
    }
}

public sealed record ServerMetricsSnapshot(
    long TotalRuns,
    long ActiveRuns,
    long CompletedRuns,
    long FailedRuns,
    long ArtifactCount,
    long ArtifactBytes,
    long UploadCount,
    long UploadBytes,
    long ActiveApiKeys,
    long DatabaseBytes,
    long DiskFreeBytes,
    string DataDirectory);
