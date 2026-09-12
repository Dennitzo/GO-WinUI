using CommunityToolkit.Mvvm.ComponentModel;
using GoWinUI.App.Services;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using GoAi.Contracts;
using System.Collections.ObjectModel;
using System.Globalization;

namespace GoWinUI.App.ViewModels;

public sealed partial class SettingsViewModel(
    SettingsCoordinator settings,
    GoAiConnectionService goAi,
    IPromptTriggerRepository triggerRepository,
    IBackupService backups,
    ShellViewModel shell,
    ModelCapabilityRegistry modelCapabilities) : ObservableObject
{
    private static readonly HashSet<string> TriggerSortableColumns =
    [
        "Phrase",
        "Action",
        "Description",
        "IsEnabled",
    ];

    private readonly List<(Guid Id, long Revision)> _deletedTriggers = [];

    public ObservableCollection<LocalAiModel> Models { get; } = [];
    public ObservableCollection<LocalAiModel> CodingModels { get; } = [];
    public ObservableCollection<PromptTriggerEditorItem> PromptTriggers { get; } = [];
    public IReadOnlyList<PromptTriggerActionOption> TriggerActions { get; } =
        PromptTriggerEditorItem.AvailableActions;
    public IReadOnlyList<PromptTriggerCategoryFilterOption> TriggerCategoryFilters { get; } =
    [
        new(null, "Alle"),
        .. PromptTriggerEditorItem.AvailableActions.Select(option =>
            new PromptTriggerCategoryFilterOption(option.Value, option.Label)),
    ];

    public IEnumerable<PromptTriggerEditorItem> VisiblePromptTriggers => ApplyTriggerSort(
        (SelectedTriggerCategoryFilter?.Value is { } action
            ? PromptTriggers.Where(item => item.Action == action)
            : PromptTriggers)
        .Where(MatchesTriggerSearch));

    public string TriggerSortColumn { get; private set; } = "Phrase";

    public bool TriggerSortDescending { get; private set; }

    [ObservableProperty]
    public partial string GoAiServerUrl { get; set; } = GoAiConnectionService.DefaultServerUrl;

    [ObservableProperty]
    public partial bool IsAiConnectionEnabled { get; set; }

    [ObservableProperty]
    public partial string LiveCaptionLanguage { get; set; } = "auto";

    [ObservableProperty]
    public partial string? SelectedModel { get; set; }

    [ObservableProperty]
    public partial LocalAiModel? SelectedGeneralModelItem { get; set; }

    [ObservableProperty]
    public partial string? SelectedCodingModel { get; set; }

    [ObservableProperty]
    public partial LocalAiModel? SelectedCodingModelItem { get; set; }

    [ObservableProperty]
    public partial string CodingModelStatus { get; set; } = "Lokale GGUF-Modelle werden beim Aktualisieren erkannt.";

    [ObservableProperty]
    public partial bool CodingToolStepsExpanded { get; set; }

    [ObservableProperty]
    public partial string TriggerSearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial PromptTriggerCategoryFilterOption? SelectedTriggerCategoryFilter { get; set; }

    [ObservableProperty]
    public partial PromptTriggerEditorItem? SelectedPromptTrigger { get; set; }

    public bool CanDeleteSelectedPromptTrigger => SelectedPromptTrigger is not null;

    [ObservableProperty]
    public partial AppTheme Theme { get; set; } = AppTheme.System;

    [ObservableProperty]
    public partial string AccentColor { get; set; } = AppSettings.DefaultAccentColor;

    [ObservableProperty]
    public partial string BackgroundColor { get; set; } = AppSettings.DefaultBackgroundColor;

    [ObservableProperty]
    public partial string Language { get; set; } = "de-DE";

    [ObservableProperty]
    public partial string ConnectionStatus { get; set; } = "Nicht geprüft";

    [ObservableProperty]
    public partial bool IsServerReady { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public void Initialize()
    {
        var current = settings.Current;
        IsAiConnectionEnabled = current.IsAiConnectionEnabled;
        GoAiServerUrl = current.GoAiServerUrl;
        LiveCaptionLanguage = current.LiveCaptionLanguage;
        SelectedModel = current.SelectedModel ?? AppSettings.DefaultSelectedModel;
        SelectedGeneralModelItem = EnsureModelItem(Models, SelectedModel);
        SelectedCodingModel = current.SelectedCodingModel;
        SelectedCodingModelItem = EnsureModelItem(CodingModels, SelectedCodingModel);
        CodingToolStepsExpanded = current.CodingToolStepsExpanded;
        Theme = current.Theme;
        AccentColor = current.AccentColor;
        BackgroundColor = current.BackgroundColor;
        Language = current.Language;
        IsServerReady = shell.IsAiServerReady;
        ConnectionStatus = IsAiConnectionEnabled
            ? "Nicht geprüft"
            : "Offline · keine Serververbindungen";
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Initialize();
        PromptTriggers.Clear();
        foreach (var trigger in await triggerRepository.ListAsync(cancellationToken))
        {
            PromptTriggers.Add(new PromptTriggerEditorItem(trigger));
        }
        SelectedTriggerCategoryFilter ??= TriggerCategoryFilters[0];
        SelectedPromptTrigger = null;
        RefreshPromptTriggerView();
        _deletedTriggers.Clear();
    }

    public async Task SetAiConnectionEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        IsAiConnectionEnabled = enabled;
        if (!enabled)
        {
            await App.Current.ApplyAiConnectionModeAsync(false);
        }
        await settings.UpdateAsync(
            current => current with { IsAiConnectionEnabled = enabled },
            cancellationToken);
        if (enabled)
        {
            await App.Current.ApplyAiConnectionModeAsync(true);
            ConnectionStatus = "Online aktiviert · Verbindung wird geprüft";
            return;
        }

        Models.Clear();
        CodingModels.Clear();
        IsServerReady = false;
        ConnectionStatus = "Offline · keine Serververbindungen";
    }

    public async Task<GoAiConnectionStatus?> SaveAsync(CancellationToken cancellationToken = default)
    {
        await SavePreferencesAsync(cancellationToken);
        await App.Current.ApplyAiConnectionModeAsync(IsAiConnectionEnabled);
        await SaveTriggersAsync(cancellationToken);
        App.Current.ApplyTheme(Theme);
        App.Current.ApplyAccentColor(AccentColor);
        App.Current.ApplyBackgroundColor(BackgroundColor);
        if (!IsAiConnectionEnabled)
        {
            IsServerReady = false;
            ConnectionStatus = "Offline · keine Serververbindungen";
            return null;
        }

        var status = await goAi.TestAsync(cancellationToken);
        ConnectionStatus = status.Message;
        IsServerReady = status.IsReady;
        shell.ApplyAiConnectionState(status.IsReachable, status.IsReady);
        return status;
    }

    // Keep preference persistence independent of window updates and connection
    // probes so it can also be validated without launching the WinUI app.
    internal async Task SavePreferencesAsync(CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(GoAiServerUrl.Trim(), UriKind.Absolute, out var goAiUri)
            || goAiUri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("GO benötigt eine gültige HTTP- oder HTTPS-Adresse zum Docker-Gateway.");
        }

        var generalModel = PreferCurrentSelection(
            SelectedGeneralModelItem?.Id,
            SelectedModel,
            AppSettings.DefaultSelectedModel);
        await settings.UpdateAsync(current => current with
        {
            IsAiConnectionEnabled = IsAiConnectionEnabled,
            GoAiServerUrl = goAiUri.ToString().TrimEnd('/'),
            LiveCaptionLanguage = string.IsNullOrWhiteSpace(LiveCaptionLanguage) ? "auto" : LiveCaptionLanguage.Trim(),
            SelectedModel = generalModel,
            SelectedCodingModel = SelectedCodingModelItem?.Id ?? SelectedCodingModel,
            CodingToolStepsExpanded = CodingToolStepsExpanded,
            ReasoningEffort = "auto",
            Theme = Theme,
            AccentColor = AccentColor,
            BackgroundColor = BackgroundColor,
            Language = Language,
        }, cancellationToken);
        SelectedModel = settings.Current.SelectedModel;
    }

    public async Task<GoAiConnectionStatus?> RefreshModelsAsync(CancellationToken cancellationToken = default)
    {
        GoAiConnectionStatus? status = null;
        IsBusy = true;
        try
        {
            // A ComboBox cannot resolve a saved ID while its dynamic catalog is
            // still empty. Keep the persisted IDs authoritative until the native runtime
            // has returned the matching model objects.
            var requestedGeneralModel = PreferCurrentSelection(
                SelectedModel,
                settings.Current.SelectedModel,
                AppSettings.DefaultSelectedModel);
            SelectedModel = requestedGeneralModel;
            // Reading the remote catalog must never persist the transient UI
            // state. In particular, an asynchronous startup refresh must not
            // overwrite the model IDs restored from settings.json.
            if (!settings.Current.IsAiConnectionEnabled)
            {
                Models.Clear();
                CodingModels.Clear();
                return null;
            }
            using var client = await goAi.CreateClientAsync(cancellationToken);
            // Load the Coding catalog even while another native model is being prepared.
            await RefreshCodingModelsAsync(client, cancellationToken);
            status = await goAi.TestAsync(cancellationToken);
            if (status is null)
            {
                Models.Clear();
                return null;
            }
            IReadOnlyList<LocalAiModel> items;
            if (!status.IsReachable)
            {
                throw new InvalidOperationException(status.Message);
            }
            using var modelTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            modelTimeout.CancelAfter(TimeSpan.FromSeconds(12));
            var modelStatus = await client.GetModelStatusAsync(modelTimeout.Token);
            if (!modelStatus.ProviderReachable)
            {
                throw new InvalidOperationException(
                    goAi.NativeRuntimeError ?? modelStatus.ErrorMessage ?? "Der lokale General-Modellkatalog ist nicht erreichbar. Prüfe die native Unsloth-Laufzeit und den Modellordner.");
            }
            modelCapabilities.Update(modelStatus);
            items = modelStatus.Models
                .Where(model => model.Downloaded
                    && model.Role == "general")
                .Select(model => new LocalAiModel(
                    model.Id,
                    string.Format(CultureInfo.CurrentCulture, "{0} · {1:N0} Token", model.DisplayName ?? model.Id, model.ContextTokens),
                    model.ContextTokens,
                    model.SupportsTools,
                    model.SupportsVision,
                    model.ReasoningEfforts,
                    model.DefaultReasoningEffort))
                .ToArray();
            ConnectionStatus = status.Message;

            Models.Clear();
            foreach (var item in items)
            {
                Models.Add(item);
            }
            // The selection may have changed while the asynchronous catalog
            // request was running. Re-read the current properties and keep
            // them authoritative. A temporarily incomplete local model catalog
            // must not silently replace a persisted model with the first item.
            SelectedModel = PreferCurrentSelection(
                SelectedModel,
                settings.Current.SelectedModel,
                requestedGeneralModel);
            // Bind the ComboBoxes to the actual catalog objects. SelectedValue
            // can remain visually empty when its value was assigned before an
            // asynchronously populated ItemsSource existed.
            SelectedGeneralModelItem = EnsureModelItem(Models, SelectedModel);
            IsServerReady = status.IsReady;
            shell.ApplyAiConnectionState(true, status.IsReady);
            return status;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (status?.IsReachable == true)
            {
                ConnectionStatus = $"Verbunden · Modellstatus nicht verfügbar · {exception.Message}";
                shell.ApplyAiConnectionState(true, status.IsReady);
            }
            else
            {
                ConnectionStatus = string.IsNullOrWhiteSpace(exception.Message)
                    ? "GO AI Server nicht erreichbar"
                    : exception.Message;
                IsServerReady = false;
                shell.ApplyAiConnectionState(false, false);
            }
            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshCodingModelsAsync(GoAi.Client.GoAiClient client, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var catalog = await client.GetCodingModelsAsync(timeout.Token);
            var selected = SelectedCodingModel ?? settings.Current.SelectedCodingModel;
            CodingModels.Clear();
            foreach (var item in catalog.Models)
            {
                CodingModels.Add(new LocalAiModel(item.Id,
                    $"{item.DisplayName ?? item.Id} · {item.ContextTokens:N0} Token",
                    item.ContextTokens, item.SupportsTools, item.SupportsVision));
            }
            SelectedCodingModel = selected;
            SelectedCodingModelItem = EnsureModelItem(CodingModels, selected);
            CodingModelStatus = $"{catalog.Models.Count} lokale Modelle · {catalog.ModelRoot}"
                + (string.IsNullOrWhiteSpace(catalog.Message) ? string.Empty : $" · {catalog.Message}");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
        {
            CodingModelStatus = $"Coding-Modellkatalog nicht erreichbar: {exception.Message}";
        }
    }

    internal static string PreferCurrentSelection(string? current, string? persisted, string fallback) =>
        !string.IsNullOrWhiteSpace(current)
            ? current.Trim()
            : !string.IsNullOrWhiteSpace(persisted)
                ? persisted.Trim()
                : fallback;

    private static LocalAiModel? FindModel(IEnumerable<LocalAiModel> models, string? id) =>
        models.FirstOrDefault(model =>
            string.Equals(model.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));

    private static LocalAiModel? EnsureModelItem(ObservableCollection<LocalAiModel> models, string? id)
    {
        var normalized = id?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        var existing = FindModel(models, normalized);
        if (existing is not null)
        {
            return existing;
        }

        // Display the persisted selection even before the asynchronous native
        // model catalog is reachable. A successful refresh replaces this
        // placeholder with the authoritative descriptor.
        var placeholder = new LocalAiModel(normalized, normalized);
        models.Insert(0, placeholder);
        return placeholder;
    }

    public PromptTriggerEditorItem AddTrigger(
        PromptTriggerAction action,
        string phrase,
        string description = "Benutzerdefinierter Prompt-Trigger.")
    {
        var normalizedPhrase = phrase.Trim();
        if (normalizedPhrase.Length is 0 or > 160)
        {
            throw new InvalidOperationException("Die Triggerphrase muss zwischen 1 und 160 Zeichen lang sein.");
        }

        var now = DateTimeOffset.UtcNow;
        var item = new PromptTriggerEditorItem(new PromptTrigger(
            Guid.NewGuid(), action, normalizedPhrase, description.Trim(),
            PromptTriggerMatchMode.Prefix, true, 100, 0, now, now));
        PromptTriggers.Insert(0, item);
        RefreshPromptTriggerView();
        return item;
    }

    public void RemoveTrigger(PromptTriggerEditorItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.IsNew)
        {
            _deletedTriggers.Add((item.Id, item.Revision));
        }
        PromptTriggers.Remove(item);
        if (ReferenceEquals(SelectedPromptTrigger, item))
        {
            SelectedPromptTrigger = null;
        }
        RefreshPromptTriggerView();
    }

    public void SortPromptTriggers(string columnName)
    {
        if (!TriggerSortableColumns.Contains(columnName))
        {
            return;
        }

        if (string.Equals(TriggerSortColumn, columnName, StringComparison.Ordinal))
        {
            TriggerSortDescending = !TriggerSortDescending;
        }
        else
        {
            TriggerSortColumn = columnName;
            TriggerSortDescending = false;
        }
        OnPropertyChanged(nameof(VisiblePromptTriggers));
    }

    public void RefreshPromptTriggerView() =>
        OnPropertyChanged(nameof(VisiblePromptTriggers));

    private async Task SaveTriggersAsync(CancellationToken cancellationToken)
    {
        foreach (var deleted in _deletedTriggers)
        {
            await triggerRepository.DeleteAsync(deleted.Id, deleted.Revision, cancellationToken);
        }
        _deletedTriggers.Clear();
        foreach (var item in PromptTriggers)
        {
            if (!item.IsDirty)
            {
                continue;
            }
            var saved = item.IsNew
                ? await triggerRepository.CreateAsync(item.ToModel(), cancellationToken)
                : await triggerRepository.UpdateAsync(item.ToModel(), item.Revision, cancellationToken);
            item.ApplySaved(saved);
        }
        RefreshPromptTriggerView();
    }

    public Task<BackupResult> CreateBackupAsync(string destinationPath, CancellationToken cancellationToken = default) =>
        backups.CreateAsync(destinationPath, cancellationToken);

    public async Task RestoreBackupAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        await backups.ValidateAsync(backupPath, cancellationToken);
        await backups.RestoreAsync(backupPath, cancellationToken);
    }

    partial void OnTriggerSearchTextChanged(string value)
    {
        SelectedPromptTrigger = null;
        RefreshPromptTriggerView();
    }

    partial void OnSelectedTriggerCategoryFilterChanged(PromptTriggerCategoryFilterOption? value)
    {
        SelectedPromptTrigger = null;
        RefreshPromptTriggerView();
    }

    partial void OnSelectedPromptTriggerChanged(PromptTriggerEditorItem? value) =>
        OnPropertyChanged(nameof(CanDeleteSelectedPromptTrigger));

    partial void OnSelectedGeneralModelItemChanged(LocalAiModel? value)
    {
        if (value is not null)
        {
            SelectedModel = value.Id;
        }
    }

    partial void OnSelectedCodingModelItemChanged(LocalAiModel? value)
    {
        if (value is not null) SelectedCodingModel = value.Id;
    }

    private IEnumerable<PromptTriggerEditorItem> ApplyTriggerSort(
        IEnumerable<PromptTriggerEditorItem> source)
    {
        var textComparer = StringComparer.CurrentCultureIgnoreCase;
        return TriggerSortColumn switch
        {
            "Action" => TriggerSortDescending
                ? source.OrderByDescending(item => item.ActionDisplayName, textComparer)
                : source.OrderBy(item => item.ActionDisplayName, textComparer),
            "Description" => TriggerSortDescending
                ? source.OrderByDescending(item => item.Description, textComparer)
                : source.OrderBy(item => item.Description, textComparer),
            "IsEnabled" => TriggerSortDescending
                ? source.OrderByDescending(item => item.IsEnabled)
                : source.OrderBy(item => item.IsEnabled),
            _ => TriggerSortDescending
                ? source.OrderByDescending(item => item.Phrase, textComparer)
                : source.OrderBy(item => item.Phrase, textComparer),
        };
    }

    private bool MatchesTriggerSearch(PromptTriggerEditorItem item)
    {
        var query = TriggerSearchText.Trim();
        if (query.Length == 0)
        {
            return true;
        }

        var enabledLabel = item.IsEnabled ? "aktiv" : "inaktiv";
        return item.Phrase.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.ActionDisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || enabledLabel.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
