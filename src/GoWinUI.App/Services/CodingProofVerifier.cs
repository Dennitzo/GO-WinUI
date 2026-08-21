using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GoWinUI.App.Services;

public sealed partial class CodingProofVerifier(LeanProofService? leanProof = null)
{
    private readonly TimeSpan _checkerTimeout = TimeSpan.FromMinutes(3);
    private readonly LeanProofService _leanProof = leanProof ?? new LeanProofService();
    private static readonly HashSet<string> AllowedManifestProperties = new(StringComparer.Ordinal)
    {
        "caseId", "kind", "statement", "assumptions", "validityDomain", "artifact", "sourceSha256", "theoremName",
    };
    private static readonly HashSet<string> RequiredManifestProperties = new(StringComparer.Ordinal)
    {
        "caseId", "kind", "statement", "assumptions", "validityDomain", "artifact", "sourceSha256",
    };

    public async Task<IReadOnlyList<CodingProofVerificationResult>> VerifyAllAsync(
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        var proofRoot = Path.Combine(workspacePath, "proofs");
        if (!Directory.Exists(proofRoot))
        {
            return [];
        }

        var results = new List<CodingProofVerificationResult>();
        foreach (var manifestPath in Directory.EnumerateFiles(proofRoot, "proof.json", SearchOption.AllDirectories)
                     .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            results.Add(await VerifyAsync(workspacePath, manifestPath, cancellationToken).ConfigureAwait(false));
        }
        return results;
    }

