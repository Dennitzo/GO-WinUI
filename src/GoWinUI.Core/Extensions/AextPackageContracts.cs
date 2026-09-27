namespace GoWinUI.Core.Extensions;

public enum AextSignatureStatus
{
    Missing,
    PresentUnverified,
    Verified,
    Rejected,
}

public sealed record AextSignatureEnvelope(
    string Algorithm,
    string KeyId,
    string ContentSha256,
    string Signature);

public sealed record AextSignatureVerification(
    AextSignatureStatus Status,
    string? Reason = null);

public sealed record AextSignatureVerificationContext(
    string PackagePath,
    string PackageSha256,
    string ContentSha256,
    ExtensionManifest Manifest,
    AextSignatureEnvelope Signature);

public sealed record AextPackageEntry(
    string Path,
    long Length,
    long CompressedLength,
    string Sha256,
    bool IsDirectory);

public sealed record AextPackageInspection(
    string PackagePath,
    string PackageSha256,
    string ContentSha256,
    long PackageLength,
    ExtensionManifest Manifest,
    IReadOnlyList<AextPackageEntry> Entries,
    AextSignatureEnvelope? Signature,
    AextSignatureVerification SignatureVerification);

public sealed record StagedAextPackage(
    string PackagePath,
    string ExtensionId,
    string Version,
    string PackageSha256);

public sealed record ActivatedAextPackage(
    string PackagePath,
    string ContentDirectory,
    string ExtensionId,
    string Version,
    string PackageSha256);

public interface IAextSignatureVerifier
{
    ValueTask<AextSignatureVerification> VerifyAsync(
        AextSignatureVerificationContext context,
        CancellationToken cancellationToken = default);
}

public interface IAextPackageInspector
{
    Task<AextPackageInspection> InspectAsync(
        string packagePath,
        CancellationToken cancellationToken = default);
}

public interface IAextPackageStore
{
    Task<StagedAextPackage> StageAsync(
        string packagePath,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AextPackageInspection>> GetActivationCandidatesAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AextPackageInspection>> GetActivePackagesAsync(
        CancellationToken cancellationToken = default);

    Task<ActivatedAextPackage> ActivateAsync(
        AextPackageInspection candidate,
        CancellationToken cancellationToken = default);
}

public static class ExtensionHashes
{
    public static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
