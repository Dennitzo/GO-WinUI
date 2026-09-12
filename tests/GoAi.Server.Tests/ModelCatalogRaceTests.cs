using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class ModelCatalogRaceTests
{
    private const string FirstId = "coding/gpt-oss-120b~test";
    private const string NextId = "vision/Qwen3.8-27B~test";

    [Theory]
    [InlineData("unloaded")]
    [InlineData("loading")]
    [InlineData("missing")]
    [InlineData("switch")]
    public async Task ConcurrentNativeModelSwitchReconcilesPropsFailureWithoutReportingRouterOffline(string nextState)
    {
        using var handler = new SwitchingHandler(nextState);
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);

        var status = await client.GetStatusAsync();

        Assert.True(status.ProviderReachable);
        Assert.Equal(2, handler.CatalogReads);
        Assert.DoesNotContain(status.Models, model => model.Id == FirstId && model.Loaded);
        if (nextState == "missing") Assert.Empty(status.Models);
        else if (nextState == "switch")
        {
            var current = Assert.Single(status.Models, model => model.Id == NextId);
            Assert.True(current.Loaded);
            Assert.Equal(16_384, current.ContextTokens);
            Assert.Equal(2, handler.PropsReads);
        }
        else Assert.All(status.Models, model => Assert.Equal(nextState, model.State));
    }

    [Theory]
    [InlineData("loaded")]
    [InlineData("sleeping")]
    public async Task PropsFailureForStillResidentModelIsNotSilentlyReplacedWithNominalContext(string nextState)
    {
        using var handler = new SwitchingHandler(nextState);
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);

        Assert.False((await client.GetStatusAsync()).ProviderReachable);
        Assert.Equal(2, handler.CatalogReads);
        Assert.Equal(1, handler.PropsReads);
    }

    [Fact]
    public async Task ReconciliationDoesNotLoopAcrossRepeatedModelSwitchesOrBrokenReplacement()
    {
        using var handler = new SwitchingHandler("switch", failReplacementProps: true);
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);

        Assert.False((await client.GetStatusAsync()).ProviderReachable);
        Assert.Equal(2, handler.CatalogReads);
        Assert.Equal(2, handler.PropsReads);
    }

    [Theory]
    [InlineData("switch")]
    [InlineData("one")]
    [InlineData("all")]
    [InlineData("except")]
    public async Task StatusDuringOwnedTransitionChecksOnlyRouterAndReportsTheExactTransition(string operation)
    {
        using var handler = new CoordinatedHandler { HoldUnload = true };
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        Task transition = operation switch
        {
            "switch" => client.EnsureModelLoadedAsync(NextId, 16_384),
            "one" => client.UnloadModelAsync(FirstId),
            "all" => client.UnloadAllModelsAsync(),
            _ => client.UnloadModelsExceptAsync([]),
        };
        await handler.TransitionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var before = handler.CatalogReads;
            var status = await client.GetStatusAsync().WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(status.ProviderReachable);
            Assert.Equal(before + 1, handler.CatalogReads);
            Assert.Equal(0, handler.PropsWhileUnloading);
            Assert.All(status.Models.Where(model => model.Id == FirstId), model =>
            {
                Assert.Equal("unloading", model.State);
                Assert.False(model.Loaded);
            });
            Assert.Equal(operation == "switch" ? "loading" : "unloaded", Assert.Single(status.Models, model => model.Id == NextId).State);
        }
        finally { handler.ReleaseUnload.TrySetResult(); }
        await transition.WaitAsync(TimeSpan.FromSeconds(5));
        var completed = await client.GetStatusAsync();
        Assert.True(completed.ProviderReachable);
        Assert.DoesNotContain(completed.Models, model => model.State is "loading" or "unloading");
    }

    [Fact]
    public async Task StatusPropsReadHoldsTheSameGateThatProtectsModelSwitches()
    {
        using var handler = new CoordinatedHandler { HoldFirstProps = true };
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        var status = client.GetStatusAsync();
        await handler.PropsEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var transition = client.EnsureModelLoadedAsync(NextId, 16_384);
        try
        {
            Assert.False(handler.TransitionEntered.Task.IsCompleted);
            Assert.False(transition.IsCompleted);
        }
        finally { handler.ReleaseProps.TrySetResult(); }
        Assert.True((await status.WaitAsync(TimeSpan.FromSeconds(5))).ProviderReachable);
        Assert.Equal(NextId, await transition.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, handler.PropsWhileUnloading);
    }

    [Fact]
    public async Task RouterFailureDuringKnownModelTransitionRemainsVisible()
    {
        using var handler = new CoordinatedHandler { HoldUnload = true, FailRouterDuringUnload = true };
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        var transition = client.EnsureModelLoadedAsync(NextId, 16_384);
        await handler.TransitionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { Assert.False((await client.GetStatusAsync().WaitAsync(TimeSpan.FromSeconds(1))).ProviderReachable); }
        finally { handler.ReleaseUnload.TrySetResult(); }
        await transition.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True((await client.GetStatusAsync()).ProviderReachable);
    }

    private static ModelRuntimeClient CreateClient(HttpClient http) => new(http,
        Options.Create(new GoAiServerOptions { ModelRuntimeUri = new Uri("http://native.test:8081") }),
        NullLogger<ModelRuntimeClient>.Instance);

    private sealed class CoordinatedHandler : HttpMessageHandler
    {
        private string? _loadedId = FirstId;
        private bool _unloading;
        private int _propsReads;
        public bool HoldUnload { get; init; }
        public bool HoldFirstProps { get; init; }
        public bool FailRouterDuringUnload { get; init; }
        public int CatalogReads { get; private set; }
        public int PropsWhileUnloading { get; private set; }
        public TaskCompletionSource TransitionEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseUnload { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PropsEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseProps { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/v1/models")
            {
                CatalogReads++;
                if (_unloading && FailRouterDuringUnload) throw new HttpRequestException("Router stopped.");
                // Deliberately retain the stale loaded state while the child shuts down.
                return Json(new { data = new[]
                {
                    new { id = FirstId, status = new { value = _loadedId == FirstId ? "loaded" : "unloaded" } },
                    new { id = NextId, status = new { value = _loadedId == NextId ? "loaded" : "unloaded" } },
                } });
            }
            if (request.RequestUri.AbsolutePath == "/props")
            {
                _propsReads++;
                if (_unloading)
                {
                    PropsWhileUnloading++;
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }
                if (_propsReads == 1 && HoldFirstProps)
                {
                    PropsEntered.TrySetResult();
                    await ReleaseProps.Task.WaitAsync(cancellationToken);
                }
                return Json(new { default_generation_settings = new { n_ctx = _loadedId == FirstId ? 32_768 : 16_384 } });
            }
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (request.RequestUri.AbsolutePath == "/models/unload")
            {
                _unloading = true;
                TransitionEntered.TrySetResult();
                try
                {
                    if (HoldUnload) await ReleaseUnload.Task.WaitAsync(cancellationToken);
                    _loadedId = null;
                }
                finally { _unloading = false; }
            }
            else
            {
                Assert.Equal("/models/load", request.RequestUri.AbsolutePath);
                _loadedId = body.RootElement.GetProperty("model").GetString();
            }
            return Json(new { success = true });
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
        };
    }

    private sealed class SwitchingHandler(string nextState, bool failReplacementProps = false) : HttpMessageHandler
    {
        public int CatalogReads { get; private set; }
        public int PropsReads { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.RequestUri!.AbsolutePath == "/v1/models")
            {
                CatalogReads++;
                object[] models = CatalogReads == 1
                    ? [new { id = FirstId, status = new { value = "loaded" } }]
                    : nextState switch
                    {
                        "missing" => [],
                        "switch" => [new { id = FirstId, status = new { value = "unloaded" } }, new { id = NextId, status = new { value = "loaded" } }],
                        _ => [new { id = FirstId, status = new { value = nextState } }],
                    };
                return Task.FromResult(Json(new { data = models }));
            }
            Assert.Equal("/props", request.RequestUri.AbsolutePath);
            PropsReads++;
            if (PropsReads == 1 || failReplacementProps)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("native child is no longer available", Encoding.UTF8, "text/plain"),
                });
            Assert.Contains(Uri.EscapeDataString(NextId), request.RequestUri.Query, StringComparison.Ordinal);
            return Task.FromResult(Json(new { default_generation_settings = new { n_ctx = 16_384 } }));
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
        };
    }
}
