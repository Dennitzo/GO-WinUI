using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using GoWinUI.Core.Extensions;

namespace GoWinUI.Infrastructure.Extensions;

public sealed record TrustedAextSigningKey(
    string KeyId,
    string Algorithm,
    string PublicKeyPem);

public sealed class DirectoryAextSignatureVerifier : IAextSignatureVerifier
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };

    private readonly Dictionary<string, TrustedAextSigningKey> _keys;

    public DirectoryAextSignatureVerifier(string trustedKeyDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedKeyDirectory);
        var directory = Path.GetFullPath(trustedKeyDirectory);
        Directory.CreateDirectory(directory);
        _keys = LoadKeys(directory);
    }

    public ValueTask<AextSignatureVerification> VerifyAsync(
        AextSignatureVerificationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_keys.TryGetValue(context.Signature.KeyId, out var key))
            return ValueTask.FromResult(new AextSignatureVerification(
                AextSignatureStatus.Rejected, "Der Signaturschlüssel ist nicht als vertrauenswürdig registriert."));
        if (!string.Equals(key.Algorithm, context.Signature.Algorithm, StringComparison.Ordinal))
            return ValueTask.FromResult(new AextSignatureVerification(
                AextSignatureStatus.Rejected, "Signaturalgorithmus und vertrauenswürdiger Schlüssel stimmen nicht überein."));

        try
        {
            var digest = Convert.FromHexString(context.ContentSha256);
            var signature = Convert.FromBase64String(context.Signature.Signature);
            var verified = key.Algorithm switch
            {
                "rsa-pss-sha256" => VerifyRsa(key.PublicKeyPem, digest, signature),
                "ecdsa-p256-sha256" => VerifyEcdsa(key.PublicKeyPem, digest, signature),
                _ => false,
            };
            return ValueTask.FromResult(new AextSignatureVerification(
                verified ? AextSignatureStatus.Verified : AextSignatureStatus.Rejected,
                verified ? null : "Die kryptografische Paketsignatur ist ungültig oder verwendet einen nicht unterstützten Algorithmus."));
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException or FormatException)
        {
            return ValueTask.FromResult(new AextSignatureVerification(
                AextSignatureStatus.Rejected, "Der vertrauenswürdige Signaturschlüssel oder die Signatur ist ungültig."));
        }
    }

    private static bool VerifyRsa(string pem, byte[] digest, byte[] signature)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        return rsa.VerifyHash(digest, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    }

    private static bool VerifyEcdsa(string pem, byte[] digest, byte[] signature)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(pem);
        return ecdsa.KeySize == 256 && ecdsa.VerifyHash(digest, signature);
    }

    private static Dictionary<string, TrustedAextSigningKey> LoadKeys(string directory)
    {
        var paths = Directory.EnumerateFiles(directory, "*.json",
            new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint })
            .Order(StringComparer.OrdinalIgnoreCase)
            .Take(257)
            .ToArray();
        if (paths.Length > 256) throw new InvalidDataException("Das Verzeichnis enthält zu viele vertrauenswürdige Extension-Schlüssel.");
        var keys = new Dictionary<string, TrustedAextSigningKey>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var file = new FileInfo(path);
            if (file.Length is <= 0 or > 64 * 1024)
                throw new InvalidDataException($"Vertrauensschlüsseldatei '{file.Name}' ist leer oder zu groß.");
            var key = JsonSerializer.Deserialize<TrustedAextSigningKey>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException($"Vertrauensschlüsseldatei '{file.Name}' ist leer.");
            if (string.IsNullOrWhiteSpace(key.KeyId) || key.KeyId.Length > 160
                || key.Algorithm is not ("rsa-pss-sha256" or "ecdsa-p256-sha256")
                || string.IsNullOrWhiteSpace(key.PublicKeyPem))
                throw new InvalidDataException($"Vertrauensschlüsseldatei '{file.Name}' ist ungültig.");
            if (!keys.TryAdd(key.KeyId, key))
                throw new InvalidDataException($"Vertrauensschlüssel-ID '{key.KeyId}' ist mehrfach vorhanden.");
        }
        return keys;
    }
}
