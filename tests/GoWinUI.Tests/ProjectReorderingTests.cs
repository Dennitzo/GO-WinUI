using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;

namespace GoWinUI.Tests;

public sealed class ProjectReorderingTests
{
    [Fact]
    public async Task MoveChecklistItemSwapsNeighboursAndRejectsAStaleRevision()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var repository = environment.Get<IProjectRepository>();
        var project = await CreateProjectAsync(repository);
        var now = DateTimeOffset.UtcNow;
        var first = await repository.SaveChecklistItemAsync(new(Guid.Parse("00000000-0000-0000-0000-000000000001"), project.Id, "A", false, 10, 0, now, now));
        var selected = await repository.SaveChecklistItemAsync(new(Guid.Parse("00000000-0000-0000-0000-000000000002"), project.Id, "B", false, 20, 0, now, now));
        var last = await repository.SaveChecklistItemAsync(new(Guid.Parse("00000000-0000-0000-0000-000000000003"), project.Id, "C", false, 30, 0, now, now));

        await repository.MoveChecklistItemAsync(project.Id, selected.Id, -1, selected.Revision);

        var moved = await repository.ListChecklistAsync(project.Id);
        Assert.Equal([selected.Id, first.Id, last.Id], moved.Select(static item => item.Id));
        Assert.Equal([10, 20, 30], moved.Select(static item => item.SortOrder));
        Assert.Equal(2, moved.Single(item => item.Id == selected.Id).Revision);
        Assert.Equal(2, moved.Single(item => item.Id == first.Id).Revision);
        Assert.Equal(1, moved.Single(item => item.Id == last.Id).Revision);

        await Assert.ThrowsAsync<RevisionConflictException>(
            () => repository.MoveChecklistItemAsync(project.Id, selected.Id, 1, selected.Revision));
        Assert.Equal(moved, await repository.ListChecklistAsync(project.Id));
    }

    [Fact]
    public async Task MoveChecklistItemNormalizesTiesAndBoundaryIsANoOp()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var repository = environment.Get<IProjectRepository>();
        var project = await CreateProjectAsync(repository);
        var now = DateTimeOffset.UtcNow;
        var first = await repository.SaveChecklistItemAsync(new(Guid.Parse("00000000-0000-0000-0000-000000000001"), project.Id, "A", false, 0, 0, now, now));
        var selected = await repository.SaveChecklistItemAsync(new(Guid.Parse("00000000-0000-0000-0000-000000000002"), project.Id, "B", false, 0, 0, now, now));
        var last = await repository.SaveChecklistItemAsync(new(Guid.Parse("00000000-0000-0000-0000-000000000003"), project.Id, "C", false, 2, 0, now, now));

        await repository.MoveChecklistItemAsync(project.Id, selected.Id, -1, selected.Revision);

        var normalized = await repository.ListChecklistAsync(project.Id);
        Assert.Equal([selected.Id, first.Id, last.Id], normalized.Select(static item => item.Id));
        Assert.Equal([0, 1, 2], normalized.Select(static item => item.SortOrder));
        Assert.Equal([2L, 2L, 1L], normalized.Select(static item => item.Revision));

        await repository.MoveChecklistItemAsync(project.Id, selected.Id, -1, normalized[0].Revision);
        Assert.Equal(normalized, await repository.ListChecklistAsync(project.Id));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repository.MoveChecklistItemAsync(project.Id, selected.Id, 0, normalized[0].Revision));
    }

    [Fact]
    public async Task MoveAssetNormalizesTiesAndEnforcesRevisionAndDirection()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var repository = environment.Get<IProjectRepository>();
        var project = await CreateProjectAsync(repository);
        var blobStore = environment.Get<IBinaryObjectStore>();
        var now = DateTimeOffset.UtcNow;
        var first = await AddAssetAsync(repository, blobStore, project.Id, "00000000-0000-0000-0000-000000000001", "a.pdf", 0, 1, now);
        var selected = await AddAssetAsync(repository, blobStore, project.Id, "00000000-0000-0000-0000-000000000002", "b.pdf", 0, 2, now);
        var last = await AddAssetAsync(repository, blobStore, project.Id, "00000000-0000-0000-0000-000000000003", "c.pdf", 2, 3, now);

        await repository.MoveAssetAsync(project.Id, selected.Id, -1, selected.Revision);

        var moved = await repository.ListAssetsAsync(project.Id);
        Assert.Equal([selected.Id, first.Id, last.Id], moved.Select(static asset => asset.Id));
        Assert.Equal([0, 1, 2], moved.Select(static asset => asset.SortOrder));
        Assert.Equal([2L, 2L, 1L], moved.Select(static asset => asset.Revision));
        await Assert.ThrowsAsync<RevisionConflictException>(
            () => repository.MoveAssetAsync(project.Id, selected.Id, 1, selected.Revision));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repository.MoveAssetAsync(project.Id, selected.Id, -2, moved[0].Revision));
        Assert.Equal(moved, await repository.ListAssetsAsync(project.Id));
    }

    private static Task<Project> CreateProjectAsync(IProjectRepository repository)
    {
        var now = DateTimeOffset.UtcNow;
        return repository.CreateAsync(new(Guid.Empty, "Sortierung", string.Empty, string.Empty, string.Empty, ProjectStatus.Active, 0, now, now));
    }

    private static async Task<ProjectAsset> AddAssetAsync(
        IProjectRepository repository,
        IBinaryObjectStore blobStore,
        Guid projectId,
        string id,
        string name,
        int sortOrder,
        byte content,
        DateTimeOffset now)
    {
        var blob = await blobStore.ImportAsync(new MemoryStream([content]), "application/pdf");
        return await repository.AddAssetAsync(new(
            Guid.Parse(id), projectId, blob.Id, name, "application/pdf", AssetCategory.Pdf, null,
            blob.Sha256, blob.Length, sortOrder, 0, now, now));
    }
}
