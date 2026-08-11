using CommunityToolkit.Mvvm.ComponentModel;
using GoWinUI.App.Services;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using System.Collections.ObjectModel;

namespace GoWinUI.App.ViewModels;

public sealed partial class SettingsViewModel(
    SettingsCoordinator settings,
    ILmStudioClient lmStudio,
    IBackupService backups,
    ShellViewModel shell) : ObservableObject
{
    public ObservableCollection<LmModel> Models { get; } = [];

    [ObservableProperty]
    public partial string LmStudioBaseUrl { get; set; } = "http://127.0.0.1:1234/v1";

    [ObservableProperty]
    public partial string? SelectedModel { get; set; }

    [ObservableProperty]
    public partial string ReasoningEffort { get; set; } = "medium";

    [ObservableProperty]
    public partial AppTheme Theme { get; set; } = AppTheme.System;

    [ObservableProperty]
    public partial string Language { get; set; } = "de-DE";

    [ObservableProperty]
    public partial string ConnectionStatus { get; set; } = "Nicht geprüft";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public void Initialize()
    {
        var current = settings.Current;
        LmStudioBaseUrl = current.LmStudioBaseUrl;
        SelectedModel = current.SelectedModel;
        ReasoningEffort = current.ReasoningEffort;
        Theme = current.Theme;
        Language = current.Language;
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(LmStudioBaseUrl.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("Die LM-Studio-Adresse muss eine gültige HTTP- oder HTTPS-URL sein.");
        }

        await settings.UpdateAsync(current => current with
        {
            LmStudioBaseUrl = uri.ToString().TrimEnd('/'),
            SelectedModel = string.IsNullOrWhiteSpace(SelectedModel) ? null : SelectedModel,
            ReasoningEffort = ReasoningEffort,
            Theme = Theme,
            Language = Language,
        }, cancellationToken);
        App.Current.ApplyTheme(Theme);
        shell.LmStudioStatus = SelectedModel ?? "Nicht verbunden";
    }

    public async Task RefreshModelsAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            await SaveAsync(cancellationToken);
            var items = await lmStudio.ListModelsAsync(cancellationToken);
            Models.Clear();
            foreach (var item in items)
            {
                Models.Add(item);
            }

            if (items.Count == 1)
            {
                SelectedModel = items[0].Id;
                await SaveAsync(cancellationToken);
            }

            ConnectionStatus = items.Count == 0
                ? "Verbunden, aber kein Modell geladen"
                : $"Verbunden · {items.Count} Modell(e) geladen";
            shell.LmStudioStatus = SelectedModel ?? "LM Studio bereit";
        }
        catch
        {
            ConnectionStatus = "LM Studio nicht erreichbar";
            shell.LmStudioStatus = "LM Studio nicht verbunden";
            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task<BackupResult> CreateBackupAsync(string destinationPath, CancellationToken cancellationToken = default) =>
        backups.CreateAsync(destinationPath, cancellationToken);

    public async Task RestoreBackupAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        await backups.ValidateAsync(backupPath, cancellationToken);
        await backups.RestoreAsync(backupPath, cancellationToken);
    }
}
