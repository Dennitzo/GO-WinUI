using System.Net;
using GoAi.Contracts;
using GoAi.Server.Core.Gateway;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GoAi.Server.Tests;

public sealed class ParallelProviderRecoveryTests
{
    [Fact]
    public async Task NativePairUnavailableAfterRestartQueuesCodingRunForRetry()
    {
        using var context = new TestServerContext();
        context.Options.ModelRuntimeUri = new("http://native.test:8081");
        using var handler = new UnavailableNativeRuntime();
        using var http = new HttpClient(handler);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGoAiServerServices(context.Options, includeHostedServices: false);
        services.AddSingleton(context.Database);
        services.AddSingleton(new ModelRuntimeClient(http, context.WrappedOptions,
            NullLogger<ModelRuntimeClient>.Instance));
        using var provider = services.BuildServiceProvider();
        var repository = provider.GetRequiredService<RunRepository>();
        var processor = provider.GetRequiredService<RunProcessor>();
        var request = new RunRequest(GoAiProtocol.Version, RunMode.Coding,
            [new("user", [new("text", Text: "Continue saved coding work")])],
            ClientCapabilities: ["coding"],
            PreferredCodingModelId: "coding/qwen-fixture",
            CodingOptions: new(ParallelModelId: "coding/qwen-fixture"));
        var runId = (await repository.CreateAsync(request, null)).Snapshot.RunId;

        var failure = await Assert.ThrowsAsync<ModelProviderRequestException>(() =>
            processor.ProcessAsync(runId, CancellationToken.None));
        Assert.Equal("model_configuration", failure.Phase);
        Assert.True(await processor.TryScheduleProviderRetryAsync(runId, failure));
        Assert.Equal(RunState.Queued, (await repository.GetAsync(runId))!.State);
        Assert.NotNull(await repository.GetProviderRetryTimeAsync(runId));
        Assert.DoesNotContain(await repository.GetEventsAfterAsync(runId, 0),
            item => item.Type == RunEventTypes.RunFailed);
    }

    private sealed class UnavailableNativeRuntime : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("native Windows llama server is restarting"),
            });
    }
}
