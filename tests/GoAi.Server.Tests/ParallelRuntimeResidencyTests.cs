using System.Net;
using System.Text;
using System.Text.Json;
using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GoAi.Server.Tests;

public sealed class ParallelRuntimeResidencyTests
{
    private const string Base = "coding/qwen3.8-27b~test";
    private static ModelRuntimeClient Client(HttpClient http) => new(http,
        Options.Create(new GoAiServerOptions { ModelRuntimeUri = new Uri("http://native.test:8081") }), NullLogger<ModelRuntimeClient>.Instance);

    [Fact]
    public async Task PairIsReconfiguredAfterNativeRestart()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler);
        using var client = Client(http);
        await client.ConfigurePairAsync(Base, Base, default);
        handler.Pair = false;
        await client.ConfigurePairAsync(Base, Base, default);
        Assert.True(handler.Pair);
        Assert.Equal(2, handler.ConfigureCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GlobalUnloadAndGeneralSwitchWaitForSecondaryInference(bool generalSwitch)
    {
        var handler = new Handler { HoldChat = true };
        using var http = new HttpClient(handler);
        using var client = Client(http);
        await client.ConfigurePairAsync(Base, Base, default);
        handler.Loaded.Add(Base + "~secondary");
        var chat = client.CompleteChatAsync(Base + "~secondary", [new LmChatMessage("user", "Hello")], []);
        await handler.Entered.Task;
        var reused = client.ConfigurePairAsync(Base, Base, default);
        Assert.True(reused.IsCompletedSuccessfully);
        Assert.Equal(Base + "~secondary", (await reused).Secondary);
        Task operation = generalSwitch ? client.EnsureModelLoadedAsync(Base, 0) : client.UnloadAllModelsAsync();
        Assert.False(operation.IsCompleted);
        Assert.Empty(handler.Unloaded);
        handler.Release.TrySetResult();
        await chat;
        await operation;
        Assert.Contains(Base + "~secondary", handler.Unloaded);
        if (generalSwitch)
        {
            Assert.False(handler.Pair);
            Assert.Contains(Base, handler.Loaded);
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public bool Pair;
        public bool HoldChat;
        public int ConfigureCalls;
        public HashSet<string> Loaded { get; } = [];
        public List<string> Unloaded { get; } = [];
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/models")
            {
                string[] ids = Pair ? [Base, Base + "~main", Base + "~secondary"] : [Base];
                return Json(new { data = ids.Select(id => new { id, status = new { value = Loaded.Contains(id) ? "loaded" : "unloaded" } }) });
            }
            if (path == "/props") return Json(new { default_generation_settings = new { n_ctx = 32768 } });
            if (path == "/v1/chat/completions/input_tokens") return Json(new { input_tokens = 3 });
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            if (path == "/models/configure-pair")
            {
                ConfigureCalls++;
                Pair = body.RootElement.GetProperty("secondaryModel").ValueKind != JsonValueKind.Null;
                return Json(new { mainAlias = Pair ? Base + "~main" : Base, secondaryAlias = Pair ? Base + "~secondary" : null });
            }
            var model = body.RootElement.GetProperty("model").GetString()!;
            if (path == "/models/unload") { Loaded.Remove(model); Unloaded.Add(model); return Json(new { success = true }); }
            if (path == "/models/load") { Loaded.Add(model); return Json(new { success = true }); }
            Assert.Equal("/v1/chat/completions", path);
            Entered.TrySetResult();
            if (HoldChat) await Release.Task.WaitAsync(token);
            return Json(new { choices = new[] { new { message = new { role = "assistant", content = "Done" } } }, usage = new { prompt_tokens = 3, completion_tokens = 1 } });
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
