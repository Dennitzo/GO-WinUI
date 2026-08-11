using GoWinUI.App.ViewModels;
using GoWinUI.App.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.Specialized;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace GoWinUI.App.Pages;

public sealed partial class LogsPage : Page
{
    private readonly ILogger<LogsPage> _logger;
    private bool _subscribed;

    public LogsPage()
    {
        InitializeComponent();
        ViewModel = App.Current.GetService<LogsViewModel>();
        _logger = App.Current.GetService<ILogger<LogsPage>>();
        Unloaded += OnUnloaded;
    }

    public LogsViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Initialize(DispatcherQueue);
        if (!_subscribed)
        {
            _subscribed = true;
            ViewModel.Entries.CollectionChanged += OnEntriesChanged;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed)
        {
            _subscribed = false;
            ViewModel.Entries.CollectionChanged -= OnEntriesChanged;
        }
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => ViewModel.Refresh();

    private void OnLevelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LevelFilter.SelectedItem is string level)
        {
            ViewModel.MinimumLevel = level;
            ViewModel.Refresh();
        }
    }

    private void OnClear(object sender, RoutedEventArgs e) => ViewModel.Clear();

    private void OnCopySelected(object sender, RoutedEventArgs e)
    {
        if (LogList.SelectedItem is not GoWinUI.Core.Models.SessionLogEntry entry)
        {
            return;
        }

        var text = $"{entry.Timestamp:O}\t{entry.Level}\t{entry.Category}\t{entry.Message}";
        if (!string.IsNullOrWhiteSpace(entry.Exception))
        {
            text += $"{Environment.NewLine}{entry.Exception}";
        }

        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    private async void OnExportText(object sender, RoutedEventArgs e) => await ExportAsync(asJson: false);

    private async void OnExportJson(object sender, RoutedEventArgs e) => await ExportAsync(asJson: true);

    private async Task ExportAsync(bool asJson)
    {
        try
        {
            ErrorBar.IsOpen = false;
            var extension = asJson ? ".json" : ".log";
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"GO-Logs-{DateTime.Now:yyyy-MM-dd-HHmmss}",
                DefaultFileExtension = extension,
            };
            picker.FileTypeChoices.Add(asJson ? "JSON" : "Text-Log", new List<string> { extension });
            InitializePicker(picker);
            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            await using var destination = await file.OpenStreamForWriteAsync();
            destination.SetLength(0);
            await ViewModel.ExportAsync(destination, asJson);
        }
        catch (Exception exception)
        {
            AppLog.LogExportFailed(_logger, exception);
            ErrorBar.Message = exception.Message;
            ErrorBar.IsOpen = true;
        }
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (ViewModel.AutoScroll && LogList.Items.Count > 0)
        {
            LogList.ScrollIntoView(LogList.Items[^1]);
        }
    }

    private static void InitializePicker(object picker)
    {
        var window = App.Current.MainWindow
            ?? throw new InvalidOperationException("Das Hauptfenster ist nicht verfügbar.");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
    }
}
