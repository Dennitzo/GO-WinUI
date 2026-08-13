using GoAi.Server.Core.Data;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;

namespace GoAi.Server.Core.Security;

public sealed class ApiKeyStore
{
    private readonly GoAiDatabase _database;

    public ApiKeyStore(GoAiDatabase database)
    {
        _database = database;
    }

    public async Task<IssuedApiKey?> EnsureBootstrapKeyAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM api_keys WHERE revoked_at IS NULL;";
        var existing = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        return existing > 0
            ? null
            : await CreateKeyCoreAsync(connection, "Erster GO Client", cancellationToken).ConfigureAwait(false);
    }

    public async Task<IssuedApiKey> CreateKeyAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await CreateKeyCoreAsync(connection, name.Trim(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ValidateAsync(string? presentedKey, CancellationToken cancellationToken = default)
    {
        if (!TryReadKeyId(presentedKey, out var keyId))
        {
            return false;
        }

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT key_hash FROM api_keys WHERE key_id = $id AND revoked_at IS NULL;";
        command.Parameters.AddWithValue("$id", keyId);
        var stored = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as byte[];
        if (stored is null)
        {
            return false;
        }

        var actual = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(presentedKey!));
        var valid = CryptographicOperations.FixedTimeEquals(stored, actual);
        if (valid)
        {
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE api_keys SET last_used_at = $now WHERE key_id = $id;";
            update.Parameters.AddWithValue("$now", GoAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
            update.Parameters.AddWithValue("$id", keyId);
            _ = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return valid;
    }

    public async Task RevokeAsync(string keyId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE api_keys SET revoked_at = $now WHERE key_id = $id AND revoked_at IS NULL;";
        command.Parameters.AddWithValue("$now", GoAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", keyId);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TryRevokePreservingOneAsync(string keyId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE api_keys
            SET revoked_at = $now
            WHERE key_id = $id
              AND revoked_at IS NULL
              AND (SELECT COUNT(*) FROM api_keys WHERE revoked_at IS NULL) > 1;
            """;
        command.Parameters.AddWithValue("$now", GoAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", keyId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<IReadOnlyList<ApiKeyInfo>> ListAsync(
        bool includeRevoked = false,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = includeRevoked
            ? "SELECT key_id, name, created_at, last_used_at, revoked_at FROM api_keys ORDER BY created_at DESC;"
            : "SELECT key_id, name, created_at, last_used_at, revoked_at FROM api_keys WHERE revoked_at IS NULL ORDER BY created_at DESC;";
        var result = new List<ApiKeyInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ApiKeyInfo(
                reader.GetString(0),
                reader.GetString(1),
                GoAiDatabase.ParseTimestamp(reader.GetString(2)),
                reader.IsDBNull(3) ? null : GoAiDatabase.ParseTimestamp(reader.GetString(3)),
                reader.IsDBNull(4) ? null : GoAiDatabase.ParseTimestamp(reader.GetString(4))));
        }
        return result;
    }

    private static async Task<IssuedApiKey> CreateKeyCoreAsync(
        SqliteConnection connection,
        string name,
        CancellationToken cancellationToken)
    {
        var keyId = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var plainText = $"goai_{keyId}_{secret}";
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(plainText));
        var now = DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO api_keys(key_id, key_hash, name, created_at)
            VALUES($id, $hash, $name, $created);
            """;
        command.Parameters.AddWithValue("$id", keyId);
        command.Parameters.Add("$hash", SqliteType.Blob).Value = hash;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$created", GoAiDatabase.FormatTimestamp(now));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return new IssuedApiKey(keyId, plainText, now);
    }

    private static bool TryReadKeyId(string? presentedKey, out string keyId)
    {
        keyId = string.Empty;
        if (string.IsNullOrWhiteSpace(presentedKey) || !presentedKey.StartsWith("goai_", StringComparison.Ordinal))
        {
            return false;
        }

        var separator = presentedKey.IndexOf('_', 5);
        if (separator <= 5 || separator == presentedKey.Length - 1)
        {
            return false;
        }

        keyId = presentedKey[5..separator];
        return keyId.Length == 12 && keyId.All(Uri.IsHexDigit);
    }
}

public sealed record ApiKeyInfo(
    string KeyId,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt);
