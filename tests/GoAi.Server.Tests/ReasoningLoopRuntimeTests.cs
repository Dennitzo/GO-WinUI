using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GoAi.Server.Tests;

public sealed class ReasoningLoopRuntimeTests
{
    private static readonly Dictionary<string, string> NoTools = new(StringComparer.Ordinal);

    [Theory]
    [InlineData("general")]
    [InlineData("coding")]
    public async Task GeneralAndCodingStopTheSameLoopWithoutProviderRetry(string role)
    {
        using var handler = new GuardHandler(loop: true, sse: true);
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        var progress = new List<ModelRuntimeProgress>();
        var failure = await Assert.ThrowsAsync<ReasoningLoopDetectedException>(() => client.CompleteChatAsync(
            GuardHandler.ModelId, [new("user", "Erstelle den Abschlussbericht")], [], modelRole: role,
            nativeProgress: (value, _) => { progress.Add(value); return ValueTask.CompletedTask; }));
        Assert.Equal("reasoning_repetition", failure.FailureKind);
        Assert.Equal(1, handler.ChatRequests);
        Assert.DoesNotContain(progress, value => value.State == "generationRetry");
        Assert.Contains(progress, value => value.State == "reasoningGuardStopped" && value.FailureKind == failure.FailureKind);
        Assert.Contains("Bericht erstellen", string.Concat(progress.Select(value => value.ReasoningDelta)));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task VisionUsesGuardForSseAndProviderJsonFallback(bool sse, bool loop)
    {
        using var handler = new GuardHandler(loop, sse);
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        var path = Path.Combine(Path.GetTempPath(), $"go-reasoning-{Guid.NewGuid():N}.png");
        try
        {
            await File.WriteAllBytesAsync(path, [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
            if (loop)
            {
                var failure = await Assert.ThrowsAsync<ReasoningLoopDetectedException>(() =>
                    client.AnalyzeImagesAsync(GuardHandler.ModelId, "Prüfe das Bild", [path]));
                Assert.Equal("reasoning_repetition", failure.FailureKind);
            }
            else Assert.Equal("Das Bild ist geprüft.", await client.AnalyzeImagesAsync(GuardHandler.ModelId, "Prüfe das Bild", [path]));
            Assert.Equal(1, handler.ChatRequests);
            Assert.True(handler.RequestedStream);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ReasoningDeadlineCancelsBlockedSseReadAndPreservesLastFragment()
    {
        await using var stream = new BlockingTailStream(SseDelta(new { reasoning_content = "Ich prüfe die einzelnen Komponenten. " }));
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentType = new("text/event-stream");
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var fragments = new List<string>();
        var failure = await Assert.ThrowsAsync<ReasoningLoopDetectedException>(() => ModelRuntimeClient.ParseStreamingChatResponseAsync(
            response, NoTools, (value, _) => { if (value.ReasoningDelta is { } text) fragments.Add(text); return ValueTask.CompletedTask; },
            false, 1, Stopwatch.StartNew(), ceiling.Token, new ReasoningLoopGuard(TimeSpan.FromMilliseconds(100))));
        Assert.Equal("reasoning_watchdog", failure.FailureKind);
        Assert.False(ceiling.IsCancellationRequested);
        Assert.True(stream.WasDisposed);
        Assert.Contains("einzelnen Komponenten", string.Concat(fragments));
    }

    [Theory]
    [InlineData("application/json", "{\"choices\":[")]
    [InlineData("text/event-stream", "")]
    public async Task MissingReasoningTelemetryStillHonorsNativeRequestCancellation(string contentType, string prefix)
    {
        await using var stream = new BlockingTailStream(prefix);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentType = new(contentType);
        using var idleDeadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ModelRuntimeClient.ParseStreamingChatResponseAsync(
            response, NoTools, null, false, 1, Stopwatch.StartNew(), idleDeadline.Token));
        Assert.True(stream.WasDisposed);
    }

    [Fact]
    public async Task LoopNeverSalvagesAPartialToolCallAsSuccess()
    {
        var body = SseDelta(new { tool_calls = new[] { new { index = 0, function = new { name = "coding_write", arguments = "{\"path\":\"unfinished" } } } })
            + SseDelta(new { reasoning_content = string.Concat(Enumerable.Repeat(ReasoningLoopGuardTests.NightPassage, 100)) });
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
        await Assert.ThrowsAsync<ReasoningLoopDetectedException>(() => ModelRuntimeClient.ParseStreamingChatResponseAsync(
            response, NoTools, null, false, 1, Stopwatch.StartNew(), CancellationToken.None));
    }

    private static ModelRuntimeClient CreateClient(HttpClient http) => new(http,
        Options.Create(new GoAiServerOptions { ModelRuntimeUri = new Uri("http://native.test:8081") }), NullLogger<ModelRuntimeClient>.Instance);

    private static string SseDelta(object delta) => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta } } }) + "\n\n";

    private sealed class GuardHandler(bool loop, bool sse) : HttpMessageHandler
    {
        internal const string ModelId = "coding/Qwen3.8-27B~guard-test";
        public int ChatRequests { get; private set; }
        public bool RequestedStream { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path is "/sessions/prepare" or "/sessions/save") return new(HttpStatusCode.NotFound);
            if (path == "/v1/models") return Json(new { data = new[] { new { id = ModelId, status = new { value = "loaded" } } } });
            if (path == "/props") return Json(new { default_generation_settings = new { n_ctx = 32_768 } });
            if (path == "/v1/chat/completions/input_tokens") return Json(new { input_tokens = 100 });
            Assert.Equal("/v1/chat/completions", path);
            ChatRequests++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            RequestedStream = body.RootElement.GetProperty("stream").GetBoolean();
            var reasoning = loop ? string.Concat(Enumerable.Repeat(ReasoningLoopGuardTests.NightPassage, 100)) : "Ich prüfe die Bilddetails. ";
            if (!sse) return Json(new { choices = new[] { new { message = new { reasoning_content = reasoning, content = "Das Bild ist geprüft." } } } });
            return new(HttpStatusCode.OK) { Content = new StringContent(SseDelta(new { reasoning_content = reasoning })
                + SseDelta(new { content = "Das Bild ist geprüft." }) + "data: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }

    private sealed class BlockingTailStream(string prefix) : Stream
    {
        private readonly byte[] _prefix = Encoding.UTF8.GetBytes(prefix);
        private int _offset;
        public bool WasDisposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _offset; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset < _prefix.Length)
            {
                var count = Math.Min(buffer.Length, _prefix.Length - _offset);
                _prefix.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
                return count;
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }
}
