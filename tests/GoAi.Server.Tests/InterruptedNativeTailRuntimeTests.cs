using System.Net;
using System.Text;
using System.Text.Json;
using GoAi.Server.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace GoAi.Server.Tests;

public sealed class InterruptedNativeTailRuntimeTests
{
    private const string ModelId = "coding/DeepSeek-V4-Flash-Vision-Fixture~tail";
    private const string SessionKey = "fixture-interrupted-session";
    private const string StreamedText = "Schon gestreamt.";
    private const string NativeTail = "Schon gestreamt.\nNoch nicht empfangen: Grüße 🧪  ";
    private const int NativePromptTokens = 1536;

    [Theory]
    [InlineData("none", "off")]
    [InlineData("high", "on")]
    public async Task CancellationPublishesTheSavedNativeTailWithoutLosingItsPromptBoundaryOrThinkingMode(
        string effort, string thinking)
    {
        using var context = new TestServerContext();
        context.Options.ModelRuntimeUri = new("http://native.test:19090");
        using var handler = new NativeHandler();
        using var http = new HttpClient(handler);
        using var runtime = new ModelRuntimeClient(http, context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var progress = new List<ModelRuntimeProgress>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.CompleteChatAsync(ModelId,
            [new("user", "Erkläre das Beispiel ausführlich.")], [], reasoningEffort: effort,
            nativeProgress: (value, token) =>
            {
                progress.Add(value);
                if (value.State == "contentDelta")
                {
                    Assert.Equal(StreamedText, value.ContentDelta);
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }
                if (value.State == "interruptedNativeTail")
                    Assert.False(token.IsCancellationRequested, "The durable cancellation receipt must use an independent token.");
                return ValueTask.CompletedTask;
            }, sessionCacheKey: SessionKey, cancellationToken: cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Contains(progress, item => item.State == "promptProcessing" && item.PromptTokens == NativePromptTokens);
        var saved = Assert.Single(handler.Saves);
        Assert.Equal(ModelId, saved.GetProperty("model").GetString());
        Assert.Equal(SessionKey, saved.GetProperty("sessionKey").GetString());
        Assert.Equal(NativePromptTokens, saved.GetProperty("promptTokens").GetInt32());
        var interrupted = Assert.Single(progress, item => item.State == "interruptedNativeTail");
        Assert.Equal(NativeTail, interrupted.ContentDelta);
        Assert.Equal(thinking, interrupted.ReasoningDelta);
        Assert.Equal("interruptedNativeTail", progress[^1].State);
        Assert.Equal(1, handler.ChatRequests);
    }

    private sealed class NativeHandler : HttpMessageHandler
    {
        private static readonly string[] Tags = ["go-context-train:32768", "go-reasoning-mode:llama-native",
            "go-reasoning-levels:none|high", "go-reasoning-default:high"];
        internal List<JsonElement> Saves { get; } = [];
        internal int ChatRequests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/models") return Json(new { data = new[] { new { id = ModelId, tags = Tags, status = new { value = "loaded" } } } });
            if (path == "/props") return Json(new { default_generation_settings = new { n_ctx = 32768 } });
            // Deliberately different from observed native prompt progress: the
            // cancellation snapshot must slice at the evaluated token boundary.
            if (path == "/v1/chat/completions/input_tokens") return Json(new { input_tokens = 256 });
            if (path == "/sessions/prepare") return Json(new { status = "prepared" });
            if (path == "/sessions/save")
            {
                Assert.False(cancellationToken.IsCancellationRequested);
                using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Saves.Add(document.RootElement.Clone());
                return Json(new { status = "saved", generatedTail = NativeTail });
            }
            if (path == "/v1/chat/completions")
            {
                ChatRequests++;
                var prompt = JsonSerializer.Serialize(new
                    { prompt_progress = new { total = NativePromptTokens, processed = NativePromptTokens, cache = 0 } });
                var text = JsonSerializer.Serialize(new
                    { choices = new[] { new { index = 0, delta = new { content = StreamedText } } } });
                return new(HttpStatusCode.OK)
                    { Content = new StringContent("data: " + prompt + "\n\ndata: " + text + "\n\n", Encoding.UTF8, "text/event-stream") };
            }
            throw new InvalidOperationException("Unexpected native fixture endpoint: " + path);
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
