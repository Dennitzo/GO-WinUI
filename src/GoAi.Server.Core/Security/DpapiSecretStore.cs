using GoAi.Server.Core.Configuration;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

namespace GoAi.Server.Core.Security;

public sealed class DpapiSecretStore
{
    private static readonly byte[] LmStudioEntropy = Encoding.UTF8.GetBytes("GO-AI-Server.LM-Studio.v1");
    private static readonly byte[] YouTubeEntropy = Encoding.UTF8.GetBytes("GO-AI-Server.YouTube.v1");
    private readonly GoAiServerOptions _options;

    public DpapiSecretStore(IOptions<GoAiServerOptions> options)
    {
        _options = options.Value;
    }

    public async Task SaveLmStudioTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        Directory.CreateDirectory(Path.GetDirectoryName(_options.LmStudioTokenPath)
            ?? throw new InvalidOperationException("LM Studio secret directory is invalid."));
        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(token),
            LmStudioEntropy,
            DataProtectionScope.LocalMachine);
        await File.WriteAllBytesAsync(_options.LmStudioTokenPath, protectedBytes, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> ReadLmStudioTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_options.LmStudioTokenPath))
        {
            return null;
        }

        var protectedBytes = await File.ReadAllBytesAsync(_options.LmStudioTokenPath, cancellationToken).ConfigureAwait(false);
        var clearBytes = ProtectedData.Unprotect(protectedBytes, LmStudioEntropy, DataProtectionScope.LocalMachine);
        return Encoding.UTF8.GetString(clearBytes);
    }

    public bool HasLmStudioToken => File.Exists(_options.LmStudioTokenPath);

    public void DeleteLmStudioToken()
    {
        if (File.Exists(_options.LmStudioTokenPath))
        {
            File.Delete(_options.LmStudioTokenPath);
        }
    }

    public async Task SaveYouTubeApiKeyAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        Directory.CreateDirectory(Path.GetDirectoryName(_options.YouTubeApiKeyPath)
            ?? throw new InvalidOperationException("YouTube secret directory is invalid."));
        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(apiKey.Trim()),
            YouTubeEntropy,
            DataProtectionScope.LocalMachine);
        await File.WriteAllBytesAsync(_options.YouTubeApiKeyPath, protectedBytes, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> ReadYouTubeApiKeyAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_options.YouTubeApiKeyPath))
        {
            return null;
        }
        var protectedBytes = await File.ReadAllBytesAsync(_options.YouTubeApiKeyPath, cancellationToken).ConfigureAwait(false);
        var clearBytes = ProtectedData.Unprotect(protectedBytes, YouTubeEntropy, DataProtectionScope.LocalMachine);
        return Encoding.UTF8.GetString(clearBytes);
    }

    public bool HasYouTubeApiKey => File.Exists(_options.YouTubeApiKeyPath);

    public void DeleteYouTubeApiKey()
    {
        if (File.Exists(_options.YouTubeApiKeyPath))
        {
            File.Delete(_options.YouTubeApiKeyPath);
        }
    }
}
