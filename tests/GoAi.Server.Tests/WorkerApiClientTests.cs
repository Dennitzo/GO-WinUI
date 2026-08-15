using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Security;
using GoAi.Server.Core.Workers;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text;

namespace GoAi.Server.Tests;

public sealed class WorkerApiClientTests
{
    [Fact]
    public async Task SpeechWarmupAcceptsTheSpeakerComponent()
    {
        var root = Path.Combine(Path.GetTempPath(), $"go-ai-worker-client-{Guid.NewGuid():N}");
        try
        {
            var options = Options.Create(new GoAiServerOptions
            {
                DataDirectory = root,
                WorkerKeyDirectory = Path.Combine(root, "Secrets"),
                SpeechWorkerUri = new Uri("http://speech.worker.test", UriKind.Absolute),
            });
            using var keys = new WorkerKeyStore(options);
            var handler = new RecordingHandler();
            using var httpClient = new HttpClient(handler);
            var client = new WorkerApiClient(httpClient, keys, options);

            var response = await client.LoadSpeechComponentAsync("speaker");

            Assert.Equal(System.Text.Json.JsonValueKind.Object, response.ValueKind);
            Assert.Equal("/load", handler.Path);
            Assert.Contains("\"component\":\"speaker\"", handler.Body, StringComparison.Ordinal);
            Assert.True(handler.HasWorkerKey);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SpeechWarmupRejectsUnknownComponentsBeforeCallingTheWorker()
    {
        var options = Options.Create(new GoAiServerOptions());
        using var keys = new WorkerKeyStore(options);
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var client = new WorkerApiClient(httpClient, keys, options);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.LoadSpeechComponentAsync("vision"));

        Assert.Null(handler.Path);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? Path { get; private set; }

        public string Body { get; private set; } = string.Empty;

        public bool HasWorkerKey { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.AbsolutePath;
            Body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            HasWorkerKey = request.Headers.Contains(GoAiHeaders.WorkerKey);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
