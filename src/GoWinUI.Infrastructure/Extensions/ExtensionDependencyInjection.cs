using GoWinUI.Core.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GoWinUI.Infrastructure.Extensions;

public static class ExtensionDependencyInjection
{
    public static IServiceCollection AddAssistantExtensionFoundation(
        this IServiceCollection services,
        AextPackageStoreOptions storeOptions,
        AextPackageInspectorOptions? inspectorOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(storeOptions);
        services.TryAddSingleton<IExtensionActionCatalog>(_ => ExtensionActionCatalog.CreateWithBuiltIns());
        services.TryAddSingleton<IAextPackageInspector>(provider =>
            new AextPackageInspector(provider.GetService<IAextSignatureVerifier>(), inspectorOptions));
        services.TryAddSingleton<IAextPackageStore>(provider =>
            new FileSystemAextPackageStore(provider.GetRequiredService<IAextPackageInspector>(), storeOptions));
        return services;
    }
}
