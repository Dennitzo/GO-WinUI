using GoAi.Server.Core.Workers;
using Microsoft.Extensions.Hosting;

namespace GoAi.Server.Core.Runtime;

public sealed class SharedModelWarmupService : BackgroundService
{
    private readonly WorkerOrchestrator _workers;
    private readonly ServerRuntimeState _runtime;

    public SharedModelWarmupService(
        WorkerOrchestrator workers,
        ServerRuntimeState runtime)
    {
        _workers = workers;
        _runtime = runtime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The desktop host starts before LM Studio and Docker. Retry during their
        // bounded startup window, then leave readiness/status to report a real fault.
        var deadline = DateTimeOffset.UtcNow.AddMinutes(10);
        var attempt = 0;
        while (!stoppingToken.IsCancellationRequested && DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                await Task.Delay(attempt == 0 ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                await _workers.WarmAllStartupResourcesAsync(stoppingToken).ConfigureAwait(false);
                _runtime.WriteLog(
                    "Information",
                    "models.startup.warm.completed",
                    "Die dauerhaften Sprachdienste sind vorgeladen. Der LM-Studio-Modellzustand wurde ohne unnötigen Modellwechsel für den nächsten Lauf vorbereitet.");
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                attempt++;
                if (attempt == 1 || attempt % 6 == 0)
                {
                    _runtime.WriteLog(
                        "Warning",
                        "models.shared.warm.retry",
                        $"AI-Startressourcen sind noch nicht vollständig verfügbar; neuer Versuch folgt ({exception.GetType().Name}).");
                }
            }
        }

        _runtime.WriteLog(
            "Error",
            "models.shared.warm.failed",
            "Die AI-Startressourcen konnten innerhalb von zehn Minuten nicht vollständig vorgeladen werden.");
    }
}
