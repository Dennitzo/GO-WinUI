using CommunityToolkit.Mvvm.ComponentModel;
using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runtime;
using GoAi.Server.Core.Security;
using GoAi.Server.Core.Status;
using Microsoft.Extensions.Options;
using Microsoft.UI.Dispatching;
using System.Collections.ObjectModel;
using System.Globalization;

namespace GoAi.Server.App.ViewModels;

public sealed partial class ServerDashboardViewModel : ObservableObject
{
    private readonly ReadinessService _readiness;
    private readonly LmStudioClient _lmStudio;
    private readonly GpuStatusService _gpu;
    private readonly ServiceProbeService _probes;
    private readonly ServerMetricsService _metrics;
    private readonly ApiKeyStore _apiKeys;
    private readonly DpapiSecretStore _secretStore;
    private readonly ServerRuntimeState _runtime;
    private readonly GoAiServerOptions _options;
    private readonly DispatcherQueue? _dispatcherQueue;

    [ObservableProperty]
    public partial string GatewayStatus { get; set; } = "Startet";

    [ObservableProperty]
    public partial string GatewayDetail { get; set; } = "Initialisierung läuft";

    [ObservableProperty]
    public partial string PublicEndpoint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LoopbackEndpoint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Uptime { get; set; } = "00:00:00";

    [ObservableProperty]
    public partial string GpuQueue { get; set; } = "0 wartend";

    [ObservableProperty]
    public partial string ActiveLease { get; set; } = "Keine aktive GPU-Aufgabe";

    [ObservableProperty]
    public partial string RunsSummary { get; set; } = "0 aktiv · 0 gesamt";

    [ObservableProperty]
    public partial string RunsDetail { get; set; } = "Noch keine Agentenläufe";

    [ObservableProperty]
    public partial string StorageSummary { get; set; } = "0 B temporär";

    [ObservableProperty]
    public partial string StorageDetail { get; set; } = "Datenbank 0 B";

    [ObservableProperty]
    public partial string DiskFree { get; set; } = "–";

