using GoWinUI.App.Services;
using GoWinUI.App.ViewModels;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace GoWinUI.Tests;

public sealed class ProjectsViewModelTests
{
    [Fact]
    public async Task MetadataEditingAndMoveActionsRefreshTheObservableState()
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

        await viewModel.MoveChecklistItemAsync(viewModel.Checklist.Single(item => item.Id == secondItem.Id), -1);
        Assert.Equal([secondItem.Id, firstItem.Id], viewModel.Checklist.Select(static item => item.Id));

        viewModel.SelectedAsset = viewModel.Assets.Single(asset => asset.Id == secondAsset.Id);
        viewModel.AssetFileName = "  umbenannt.bin  ";
        viewModel.SelectedAssetCategory = AssetCategory.Drawing;
        await viewModel.UpdateAssetMetadataAsync();

        Assert.Equal("umbenannt.bin", viewModel.SelectedAsset!.FileName);
        Assert.Equal(AssetCategory.Drawing, viewModel.SelectedAsset.Category);
        Assert.Equal("umbenannt.bin", viewModel.AssetFileName);
        await viewModel.MoveAssetAsync(viewModel.SelectedAsset, -1);
        Assert.Equal([secondAsset.Id, firstAsset.Id], viewModel.Assets.Select(static asset => asset.Id));
        Assert.Equal(secondAsset.Id, viewModel.SelectedAsset!.Id);
    }

    [Fact]
    public async Task WorkingCopyActionsExposeModificationReimportAndDiscardState()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var repository = environment.Get<IProjectRepository>();
        var blobs = environment.Get<IBinaryObjectStore>();
        var workingCopies = environment.Get<IProjectAssetWorkingCopyService>();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var project = await CreateProjectAsync(repository);
        await settings.UpdateAsync(current => current with { ActiveProjectId = project.Id });
        var asset = await AddAssetAsync(repository, blobs, project.Id, "arbeitskopie.bin", 0, 1);
        var thumbnails = new ProjectAssetThumbnailService(
            repository,
            blobs,
            workingCopies,
            NullLogger<ProjectAssetThumbnailService>.Instance);
        var viewModel = new ProjectsViewModel(repository, blobs, workingCopies, thumbnails, settings);
        await viewModel.InitializeAsync();
        viewModel.SelectedAsset = viewModel.Assets.Single(item => item.Id == asset.Id);

        var copy = await viewModel.MaterializeAssetAsync(viewModel.SelectedAsset!);
        await File.WriteAllBytesAsync(copy.Path, [4, 5, 6]);
        await viewModel.InspectWorkingCopyAsync(viewModel.SelectedAsset!);
        Assert.True(viewModel.HasModifiedWorkingCopy);

        await viewModel.ReimportWorkingCopyAsync(viewModel.SelectedAsset!);
        Assert.False(viewModel.HasModifiedWorkingCopy);
        Assert.Equal(new byte[] { 4, 5, 6 }, await ReadAssetAsync(viewModel, viewModel.SelectedAsset!));

        await File.WriteAllBytesAsync(copy.Path, [9]);
        await viewModel.InspectWorkingCopyAsync(viewModel.SelectedAsset!);
        Assert.True(viewModel.HasModifiedWorkingCopy);
        await viewModel.DiscardWorkingCopyChangesAsync(viewModel.SelectedAsset!);
        Assert.False(viewModel.HasModifiedWorkingCopy);
        Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(copy.Path));
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

    private static async Task<byte[]> ReadAssetAsync(ProjectsViewModel viewModel, ProjectAsset asset)
    {
        await using var stream = await viewModel.OpenAssetAsync(asset);
        using var destination = new MemoryStream();
        await stream.CopyToAsync(destination);
        return destination.ToArray();
    }
}
