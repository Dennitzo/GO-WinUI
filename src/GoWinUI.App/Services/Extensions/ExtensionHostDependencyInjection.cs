using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace GoWinUI.App.Services.Extensions;

public static class ExtensionHostDependencyInjection
{
    public static IServiceCollection AddExtensionHostSupervisor(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IExtensionHostSupervisor, ExtensionHostSupervisor>();
        services.TryAddSingleton<ExtensionRuntimeService>();
        services.TryAddSingleton<IExtensionRuntimeService>(provider => provider.GetRequiredService<ExtensionRuntimeService>());
        services.AddSingleton<IHostedService>(
            provider => provider.GetRequiredService<ExtensionRuntimeService>());
        return services;
    }
}
