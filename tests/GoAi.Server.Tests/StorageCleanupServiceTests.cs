using GoAi.Contracts;
using GoAi.Server.Core.Runs;
using GoAi.Server.Core.Storage;

namespace GoAi.Server.Tests;

public sealed class StorageCleanupServiceTests
{
    [Fact]
    public async Task CleanupKeepsExpiredUploadReferencedByActiveRun()
    {
        using var context = new TestServerContext();
        var uploadId = "upload-" + Guid.NewGuid().ToString("N");
        var uploadDirectory = Path.Combine(context.Options.UploadDirectory, uploadId);
        Directory.CreateDirectory(uploadDirectory);
        await File.WriteAllTextAsync(Path.Combine(uploadDirectory, "payload.bin"), "image");

        await using (var connection = await context.Database.OpenConnectionAsync())
        {
            var expired = GoAi.Server.Core.Data.GoAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow.AddMinutes(-1));
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO uploads(upload_id, file_name, media_type, total_length, total_sha256,
                    chunk_size, chunk_count, state, created_at, expires_at)
                VALUES($id, 'reference.webp', 'image/webp', 5, $sha, 5, 1, 'complete', $expired, $expired);
                """;
            insert.Parameters.AddWithValue("$id", uploadId);
            insert.Parameters.AddWithValue("$sha", new string('a', 64));
            insert.Parameters.AddWithValue("$expired", expired);
            _ = await insert.ExecuteNonQueryAsync();
        }

        var repository = new RunRepository(context.Database, new RunEventNotifier());
        _ = await repository.CreateAsync(new RunRequest(
            GoAiProtocol.Version,
            RunMode.Coding,
            [new RunMessage("user", [new ContentPart("upload", UploadId: uploadId)])],
            UploadIds: [uploadId]), null);

        var cleanup = new StorageCleanupService(context.Database, context.WrappedOptions);
        await cleanup.CleanupExpiredAsync();

        await using var verify = await context.Database.OpenConnectionAsync();
        await using var count = verify.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM uploads WHERE upload_id = $id;";
        count.Parameters.AddWithValue("$id", uploadId);
        Assert.Equal(1L, Convert.ToInt64(await count.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(Directory.Exists(uploadDirectory));
    }

    [Fact]
    public async Task CleanupRemovesExpiredTerminalRunEventsAndReleasedLeases()
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var request = new RunRequest(
            GoAiProtocol.Version,
            RunMode.General,
            [new RunMessage("user", [new ContentPart("text", "Temporärer Test")])]);
        var (run, _) = await repository.CreateAsync(request, null);
        await repository.AppendEventAsync(run.RunId, RunEventTypes.RunStarted, new { protocolVersion = GoAiProtocol.Version });

        await using (var connection = await context.Database.OpenConnectionAsync())
        {
            var old = GoAi.Server.Core.Data.GoAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow.AddHours(-25));
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE runs SET state = 'Completed', updated_at = $old WHERE run_id = $run;
                INSERT INTO gpu_leases(lease_id, run_id, workload, state, created_at, acquired_at, released_at)
                VALUES('lease-expired', $run, 'llm', 'released', $old, $old, $old);
                """;
            command.Parameters.AddWithValue("$old", old);
            command.Parameters.AddWithValue("$run", run.RunId);
            _ = await command.ExecuteNonQueryAsync();
        }

        var cleanup = new StorageCleanupService(context.Database, context.WrappedOptions);
        await cleanup.CleanupExpiredAsync();

        await using var verify = await context.Database.OpenConnectionAsync();
        foreach (var table in new[] { "runs", "run_events", "gpu_leases" })
        {
            await using var count = verify.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM {table};";
            Assert.Equal(0L, Convert.ToInt64(await count.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
