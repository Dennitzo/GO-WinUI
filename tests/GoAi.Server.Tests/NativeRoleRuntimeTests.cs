using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class NativeRoleRuntimeTests
{
    private const string GeneralId = "coding/gpt-oss-120b-MXFP4~123";
    private const string VisionId = "vision/Qwen3-VL-30B-A3B-Instruct-Q4~123";
    private const string EmbeddingId = "embedding/bge-m3-f16~123";
    private static readonly string[] EmbeddingInputs = ["context"];
    private static readonly string[] InstalledIds = [GeneralId, VisionId, EmbeddingId];
    private static readonly string[] ExpectedSwitch = ["unload:" + GeneralId, "load:" + EmbeddingId];
    private static readonly double[] ExpectedEmbedding = [0.1, 0.2, 0.3];

    [Fact]
    public async Task OneNativeCatalogExposesAllRolesAndResolvesLegacySelections()
    {
        var handler = new NativeHandler();
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        var status = await client.GetStatusAsync();
        Assert.Equal(4, status.Models.Count);
        Assert.Equal(GeneralId, ModelRuntimeClient.ResolveModelStatus(status.Models, "gpt-oss-120b", "general")?.Id);
        Assert.Equal(GeneralId, ModelRuntimeClient.ResolveModelStatus(status.Models, GeneralId, "coding")?.Id);
        Assert.Equal(VisionId, ModelRuntimeClient.ResolveModelStatus(status.Models, "qwen3-vl-30b-a3b-instruct", "vision")?.Id);
        Assert.Equal(EmbeddingId, ModelRuntimeClient.ResolveModelStatus(status.Models, "text-embedding-bge-m3", "embedding")?.Id);
        Assert.Null(ModelRuntimeClient.ResolveModelStatus(status.Models, "coding/gpt-oss-120b-MXFP4~different", "general"));
        Assert.All(status.Models.Where(static model => model.Role is "general" or "coding"), model => Assert.Equal(32_768, model.ContextTokens));
        Assert.Equal(262_144, Assert.Single(status.Models, static model => model.Role == "vision").ContextTokens);
        Assert.Equal(8_192, Assert.Single(status.Models, static model => model.Role == "embedding").ContextTokens);
    }

    [Fact]
    public async Task GeneralAndCodingReuseTheSameResidentPresetWithoutUnloading()
    {
        var handler = new NativeHandler();
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        Assert.Equal(GeneralId, await client.EnsureModelLoadedAsync("gpt-oss-120b", 32_768));
        Assert.Equal(GeneralId, await client.EnsureModelLoadedAsync(GeneralId, 32_768));
        Assert.Empty(handler.ModelOperations);
        Assert.All(handler.Requests, request => Assert.Equal("native.test", request.Host));
    }

    [Fact]
    public async Task EmbeddingSwitchUnloadsOnlyThePreviousNativePresetAndRunsNativeEmbeddingApi()
    {
        var handler = new NativeHandler();
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        var embeddings = await client.CreateEmbeddingsAsync("text-embedding-bge-m3", EmbeddingInputs);
        Assert.Equal(ExpectedSwitch, handler.ModelOperations);
        Assert.Equal(ExpectedEmbedding, Assert.Single(embeddings));
        Assert.Equal(EmbeddingId, handler.LastInferenceModel);
        Assert.Contains(handler.Requests, request => request.AbsolutePath == "/v1/embeddings");
        Assert.All(handler.Requests, request => Assert.Equal("native.test", request.Host));
    }

    [Fact]
    public async Task NativeUnavailableFailsWithoutAnotherProviderOrPortFallback()
    {
        var handler = new NativeHandler { Available = false };
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        Assert.False((await client.GetStatusAsync()).ProviderReachable);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.EnsureModelLoadedAsync("gpt-oss-120b", 32_768));
        Assert.All(handler.Requests, request => Assert.Equal("native.test", request.Host));
        Assert.DoesNotContain(handler.Requests, request => request.Port == 1234 || request.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EmbeddingCannotUnloadChatModelUntilItsInferenceCompletes()
    {
        var handler = new NativeHandler { HoldChat = true };
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        var chat = client.CompleteChatAsync("gpt-oss-120b", [new LmChatMessage("user", "Hello")], []);
        await handler.ChatEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var embedding = client.CreateEmbeddingsAsync("text-embedding-bge-m3", EmbeddingInputs);
        try
        {
            await Task.Delay(100);
            Assert.False(embedding.IsCompleted);
            Assert.Empty(handler.ModelOperations);
        }
        finally { handler.ReleaseChat.TrySetResult(); }
        Assert.Equal("Done", (await chat).Content);
        Assert.Single(await embedding);
        Assert.Equal(ExpectedSwitch, handler.ModelOperations);
    }

    private static ModelRuntimeClient CreateClient(HttpClient http) => new(http,
        Options.Create(new GoAiServerOptions { ModelRuntimeUri = new Uri("http://native.test:8081") }), NullLogger<ModelRuntimeClient>.Instance);

    private sealed class NativeHandler : HttpMessageHandler
    {
        public bool Available { get; set; } = true;
        public bool HoldChat { get; set; }
        public TaskCompletionSource ChatEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseChat { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<Uri> Requests { get; } = [];
        public List<string> ModelOperations { get; } = [];
        public string? LastInferenceModel { get; private set; }
        private string? _loadedId = GeneralId;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            if (!Available) throw new HttpRequestException("Native runtime is offline.");
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/props") return Json(new { default_generation_settings = new { n_ctx = _loadedId == EmbeddingId ? 8_192 : 32_768 } });
            if (path == "/v1/chat/completions/input_tokens") return Json(new { input_tokens = 3 });
            if (path == "/v1/models") return Json(new
            {
                data = InstalledIds.Select(id => new { id, status = new { value = id == _loadedId ? "loaded" : "unloaded" } }),
            });
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var model = body.RootElement.GetProperty("model").GetString();
            if (path == "/models/load")
            {
                _loadedId = model;
                ModelOperations.Add("load:" + model);
                return Json(new { success = true });
            }
            if (path == "/models/unload")
            {
                Assert.Equal(_loadedId, model);
                _loadedId = null;
                ModelOperations.Add("unload:" + model);
                return Json(new { success = true });
            }
            Assert.Equal(_loadedId, model);
            LastInferenceModel = model;
            if (path == "/v1/embeddings") return Json(new { data = new[] { new { index = 0, embedding = ExpectedEmbedding } } });
            Assert.Equal("/v1/chat/completions", path);
            ChatEntered.TrySetResult();
            if (HoldChat) await ReleaseChat.Task.WaitAsync(cancellationToken);
            return Json(new { choices = new[] { new { message = new { role = "assistant", content = "Done" } } }, usage = new { prompt_tokens = 3, completion_tokens = 1 } });
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
        };
    }
}
