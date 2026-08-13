using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace GoAi.Server.Core.Storage;

public sealed class StorageCleanupService : BackgroundService
{
    private readonly GoAiDatabase _database;
    private readonly GoAiServerOptions _options;

    public StorageCleanupService(GoAiDatabase database, IOptions<GoAiServerOptions> options)
    {
        _database = database;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
        do
        {
            await CleanupExpiredAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    public async Task CleanupExpiredAsync(CancellationToken cancellationToken = default)
    {
        var current = DateTimeOffset.UtcNow;
        var now = GoAiDatabase.FormatTimestamp(current);
        var historyCutoff = GoAiDatabase.FormatTimestamp(current.AddHours(-24));
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var paths = new List<string>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT upload_id FROM uploads WHERE expires_at <= $now UNION ALL SELECT artifact_id FROM artifacts WHERE expires_at <= $now;";
            select.Parameters.AddWithValue("$now", now);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                paths.Add(reader.GetString(0));
            }
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.CommandText = """
                DELETE FROM uploads WHERE expires_at <= $now;
                DELETE FROM artifacts WHERE expires_at <= $now;
                DELETE FROM client_tool_proposals WHERE expires_at <= $now;
                DELETE FROM runs
                WHERE updated_at <= $historyCutoff
                  AND state IN ('Completed', 'Failed', 'Cancelled', 'Interrupted');
                DELETE FROM gpu_leases
                WHERE created_at <= $historyCutoff
                  AND state IN ('released', 'cancelled', 'interrupted');
                """;
            delete.Parameters.AddWithValue("$now", now);
            delete.Parameters.AddWithValue("$historyCutoff", historyCutoff);
            _ = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var id in paths)
        {
            if (id.StartsWith("upload-", StringComparison.Ordinal))
            {
                var directory = Path.Combine(_options.UploadDirectory, id);
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            else if (id.StartsWith("artifact-", StringComparison.Ordinal))
            {
                var path = Path.Combine(_options.ArtifactDirectory, id + ".bin");
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }
}
