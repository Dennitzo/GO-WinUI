using GoAi.Server.Core.Data;
using GoAi.Server.Core.Security;
using GoAi.Server.Core.Status;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Workers;

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
    private readonly GeneralModelSelectionService _generalModels;
    private readonly CodingModelSelectionService _codingModels;
    private readonly WorkerApiClient _workers;
    private readonly LmStudioClient _lmStudio;

    public ServerInitializationService(
        GoAiDatabase database,
        ApiKeyStore apiKeys,
        WorkerKeyStore workerKeys,
        ReadinessService readiness,
        ServerRuntimeState runtime,
        GpuLeaseScheduler gpuScheduler,
        IOptions<GoAiServerOptions> options,
        GeneralModelSelectionService generalModels,
        CodingModelSelectionService codingModels,
        WorkerApiClient workers,
        LmStudioClient lmStudio)
    {
        _database = database;
        _apiKeys = apiKeys;
        _workerKeys = workerKeys;
        _readiness = readiness;
        _runtime = runtime;
        _gpuScheduler = gpuScheduler;
        _options = options.Value;
        _generalModels = generalModels;
        _codingModels = codingModels;
        _workers = workers;
        _lmStudio = lmStudio;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _generalModels.RestoreAsync(cancellationToken).ConfigureAwait(false);
        await _codingModels.RestoreAsync(cancellationToken).ConfigureAwait(false);
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

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _runtime.SetGatewayState("Wird beendet", "AI-Modelle und Worker-Ressourcen werden entladen.");
        try
        {
            await _workers.ReleaseAllAsync(exceptWorker: null, cancellationToken).ConfigureAwait(false);
            _runtime.WriteLog(
                "Information",
                "workers.released",
                "Alle geladenen Worker-Modelle wurden entladen.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _runtime.WriteLog(
                "Warning",
                "workers.release_failed",
                $"Worker-Modelle konnten beim Beenden nicht vollständig entladen werden ({exception.GetType().Name}).");
        }

        try
        {
            await _lmStudio.UnloadAllModelsAsync(cancellationToken).ConfigureAwait(false);
            _runtime.WriteLog(
                "Information",
                "lmstudio.models.unloaded",
                "Alle durch LM Studio geladenen Modellinstanzen wurden entladen; LM Studio selbst bleibt aktiv.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _runtime.WriteLog(
                "Warning",
                "lmstudio.models.unload_failed",
                $"LM-Studio-Modellinstanzen konnten beim Beenden nicht vollständig entladen werden ({exception.GetType().Name}).");
        }

        _runtime.SetGatewayState("Beendet", "AI-Modelle wurden entladen. LM Studio bleibt aktiv.");
    }
}
