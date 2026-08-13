using GoAi.Server.Core.Configuration;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

namespace GoAi.Server.Core.Security;

public sealed class DpapiSecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("GO-AI-Server.LM-Studio.v1");
    private readonly GoAiServerOptions _options;

    public DpapiSecretStore(IOptions<GoAiServerOptions> options)
    {
        _options = options.Value;
    }

    public async Task SaveLmStudioTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        Directory.CreateDirectory(_options.SecretDirectory);
        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(token),
            Entropy,
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
        var clearBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.LocalMachine);
        return Encoding.UTF8.GetString(clearBytes);
    }
}
