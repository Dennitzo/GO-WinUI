using GoWinUI.Core.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace GoWinUI.Infrastructure.Memory;

public static class ProjectMemoryDependencyInjection
{
    public static IServiceCollection AddProjectMemory(
        this IServiceCollection services,
        Action<ProjectMemoryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new ProjectMemoryOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddSingleton<SqliteProjectMemoryStore>();
        services.AddSingleton<IProjectMemoryStore>(static provider => provider.GetRequiredService<SqliteProjectMemoryStore>());
        return services;
    }
}
