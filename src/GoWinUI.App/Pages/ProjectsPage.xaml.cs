using GoWinUI.App.Services;
using GoWinUI.App.ViewModels;
using GoWinUI.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using WinRT.Interop;

namespace GoWinUI.App.Pages;

public sealed partial class ProjectsPage : Page
{
    private readonly ILogger<ProjectsPage> _logger;
    private bool _initialized;
    private bool _isWindowActivationSubscribed;

    public ProjectsPage()
    {
        InitializeComponent();
        ViewModel = App.Current.GetService<ProjectsViewModel>();
        _logger = App.Current.GetService<ILogger<ProjectsPage>>();
    }

    public ProjectsViewModel ViewModel { get; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SubscribeToWindowActivation();
        if (_initialized)
        {
            if (ViewModel.SelectedAsset is { } selected)
            {
                await RunUiActionAsync(() => ViewModel.InspectWorkingCopyAsync(selected));
            }

            return;
        }

        _initialized = true;
        await RunUiActionAsync(async () =>
        {
            await ViewModel.InitializeAsync();
            ProjectList.SelectedItem = ViewModel.SelectedProject;
        });
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_isWindowActivationSubscribed && App.Current.MainWindow is { } window)
        {
            window.Activated -= OnWindowActivated;
            _isWindowActivationSubscribed = false;
        }
    }

    private void SubscribeToWindowActivation()
    {
        if (!_isWindowActivationSubscribed && App.Current.MainWindow is { } window)
        {
            window.Activated += OnWindowActivated;
            _isWindowActivationSubscribed = true;
        }
    }

    private async void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated
            || !_initialized
            || ViewModel.SelectedAsset is not { } asset)
        {
            return;
        }

        await RunUiActionAsync(() => ViewModel.InspectWorkingCopyAsync(asset));
    }

    private async void OnCreateProject(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async () =>
        {
            await ViewModel.CreateAsync();
            ProjectList.SelectedItem = ViewModel.SelectedProject;
        });
    }

    private async void OnArchiveFilterChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        await RunUiActionAsync(async () =>
        {
            await ViewModel.ReloadProjectsAsync();
            ProjectList.SelectedItem = ViewModel.SelectedProject;
        });
    }

    private async void OnProjectSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || Equals(ProjectList.SelectedItem, ViewModel.SelectedProject))
        {
            return;
        }

        await RunUiActionAsync(async () =>
        {
            await ViewModel.SelectAsync(ProjectList.SelectedItem as Project);
            AssetList.SelectedItem = null;
            UpdateAssetSummary(null);
            ResetPreview("Vorschau für Bilder, Text und PDF");
        });
    }

    private async void OnSaveProject(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync(() => ViewModel.SaveAsync());

    private async void OnToggleArchive(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async () =>
        {
            await ViewModel.ToggleArchiveAsync();
            ProjectList.SelectedItem = ViewModel.SelectedProject;
        });
    }

    private async void OnDeleteProject(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedProject is not { } project)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Projekt endgültig löschen?",
            Content = $"„{project.Name}“ sowie alle Checklisten, gespeicherten Dateien und Arbeitskopien werden entfernt.",
            PrimaryButtonText = "Löschen",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await RunUiActionAsync(async () =>
            {
                await ViewModel.DeleteProjectAsync();
                ProjectList.SelectedItem = ViewModel.SelectedProject;
            });
        }
    }

    private async void OnAddChecklistItem(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync(() => ViewModel.AddChecklistItemAsync());

    private async void OnChecklistClicked(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: ChecklistItem item, IsChecked: { } completed })
        {
            await RunUiActionAsync(() => ViewModel.ToggleChecklistItemAsync(item, completed));
        }
    }

    private async void OnMoveChecklistItemUp(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ChecklistItem item })
        {
            await RunUiActionAsync(() => ViewModel.MoveChecklistItemAsync(item, -1));
        }
    }

    private async void OnMoveChecklistItemDown(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ChecklistItem item })
        {
            await RunUiActionAsync(() => ViewModel.MoveChecklistItemAsync(item, 1));
        }
    }

    private async void OnDeleteChecklistItem(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ChecklistItem item })
        {
            await RunUiActionAsync(() => ViewModel.DeleteChecklistItemAsync(item));
        }
    }

    private async void OnImportAsset(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedProject is null)
        {
            return;
        }

        await RunUiActionAsync(async () =>
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                ViewMode = PickerViewMode.List,
            };
            picker.FileTypeFilter.Add("*");
            InitializePicker(picker);
            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            var category = Enum.TryParse<AssetCategory>(
                (AssetCategoryBox.SelectedItem as ComboBoxItem)?.Tag as string,
                out var parsed)
                ? parsed
                : InferCategory(file.FileType);
            await using var content = await file.OpenStreamForReadAsync();
            await ViewModel.ImportAssetAsync(
                file.Name,
                string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                file.Path,
                category,
                content);
            AssetList.SelectedItem = ViewModel.SelectedAsset;
        });
    }

    private async void OnAssetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = AssetList.SelectedItem as ProjectAsset;
        if (ViewModel.SelectedAsset?.Id != selected?.Id)
        {
            ViewModel.SelectedAsset = selected;
        }

        UpdateAssetSummary(selected);
        if (selected is null)
        {
            ResetPreview("Vorschau für Bilder, Text und PDF");
            return;
        }

        await RunUiActionAsync(async () =>
        {
            await ViewModel.InspectWorkingCopyAsync(selected);
            await PreviewAssetAsync(selected);
        });
    }

    private async void OnSaveAssetMetadata(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async () =>
        {
            await ViewModel.UpdateAssetMetadataAsync();
            await RefreshSelectedAssetAsync();
        });
    }

    private async void OnMoveAssetUp(object sender, RoutedEventArgs e) =>
        await MoveSelectedAssetAsync(-1);

    private async void OnMoveAssetDown(object sender, RoutedEventArgs e) =>
        await MoveSelectedAssetAsync(1);

    private async Task MoveSelectedAssetAsync(int direction)
    {
        if (ViewModel.SelectedAsset is not { } asset)
        {
            return;
        }

        await RunUiActionAsync(async () =>
        {
            await ViewModel.MoveAssetAsync(asset, direction);
            AssetList.SelectedItem = ViewModel.SelectedAsset;
        });
    }

    private async void OnOpenAsset(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedAsset is not { } selected)
        {
            return;
        }

        await RunUiActionAsync(async () =>
        {
            var asset = await ResolveModifiedWorkingCopyBeforeOpenAsync(selected);
            if (asset is null)
            {
                return;
            }

            var workingCopy = await ViewModel.MaterializeAssetAsync(asset);
            var file = await StorageFile.GetFileFromPathAsync(workingCopy.Path);
            if (!await Launcher.LaunchFileAsync(file))
            {
                throw new InvalidOperationException("Für diesen Dateityp ist kein Programm registriert.");
            }
        });
    }

    private async Task<ProjectAsset?> ResolveModifiedWorkingCopyBeforeOpenAsync(ProjectAsset asset)
    {
        var state = await ViewModel.InspectWorkingCopyAsync(asset);
        if (state.State != AssetWorkingCopyState.Modified)
        {
            return asset;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Geänderte Arbeitskopie gefunden",
            Content = "Vor dem erneuten Öffnen muss entschieden werden, ob die externe Änderung in GO übernommen oder verworfen wird.",
            PrimaryButtonText = "Übernehmen",
            SecondaryButtonText = "Verwerfen",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Close,
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await ViewModel.ReimportWorkingCopyAsync(asset);
        }
        else if (result == ContentDialogResult.Secondary)
        {
            await ViewModel.DiscardWorkingCopyChangesAsync(asset);
        }
        else
        {
            return null;
        }

        await RefreshSelectedAssetAsync();
        return ViewModel.SelectedAsset;
    }

    private async void OnReimportWorkingCopy(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedAsset is not { } asset)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Externe Änderungen übernehmen?",
            Content = "Die gespeicherte Projektdatei wird durch den aktuellen Inhalt der Arbeitskopie ersetzt.",
            PrimaryButtonText = "Übernehmen",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await RunUiActionAsync(async () =>
            {
                await ViewModel.ReimportWorkingCopyAsync(asset);
                await RefreshSelectedAssetAsync();
            });
        }
    }

    private async void OnDiscardWorkingCopy(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedAsset is not { } asset)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Externe Änderungen verwerfen?",
            Content = "Die Arbeitskopie wird wieder aus dem in GO gespeicherten Stand hergestellt. Die externe Änderung geht verloren.",
            PrimaryButtonText = "Verwerfen",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await RunUiActionAsync(async () =>
            {
                await ViewModel.DiscardWorkingCopyChangesAsync(asset);
                await RefreshSelectedAssetAsync();
            });
        }
    }

    private async void OnExportAsset(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedAsset is not { } asset)
        {
            return;
        }

        await RunUiActionAsync(async () =>
        {
            var extension = Path.GetExtension(asset.FileName);
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = Path.GetFileNameWithoutExtension(asset.FileName),
                DefaultFileExtension = string.IsNullOrWhiteSpace(extension) ? ".bin" : extension,
            };
            picker.FileTypeChoices.Add(
                "Projektdatei",
                new List<string> { string.IsNullOrWhiteSpace(extension) ? ".bin" : extension });
            InitializePicker(picker);
            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            await using var destination = await file.OpenStreamForWriteAsync();
            destination.SetLength(0);
            await ViewModel.ExportAssetAsync(asset, destination);
        });
    }

    private async void OnDeleteAsset(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedAsset is not { } asset)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Projektdatei löschen?",
            Content = $"„{asset.FileName}“ und eine eventuell geänderte Arbeitskopie werden entfernt.",
            PrimaryButtonText = "Löschen",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await RunUiActionAsync(async () =>
            {
                await ViewModel.DeleteAssetAsync(asset);
                UpdateAssetSummary(null);
                ResetPreview("Vorschau für Bilder, Text und PDF");
            });
        }
    }

    private async void OnAssetThumbnailLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image { DataContext: ProjectAsset asset } image)
        {
            return;
        }

        try
        {
            await using var stream = await ViewModel.OpenThumbnailAsync(asset);
            if (stream is null)
            {
                image.Source = null;
                return;
            }

            using var randomAccess = new InMemoryRandomAccessStream();
            await stream.CopyToAsync(randomAccess.AsStreamForWrite());
            randomAccess.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(randomAccess);
            if (image.DataContext is ProjectAsset current && current.Id == asset.Id)
            {
                image.Source = bitmap;
            }
        }
        catch (Exception exception)
        {
            AppLog.AssetThumbnailLoadingFailed(_logger, exception, asset.Id);
            image.Source = null;
        }
    }

    private async Task RefreshSelectedAssetAsync()
    {
        AssetList.SelectedItem = ViewModel.SelectedAsset;
        UpdateAssetSummary(ViewModel.SelectedAsset);
        if (ViewModel.SelectedAsset is { } selected)
        {
            await PreviewAssetAsync(selected);
        }
    }

    private async Task PreviewAssetAsync(ProjectAsset asset)
    {
        ResetPreview("Vorschau wird geladen …");
        var extension = Path.GetExtension(asset.FileName).ToLowerInvariant();
        if (asset.Category == AssetCategory.Image
            || extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp")
        {
            await using var source = await ViewModel.OpenAssetAsync(asset);
            using var randomAccess = new InMemoryRandomAccessStream();
            await source.CopyToAsync(randomAccess.AsStreamForWrite());
            randomAccess.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(randomAccess);
            ImagePreview.Source = bitmap;
            ImagePreview.Visibility = Visibility.Visible;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            return;
        }

        if (asset.Category == AssetCategory.Pdf || extension == ".pdf")
        {
            var workingCopy = await ViewModel.MaterializeAssetAsync(asset);
            await PdfPreview.EnsureCoreWebView2Async();
            PdfPreview.CoreWebView2.Settings.AreDevToolsEnabled = false;
            PdfPreview.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            PdfPreview.CoreWebView2.Settings.IsWebMessageEnabled = false;
            PdfPreview.Source = new Uri(workingCopy.Path);
            PdfPreview.Visibility = Visibility.Visible;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            return;
        }

        if (IsTextPreview(extension, asset.ContentType))
        {
            await using var stream = await ViewModel.OpenAssetAsync(asset);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
            var buffer = new char[1_000_000];
            var length = await reader.ReadBlockAsync(buffer);
            TextPreviewContent.Text = new string(buffer, 0, length)
                + (length == buffer.Length ? Environment.NewLine + "[Vorschau nach 1.000.000 Zeichen gekürzt]" : string.Empty);
            TextPreview.Visibility = Visibility.Visible;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            return;
        }

        ResetPreview("Für diesen Dateityp gibt es keine interne Vorschau. Nutze „Öffnen“ mit dem registrierten Windows-Programm.");
    }

    private void UpdateAssetSummary(ProjectAsset? asset)
    {
        PreviewSummary.Text = asset is null
            ? "Datei auswählen, um Aktionen anzuzeigen."
            : $"{asset.FileName} · {FormatBytes(asset.Length)} · SHA-256 {asset.Sha256[..Math.Min(12, asset.Sha256.Length)]}…";
    }

    private void ResetPreview(string message)
    {
        ImagePreview.Source = null;
        ImagePreview.Visibility = Visibility.Collapsed;
        TextPreview.Visibility = Visibility.Collapsed;
        TextPreviewContent.Text = string.Empty;
        PdfPreview.Visibility = Visibility.Collapsed;
        if (PdfPreview.CoreWebView2 is not null)
        {
            PdfPreview.Source = null;
        }

        PreviewPlaceholder.Text = message;
        PreviewPlaceholder.Visibility = Visibility.Visible;
    }

    private async Task RunUiActionAsync(Func<Task> action)
    {
        try
        {
            ErrorBar.IsOpen = false;
            await action();
        }
        catch (Exception exception)
        {
            AppLog.ProjectActionFailed(_logger, exception);
            ErrorBar.Message = exception.Message;
            ErrorBar.IsOpen = true;
        }
    }

    private static AssetCategory InferCategory(string extension) => extension.ToLowerInvariant() switch
    {
        ".pdf" => AssetCategory.Pdf,
        ".dwg" or ".rvt" => AssetCategory.Drawing,
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" => AssetCategory.Image,
        ".doc" or ".docx" or ".odt" or ".txt" or ".md" or ".rtf" or ".log" or ".xml" or ".json" => AssetCategory.Meeting,
        _ => AssetCategory.Other,
    };

    private static bool IsTextPreview(string extension, string contentType) =>
        contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || extension is ".txt" or ".md" or ".csv" or ".json" or ".xml" or ".html" or ".htm"
            or ".rtf" or ".log" or ".ini" or ".yaml" or ".yml" or ".css" or ".js" or ".ts"
            or ".py" or ".cpp" or ".c" or ".h" or ".hpp" or ".cs" or ".java" or ".sql";

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0, (double)bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    private static void InitializePicker(object picker)
    {
        var window = App.Current.MainWindow
            ?? throw new InvalidOperationException("Das Hauptfenster ist nicht verfügbar.");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
    }
}
