using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GoWinUI.Core.Extensions;

namespace GoWinUI.Infrastructure.Extensions;

public sealed record AextPackageInspectorOptions(
    long MaximumPackageBytes = 256L * 1024 * 1024,
    long MaximumEntryBytes = 128L * 1024 * 1024,
    long MaximumUncompressedBytes = 512L * 1024 * 1024,
    int MaximumEntries = 1_024,
    int MaximumManifestBytes = 1024 * 1024,
    double MaximumCompressionRatio = 200);

public sealed class AextPackageInspector : IAextPackageInspector
{
    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private readonly IAextSignatureVerifier? _signatureVerifier;
    private readonly AextPackageInspectorOptions _options;

    public AextPackageInspector(
        IAextSignatureVerifier? signatureVerifier = null,
        AextPackageInspectorOptions? options = null)
    {
        _signatureVerifier = signatureVerifier;
        _options = options ?? new();
        ValidateOptions(_options);
    }

    public async Task<AextPackageInspection> InspectAsync(
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        var fullPath = Path.GetFullPath(packagePath);
        if (!string.Equals(Path.GetExtension(fullPath), ExtensionPackageFormat.FileExtension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Extension-Pakete müssen die Endung '{ExtensionPackageFormat.FileExtension}' verwenden.");
        var file = new FileInfo(fullPath);
        if (!file.Exists) throw new FileNotFoundException("Extension-Paket wurde nicht gefunden.", fullPath);
        if (file.Length is <= 0 || file.Length > _options.MaximumPackageBytes)
            throw new InvalidDataException("Das Extension-Paket ist leer oder überschreitet das Größenlimit.");

        await using var packageStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var packageHash = Convert.ToHexString(await SHA256.HashDataAsync(packageStream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        packageStream.Position = 0;
        try
        {
            using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count is < 1 || archive.Entries.Count > _options.MaximumEntries)
                throw new InvalidDataException("Das Extension-Paket enthält keine oder zu viele Einträge.");

            var normalizedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var inspectedEntries = new List<AextPackageEntry>(archive.Entries.Count);
            var archiveEntries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            long totalLength = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ExtensionPathRules.TryNormalizeRelativePath(entry.FullName, out var normalized, out var pathError))
                    throw new InvalidDataException($"Ungültiger Paketeintrag '{entry.FullName}': {pathError}");
                if (!normalizedPaths.Add(normalized))
                    throw new InvalidDataException($"Paketeintrag '{normalized}' kollidiert mit einem bereits vorhandenen Pfad.");
                if (IsSymbolicLink(entry))
                    throw new InvalidDataException($"Symbolische Verknüpfung '{normalized}' ist in Extension-Paketen nicht zulässig.");

                var isDirectory = normalized.EndsWith('/');
                if (isDirectory && (entry.Length != 0 || entry.CompressedLength != 0))
                    throw new InvalidDataException($"Verzeichniseintrag '{normalized}' enthält unerwartete Daten.");
                if (entry.Length < 0 || entry.Length > _options.MaximumEntryBytes)
                    throw new InvalidDataException($"Paketeintrag '{normalized}' überschreitet das Größenlimit.");
                totalLength = checked(totalLength + entry.Length);
                if (totalLength > _options.MaximumUncompressedBytes)
                    throw new InvalidDataException("Das entpackte Extension-Paket überschreitet das Größenlimit.");
                if (!isDirectory && entry.Length > 0
                    && (entry.CompressedLength <= 0 || entry.Length / (double)entry.CompressedLength > _options.MaximumCompressionRatio))
                    throw new InvalidDataException($"Kompressionsverhältnis von '{normalized}' überschreitet das Sicherheitslimit.");

                var hash = isDirectory
                    ? Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant()
                    : await HashEntryAsync(entry, cancellationToken).ConfigureAwait(false);
                inspectedEntries.Add(new(normalized, entry.Length, entry.CompressedLength, hash, isDirectory));
                archiveEntries.Add(normalized, entry);
            }

            var manifestEntry = RequireFileEntry(archiveEntries, ExtensionPackageFormat.ManifestEntryName);
            if (manifestEntry.Length > _options.MaximumManifestBytes)
                throw new InvalidDataException("extension.json überschreitet das Größenlimit.");
            var manifest = await DeserializeEntryAsync<ExtensionManifest>(manifestEntry, cancellationToken).ConfigureAwait(false);
            ExtensionManifestValidator.Validate(manifest).ThrowIfInvalid();
            EnsureEntrypointsExist(manifest, archiveEntries);

            var contentHash = ComputeContentHash(inspectedEntries);
            AextSignatureEnvelope? signature = null;
            AextSignatureVerification verification;
            if (archiveEntries.TryGetValue(ExtensionPackageFormat.SignatureEntryName, out var signatureEntry))
            {
                if (signatureEntry.Length > _options.MaximumManifestBytes)
                    throw new InvalidDataException("signature.json überschreitet das Größenlimit.");
                signature = await DeserializeEntryAsync<AextSignatureEnvelope>(signatureEntry, cancellationToken).ConfigureAwait(false);
                ValidateSignatureEnvelope(signature, contentHash);
                verification = _signatureVerifier is null
                    ? new(AextSignatureStatus.PresentUnverified, "Kein Signaturprüfer ist konfiguriert.")
                    : await _signatureVerifier.VerifyAsync(
                        new(fullPath, packageHash, contentHash, manifest, signature), cancellationToken).ConfigureAwait(false);
                if (verification.Status is AextSignatureStatus.Missing or AextSignatureStatus.PresentUnverified)
                    verification = new(AextSignatureStatus.Rejected, "Der konfigurierte Signaturprüfer hat die Signatur nicht abschließend bewertet.");
            }
            else verification = new(AextSignatureStatus.Missing, "Das Paket enthält keine signature.json.");

            return new(fullPath, packageHash, contentHash, file.Length, manifest,
                inspectedEntries.AsReadOnly(), signature, verification);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is IOException or JsonException or OverflowException)
        {
            throw new InvalidDataException("Das Extension-Paket ist beschädigt oder entspricht nicht dem .aext-Format.", exception);
        }
    }

    private static async Task<T> DeserializeEntryAsync<T>(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        await using var stream = entry.Open();
        return await JsonSerializer.DeserializeAsync<T>(stream, ManifestJson, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Paketeintrag '{entry.FullName}' ist leer.");
    }

    private static ZipArchiveEntry RequireFileEntry(
        Dictionary<string, ZipArchiveEntry> entries,
        string path)
    {
        if (!entries.TryGetValue(path, out var entry) || entry.FullName.EndsWith('/'))
            throw new InvalidDataException($"Erforderlicher Paketeintrag '{path}' fehlt.");
        return entry;
    }

    private static async Task<string> HashEntryAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        await using var stream = entry.Open();
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static string ComputeContentHash(IEnumerable<AextPackageEntry> entries)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in entries
            .Where(static entry => !string.Equals(entry.Path, ExtensionPackageFormat.SignatureEntryName, StringComparison.Ordinal))
            .OrderBy(static entry => entry.Path, StringComparer.Ordinal))
        {
            var canonical = Encoding.UTF8.GetBytes($"{entry.Path}\0{entry.Length}\0{entry.Sha256}\n");
            hash.AppendData(canonical);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void EnsureEntrypointsExist(
        ExtensionManifest manifest,
        Dictionary<string, ZipArchiveEntry> entries)
    {
        foreach (var entrypoint in manifest.Entrypoints)
        {
            _ = ExtensionPathRules.TryNormalizeRelativePath(entrypoint.Path, out var normalized, out _);
            if (!entries.TryGetValue(normalized, out var entry) || entry.FullName.EndsWith('/'))
                throw new InvalidDataException($"Entrypoint-Datei '{normalized}' fehlt im Paket.");
        }
    }

    private static void ValidateSignatureEnvelope(AextSignatureEnvelope signature, string contentHash)
    {
        if (string.IsNullOrWhiteSpace(signature.Algorithm) || signature.Algorithm.Length > 40
            || !signature.Algorithm.All(static value => char.IsAsciiLetterOrDigit(value) || value is '-' or '_'))
            throw new InvalidDataException("Signaturalgorithmus fehlt oder ist ungültig.");
        if (string.IsNullOrWhiteSpace(signature.KeyId) || signature.KeyId.Length > 160
            || !signature.KeyId.All(static value => char.IsAsciiLetterOrDigit(value) || value is '-' or '_' or '.' or ':'))
            throw new InvalidDataException("Signaturschlüssel-ID fehlt oder ist ungültig.");
        if (!ExtensionHashes.IsSha256(signature.ContentSha256)
            || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(signature.ContentSha256), Convert.FromHexString(contentHash)))
            throw new InvalidDataException("Der signierte Inhalts-Hash stimmt nicht mit dem Paketinhalt überein.");
        try
        {
            var bytes = Convert.FromBase64String(signature.Signature);
            if (bytes.Length is < 32 or > 8_192) throw new InvalidDataException("Die Signatur besitzt eine ungültige Länge.");
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("Die Signatur ist nicht gültig Base64-kodiert.", exception);
        }
    }

    private static bool IsSymbolicLink(ZipArchiveEntry entry)
    {
        const int UnixFileTypeMask = 0xF000;
        const int UnixSymbolicLink = 0xA000;
        var unixMode = (entry.ExternalAttributes >> 16) & UnixFileTypeMask;
        return unixMode == UnixSymbolicLink;
    }

    private static void ValidateOptions(AextPackageInspectorOptions options)
    {
        if (options.MaximumPackageBytes <= 0 || options.MaximumEntryBytes <= 0
            || options.MaximumUncompressedBytes <= 0 || options.MaximumEntries <= 0
            || options.MaximumManifestBytes <= 0 || options.MaximumCompressionRatio < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "Alle .aext-Prüfgrenzen müssen positiv sein.");
    }
}
