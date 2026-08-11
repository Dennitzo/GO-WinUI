using GoWinUI.App.Services;
using GoWinUI.App.ViewModels;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace GoWinUI.Tests;

public sealed class ProjectsViewModelTests
{
    [Fact]
    public async Task ExplicitArchiveFilterCanSwitchRepeatedlyWithoutLosingProjects()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var repository = environment.Get<IProjectRepository>();
        var blobs = environment.Get<IBinaryObjectStore>();
        var workingCopies = environment.Get<IProjectAssetWorkingCopyService>();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var active = await CreateProjectAsync(repository);
        var archived = await CreateProjectAsync(repository);
        await repository.ArchiveAsync(archived.Id, archived.Revision);
        await settings.UpdateAsync(current => current with { ActiveProjectId = active.Id });
        var thumbnails = new ProjectAssetThumbnailService(
            repository,
            blobs,
            workingCopies,
            NullLogger<ProjectAssetThumbnailService>.Instance);
        var viewModel = new ProjectsViewModel(repository, blobs, workingCopies, thumbnails, settings);

        await viewModel.InitializeAsync();
        Assert.False(viewModel.ShowArchived);
        Assert.Equal([active.Id], viewModel.Projects.Select(static project => project.Id));

        for (var iteration = 0; iteration < 4; iteration++)
        {
            await viewModel.ReloadProjectsAsync(showArchived: true);
            Assert.True(viewModel.ShowArchived);
            Assert.Equal([archived.Id], viewModel.Projects.Select(static project => project.Id));

            await viewModel.ReloadProjectsAsync(showArchived: false);
            Assert.False(viewModel.ShowArchived);
            Assert.Equal([active.Id], viewModel.Projects.Select(static project => project.Id));
        }
    }

    [Fact]
    public async Task ChecklistMoveAndAssetDeletionRefreshTheObservableState()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var repository = environment.Get<IProjectRepository>();
        var blobs = environment.Get<IBinaryObjectStore>();
        var workingCopies = environment.Get<IProjectAssetWorkingCopyService>();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var project = await CreateProjectAsync(repository);
        await settings.UpdateAsync(current => current with { ActiveProjectId = project.Id });
        var now = DateTimeOffset.UtcNow;
        var firstItem = await repository.SaveChecklistItemAsync(new(
            Guid.Empty, project.Id, "Erster Punkt", false, 0, 0, now, now));
        var secondItem = await repository.SaveChecklistItemAsync(new(
            Guid.Empty, project.Id, "Zweiter Punkt", false, 1, 0, now, now));
        var firstAsset = await AddAssetAsync(repository, blobs, project.Id, "eins.bin", 0, 1);
        var secondAsset = await AddAssetAsync(repository, blobs, project.Id, "zwei.bin", 1, 2);
        var thumbnails = new ProjectAssetThumbnailService(
            repository,
            blobs,
            workingCopies,
            NullLogger<ProjectAssetThumbnailService>.Instance);
        var viewModel = new ProjectsViewModel(repository, blobs, workingCopies, thumbnails, settings);
        await viewModel.InitializeAsync();

        Assert.Equal([firstAsset.Id, secondAsset.Id], viewModel.OtherFiles.Select(static asset => asset.Id));
        Assert.Empty(viewModel.ConstructionDrawings);

        await viewModel.MoveChecklistItemAsync(viewModel.Checklist.Single(item => item.Id == secondItem.Id), -1);
        Assert.Equal([secondItem.Id, firstItem.Id], viewModel.Checklist.Select(static item => item.Id));

        await viewModel.DeleteAssetAsync(secondAsset);
        Assert.Equal([firstAsset.Id], viewModel.Assets.Select(static asset => asset.Id));
        Assert.Equal([firstAsset.Id], viewModel.OtherFiles.Select(static asset => asset.Id));
        Assert.Empty(viewModel.ConstructionDrawings);
    }

    private static Task<Project> CreateProjectAsync(IProjectRepository repository)
    {
        var now = DateTimeOffset.UtcNow;
        return repository.CreateAsync(new(
            Guid.Empty,
            "ViewModel",
            string.Empty,
            string.Empty,
            string.Empty,
            ProjectStatus.Active,
            0,
            now,
            now));
    }

    private static async Task<ProjectAsset> AddAssetAsync(
        IProjectRepository repository,
        IBinaryObjectStore blobs,
        Guid projectId,
        string fileName,
        int sortOrder,
        byte content)
    {
        var now = DateTimeOffset.UtcNow;
        var blob = await blobs.ImportAsync(new MemoryStream([content]), "application/octet-stream");
        return await repository.AddAssetAsync(new(
            Guid.Empty,
            projectId,
            blob.Id,
            fileName,
            "application/octet-stream",
            AssetCategory.Other,
            null,
            blob.Sha256,
            blob.Length,
            sortOrder,
            0,
            now,
            now));
    }

}
