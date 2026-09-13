using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Data;
using GoAi.Server.Core.Gateway;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Research;
using GoAi.Server.Core.Runs;
using GoAi.Server.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace GoAi.CodingBenchmarks;

internal sealed class BenchmarkHost : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly RunProcessor _processor;
    private readonly ClientToolDeadlineService _deadlines;

    private BenchmarkHost(IHost host, int port)
    {
        _host = host;
        _processor = host.Services.GetRequiredService<RunProcessor>();
        _deadlines = host.Services.GetRequiredService<ClientToolDeadlineService>();
        Address = new Uri($"http://127.0.0.1:{port}/");
    }

    internal Uri Address { get; }

    internal static async Task<BenchmarkHost> StartAsync(string directory, Uri runtime, CancellationToken token)
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders().AddSimpleConsole().SetMinimumLevel(LogLevel.Warning))
            .ConfigureGoAiServer(options =>
            {
                options.DataDirectory = Path.Combine(directory, "server");
                options.GatewayPort = port;
                options.PublicUrl = $"http://127.0.0.1:{port}";
                options.ModelRuntimeUri = runtime;
                // Search transport is an explicitly labelled frozen benchmark fixture.
                // Web fetches still use the production public-address validation and real HTTPS source.
                options.SearxngUri = new Uri("http://searxng.benchmark.invalid/");
            }, includeHostedServices: false, listenAddress: IPAddress.Loopback)
            .ConfigureServices(services => services.AddHttpClient(nameof(WebResearchService))
                .ConfigurePrimaryHttpMessageHandler(static () => new FrozenSearchHandler()))
            .Build();
        var result = new BenchmarkHost(host, port);
        try
        {
            await host.Services.GetRequiredService<GoAiDatabase>().InitializeAsync(token).ConfigureAwait(false);
            await host.Services.GetRequiredService<GpuLeaseScheduler>().RecoverInterruptedLeasesAsync(token).ConfigureAwait(false);
            await host.StartAsync(token).ConfigureAwait(false);
            await result._processor.StartAsync(token).ConfigureAwait(false);
            await result._deadlines.StartAsync(token).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await result.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await _processor.StopAsync(stop.Token).ConfigureAwait(false);
            await _deadlines.StopAsync(stop.Token).ConfigureAwait(false);
            await _host.StopAsync(stop.Token).ConfigureAwait(false);
        }
        finally { _host.Dispose(); }
    }

    private sealed class FrozenSearchHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Method != HttpMethod.Get || request.RequestUri?.Host != "searxng.benchmark.invalid"
                || request.RequestUri.AbsolutePath != "/search") throw new HttpRequestException("Unexpected frozen-search fixture request.");
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    results = new[] { new
                    {
                        title = "Frozen benchmark index: Python 3.13 urllib.parse reference",
                        url = BenchmarkTasks.ResearchUrl,
                        content = "urlencode(query, doseq=False) — inspect the actual Python reference for sequence handling.",
                        engine = "frozen-benchmark-index-not-live-search",
                    } },
                    unresponsive_engines = Array.Empty<string>(),
                }), Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }
}
