using System.Collections.Concurrent;
using System.Security.Cryptography;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using Microsoft.Extensions.Logging;

namespace GoWinUI.Infrastructure.Projects;

public sealed class ProjectAssetWorkingCopyService(
    IBinaryObjectStore binaryObjects,
    IProjectRepository projects,
    GoInfrastructureOptions options,
    ILogger<ProjectAssetWorkingCopyService> logger) : IProjectAssetWorkingCopyService, IDisposable
{
    private const int BufferSize = 1024 * 1024;
    private static readonly TimeSpan ChangeDebounce = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly Action<ILogger, Guid, Exception?> SynchronizationFailed = LoggerMessage.Define<Guid>(
        LogLevel.Warning,
        new EventId(2300, nameof(SynchronizationFailed)),
        "Automatic synchronization of project asset {AssetId} failed");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, WatchRegistration> _watchedAssets = new();
    private int _disposed;

    public event EventHandler<ProjectAssetSynchronizedEventArgs>? AssetSynchronized;

    public async Task<AssetWorkingCopy> InspectAsync(
        ProjectAsset asset,
        CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(asset);
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
            if (!string.IsNullOrWhiteSpace(asset.SourcePath))
            {
                return existing;
            }
            if (existing.State != AssetWorkingCopyState.Missing)
            {
                return existing;
            }

            return await ExportAuthoritativeCopyAsync(asset, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AssetWorkingCopy> MaterializeAndWatchAsync(
        ProjectAsset asset,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var workingCopy = await MaterializeAsync(asset, cancellationToken).ConfigureAwait(false);
        var registration = new WatchRegistration(asset, workingCopy.Path, QueueSynchronization);
        StopMonitoring(asset.Id);
        if (!_watchedAssets.TryAdd(asset.Id, registration))
        {
            registration.Dispose();
            throw new InvalidOperationException("Die Arbeitskopie konnte nicht überwacht werden.");
        }

        registration.Start();
        if (workingCopy.State == AssetWorkingCopyState.Modified)
        {
            QueueSynchronization(asset.Id);
        }

        return workingCopy;
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
            if (string.IsNullOrWhiteSpace(asset.SourcePath))
            {
                var blob = await binaryObjects.ImportAsync(source, asset.ContentType, cancellationToken).ConfigureAwait(false);
                try
                {
                    var blobUpdated = await projects.UpdateAssetAsync(asset with
                    {
                        BlobId = blob.Id,
                        Sha256 = blob.Sha256,
                        Length = blob.Length,
                    }, expectedRevision, cancellationToken).ConfigureAwait(false);
                    await binaryObjects.DeleteIfUnreferencedAsync(asset.BlobId, CancellationToken.None).ConfigureAwait(false);
                    UpdateWatchedAsset(blobUpdated);
                    return blobUpdated;
                }
                catch
                {
                    await binaryObjects.DeleteIfUnreferencedAsync(blob.Id, CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            }
            var (sha256, length) = await HashFileAsync(workingCopy.Path, cancellationToken).ConfigureAwait(false);
            var updated = await projects.UpdateAssetAsync(asset with
            {
                Sha256 = sha256,
                Length = length,
            }, expectedRevision, cancellationToken).ConfigureAwait(false);
            UpdateWatchedAsset(updated);
            return updated;
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
        StopMonitoring(assetId);
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

    private string GetExpectedPath(ProjectAsset asset) =>
        Path.Combine(GetAssetDirectory(asset.Id), ProjectAssetFileName.Normalize(asset.FileName));

    private string ResolvePath(ProjectAsset asset) =>
        string.IsNullOrWhiteSpace(asset.SourcePath)
            ? GetExpectedPath(asset)
            : Path.GetFullPath(asset.SourcePath);

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

    private void QueueSynchronization(Guid assetId)
    {
        if (Volatile.Read(ref _disposed) != 0
            || !_watchedAssets.TryGetValue(assetId, out var registration))
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        CancellationTokenSource? previous;
        lock (registration.SyncRoot)
        {
            previous = registration.DebounceCancellation;
            registration.DebounceCancellation = cancellation;
        }

        previous?.Cancel();
        previous?.Dispose();
        _ = SynchronizeAfterDelayAsync(assetId, registration, cancellation);
    }

    private async Task SynchronizeAfterDelayAsync(
        Guid assetId,
        WatchRegistration registration,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(ChangeDebounce, cancellation.Token).ConfigureAwait(false);
            await SynchronizeAsync(assetId, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A newer file-system event replaces this debounce operation.
        }
        catch (Exception exception)
        {
            SynchronizationFailed(logger, assetId, exception);
        }
        finally
        {
            lock (registration.SyncRoot)
            {
                if (ReferenceEquals(registration.DebounceCancellation, cancellation))
                {
                    registration.DebounceCancellation = null;
                }
            }

            cancellation.Dispose();
        }
    }

    private async Task SynchronizeAsync(Guid assetId, CancellationToken cancellationToken)
    {
        var transientRetryDelay = RetryDelay;
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_watchedAssets.TryGetValue(assetId, out var registration))
            {
                return;
            }

            ProjectAsset watchedAsset;
            lock (registration.SyncRoot)
            {
                watchedAsset = registration.Asset;
            }

            var project = await projects.GetAsync(watchedAsset.ProjectId, cancellationToken).ConfigureAwait(false);
            if (project?.Status != ProjectStatus.Active)
            {
                return;
            }

            var current = (await projects.ListAssetsAsync(watchedAsset.ProjectId, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(asset => asset.Id == assetId);
            if (current is null)
            {
                StopMonitoring(assetId);
                return;
            }

            try
            {
                var state = await InspectAsync(current, cancellationToken).ConfigureAwait(false);
                if (state.State == AssetWorkingCopyState.Unchanged)
                {
                    UpdateWatchedAsset(current);
                    return;
                }

                if (state.State == AssetWorkingCopyState.Missing)
                {
                    if (attempt < 7)
                    {
                        await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    return;
                }

                var updated = await ReimportAsync(current, current.Revision, cancellationToken).ConfigureAwait(false);
                UpdateWatchedAsset(updated);
                AssetSynchronized?.Invoke(this, new ProjectAssetSynchronizedEventArgs(updated, state.Path));
                return;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or RevisionConflictException)
            {
                if (!_watchedAssets.ContainsKey(assetId))
                {
                    return;
                }

                await Task.Delay(transientRetryDelay, cancellationToken).ConfigureAwait(false);
                transientRetryDelay = TimeSpan.FromMilliseconds(Math.Min(
                    transientRetryDelay.TotalMilliseconds * 2,
                    MaximumRetryDelay.TotalMilliseconds));
            }
        }
    }

    private void UpdateWatchedAsset(ProjectAsset asset)
    {
        if (_watchedAssets.TryGetValue(asset.Id, out var registration))
        {
            lock (registration.SyncRoot)
            {
                registration.Asset = asset;
            }
        }
    }

    private void StopMonitoring(Guid assetId)
    {
        if (_watchedAssets.TryRemove(assetId, out var registration))
        {
            registration.Dispose();
        }
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var assetId in _watchedAssets.Keys)
        {
            StopMonitoring(assetId);
        }

        _gate.Dispose();
    }

    private sealed class WatchRegistration : IDisposable
    {
        private readonly FileSystemWatcher _watcher;
        private int _started;
        private int _disposed;

        public WatchRegistration(ProjectAsset asset, string path, Action<Guid> changed)
        {
            Asset = asset;
            Path = path;
            var directory = System.IO.Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException("Der Arbeitskopieordner ist ungültig.");
            _watcher = new FileSystemWatcher(directory)
            {
                Filter = "*",
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.Size
                    | NotifyFilters.CreationTime,
            };
            _watcher.Changed += (_, _) => changed(asset.Id);
            _watcher.Created += (_, _) => changed(asset.Id);
            _watcher.Deleted += (_, _) => changed(asset.Id);
            _watcher.Renamed += (_, _) => changed(asset.Id);
            _watcher.Error += (_, _) => changed(asset.Id);
        }

        public object SyncRoot { get; } = new();

        public ProjectAsset Asset { get; set; }

        public string Path { get; }

        public CancellationTokenSource? DebounceCancellation { get; set; }

        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) == 0)
            {
                _watcher.EnableRaisingEvents = true;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            lock (SyncRoot)
            {
                DebounceCancellation?.Cancel();
                DebounceCancellation?.Dispose();
                DebounceCancellation = null;
            }

            _watcher.Dispose();
        }
    }
}
