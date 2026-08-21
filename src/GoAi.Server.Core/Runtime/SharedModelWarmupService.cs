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
        // The desktop host can start before Docker. Retry only the resident speech
        // stack; LM Studio model selection remains strictly request-driven.
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
                    "Spracheingabe, Sprechertrennung und Sprachausgabe sind vorgeladen. LM-Studio-Modelle warten unverändert auf einen AI-Lauf.");
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
                        $"Die dauerhaften Sprachdienste sind noch nicht vollständig verfügbar; neuer Versuch folgt ({exception.GetType().Name}).");
                }
            }
        }

        _runtime.WriteLog(
            "Error",
            "models.shared.warm.failed",
            "Die dauerhaften Sprachdienste konnten innerhalb von zehn Minuten nicht vollständig vorgeladen werden.");
    }
}
