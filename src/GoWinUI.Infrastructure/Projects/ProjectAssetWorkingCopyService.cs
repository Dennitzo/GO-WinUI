using System.Security.Cryptography;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;

namespace GoWinUI.Infrastructure.Projects;

public sealed class ProjectAssetWorkingCopyService(
    IBinaryObjectStore binaryObjects,
    IProjectRepository projects,
    GoInfrastructureOptions options) : IProjectAssetWorkingCopyService, IDisposable
{
    private const int BufferSize = 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<AssetWorkingCopy> InspectAsync(
        ProjectAsset asset,
        CancellationToken cancellationToken = default)
    {
        var path = ResolveExistingPath(asset) ?? GetExpectedPath(asset);
        if (!File.Exists(path))
        {
            return new(asset.Id, path, AssetWorkingCopyState.Missing, null, 0);
        }

        var (sha256, length) = await HashFileAsync(path, cancellationToken).ConfigureAwait(false);
        return new(
            asset.Id,
            path,
            string.Equals(sha256, asset.Sha256, StringComparison.OrdinalIgnoreCase)
                ? AssetWorkingCopyState.Unchanged
                : AssetWorkingCopyState.Modified,
            sha256,
            length);
    }

    public async Task<AssetWorkingCopy> MaterializeAsync(
        ProjectAsset asset,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await InspectAsync(asset, cancellationToken).ConfigureAwait(false);
            if (existing.State != AssetWorkingCopyState.Missing)
            {
                return await MoveUnchangedCopyToExpectedPathAsync(asset, existing, cancellationToken).ConfigureAwait(false);
            }

            return await ExportAuthoritativeCopyAsync(asset, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProjectAsset> ReimportAsync(
        ProjectAsset asset,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var workingCopy = await InspectAsync(asset, cancellationToken).ConfigureAwait(false);
            if (workingCopy.State == AssetWorkingCopyState.Missing)
            {
                throw new FileNotFoundException("Die Arbeitskopie wurde nicht gefunden.", workingCopy.Path);
            }

            if (workingCopy.State == AssetWorkingCopyState.Unchanged)
            {
                return asset;
            }

            await using var source = new FileStream(
                workingCopy.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var blob = await binaryObjects.ImportAsync(source, asset.ContentType, cancellationToken).ConfigureAwait(false);
            try
            {
                var updated = await projects.UpdateAssetAsync(asset with
                {
                    BlobId = blob.Id,
                    Sha256 = blob.Sha256,
                    Length = blob.Length,
                }, expectedRevision, cancellationToken).ConfigureAwait(false);
                await binaryObjects.DeleteIfUnreferencedAsync(asset.BlobId, CancellationToken.None).ConfigureAwait(false);
                return updated;
            }
            catch
            {
                await binaryObjects.DeleteIfUnreferencedAsync(blob.Id, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AssetWorkingCopy> DiscardChangesAsync(
        ProjectAsset asset,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = GetAssetDirectory(asset.Id);
            if (Directory.Exists(directory))
            {
                foreach (var path in Directory.EnumerateFiles(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Delete(path);
                }
            }

            return await ExportAuthoritativeCopyAsync(asset, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = GetAssetDirectory(assetId);
            if (!Directory.Exists(directory))
            {
                return;
            }

            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Delete(path);
            }

            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AssetWorkingCopy> ExportAuthoritativeCopyAsync(
        ProjectAsset asset,
        CancellationToken cancellationToken)
    {
        var destination = GetExpectedPath(asset);
        var directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("Ungültiger Arbeitskopiepfad.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                await binaryObjects.ExportAsync(asset.BlobId, output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var (sha256, length) = await HashFileAsync(temporary, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(sha256, asset.Sha256, StringComparison.OrdinalIgnoreCase)
                || length != asset.Length)
            {
                throw new InvalidDataException("Die materialisierte Arbeitskopie stimmt nicht mit dem gespeicherten Asset überein.");
            }

            File.Move(temporary, destination, overwrite: true);
            return new(asset.Id, destination, AssetWorkingCopyState.Unchanged, sha256, length);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
                // The operating system can remove a briefly locked temp file later.
            }
        }
    }

    private async Task<AssetWorkingCopy> MoveUnchangedCopyToExpectedPathAsync(
        ProjectAsset asset,
        AssetWorkingCopy existing,
        CancellationToken cancellationToken)
    {
        var expected = GetExpectedPath(asset);
        if (existing.State != AssetWorkingCopyState.Unchanged
            || string.Equals(existing.Path, expected, StringComparison.OrdinalIgnoreCase))
        {
            return existing;
        }

        cancellationToken.ThrowIfCancellationRequested();
        File.Move(existing.Path, expected, overwrite: true);
        return existing with { Path = expected };
    }

    private string? ResolveExistingPath(ProjectAsset asset)
    {
        var expected = GetExpectedPath(asset);
        if (File.Exists(expected))
        {
            return expected;
        }

        var directory = GetAssetDirectory(asset.Id);
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "working-copy*", SearchOption.TopDirectoryOnly)
                .Order(StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault()
            : null;
    }

    private string GetExpectedPath(ProjectAsset asset) =>
        Path.Combine(GetAssetDirectory(asset.Id), $"working-copy{GetSafeExtension(asset.FileName)}");

    private string GetAssetDirectory(Guid assetId)
    {
        var cacheRoot = Path.GetFullPath(Path.Combine(options.DataDirectory, "Cache", "Projects"));
        var directory = Path.GetFullPath(Path.Combine(cacheRoot, assetId.ToString("N")));
        if (!directory.StartsWith(cacheRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Der Arbeitskopiepfad liegt außerhalb des GO-Caches.");
        }

        return directory;
    }

    private static string GetSafeExtension(string fileName)
    {
        var extension = Path.GetExtension(Path.GetFileName(fileName));
        return extension.Length is > 0 and <= 16
               && extension.Skip(1).All(char.IsLetterOrDigit)
            ? extension.ToLowerInvariant()
            : ".bin";
    }

    private static async Task<(string Sha256, long Length)> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return (Convert.ToHexStringLower(hash), stream.Length);
    }

    public void Dispose() => _gate.Dispose();
}
