using GoAi.Server.Core.Runtime;
using Microsoft.Extensions.Hosting;

namespace GoAi.Server.Core.Runs;

public sealed class ClientToolDeadlineService(
    RunRepository repository,
    RunWorkChannel queue,
    ServerRuntimeState runtime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            try
            {
                await QueueExpiredAsync(DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException && !stoppingToken.IsCancellationRequested)
            {
                runtime.WriteLog("Error", "run.deadline_sweep_failed", "Client-Auftragsfristen konnten nicht geprüft werden: " + exception.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    internal async Task<int> QueueExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var expired = await repository.QueueExpiredWaitingRunsAsync(now, cancellationToken).ConfigureAwait(false);
        foreach (var runId in expired)
            await queue.EnqueueAsync(runId, cancellationToken).ConfigureAwait(false);
        var retries = await repository.QueueDueProviderRetriesAsync(now, cancellationToken).ConfigureAwait(false);
        foreach (var runId in retries)
            await queue.EnqueueAsync(runId, cancellationToken).ConfigureAwait(false);
        return expired.Count + retries.Count;
    }
}
