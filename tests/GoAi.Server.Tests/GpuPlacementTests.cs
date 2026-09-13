using System.Net;
using System.Text;
using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GoAi.Server.Tests;

public sealed class GpuPlacementTests
{
    [Theory]
    [InlineData(true, 8082)]
    [InlineData(false, 8081)]
    public async Task LoadUsesAdvertisedWindowsGpuControllerOnly(bool managed, int port)
    {
        using var handler = new PlacementHandler(managed);
        using var http = new HttpClient(handler);
        using var runtime = new ModelRuntimeClient(http, Options.Create(new GoAiServerOptions { ModelRuntimeUri = new("http://native.test:8081") }), NullLogger<ModelRuntimeClient>.Instance);
        await runtime.EnsureModelLoadedAsync("coding/test", 0);
        Assert.Equal(port, handler.LoadUri!.Port);
        await runtime.EnsureModelLoadedAsync("coding/test", 0);
        Assert.Equal(1, handler.Loads);
    }

    private sealed class PlacementHandler(bool managed) : HttpMessageHandler
    {
        internal Uri? LoadUri;
        internal int Loads;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string response;
            if (request.RequestUri!.AbsolutePath == "/models/load")
            {
                if (managed) Assert.True(request.Content!.Headers.ContentLength > 0);
                LoadUri = request.RequestUri;
                Loads++;
                response = "{}";
            }
            else if (request.RequestUri.AbsolutePath == "/props") response = """{"default_generation_settings":{"n_ctx":32768}}""";
            else
                response = "{\"data\":[{\"id\":\"coding/test\",\"status\":{\"value\":\"" + (Loads > 0 ? "loaded" : "unloaded") + "\"},\"tags\":[\"" + (managed ? "go-gpu-policy:single-preferred-v1" : "legacy") + "\"]}]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") });
        }
    }
}
