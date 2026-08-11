using GoWinUI.Core.Contracts;
using GoWinUI.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace GoWinUI.Tests;

internal sealed class TestEnvironment : IAsyncDisposable
{
    private TestEnvironment(string directory, ServiceProvider services)
    {
        Directory = directory;
        Services = services;
    }

    internal string Directory { get; }
    internal ServiceProvider Services { get; }
    internal T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    internal static async Task<TestEnvironment> CreateAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"GO-tests-{Guid.NewGuid():N}");
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddGoInfrastructure(options => options.DataDirectory = directory);
        var services = collection.BuildServiceProvider(validateScopes: true);
        var environment = new TestEnvironment(directory, services);
        await environment.Get<IGoDatabase>().InitializeAsync().ConfigureAwait(false);
        return environment;
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync().ConfigureAwait(false);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { System.IO.Directory.Delete(Directory, recursive: true); } catch (IOException) { }
    }
}
