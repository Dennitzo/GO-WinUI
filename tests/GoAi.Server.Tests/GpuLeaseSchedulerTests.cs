using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runtime;

namespace GoAi.Server.Tests;

public sealed class GpuLeaseSchedulerTests
{
    [Fact]
    public async Task SchedulerAllowsExactlyOneGpuLease()
    {
        using var context = new TestServerContext();
        using var scheduler = new GpuLeaseScheduler(context.Database, new ServerRuntimeState());
        await using var first = await scheduler.AcquireAsync("llm", "run-1");

        var secondTask = scheduler.AcquireAsync("image", "run-2");
        await Task.Delay(100);
        Assert.False(secondTask.IsCompleted);
        Assert.Equal(1, scheduler.QueueLength);

        await first.DisposeAsync();
        await using var second = await secondTask;
        Assert.Equal(0, scheduler.QueueLength);
        Assert.Equal(second.LeaseId, scheduler.ActiveLease);
        var activity = Assert.Single(scheduler.ActiveActivities);
        Assert.Equal("image", activity.Workload);
        Assert.Equal("run-2", activity.RunId);
    }

    [Fact]
    public async Task GeneralAndSpeechShareCapacityWhileLagunaWaitsExclusively()
    {
        using var context = new TestServerContext();
        using var scheduler = new GpuLeaseScheduler(context.Database, new ServerRuntimeState());
        var general = await scheduler.AcquireAsync("llm-general", "run-general", GpuLeaseMode.Shared);
        var speech = await scheduler.AcquireAsync("live-caption", "caption-1", GpuLeaseMode.Shared);
        try
        {
            Assert.Contains(general.LeaseId, scheduler.ActiveLease, StringComparison.Ordinal);
            Assert.Contains(speech.LeaseId, scheduler.ActiveLease, StringComparison.Ordinal);
            Assert.Collection(
                scheduler.ActiveActivities.OrderBy(static activity => activity.Workload),
                activity => Assert.Equal("live-caption", activity.Workload),
                activity => Assert.Equal("llm-general", activity.Workload));

            var lagunaTask = scheduler.AcquireAsync("llm-code", "run-code", GpuLeaseMode.Exclusive);
            await Task.Delay(100);
            Assert.False(lagunaTask.IsCompleted);
            Assert.Equal(1, scheduler.QueueLength);

            await general.DisposeAsync();
            await Task.Delay(50);
            Assert.False(lagunaTask.IsCompleted);

            await speech.DisposeAsync();
            await using var laguna = await lagunaTask;
            Assert.Equal(laguna.LeaseId, scheduler.ActiveLease);
        }
        finally
        {
            await general.DisposeAsync();
            await speech.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("llm-general", "gpt-oss-20b", "LM Studio")]
    [InlineData("llm-code", "Laguna-S-2.1", "LM Studio")]
    [InlineData("live-caption", "Sprache wird live transkribiert", "Docker · Whisper STT")]
    [InlineData("text-to-speech", "Antwort wird vorgelesen", "Docker · Supertonic F5 Ultra · GPU 1")]
    [InlineData("image-generation", "Bild wird erstellt", "Docker · Image")]
    public void GpuStatusMapsWorkloadsToFooterLabels(
        string workload,
        string expectedName,
        string expectedRuntime)
    {
        var actual = GpuStatusService.DescribeWorkload(workload);

        Assert.Equal(expectedName, actual.DisplayName);
        Assert.Equal(expectedRuntime, actual.Runtime);
    }

    [Fact]
    public async Task RecoveryMarksPersistedActiveAndQueuedLeasesInterrupted()
    {
        using var context = new TestServerContext();
        await using (var connection = await context.Database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO gpu_leases(lease_id, workload, state, created_at)
                VALUES('lease-active', 'vision', 'active', $now),
                      ('lease-queued', 'speech', 'queued', $now),
                      ('lease-released', 'llm', 'released', $now);
                """;
            command.Parameters.AddWithValue("$now", GoAi.Server.Core.Data.GoAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
            _ = await command.ExecuteNonQueryAsync();
        }

        using var scheduler = new GpuLeaseScheduler(context.Database, new ServerRuntimeState());
        await scheduler.RecoverInterruptedLeasesAsync();

        await using var verify = await context.Database.OpenConnectionAsync();
        await using var read = verify.CreateCommand();
        read.CommandText = "SELECT lease_id, state FROM gpu_leases ORDER BY lease_id;";
        await using var reader = await read.ExecuteReaderAsync();
        var states = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync())
        {
            states[reader.GetString(0)] = reader.GetString(1);
        }
        Assert.Equal("interrupted", states["lease-active"]);
        Assert.Equal("interrupted", states["lease-queued"]);
        Assert.Equal("released", states["lease-released"]);
    }
}
