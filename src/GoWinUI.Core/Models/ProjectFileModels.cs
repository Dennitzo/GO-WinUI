namespace GoWinUI.Core.Models;

public enum AssetWorkingCopyState
{
    Missing,
    Unchanged,
    Modified,
}

public sealed record AssetWorkingCopy(
    Guid AssetId,
    string Path,
    AssetWorkingCopyState State,
    string? CurrentSha256,
    long Length);

public sealed class ProjectAssetSynchronizedEventArgs(ProjectAsset asset, string path) : EventArgs
{
    public ProjectAsset Asset { get; } = asset;

    public string Path { get; } = path;
}
