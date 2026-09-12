using GoAi.Server.Core.Data;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Status;
using Microsoft.Extensions.Hosting;

namespace GoAi.Server.Core.Runtime;

public sealed class ServerInitializationService : IHostedService
{
    private readonly GoAiDatabase _database;
    private readonly ReadinessService _readiness;
    private readonly ServerRuntimeState _runtime;
    private readonly GpuLeaseScheduler _gpuScheduler;

    public ServerInitializationService(
        GoAiDatabase database,
        ReadinessService readiness,
        ServerRuntimeState runtime,
        GpuLeaseScheduler gpuScheduler)
    {
        _database = database;
        _readiness = readiness;
        _runtime = runtime;
        _gpuScheduler = gpuScheduler;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gpuScheduler.RecoverInterruptedLeasesAsync(cancellationToken).ConfigureAwait(false);
        var readiness = await _readiness.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        _runtime.WriteLog(
            readiness.Status == "ready" ? "Information" : "Warning",
            "server.initialized",
            readiness.Status is "ready" or "modelNotLoaded" or "modelLoading"
                ? "Der Docker-Gatewaystack ist erreichbar."
                : $"Der Docker-Gatewaystack ist nicht bereit: {readiness.Reason}");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // The gateway does not own the native Windows llama server or worker lifetimes.
        // Restarting this container preserves independently managed model and speech processes.
        _runtime.SetGatewayState("Beendet", "Gateway beendet; native Modellruntime und Docker-Worker werden unabhängig verwaltet.");
        return Task.CompletedTask;
    }
}
