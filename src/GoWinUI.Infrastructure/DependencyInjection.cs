using GoWinUI.Core.Chat;
using GoWinUI.Core.Contracts;
using GoWinUI.Infrastructure.AI;
using GoWinUI.Infrastructure.Backup;
using GoWinUI.Infrastructure.Documents;
using GoWinUI.Infrastructure.Logging;
using GoWinUI.Infrastructure.Projects;
using GoWinUI.Infrastructure.Repositories;
using GoWinUI.Infrastructure.Settings;
using GoWinUI.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GoWinUI.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddGoInfrastructure(this IServiceCollection services, Action<GoInfrastructureOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddLogging();
        var options = new GoInfrastructureOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddSingleton<SqliteDatabase>();
        services.AddSingleton<IGoDatabase>(static provider => provider.GetRequiredService<SqliteDatabase>());
        services.AddSingleton<IChatRepository, SqliteChatRepository>();
        services.AddSingleton<IWorkflowRepository, SqliteWorkflowRepository>();
        services.AddSingleton<ICodingCampaignRepository, SqliteCodingCampaignRepository>();
        services.AddSingleton<IPromptTriggerRepository, SqlitePromptTriggerRepository>();
        services.AddSingleton<IAssistantAttachmentRepository, SqliteAssistantAttachmentRepository>();
        services.AddSingleton<IChatArtifactRepository, SqliteChatArtifactRepository>();
        services.AddSingleton<IGoAiRunRepository, SqliteGoAiRunRepository>();
        services.AddSingleton<IClientToolExecutionRepository, SqliteClientToolExecutionRepository>();
        services.AddSingleton<IProjectRepository, SqliteProjectRepository>();
        services.AddSingleton<IProjectAssetWorkingCopyService, ProjectAssetWorkingCopyService>();
        services.AddSingleton<IBinaryObjectStore, SqliteBinaryObjectStore>();
        services.AddSingleton<IDocumentIngestor, DocumentIngestor>();
        services.AddSingleton<IContextAssembler, ContextAssembler>();
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddSingleton<IBackupService, ZipBackupService>();
        services.AddSingleton<RingBufferLoggerProvider>();
        services.AddSingleton<ISessionLog>(static provider => provider.GetRequiredService<RingBufferLoggerProvider>());
        services.AddSingleton<ILoggerProvider>(static provider => provider.GetRequiredService<RingBufferLoggerProvider>());
        services.AddHttpClient<ILmStudioClient, LmStudioClient>(static client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<IChatOrchestrator, ChatOrchestrator>();
        return services;
    }
}
