using System.IO.Compression;
using System.Security.Cryptography;
using GoWinUI.Core.Extensions;

namespace GoWinUI.Infrastructure.Extensions;

public sealed record AextPackageStoreOptions(
    string RootDirectory,
    bool RequireVerifiedSignatures = true);

public sealed class FileSystemAextPackageStore : IAextPackageStore, IDisposable
{
    private readonly IAextPackageInspector _inspector;
    private readonly AextPackageStoreOptions _options;
    private readonly string _pendingRoot;
    private readonly string _activeRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileSystemAextPackageStore(
        IAextPackageInspector inspector,
        AextPackageStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RootDirectory);
        _inspector = inspector;
        _options = options with { RootDirectory = Path.GetFullPath(options.RootDirectory) };
        _pendingRoot = Path.Combine(_options.RootDirectory, "pending");
        _activeRoot = Path.Combine(_options.RootDirectory, "active");
        Directory.CreateDirectory(_pendingRoot);
        Directory.CreateDirectory(_activeRoot);
    }

    public async Task<StagedAextPackage> StageAsync(
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        var inspection = await _inspector.InspectAsync(packagePath, cancellationToken).ConfigureAwait(false);
        EnsureTrusted(inspection);
        var directory = Path.Combine(_pendingRoot, inspection.Manifest.Id, inspection.Manifest.Version);
        var destination = Path.Combine(directory, inspection.PackageSha256 + ExtensionPackageFormat.FileExtension);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(directory);
            var conflicts = Directory.EnumerateFiles(directory, "*" + ExtensionPackageFormat.FileExtension, SearchOption.TopDirectoryOnly)
                .Where(path => !string.Equals(Path.GetFileName(path), Path.GetFileName(destination), StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (conflicts.Length != 0)
                throw new InvalidDataException($"Extension '{inspection.Manifest.Id}' Version '{inspection.Manifest.Version}' ist bereits mit anderem Inhalt vorgemerkt.");
            if (!File.Exists(destination)) await CopyAtomicallyAsync(inspection.PackagePath, destination, cancellationToken).ConfigureAwait(false);
            var stagedInspection = await _inspector.InspectAsync(destination, cancellationToken).ConfigureAwait(false);
            EnsureSamePackage(inspection, stagedInspection);
            EnsureTrusted(stagedInspection);
            return new(destination, stagedInspection.Manifest.Id, stagedInspection.Manifest.Version, stagedInspection.PackageSha256);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<AextPackageInspection>> GetActivationCandidatesAsync(
        CancellationToken cancellationToken = default)
    {
        var paths = Directory.EnumerateFiles(_pendingRoot, "*" + ExtensionPackageFormat.FileExtension,
            new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var candidates = new List<AextPackageInspection>(paths.Length);
        foreach (var path in paths)
        {
            var inspection = await _inspector.InspectAsync(path, cancellationToken).ConfigureAwait(false);
            EnsureTrusted(inspection);
            candidates.Add(inspection);
        }
        return candidates.AsReadOnly();
    }

    public async Task<IReadOnlyList<AextPackageInspection>> GetActivePackagesAsync(
        CancellationToken cancellationToken = default)
    {
        var paths = Directory.EnumerateFiles(_activeRoot, "package" + ExtensionPackageFormat.FileExtension,
            new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var packages = new List<AextPackageInspection>(paths.Length);
        foreach (var path in paths)
        {
            var inspection = await _inspector.InspectAsync(path, cancellationToken).ConfigureAwait(false);
            EnsureTrusted(inspection);
            var contentDirectory = Path.Combine(Path.GetDirectoryName(path)
                ?? throw new InvalidDataException("Aktiver Extension-Paketpfad besitzt kein Verzeichnis."), "content");
            if (!Directory.Exists(contentDirectory))
                throw new InvalidDataException($"Aktiver Extension-Inhalt für '{inspection.Manifest.Id}' fehlt.");
            await VerifyExtractedContentAsync(inspection, contentDirectory, cancellationToken).ConfigureAwait(false);
            packages.Add(inspection);
        }
        return packages.AsReadOnly();
    }

    public async Task<ActivatedAextPackage> ActivateAsync(
        AextPackageInspection candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        EnsureInside(candidate.PackagePath, _pendingRoot, "Nur vorgemerkte Extension-Pakete können aktiviert werden.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryDirectory = null;
        try
        {
            var fresh = await _inspector.InspectAsync(candidate.PackagePath, cancellationToken).ConfigureAwait(false);
            EnsureSamePackage(candidate, fresh);
            EnsureTrusted(fresh);

            var versionDirectory = Path.Combine(_activeRoot, fresh.Manifest.Id, fresh.Manifest.Version);
            var targetDirectory = Path.Combine(versionDirectory, fresh.PackageSha256);
            var targetPackage = Path.Combine(targetDirectory, "package" + ExtensionPackageFormat.FileExtension);
            var targetContent = Path.Combine(targetDirectory, "content");
            if (Directory.Exists(targetDirectory))
            {
                if (!File.Exists(targetPackage) || !Directory.Exists(targetContent))
                    throw new InvalidDataException("Das aktive Extension-Verzeichnis ist unvollständig.");
                File.Delete(fresh.PackagePath);
                return new(targetPackage, targetContent, fresh.Manifest.Id, fresh.Manifest.Version, fresh.PackageSha256);
            }

            Directory.CreateDirectory(versionDirectory);
            if (Directory.EnumerateDirectories(versionDirectory)
                .Any(path => !Path.GetFileName(path).StartsWith(".activating-", StringComparison.Ordinal)))
                throw new InvalidDataException($"Extension '{fresh.Manifest.Id}' Version '{fresh.Manifest.Version}' ist bereits mit anderem Inhalt aktiv.");

            temporaryDirectory = Path.Combine(versionDirectory, ".activating-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
            var temporaryPackage = Path.Combine(temporaryDirectory, "package" + ExtensionPackageFormat.FileExtension);
            await CopyAtomicallyAsync(fresh.PackagePath, temporaryPackage, cancellationToken).ConfigureAwait(false);
            var copied = await _inspector.InspectAsync(temporaryPackage, cancellationToken).ConfigureAwait(false);
            EnsureSamePackage(fresh, copied);
            EnsureTrusted(copied);
            var contentDirectory = Path.Combine(temporaryDirectory, "content");
            Directory.CreateDirectory(contentDirectory);
            await ExtractAsync(temporaryPackage, contentDirectory, cancellationToken).ConfigureAwait(false);
            await VerifyExtractedContentAsync(copied, contentDirectory, cancellationToken).ConfigureAwait(false);
            Directory.Move(temporaryDirectory, targetDirectory);
            temporaryDirectory = null;
            File.Delete(fresh.PackagePath);
            return new(targetPackage, targetContent, fresh.Manifest.Id, fresh.Manifest.Version, fresh.PackageSha256);
        }
        finally
        {
            if (temporaryDirectory is not null) TryDeleteDirectory(temporaryDirectory);
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private void EnsureTrusted(AextPackageInspection inspection)
    {
        if (_options.RequireVerifiedSignatures && inspection.SignatureVerification.Status != AextSignatureStatus.Verified)
            throw new InvalidDataException("Das Extension-Paket besitzt keine erfolgreich verifizierte Signatur.");
        if (inspection.SignatureVerification.Status == AextSignatureStatus.Rejected)
            throw new InvalidDataException("Die Signatur des Extension-Pakets wurde abgelehnt: " + inspection.SignatureVerification.Reason);
    }

    private static void EnsureSamePackage(AextPackageInspection expected, AextPackageInspection actual)
    {
        if (!string.Equals(expected.Manifest.Id, actual.Manifest.Id, StringComparison.Ordinal)
            || !string.Equals(expected.Manifest.Version, actual.Manifest.Version, StringComparison.Ordinal)
            || !string.Equals(expected.PackageSha256, actual.PackageSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Das Extension-Paket wurde während der Verarbeitung ausgetauscht.");
    }

    private static async Task CopyAtomicallyAsync(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("Extension-Zielverzeichnis fehlt."));
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, destination);
        }
        finally { TryDeleteFile(temporary); }
    }

    private static async Task ExtractAsync(string packagePath, string destination, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ExtensionPathRules.TryNormalizeRelativePath(entry.FullName, out var normalized, out var error))
                throw new InvalidDataException(error);
            var target = ExtensionPathRules.ResolveInside(destination, normalized);
            if (normalized.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void EnsureInside(string path, string root, string message)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(message);
    }

    private static async Task VerifyExtractedContentAsync(
        AextPackageInspection inspection,
        string contentDirectory,
        CancellationToken cancellationToken)
    {
        var expected = inspection.Entries
            .Where(static entry => !entry.IsDirectory)
            .ToDictionary(static entry => entry.Path, StringComparer.OrdinalIgnoreCase);
        var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in EnumerateFilesWithoutReparsePoints(contentDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(contentDirectory, path).Replace(Path.DirectorySeparatorChar, '/');
            if (!ExtensionPathRules.TryNormalizeRelativePath(relative, out var normalized, out var error))
                throw new InvalidDataException(error);
            if (!discovered.Add(normalized) || !expected.TryGetValue(normalized, out var expectedEntry))
                throw new InvalidDataException($"Aktiver Extension-Inhalt enthält die unerwartete Datei '{normalized}'.");
            var file = new FileInfo(path);
            if (file.Length != expectedEntry.Length)
                throw new InvalidDataException($"Aktiver Extension-Inhalt '{normalized}' stimmt nicht mit dem signierten Paket überein.");
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            if (!string.Equals(hash, expectedEntry.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException($"Aktiver Extension-Inhalt '{normalized}' stimmt nicht mit dem signierten Paket überein.");
        }
        if (discovered.Count != expected.Count)
            throw new InvalidDataException("Aktiver Extension-Inhalt ist unvollständig.");
    }

    private static IEnumerable<string> EnumerateFilesWithoutReparsePoints(string root)
    {
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Aktiver Extension-Inhalt darf keine Dateisystemverknüpfungen enthalten.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                else yield return path;
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
