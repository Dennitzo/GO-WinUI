using GoAi.Contracts;
using Microsoft.AspNetCore.Http;
using System.Text.Json;

namespace GoAi.Server.Core.Gateway;

internal static class GatewayRequestReader
{
    public static async Task<byte[]> ReadBinaryAsync(
        HttpContext context,
        int maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (context.Request.ContentLength is > 0 && context.Request.ContentLength > maximumBytes)
        {
            throw new ArgumentException($"Binary request exceeds the {maximumBytes}-byte protocol limit.");
        }

        using var buffer = new MemoryStream();
        var bytes = new byte[Math.Min(64 * 1024, maximumBytes)];
        while (true)
        {
            var read = await context.Request.Body.ReadAsync(bytes, context.RequestAborted).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            if (buffer.Length + read > maximumBytes)
            {
                throw new ArgumentException($"Binary request exceeds the {maximumBytes}-byte protocol limit.");
            }
            buffer.Write(bytes, 0, read);
        }
        return buffer.ToArray();
    }

    public static async Task<T> ReadJsonAsync<T>(
        HttpContext context,
        JsonSerializerOptions jsonOptions)
    {
        if (context.Request.ContentLength is > GoAiProtocol.MaximumJsonBytes)
        {
            throw new ArgumentException("JSON request exceeds the 2 MiB protocol limit.");
        }

        if (!context.Request.HasJsonContentType())
        {
            throw new JsonException("Request body must use application/json.");
        }

        using var buffer = new MemoryStream();
        var bytes = new byte[64 * 1024];
        while (true)
        {
            var read = await context.Request.Body.ReadAsync(bytes, context.RequestAborted).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            if (buffer.Length + read > GoAiProtocol.MaximumJsonBytes)
            {
                throw new ArgumentException("JSON request exceeds the 2 MiB protocol limit.");
            }
            buffer.Write(bytes, 0, read);
        }

        buffer.Position = 0;
        return await JsonSerializer.DeserializeAsync<T>(
            buffer,
            jsonOptions,
            context.RequestAborted).ConfigureAwait(false)
            ?? throw new JsonException("Request body is required.");
    }
}
