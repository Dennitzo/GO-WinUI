using GoWinUI.App.ViewModels;
using GoWinUI.App.Services;
using GoWinUI.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppLifecycle;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace GoWinUI.App.Pages;

public sealed partial class SettingsPage : Page
{
    private readonly ILogger<SettingsPage> _logger;
    private bool _synchronizing;

    public SettingsPage()
    {
        InitializeComponent();
        ViewModel = App.Current.GetService<SettingsViewModel>();
        _logger = App.Current.GetService<ILogger<SettingsPage>>();
    }

    public SettingsViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _synchronizing = true;
        try
        {
            ViewModel.Initialize();
            SelectByTag(ReasoningBox, ViewModel.ReasoningEffort);
            SelectByTag(ThemeBox, ViewModel.Theme.ToString());
            SelectByTag(LanguageBox, ViewModel.Language);
            ModelBox.SelectedItem = ViewModel.Models.FirstOrDefault(model => model.Id == ViewModel.SelectedModel);
        }
        finally
        {
            _synchronizing = false;
        }
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        await RunActionAsync(async () =>
        {
            await ViewModel.SaveAsync();
            ShowStatus("Einstellungen gespeichert.", InfoBarSeverity.Success);
        });
    }

    private async void OnRefreshModels(object sender, RoutedEventArgs e)
    {
        await RunActionAsync(async () =>
        {
            await ViewModel.RefreshModelsAsync();
            _synchronizing = true;
            try
            {
                ModelBox.SelectedItem = ViewModel.Models.FirstOrDefault(model => model.Id == ViewModel.SelectedModel);
            }
            finally
            {
                _synchronizing = false;
            }

            ShowStatus(ViewModel.ConnectionStatus, InfoBarSeverity.Success);
        });
    }

    private void OnModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_synchronizing && ModelBox.SelectedItem is LmModel model)
        {
            ViewModel.SelectedModel = model.Id;
        }
    }

    private void OnReasoningChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_synchronizing && (ReasoningBox.SelectedItem as ComboBoxItem)?.Tag is string value)
        {
            ViewModel.ReasoningEffort = value;
        }
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_synchronizing
            && (ThemeBox.SelectedItem as ComboBoxItem)?.Tag is string value
            && Enum.TryParse<AppTheme>(value, out var theme))
        {
            ViewModel.Theme = theme;
            App.Current.ApplyTheme(theme);
        }
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_synchronizing && (LanguageBox.SelectedItem as ComboBoxItem)?.Tag is string value)
        {
            ViewModel.Language = value;
        }
    }

    private async void OnCreateBackup(object sender, RoutedEventArgs e)
    {
        await RunActionAsync(async () =>
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"GO-Backup-{DateTime.Now:yyyy-MM-dd-HHmm}",
                DefaultFileExtension = ".gobackup",
            };
            picker.FileTypeChoices.Add("GO-Backup", new List<string> { ".gobackup" });
            InitializePicker(picker);
            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            var result = await ViewModel.CreateBackupAsync(file.Path);
            ShowStatus($"Backup erstellt · SHA-256 {result.Sha256[..12]}…", InfoBarSeverity.Success);
        });
    }

    private async void OnRestoreBackup(object sender, RoutedEventArgs e)
    {
        var warning = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Backup wiederherstellen?",
            Content = "Der aktuelle Zustand wird zuerst gesichert und anschließend durch das Backup ersetzt. GO muss danach neu gestartet werden.",
            PrimaryButtonText = "Wiederherstellen",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await warning.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await RunActionAsync(async () =>
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                ViewMode = PickerViewMode.List,
            };
            picker.FileTypeFilter.Add(".gobackup");
            InitializePicker(picker);
            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            await ViewModel.RestoreBackupAsync(file.Path);
            ShowStatus("Backup wiederhergestellt. GO wird neu gestartet.", InfoBarSeverity.Success);
            _ = AppInstance.Restart("--restored-backup");
        });
    }

    private async Task RunActionAsync(Func<Task> action)
    {
        try
        {
            StatusBar.IsOpen = false;
            await action();
        }
        catch (Exception exception)
        {
            AppLog.SettingsActionFailed(_logger, exception);
            ShowStatus(exception.Message, InfoBarSeverity.Error);
        }
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }

    private static void SelectByTag(ComboBox comboBox, string value)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, value, StringComparison.OrdinalIgnoreCase));
    }

    private static void InitializePicker(object picker)
    {
        var window = App.Current.MainWindow
            ?? throw new InvalidOperationException("Das Hauptfenster ist nicht verfügbar.");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
    }
}
