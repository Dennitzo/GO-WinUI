using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Security.Cryptography;

namespace GoAi.Server.Core.Storage;

public sealed class UploadService
{
    private readonly GoAiDatabase _database;
    private readonly GoAiServerOptions _options;

    public UploadService(GoAiDatabase database, IOptions<GoAiServerOptions> options)
    {
        _database = database;
        _options = options.Value;
    }

    public async Task<UploadCreated> CreateAsync(
        UploadManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ValidateManifest(manifest);
        var chunkCount = checked((int)Math.Ceiling(manifest.Length / (double)manifest.ChunkSize));
        if (manifest.ChunkCount is { } requestedChunkCount && requestedChunkCount != chunkCount)
        {
            throw new ArgumentException("The declared chunk count does not match the upload length.", nameof(manifest));
        }

        var uploadId = $"upload-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var expires = now.AddHours(2);
        Directory.CreateDirectory(Path.Combine(_options.UploadDirectory, uploadId));
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO uploads(
                upload_id, file_name, media_type, total_length, total_sha256,
                chunk_size, chunk_count, state, created_at, expires_at)
            VALUES($id, $name, $media, $length, $sha, $chunkSize, $chunkCount, 'pending', $created, $expires);
            """;
        command.Parameters.AddWithValue("$id", uploadId);
        command.Parameters.AddWithValue("$name", manifest.FileName);
        command.Parameters.AddWithValue("$media", manifest.MediaType);
        command.Parameters.AddWithValue("$length", manifest.Length);
        command.Parameters.AddWithValue("$sha", NormalizeSha256(manifest.Sha256));
        command.Parameters.AddWithValue("$chunkSize", manifest.ChunkSize);
        command.Parameters.AddWithValue("$chunkCount", chunkCount);
        command.Parameters.AddWithValue("$created", GoAiDatabase.FormatTimestamp(now));
        command.Parameters.AddWithValue("$expires", GoAiDatabase.FormatTimestamp(expires));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return new UploadCreated(uploadId, manifest.ChunkSize, chunkCount, [], expires);
    }

    public async Task<UploadCreated?> GetAsync(string uploadId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var record = await GetRecordAsync(connection, uploadId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }

        var chunks = await GetChunkIndexesAsync(connection, uploadId, cancellationToken).ConfigureAwait(false);
        return new UploadCreated(uploadId, record.ChunkSize, record.ChunkCount, chunks, record.ExpiresAt);
    }

    public async Task<UploadChunkReceipt> PutChunkAsync(
        string uploadId,
        int index,
        Stream content,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var record = await GetRecordAsync(connection, uploadId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Upload not found.");
        if (!string.Equals(record.State, "pending", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The upload no longer accepts chunks.");
        }

        if (record.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException("The upload has expired.");
        }

        if (index < 0 || index >= record.ChunkCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var maximumLength = index == record.ChunkCount - 1
            ? record.TotalLength - ((long)index * record.ChunkSize)
            : record.ChunkSize;
        var directory = Path.Combine(_options.UploadDirectory, uploadId);
        Directory.CreateDirectory(directory);
        var finalPath = Path.Combine(directory, $"{index:D8}.part");
        var temporaryPath = finalPath + $".{Guid.NewGuid():N}.tmp";
        long length;
        string actualSha;
        try
        {
            (length, actualSha) = await CopyBoundedAndHashAsync(
                content,
                temporaryPath,
                maximumLength,
                cancellationToken).ConfigureAwait(false);
            var normalizedExpected = NormalizeSha256(expectedSha256);
            if (length != maximumLength || !string.Equals(actualSha, normalizedExpected, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Chunk length or SHA-256 does not match its manifest.");
            }

            File.Move(temporaryPath, finalPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO upload_chunks(upload_id, chunk_index, chunk_length, chunk_sha256, file_path, created_at)
            VALUES($upload, $index, $length, $sha, $path, $created)
            ON CONFLICT(upload_id, chunk_index) DO UPDATE SET
                chunk_length = excluded.chunk_length,
                chunk_sha256 = excluded.chunk_sha256,
                file_path = excluded.file_path,
                created_at = excluded.created_at;
            """;
        command.Parameters.AddWithValue("$upload", uploadId);
        command.Parameters.AddWithValue("$index", index);
        command.Parameters.AddWithValue("$length", length);
        command.Parameters.AddWithValue("$sha", actualSha);
        command.Parameters.AddWithValue("$path", finalPath);
        command.Parameters.AddWithValue("$created", GoAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return new UploadChunkReceipt(uploadId, index, actualSha, length, true);
    }

    public async Task<UploadCompleted> CompleteAsync(
        string uploadId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var record = await GetRecordAsync(connection, uploadId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Upload not found.");
        var chunks = await GetChunksAsync(connection, uploadId, cancellationToken).ConfigureAwait(false);
        if (chunks.Count != record.ChunkCount || chunks.Select(static chunk => chunk.Index).Where((index, position) => index != position).Any())
        {
            throw new InvalidOperationException("Upload chunks are incomplete.");
        }

        var directory = Path.Combine(_options.UploadDirectory, uploadId);
        var completedPath = Path.Combine(directory, "payload.bin");
        var temporaryPath = completedPath + ".tmp";
        var buffer = new byte[128 * 1024];
        long totalLength = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            await using (var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length, true))
            {
                foreach (var chunk in chunks)
                {
                    await using var input = new FileStream(chunk.Path, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, true);
                    while (true)
                    {
                        var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        hash.AppendData(buffer.AsSpan(0, read));
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        totalLength += read;
                    }
                }
            }

            var actualSha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (totalLength != record.TotalLength || !string.Equals(actualSha, record.TotalSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Completed upload does not match its manifest.");
            }

            File.Move(temporaryPath, completedPath, overwrite: true);
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE uploads SET state = 'complete' WHERE upload_id = $id;";
            update.Parameters.AddWithValue("$id", uploadId);
            _ = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            foreach (var chunk in chunks)
            {
                File.Delete(chunk.Path);
            }

            return new UploadCompleted(
                uploadId,
                record.FileName,
                record.MediaType,
                totalLength,
                actualSha,
                record.ExpiresAt);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task DeleteAsync(string uploadId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM uploads WHERE upload_id = $id;";
        command.Parameters.AddWithValue("$id", uploadId);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var directory = Path.GetFullPath(Path.Combine(_options.UploadDirectory, uploadId));
        var expectedParent = Path.GetFullPath(_options.UploadDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (directory.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase) && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    public async Task<string?> ResolveCompletedPathAsync(string uploadId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var record = await GetRecordAsync(connection, uploadId, cancellationToken).ConfigureAwait(false);
        if (record is null || !string.Equals(record.State, "complete", StringComparison.Ordinal))
        {
            return null;
        }

        var path = Path.Combine(_options.UploadDirectory, uploadId, "payload.bin");
        return File.Exists(path) ? path : null;
    }

    public async Task<UploadCompleted?> GetCompletedAsync(
        string uploadId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var record = await GetRecordAsync(connection, uploadId, cancellationToken).ConfigureAwait(false);
        if (record is null || !string.Equals(record.State, "complete", StringComparison.Ordinal))
        {
            return null;
        }

        return new UploadCompleted(
            uploadId,
            record.FileName,
            record.MediaType,
            record.TotalLength,
            record.TotalSha256,
            record.ExpiresAt);
    }

    private static void ValidateManifest(UploadManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (string.IsNullOrWhiteSpace(manifest.FileName)
            || !string.Equals(Path.GetFileName(manifest.FileName), manifest.FileName, StringComparison.Ordinal)
            || manifest.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("The upload file name is invalid.", nameof(manifest));
        }

        if (string.IsNullOrWhiteSpace(manifest.MediaType) || !manifest.MediaType.Contains('/', StringComparison.Ordinal))
        {
            throw new ArgumentException("A valid media type is required.", nameof(manifest));
        }

        var maximum = GetMaximumLength(manifest.MediaType);
        if (manifest.Length <= 0 || manifest.Length > maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(manifest), $"Upload exceeds the {maximum} byte limit.");
        }

        if (manifest.ChunkSize != GoAiProtocol.UploadChunkSize)
        {
            throw new ArgumentException("GO AI protocol v1 requires 8 MiB chunks.", nameof(manifest));
        }

        _ = NormalizeSha256(manifest.Sha256);
    }

    private static long GetMaximumLength(string mediaType) => mediaType.Split('/')[0].ToLowerInvariant() switch
    {
        "image" => 25L * 1024 * 1024,
        "audio" => 100L * 1024 * 1024,
        "video" => 500L * 1024 * 1024,
        _ => 100L * 1024 * 1024,
    };

    private static string NormalizeSha256(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length != 64 || !normalized.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("A lowercase or uppercase SHA-256 hex value is required.", nameof(value));
        }

        return normalized;
    }

    private static async Task<(long Length, string Sha256)> CopyBoundedAndHashAsync(
        Stream input,
        string path,
        long expectedMaximum,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        long length = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, true);
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            length += read;
            if (length > expectedMaximum)
            {
                throw new InvalidDataException("Upload chunk exceeds its declared size.");
            }

            hash.AppendData(buffer.AsSpan(0, read));
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return (length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static async Task<UploadRecord?> GetRecordAsync(
        SqliteConnection connection,
        string uploadId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT file_name, media_type, total_length, total_sha256, chunk_size, chunk_count, state, expires_at
            FROM uploads WHERE upload_id = $id;
            """;
        command.Parameters.AddWithValue("$id", uploadId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new UploadRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetString(6),
                GoAiDatabase.ParseTimestamp(reader.GetString(7)))
            : null;
    }

    private static async Task<IReadOnlyList<int>> GetChunkIndexesAsync(
        SqliteConnection connection,
        string uploadId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT chunk_index FROM upload_chunks WHERE upload_id = $id ORDER BY chunk_index;";
        command.Parameters.AddWithValue("$id", uploadId);
        var result = new List<int>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(reader.GetInt32(0));
        }

        return result;
    }

    private static async Task<IReadOnlyList<ChunkRecord>> GetChunksAsync(
        SqliteConnection connection,
        string uploadId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT chunk_index, chunk_length, chunk_sha256, file_path FROM upload_chunks WHERE upload_id = $id ORDER BY chunk_index;";
        command.Parameters.AddWithValue("$id", uploadId);
        var result = new List<ChunkRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ChunkRecord(reader.GetInt32(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3)));
        }

        return result;
    }

    private sealed record UploadRecord(
        string FileName,
        string MediaType,
        long TotalLength,
        string TotalSha256,
        int ChunkSize,
        int ChunkCount,
        string State,
        DateTimeOffset ExpiresAt);

    private sealed record ChunkRecord(int Index, long Length, string Sha256, string Path);
}