    internal async Task<CodingProofVerificationResult> VerifyAsync(
        string workspacePath,
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        var relativeManifest = Path.GetRelativePath(workspacePath, manifestPath).Replace('\\', '/');
        var caseId = "unknown";
        var kind = CodingProofKind.NumericalEvidence;
        var isProof = false;
        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false));
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("caseId", out var declaredCaseId)
                && declaredCaseId.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(declaredCaseId.GetString()))
            {
                caseId = declaredCaseId.GetString()!.Trim();
            }
            ValidateManifestContract(root);
            caseId = RequiredString(root, "caseId");
            kind = ParseKind(RequiredString(root, "kind"));
            isProof = kind != CodingProofKind.NumericalEvidence;
            var statement = RequiredString(root, "statement");
            var validityDomain = RequiredValidityDomain(root);
            if (!root.TryGetProperty("assumptions", out var assumptions)
                || assumptions.ValueKind != JsonValueKind.Array
                || assumptions.EnumerateArray().Any(static item => item.ValueKind != JsonValueKind.String))
            {
                return Failure(caseId, relativeManifest, kind, "assumptions muss als Liste expliziter Textannahmen vorliegen.");
            }
            var artifactRelativePath = RequiredString(root, "artifact").Replace('/', Path.DirectorySeparatorChar);
            if (statement.Length < 20 || validityDomain.Length < 10)
            {
                return Failure(caseId, relativeManifest, kind, "Aussage oder Gültigkeitsbereich ist nicht ausreichend dokumentiert.");
            }

            var artifactPath = ResolveInside(workspacePath, artifactRelativePath);
            if (!File.Exists(artifactPath))
            {
                return Failure(caseId, relativeManifest, kind, $"Beweisartefakt fehlt: {artifactRelativePath}");
            }

            var extension = Path.GetExtension(artifactPath);
            if (kind == CodingProofKind.Formal && !extension.Equals(".lean", StringComparison.OrdinalIgnoreCase)
                || kind is CodingProofKind.Symbolic or CodingProofKind.IntervalCertified
                && !extension.Equals(".py", StringComparison.OrdinalIgnoreCase))
            {
                return Failure(caseId, relativeManifest, kind, "Beweisart und Dateiformat des Checker-Artefakts stimmen nicht überein.");
            }

            if (!root.TryGetProperty("sourceSha256", out var hashElement)
                || hashElement.ValueKind != JsonValueKind.String
                || !Sha256Pattern().IsMatch(hashElement.GetString() ?? string.Empty))
            {
                return Failure(caseId, relativeManifest, kind, "sourceSha256 fehlt oder ist kein gültiger SHA-256-Wert.");
            }
            var expected = hashElement.GetString()!.Trim();
            await using (var stream = File.OpenRead(artifactPath))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                {
                    return Failure(caseId, relativeManifest, kind, "SHA-256 des Beweisartefakts stimmt nicht mit dem Manifest überein.");
                }
            }

            if (kind == CodingProofKind.NumericalEvidence)
            {
                return new(caseId, relativeManifest, kind, false, true,
                    "Numerische Evidenz wurde geprüft, gilt aber definitionsgemäß nicht als mathematischer Beweis.");
            }

            if (kind == CodingProofKind.Formal)
            {
                var source = await File.ReadAllTextAsync(artifactPath, cancellationToken).ConfigureAwait(false);
                var forbidden = LeanProofService.FindForbiddenConstructs(source);
                if (forbidden.Count > 0)
                {
                    return Failure(
                        caseId,
                        relativeManifest,
                        kind,
                        "Formaler Beweis enthält unzulässige Konstrukte: " + string.Join(", ", forbidden));
                }

                var theoremName = RequiredString(root, "theoremName");
                var relativeArtifact = Path.GetRelativePath(workspacePath, artifactPath).Replace('\\', '/');
                var verification = await _leanProof.VerifyAsync(
                    workspacePath,
                    relativeArtifact,
                    theoremName,
                    _checkerTimeout,
                    cancellationToken).ConfigureAwait(false);
                return new(
                    caseId,
                    relativeManifest,
                    kind,
                    true,
                    verification.Passed,
                    verification.Passed
                        ? $"Lean verify erfolgreich für {theoremName}; Axiome: {FormatAxioms(verification.Axioms)}"
                        : verification.Message);
            }

            var execution = await ExecuteCheckerAsync(workspacePath, artifactPath, kind, cancellationToken).ConfigureAwait(false);
            return new(caseId, relativeManifest, kind, true, execution.ExitCode == 0,
                execution.ExitCode == 0
                    ? $"Checker erfolgreich: {execution.Command}"
                    : $"Checker fehlgeschlagen (Exit {execution.ExitCode}): {Limit(execution.Output, 1200)}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            return new(caseId, relativeManifest, kind, isProof, false,
                $"Ungültiges Beweismanifest: {exception.Message}");
        }
    }

    private static void ValidateManifestContract(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Das Beweismanifest muss ein JSON-Objekt sein.");
        }
        var properties = root.EnumerateObject().Select(static property => property.Name).ToHashSet(StringComparer.Ordinal);
        var unexpected = properties.Except(AllowedManifestProperties, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (unexpected.Length > 0)
        {
            throw new InvalidDataException($"Unerlaubte Manifestfelder: {string.Join(", ", unexpected)}");
        }
        var missing = RequiredManifestProperties.Except(properties, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException($"Fehlende Manifestfelder: {string.Join(", ", missing)}");
        }
    }

    private async Task<CheckerExecution> ExecuteCheckerAsync(
        string workspacePath,
        string artifactPath,
        CodingProofKind kind,
        CancellationToken cancellationToken)
    {
        if (kind == CodingProofKind.Formal)
        {
            throw new InvalidOperationException("Formale Beweise müssen über LeanProofService geprüft werden.");
        }

        string command;
        string arguments;
        var venvPython = Path.Combine(workspacePath, ".venv", "Scripts", "python.exe");
        if (File.Exists(venvPython))
        {
            command = venvPython;
            arguments = Quote(artifactPath);
        }
        else
        {
            command = "py";
            arguments = $"-3.11 {Quote(artifactPath)}";
        }

        var startInfo = new ProcessStartInfo(command, arguments)
        {
            WorkingDirectory = workspacePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Checker konnte nicht gestartet werden: {command}");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_checkerTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new(command + " " + arguments, -1, "Zeitlimit des Beweischeckers überschritten.");
        }
        var output = string.Join(Environment.NewLine, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false)).Trim();
        return new(command + " " + arguments, process.ExitCode, output);
    }

    private static string RequiredString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"Pflichtfeld fehlt: {property}");
        }
        return value.GetString()!.Trim();
    }

    private static string RequiredValidityDomain(JsonElement root)
    {
        if (!root.TryGetProperty("validityDomain", out var value))
        {
            throw new InvalidDataException("Pflichtfeld fehlt: validityDomain");
        }
        if (value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()))
        {
            return value.GetString()!.Trim();
        }
        if (value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("description", out var description)
            && description.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(description.GetString()))
        {
            return description.GetString()!.Trim();
        }

        throw new InvalidDataException(
            "Pflichtfeld validityDomain muss ein nicht leerer Text oder ein Objekt mit nicht leerer description sein.");
    }

    private static CodingProofKind ParseKind(string value) => value.Trim().ToLowerInvariant() switch
    {
        "symbolic" => CodingProofKind.Symbolic,
        "interval-certified" => CodingProofKind.IntervalCertified,
        "formal" => CodingProofKind.Formal,
        "numerical-evidence" => CodingProofKind.NumericalEvidence,
        _ => throw new InvalidDataException(
            $"Unbekannte Beweisart: {value}. Erlaubt sind symbolic, interval-certified, formal und numerical-evidence."),
    };

    private static string ResolveInside(string workspacePath, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("Beweisartefakte müssen relative Workspacepfade verwenden.");
        }
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Beweisartefakt verlässt den Workspace.");
        }
        return candidate;
    }

    private static CodingProofVerificationResult Failure(string caseId, string manifest, CodingProofKind kind, string detail) =>
        new(caseId, manifest, kind, kind != CodingProofKind.NumericalEvidence, false, detail);

    private static string Quote(string value) => '"' + value.Replace("\"", "\\\"") + '"';
    private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum] + "…";

    private static string FormatAxioms(IReadOnlyList<string> axioms) =>
        axioms.Count == 0 ? "keine" : string.Join(", ", axioms);

    [GeneratedRegex(@"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    private sealed record CheckerExecution(string Command, int ExitCode, string Output);
}
