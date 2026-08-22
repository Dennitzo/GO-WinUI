using GoAi.Client;
using GoAi.Contracts;
using System.Net;
using System.Text;

namespace GoAi.Server.Tests;

public sealed class GoAiClientTests
{
    [Fact]
    public async Task ReadinessServiceUnavailableReturnsItsDiagnosticSnapshot()
    {
        const string payload = """
            {
              "status": "notReady",
              "protocolVersion": "1.0",
              "timestamp": "2026-08-20T07:29:18Z",
              "reason": "Erforderliche Modelle fehlen: qwen3-coder-next",
              "repair": "Das Coding-Modell vollständig herunterladen."
            }
            """;
        using var http = new HttpClient(new StaticResponseHandler(
            HttpStatusCode.ServiceUnavailable,
            payload,
            "application/json"))
        {
            BaseAddress = new Uri("https://go-ai.test/"),
        };
        using var client = new GoAiClient(http, "goai_123456789abc_test");

        var health = await client.GetReadyHealthAsync();

        Assert.Equal("notReady", health.Status);
        Assert.Contains("qwen3-coder-next", health.Reason, StringComparison.Ordinal);
        Assert.Contains("herunterladen", health.Repair, StringComparison.Ordinal);
    }

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

    [Fact]
    public async Task SpeechSessionClientUsesParagraphAndCompletionEndpoints()
    {
        var handler = new SpeechSessionHandler();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://go-ai.test/"),
        };
        using var client = new GoAiClient(http, "goai_123456789abc_test");

        var session = await client.CreateSpeechSessionAsync(
            new SpeechSessionRequest(
                SpeechContentProfile.Prepared,
                "de"));
        var paragraph = await client.SynthesizeSpeechParagraphAsync(
            session.SessionId,
            new SpeechParagraphRequest(
                "Der Absatz bleibt vollständig. Die Markierung bleibt satzgenau.",
                0,
                Parts:
                [
                    new(0, "Der Absatz bleibt vollständig.", 0.88, 120, 80),
                    new(1, "Die Markierung bleibt satzgenau.", 1.0, 0, 0),
                ]));
        _ = await client.EndSpeechSessionAsync(session.SessionId);

