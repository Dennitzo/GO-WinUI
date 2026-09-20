using GoAi.Server.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class MalformedSessionCacheResponseTests
{
    private const string ModelId = "coding/DeepSeek-V4-Flash-Vision-Fixture~cache-json";

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"status\":{}}")]
    [InlineData("{\"status\":null}")]
    [InlineData("{\"status\":42}")]
    [InlineData("{\"status\":\"saved\",\"detail\":{}}")]
    [InlineData("{\"status\":\"saved\",\"detail\":null}")]
    [InlineData("{\"status\":\"saved\",\"detail\":false,\"generatedTail\":\"Untrusted tail\"}")]
    public async Task InvalidCacheResponseCannotReplaceUserCancellationOrPublishAnExactTail(string invalidResponse)
    {
        using var context = new TestServerContext();
        context.Options.ModelRuntimeUri = new("http://native.test:19090");
        using var handler = new MalformedCacheHandler(invalidResponse);
        using var http = new HttpClient(handler);
        using var runtime = new ModelRuntimeClient(http, context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var progress = new List<ModelRuntimeProgress>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.CompleteChatAsync(ModelId,
            [new("user", "Erkläre das Beispiel.")], [], reasoningEffort: "high",
            nativeProgress: (value, token) =>
            {
                progress.Add(value);
                if (value.State == "reasoningDelta")
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }
                return ValueTask.CompletedTask;
            }, sessionCacheKey: "cache-json-session", cancellationToken: cancellation.Token));
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(1, handler.Saves);
        Assert.Contains(progress, value => value.State == "reasoningDelta");
        Assert.DoesNotContain(progress, value => value.State == "interruptedNativeTail");
    }

    private sealed class MalformedCacheHandler(string invalidResponse) : HttpMessageHandler
    {
        private static readonly string[] Tags = ["go-context-train:32768", "go-reasoning-mode:llama-native",
            "go-reasoning-levels:none|high", "go-reasoning-default:high"];
        internal int Saves { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/models") return Json(new { data = new[] { new { id = ModelId, tags = Tags, status = new { value = "loaded" } } } });
            if (path == "/props") return Json(new { default_generation_settings = new { n_ctx = 32768 } });
            if (path == "/v1/chat/completions/input_tokens") return Json(new { input_tokens = 1536 });
            if (path == "/sessions/prepare") return Json(new { status = "prepared" });
            if (path == "/sessions/save")
            {
                Saves++;
                Assert.False(cancellationToken.IsCancellationRequested);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(invalidResponse, Encoding.UTF8, "application/json") });
            }
            if (path == "/v1/chat/completions")
            {
                var prefill = JsonSerializer.Serialize(new { prompt_progress = new { total = 1536, processed = 1536, cache = 0 } });
                var thought = JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { reasoning_content = "Ich prüfe das Beispiel." } } } });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("data: " + prefill + "\n\ndata: " + thought + "\n\n", Encoding.UTF8, "text/event-stream") });
            }
            throw new InvalidOperationException("Unexpected endpoint: " + path);
        }

        private static Task<HttpResponseMessage> Json(object value) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") });
    }
}
