using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runtime;
using Microsoft.Extensions.Options;

namespace GoAi.Server.Core.Status;

public sealed class ReadinessService
{
    private readonly GoAiServerOptions _options;
    private readonly ModelRuntimeClient _modelRuntime;
    private readonly ServerRuntimeState _runtime;

    public ReadinessService(
        IOptions<GoAiServerOptions> options,
        ModelRuntimeClient modelRuntime,
        ServerRuntimeState runtime)
    {
        _options = options.Value;
        _modelRuntime = modelRuntime;
        _runtime = runtime;
    }

    public async Task<HealthSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var modelStatus = await _modelRuntime.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return await GetSnapshotAsync(modelStatus, cancellationToken).ConfigureAwait(false);
    }

    public async Task<HealthSnapshot> GetSnapshotAsync(
        ModelStatusSnapshot modelStatus,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelStatus);
        if (!modelStatus.ProviderReachable)
        {
            return NotReady(
                $"Der private Modellrouter ist über {_options.ModelRuntimeUri} nicht erreichbar.",
                "Den Docker-Container 'llm' und dessen Logs prüfen.");
        }

        var requiredModelIds = new HashSet<string>(
            [_options.GeneralModelId, _options.CodeModelId],
            StringComparer.OrdinalIgnoreCase);
        var missingRequired = modelStatus.Models
            .Where(model => requiredModelIds.Contains(model.Id))
            .Where(static model => !model.Downloaded)
            .Select(static model => model.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missingRequired.Length > 0)
        {
            return NotReady(
                "Erforderliche Modelle fehlen: " + string.Join(", ", missingRequired),
                "Das gepinnte Modellverzeichnis /models und models.ini prüfen.");
        }

        var selectableModelIds = new HashSet<string>(
            [_options.GeneralModelId, .. CodingModelCatalog.Models.Select(static model => model.Id)],
            StringComparer.OrdinalIgnoreCase);
        var selectableModels = modelStatus.Models
            .Where(model => selectableModelIds.Contains(model.Id))
            .ToArray();
        var loading = selectableModels.FirstOrDefault(model =>
            !model.Loaded && string.Equals(model.State, "loading", StringComparison.OrdinalIgnoreCase));
        if (loading is not null)
        {
            _runtime.SetGatewayState("Modell wird geladen", loading.Id);
            return new HealthSnapshot("modelLoading", GoAiProtocol.Version, DateTimeOffset.UtcNow, loading.Id);
        }
        if (!selectableModels.Any(static model => model.Loaded))
        {
            _runtime.SetGatewayState("Bereit", "Gateway bereit; Modell wird beim ersten AI-Lauf geladen.");
            return new HealthSnapshot(
                "modelNotLoaded",
                GoAiProtocol.Version,
                DateTimeOffset.UtcNow,
                "Kein General- oder Coding-Modell ist geladen.");
        }
        _runtime.SetGatewayState("Bereit", "Gateway und Modellruntime sind bereit.");
        return new HealthSnapshot("ready", GoAiProtocol.Version, DateTimeOffset.UtcNow);
    }

    private HealthSnapshot NotReady(string reason, string repair)
    {
        _runtime.SetGatewayState("Nicht bereit", reason);
        return new HealthSnapshot("notReady", GoAiProtocol.Version, DateTimeOffset.UtcNow, reason, repair);
    }
}
