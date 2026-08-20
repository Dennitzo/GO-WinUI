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

    [Fact]
    public async Task ExclusiveLmStudioTransitionCanReleaseOptionalWorkersWithoutReleasingSpeech()
    {
        var root = Path.Combine(Path.GetTempPath(), $"go-ai-worker-client-{Guid.NewGuid():N}");
        try
        {
            var options = Options.Create(new GoAiServerOptions
            {
                DataDirectory = root,
                WorkerKeyDirectory = Path.Combine(root, "Secrets"),
                SpeechWorkerUri = new Uri("http://speech.worker.test", UriKind.Absolute),
                MediaWorkerUri = new Uri("http://media.worker.test", UriKind.Absolute),
                ImageWorkerUri = new Uri("http://image.worker.test", UriKind.Absolute),
            });
            using var keys = new WorkerKeyStore(options);
            var handler = new RecordingHandler();
            using var httpClient = new HttpClient(handler);
            var client = new WorkerApiClient(httpClient, keys, options);

            await client.ReleaseAllAsync(exceptWorker: "speech");

            Assert.Collection(
                handler.Requests.OrderBy(static request => request.Host),
                request =>
                {
                    Assert.Equal("image.worker.test", request.Host);
                    Assert.Equal("/release", request.Path);
                },
                request =>
                {
                    Assert.Equal("media.worker.test", request.Host);
                    Assert.Equal("/release", request.Path);
                });
            Assert.DoesNotContain(handler.Requests, static request => request.Host == "speech.worker.test");
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
    public async Task SpeechParagraphRequestCarriesProviderAndSentenceParts()
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
            const string sessionId = "speech-0123456789abcdef0123456789abcdef";

            _ = await client.SynthesizeParagraphAsync(
                sessionId,
                new SpeechParagraphRequest(
                    "Erster Satz. Zweiter Satz.",
                    3,
                    Parts:
                    [
                        new(10, "Erster Satz.", 0.82, 180, 100),
                        new(11, "Zweiter Satz.", 0.76, 0, 240),
                    ]),
                forceSegmentSynthesis: true);

            Assert.Equal($"/speech/sessions/{sessionId}/paragraphs", handler.Path);
            using var body = System.Text.Json.JsonDocument.Parse(handler.Body);
            Assert.Equal(3, body.RootElement.GetProperty("paragraphIndex").GetInt32());
            Assert.Equal("Erster Satz. Zweiter Satz.", body.RootElement.GetProperty("text").GetString());
            var parts = body.RootElement.GetProperty("parts");
            Assert.Equal(2, parts.GetArrayLength());
            Assert.Equal(10, parts[0].GetProperty("segmentIndex").GetInt32());
            Assert.Equal(180, parts[0].GetProperty("pauseBeforeMilliseconds").GetInt32());
            Assert.Equal(100, parts[0].GetProperty("pauseAfterMilliseconds").GetInt32());
            Assert.Equal("Zweiter Satz.", parts[1].GetProperty("text").GetString());
            Assert.Equal(0.76, parts[1].GetProperty("speed").GetDouble(), 3);
            Assert.Equal(240, parts[1].GetProperty("pauseAfterMilliseconds").GetInt32());
            Assert.True(body.RootElement.GetProperty("forceSegmentSynthesis").GetBoolean());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? Path { get; private set; }

        public string Body { get; private set; } = string.Empty;

        public bool HasWorkerKey { get; private set; }

        public List<(string Host, string Path)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.AbsolutePath;
            Requests.Add((request.RequestUri?.Host ?? string.Empty, Path ?? string.Empty));
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
