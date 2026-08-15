using System.Buffers;
using System.Security.Cryptography;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using Microsoft.Data.Sqlite;

namespace GoWinUI.Infrastructure.Storage;

public sealed class SqliteBinaryObjectStore(SqliteDatabase database) : IBinaryObjectStore
{
    public const int ChunkSize = 2 * 1024 * 1024;

    public Task<BinaryObjectDescriptor> ImportAsync(Stream source, string contentType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        EnsureFreeSpaceForImport(source);
        return database.WriteAsync(async (connection, transaction, token) =>
        {
            var id = Guid.NewGuid();
            var created = DateTimeOffset.UtcNow;
            var provisionalHash = Convert.ToHexString(SHA256.HashData(id.ToByteArray())).ToLowerInvariant();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO binary_objects(id,sha256,length,content_type,chunk_count,created_at) VALUES($id,$sha,0,$type,0,$created);";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$sha", provisionalHash);
            command.Parameters.AddWithValue("$type", contentType);
            command.Parameters.AddWithValue("$created", created.ToDb());
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
            long length = 0;
            var chunk = 0;
            try
            {
                while (true)
                {
                    var read = await ReadChunkAsync(source, buffer.AsMemory(0, ChunkSize), token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    hash.AppendData(buffer, 0, read);
                    length += read;
                    command.Parameters.Clear();
                    command.CommandText = "INSERT INTO binary_chunks(object_id,chunk_index,data) VALUES($id,$index,$data);";
                    command.Parameters.AddWithValue("$id", id.ToString("D"));
                    command.Parameters.AddWithValue("$index", chunk++);
                    command.Parameters.Add("$data", SqliteType.Blob).Value = buffer.AsSpan(0, read).ToArray();
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }

            var finalHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            command.Parameters.Clear();
            command.CommandText = "SELECT id,sha256,length,content_type,chunk_count,created_at FROM binary_objects WHERE sha256=$sha AND id<>$id LIMIT 1;";
            command.Parameters.AddWithValue("$sha", finalHash);
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            {
                if (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    var existing = ReadDescriptor(reader);
                    await reader.DisposeAsync().ConfigureAwait(false);
                    command.Parameters.Clear();
                    command.CommandText = "DELETE FROM binary_objects WHERE id=$id;";
                    command.Parameters.AddWithValue("$id", id.ToString("D"));
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    return existing;
                }
            }

            command.Parameters.Clear();
            command.CommandText = "UPDATE binary_objects SET sha256=$sha,length=$length,chunk_count=$chunks WHERE id=$id;";
            command.Parameters.AddWithValue("$sha", finalHash);
            command.Parameters.AddWithValue("$length", length);
            command.Parameters.AddWithValue("$chunks", chunk);
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            return new BinaryObjectDescriptor(id, finalHash, length, contentType, chunk, created);
        }, cancellationToken);
    }

    private void EnsureFreeSpaceForImport(Stream source)
    {
        if (!source.CanSeek) return;
        long remaining;
        try { remaining = Math.Max(0, source.Length - source.Position); }
        catch (NotSupportedException) { return; }
        const long fixedReserve = 64L * 1024 * 1024;
        var required = remaining > (long.MaxValue - fixedReserve) / 2 ? long.MaxValue : (remaining * 2) + fixedReserve;
        var root = Path.GetPathRoot(Path.GetFullPath(database.DatabasePath));
        if (string.IsNullOrWhiteSpace(root)) return;
        try
        {
            var available = new DriveInfo(root).AvailableFreeSpace;
            if (available < required)
                throw new IOException($"Für den Import werden einschließlich SQLite/WAL-Reserve mindestens {required:N0} Bytes frei benötigt; verfügbar sind {available:N0} Bytes.");
        }
        catch (DriveNotFoundException)
        {
            // Auf exotischen gemounteten Pfaden bleibt SQLite selbst die maßgebliche Speicherprüfung.
        }
    }

    public async Task<Stream> OpenReadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,sha256,length,content_type,chunk_count,created_at FROM binary_objects WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new KeyNotFoundException($"Binärobjekt '{id}' wurde nicht gefunden.");
        }

        return new ChunkReadStream(database, ReadDescriptor(reader));
    }

    public async Task ExportAsync(Guid id, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        await using var source = await OpenReadAsync(id, cancellationToken).ConfigureAwait(false);
        await source.CopyToAsync(destination, 128 * 1024, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> VerifyAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var source = await OpenReadAsync(id, cancellationToken).ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
            }

            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT sha256 FROM binary_objects WHERE id=$id;";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            var expected = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    public Task DeleteIfUnreferencedAsync(Guid id, CancellationToken cancellationToken = default) => database.WriteAsync(async (connection, transaction, token) =>
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM binary_objects WHERE id=$id
              AND NOT EXISTS(SELECT 1 FROM documents WHERE blob_id=$id)
              AND NOT EXISTS(SELECT 1 FROM project_assets WHERE blob_id=$id)
              AND NOT EXISTS(SELECT 1 FROM project_asset_thumbnails WHERE blob_id=$id)
              AND NOT EXISTS(SELECT 1 FROM assistant_attachments WHERE blob_id=$id)
              AND NOT EXISTS(SELECT 1 FROM chat_artifacts WHERE blob_id=$id);
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }, cancellationToken);

    private static async Task<int> ReadChunkAsync(Stream source, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await source.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static BinaryObjectDescriptor ReadDescriptor(SqliteDataReader reader) => new(
        reader.ReadGuid(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3), reader.GetInt32(4), reader.ReadDate(5));

    private sealed class ChunkReadStream(SqliteDatabase database, BinaryObjectDescriptor descriptor) : Stream
    {
        private long _position;
        private int _loadedChunk = -1;
        private byte[] _buffer = [];

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => descriptor.Length;
        public override long Position
        {
            get => _position;
            set => _ = Seek(value, SeekOrigin.Begin);
        }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            if (_position >= Length || destination.Length == 0)
            {
                return 0;
            }

            var total = 0;
            while (destination.Length > 0 && _position < Length)
            {
                var index = checked((int)(_position / ChunkSize));
                if (_loadedChunk != index)
                {
                    _buffer = await LoadChunkAsync(index, cancellationToken).ConfigureAwait(false);
                    _loadedChunk = index;
                }

                var offset = checked((int)(_position % ChunkSize));
                var count = Math.Min(destination.Length, _buffer.Length - offset);
                if (count <= 0)
                {
                    throw new InvalidDataException("Ein gespeicherter Binärblock fehlt oder ist beschädigt.");
                }

                _buffer.AsMemory(offset, count).CopyTo(destination);
                destination = destination[count..];
                _position += count;
                total += count;
            }

            return total;
        }

        private async Task<byte[]> LoadChunkAsync(int index, CancellationToken cancellationToken)
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT data FROM binary_chunks WHERE object_id=$id AND chunk_index=$index;";
            command.Parameters.AddWithValue("$id", descriptor.Id.ToString("D"));
            command.Parameters.AddWithValue("$index", index);
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as byte[]
                ?? throw new InvalidDataException($"Binärblock {index} fehlt.");
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(Length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            if (target < 0 || target > Length)
            {
                throw new IOException("Die angeforderte Binärposition liegt außerhalb des Objekts.");
            }
            _position = target;
            return _position;
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
