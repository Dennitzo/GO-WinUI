using GoAi.Server.Core.Workers;
using Microsoft.Extensions.Hosting;

namespace GoAi.Server.Core.Runtime;

public sealed class SpeechWarmupService : BackgroundService
{
    private readonly WorkerOrchestrator _workers;
    private readonly ServerRuntimeState _runtime;

    public SpeechWarmupService(WorkerOrchestrator workers, ServerRuntimeState runtime)
    {
        _workers = workers;
        _runtime = runtime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken).ConfigureAwait(false);
            await _workers.WarmSpeechResourcesAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _runtime.WriteLog("Warning", "speech.warm.failed", "Sprachmodelle konnten beim Start nicht vorgewärmt werden.");
            _ = exception;
        }
    }
}
