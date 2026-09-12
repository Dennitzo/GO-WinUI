using GoAi.Contracts;
using GoAi.Server.Core.Audio;
using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Data;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Research;
using GoAi.Server.Core.Runs;
using GoAi.Server.Core.Runtime;
using GoAi.Server.Core.Security;
using GoAi.Server.Core.Status;
using GoAi.Server.Core.Storage;
using GoAi.Server.Core.Workers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Net;

namespace GoAi.Server.Core.Gateway;

public static class GoAiServerHostExtensions
{
    public static IServiceCollection AddGoAiServerServices(
        this IServiceCollection services,
        GoAiServerOptions options,
        bool includeHostedServices)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton<IOptions<GoAiServerOptions>>(Options.Create(options));
        services.AddSingleton<ServerRuntimeState>();
        services.AddSingleton<GoAiDatabase>();
        services.AddSingleton<RunEventNotifier>();
        services.AddSingleton<RunRepository>();
        services.AddSingleton<RunWorkChannel>();
        services.AddSingleton<ModelRouter>();
        services.AddSingleton<AgentToolCatalog>();
        services.AddSingleton<AgentToolExecutor>();
        services.AddSingleton<GpuLeaseScheduler>();
        services.AddSingleton<ServiceActivityTracker>();
        services.AddSingleton<GpuStatusService>();
        services.AddSingleton<UploadService>();
        services.AddSingleton<ArtifactService>();
        services.AddSingleton<CapabilityService>();
        services.AddSingleton<ReadinessService>();
        services.AddSingleton<ServiceProbeService>();
        services.AddSingleton<ServerMetricsService>();
        services.AddSingleton<WebResearchService>();
        services.AddSingleton<UtteranceIntentService>();
        services.AddSingleton<WorkerOrchestrator>();
        services.AddSingleton<LiveCaptionService>();
        services.AddHttpClient();
        services.AddSingleton(static provider => new ModelRuntimeClient(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(ModelRuntimeClient)),
            provider.GetRequiredService<IOptions<GoAiServerOptions>>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ModelRuntimeClient>>()));
        services.AddHttpClient<WorkerApiClient>();
        services.AddSingleton<RunProcessor>();
        services.AddSingleton<ClientToolDeadlineService>();
        if (includeHostedServices)
        {
            services.AddHostedService<ServerInitializationService>();
            services.AddHostedService<SharedModelWarmupService>();
            services.AddHostedService(static provider => provider.GetRequiredService<RunProcessor>());
            services.AddHostedService(static provider => provider.GetRequiredService<ClientToolDeadlineService>());
            services.AddHostedService<StorageCleanupService>();
            services.AddHostedService(static provider => provider.GetRequiredService<LiveCaptionService>());
        }

        return services;
    }

    public static IHostBuilder ConfigureGoAiServer(
        this IHostBuilder builder,
        Action<GoAiServerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new GoAiServerOptions();
        configure?.Invoke(options);

        builder.ConfigureServices(services => services.AddGoAiServerServices(options, includeHostedServices: true));

        builder.ConfigureWebHostDefaults(webBuilder =>
        {
            webBuilder.SuppressStatusMessages(true);
            webBuilder.UseKestrel(kestrel =>
            {
                kestrel.ListenAnyIP(options.GatewayPort, listen =>
                {
                    listen.Protocols = HttpProtocols.Http1;
                });
                kestrel.Limits.MaxRequestBodySize = GoAiProtocol.UploadChunkSize + (128 * 1024);
                kestrel.AddServerHeader = false;
            });
            webBuilder.Configure(application =>
            {
                application.UseMiddleware<ProblemDetailsMiddleware>();
                application.UseRouting();
                application.UseMiddleware<ApiRateLimitMiddleware>();
                application.UseEndpoints(GatewayEndpoints.Map);
            });
        });

        return builder;
    }
}
