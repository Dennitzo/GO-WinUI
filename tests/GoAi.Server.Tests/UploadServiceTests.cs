using GoAi.Contracts;
using GoAi.Server.Core.Storage;
using System.Security.Cryptography;

namespace GoAi.Server.Tests;

public sealed class UploadServiceTests
{
    [Fact]
    public async Task UploadResumesAndCompletesOnlyAfterShaValidation()
    {
        using var context = new TestServerContext();
        var service = new UploadService(context.Database, context.WrappedOptions);
        var bytes = System.Text.Encoding.UTF8.GetBytes("TGA Projektdokument");
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var created = await service.CreateAsync(new UploadManifest("projekt.txt", "text/plain", bytes.Length, sha));

        await using var chunk = new MemoryStream(bytes, writable: false);
        var receipt = await service.PutChunkAsync(created.UploadId, 0, chunk, sha);
        var resumed = await service.GetAsync(created.UploadId);
        var completed = await service.CompleteAsync(created.UploadId);

        Assert.True(receipt.Accepted);
        Assert.Equal([0], resumed?.ReceivedChunks);
        Assert.Equal(sha, completed.Sha256);
        Assert.NotNull(await service.ResolveCompletedPathAsync(created.UploadId));
    }

    [Fact]
    public async Task WrongChunkHashIsRejected()
    {
        using var context = new TestServerContext();
        var service = new UploadService(context.Database, context.WrappedOptions);
        byte[] bytes = [1, 2, 3, 4];
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var created = await service.CreateAsync(new UploadManifest("bild.bin", "application/octet-stream", bytes.Length, sha));
        await using var chunk = new MemoryStream(bytes, writable: false);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.PutChunkAsync(created.UploadId, 0, chunk, new string('0', 64)));
    }
}
