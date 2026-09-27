using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GoAi.Contracts;

namespace GoWinUI.Core.Coding;

/// <summary>
/// Workspace-bound scientific tools. Generated sources, manifests and change sets live below
/// .assistant/research/&lt;projectId&gt;; processes reuse the Coding executor's job-object lifecycle.
/// </summary>
public sealed class ScientificResearchToolExecutor(
    string workspaceRoot,
    Func<CodingCommandProgress, Task>? commandProgress = null,
    CodingRunEvidenceStore.CodingEvidenceCapture? evidence = null)
{
    private static readonly CodingProcessLimits ScientificProcessLimits = new(4UL * 1024 * 1024 * 1024, 32);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
    private readonly LocalCodingToolExecutor _coding = new(workspaceRoot, commandProgress, evidence, ScientificProcessLimits);

    public static void ValidateArguments(string toolName, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) throw new ArgumentException("Werkzeugargumente müssen ein JSON-Objekt sein.");
        var allowed = toolName switch
        {
            ClientToolNames.MathSymbolic => new[] { "projectId", "expression", "operation", "symbol", "timeoutSeconds" },
            ClientToolNames.MathNumeric => new[] { "projectId", "expression", "precision", "timeoutSeconds" },
            ClientToolNames.MathSmt or ClientToolNames.MathFormalProof => new[] { "projectId", "source", "timeoutSeconds" },
            ClientToolNames.ResearchCodeWrite => new[] { "projectId", "path", "content", "expectedSha256" },
            ClientToolNames.ResearchCodeExecute => new[] { "projectId", "experimentId", "executable", "arguments", "workingDirectory", "randomSeed", "timeoutSeconds" },
            ClientToolNames.ResearchCodeTest => new[] { "projectId", "experimentId", "executable", "arguments", "workingDirectory", "timeoutSeconds" },
            ClientToolNames.ResearchCodeBenchmark => new[] { "projectId", "experimentId", "executable", "arguments", "workingDirectory", "repetitions", "timeoutSeconds" },
            ClientToolNames.ResearchCodeRestore => new[] { "projectId", "changeSetId" },
            _ => throw new ArgumentException($"Unbekanntes wissenschaftliches Werkzeug: {toolName}.")
        };
        if (arguments.EnumerateObject().Any(property => !allowed.Contains(property.Name, StringComparer.Ordinal)))
            throw new ArgumentException($"{toolName} enthält ein unbekanntes Argument.");
        _ = Identifier(arguments, "projectId");
        if (toolName == ClientToolNames.MathSymbolic)
        {
            _ = RequiredString(arguments, "expression", 16_000);
            if (RequiredString(arguments, "operation", 32) is not ("simplify" or "factor" or "expand" or "solve" or "differentiate" or "integrate"))
                throw new ArgumentException("operation ist ungültig.");
        }
        else if (toolName == ClientToolNames.MathNumeric) _ = RequiredString(arguments, "expression", 16_000);
        else if (toolName is ClientToolNames.MathSmt or ClientToolNames.MathFormalProof) _ = RequiredString(arguments, "source", 64_000);
        else if (toolName == ClientToolNames.ResearchCodeWrite)
        {
            _ = RequiredString(arguments, "path", 1_024); _ = RequiredString(arguments, "content", 64_000, allowEmpty: true);
        }
        else if (toolName == ClientToolNames.ResearchCodeRestore) _ = Identifier(arguments, "changeSetId");
        else
        {
            _ = Identifier(arguments, "experimentId"); _ = RequiredString(arguments, "executable", 1_024);
            if (!arguments.TryGetProperty("arguments", out var values) || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > 64
                || values.EnumerateArray().Any(static value => value.ValueKind != JsonValueKind.String || value.GetString()!.Length > 4_000))
                throw new ArgumentException("arguments ist ungültig.");
        }
    }

    public async Task<JsonElement> ExecuteAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        ValidateArguments(toolName, arguments);
        var projectId = Identifier(arguments, "projectId");
        var projectRelative = Path.Combine(".assistant", "research", projectId);
        var projectRoot = WorkspaceFilePath.Resolve(_workspace, projectRelative);
        Directory.CreateDirectory(projectRoot);

        return toolName switch
        {
            ClientToolNames.ResearchCodeWrite => await WriteAsync(projectId, projectRoot, arguments, cancellationToken).ConfigureAwait(false),
            ClientToolNames.ResearchCodeRestore => await RestoreAsync(projectId, projectRoot, arguments, cancellationToken).ConfigureAwait(false),
            ClientToolNames.ResearchCodeExecute or ClientToolNames.ResearchCodeTest =>
                await RunExperimentAsync(toolName, projectId, projectRelative, projectRoot, arguments, 1, cancellationToken).ConfigureAwait(false),
            ClientToolNames.ResearchCodeBenchmark => await RunExperimentAsync(toolName, projectId, projectRelative, projectRoot,
                arguments, Integer(arguments, "repetitions", 3, 2, 20), cancellationToken).ConfigureAwait(false),
            ClientToolNames.MathSymbolic => await RunGeneratedMathAsync(toolName, projectId, projectRelative, projectRoot,
                arguments, SymbolicHarness(arguments), ".py", "python", cancellationToken).ConfigureAwait(false),
            ClientToolNames.MathNumeric => await RunGeneratedMathAsync(toolName, projectId, projectRelative, projectRoot,
                arguments, NumericHarness(arguments), ".py", "python", cancellationToken).ConfigureAwait(false),
            ClientToolNames.MathSmt => await RunGeneratedMathAsync(toolName, projectId, projectRelative, projectRoot,
                arguments, RequiredString(arguments, "source", 64_000), ".py", "python", cancellationToken).ConfigureAwait(false),
            ClientToolNames.MathFormalProof => await RunGeneratedMathAsync(toolName, projectId, projectRelative, projectRoot,
                arguments, RequiredString(arguments, "source", 64_000), ".lean", "lean", cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentException($"Unbekanntes wissenschaftliches Werkzeug: {toolName}.")
        };
    }

    private static async Task<JsonElement> WriteAsync(string projectId, string projectRoot, JsonElement args, CancellationToken cancellationToken)
    {
        var relative = RequiredString(args, "path", 1_024);
        if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0].Equals(".state", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Der interne Forschungsstatus ist kein Dateischreibziel.");
        var target = WorkspaceFilePath.Resolve(projectRoot, relative);
        var content = RequiredString(args, "content", 64_000, allowEmpty: true);
        var before = File.Exists(target) ? await File.ReadAllBytesAsync(target, cancellationToken).ConfigureAwait(false) : null;
        var expected = OptionalString(args, "expectedSha256");
        if (before is not null && (expected is null || !Hash(before).Equals(expected, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Die Forschungsdatei wurde verändert oder expectedSha256 fehlt. Lies den aktuellen Stand erneut.");
        if (before is null && expected is not null)
            throw new InvalidOperationException("expectedSha256 wurde für eine neue Forschungsdatei angegeben.");

        var bytes = Encoding.UTF8.GetBytes(content);
        var changeSetId = $"cs-{Guid.NewGuid():N}";
        var changeRoot = Path.Combine(projectRoot, ".state", "changesets", changeSetId);
        Directory.CreateDirectory(changeRoot);
        if (before is not null) await File.WriteAllBytesAsync(Path.Combine(changeRoot, "before.bin"), before, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await AtomicWriteAsync(target, bytes, cancellationToken).ConfigureAwait(false);
        var manifest = new ResearchChangeSet(changeSetId, projectId, Path.GetRelativePath(projectRoot, target), before is null,
            before is null ? null : Hash(before), Hash(bytes), DateTimeOffset.UtcNow);
        await WriteJsonAsync(Path.Combine(changeRoot, "manifest.json"), manifest, cancellationToken).ConfigureAwait(false);
        return Serialize(new { success = true, projectId, changeSetId, path = manifest.Path, created = manifest.Created,
            beforeSha256 = manifest.BeforeSha256, afterSha256 = manifest.AfterSha256, verificationStatus = "HashVerified" });
    }

    private static async Task<JsonElement> RestoreAsync(string projectId, string projectRoot, JsonElement args, CancellationToken cancellationToken)
    {
        var changeSetId = Identifier(args, "changeSetId");
        var changeRoot = WorkspaceFilePath.Resolve(projectRoot, Path.Combine(".state", "changesets", changeSetId));
        var manifestPath = Path.Combine(changeRoot, "manifest.json");
        if (!File.Exists(manifestPath)) throw new FileNotFoundException("Das Forschungs-Change-Set existiert nicht.");
        var manifest = JsonSerializer.Deserialize<ResearchChangeSet>(await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false), JsonOptions)
            ?? throw new InvalidDataException("Das Change-Set-Manifest ist ungültig.");
        if (!manifest.ProjectId.Equals(projectId, StringComparison.Ordinal)) throw new UnauthorizedAccessException("Das Change-Set gehört zu einem anderen Forschungsprojekt.");
        var target = WorkspaceFilePath.Resolve(projectRoot, manifest.Path);
        var current = File.Exists(target) ? await File.ReadAllBytesAsync(target, cancellationToken).ConfigureAwait(false) : null;
        if (current is null || !Hash(current).Equals(manifest.AfterSha256, StringComparison.OrdinalIgnoreCase))
        {
            var recoveryId = $"recovery-{Guid.NewGuid():N}";
            var recoveryRoot = Path.Combine(projectRoot, ".state", "recovery", recoveryId);
            Directory.CreateDirectory(recoveryRoot);
            if (current is not null) await File.WriteAllBytesAsync(Path.Combine(recoveryRoot, "current.bin"), current, cancellationToken).ConfigureAwait(false);
            File.Copy(manifestPath, Path.Combine(recoveryRoot, "manifest.json"));
            return Serialize(new { success = false, conflict = true, projectId, changeSetId, recoveryId,
                expectedAfterSha256 = manifest.AfterSha256, actualSha256 = current is null ? null : Hash(current),
                message = "Die Datei wurde nach der AI-Änderung extern verändert; sie wurde nicht überschrieben." });
        }
        if (manifest.Created) File.Delete(target);
        else
        {
            var before = await File.ReadAllBytesAsync(Path.Combine(changeRoot, "before.bin"), cancellationToken).ConfigureAwait(false);
            if (!Hash(before).Equals(manifest.BeforeSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Die Change-Set-Sicherung ist beschädigt.");
            await AtomicWriteAsync(target, before, cancellationToken).ConfigureAwait(false);
        }
        return Serialize(new { success = true, restored = true, projectId, changeSetId, path = manifest.Path,
            restoredSha256 = manifest.Created ? null : manifest.BeforeSha256 });
    }

    private async Task<JsonElement> RunGeneratedMathAsync(string toolName, string projectId, string projectRelative,
        string projectRoot, JsonElement args, string source, string extension, string executable, CancellationToken cancellationToken)
    {
        var experimentId = $"exp-{Guid.NewGuid():N}";
        var generatedDirectory = Path.Combine(projectRoot, "generated");
        Directory.CreateDirectory(generatedDirectory);
        var fileName = experimentId + extension;
        var filePath = Path.Combine(generatedDirectory, fileName);
        await AtomicWriteAsync(filePath, Encoding.UTF8.GetBytes(source), cancellationToken).ConfigureAwait(false);
        var actualExecutable = executable == "python" ? ResolvePython(projectRoot) : executable;
        var commandArgs = JsonSerializer.SerializeToElement(new
        {
            projectId,
            experimentId,
            executable = actualExecutable,
            arguments = new[] { Path.Combine("generated", fileName) },
            workingDirectory = ".",
            timeoutSeconds = Integer(args, "timeoutSeconds", executable == "lean" ? 300 : 120, 1, 3600),
        });
        var result = await RunExperimentAsync(toolName, projectId, projectRelative, projectRoot, commandArgs, 1, cancellationToken).ConfigureAwait(false);
        var node = JsonNode.Parse(result.GetRawText())!.AsObject();
        node["generatedSource"] = Path.GetRelativePath(_workspace, filePath);
        node["toolchain"] = actualExecutable;
        node["formalVerification"] = toolName == ClientToolNames.MathFormalProof
            && node["success"]?.GetValue<bool>() == true ? "KernelAccepted" : "NotEstablished";
        return JsonSerializer.SerializeToElement(node, JsonOptions);
    }

    private async Task<JsonElement> RunExperimentAsync(string toolName, string projectId, string projectRelative,
        string projectRoot, JsonElement args, int repetitions, CancellationToken cancellationToken)
    {
        var experimentId = Identifier(args, "experimentId");
        var executable = RequiredString(args, "executable", 1_024);
        var commandArguments = args.GetProperty("arguments").EnumerateArray().Select(static value => value.GetString()!).ToArray();
        var relativeWorkingDirectory = OptionalString(args, "workingDirectory") ?? ".";
        var workingRoot = relativeWorkingDirectory == "." ? projectRoot : WorkspaceFilePath.Resolve(projectRoot, relativeWorkingDirectory);
        Directory.CreateDirectory(workingRoot);
        var workspaceWorkingDirectory = Path.GetRelativePath(_workspace, workingRoot);
        var timeout = Integer(args, "timeoutSeconds", 600, 1, int.MaxValue);
        var runs = new List<JsonElement>();
        for (var index = 0; index < repetitions; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var command = JsonSerializer.SerializeToElement(new { executable, arguments = commandArguments,
                workingDirectory = workspaceWorkingDirectory, timeoutSeconds = timeout });
            runs.Add(await _coding.ExecuteAsync(ClientToolNames.CodingCommand, command, cancellationToken).ConfigureAwait(false));
        }
        var success = runs.All(static run => run.TryGetProperty("success", out var value) && value.ValueKind == JsonValueKind.True);
        var experimentRoot = Path.Combine(projectRoot, ".state", "experiments", experimentId);
        Directory.CreateDirectory(experimentRoot);
        var manifestPath = Path.Combine(experimentRoot, "manifest.json");
        var environmentLockPath = Path.Combine(projectRoot, "environment.lock.json");
        var sourceHashes = HashSourceFiles(projectRoot);
        await WriteJsonAsync(environmentLockPath, new
        {
            schemaVersion = 1, projectId, executable, arguments = commandArguments,
            sourceHashes, processIsolation = "WindowsJobObjectProcessTree",
            networkIsolation = "NotEnforced", generatedAt = DateTimeOffset.UtcNow,
        }, cancellationToken).ConfigureAwait(false);
        var manifest = new
        {
            experimentId, projectId, toolName, createdAt = DateTimeOffset.UtcNow, executable,
            arguments = commandArguments, workingDirectory = Path.GetRelativePath(projectRoot, workingRoot),
            timeoutSeconds = timeout, repetitions, randomSeed = args.TryGetProperty("randomSeed", out var seed) ? seed.GetInt32() : (int?)null,
            processIsolation = "WindowsJobObjectProcessTree", networkIsolation = "NotEnforced",
            resourceLimits = new { maximumJobMemoryBytes = ScientificProcessLimits.MaximumJobMemoryBytes,
                maximumActiveProcesses = ScientificProcessLimits.MaximumActiveProcesses, wallClockTimeoutSeconds = timeout },
            sourceHashes, environmentLock = Path.GetRelativePath(projectRoot, environmentLockPath), runs,
            verificationStatus = success ? "ProcessSucceeded" : "ProcessFailed",
        };
        await WriteJsonAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
        return Serialize(new { success, projectId, experimentId, repetitions, manifestPath = Path.GetRelativePath(_workspace, manifestPath),
            environmentLock = Path.GetRelativePath(_workspace, environmentLockPath),
            processIsolation = "WindowsJobObjectProcessTree", networkIsolation = "NotEnforced",
            resourceLimits = new { maximumJobMemoryBytes = ScientificProcessLimits.MaximumJobMemoryBytes,
                maximumActiveProcesses = ScientificProcessLimits.MaximumActiveProcesses, wallClockTimeoutSeconds = timeout },
            verificationStatus = success ? "ProcessSucceeded" : "ProcessFailed", runs });
    }

    private static string SymbolicHarness(JsonElement args)
    {
        var expression = JsonSerializer.Serialize(RequiredString(args, "expression", 16_000));
        var operation = JsonSerializer.Serialize(RequiredString(args, "operation", 32));
        var symbol = JsonSerializer.Serialize(OptionalString(args, "symbol") ?? "x");
        return $$$"""
            import json
            try:
                import sympy as sp
                expr = sp.sympify({{{expression}}})
                symbol = sp.Symbol({{{symbol}}})
                operation = {{{operation}}}
                operations = {
                    "simplify": lambda: sp.simplify(expr), "factor": lambda: sp.factor(expr),
                    "expand": lambda: sp.expand(expr), "solve": lambda: sp.solve(expr, symbol),
                    "differentiate": lambda: sp.diff(expr, symbol), "integrate": lambda: sp.integrate(expr, symbol)
                }
                result = operations[operation]()
                print(json.dumps({"success": True, "result": str(result), "latex": sp.latex(result), "sympyVersion": sp.__version__}))
            except ModuleNotFoundError as error:
                print(json.dumps({"success": False, "blocked": True, "missingDependency": error.name}))
                raise SystemExit(3)
            except Exception as error:
                print(json.dumps({"success": False, "error": type(error).__name__, "message": str(error)}))
                raise SystemExit(2)
            """;
    }

    private static string NumericHarness(JsonElement args)
    {
        var expression = JsonSerializer.Serialize(RequiredString(args, "expression", 16_000));
        var precision = Integer(args, "precision", 80, 15, 1000);
        return $$$"""
            import json
            try:
                import mpmath as mp
                mp.mp.dps = {{{precision}}}
                namespace = {name: getattr(mp, name) for name in dir(mp) if not name.startswith("_")}
                result = eval({{{expression}}}, {"__builtins__": {}}, namespace)
                print(json.dumps({"success": True, "result": str(result), "precision": mp.mp.dps, "mpmathVersion": mp.__version__}))
            except ModuleNotFoundError as error:
                print(json.dumps({"success": False, "blocked": True, "missingDependency": error.name}))
                raise SystemExit(3)
            except Exception as error:
                print(json.dumps({"success": False, "error": type(error).__name__, "message": str(error)}))
                raise SystemExit(2)
            """;
    }

    private static string ResolvePython(string projectRoot)
    {
        var local = OperatingSystem.IsWindows() ? Path.Combine(projectRoot, ".venv", "Scripts", "python.exe")
            : Path.Combine(projectRoot, ".venv", "bin", "python");
        return File.Exists(local) ? local : "python";
    }

    private static Dictionary<string, string> HashSourceFiles(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory) && result.Count < 500)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.OrdinalIgnoreCase))
            {
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    var name = Path.GetFileName(entry);
                    if (!name.Equals(".state", StringComparison.OrdinalIgnoreCase)
                        && !name.Equals(".venv", StringComparison.OrdinalIgnoreCase)) pending.Push(entry);
                }
                else result[Path.GetRelativePath(root, entry)] = Hash(File.ReadAllBytes(entry));
                if (result.Count == 500) break;
            }
        }
        return result;
    }

    private static async Task AtomicWriteAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try { await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken) =>
        AtomicWriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions), cancellationToken);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static JsonElement Serialize<T>(T value) => JsonSerializer.SerializeToElement(value, JsonOptions);
    private static string Identifier(JsonElement args, string name)
    {
        var value = RequiredString(args, name, 128);
        if (value.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
            throw new ArgumentException($"{name} enthält ungültige Zeichen.");
        if (value is "." or "..") throw new ArgumentException($"{name} ist ungültig.");
        return value;
    }
    private static string RequiredString(JsonElement args, string name, int maximum, bool allowEmpty = false)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String
            || (!allowEmpty && string.IsNullOrWhiteSpace(value.GetString())) || value.GetString()!.Length > maximum)
            throw new ArgumentException($"{name} ist ungültig.");
        return value.GetString()!;
    }
    private static string? OptionalString(JsonElement args, string name) => args.TryGetProperty(name, out var value) ? value.GetString() : null;
    private static int Integer(JsonElement args, string name, int fallback, int minimum, int maximum)
    {
        if (!args.TryGetProperty(name, out var value)) return fallback;
        if (!value.TryGetInt32(out var number) || number < minimum || number > maximum) throw new ArgumentException($"{name} ist ungültig.");
        return number;
    }

    private sealed record ResearchChangeSet(string ChangeSetId, string ProjectId, string Path, bool Created,
        string? BeforeSha256, string AfterSha256, DateTimeOffset CreatedAt);
}
