using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using Microsoft.Extensions.Logging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace GoWinUI.App.Services;

public sealed class ProjectAssetThumbnailService(
    IProjectRepository projects,
    IBinaryObjectStore binaryObjects,
    IProjectAssetWorkingCopyService workingCopies,
    ILogger<ProjectAssetThumbnailService> logger)
{
    private const uint MaximumDimension = 256;

    public async Task<bool> GenerateAsync(ProjectAsset asset, CancellationToken cancellationToken = default)
    {
        if (!IsSupportedImage(asset))
        {
            await projects.DeleteAssetThumbnailAsync(asset.Id, cancellationToken);
            return false;
        }

        try
        {
            var workingCopy = await workingCopies.MaterializeAsync(asset, cancellationToken);
            var storageFile = await StorageFile.GetFileFromPathAsync(workingCopy.Path);
            using var source = await storageFile.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(source);
            var scale = Math.Min(
                1d,
                MaximumDimension / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            var width = Math.Max(1u, (uint)Math.Round(decoder.PixelWidth * scale));
            var height = Math.Max(1u, (uint)Math.Round(decoder.PixelHeight * scale));
            var transform = new BitmapTransform
            {
                ScaledWidth = width,
                ScaledHeight = height,
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);

            using var encoded = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, encoded);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                width,
                height,
                decoder.DpiX > 0 ? decoder.DpiX : 96,
                decoder.DpiY > 0 ? decoder.DpiY : 96,
                pixelData.DetachPixelData());
            await encoder.FlushAsync();
            encoded.Seek(0);

            await using var input = encoded.AsStreamForRead();
            var blob = await binaryObjects.ImportAsync(input, "image/png", cancellationToken);
            try
            {
                await projects.SaveAssetThumbnailAsync(
                    new(asset.Id, blob.Id, "image/png", checked((int)width), checked((int)height), DateTimeOffset.UtcNow),
                    cancellationToken);
                return true;
            }
            catch
            {
                await binaryObjects.DeleteIfUnreferencedAsync(blob.Id, CancellationToken.None);
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppLog.AssetThumbnailGenerationFailed(logger, exception, asset.Id);
            await projects.DeleteAssetThumbnailAsync(asset.Id, CancellationToken.None);
            return false;
        }
    }

    public async Task<Stream?> OpenAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        var thumbnail = await projects.GetAssetThumbnailAsync(assetId, cancellationToken);
        return thumbnail is null
            ? null
            : await binaryObjects.OpenReadAsync(thumbnail.BlobId, cancellationToken);
    }

    private static bool IsSupportedImage(ProjectAsset asset) =>
        asset.Category == AssetCategory.Image
        || asset.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
}
