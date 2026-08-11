using GoWinUI.Core.Models;

namespace GoWinUI.Core.Contracts;

public interface IProjectAssetWorkingCopyService
{
    Task<AssetWorkingCopy> InspectAsync(
        ProjectAsset asset,
        CancellationToken cancellationToken = default);

    Task<AssetWorkingCopy> MaterializeAsync(
        ProjectAsset asset,
        CancellationToken cancellationToken = default);

    Task<ProjectAsset> ReimportAsync(
        ProjectAsset asset,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    Task<AssetWorkingCopy> DiscardChangesAsync(
        ProjectAsset asset,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(
        Guid assetId,
        CancellationToken cancellationToken = default);
}
