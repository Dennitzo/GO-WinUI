using GoWinUI.App.Services;
using GoWinUI.App.ViewModels;
using GoWinUI.Core.Contracts;
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
    private readonly IProjectAssetWorkingCopyService _workingCopies;
    private Task _archiveFilterUpdate = Task.CompletedTask;
    private bool _initialized;
    private bool _isWorkingCopySubscribed;
    private bool _ignoreArchiveToggle;
    private int _archiveFilterVersion;

    public ProjectsPage()
    {
        InitializeComponent();
        ViewModel = App.Current.GetService<ProjectsViewModel>();
        _workingCopies = App.Current.GetService<IProjectAssetWorkingCopyService>();
        _logger = App.Current.GetService<ILogger<ProjectsPage>>();
    }

    public ProjectsViewModel ViewModel { get; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SubscribeToWorkingCopyChanges();
        if (_initialized)
        {
            await RunUiActionAsync(() => ViewModel.RefreshAssetsAsync());
            return;
        }

        _initialized = true;
        await RunUiActionAsync(async () =>
        {
            await ViewModel.InitializeAsync();
            SetArchiveToggle(ViewModel.ShowArchived);
            UpdateProjectOverviewState();
            ShowProjectOverview();
        });
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_isWorkingCopySubscribed)
        {
            _workingCopies.AssetSynchronized -= OnAssetSynchronized;
            _isWorkingCopySubscribed = false;
        }
    }

    private void SubscribeToWorkingCopyChanges()
    {
        if (!_isWorkingCopySubscribed)
        {
            _workingCopies.AssetSynchronized += OnAssetSynchronized;
            _isWorkingCopySubscribed = true;
        }
    }

    private void OnAssetSynchronized(object? sender, ProjectAssetSynchronizedEventArgs args)
    {
        if (!_initialized)
        {
            return;
        }

        _ = DispatcherQueue.TryEnqueue(async () =>
            await RunUiActionAsync(() => ViewModel.ApplySynchronizedAssetAsync(args.Asset)));
    }

    private async void OnCreateProject(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async () =>
        {
            await ViewModel.CreateAsync();
            SetArchiveToggle(false);
            UpdateProjectOverviewState();
            ShowProjectDetails();
        });
    }

    private async void OnArchiveFilterChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _ignoreArchiveToggle)
        {
            return;
        }

        var showArchived = ArchivedToggle.IsOn;
        var version = Interlocked.Increment(ref _archiveFilterVersion);
        var precedingUpdate = _archiveFilterUpdate;
        _archiveFilterUpdate = ApplyArchiveFilterAsync(precedingUpdate, showArchived, version);
        await _archiveFilterUpdate;
    }

    private async Task ApplyArchiveFilterAsync(Task precedingUpdate, bool showArchived, int version)
    {
        try
        {
            await precedingUpdate;
        }
        catch
        {
            // RunUiActionAsync reports UI failures. A failed predecessor must not block later toggles.
        }

        if (version != Volatile.Read(ref _archiveFilterVersion))
        {
            return;
        }

        await RunUiActionAsync(async () =>
        {
            await ViewModel.ReloadProjectsAsync(showArchived);
            if (version != Volatile.Read(ref _archiveFilterVersion))
            {
                return;
            }

            UpdateProjectOverviewState();
            ShowProjectOverview();
        });
    }

    private async void OnProjectItemClick(object sender, ItemClickEventArgs e)
    {
        if (!_initialized || e.ClickedItem is not Project project)
        {
            return;
        }

        await RunUiActionAsync(async () =>
        {
            await ViewModel.SelectAsync(project);
            ShowProjectDetails();
        });
    }

    private async void OnSaveProject(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async () =>
        {
            await ViewModel.SaveAsync();
            ShowProjectDetails();
        });
    }

    private async void OnToggleArchive(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async () =>
        {
            await ViewModel.ToggleArchiveAsync();
            UpdateProjectOverviewState();
            ShowProjectOverview();
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
                UpdateProjectOverviewState();
                ShowProjectOverview();
            });
        }
    }

    private void OnBackToProjects(object sender, RoutedEventArgs e) => ShowProjectOverview();

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
            var category = Enum.TryParse<AssetCategory>(
                (sender as FrameworkElement)?.Tag as string,
                out var requestedCategory)
                ? requestedCategory
                : AssetCategory.Other;
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                ViewMode = category == AssetCategory.Image ? PickerViewMode.Thumbnail : PickerViewMode.List,
            };
            foreach (var extension in FileTypeFilters(category))
            {
                picker.FileTypeFilter.Add(extension);
            }
            InitializePicker(picker);
            var files = await picker.PickMultipleFilesAsync();
            if (files.Count == 0)
            {
                return;
            }

            foreach (var file in files)
            {
                await using var content = await file.OpenStreamForReadAsync();
                await ViewModel.ImportAssetAsync(
                    file.Name,
                    string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                    file.Path,
                    category,
                    content);
            }
        });
    }

    private async void OnAssetItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ProjectAsset asset)
        {
            return;
        }

        await RunUiActionAsync(() => OpenProjectAssetAsync(asset));
    }

    private async Task OpenProjectAssetAsync(ProjectAsset asset)
    {
        FileOpeningMessage.Text = $"„{asset.FileName}“ wird im Standardprogramm geöffnet …";
        FileOpeningOverlay.Visibility = Visibility.Visible;
        var minimumDisplay = Task.Delay(TimeSpan.FromSeconds(2));
        try
        {
            var workingCopy = await ViewModel.MaterializeAssetForOpenAsync(asset);
            var file = await StorageFile.GetFileFromPathAsync(workingCopy.Path);
            if (!await Launcher.LaunchFileAsync(file))
            {
                throw new InvalidOperationException("Für diesen Dateityp ist kein Standardprogramm registriert.");
            }

            await minimumDisplay;
        }
        catch
        {
            await minimumDisplay;
            throw;
        }
        finally
        {
            FileOpeningOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnDeleteAsset(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ProjectAsset asset })
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
            await RunUiActionAsync(() => ViewModel.DeleteAssetAsync(asset));
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

            var bitmap = await LoadBitmapAsync(stream);
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

    private static async Task<BitmapImage> LoadBitmapAsync(Stream source)
    {
        using var randomAccess = new InMemoryRandomAccessStream();
        using var output = randomAccess.AsStreamForWrite();
        await source.CopyToAsync(output);
        await output.FlushAsync();
        randomAccess.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(randomAccess);
        return bitmap;
    }

    private void OnAssetFormatIconLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FontIcon { DataContext: ProjectAsset asset } icon)
        {
            icon.Glyph = AssetIconGlyph(asset);
            ToolTipService.SetToolTip(icon, GetAssetFormatLabel(asset));
        }
    }

    private void OnAssetFormatBadgeLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock { DataContext: ProjectAsset asset } badge)
        {
            badge.Text = GetAssetFormatLabel(asset);
        }
    }

    private void OnAssetTypeTextLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock { DataContext: ProjectAsset asset } text)
        {
            text.Text = FriendlyAssetType(asset);
        }
    }

    private void OnAssetSizeTextLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock { DataContext: ProjectAsset asset } text)
        {
            text.Text = FormatBytes(asset.Length);
        }
    }

    private void OnAssetModifiedTextLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock { DataContext: ProjectAsset asset } text)
        {
            text.Text = $"Geändert {asset.UpdatedAt.ToLocalTime():dd.MM.yyyy, HH:mm}";
        }
    }

    private void UpdateProjectOverviewState()
    {
        var hasProjects = ViewModel.Projects.Count > 0;
        ProjectList.Visibility = hasProjects ? Visibility.Visible : Visibility.Collapsed;
        ProjectEmptyState.Visibility = hasProjects ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetArchiveToggle(bool isOn)
    {
        _ignoreArchiveToggle = true;
        try
        {
            ArchivedToggle.IsOn = isOn;
        }
        finally
        {
            _ignoreArchiveToggle = false;
        }
    }

    private void ShowProjectOverview()
    {
        ProjectMasterPane.Visibility = Visibility.Visible;
        ProjectDetailsPane.Visibility = Visibility.Collapsed;
        PageTitleText.Text = ViewModel.ShowArchived ? "Archivierte Projekte" : "Projekte";
        PageDescriptionText.Text = "Projektinformationen, Checklisten und Dateien übersichtlich verwalten.";
        ProjectMasterPane.ChangeView(null, 0, null, disableAnimation: true);
    }

    private void ShowProjectDetails()
    {
        if (ViewModel.SelectedProject is not { } project)
        {
            ShowProjectOverview();
            return;
        }

        ProjectMasterPane.Visibility = Visibility.Collapsed;
        ProjectDetailsPane.Visibility = Visibility.Visible;
        PageTitleText.Text = project.Name;
        PageDescriptionText.Text = string.IsNullOrWhiteSpace(project.ConstructionProject)
            ? "Projektinformationen, Checklisten und Dateien vollständig verwalten."
            : $"{project.ConstructionProject} · Projektinformationen, Checklisten und Dateien vollständig verwalten.";
        ProjectDetailsPane.ChangeView(null, 0, null, disableAnimation: true);
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

    private static IReadOnlyList<string> FileTypeFilters(AssetCategory category) => category switch
    {
        AssetCategory.Pdf => [".pdf"],
        AssetCategory.Drawing => [".dwg", ".dxf", ".dwt", ".rvt", ".rfa", ".ifc"],
        AssetCategory.Image => [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff"],
        AssetCategory.Meeting => [".doc", ".docx", ".odt", ".pdf", ".txt", ".md", ".rtf", ".xlsx", ".csv"],
        _ => ["*"],
    };

    private static string GetAssetFormatLabel(ProjectAsset asset)
    {
        var extension = Path.GetExtension(asset.FileName).TrimStart('.').ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(extension))
        {
            return extension.Length <= 5 ? extension : extension[..5];
        }

        return asset.Category switch
        {
            AssetCategory.Pdf => "PDF",
            AssetCategory.Drawing => "CAD",
            AssetCategory.Image => "IMG",
            AssetCategory.Meeting => "DOC",
            _ => "FILE",
        };
    }

    private static string AssetIconGlyph(ProjectAsset asset)
    {
        var extension = Path.GetExtension(asset.FileName).ToLowerInvariant();
        return extension switch
        {
            ".pdf" => "\uEA90",
            ".dwg" or ".dxf" or ".dwt" or ".rvt" or ".rfa" or ".ifc" => "\uE7C3",
            ".doc" or ".docx" or ".odt" or ".rtf" => "\uE8A5",
            ".xls" or ".xlsx" or ".csv" => "\uE9F9",
            ".ppt" or ".pptx" => "\uE8A5",
            ".zip" or ".7z" or ".rar" => "\uF012",
            _ when asset.Category == AssetCategory.Image => "\uEB9F",
            _ when asset.Category == AssetCategory.Drawing => "\uE7C3",
            _ => "\uE8A5",
        };
    }

    private static string FriendlyAssetType(ProjectAsset asset)
    {
        var format = GetAssetFormatLabel(asset);
        return asset.Category switch
        {
            AssetCategory.Pdf => $"{format}-Bauplan",
            AssetCategory.Drawing => $"{format}-Bauzeichnung",
            AssetCategory.Meeting => $"{format}-Besprechungsdatei",
            AssetCategory.Image => $"{format}-Bild",
            _ => $"{format}-Datei",
        };
    }

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
