using GoAi.Server.Core.Security;

namespace GoAi.Server.Tests;

public sealed class ApiKeyStoreTests
{
    [Fact]
    public async Task BootstrapKeyIsCreatedOnceAndValidatedByConstantHashPath()
    {
        using var context = new TestServerContext();
        var store = new ApiKeyStore(context.Database);

        var first = await store.EnsureBootstrapKeyAsync();
        var second = await store.EnsureBootstrapKeyAsync();

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.StartsWith("goai_", first.PlainText, StringComparison.Ordinal);
        Assert.True(await store.ValidateAsync(first.PlainText));
        Assert.False(await store.ValidateAsync(first.PlainText + "x"));

        await using var connection = await context.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT key_hash FROM api_keys WHERE key_id = $id;";
        command.Parameters.AddWithValue("$id", first.KeyId);
        var storedHash = Assert.IsType<byte[]>(await command.ExecuteScalarAsync());
        Assert.Equal(32, storedHash.Length);
        Assert.NotEqual(System.Text.Encoding.UTF8.GetBytes(first.PlainText), storedHash);
    }

    [Fact]
    public async Task RevokedKeyStopsAuthenticating()
    {
        using var context = new TestServerContext();
        var store = new ApiKeyStore(context.Database);
        var issued = await store.CreateKeyAsync("Test");

        Assert.True(await store.ValidateAsync(issued.PlainText));
        await store.RevokeAsync(issued.KeyId);
        Assert.False(await store.ValidateAsync(issued.PlainText));
    }

    [Fact]
    public async Task RotationListsUsageAndKeepsOneActiveKey()
    {
        using var context = new TestServerContext();
        var store = new ApiKeyStore(context.Database);
        var first = await store.CreateKeyAsync("Erster Client");

        Assert.True(await store.ValidateAsync(first.PlainText));
        var initial = Assert.Single(await store.ListAsync());
        Assert.Equal(first.KeyId, initial.KeyId);
        Assert.NotNull(initial.LastUsedAt);
        Assert.False(await store.TryRevokePreservingOneAsync(first.KeyId));

        var second = await store.CreateKeyAsync("Zweiter Client");
        Assert.True(await store.TryRevokePreservingOneAsync(first.KeyId));
        var active = Assert.Single(await store.ListAsync());
        Assert.Equal(second.KeyId, active.KeyId);

        var all = await store.ListAsync(includeRevoked: true);
        Assert.Equal(2, all.Count);
        Assert.NotNull(Assert.Single(all, key => key.KeyId == first.KeyId).RevokedAt);
    }
}
