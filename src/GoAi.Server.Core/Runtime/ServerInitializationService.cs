using GoAi.Server.Core.Data;
using GoAi.Server.Core.Security;
using GoAi.Server.Core.Status;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Models;

namespace GoAi.Server.Core.Runtime;

public sealed class ServerInitializationService : IHostedService
{
    private readonly GoAiDatabase _database;
    private readonly ApiKeyStore _apiKeys;
    private readonly WorkerKeyStore _workerKeys;
    private readonly ReadinessService _readiness;
    private readonly ServerRuntimeState _runtime;
    private readonly GoAiServerOptions _options;
    private readonly GpuLeaseScheduler _gpuScheduler;

    public ServerInitializationService(
        GoAiDatabase database,
        ApiKeyStore apiKeys,
        WorkerKeyStore workerKeys,
        ReadinessService readiness,
        ServerRuntimeState runtime,
        GpuLeaseScheduler gpuScheduler,
        IOptions<GoAiServerOptions> options)
    {
        _database = database;
        _apiKeys = apiKeys;
        _workerKeys = workerKeys;
        _readiness = readiness;
        _runtime = runtime;
        _gpuScheduler = gpuScheduler;
        _options = options.Value;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gpuScheduler.RecoverInterruptedLeasesAsync(cancellationToken).ConfigureAwait(false);
        await _workerKeys.EnsureKeysAsync(cancellationToken).ConfigureAwait(false);
        var bootstrap = await _apiKeys.EnsureBootstrapKeyAsync(cancellationToken).ConfigureAwait(false);
        if (bootstrap is not null)
        {
            await File.WriteAllTextAsync(
                _options.BootstrapKeyExportPath,
                bootstrap.PlainText,
                cancellationToken).ConfigureAwait(false);
            _runtime.SetOneTimeBootstrapKey(bootstrap.PlainText);
            _runtime.WriteLog("Warning", "security.bootstrap_key.created", "Ein einmal sichtbarer GO-Client-Schlüssel wurde erzeugt.");
        }

        var readiness = await _readiness.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        _runtime.WriteLog(
            readiness.Status == "ready" ? "Information" : "Warning",
            "server.initialized",
            readiness.Status == "ready" ? "GO AI Server ist bereit." : $"GO AI Server ist nicht bereit: {readiness.Reason}");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _runtime.SetGatewayState("Beendet", "Server wurde beendet.");
        return Task.CompletedTask;
    }
}
