using System.Text.Json;
using System.Text.Json.Nodes;

namespace GoWinUI.BricsCad.Protocol;

public sealed class BridgeProtocolException : IOException
{
    public BridgeProtocolException(string message)
        : base(message)
    {
    }

    public BridgeProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class BridgeFrameReader
{
    private const int InitialBufferBytes = 64 * 1024;
    private readonly int _maximumFrameBytes;
    private byte[] _buffer;
    private int _count;
    private bool _endOfStream;

    public BridgeFrameReader(int maximumFrameBytes = BridgeProtocol.MaximumFrameBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFrameBytes, 1);

        _maximumFrameBytes = maximumFrameBytes;
        _buffer = new byte[Math.Min(InitialBufferBytes, maximumFrameBytes + 1)];
    }

    public async ValueTask<JsonObject?> ReadObjectAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        while (true)
        {
            int newlineIndex = Array.IndexOf(_buffer, (byte)'\n', 0, _count);
            if (newlineIndex >= 0)
            {
                int payloadLength = newlineIndex;
                if (payloadLength > 0 && _buffer[payloadLength - 1] == (byte)'\r')
                {
                    payloadLength--;
                }

                JsonObject? result = payloadLength == 0
                    ? null
                    : ParseObject(_buffer.AsSpan(0, payloadLength));
                Consume(newlineIndex + 1);
                if (result is not null)
                {
                    return result;
                }

                continue;
            }

            if (_count > _maximumFrameBytes)
            {
                throw new BridgeProtocolException(
                    $"Bridge frame exceeds {_maximumFrameBytes} UTF-8 bytes.");
            }

            if (_endOfStream)
            {
                if (_count == 0)
                {
                    return null;
                }

                JsonObject final = ParseObject(_buffer.AsSpan(0, _count));
                _count = 0;
                return final;
            }

            EnsureReadCapacity();
            int read = await stream.ReadAsync(
                _buffer.AsMemory(_count, _buffer.Length - _count),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                _endOfStream = true;
            }
            else
            {
                _count += read;
            }
        }
    }

    private static JsonObject ParseObject(ReadOnlySpan<byte> utf8)
    {
        try
        {
            return JsonNode.Parse(utf8)?.AsObject()
                ?? throw new BridgeProtocolException("Bridge frame must contain a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new BridgeProtocolException("Bridge frame contains invalid JSON.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new BridgeProtocolException("Bridge frame must contain a JSON object.", exception);
        }
    }

    private void EnsureReadCapacity()
    {
        if (_count < _buffer.Length)
        {
            return;
        }

        int maximumBufferBytes = checked(_maximumFrameBytes + 1);
        if (_buffer.Length >= maximumBufferBytes)
        {
            throw new BridgeProtocolException(
                $"Bridge frame exceeds {_maximumFrameBytes} UTF-8 bytes.");
        }

        int nextLength = Math.Min(maximumBufferBytes, checked(_buffer.Length * 2));
        Array.Resize(ref _buffer, nextLength);
    }

    private void Consume(int bytes)
    {
        int remaining = _count - bytes;
        if (remaining > 0)
        {
            Buffer.BlockCopy(_buffer, bytes, _buffer, 0, remaining);
        }

        _count = remaining;
    }
}

public static class BridgeJsonFraming
{
    public static ValueTask WriteObjectAsync(
        Stream stream,
        JsonObject message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return WriteBytesAsync(stream, JsonSerializer.SerializeToUtf8Bytes(message, BridgeProtocol.JsonOptions), cancellationToken);
    }

    public static ValueTask WriteAsync<T>(
        Stream stream,
        T message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return WriteBytesAsync(stream, JsonSerializer.SerializeToUtf8Bytes(message, BridgeProtocol.JsonOptions), cancellationToken);
    }

    private static async ValueTask WriteBytesAsync(
        Stream stream,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (payload.Length > BridgeProtocol.MaximumFrameBytes)
        {
            throw new BridgeProtocolException(
                $"Bridge frame exceeds {BridgeProtocol.MaximumFrameBytes} UTF-8 bytes.");
        }

        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
