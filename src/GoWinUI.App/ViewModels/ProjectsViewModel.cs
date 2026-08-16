using CommunityToolkit.Mvvm.ComponentModel;
using GoWinUI.App.Services;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using System.Collections.ObjectModel;

namespace GoWinUI.App.ViewModels;

public sealed partial class ProjectsViewModel(
    IProjectRepository projects,
    IBinaryObjectStore binaryObjects,
    IProjectAssetWorkingCopyService workingCopies,
    ProjectAssetThumbnailService thumbnails,
    SettingsCoordinator settings) : ObservableObject
{
    public ObservableCollection<Project> Projects { get; } = [];
    public ObservableCollection<ChecklistItem> Checklist { get; } = [];
    public ObservableCollection<ProjectAsset> Assets { get; } = [];
    public ObservableCollection<ProjectAsset> ConstructionPlans { get; } = [];
    public ObservableCollection<ProjectAsset> ConstructionDrawings { get; } = [];
    public ObservableCollection<ProjectAsset> MeetingFiles { get; } = [];
    public ObservableCollection<ProjectAsset> OtherFiles { get; } = [];
    public ObservableCollection<ProjectAsset> Images { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(IsArchived))]
    [NotifyPropertyChangedFor(nameof(ArchiveActionLabel))]
    public partial Project? SelectedProject { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ConstructionProject { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Notes { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewChecklistText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowArchived { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    public bool HasSelection => SelectedProject is not null;

    public bool IsArchived => SelectedProject?.Status == ProjectStatus.Archived;

    public string ArchiveActionLabel => IsArchived ? "Wiederherstellen" : "Archivieren";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await ReloadProjectsAsync(cancellationToken);
        var selected = settings.Current.ActiveProjectId is { } id
            ? Projects.FirstOrDefault(project => project.Id == id)
            : Projects.FirstOrDefault();
        await SelectAsync(selected, cancellationToken);
    }

    public Task ReloadProjectsAsync(CancellationToken cancellationToken = default) =>
        ReloadProjectsAsync(ShowArchived, cancellationToken);

    public async Task ReloadProjectsAsync(bool showArchived, CancellationToken cancellationToken = default)
    {
        await RunBusyAsync(async () =>
        {
            ShowArchived = showArchived;
            var status = showArchived ? ProjectStatus.Archived : ProjectStatus.Active;
            var items = await projects.ListAsync(status, cancellationToken);
            Replace(Projects, items);
            if (SelectedProject is not null && Projects.All(project => project.Id != SelectedProject.Id))
            {
                await SelectAsync(Projects.FirstOrDefault(), cancellationToken);
            }
        });
    }

    public async Task SelectAsync(Project? project, CancellationToken cancellationToken = default)
    {
        SelectedProject = project;
        Name = project?.Name ?? string.Empty;
        ConstructionProject = project?.ConstructionProject ?? string.Empty;
        Description = project?.Description ?? string.Empty;
        Notes = project?.Notes ?? string.Empty;
        Checklist.Clear();
        ReplaceAssets([]);
        if (project is not null)
        {
            var checklist = await projects.ListChecklistAsync(project.Id, cancellationToken);
            var assets = await projects.ListAssetsAsync(project.Id, cancellationToken);
            Replace(Checklist, checklist);
            ReplaceAssets(assets);
        }

        await settings.UpdateAsync(current => current with { ActiveProjectId = project?.Id }, cancellationToken);
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsArchived));
        OnPropertyChanged(nameof(ArchiveActionLabel));
    }

    public async Task CreateAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var project = await projects.CreateAsync(new Project(
            Guid.NewGuid(),
            "Neues Projekt",
            string.Empty,
            string.Empty,
            string.Empty,
            ProjectStatus.Active,
            0,
            now,
            now), cancellationToken);
        ShowArchived = false;
        await ReloadProjectsAsync(cancellationToken);
        await SelectAsync(Projects.FirstOrDefault(item => item.Id == project.Id) ?? project, cancellationToken);
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        var selected = SelectedProject ?? throw new InvalidOperationException("Kein Projekt ausgewählt.");
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new InvalidOperationException("Der Projektname darf nicht leer sein.");
        }

        var updated = await projects.UpdateAsync(selected with
        {
            Name = Name.Trim(),
            ConstructionProject = ConstructionProject.Trim(),
            Description = Description.Trim(),
            Notes = Notes.Trim(),
        }, selected.Revision, cancellationToken);
        SelectedProject = updated;
        await ReloadProjectsAsync(cancellationToken);
        await SelectAsync(Projects.FirstOrDefault(item => item.Id == updated.Id) ?? updated, cancellationToken);
    }

    public async Task ToggleArchiveAsync(CancellationToken cancellationToken = default)
    {
        var selected = SelectedProject ?? throw new InvalidOperationException("Kein Projekt ausgewählt.");
        if (selected.Status == ProjectStatus.Active)
        {
            await projects.ArchiveAsync(selected.Id, selected.Revision, cancellationToken);
        }
        else
        {
            await projects.RestoreAsync(selected.Id, selected.Revision, cancellationToken);
        }

        await ReloadProjectsAsync(cancellationToken);
        await SelectAsync(Projects.FirstOrDefault(), cancellationToken);
    }

    public async Task DeleteProjectAsync(CancellationToken cancellationToken = default)
    {
        var selected = SelectedProject ?? throw new InvalidOperationException("Kein Projekt ausgewählt.");
        foreach (var asset in Assets)
        {
            await workingCopies.RemoveAsync(asset.Id, cancellationToken);
        }

        await projects.DeleteAsync(selected.Id, selected.Revision, cancellationToken);
        await ReloadProjectsAsync(cancellationToken);
        await SelectAsync(Projects.FirstOrDefault(), cancellationToken);
    }

    public async Task AddChecklistItemAsync(CancellationToken cancellationToken = default)
    {
        var project = SelectedProject ?? throw new InvalidOperationException("Kein Projekt ausgewählt.");
        var text = NewChecklistText.Trim();
        if (text.Length == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        await projects.SaveChecklistItemAsync(new ChecklistItem(
            Guid.Empty,
            project.Id,
            text,
            false,
            Checklist.Count,
            0,
            now,
            now), cancellationToken: cancellationToken);
        NewChecklistText = string.Empty;
        Replace(Checklist, await projects.ListChecklistAsync(project.Id, cancellationToken));
    }

    public async Task ToggleChecklistItemAsync(ChecklistItem item, bool completed, CancellationToken cancellationToken = default)
    {
        await projects.SaveChecklistItemAsync(item with { IsCompleted = completed }, item.Revision, cancellationToken);
        if (SelectedProject is { } project)
        {
            Replace(Checklist, await projects.ListChecklistAsync(project.Id, cancellationToken));
        }
    }

    public async Task DeleteChecklistItemAsync(ChecklistItem item, CancellationToken cancellationToken = default)
    {
        await projects.DeleteChecklistItemAsync(item.Id, item.Revision, cancellationToken);
        if (SelectedProject is { } project)
        {
            Replace(Checklist, await projects.ListChecklistAsync(project.Id, cancellationToken));
        }
    }

    public async Task MoveChecklistItemAsync(ChecklistItem item, int direction, CancellationToken cancellationToken = default)
    {
        var project = SelectedProject ?? throw new InvalidOperationException("Kein Projekt ausgewählt.");
        if (item.ProjectId != project.Id)
        {
            throw new InvalidOperationException("Der Checklistenpunkt gehört nicht zum ausgewählten Projekt.");
        }

        await projects.MoveChecklistItemAsync(project.Id, item.Id, direction, item.Revision, cancellationToken);
        Replace(Checklist, await projects.ListChecklistAsync(project.Id, cancellationToken));
    }

    public async Task ImportAssetAsync(
        string fileName,
        string contentType,
        string? sourcePath,
        AssetCategory category,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        var project = SelectedProject ?? throw new InvalidOperationException("Kein Projekt ausgewählt.");
        fileName = ProjectAssetFileName.Normalize(fileName);
        var blob = await binaryObjects.ImportAsync(content, contentType, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        ProjectAsset created;
        try
        {
            created = await projects.AddAssetAsync(new ProjectAsset(
                Guid.Empty,
                project.Id,
                blob.Id,
                fileName,
                contentType,
                category,
                sourcePath,
                blob.Sha256,
                blob.Length,
                Assets.Count,
                0,
                now,
                now), cancellationToken);
        }
        catch
        {
            await binaryObjects.DeleteIfUnreferencedAsync(blob.Id, CancellationToken.None);
            throw;
        }

        await thumbnails.GenerateAsync(created, cancellationToken);
        await RefreshAssetsAsync(cancellationToken);
    }

    public Task<AssetWorkingCopy> MaterializeAssetForOpenAsync(
        ProjectAsset asset,
        CancellationToken cancellationToken = default) =>
        workingCopies.MaterializeAndWatchAsync(asset, cancellationToken);

    public async Task<Stream?> OpenThumbnailAsync(
        ProjectAsset asset,
        CancellationToken cancellationToken = default)
    {
        var thumbnail = await thumbnails.OpenOrGenerateAsync(asset, cancellationToken);
        if (thumbnail is not null || asset.Category != AssetCategory.Image)
        {
            return thumbnail;
        }

        // Match Barebone-Qt's fallback: a usable original image is preferable to
        // a generic icon when thumbnail generation is unavailable for a format.
        return await binaryObjects.OpenReadAsync(asset.BlobId, cancellationToken);
    }

    public async Task ApplySynchronizedAssetAsync(
        ProjectAsset asset,
        CancellationToken cancellationToken = default)
    {
        await thumbnails.GenerateAsync(asset, cancellationToken);
        if (SelectedProject?.Id == asset.ProjectId)
        {
            if (!TryReplaceAsset(asset))
            {
                await RefreshAssetsAsync(cancellationToken);
            }
        }
    }

    public async Task RefreshAssetsAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedProject is not { } project)
        {
            return;
        }

        ReplaceAssets(await projects.ListAssetsAsync(project.Id, cancellationToken));
    }

    public async Task DeleteAssetAsync(ProjectAsset asset, CancellationToken cancellationToken = default)
    {
        await workingCopies.RemoveAsync(asset.Id, cancellationToken);
        await projects.DeleteAssetAsync(asset.Id, asset.Revision, cancellationToken);
        if (SelectedProject is { } project)
        {
            ReplaceAssets(await projects.ListAssetsAsync(project.Id, cancellationToken));
        }

    }

    public async Task SaveAssetTitleAsync(
        ProjectAsset asset,
        string? title,
        CancellationToken cancellationToken = default)
    {
        title = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(title?.Length ?? 0, 200);
        if (string.Equals(asset.Title, title, StringComparison.Ordinal))
        {
            return;
        }

        var candidate = asset;
        ProjectAsset updated;
        try
        {
            updated = await projects.UpdateAssetAsync(
                candidate with { Title = title },
                candidate.Revision,
                cancellationToken);
        }
        catch (RevisionConflictException)
        {
            candidate = (await projects.ListAssetsAsync(asset.ProjectId, cancellationToken))
                .FirstOrDefault(item => item.Id == asset.Id)
                ?? throw new InvalidOperationException("Die Projektdatei wurde zwischenzeitlich gelöscht.");
            updated = string.Equals(candidate.Title, title, StringComparison.Ordinal)
                ? candidate
                : await projects.UpdateAssetAsync(
                    candidate with { Title = title },
                    candidate.Revision,
                    cancellationToken);
        }
        if (!TryReplaceAsset(updated))
        {
            await RefreshAssetsAsync(cancellationToken);
        }
    }

    private bool TryReplaceAsset(ProjectAsset asset)
    {
        if (!TryReplaceAsset(Assets, asset))
        {
            return false;
        }

        var categoryAssets = asset.Category switch
        {
            AssetCategory.Pdf => ConstructionPlans,
            AssetCategory.Drawing => ConstructionDrawings,
            AssetCategory.Meeting => MeetingFiles,
            AssetCategory.Image => Images,
            _ => OtherFiles,
        };
        return TryReplaceAsset(categoryAssets, asset);
    }

    private static bool TryReplaceAsset(
        ObservableCollection<ProjectAsset> assets,
        ProjectAsset replacement)
    {
        for (var index = 0; index < assets.Count; index++)
        {
            if (assets[index].Id == replacement.Id)
            {
                assets[index] = replacement;
                return true;
            }
        }

        return false;
    }

    private void ReplaceAssets(IEnumerable<ProjectAsset> items)
    {
        var snapshot = items.ToArray();
        Replace(Assets, snapshot);
        Replace(ConstructionPlans, snapshot.Where(static asset => asset.Category == AssetCategory.Pdf));
        Replace(ConstructionDrawings, snapshot.Where(static asset => asset.Category == AssetCategory.Drawing));
        Replace(MeetingFiles, snapshot.Where(static asset => asset.Category == AssetCategory.Meeting));
        Replace(OtherFiles, snapshot.Where(static asset => asset.Category == AssetCategory.Other));
        Replace(Images, snapshot.Where(static asset => asset.Category == AssetCategory.Image));
    }

    private async Task RunBusyAsync(Func<Task> operation)
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await operation();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items)
        {
            collection.Add(item);
        }
    }
}
