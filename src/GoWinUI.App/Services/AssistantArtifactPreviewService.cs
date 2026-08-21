using GoWinUI.Core.Contracts;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace GoWinUI.App.Services;

public sealed record AssistantArtifactPreview(string Url, string? PosterUrl = null);

public sealed class AssistantArtifactPreviewService : IDisposable
{
    public const string VirtualHost = "go-preview.local";
    private readonly IChatArtifactRepository _artifacts;
    private readonly IBinaryObjectStore _blobs;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public string CacheRoot { get; }

    public AssistantArtifactPreviewService(
        IChatArtifactRepository artifacts,
        IBinaryObjectStore blobs)
        : this(artifacts, blobs, Path.Combine(App.Current.DataDirectory, "PreviewCache", "Artifacts"))
    {
    }

    internal AssistantArtifactPreviewService(
        IChatArtifactRepository artifacts,
        IBinaryObjectStore blobs,
        string cacheRoot)
    {
        _artifacts = artifacts;
        _blobs = blobs;
        CacheRoot = cacheRoot;
    }

    public async Task<AssistantArtifactPreview> PrepareAsync(Guid artifactId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var artifact = await _artifacts.GetAsync(artifactId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("Das lokale Artefakt wurde nicht gefunden.");
            var directory = Path.Combine(CacheRoot, artifact.Id.ToString("N"));
            Directory.CreateDirectory(directory);
            if (artifact.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                var path = Path.Combine(directory, "preview.png");
                if (!File.Exists(path))
                {
                    await WriteImagePreviewAsync(artifact.BlobId, path, cancellationToken).ConfigureAwait(false);
                }
                var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                return new(BuildImageDataUrl(bytes));
            }
            if (artifact.ContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            {
                var audioName = "media" + ResolveMediaExtension(artifact.ContentType, artifact.FileName, ".wav");
                var audioPath = Path.Combine(directory, audioName);
                if (!File.Exists(audioPath) || new FileInfo(audioPath).Length != artifact.Length)
                {
                    await WriteOriginalAsync(artifact.BlobId, audioPath, cancellationToken).ConfigureAwait(false);
                }
                return new(ToUrl(artifact.Id, audioName));
            }
            if (artifact.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            {
                var videoName = "media" + ResolveMediaExtension(artifact.ContentType, artifact.FileName, ".mp4");
                var videoPath = Path.Combine(directory, videoName);
                if (!File.Exists(videoPath) || new FileInfo(videoPath).Length != artifact.Length)
                {
                    await WriteOriginalAsync(artifact.BlobId, videoPath, cancellationToken).ConfigureAwait(false);
                }
                var posterPath = Path.Combine(directory, "poster.jpg");
                if (!File.Exists(posterPath))
                {
                    try
                    {
                        await WriteVideoPosterAsync(videoPath, posterPath, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (
                        exception is not OperationCanceledException and not OutOfMemoryException)
                    {
                        // Ein fehlender Windows-Thumbnail-Codec darf die eigentliche
                        // MP4-Wiedergabe nicht verhindern. Das Video funktioniert auch
                        // ohne Poster und kann später erneut materialisiert werden.
                    }
                }
                return new(ToUrl(artifact.Id, videoName), File.Exists(posterPath) ? ToUrl(artifact.Id, "poster.jpg") : null);
            }
            throw new NotSupportedException("Für diesen Artefakttyp ist keine Vorschau verfügbar.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> MaterializeOriginalAsync(Guid artifactId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var artifact = await _artifacts.GetAsync(artifactId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("Das lokale Artefakt wurde nicht gefunden.");
            var directory = Path.Combine(CacheRoot, artifact.Id.ToString("N"));
            Directory.CreateDirectory(directory);
            var extension = SafeExtension(artifact.FileName, artifact.ContentType);
            var path = Path.Combine(directory, "original" + extension);
            if (!File.Exists(path) || new FileInfo(path).Length != artifact.Length)
            {
                await WriteOriginalAsync(artifact.BlobId, path, cancellationToken).ConfigureAwait(false);
            }
            return path;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WriteImagePreviewAsync(Guid blobId, string destination, CancellationToken cancellationToken)
    {
        await using var source = await _blobs.OpenReadAsync(blobId, cancellationToken).ConfigureAwait(false);
        using var random = source.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(random);
        var scale = Math.Min(1d, Math.Min(1280d / decoder.PixelWidth, 1280d / decoder.PixelHeight));
        var width = Math.Max(1u, (uint)Math.Round(decoder.PixelWidth * scale));
        var height = Math.Max(1u, (uint)Math.Round(decoder.PixelHeight * scale));
        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform { ScaledWidth = width, ScaledHeight = height },
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, true))
        using (var output = file.AsRandomAccessStream())
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, width, height, decoder.DpiX, decoder.DpiY, pixels.DetachPixelData());
            await encoder.FlushAsync();
        }
        File.Move(temporary, destination, true);
    }

    private async Task WriteOriginalAsync(Guid blobId, string destination, CancellationToken cancellationToken)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true))
        {
            await _blobs.ExportAsync(blobId, file, cancellationToken).ConfigureAwait(false);
            await file.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        File.Move(temporary, destination, true);
    }

    private static async Task WriteVideoPosterAsync(string videoPath, string destination, CancellationToken cancellationToken)
    {
        var file = await StorageFile.GetFileFromPathAsync(videoPath);
        using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.VideosView, 640, ThumbnailOptions.UseCurrentScale);
        if (thumbnail.Size == 0) return;
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true))
            {
                await thumbnail.AsStreamForRead().CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static string ToUrl(Guid id, string fileName) =>
        $"https://{VirtualHost}/{id:N}/{Uri.EscapeDataString(fileName)}";

    internal static string ResolveMediaExtension(string contentType, string fileName, string fallback)
    {
        var known = contentType.Trim().ToLowerInvariant() switch
        {
            "audio/wav" or "audio/x-wav" or "audio/wave" => ".wav",
            "audio/mpeg" => ".mp3",
            "audio/mp4" or "audio/x-m4a" => ".m4a",
            "audio/ogg" => ".ogg",
            "audio/webm" => ".webm",
            "video/mp4" => ".mp4",
            "video/webm" => ".webm",
            _ => null,
        };
        if (known is not null)
        {
            return known;
        }

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension.Length is >= 2 and <= 10
            && extension[0] == '.'
            && extension.AsSpan(1).IndexOfAnyExceptInRange('a', 'z') < 0
                ? extension
                : fallback;
    }

    private static string SafeExtension(string fileName, string contentType)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension.Length is >= 2 and <= 10
            && extension[0] == '.'
            && extension.AsSpan(1).IndexOfAnyExceptInRange('a', 'z') < 0)
        {
            return extension;
        }
        return contentType.Trim().ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            "image/tiff" => ".tiff",
            _ => ".png",
        };
    }

    internal static string BuildImageDataUrl(ReadOnlySpan<byte> pngBytes) =>
        "data:image/png;base64," + Convert.ToBase64String(pngBytes);

    public void Dispose() => _gate.Dispose();
}
