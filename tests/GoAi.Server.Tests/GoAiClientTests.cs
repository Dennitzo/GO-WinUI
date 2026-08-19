using GoAi.Client;
using GoAi.Contracts;
using System.Net;
using System.Text;

namespace GoAi.Server.Tests;

public sealed class GoAiClientTests
{
    [Fact]
    public async Task EmbeddingBatchesCanKeepTheModelLoadedAndReleaseItExplicitly()
    {
        var handler = new EmbeddingSessionHandler();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://go-ai.test/"),
        };
        using var client = new GoAiClient(http, "goai_123456789abc_test");

        var result = await client.CreateEmbeddingsAsync(new EmbeddingBatchRequest(
            [new EmbeddingInput("chunk-1", "Lüftungsanlage")],
            KeepModelLoaded: true));
        await client.ReleaseEmbeddingModelAsync();

        Assert.Equal("text-embedding-bge-m3", result.ModelId);
        Assert.Equal(
            ["v1/context/embeddings", "v1/context/embeddings/release"],
            handler.Paths);
        Assert.Contains("\"keepModelLoaded\":true", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.Equal(string.Empty, handler.RequestBodies[1]);
    }

    [Fact]
    public async Task ArtifactResumeRejectsServerThatIgnoresRange()
    {
        var root = Path.Combine(Path.GetTempPath(), "GO-AI-Client-Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, "artifact.bin");
        await File.WriteAllBytesAsync(destination, [1, 2, 3, 4]);
        try
        {
            using var http = new HttpClient(new StaticResponseHandler(HttpStatusCode.OK, "complete-file"))
            {
                BaseAddress = new Uri("https://go-ai.test/"),
            };
            using var client = new GoAiClient(http, "goai_123456789abc_test");

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                client.DownloadArtifactAsync("artifact-0123456789abcdef0123456789abcdef", destination, 4));

            Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(destination));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StaticResponseHandler(HttpStatusCode statusCode, string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/octet-stream"),
            });
    }

    private sealed class EmbeddingSessionHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.PathAndQuery.TrimStart('/'));
            RequestBodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            if (request.RequestUri.AbsolutePath.EndsWith("/release", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"modelId\":\"text-embedding-bge-m3\",\"dimensions\":2,\"vectors\":[{\"id\":\"chunk-1\",\"values\":[0.5,0.25]}]}",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }
}
