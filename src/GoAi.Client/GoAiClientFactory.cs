using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace GoAi.Client;

public static class GoAiClientFactory
{
    public static GoAiClient Create(GoAiClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var handler = new HttpClientHandler();
        if (!string.IsNullOrWhiteSpace(options.ExpectedCaSha256))
        {
            var expected = NormalizeFingerprint(options.ExpectedCaSha256);
            handler.ServerCertificateCustomValidationCallback = (_, certificate, chain, errors) =>
                ValidatePinnedAuthority(certificate, chain, errors, expected);
        }

        var httpClient = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = EnsureTrailingSlash(options.ServerUri),
            Timeout = options.RequestTimeout ?? TimeSpan.FromMinutes(10),
        };
        return new GoAiClient(httpClient, options.ApiKey, ownsHttpClient: true);
    }

    public static string ComputeSha256Fingerprint(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToHexString(SHA256.HashData(certificate.RawData));
    }

    public static Func<X509Certificate2, X509Chain?, SslPolicyErrors, bool> CreatePinnedChainValidator(
        X509Certificate2 trustedAuthority)
    {
        ArgumentNullException.ThrowIfNull(trustedAuthority);
        var expected = ComputeSha256Fingerprint(trustedAuthority);
        return (certificate, serverChain, errors) =>
        {
            if ((errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
            {
                return false;
            }

            using var customChain = new X509Chain();
            customChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            customChain.ChainPolicy.CustomTrustStore.Add(trustedAuthority);
            customChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            customChain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
            if (serverChain is not null)
            {
                foreach (var element in serverChain.ChainElements.Cast<X509ChainElement>())
                {
                    if (!element.Certificate.RawData.AsSpan().SequenceEqual(certificate.RawData)
                        && !element.Certificate.RawData.AsSpan().SequenceEqual(trustedAuthority.RawData))
                    {
                        customChain.ChainPolicy.ExtraStore.Add(element.Certificate);
                    }
                }
            }
            if (!customChain.Build(certificate))
            {
                return false;
            }

            return customChain.ChainElements.Cast<X509ChainElement>().Any(element =>
                FixedTimeFingerprintEquals(ComputeSha256Fingerprint(element.Certificate), expected));
        };
    }

    private static bool ValidatePinnedAuthority(
        X509Certificate2? certificate,
        X509Chain? chain,
        SslPolicyErrors errors,
        string expected)
    {
        if (certificate is null || chain is null || errors != SslPolicyErrors.None)
        {
            return false;
        }

        foreach (var element in chain.ChainElements)
        {
            var fingerprint = Convert.ToHexString(SHA256.HashData(element.Certificate.RawData));
            if (FixedTimeFingerprintEquals(fingerprint, expected))
            {
                return true;
            }
        }

        return false;
    }

    private static bool FixedTimeFingerprintEquals(string actual, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(actual),
            Convert.FromHexString(expected));

    private static string NormalizeFingerprint(string value)
    {
        var normalized = value.Replace(":", string.Empty, StringComparison.Ordinal).Trim();
        if (normalized.Length != 64 || !normalized.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("The CA fingerprint must contain 32 SHA-256 bytes.", nameof(value));
        }

        return normalized.ToUpperInvariant();
    }

    private static Uri EnsureTrailingSlash(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var text = uri.AbsoluteUri.EndsWith('/')
            ? uri.AbsoluteUri
            : uri.AbsoluteUri + "/";
        return new Uri(text, UriKind.Absolute);
    }
}
