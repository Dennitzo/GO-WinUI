using System.Net;
using System.Text;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using GoWinUI.Infrastructure.AI;

namespace GoWinUI.Tests;

public sealed class LmStudioClientTests
{
    [Fact]
    public async Task NativeModelsEndpointReturnsOnlyLoadedModelsWithRuntimeContext()
    {
        var handler = new QueueHandler(Response(HttpStatusCode.OK,
            "{\"models\":[{\"type\":\"llm\",\"key\":\"loaded-model\",\"display_name\":\"Loaded\",\"loaded_instances\":[{\"id\":\"instance\",\"config\":{\"context_length\":16384}}]},{\"type\":\"llm\",\"key\":\"offline\",\"loaded_instances\":[]}]}",
            "application/json"));
        var client = new LmStudioClient(new HttpClient(handler), new StaticSettings());

        var model = Assert.Single(await client.ListModelsAsync());

        Assert.Equal("loaded-model", model.Id);
        Assert.Equal(16_384, model.ContextLength);
        Assert.Equal("/api/v1/models", handler.Requests[0].RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task ModelDiscoveryFallsBackToOpenAiEndpoint()
    {
        var handler = new QueueHandler(
            Response(HttpStatusCode.NotFound, "missing", "application/json"),
            Response(HttpStatusCode.OK, "{\"data\":[{\"id\":\"fallback-model\",\"max_context_length\":8192}]}", "application/json"));
        var client = new LmStudioClient(new HttpClient(handler), new StaticSettings());

        var model = Assert.Single(await client.ListModelsAsync());

        Assert.Equal("fallback-model", model.Id);
        Assert.Equal(8_192, model.ContextLength);
        Assert.Equal("/v1/models", handler.Requests[1].RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task ResponsesApiStreamsTextAndCompletion()
    {
        var handler = new QueueHandler(
            Response(HttpStatusCode.OK, "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"Hallo\"}\n\nevent: response.completed\ndata: {\"type\":\"response.completed\"}\n\n"));
        var client = new LmStudioClient(new HttpClient(handler), new StaticSettings());
        var result = new List<LmDelta>();
        await foreach (var delta in client.StreamAsync(Request())) result.Add(delta);

        Assert.Contains(result, static delta => delta.Text == "Hallo");
        Assert.True(result[^1].IsCompleted);
        Assert.EndsWith("/v1/responses", handler.Requests[0].RequestUri?.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsupportedResponsesEndpointFallsBackBeforeAnyTokens()
    {
        var handler = new QueueHandler(
            Response(HttpStatusCode.NotFound, "missing", "application/json"),
            Response(HttpStatusCode.OK, "data: {\"choices\":[{\"delta\":{\"content\":\"Fallback\"},\"finish_reason\":null}]}\n\ndata: [DONE]\n\n"));
        var client = new LmStudioClient(new HttpClient(handler), new StaticSettings());
        var result = new List<LmDelta>();
        await foreach (var delta in client.StreamAsync(Request())) result.Add(delta);

        Assert.Equal(2, handler.Requests.Count);
        Assert.EndsWith("/v1/chat/completions", handler.Requests[1].RequestUri?.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains(result, static delta => delta.Text == "Fallback");
    }

    private static LmChatRequest Request() => new("local-model", [new(ChatRole.User, "Hallo")]);

    private static HttpResponseMessage Response(HttpStatusCode status, string body, string contentType = "text/event-stream") => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, contentType),
    };

    private sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        internal List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class StaticSettings : ISettingsStore
    {
        public string SettingsPath => string.Empty;
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppSettings());
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
