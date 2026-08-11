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
    public IReadOnlyList<AssetCategory> AssetCategories { get; } = Enum.GetValues<AssetCategory>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(IsArchived))]
    [NotifyPropertyChangedFor(nameof(ArchiveActionLabel))]
    public partial Project? SelectedProject { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAssetSelection))]
    public partial ProjectAsset? SelectedAsset { get; set; }

    [ObservableProperty]
    public partial string AssetFileName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial AssetCategory SelectedAssetCategory { get; set; } = AssetCategory.Other;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModifiedWorkingCopy))]
    [NotifyPropertyChangedFor(nameof(WorkingCopyStatusText))]
    public partial AssetWorkingCopy? WorkingCopy { get; set; }

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

    public bool HasAssetSelection => SelectedAsset is not null;

    public bool IsArchived => SelectedProject?.Status == ProjectStatus.Archived;

    public string ArchiveActionLabel => IsArchived ? "Wiederherstellen" : "Archivieren";

    public bool HasModifiedWorkingCopy => WorkingCopy?.State == AssetWorkingCopyState.Modified;

    public string WorkingCopyStatusText => WorkingCopy?.State switch
    {
        AssetWorkingCopyState.Modified => "Die Arbeitskopie wurde außerhalb von GO geändert. Übernehmen oder verwerfen Sie die Änderungen.",
        AssetWorkingCopyState.Unchanged => "Die lokale Arbeitskopie stimmt mit dem gespeicherten Stand überein.",
        _ => "Für dieses Asset wurde noch keine Arbeitskopie angelegt.",
    };

    partial void OnSelectedAssetChanged(ProjectAsset? value)
    {
        AssetFileName = value?.FileName ?? string.Empty;
        SelectedAssetCategory = value?.Category ?? AssetCategory.Other;
        WorkingCopy = null;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await ReloadProjectsAsync(cancellationToken);
        var selected = settings.Current.ActiveProjectId is { } id
            ? Projects.FirstOrDefault(project => project.Id == id)
            : Projects.FirstOrDefault();
        await SelectAsync(selected, cancellationToken);
    }

    public async Task ReloadProjectsAsync(CancellationToken cancellationToken = default)
    {
        await RunBusyAsync(async () =>
        {
            var status = ShowArchived ? ProjectStatus.Archived : ProjectStatus.Active;
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
        SelectedAsset = null;
        Name = project?.Name ?? string.Empty;
        ConstructionProject = project?.ConstructionProject ?? string.Empty;
        Description = project?.Description ?? string.Empty;
        Notes = project?.Notes ?? string.Empty;
        Checklist.Clear();
        Assets.Clear();
        if (project is not null)
        {
            var checklist = await projects.ListChecklistAsync(project.Id, cancellationToken);
            var assets = await projects.ListAssetsAsync(project.Id, cancellationToken);
            Replace(Checklist, checklist);
            Replace(Assets, assets);
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
        await RefreshAssetsAsync(created.Id, cancellationToken);
    }

    public Task<Stream> OpenAssetAsync(ProjectAsset asset, CancellationToken cancellationToken = default) =>
        binaryObjects.OpenReadAsync(asset.BlobId, cancellationToken);

    public Task ExportAssetAsync(ProjectAsset asset, Stream destination, CancellationToken cancellationToken = default) =>
        binaryObjects.ExportAsync(asset.BlobId, destination, cancellationToken);

    public async Task<AssetWorkingCopy> InspectWorkingCopyAsync(
        ProjectAsset asset,
        CancellationToken cancellationToken = default)
    {
        var state = await workingCopies.InspectAsync(asset, cancellationToken);
        if (SelectedAsset?.Id == asset.Id)
        {
            WorkingCopy = state;
        }

        return state;
    }

    public async Task<AssetWorkingCopy> MaterializeAssetAsync(
        ProjectAsset asset,
        CancellationToken cancellationToken = default)
    {
        var state = await workingCopies.MaterializeAsync(asset, cancellationToken);
        if (SelectedAsset?.Id == asset.Id)
        {
            WorkingCopy = state;
        }

        return state;
    }

    public async Task UpdateAssetMetadataAsync(CancellationToken cancellationToken = default)
    {
        var asset = SelectedAsset ?? throw new InvalidOperationException("Keine Projektdatei ausgewählt.");
        var fileName = ProjectAssetFileName.Normalize(AssetFileName);
        var previousCopy = await workingCopies.InspectAsync(asset, cancellationToken);
        var updated = await projects.UpdateAssetAsync(asset with
        {
            FileName = fileName,
            Category = SelectedAssetCategory,
        }, asset.Revision, cancellationToken);
        await RefreshAssetsAsync(updated.Id, cancellationToken);
        WorkingCopy = previousCopy.State == AssetWorkingCopyState.Missing
            ? await workingCopies.InspectAsync(updated, cancellationToken)
            : await workingCopies.MaterializeAsync(updated, cancellationToken);
    }

    public async Task MoveAssetAsync(ProjectAsset asset, int direction, CancellationToken cancellationToken = default)
    {
        var project = SelectedProject ?? throw new InvalidOperationException("Kein Projekt ausgewählt.");
        if (asset.ProjectId != project.Id)
        {
            throw new InvalidOperationException("Das Asset gehört nicht zum ausgewählten Projekt.");
        }

        var workingCopy = WorkingCopy;
        await projects.MoveAssetAsync(project.Id, asset.Id, direction, asset.Revision, cancellationToken);
        await RefreshAssetsAsync(asset.Id, cancellationToken);
        WorkingCopy = workingCopy;
    }

    public async Task ReimportWorkingCopyAsync(ProjectAsset asset, CancellationToken cancellationToken = default)
    {
        var updated = await workingCopies.ReimportAsync(asset, asset.Revision, cancellationToken);
        await thumbnails.GenerateAsync(updated, cancellationToken);
        await RefreshAssetsAsync(updated.Id, cancellationToken);
        WorkingCopy = await workingCopies.InspectAsync(updated, cancellationToken);
    }

    public async Task DiscardWorkingCopyChangesAsync(ProjectAsset asset, CancellationToken cancellationToken = default)
    {
        WorkingCopy = await workingCopies.DiscardChangesAsync(asset, cancellationToken);
    }

    public Task<Stream?> OpenThumbnailAsync(ProjectAsset asset, CancellationToken cancellationToken = default) =>
        thumbnails.OpenAsync(asset.Id, cancellationToken);

    public async Task DeleteAssetAsync(ProjectAsset asset, CancellationToken cancellationToken = default)
    {
        await workingCopies.RemoveAsync(asset.Id, cancellationToken);
        await projects.DeleteAssetAsync(asset.Id, asset.Revision, cancellationToken);
        if (SelectedProject is { } project)
        {
            Replace(Assets, await projects.ListAssetsAsync(project.Id, cancellationToken));
        }

        SelectedAsset = null;
    }

    private async Task RefreshAssetsAsync(Guid selectedAssetId, CancellationToken cancellationToken)
    {
        if (SelectedProject is not { } project)
        {
            return;
        }

        Replace(Assets, await projects.ListAssetsAsync(project.Id, cancellationToken));
        SelectedAsset = Assets.FirstOrDefault(asset => asset.Id == selectedAssetId);
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
