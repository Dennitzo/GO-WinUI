using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Data;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text.Json;

namespace GoAi.Server.Core.Storage;

public sealed class ArtifactService
{
    private readonly GoAiDatabase _database;
    private readonly GoAiServerOptions _options;

    public ArtifactService(GoAiDatabase database, IOptions<GoAiServerOptions> options)
    {
        _database = database;
        _options = options.Value;
    }

    public async Task<ArtifactDescriptor> ImportAsync(
        string sourcePath,
        string fileName,
        string mediaType,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Artifact source does not exist.", sourcePath);
        }

        var artifactId = $"artifact-{Guid.NewGuid():N}";
        Directory.CreateDirectory(_options.ArtifactDirectory);
        var destination = Path.Combine(_options.ArtifactDirectory, artifactId + ".bin");
        await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
        {
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }

        var info = new FileInfo(destination);
        await using var hashStream = info.OpenRead();
        var sha = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        var created = DateTimeOffset.UtcNow;
        var expires = created.AddHours(24);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO artifacts(
                artifact_id, file_name, media_type, total_length, sha256, file_path,
                metadata_json, created_at, expires_at)
            VALUES($id, $name, $media, $length, $sha, $path, $metadata, $created, $expires);
            """;
        command.Parameters.AddWithValue("$id", artifactId);
        command.Parameters.AddWithValue("$name", fileName);
        command.Parameters.AddWithValue("$media", mediaType);
        command.Parameters.AddWithValue("$length", info.Length);
        command.Parameters.AddWithValue("$sha", sha);
        command.Parameters.AddWithValue("$path", destination);
        command.Parameters.AddWithValue("$metadata", metadata is null ? DBNull.Value : JsonSerializer.Serialize(metadata, _database.JsonOptions));
        command.Parameters.AddWithValue("$created", GoAiDatabase.FormatTimestamp(created));
        command.Parameters.AddWithValue("$expires", GoAiDatabase.FormatTimestamp(expires));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return new ArtifactDescriptor(artifactId, fileName, mediaType, info.Length, sha, created, expires, metadata);
    }

    public async Task<ArtifactFile?> ResolveAsync(string artifactId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT file_name, media_type, total_length, sha256, file_path, metadata_json, created_at, expires_at
            FROM artifacts WHERE artifact_id = $id;
            """;
        command.Parameters.AddWithValue("$id", artifactId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var path = reader.GetString(4);
        if (!File.Exists(path))
        {
            return null;
        }

        IReadOnlyDictionary<string, string>? metadata = reader.IsDBNull(5)
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(5), _database.JsonOptions);
        var descriptor = new ArtifactDescriptor(
            artifactId,
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetString(3),
            GoAiDatabase.ParseTimestamp(reader.GetString(6)),
            GoAiDatabase.ParseTimestamp(reader.GetString(7)),
            metadata);
        return new ArtifactFile(descriptor, path);
    }
}

public sealed record ArtifactFile(ArtifactDescriptor Descriptor, string Path);
