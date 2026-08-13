using GoAi.Contracts;
using GoAi.Server.Core.Runs;
using GoAi.Server.Core.Storage;

namespace GoAi.Server.Tests;

public sealed class StorageCleanupServiceTests
{
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