        Assert.Equal(SpeechProviderIds.SupertonicF5Cuda, paragraph.Provider);
        Assert.Equal(SpeechAlignmentStatus.Deterministic, paragraph.AlignmentStatus);
        Assert.Equal(2, paragraph.Timings?.Count);
        Assert.Equal(1, paragraph.Timings?[1].SegmentIndex);
        Assert.Equal(
            [
                "v1/audio/speech/sessions",
                $"v1/audio/speech/sessions/{session.SessionId}/paragraphs",
                $"v1/audio/speech/sessions/{session.SessionId}/end",
            ],
            handler.Paths);
        Assert.Contains("\"profile\":\"prepared\"", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("providerId", handler.RequestBodies[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"paragraphIndex\":0", handler.RequestBodies[1], StringComparison.Ordinal);
        Assert.Contains("\"segmentIndex\":1", handler.RequestBodies[1], StringComparison.Ordinal);
        Assert.Contains("\"pauseBeforeMilliseconds\":120", handler.RequestBodies[1], StringComparison.Ordinal);
        Assert.Contains("\"pauseAfterMilliseconds\":80", handler.RequestBodies[1], StringComparison.Ordinal);
        Assert.Equal(string.Empty, handler.RequestBodies[2]);
    }

    [Fact]
    public async Task SpeechSessionRetriesRateLimitResponseUsingRetryAfter()
    {
        var handler = new RateLimitedSpeechSessionHandler();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://go-ai.test/"),
        };
        using var client = new GoAiClient(http, "goai_123456789abc_test");

        var session = await client.CreateSpeechSessionAsync(
            new SpeechSessionRequest(SpeechContentProfile.Prepared, "de"));

        Assert.Equal("speech-0123456789abcdef0123456789abcdef", session.SessionId);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task ArtifactDownloadRetriesRateLimitResponseUsingRetryAfter()
    {
        var root = Path.Combine(Path.GetTempPath(), "GO-AI-Client-Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, "speech.wav");
        var handler = new RateLimitedArtifactHandler();
        try
        {
            using var http = new HttpClient(handler)
            {
                BaseAddress = new Uri("https://go-ai.test/"),
            };
            using var client = new GoAiClient(http, "goai_123456789abc_test");

            await client.DownloadArtifactAsync("artifact-0123456789abcdef0123456789abcdef", destination);

            Assert.Equal(2, handler.RequestCount);
            Assert.Equal("wave-data", await File.ReadAllTextAsync(destination));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DictationChunkCarriesRevisionMetadataInHeaders()
    {
        var handler = new DictationChunkHandler();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://go-ai.test/"),
        };
        using var client = new GoAiClient(http, "goai_123456789abc_test");

        var response = await client.SendLiveCaptionChunkAsync(
            "caption-0123456789abcdef0123456789abcdef",
            4,
            new byte[44 + 3_200],
            new LiveCaptionChunkMetadata("turn-42", 9, 1_200, false));

        Assert.Equal("turn-42", handler.Headers[GoAiHeaders.CaptionTurnId]);
        Assert.Equal("9", handler.Headers[GoAiHeaders.CaptionRevision]);
        Assert.Equal("1200", handler.Headers[GoAiHeaders.CaptionWindowStartMilliseconds]);
        Assert.Equal("false", handler.Headers[GoAiHeaders.CaptionFinal]);
        Assert.Equal(9, response.Revision);
        Assert.Equal("Heizlast", response.StableText);
    }

    private sealed class StaticResponseHandler(
        HttpStatusCode statusCode,
        string content,
        string mediaType = "application/octet-stream") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content, Encoding.UTF8, mediaType),
            });
    }

    private sealed class DictationChunkHandler : HttpMessageHandler
    {
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            foreach (var header in request.Headers)
            {
                Headers[header.Key] = Assert.Single(header.Value);
            }
            const string payload = """
                {"sessionId":"caption-0123456789abcdef0123456789abcdef","sequence":4,"text":"Heizlast berechnen","transcript":"Heizlast berechnen","language":"de","languageProbability":0.98,"segments":[],"isFinal":false,"provider":"faster-whisper-large-v3-dictation","createdAt":"2026-08-22T10:00:00Z","turnId":"turn-42","revision":9,"stableText":"Heizlast","provisionalText":"berechnen"}
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            });
        }
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

    private sealed class SpeechSessionHandler : HttpMessageHandler
    {
        private const string SessionId = "speech-0123456789abcdef0123456789abcdef";

        public List<string> Paths { get; } = [];

        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery.TrimStart('/');
            Paths.Add(path);
            RequestBodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            var payload = path.EndsWith("/paragraphs", StringComparison.Ordinal)
                ? "{\"artifact\":{\"artifactId\":\"artifact-0123456789abcdef0123456789abcdef\",\"fileName\":\"speech.wav\",\"mediaType\":\"audio/wav\",\"length\":1024,\"sha256\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\",\"createdAt\":\"2026-08-19T12:00:00Z\",\"expiresAt\":\"2026-08-20T12:00:00Z\"},\"provider\":\"supertonic-3-f5-cuda\",\"paragraphIndex\":0,\"durationSeconds\":3.5,\"sampleRate\":44100,\"timings\":[{\"segmentIndex\":0,\"startSeconds\":0.0,\"endSeconds\":1.5},{\"segmentIndex\":1,\"startSeconds\":1.5,\"endSeconds\":3.5}],\"alignmentStatus\":\"deterministic\",\"alignmentConfidence\":1.0}"
                : $"{{\"sessionId\":\"{SessionId}\",\"state\":\"active\",\"profile\":\"prepared\",\"provider\":\"supertonic-3-f5-cuda\",\"generalModelEjected\":false,\"createdAt\":\"2026-08-19T12:00:00Z\",\"updatedAt\":\"2026-08-19T12:00:00Z\"}}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class RateLimitedSpeechSessionHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            if (RequestCount == 1)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return Task.FromResult(limited);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"sessionId\":\"speech-0123456789abcdef0123456789abcdef\",\"state\":\"active\",\"profile\":\"prepared\",\"provider\":\"supertonic-3-f5-cuda\",\"generalModelEjected\":false,\"createdAt\":\"2026-08-19T12:00:00Z\",\"updatedAt\":\"2026-08-19T12:00:00Z\"}",
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }

    private sealed class RateLimitedArtifactHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            if (RequestCount == 1)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return Task.FromResult(limited);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("wave-data", Encoding.UTF8, "application/octet-stream"),
            });
        }
    }

}