    [ObservableProperty]
    public partial string DataDirectory { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ApiKeySummary { get; set; } = "0 aktive Schlüssel";

    [ObservableProperty]
    public partial string? OneTimeApiKey { get; set; }

    [ObservableProperty]
    public partial string SecurityMessage { get; set; } = "API-Schlüssel werden nur gehasht gespeichert.";

    [ObservableProperty]
    public partial string LogText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsRefreshing { get; set; }

    public ServerDashboardViewModel(
        ReadinessService readiness,
        LmStudioClient lmStudio,
        GpuStatusService gpu,
        ServiceProbeService probes,
        ServerMetricsService metrics,
        ApiKeyStore apiKeys,
        DpapiSecretStore secretStore,
        ServerRuntimeState runtime,
        IOptions<GoAiServerOptions> options)
    {
        _readiness = readiness;
        _lmStudio = lmStudio;
        _gpu = gpu;
        _probes = probes;
        _metrics = metrics;
        _apiKeys = apiKeys;
        _secretStore = secretStore;
        _runtime = runtime;
        _options = options.Value;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        PublicEndpoint = _options.PublicUrl;
        LoopbackEndpoint = $"http://127.0.0.1:{_options.GatewayPort.ToString(CultureInfo.InvariantCulture)}";
        DataDirectory = _options.DataDirectory;
        OneTimeApiKey = runtime.OneTimeBootstrapKey;
        RebuildLogText();
        _runtime.LogAdded += OnLogAdded;
        _runtime.Changed += OnRuntimeChanged;
    }

    public ObservableCollection<ModelStatusRow> Models { get; } = [];

    public ObservableCollection<GpuStatusRow> Gpus { get; } = [];

    public ObservableCollection<ServiceStatusRow> Services { get; } = [];

    public ObservableCollection<ApiKeyRow> ApiKeys { get; } = [];

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (IsRefreshing)
        {
            return;
        }

        IsRefreshing = true;
        try
        {
            var readinessTask = _readiness.GetSnapshotAsync(cancellationToken);
            var modelTask = _lmStudio.GetStatusAsync(cancellationToken);
            var gpuTask = _gpu.GetStatusAsync(cancellationToken);
            var servicesTask = _probes.GetStatusesAsync(cancellationToken);
            var metricsTask = _metrics.GetSnapshotAsync(cancellationToken);
            var apiKeysTask = _apiKeys.ListAsync(cancellationToken: cancellationToken);
            await Task.WhenAll(readinessTask, modelTask, gpuTask, servicesTask, metricsTask, apiKeysTask);

            var readiness = await readinessTask;
            var models = await modelTask;
            var gpu = await gpuTask;
            var services = await servicesTask;
            var metrics = await metricsTask;
            var apiKeys = await apiKeysTask;
            GatewayStatus = readiness.Status == "ready" ? "Bereit" : "Nicht bereit";
            GatewayDetail = readiness.Reason ?? "Gateway, Netzwerk und LM Studio sind bereit.";
            Uptime = (DateTimeOffset.UtcNow - _runtime.StartedAt).ToString("dd'.'hh':'mm':'ss", CultureInfo.InvariantCulture);
            GpuQueue = $"{gpu.QueueLength.ToString(CultureInfo.CurrentCulture)} wartend";
            ActiveLease = gpu.ActiveLease is null ? "Keine aktive GPU-Aufgabe" : $"Aktiv: {gpu.ActiveLease}";
            RunsSummary = $"{metrics.ActiveRuns:N0} aktiv · {metrics.TotalRuns:N0} gesamt";
            RunsDetail = $"{metrics.CompletedRuns:N0} abgeschlossen · {metrics.FailedRuns:N0} fehlgeschlagen";
            StorageSummary = $"{FormatBytes(metrics.ArtifactBytes + metrics.UploadBytes)} temporär";
            StorageDetail = $"{metrics.ArtifactCount:N0} Artefakte · {metrics.UploadCount:N0} Uploads · DB {FormatBytes(metrics.DatabaseBytes)}";
            DiskFree = $"{FormatBytes(metrics.DiskFreeBytes)} frei";
            DataDirectory = metrics.DataDirectory;
            ApiKeySummary = $"{metrics.ActiveApiKeys:N0} aktive Schlüssel";

            ReplaceRows(Models, models.Models.Select(static model => new ModelStatusRow(
                model.Id,
                model.Role,
                $"{model.ContextTokens:N0} Token",
                model.State,
                model.Downloaded ? "Vorhanden" : "Fehlt")));
            ReplaceRows(Gpus, gpu.Devices.Select(static device => new GpuStatusRow(
                $"GPU {device.Index}: {device.Name}",
                $"{device.MemoryUsedMiB:N0} / {device.MemoryTotalMiB:N0} MiB",
                $"{device.UtilizationPercent}%",
                $"{device.TemperatureCelsius} °C")));
            ReplaceRows(Services, services.Select(static service => new ServiceStatusRow(
                service.Name,
                service.Endpoint,
                service.State,
                service.Detail ?? string.Empty)));
            ReplaceRows(ApiKeys, apiKeys.Select(key => new ApiKeyRow(
                key.KeyId,
                key.Name,
                $"Erstellt {key.CreatedAt.ToLocalTime():dd.MM.yyyy, HH:mm}",
                key.LastUsedAt is null ? "Noch nicht verwendet" : $"Zuletzt {key.LastUsedAt.Value.ToLocalTime():dd.MM.yyyy, HH:mm}",
                apiKeys.Count > 1)));
            OneTimeApiKey ??= _runtime.OneTimeBootstrapKey;
            RebuildLogText();
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    public async Task<string> CreateApiKeyAsync(CancellationToken cancellationToken = default)
    {
        var issued = await _apiKeys.CreateKeyAsync("GO Client", cancellationToken);
        OneTimeApiKey = issued.PlainText;
        SecurityMessage = $"Neuer Schlüssel {issued.KeyId} – jetzt kopieren; er wird nicht erneut angezeigt.";
        await RefreshAsync(cancellationToken);
        return issued.PlainText;
    }

    public async Task RevokeApiKeyAsync(string keyId, CancellationToken cancellationToken = default)
    {
        if (!await _apiKeys.TryRevokePreservingOneAsync(keyId, cancellationToken))
        {
            SecurityMessage = "Der Schlüssel wurde nicht widerrufen. Der letzte aktive Schlüssel bleibt geschützt.";
            return;
        }

        SecurityMessage = $"API-Schlüssel {keyId} wurde widerrufen.";
        await RefreshAsync(cancellationToken);
    }

    public async Task SaveLmStudioTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        await _secretStore.SaveLmStudioTokenAsync(token, cancellationToken);
        SecurityMessage = "LM-Studio-Token wurde DPAPI-geschützt für dieses Windows-Konto gespeichert.";
    }

    public void HideOneTimeApiKey()
    {
        OneTimeApiKey = null;
        _runtime.ClearOneTimeBootstrapKey();
        SecurityMessage = "Der Klartextschlüssel wurde aus der Oberfläche entfernt.";
    }

    public void DisposeSubscriptions()
    {
        _runtime.LogAdded -= OnLogAdded;
        _runtime.Changed -= OnRuntimeChanged;
    }

    private void OnRuntimeChanged(object? sender, EventArgs e)
    {
        RunOnUiThread(() =>
        {
            GatewayStatus = _runtime.GatewayState;
            GatewayDetail = _runtime.ReadinessReason;
            OneTimeApiKey ??= _runtime.OneTimeBootstrapKey;
        });
    }

    private void OnLogAdded(object? sender, ServerLogEntry e) => RunOnUiThread(RebuildLogText);

    private void RunOnUiThread(Action action)
    {
        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
        {
            action();
            return;
        }

        _ = _dispatcherQueue.TryEnqueue(() => action());
    }

    private void RebuildLogText()
    {
        LogText = string.Join(
            Environment.NewLine,
            _runtime.GetLogs().Select(static entry =>
                $"[{entry.Timestamp:HH:mm:ss}] {entry.Level,-11} {entry.EventId}  {entry.Message}"));
    }

    private static void ReplaceRows<T>(ObservableCollection<T> target, IEnumerable<T> rows)
    {
        target.Clear();
        foreach (var row in rows)
        {
            target.Add(row);
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = Math.Max(0, bytes);
        var unit = 0;
        var display = (double)value;
        while (display >= 1024 && unit < units.Length - 1)
        {
            display /= 1024;
            unit++;
        }

        return $"{display:0.#} {units[unit]}";
    }
}
