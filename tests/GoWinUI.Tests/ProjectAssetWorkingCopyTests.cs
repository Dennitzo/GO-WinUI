using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;

namespace GoWinUI.Tests;

public sealed class ProjectAssetWorkingCopyTests
{
    [Fact]
    public async Task ReimportDetectsChangesInvalidatesThumbnailAndCleansOldBlobs()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var repository = environment.Get<IProjectRepository>();
        var blobs = environment.Get<IBinaryObjectStore>();
        var workingCopies = environment.Get<IProjectAssetWorkingCopyService>();
        var asset = await CreateAssetAsync(repository, blobs, [1, 2, 3]);
        var thumbnailBlob = await blobs.ImportAsync(new MemoryStream([9, 8, 7]), "image/png");
        await repository.SaveAssetThumbnailAsync(
            new(asset.Id, thumbnailBlob.Id, "image/png", 32, 24, DateTimeOffset.UtcNow));

        Assert.Equal(AssetWorkingCopyState.Missing, (await workingCopies.InspectAsync(asset)).State);
        var materialized = await workingCopies.MaterializeAsync(asset);
        Assert.Equal(AssetWorkingCopyState.Unchanged, materialized.State);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(materialized.Path));

        await File.WriteAllBytesAsync(materialized.Path, [4, 5, 6, 7]);
        Assert.Equal(AssetWorkingCopyState.Modified, (await workingCopies.InspectAsync(asset)).State);

        var updated = await workingCopies.ReimportAsync(asset, asset.Revision);

        Assert.Equal(asset.Revision + 1, updated.Revision);
        Assert.Equal(4, updated.Length);
        Assert.NotEqual(asset.Sha256, updated.Sha256);
        Assert.Null(await repository.GetAssetThumbnailAsync(asset.Id));
        Assert.Equal(AssetWorkingCopyState.Unchanged, (await workingCopies.InspectAsync(updated)).State);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => blobs.OpenReadAsync(asset.BlobId));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => blobs.OpenReadAsync(thumbnailBlob.Id));

        await File.WriteAllBytesAsync(materialized.Path, [0, 0]);
        var discarded = await workingCopies.DiscardChangesAsync(updated);
        Assert.Equal(AssetWorkingCopyState.Unchanged, discarded.State);
        Assert.Equal(new byte[] { 4, 5, 6, 7 }, await File.ReadAllBytesAsync(discarded.Path));

        await workingCopies.RemoveAsync(asset.Id);
        Assert.Equal(AssetWorkingCopyState.Missing, (await workingCopies.InspectAsync(updated)).State);
    }

    [Fact]
    public async Task MaterializeNeverOverwritesAnExternallyModifiedCopy()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var repository = environment.Get<IProjectRepository>();
        var blobs = environment.Get<IBinaryObjectStore>();
        var workingCopies = environment.Get<IProjectAssetWorkingCopyService>();
        var asset = await CreateAssetAsync(repository, blobs, [1, 2, 3]);
        var workingCopy = await workingCopies.MaterializeAsync(asset);
        await File.WriteAllBytesAsync(workingCopy.Path, [7, 7, 7]);

        var secondMaterialization = await workingCopies.MaterializeAsync(asset);

        Assert.Equal(AssetWorkingCopyState.Modified, secondMaterialization.State);
        Assert.Equal(new byte[] { 7, 7, 7 }, await File.ReadAllBytesAsync(secondMaterialization.Path));
        await Assert.ThrowsAsync<RevisionConflictException>(
            () => workingCopies.ReimportAsync(asset, asset.Revision + 1));
        Assert.Equal(new byte[] { 7, 7, 7 }, await File.ReadAllBytesAsync(secondMaterialization.Path));
    }

    [Fact]
    public async Task MaterializedWorkingCopyUsesTheExactOriginalFileName()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var repository = environment.Get<IProjectRepository>();
        var blobs = environment.Get<IBinaryObjectStore>();
        var workingCopies = environment.Get<IProjectAssetWorkingCopyService>();
        var asset = await CreateAssetAsync(repository, blobs, [1, 2, 3], "Zeichnung1.DWG");

        var workingCopy = await workingCopies.MaterializeAsync(asset);

        Assert.Equal(asset.FileName, Path.GetFileName(workingCopy.Path));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(workingCopy.Path));
    }

    [Fact]
    public async Task WatchedWorkingCopyAutomaticallySynchronizesChangesBackToTheDatabase()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var repository = environment.Get<IProjectRepository>();
        var blobs = environment.Get<IBinaryObjectStore>();
        var workingCopies = environment.Get<IProjectAssetWorkingCopyService>();
        var asset = await CreateAssetAsync(repository, blobs, [1, 2, 3]);
        var synchronized = new TaskCompletionSource<ProjectAsset>(TaskCreationOptions.RunContinuationsAsynchronously);
        workingCopies.AssetSynchronized += (_, args) =>
        {
            if (args.Asset.Id == asset.Id)
            {
                synchronized.TrySetResult(args.Asset);
            }
        };

        var workingCopy = await workingCopies.MaterializeAndWatchAsync(asset);
        await File.WriteAllBytesAsync(workingCopy.Path, [8, 7, 6, 5]);

        var updated = await synchronized.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(asset.Revision + 1, updated.Revision);
        Assert.Equal(4, updated.Length);
        Assert.True(updated.UpdatedAt > asset.UpdatedAt);
        Assert.NotEqual(asset.BlobId, updated.BlobId);
        await using var stored = await blobs.OpenReadAsync(updated.BlobId);
        using var buffer = new MemoryStream();
        await stored.CopyToAsync(buffer);
        Assert.Equal(new byte[] { 8, 7, 6, 5 }, buffer.ToArray());
    }

    [Fact]
    public async Task WatchedWorkingCopySynchronizesAfterAnExtendedExclusiveFileLock()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var repository = environment.Get<IProjectRepository>();
        var blobs = environment.Get<IBinaryObjectStore>();
        var workingCopies = environment.Get<IProjectAssetWorkingCopyService>();
        var asset = await CreateAssetAsync(repository, blobs, [1, 2, 3]);
        var synchronized = new TaskCompletionSource<ProjectAsset>(TaskCreationOptions.RunContinuationsAsynchronously);
        workingCopies.AssetSynchronized += (_, args) =>
        {
            if (args.Asset.Id == asset.Id)
            {
                synchronized.TrySetResult(args.Asset);
            }
        };

        var workingCopy = await workingCopies.MaterializeAndWatchAsync(asset);
        await File.WriteAllBytesAsync(workingCopy.Path, [9, 8, 7, 6, 5]);
        await using (var locked = new FileStream(
            workingCopy.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None))
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
        }

        var updated = await synchronized.Task.WaitAsync(TimeSpan.FromSeconds(12));
        Assert.Equal(asset.Revision + 1, updated.Revision);
        Assert.Equal(5, updated.Length);
        Assert.True(updated.UpdatedAt > asset.UpdatedAt);
    }

    [Theory]
    [InlineData("  plan final.pdf  ", "plan final.pdf")]
    [InlineData("Foto_01.png", "Foto_01.png")]
    public void AssetFileNameNormalizationTrimsValidWindowsNames(string input, string expected) =>
        Assert.Equal(expected, ProjectAssetFileName.Normalize(input));

    [Theory]
    [InlineData("../plan.pdf")]
    [InlineData("plan?.pdf")]
    [InlineData("CON.txt")]
    [InlineData("datei. ")]
    public void AssetFileNameNormalizationRejectsUnsafeWindowsNames(string input) =>
        Assert.Throws<ArgumentException>(() => ProjectAssetFileName.Normalize(input));

    private static async Task<ProjectAsset> CreateAssetAsync(
        IProjectRepository repository,
        IBinaryObjectStore blobs,
        byte[] content,
        string fileName = "modell.bin")
    {
        var now = DateTimeOffset.UtcNow;
        var project = await repository.CreateAsync(new(
            Guid.Empty,
            "Arbeitskopien",
            string.Empty,
            string.Empty,
            string.Empty,
            ProjectStatus.Active,
            0,
            now,
            now));
        var blob = await blobs.ImportAsync(new MemoryStream(content, writable: false), "application/octet-stream");
        return await repository.AddAssetAsync(new(
            Guid.Empty,
            project.Id,
            blob.Id,
            fileName,
            "application/octet-stream",
            AssetCategory.Other,
            null,
            blob.Sha256,
            blob.Length,
            0,
            0,
            now,
            now));
    }
}
