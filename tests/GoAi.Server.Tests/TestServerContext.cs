using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace GoAi.Server.Tests;

internal sealed class TestServerContext : IDisposable
{
    public TestServerContext()
    {
        Root = Path.Combine(Path.GetTempPath(), "GO-AI-Server-Tests", Guid.NewGuid().ToString("N"));
        Options = new GoAiServerOptions
        {
            DataDirectory = Root,
        };
        Database = new GoAiDatabase(Microsoft.Extensions.Options.Options.Create(Options));
    }

    public string Root { get; }

    public GoAiServerOptions Options { get; }

    public GoAiDatabase Database { get; }

    public IOptions<GoAiServerOptions> WrappedOptions => Microsoft.Extensions.Options.Options.Create(Options);

    public void Dispose()
    {
        Database.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
