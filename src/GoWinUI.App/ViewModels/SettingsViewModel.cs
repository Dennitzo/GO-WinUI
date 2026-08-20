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
    IAiSecretStore secrets,
    IPromptTriggerRepository triggerRepository,
    IBackupService backups,
    ShellViewModel shell) : ObservableObject
{
    private static readonly HashSet<string> TriggerSortableColumns =
    [
        "Phrase",
        "Action",
        "Description",
        "IsEnabled",
    ];

    private readonly List<(Guid Id, long Revision)> _deletedTriggers = [];

    public ObservableCollection<LmModel> Models { get; } = [];
    public ObservableCollection<LmModel> CodingModels { get; } = [];
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
    public partial string GoAiApiKey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LocalToolWorkspacePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LiveCaptionLanguage { get; set; } = "auto";

    [ObservableProperty]
    public partial string? SelectedModel { get; set; }

    [ObservableProperty]
    public partial string SelectedCodingModel { get; set; } = AppSettings.DefaultSelectedCodingModel;

    [ObservableProperty]
    public partial string ReasoningEffort { get; set; } = "medium";

    [ObservableProperty]
    public partial bool HasStoredApiKey { get; set; }

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
        LocalToolWorkspacePath = current.LocalToolWorkspacePath ?? string.Empty;
        LiveCaptionLanguage = current.LiveCaptionLanguage;
        SelectedModel = current.SelectedModel;
        SelectedCodingModel = current.SelectedCodingModel;
        ReasoningEffort = current.ReasoningEffort;
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
        HasStoredApiKey = !string.IsNullOrWhiteSpace(await secrets.GetApiKeyAsync(cancellationToken));
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
        if (!Uri.TryCreate(GoAiServerUrl.Trim(), UriKind.Absolute, out var goAiUri)
            || goAiUri.Scheme is not ("http" or "https")
            || (goAiUri.Scheme == "http" && !goAiUri.IsLoopback))
        {
            throw new InvalidOperationException("GO AI Server benötigt eine gültige HTTPS-Adresse; HTTP ist nur auf Loopback erlaubt.");
        }
        if (!string.IsNullOrWhiteSpace(GoAiApiKey))
        {
            await secrets.SetApiKeyAsync(GoAiApiKey, cancellationToken);
            GoAiApiKey = string.Empty;
            HasStoredApiKey = true;
        }

        var workspace = string.IsNullOrWhiteSpace(LocalToolWorkspacePath)
            ? null
            : Path.GetFullPath(LocalToolWorkspacePath.Trim());
        await settings.UpdateAsync(current => current with
        {
            IsAiConnectionEnabled = IsAiConnectionEnabled,
            GoAiServerUrl = goAiUri.ToString().TrimEnd('/'),
            LocalToolWorkspacePath = workspace,
            LiveCaptionLanguage = string.IsNullOrWhiteSpace(LiveCaptionLanguage) ? "auto" : LiveCaptionLanguage.Trim(),
            SelectedModel = string.IsNullOrWhiteSpace(SelectedModel)
                ? AppSettings.DefaultSelectedModel
                : SelectedModel.Trim(),
            SelectedCodingModel = string.IsNullOrWhiteSpace(SelectedCodingModel)
                ? AppSettings.DefaultSelectedCodingModel
                : SelectedCodingModel.Trim(),
            ReasoningEffort = ReasoningEffort,
            Theme = Theme,
            AccentColor = AccentColor,
            BackgroundColor = BackgroundColor,
            Language = Language,
        }, cancellationToken);
        await App.Current.ApplyAiConnectionModeAsync(IsAiConnectionEnabled);
        if (IsAiConnectionEnabled && !string.IsNullOrWhiteSpace(SelectedModel))
        {
            using var client = await goAi.CreateClientAsync(cancellationToken);
            _ = await client.SelectGeneralModelAsync(SelectedModel, cancellationToken);
            _ = await client.SelectCodingModelAsync(SelectedCodingModel, cancellationToken);
        }
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

    public async Task<GoAiConnectionStatus?> RefreshModelsAsync(CancellationToken cancellationToken = default)
    {
        GoAiConnectionStatus? status = null;
        IsBusy = true;
        try
        {
            status = await SaveAsync(cancellationToken);
            if (status is null)
            {
                Models.Clear();
                CodingModels.Clear();
                return null;
            }
            IReadOnlyList<LmModel> items;
            if (!status.IsReachable)
            {
                throw new InvalidOperationException(status.Message);
            }
            using var client = await goAi.CreateClientAsync(cancellationToken);
            var modelStatus = await client.GetModelStatusAsync(cancellationToken);
            items = modelStatus.Models
                .Where(model => model.Downloaded && model.Role == "general")
                .Select(model => new LmModel(
                    model.Id,
                    string.Format(CultureInfo.CurrentCulture, "{0} · {1:N0} Token", model.DisplayName ?? model.Id, model.ContextTokens),
                    model.ContextTokens))
                .ToArray();
            var codingItems = modelStatus.Models
                .Where(model => model.Downloaded && model.Role == "code")
                .OrderByDescending(model => string.Equals(
                    model.Id,
                    AppSettings.DefaultSelectedCodingModel,
                    StringComparison.OrdinalIgnoreCase))
                .Select(model => new LmModel(
                    model.Id,
                    string.Format(CultureInfo.CurrentCulture, "{0} · {1:N0} Token", model.DisplayName ?? model.Id, model.ContextTokens),
                    model.ContextTokens))
                .ToArray();
            ConnectionStatus = status.Message;

            Models.Clear();
            foreach (var item in items)
            {
                Models.Add(item);
            }
            CodingModels.Clear();
            foreach (var item in codingItems)
            {
                CodingModels.Add(item);
            }
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

    public async Task ImportConnectionBundleAsync(string path, CancellationToken cancellationToken = default)
    {
        var imported = await goAi.ImportConnectionBundleAsync(path, cancellationToken);
        GoAiServerUrl = imported.ServerUrl;
    }

    public async Task DeleteApiKeyAsync(CancellationToken cancellationToken = default)
    {
        await secrets.DeleteApiKeyAsync(cancellationToken);
        GoAiApiKey = string.Empty;
        HasStoredApiKey = false;
    }

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
