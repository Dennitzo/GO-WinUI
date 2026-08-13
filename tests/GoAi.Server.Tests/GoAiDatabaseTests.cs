namespace GoAi.Server.Tests;

public sealed class GoAiDatabaseTests
{
    [Fact]
    public async Task InitializationAppliesVersionedSchemaInWalMode()
    {
        using var context = new TestServerContext();
        await using var connection = await context.Database.OpenConnectionAsync();

        await using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(1L, Convert.ToInt64(await version.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));

        await using var migration = connection.CreateCommand();
        migration.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version = 1;";
        Assert.Equal(1L, Convert.ToInt64(await migration.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));

        await using var journal = connection.CreateCommand();
        journal.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", Convert.ToString(await journal.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture), ignoreCase: true);
    }
}
