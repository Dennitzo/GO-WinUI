using GoAi.Client;
using GoAi.Contracts;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using Microsoft.Extensions.Logging;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace GoWinUI.App.Services;

public sealed record GoAiConnectionStatus(
    bool IsReady,
    string Message,
    CapabilitySnapshot? Capabilities = null,
    HealthSnapshot? Health = null);

public sealed class GoAiConnectionService(
    SettingsCoordinator settings,
    IAiSecretStore secrets,
    ILogger<GoAiConnectionService> logger)
{
    private const int MaximumBootstrapKeyLength = 512;
    private static readonly Action<ILogger, string, Exception?> ConnectionFailed = LoggerMessage.Define<string>(
        LogLevel.Warning,
        new EventId(5200, nameof(ConnectionFailed)),
        "GO AI Server connection check failed ({FailureKind}).");

    public async Task<GoAiClient> CreateClientAsync(CancellationToken cancellationToken = default)
    {
        var current = settings.Current;
        if (!Uri.TryCreate(current.GoAiServerUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseAddress))
        {
            throw new InvalidOperationException("Die GO-AI-Serveradresse ist ungültig.");
        }
        if (baseAddress.Scheme == Uri.UriSchemeHttp && !baseAddress.IsLoopback)
        {
            throw new InvalidOperationException("GO AI Server akzeptiert unverschlüsseltes HTTP nur auf Loopback.");
        }
        var apiKey = await secrets.GetApiKeyAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Für GO AI Server ist noch kein API-Schlüssel gespeichert.");
        }

        var handler = new HttpClientHandler
        {
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, certificate, chain, errors) =>
                ValidateCertificate(certificate, chain, errors),
        };
        var httpClient = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = baseAddress,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("GO-WinUI/1.0");
        return new GoAiClient(httpClient, apiKey, ownsHttpClient: true);
    }

    public async Task<bool> TryProvisionLocalHostAsync(CancellationToken cancellationToken = default)
    {
        var dataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "GO-AI-Server");
        var rootCertificatePath = Path.Combine(
            dataRoot,
            "Caddy", "data", "caddy", "pki", "authorities", "local", "root.crt");
        var bootstrapKeyPath = Path.Combine(dataRoot, "Secrets", "bootstrap-client-key.once");
        if (!File.Exists(rootCertificatePath) || !File.Exists(bootstrapKeyPath))
        {
            return false;
        }

        try
        {
            var apiKey = (await File.ReadAllTextAsync(bootstrapKeyPath, cancellationToken).ConfigureAwait(false)).Trim();
            if (apiKey.Length is < 32 or > MaximumBootstrapKeyLength
                || apiKey.Any(char.IsWhiteSpace))
            {
                throw new InvalidDataException("Der lokale Bootstrap-Schlüssel besitzt ein ungültiges Format.");
            }
            var existingApiKey = await secrets.GetApiKeyAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(existingApiKey)
                && !FixedTimeEquals(existingApiKey, apiKey))
            {
                // A manually imported client credential must never be replaced with a host-local
                // bootstrap credential merely because GO happens to run on the server host.
                return false;
            }

            using var certificate = X509CertificateLoader.LoadCertificateFromFile(rootCertificatePath);
            var fingerprint = Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256)).ToLowerInvariant();
            using (var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser))
            {
                store.Open(OpenFlags.ReadWrite);
                if (!store.Certificates.Any(item =>
                        string.Equals(item.Thumbprint, certificate.Thumbprint, StringComparison.OrdinalIgnoreCase)))
                {
                    store.Add(certificate);
                }
            }

            if (string.IsNullOrWhiteSpace(existingApiKey))
            {
                await secrets.SetApiKeyAsync(apiKey, cancellationToken).ConfigureAwait(false);
            }
            await settings.UpdateAsync(current => current with
            {
                AiProvider = AiProviderKind.GoAiServer,
                GoAiCaFingerprint = fingerprint,
                GoAiProtocolVersion = GoAiProtocol.Version,
                GoAiConnectionName = "Lokaler GO AI Server",
            }, cancellationToken).ConfigureAwait(false);

            var status = await TestAsync(cancellationToken).ConfigureAwait(false);
            if (!status.IsReady)
            {
                LocalProvisioningDeferred(logger, status.Message, null);
                return true;
            }

            try
            {
                File.Delete(bootstrapKeyPath);
            }
            catch (IOException exception)
            {
                LocalProvisioningCleanupFailed(logger, exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                LocalProvisioningCleanupFailed(logger, exception);
            }
            LocalProvisioningCompleted(logger, null);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LocalProvisioningFailed(logger, exception.GetType().Name, exception);
            return false;
        }
    }

    public async Task<GoAiConnectionStatus> TestAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = await CreateClientAsync(cancellationToken).ConfigureAwait(false);
            var health = await client.GetReadyHealthAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(health.ProtocolVersion, settings.Current.GoAiProtocolVersion, StringComparison.Ordinal))
            {
                return new(false, $"Protokollabweichung: Server {health.ProtocolVersion}, GO {settings.Current.GoAiProtocolVersion}.", Health: health);
            }
            if (!string.Equals(health.Status, "ready", StringComparison.OrdinalIgnoreCase))
            {
                var detail = string.IsNullOrWhiteSpace(health.Reason) ? "Server nicht bereit" : health.Reason;
                return new(false, detail, Health: health);
            }
            var capabilities = await client.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            return new(true, $"Verbunden · {capabilities.Models.Count} Modelle · {capabilities.ServerTools.Count} Servertools", capabilities, health);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ConnectionFailed(logger, exception.GetType().Name, exception);
            return new(false, FriendlyConnectionError(exception));
        }
    }

    public async Task<GoAiImportedConnection> ImportConnectionBundleAsync(
        string connectionJsonPath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(connectionJsonPath);
        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 32 * 1024, true);
        var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("schema", out var schema)
                || !string.Equals(schema.GetString(), "go.ai.connection.v1", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Das Verbindungspaket verwendet kein unterstütztes GO-AI-Schema.");
            }
            var serverUrl = root.GetProperty("serverUrl").GetString()
                ?? throw new InvalidDataException("Die Serveradresse fehlt.");
            if (!Uri.TryCreate(serverUrl.TrimEnd('/') + "/", UriKind.Absolute, out var serverUri)
                || (serverUri.Scheme != Uri.UriSchemeHttps && serverUri.Scheme != Uri.UriSchemeHttp)
                || (serverUri.Scheme == Uri.UriSchemeHttp && !serverUri.IsLoopback))
            {
                throw new InvalidDataException("Das Verbindungspaket enthält keine zulässige HTTPS-Serveradresse.");
            }
            var protocolVersion = root.GetProperty("protocolVersion").GetString()
                ?? throw new InvalidDataException("Die Protokollversion fehlt.");
            var fingerprint = NormalizeFingerprint(root.GetProperty("caSha256Fingerprint").GetString())
                ?? throw new InvalidDataException("Der CA-Fingerprint ist ungültig.");
            var certificateName = root.GetProperty("caCertificate").GetString()
                ?? throw new InvalidDataException("Das Root-Zertifikat fehlt.");
            var certificatePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fullPath)!, certificateName));
            if (!certificatePath.StartsWith(Path.GetDirectoryName(fullPath)! + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(certificatePath))
            {
                throw new InvalidDataException("Das Root-Zertifikat liegt nicht im Verbindungspaket.");
            }
            using var certificate = X509CertificateLoader.LoadCertificateFromFile(certificatePath);
            var actual = Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actual),
                    Convert.FromHexString(fingerprint)))
            {
                throw new InvalidDataException("Root-Zertifikat und CA-Fingerprint stimmen nicht überein.");
            }
            using (var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser))
            {
                store.Open(OpenFlags.ReadWrite);
                if (!store.Certificates.Any(item => string.Equals(item.Thumbprint, certificate.Thumbprint, StringComparison.OrdinalIgnoreCase)))
                {
                    store.Add(certificate);
                }
            }
            await settings.UpdateAsync(current => current with
            {
                AiProvider = AiProviderKind.GoAiServer,
                GoAiServerUrl = serverUri.ToString().TrimEnd('/'),
                GoAiProtocolVersion = protocolVersion,
                GoAiCaFingerprint = fingerprint,
                GoAiConnectionName = Path.GetFileName(Path.GetDirectoryName(fullPath)) ?? "GO AI Server",
            }, cancellationToken).ConfigureAwait(false);
            return new GoAiImportedConnection(serverUri.ToString().TrimEnd('/'), protocolVersion, fingerprint, certificate.Subject);
        }
    }

    private static bool ValidateCertificate(
        X509Certificate2? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        if (certificate is null || chain is null || errors != SslPolicyErrors.None)
        {
            return false;
        }
        return true;
    }

    private static string? NormalizeFingerprint(string? value)
    {
        var normalized = new string((value ?? string.Empty).Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
        return normalized.Length == 64 ? normalized : null;
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        try
        {
            return leftBytes.Length == rightBytes.Length
                && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private static string FriendlyConnectionError(Exception exception) => exception switch
    {
        HttpRequestException => "GO AI Server ist nicht erreichbar oder das Zertifikat wurde abgewiesen.",
        TaskCanceledException => "Die Verbindung zu GO AI Server hat das Zeitlimit überschritten.",
        _ => exception.Message,
    };

    private static readonly Action<ILogger, Exception?> LocalProvisioningCompleted = LoggerMessage.Define(
        LogLevel.Information,
        new EventId(5201, nameof(LocalProvisioningCompleted)),
        "The local GO AI Server connection was provisioned into Windows Credential Manager.");
    private static readonly Action<ILogger, string, Exception?> LocalProvisioningDeferred = LoggerMessage.Define<string>(
        LogLevel.Warning,
        new EventId(5202, nameof(LocalProvisioningDeferred)),
        "The local GO AI Server connection was provisioned but is not ready yet ({Reason}).");
    private static readonly Action<ILogger, string, Exception?> LocalProvisioningFailed = LoggerMessage.Define<string>(
        LogLevel.Warning,
        new EventId(5203, nameof(LocalProvisioningFailed)),
        "The local GO AI Server connection could not be provisioned ({FailureKind}).");
    private static readonly Action<ILogger, Exception?> LocalProvisioningCleanupFailed = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(5204, nameof(LocalProvisioningCleanupFailed)),
        "The consumed local bootstrap key file could not be removed.");
}

public sealed record GoAiImportedConnection(
    string ServerUrl,
    string ProtocolVersion,
    string CaFingerprint,
    string CertificateSubject);
