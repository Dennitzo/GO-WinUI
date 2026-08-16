using GoWinUI.App.Services;
using GoWinUI.App.ViewModels;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
    private readonly RecentActivityService _recentActivity;
    private readonly HashSet<TextBox> _committingAssetTitleEditors = [];
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
        _recentActivity = App.Current.GetService<RecentActivityService>();
        _logger = App.Current.GetService<ILogger<ProjectsPage>>();
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnPagePointerPressed), true);
    }

    public ProjectsViewModel ViewModel { get; }

    private void OnPagePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is not TextBox
            || IsInsideTextBox(e.OriginalSource as DependencyObject))
        {
            return;
        }

        MoveFocusOutsideTextBoxes(FocusState.Pointer);
    }

    private static bool IsInsideTextBox(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is TextBox)
            {
                return true;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    private void MoveFocusOutsideTextBoxes(FocusState focusState)
    {
        if (!ProjectDetailsPane.Focus(focusState))
        {
            System.Diagnostics.Debug.WriteLine("GO could not move focus from the project text input.");
        }
    }

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
            if (ViewModel.SelectedProject is { } project)
            {
                await RecordProjectActivityAsync(project, "erstellt");
            }

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
            await RecordProjectActivityAsync(project, "geöffnet");
            ShowProjectDetails();
        });
    }

    private async void OnSaveProject(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async () =>
        {
            await ViewModel.SaveAsync();
            if (ViewModel.SelectedProject is { } project)
            {
                await RecordProjectActivityAsync(project, "bearbeitet");
            }

            ShowProjectDetails();
        });
    }

    private async void OnToggleArchive(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async () =>
        {
            var project = ViewModel.SelectedProject
                ?? throw new InvalidOperationException("Kein Projekt ausgewählt.");
            var activity = project.Status == ProjectStatus.Active ? "archiviert" : "wiederhergestellt";
            await ViewModel.ToggleArchiveAsync();
            await RecordProjectActivityAsync(project, activity);
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
                await RecordProjectActivityAsync(project, "gelöscht");
                UpdateProjectOverviewState();
                ShowProjectOverview();
            });
        }
    }

    private void OnBackToProjects(object sender, RoutedEventArgs e) => ShowProjectOverview();

    private async void OnAddChecklistItem(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ViewModel.NewChecklistText))
        {
            return;
        }

        await RunUiActionAsync(async () =>
        {
            var project = ViewModel.SelectedProject;
            await ViewModel.AddChecklistItemAsync();
            if (project is not null)
            {
                await _recentActivity.RecordAsync($"Checkliste in Projekt „{project.Name}“ ergänzt");
            }
        });
    }

    private async void OnChecklistClicked(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: ChecklistItem item, IsChecked: { } completed })
        {
            await RunUiActionAsync(async () =>
            {
                var project = ViewModel.SelectedProject;
                await ViewModel.ToggleChecklistItemAsync(item, completed);
                if (project is not null)
                {
                    var activity = completed ? "als erledigt markiert" : "wieder geöffnet";
                    await _recentActivity.RecordAsync(
                        $"Checklistenpunkt in Projekt „{project.Name}“ {activity}");
                }
            });
        }
    }

    private async void OnMoveChecklistItemUp(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ChecklistItem item })
        {
            await RunUiActionAsync(async () =>
            {
                var project = ViewModel.SelectedProject;
                await ViewModel.MoveChecklistItemAsync(item, -1);
                if (project is not null)
                {
                    await _recentActivity.RecordAsync($"Checkliste in Projekt „{project.Name}“ neu sortiert");
                }
            });
        }
    }

    private async void OnMoveChecklistItemDown(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ChecklistItem item })
        {
            await RunUiActionAsync(async () =>
            {
                var project = ViewModel.SelectedProject;
                await ViewModel.MoveChecklistItemAsync(item, 1);
                if (project is not null)
                {
                    await _recentActivity.RecordAsync($"Checkliste in Projekt „{project.Name}“ neu sortiert");
                }
            });
        }
    }

    private async void OnDeleteChecklistItem(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ChecklistItem item })
        {
            await RunUiActionAsync(async () =>
            {
                var project = ViewModel.SelectedProject;
                await ViewModel.DeleteChecklistItemAsync(item);
                if (project is not null)
                {
                    await _recentActivity.RecordAsync($"Checklistenpunkt aus Projekt „{project.Name}“ gelöscht");
                }
            });
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
            var project = ViewModel.SelectedProject
                ?? throw new InvalidOperationException("Kein Projekt ausgewählt.");
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

            var activity = files.Count == 1
                ? $"Datei „{files[0].Name}“ zu Projekt „{project.Name}“ hinzugefügt"
                : $"{files.Count} Dateien zu Projekt „{project.Name}“ hinzugefügt";
            await _recentActivity.RecordAsync(activity);
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

            var projectName = ViewModel.SelectedProject?.Name ?? "Projekt";
            await _recentActivity.RecordAsync($"Datei „{asset.FileName}“ aus „{projectName}“ geöffnet");
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
            await RunUiActionAsync(async () =>
            {
                var projectName = ViewModel.SelectedProject?.Name ?? "Projekt";
                await ViewModel.DeleteAssetAsync(asset);
                await _recentActivity.RecordAsync($"Datei „{asset.FileName}“ aus „{projectName}“ gelöscht");
            });
        }
    }

    private void OnAssetTitleDisplayClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ProjectAsset asset, Parent: Grid host } displayButton
            || FindAssetTitleEditor(host) is not { } editor)
        {
            return;
        }

        editor.Text = asset.Title ?? string.Empty;
        displayButton.Visibility = Visibility.Collapsed;
        editor.Visibility = Visibility.Visible;
        _ = editor.Focus(FocusState.Programmatic);
        editor.SelectAll();
    }

    private async void OnAssetTitleKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || sender is not TextBox textBox)
        {
            return;
        }

        e.Handled = true;
        await CommitAssetTitleEditAsync(textBox, moveFocus: true);
    }

    private async void OnAssetTitleLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { Visibility: Visibility.Visible } textBox)
        {
            await CommitAssetTitleEditAsync(textBox, moveFocus: false);
        }
    }

    private async Task CommitAssetTitleEditAsync(TextBox textBox, bool moveFocus)
    {
        if (!_committingAssetTitleEditors.Add(textBox))
        {
            return;
        }

        try
        {
            if (textBox.DataContext is not ProjectAsset asset)
            {
                return;
            }

            var title = string.IsNullOrWhiteSpace(textBox.Text) ? null : textBox.Text.Trim();
            if (moveFocus)
            {
                MoveFocusOutsideTextBoxes(FocusState.Programmatic);
            }

            if (!string.Equals(asset.Title, title, StringComparison.Ordinal)
                && !await RunUiActionAsync(() => ViewModel.SaveAssetTitleAsync(asset, title)))
            {
                return;
            }

            if (textBox.Parent is Grid host
                && FindAssetTitleDisplayButton(host) is { } displayButton)
            {
                if (displayButton.Content is TextBlock displayText)
                {
                    UpdateAssetTitleDisplay(displayText, title);
                }

                textBox.Text = title ?? string.Empty;
                textBox.Visibility = Visibility.Collapsed;
                displayButton.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            _committingAssetTitleEditors.Remove(textBox);
        }
    }

    private void OnAssetTitleDisplayLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock { DataContext: ProjectAsset asset } text)
        {
            UpdateAssetTitleDisplay(text, asset.Title);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "WinUI XAML event handlers must be instance methods.")]
    private void OnAssetTitleDisplayDataContextChanged(
        FrameworkElement sender,
        DataContextChangedEventArgs args)
    {
        if (sender is TextBlock text && args.NewValue is ProjectAsset asset)
        {
            UpdateAssetTitleDisplay(text, asset.Title);
        }
    }

    private static void UpdateAssetTitleDisplay(TextBlock text, string? title)
    {
        var hasTitle = !string.IsNullOrWhiteSpace(title);
        text.Text = hasTitle ? title!.Trim() : "Überschrift eingeben";
        text.FontWeight = hasTitle ? FontWeights.SemiBold : FontWeights.Normal;
        text.Opacity = hasTitle ? 1 : 0.55;
    }

    private static TextBox? FindAssetTitleEditor(Grid host) =>
        host.Children.OfType<TextBox>().FirstOrDefault(static child => child.Name == "AssetTitleEditor");

    private static Button? FindAssetTitleDisplayButton(Grid host) =>
        host.Children.OfType<Button>().FirstOrDefault(static child => child.Name == "AssetTitleDisplayButton");

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
            UpdateAssetFormatIcon(icon, asset);
        }
    }

    private void OnAssetFormatBadgeLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock { DataContext: ProjectAsset asset } badge)
        {
            UpdateAssetFormatBadge(badge, asset);
        }
    }

    private void OnAssetTypeTextLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock { DataContext: ProjectAsset asset } text)
        {
            UpdateAssetTypeText(text, asset);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "WinUI XAML event handlers must be instance methods.")]
    private void OnAssetFormatIconDataContextChanged(
        FrameworkElement sender,
        DataContextChangedEventArgs args)
    {
        if (sender is FontIcon icon && args.NewValue is ProjectAsset asset)
        {
            UpdateAssetFormatIcon(icon, asset);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "WinUI XAML event handlers must be instance methods.")]
    private void OnAssetFormatBadgeDataContextChanged(
        FrameworkElement sender,
        DataContextChangedEventArgs args)
    {
        if (sender is TextBlock badge && args.NewValue is ProjectAsset asset)
        {
            UpdateAssetFormatBadge(badge, asset);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "WinUI XAML event handlers must be instance methods.")]
    private void OnAssetTypeTextDataContextChanged(
        FrameworkElement sender,
        DataContextChangedEventArgs args)
    {
        if (sender is TextBlock text && args.NewValue is ProjectAsset asset)
        {
            UpdateAssetTypeText(text, asset);
        }
    }

    private static void UpdateAssetFormatIcon(FontIcon icon, ProjectAsset asset)
    {
        icon.Glyph = AssetIconGlyph(asset);
        ToolTipService.SetToolTip(icon, GetAssetFormatLabel(asset));
    }

    private static void UpdateAssetFormatBadge(TextBlock badge, ProjectAsset asset) =>
        badge.Text = GetAssetFormatLabel(asset);

    private static void UpdateAssetTypeText(TextBlock text, ProjectAsset asset) =>
        text.Text = FriendlyAssetType(asset);

    private void UpdateProjectOverviewState()
    {
        var hasProjects = ViewModel.Projects.Count > 0;
        ProjectList.Visibility = hasProjects ? Visibility.Visible : Visibility.Collapsed;
        ProjectEmptyState.Visibility = hasProjects ? Visibility.Collapsed : Visibility.Visible;
    }

    private Task RecordProjectActivityAsync(Project project, string activity) =>
        _recentActivity.RecordAsync($"Projekt „{project.Name}“ {activity}");

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

    private async Task<bool> RunUiActionAsync(Func<Task> action)
    {
        try
        {
            ErrorBar.IsOpen = false;
            await action();
            return true;
        }
        catch (Exception exception)
        {
            AppLog.ProjectActionFailed(_logger, exception);
            ErrorBar.Message = exception.Message;
            ErrorBar.IsOpen = true;
            return false;
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

    private static void InitializePicker(object picker)
    {
        var window = App.Current.MainWindow
            ?? throw new InvalidOperationException("Das Hauptfenster ist nicht verfügbar.");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
    }
}
