using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GoWinUI.App.Services;

public sealed partial class LeanProofService
{
    private const int MaximumOutputCharacters = 256 * 1024;
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(15);
    private readonly string _toolchainDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".elan",
        "bin");
    private readonly object _versionLock = new();
    private Task<LeanVersions>? _versionTask;
    private static readonly HashSet<string> AllowedAxioms = new(StringComparer.Ordinal)
    {
        "propext",
        "Classical.choice",
        "Quot.sound",
    };

    public async Task<LeanProofResult> ExecuteAsync(
        string workspacePath,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("proof.lean benötigt ein JSON-Objekt.");
        }

        var operation = RequiredString(arguments, "operation").ToLowerInvariant();
        var timeoutSeconds = arguments.TryGetProperty("timeoutSeconds", out var timeoutElement)
            ? timeoutElement.GetInt32()
            : 180;
        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 1800));
        var path = OptionalString(arguments, "path");
        var target = OptionalString(arguments, "target");
        var theoremName = OptionalString(arguments, "theoremName");

        return operation switch
        {
            "status" => await StatusAsync(workspacePath, cancellationToken).ConfigureAwait(false),
            "check" => await CheckAsync(workspacePath, Required(path, "path"), timeout, cancellationToken).ConfigureAwait(false),
            "build" => await BuildAsync(workspacePath, path ?? ".", target, timeout, cancellationToken).ConfigureAwait(false),
            "axioms" => await AxiomsAsync(
                workspacePath,
                Required(path, "path"),
                Required(theoremName, "theoremName"),
                timeout,
                cancellationToken).ConfigureAwait(false),
            "verify" => await VerifyAsync(
                workspacePath,
                Required(path, "path"),
                Required(theoremName, "theoremName"),
                timeout,
                cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidDataException($"Unbekannte proof.lean-Operation: {operation}"),
        };
    }

    public async Task<LeanProofResult> StatusAsync(
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        var root = CanonicalWorkspace(workspacePath);
        var lean = ResolvePinnedExecutable("lean");
        var lake = ResolvePinnedExecutable("lake");
        if (lean is null || lake is null)
        {
            return MissingToolchain("status", root);
        }

        var stopwatch = Stopwatch.StartNew();
        var leanVersion = await RunProcessAsync(lean, ["--version"], root, VersionTimeout, cancellationToken).ConfigureAwait(false);
        var lakeVersion = await RunProcessAsync(lake, ["--version"], root, VersionTimeout, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        var passed = leanVersion.ExitCode == 0 && lakeVersion.ExitCode == 0;
        return new LeanProofResult(
            "status", true, passed, false, false,
            FirstLine(leanVersion.Output), FirstLine(lakeVersion.Output),
            Path.GetFileName(root), ".", null, null,
            passed ? 0 : Math.Max(leanVersion.ExitCode, lakeVersion.ExitCode),
            ParseDiagnostics(leanVersion.Output + Environment.NewLine + lakeVersion.Output),
            [], [], stopwatch.ElapsedMilliseconds,
            passed
                ? "Die gepinnte Lean-/Lake-Toolchain ist verfügbar."
                : "Die Lean-/Lake-Version konnte nicht vollständig abgefragt werden.");
    }

    public async Task<LeanProofResult> CheckAsync(
        string workspacePath,
        string relativeFilePath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var context = ResolveLeanFile(workspacePath, relativeFilePath);
        var tools = ResolveTools();
        if (tools is null)
        {
            return MissingToolchain("check", context.WorkspaceRoot, context.RelativePath);
        }

        var execution = await RunLeanFileAsync(context, tools.Value, context.FilePath, timeout, cancellationToken).ConfigureAwait(false);
        var versions = await GetVersionsAsync(tools.Value, context.WorkspaceRoot, cancellationToken).ConfigureAwait(false);
        return CreateExecutionResult("check", context, execution, versions, null, [], []);
    }

    public async Task<LeanProofResult> BuildAsync(
        string workspacePath,
        string relativeProjectPath,
        string? target,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var workspaceRoot = CanonicalWorkspace(workspacePath);
        var projectPath = ResolveInside(workspaceRoot, relativeProjectPath, requireExisting: true);
        if (!Directory.Exists(projectPath))
        {
            throw new InvalidDataException("proof.lean build benötigt einen relativen Projektordner.");
        }
        EnsureNoReparseEscape(workspaceRoot, projectPath);
        if (!HasLakeProject(projectPath))
        {
            throw new InvalidDataException("Im gewählten Pfad wurde kein lakefile.lean oder lakefile.toml gefunden.");
        }
        if (!string.IsNullOrWhiteSpace(target) && !SafeTargetPattern().IsMatch(target))
        {
            throw new InvalidDataException("Das Lake-Build-Target enthält nicht erlaubte Zeichen.");
        }

        var tools = ResolveTools();
        var relative = NormalizeRelative(workspaceRoot, projectPath);
        if (tools is null)
        {
            return MissingToolchain("build", workspaceRoot, relative, target: target);
        }

        var arguments = new List<string> { "build" };
        if (!string.IsNullOrWhiteSpace(target)) arguments.Add(target);
        var execution = await RunProcessAsync(tools.Value.Lake, arguments, projectPath, timeout, cancellationToken).ConfigureAwait(false);
        var context = new LeanFileContext(workspaceRoot, projectPath, relative, projectPath);
        var versions = await GetVersionsAsync(tools.Value, workspaceRoot, cancellationToken).ConfigureAwait(false);
        return CreateExecutionResult("build", context, execution, versions, null, [], [], target);
    }

    public async Task<LeanProofResult> AxiomsAsync(
        string workspacePath,
        string relativeFilePath,
        string theoremName,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var context = ResolveLeanFile(workspacePath, relativeFilePath);
        ValidateTheoremName(theoremName);
        var tools = ResolveTools();
        if (tools is null)
        {
            return MissingToolchain("axioms", context.WorkspaceRoot, context.RelativePath, theoremName: theoremName);
        }

        var audit = await RunAxiomAuditAsync(context, tools.Value, theoremName, timeout, cancellationToken).ConfigureAwait(false);
        var forbiddenAxioms = audit.Axioms.Where(axiom => !AllowedAxioms.Contains(axiom)).ToArray();
        var passed = audit.Execution.ExitCode == 0 && audit.FoundAxiomReport && forbiddenAxioms.Length == 0;
        var execution = audit.Execution with
        {
            ExitCode = passed ? 0 : audit.Execution.ExitCode == 0 ? 2 : audit.Execution.ExitCode,
            Output = audit.Execution.Output,
        };
        var forbidden = forbiddenAxioms.Select(axiom => "axiom:" + axiom).ToArray();
        var versions = await GetVersionsAsync(tools.Value, context.WorkspaceRoot, cancellationToken).ConfigureAwait(false);
        return CreateExecutionResult("axioms", context, execution, versions, theoremName, audit.Axioms, forbidden);
    }

    public async Task<LeanProofResult> VerifyAsync(
        string workspacePath,
        string relativeFilePath,
        string theoremName,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var context = ResolveLeanFile(workspacePath, relativeFilePath);
        ValidateTheoremName(theoremName);
        var source = await File.ReadAllTextAsync(context.FilePath, cancellationToken).ConfigureAwait(false);
        var forbiddenConstructs = FindForbiddenConstructs(source);
        if (forbiddenConstructs.Count > 0)
        {
            return new LeanProofResult(
                "verify", true, false, false, false,
                null, null, Path.GetFileName(context.ProjectRoot), context.RelativePath, null, theoremName,
                2, [], [], forbiddenConstructs, 0,
                "Der Lean-Quelltext enthält unzulässige Beweiskonstrukte.");
        }
        var tools = ResolveTools();
        if (tools is null)
        {
            return MissingToolchain("verify", context.WorkspaceRoot, context.RelativePath, theoremName: theoremName);
        }

        var stopwatch = Stopwatch.StartNew();
        var versions = await GetVersionsAsync(tools.Value, context.WorkspaceRoot, cancellationToken).ConfigureAwait(false);
        var compile = await RunLeanFileAsync(context, tools.Value, context.FilePath, timeout, cancellationToken).ConfigureAwait(false);
        if (compile.ExitCode != 0 || compile.TimedOut)
        {
            stopwatch.Stop();
            return CreateExecutionResult("verify", context, compile, versions, theoremName, [], [], elapsedOverride: stopwatch.ElapsedMilliseconds);
        }

        var remaining = timeout - stopwatch.Elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            stopwatch.Stop();
            return new LeanProofResult(
                "verify", true, false, true, false,
                null, null, Path.GetFileName(context.ProjectRoot), context.RelativePath, null, theoremName,
                -1, [], [], [], stopwatch.ElapsedMilliseconds,
                "Das Zeitlimit wurde vor der Axiomprüfung erreicht.");
        }

        var audit = await RunAxiomAuditAsync(context, tools.Value, theoremName, remaining, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        var forbiddenAxioms = audit.Axioms.Where(axiom => !AllowedAxioms.Contains(axiom)).ToArray();
        var passed = audit.Execution.ExitCode == 0 && audit.FoundAxiomReport && forbiddenAxioms.Length == 0;
        var combinedOutput = compile.Output + Environment.NewLine + audit.Execution.Output;
        var combined = audit.Execution with
        {
            ExitCode = passed ? 0 : audit.Execution.ExitCode == 0 ? 2 : audit.Execution.ExitCode,
            Output = combinedOutput,
            DurationMilliseconds = stopwatch.ElapsedMilliseconds,
        };
        return CreateExecutionResult(
            "verify",
            context,
            combined,
            versions,
            theoremName,
            audit.Axioms,
            forbiddenAxioms.Select(axiom => "axiom:" + axiom).ToArray(),
            elapsedOverride: stopwatch.ElapsedMilliseconds);
    }

    internal static IReadOnlyList<string> FindForbiddenConstructs(string source)
    {
        var code = StripLeanCommentsAndStrings(source);
        var result = new List<string>();
        if (SorryPattern().IsMatch(code)) result.Add("sorry");
        if (AdmitPattern().IsMatch(code)) result.Add("admit");
        if (AxiomDeclarationPattern().IsMatch(code)) result.Add("axiom");
        if (SorryAxiomPattern().IsMatch(code)) result.Add("sorryAx");
        if (TrustCompilerPattern().IsMatch(code)) result.Add("Lean.trustCompiler");
        return result;
    }

    private static async Task<AxiomAudit> RunAxiomAuditAsync(
        LeanFileContext context,
        LeanTools tools,
        string theoremName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var auditRoot = Path.Combine(Path.GetTempPath(), "go-lean-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(auditRoot);
        var auditPath = Path.Combine(auditRoot, "Audit.lean");
        try
        {
            var source = await File.ReadAllTextAsync(context.FilePath, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                auditPath,
                source.TrimEnd() + Environment.NewLine + Environment.NewLine + "#print axioms " + theoremName + Environment.NewLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
            var execution = await RunLeanFileAsync(context, tools, auditPath, timeout, cancellationToken).ConfigureAwait(false);
            var (found, axioms) = ParseAxioms(execution.Output, theoremName);
            return new AxiomAudit(execution, found, axioms);
        }
        finally
        {
            try
            {
                if (Directory.Exists(auditRoot)) Directory.Delete(auditRoot, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<LeanProcessResult> RunLeanFileAsync(
        LeanFileContext context,
        LeanTools tools,
        string filePath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return HasLakeProject(context.ProjectRoot)
            ? await RunProcessAsync(
                tools.Lake,
                ["env", "lean", filePath],
                context.ProjectRoot,
                timeout,
                cancellationToken).ConfigureAwait(false)
            : await RunProcessAsync(
                tools.Lean,
                [filePath],
                context.WorkspaceRoot,
                timeout,
                cancellationToken).ConfigureAwait(false);
    }

    private static async Task<LeanProcessResult> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        var stopwatch = Stopwatch.StartNew();
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Die gepinnte Lean-Toolchain konnte nicht gestartet werden.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            stopwatch.Stop();
            return new LeanProcessResult(-1, "Zeitlimit der Lean-Prüfung überschritten.", true, false, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var output = Limit((await stdout.ConfigureAwait(false) + Environment.NewLine + await stderr.ConfigureAwait(false)).Trim());
        stopwatch.Stop();
        return new LeanProcessResult(process.ExitCode, output, false, false, stopwatch.ElapsedMilliseconds);
    }

    private static LeanProofResult CreateExecutionResult(
        string operation,
        LeanFileContext context,
        LeanProcessResult execution,
        LeanVersions versions,
        string? theoremName,
        IReadOnlyList<string> axioms,
        string[] forbidden,
        string? target = null,
        long? elapsedOverride = null)
    {
        var passed = execution.ExitCode == 0 && !execution.TimedOut && forbidden.Length == 0;
        return new LeanProofResult(
            operation,
            true,
            passed,
            execution.TimedOut,
            execution.Cancelled,
            versions.Lean,
            versions.Lake,
            Path.GetFileName(context.ProjectRoot),
            context.RelativePath,
            target,
            theoremName,
            execution.ExitCode,
            ParseDiagnostics(execution.Output),
            axioms,
            forbidden,
            elapsedOverride ?? execution.DurationMilliseconds,
            passed
                ? operation == "verify" ? "Lean-Kompilierung und Axiomprüfung waren erfolgreich." : "Lean-Prüfung erfolgreich."
                : execution.TimedOut ? "Zeitlimit der Lean-Prüfung überschritten." : Limit(execution.Output, 4000));
    }

    private static LeanProofResult MissingToolchain(
        string operation,
        string workspaceRoot,
        string path = ".",
        string? target = null,
        string? theoremName = null) =>
        new(
            operation, false, false, false, false,
            null, null, Path.GetFileName(workspaceRoot), path, target, theoremName,
            -1, [], [], [], 0,
            "Lean ist nicht installiert. Führe windows/install-coding-proof-tools.ps1 aus und starte den Auftrag erneut.");

    private static LeanFileContext ResolveLeanFile(string workspacePath, string relativeFilePath)
    {
        var workspaceRoot = CanonicalWorkspace(workspacePath);
        var filePath = ResolveInside(workspaceRoot, relativeFilePath, requireExisting: true);
        EnsureNoReparseEscape(workspaceRoot, filePath);
        if (!File.Exists(filePath) || !Path.GetExtension(filePath).Equals(".lean", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("proof.lean benötigt eine vorhandene .lean-Datei im Workspace.");
        }
        var projectRoot = FindLakeProjectRoot(Path.GetDirectoryName(filePath)!, workspaceRoot) ?? workspaceRoot;
        return new LeanFileContext(workspaceRoot, filePath, NormalizeRelative(workspaceRoot, filePath), projectRoot);
    }

    private static string CanonicalWorkspace(string workspacePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Der Lean-Workspace ist nicht verfügbar.");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Der Workspace darf für proof.lean kein Reparse-Punkt sein.");
        }
        return root;
    }

    private static string ResolveInside(string root, string relativePath, bool requireExisting)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("proof.lean akzeptiert ausschließlich relative Workspacepfade.");
        }
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
            && !candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Der proof.lean-Pfad verlässt den Workspace.");
        }
        if (requireExisting && !File.Exists(candidate) && !Directory.Exists(candidate))
        {
            throw new FileNotFoundException("Der proof.lean-Pfad wurde nicht gefunden.", relativePath);
        }
        return candidate;
    }

    private static void EnsureNoReparseEscape(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        var current = root;
        foreach (var component in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("proof.lean blockiert Reparse- und Symlink-Ausbrüche.");
            }
        }
    }

    private static string? FindLakeProjectRoot(string start, string workspaceRoot)
    {
        var current = new DirectoryInfo(start);
        while (current is not null
               && (current.FullName.Equals(workspaceRoot, StringComparison.OrdinalIgnoreCase)
                   || current.FullName.StartsWith(workspaceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        {
            if (HasLakeProject(current.FullName)) return current.FullName;
            if (current.FullName.Equals(workspaceRoot, StringComparison.OrdinalIgnoreCase)) break;
            current = current.Parent;
        }
        return null;
    }

    private static bool HasLakeProject(string directory) =>
        File.Exists(Path.Combine(directory, "lakefile.lean"))
        || File.Exists(Path.Combine(directory, "lakefile.toml"));

    private async Task<LeanVersions> GetVersionsAsync(
        LeanTools tools,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        Task<LeanVersions> task;
        lock (_versionLock)
        {
            task = _versionTask ??= QueryVersionsAsync(tools, workingDirectory);
        }
        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (task.IsFaulted)
            {
                lock (_versionLock)
                {
                    if (ReferenceEquals(_versionTask, task)) _versionTask = null;
                }
            }
            throw;
        }
    }

    private static async Task<LeanVersions> QueryVersionsAsync(LeanTools tools, string workingDirectory)
    {
        var lean = await RunProcessAsync(
            tools.Lean, ["--version"], workingDirectory, VersionTimeout, CancellationToken.None).ConfigureAwait(false);
        var lake = await RunProcessAsync(
            tools.Lake, ["--version"], workingDirectory, VersionTimeout, CancellationToken.None).ConfigureAwait(false);
        return new LeanVersions(FirstLine(lean.Output), FirstLine(lake.Output));
    }

    private LeanTools? ResolveTools()
    {
        var lean = ResolvePinnedExecutable("lean");
        var lake = ResolvePinnedExecutable("lake");
        return lean is null || lake is null ? null : new LeanTools(lean, lake);
    }

    private string? ResolvePinnedExecutable(string name)
    {
        var path = Path.Combine(_toolchainDirectory, name + ".exe");
        return File.Exists(path) ? path : null;
    }

    private static (bool Found, IReadOnlyList<string> Axioms) ParseAxioms(string output, string theoremName)
    {
        if (NoAxiomsPattern().IsMatch(output)) return (true, []);
        var match = AxiomListPattern().Match(output);
        if (!match.Success) return (false, []);
        var axioms = match.Groups[1].Value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return (true, axioms);
    }

    private static List<LeanDiagnostic> ParseDiagnostics(string output)
    {
        var result = new List<LeanDiagnostic>();
        foreach (Match match in DiagnosticPattern().Matches(output))
        {
            _ = int.TryParse(match.Groups[2].Value, out var line);
            _ = int.TryParse(match.Groups[3].Value, out var column);
            result.Add(new LeanDiagnostic(
                match.Groups[4].Value.ToLowerInvariant(),
                match.Groups[1].Value,
                line,
                column,
                match.Groups[5].Value.Trim()));
            if (result.Count == 200) break;
        }
        return result;
    }

    private static string StripLeanCommentsAndStrings(string source)
    {
        var builder = new StringBuilder(source.Length);
        var blockDepth = 0;
        var inString = false;
        for (var index = 0; index < source.Length; index++)
        {
            var current = source[index];
            var next = index + 1 < source.Length ? source[index + 1] : '\0';
            if (blockDepth > 0)
            {
                if (current == '/' && next == '-') { blockDepth++; index++; }
                else if (current == '-' && next == '/') { blockDepth--; index++; }
                else if (current is '\r' or '\n') builder.Append(current);
                else builder.Append(' ');
                continue;
            }
            if (!inString && current == '-' && next == '-')
            {
                while (index < source.Length && source[index] != '\n') { builder.Append(' '); index++; }
                if (index < source.Length) builder.Append('\n');
                continue;
            }
            if (!inString && current == '/' && next == '-')
            {
                blockDepth = 1;
                builder.Append("  ");
                index++;
                continue;
            }
            if (current == '"' && (index == 0 || source[index - 1] != '\\'))
            {
                inString = !inString;
                builder.Append(' ');
                continue;
            }
            builder.Append(inString && current is not '\r' and not '\n' ? ' ' : current);
        }
        return builder.ToString();
    }

    private static void ValidateTheoremName(string theoremName)
    {
        if (!TheoremNamePattern().IsMatch(theoremName))
        {
            throw new InvalidDataException("Der theoremName ist kein sicherer vollständig qualifizierter Lean-Bezeichner.");
        }
    }

    private static string RequiredString(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : throw new InvalidDataException($"proof.lean benötigt '{name}'.");

    private static string? OptionalString(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private static string Required(string? value, string name) =>
        !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidDataException($"proof.lean benötigt '{name}'.");

    private static string NormalizeRelative(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return relative.Length == 0 ? "." : relative;
    }

    private static string FirstLine(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;

    private static string Limit(string value, int maximum = MaximumOutputCharacters) =>
        value.Length <= maximum ? value : value[..maximum] + "…";

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    [GeneratedRegex(@"\A\+?[A-Za-z0-9_][A-Za-z0-9_.:-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex SafeTargetPattern();

    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_']*(?:\.[A-Za-z_][A-Za-z0-9_']*)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex TheoremNamePattern();

    [GeneratedRegex(@"(?m)\bsorry\b")]
    private static partial Regex SorryPattern();

    [GeneratedRegex(@"(?m)\badmit\b")]
    private static partial Regex AdmitPattern();

    [GeneratedRegex(@"(?m)^\s*axiom\b")]
    private static partial Regex AxiomDeclarationPattern();

    [GeneratedRegex(@"\bsorryAx\b")]
    private static partial Regex SorryAxiomPattern();

    [GeneratedRegex(@"\bLean\.trustCompiler\b")]
    private static partial Regex TrustCompilerPattern();

    [GeneratedRegex(@"does not depend on any axioms", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoAxiomsPattern();

    [GeneratedRegex(@"depends on axioms:\s*\[(.*?)\]", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex AxiomListPattern();

    [GeneratedRegex(@"(?m)^(.+?):(\d+):(\d+):\s*(error|warning|information)(?:\([^\r\n)]*\))?:\s*(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex DiagnosticPattern();

    private readonly record struct LeanTools(string Lean, string Lake);
    private sealed record LeanVersions(string Lean, string Lake);
    private sealed record LeanFileContext(string WorkspaceRoot, string FilePath, string RelativePath, string ProjectRoot);
    private sealed record LeanProcessResult(int ExitCode, string Output, bool TimedOut, bool Cancelled, long DurationMilliseconds);
    private sealed record AxiomAudit(LeanProcessResult Execution, bool FoundAxiomReport, IReadOnlyList<string> Axioms);
}

public sealed record LeanDiagnostic(
    string Severity,
    string File,
    int Line,
    int Column,
    string Message);

public sealed record LeanProofResult(
    string Operation,
    bool Available,
    bool Passed,
    bool TimedOut,
    bool Cancelled,
    string? LeanVersion,
    string? LakeVersion,
    string Project,
    string Path,
    string? Target,
    string? TheoremName,
    int ExitCode,
    IReadOnlyList<LeanDiagnostic> Diagnostics,
    IReadOnlyList<string> Axioms,
    IReadOnlyList<string> ForbiddenConstructs,
    long DurationMilliseconds,
    string Message);
