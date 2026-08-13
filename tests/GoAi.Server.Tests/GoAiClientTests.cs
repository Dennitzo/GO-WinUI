using GoAi.Client;
using System.Net;
using System.Text;

namespace GoAi.Server.Tests;

public sealed class GoAiClientTests
{
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
}
