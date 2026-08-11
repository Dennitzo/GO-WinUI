using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GoWinUI.BricsCad.Protocol;

public sealed record BridgeRendezvousDescriptor
{
    [JsonPropertyName("schema")]
    public string Schema { get; init; } = BridgeProtocol.RendezvousSchema;

    [JsonPropertyName("host")]
    public string Host { get; init; } = IPAddress.Loopback.ToString();

    [JsonPropertyName("port")]
    public int Port { get; init; }

    [JsonPropertyName("token")]
    public required string Token { get; init; }

    [JsonPropertyName("protocol")]
    public int Protocol { get; init; } = BridgeProtocol.Version;

    [JsonPropertyName("bridgeBuild")]
    public string BridgeBuild { get; init; } = BridgeProtocol.BridgeBuild;

    [JsonPropertyName("contractVersion")]
    public string ContractVersion { get; init; } = BridgeProtocol.ContractVersion;

    [JsonPropertyName("contractHash")]
    public string ContractHash { get; init; } = ContractIdentity.Sha256;

    [JsonPropertyName("processId")]
    public int ProcessId { get; init; } = Environment.ProcessId;

    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("appBuildId")]
    public string? AppBuildId { get; init; }
}

public static class BridgeRendezvousFile
{
    public static string DirectoryPath
    {
        get
        {
            string? requested = Environment.GetEnvironmentVariable("GO_BRIDGE_DIRECTORY");
            if (!string.IsNullOrWhiteSpace(requested))
            {
                return Path.GetFullPath(requested);
            }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                throw new InvalidOperationException("LOCALAPPDATA is unavailable.");
            }

            return Path.Combine(localAppData, "GO", "Bridge");
        }
    }

    public static string ActivePath => Path.Combine(DirectoryPath, "active.json");

    public static BridgeRendezvousDescriptor Create(int port, string? appBuildId = null)
    {
        var descriptor = new BridgeRendezvousDescriptor
        {
            Port = port,
            Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            AppBuildId = appBuildId
        };
        Validate(descriptor);
        return descriptor;
    }

    public static async Task WriteAsync(
        BridgeRendezvousDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        Validate(descriptor);
        Directory.CreateDirectory(DirectoryPath);
        BridgeRendezvousSecurity.ApplyCurrentUserOnly(DirectoryPath, isDirectory: true);
        string temporaryPath = Path.Combine(
            DirectoryPath,
            $"active.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");

        try
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(descriptor, BridgeProtocol.JsonOptions);
            await File.WriteAllBytesAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            BridgeRendezvousSecurity.ApplyCurrentUserOnly(temporaryPath, isDirectory: false);
            File.Move(temporaryPath, ActivePath, true);
            BridgeRendezvousSecurity.VerifyCurrentUserOnly(ActivePath);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public static bool TryRead(
        out BridgeRendezvousDescriptor? descriptor,
        out string error)
    {
        descriptor = null;
        error = string.Empty;
        try
        {
            BridgeRendezvousSecurity.VerifyCurrentUserOnly(DirectoryPath);
            BridgeRendezvousSecurity.VerifyCurrentUserOnly(ActivePath);
            byte[] json = File.ReadAllBytes(ActivePath);
            descriptor = JsonSerializer.Deserialize<BridgeRendezvousDescriptor>(json, BridgeProtocol.JsonOptions);
            if (descriptor is null)
            {
                error = "Rendezvous file is empty.";
                return false;
            }

            Validate(descriptor);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidDataException)
        {
            descriptor = null;
            error = exception.Message;
            return false;
        }
    }

    public static bool TryDeleteOwned(string token)
    {
        if (!TryRead(out BridgeRendezvousDescriptor? current, out _)
            || current is null
            || !SecureEquals(current.Token, token))
        {
            return false;
        }

        try
        {
            File.Delete(ActivePath);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void Validate(BridgeRendezvousDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!string.Equals(descriptor.Schema, BridgeProtocol.RendezvousSchema, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Unsupported bridge rendezvous schema.");
        }

        if (!IPAddress.TryParse(descriptor.Host, out IPAddress? host) || !IPAddress.IsLoopback(host))
        {
            throw new InvalidDataException("Bridge host must be a numeric loopback address.");
        }

        if (descriptor.Port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
        {
            throw new InvalidDataException("Bridge port is outside the valid TCP range.");
        }

        if (string.IsNullOrWhiteSpace(descriptor.Token)
            || descriptor.Token.Length != 64
            || !IsHex(descriptor.Token))
        {
            throw new InvalidDataException("Bridge token must contain 32 random bytes encoded as hexadecimal.");
        }

        if (descriptor.Protocol != BridgeProtocol.Version
            || !string.Equals(descriptor.BridgeBuild, BridgeProtocol.BridgeBuild, StringComparison.Ordinal)
            || !string.Equals(descriptor.ContractVersion, BridgeProtocol.ContractVersion, StringComparison.Ordinal)
            || !ContractIdentity.MatchesSha256(descriptor.ContractHash))
        {
            throw new InvalidDataException("Bridge protocol or contract identity does not match this build.");
        }

        if (descriptor.ProcessId <= 0)
        {
            throw new InvalidDataException("Bridge process id is invalid.");
        }

        if (descriptor.CreatedAtUtc == default
            || descriptor.CreatedAtUtc > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            throw new InvalidDataException("Bridge creation time is invalid.");
        }
    }

    public static bool IsOwnerProcessRunning(BridgeRendezvousDescriptor descriptor)
    {
        try
        {
            using Process process = Process.GetProcessById(descriptor.ProcessId);
            DateTimeOffset startedAt = new(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            return !process.HasExited && startedAt <= descriptor.CreatedAtUtc.AddSeconds(5);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    public static bool SecureEquals(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        byte[] leftBytes = Encoding.UTF8.GetBytes(left);
        byte[] rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length
            && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static bool IsHex(string value)
    {
        foreach (char character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
