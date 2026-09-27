using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using GoWinUI.Core.Research;

namespace GoWinUI.App.Services;

/// <summary>
/// Owns each Claude Science project's private data tree and executes Python only
/// in a constrained Docker container. A missing or unbuildable runner is a hard
/// failure; this service never falls back to host PowerShell or host Python.
/// </summary>
public sealed class ResearchSandboxService(AssistantRuntimeProfile profile) : IResearchSandboxService, IDisposable
{
    public const string RunnerImage = "go-ai/research-runner:1";
    private const long MaximumProjectBytes = 100L * 1024 * 1024 * 1024;
    private const int MaximumOutputCharacters = 2 * 1024 * 1024;
    private static readonly Regex SafeId = new("^[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string Root => Path.Combine(profile.DataDirectory, "ResearchSandbox");
    private string Trash => Path.Combine(Root, "Trash");

    public async Task<ResearchSandboxLayout> EnsureProjectAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        var root = Path.GetFullPath(Path.Combine(Root, projectId));
        EnsureBelowRoot(root, Path.GetFullPath(Root));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Root);
            RejectReparsePoint(Path.GetFullPath(Root));
            Directory.CreateDirectory(root);
            RejectReparsePoint(root);
            foreach (var name in new[] { "inputs", "work", "artifacts", "notebooks", "manuscripts", "env", "runs", "snapshots" })
            {
                var path = Path.Combine(root, name);
                Directory.CreateDirectory(path);
                RejectReparsePoint(path);
            }
            var manifest = Path.Combine(root, "sandbox.json");
            if (!File.Exists(manifest))
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schemaVersion = 1,
                    projectId,
                    createdAt = DateTimeOffset.UtcNow,
                    runtime = "docker-linux-isolated",
                    network = "disabled",
                    limits = new { cpuCores = 16, memoryBytes = 48L * 1024 * 1024 * 1024, maximumProjectBytes = MaximumProjectBytes, maximumStageSeconds = 7200 },
                }, JsonOptions);
                await File.WriteAllBytesAsync(manifest, bytes, cancellationToken).ConfigureAwait(false);
            }
            var now = File.GetCreationTimeUtc(manifest);
            return new(projectId, root,
                Path.Combine(root, "inputs"), Path.Combine(root, "work"), Path.Combine(root, "artifacts"),
                Path.Combine(root, "notebooks"), Path.Combine(root, "manuscripts"), Path.Combine(root, "env"),
                Path.Combine(root, "runs"), Path.Combine(root, "snapshots"), new DateTimeOffset(now, TimeSpan.Zero),
                "ready; execution is network-isolated; GPU passthrough is disabled until verified");
        }
        finally { _gate.Release(); }
    }

    public async Task<ResearchSandboxRuntimeStatus> PrepareRuntimeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var daemon = await RunDockerCaptureAsync(["info", "--format", "{{.ServerVersion}}"], cancellationToken).ConfigureAwait(false);
            if (daemon.ExitCode != 0) return new(false, "docker-unavailable", daemon.StandardError.Trim());
            await EnsureRunnerImageAsync(cancellationToken).ConfigureAwait(false);
            return new(true, "ready-network-isolated", "Docker runner ready; container egress is disabled and GPU passthrough is not enabled.");
        }
        catch (Exception exception) when ((exception is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or OperationCanceledException)
                                          && !cancellationToken.IsCancellationRequested)
        {
            return new(false, "runner-unavailable", exception.Message);
        }
    }

    public async Task<ResearchSandboxFileChange> WriteTextAsync(
        string projectId,
        string relativePath,
        string content,
        string? expectedSha256 = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var layout = await EnsureProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        var parentRelative = Path.GetDirectoryName(relativePath.Replace('\\', Path.DirectorySeparatorChar));
        if (!string.IsNullOrWhiteSpace(parentRelative)) CreateSafeWorkDirectory(layout.WorkPath, parentRelative);
        var destination = ResolveWorkFile(layout.WorkPath, relativePath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var exists = File.Exists(destination);
            var beforeBytes = exists ? await File.ReadAllBytesAsync(destination, cancellationToken).ConfigureAwait(false) : null;
            var beforeHash = beforeBytes is null ? null : Hash(beforeBytes);
            if (expectedSha256 is not null && !string.Equals(expectedSha256, beforeHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Die Sandboxdatei wurde seit dem erwarteten Stand geändert; es wurde nichts überschrieben.");
            var changeSetId = Guid.NewGuid().ToString("N");
            var snapshotDirectory = Path.Combine(layout.SnapshotsPath, changeSetId);
            Directory.CreateDirectory(snapshotDirectory);
            if (beforeBytes is not null)
                await File.WriteAllBytesAsync(Path.Combine(snapshotDirectory, "before.bin"), beforeBytes, cancellationToken).ConfigureAwait(false);
            var contentBytes = System.Text.Encoding.UTF8.GetBytes(content);
            var temp = destination + "." + changeSetId + ".tmp";
            await File.WriteAllBytesAsync(temp, contentBytes, cancellationToken).ConfigureAwait(false);
            File.Move(temp, destination, overwrite: true);
            var afterHash = Hash(contentBytes);
            var change = new ResearchSandboxFileChange(changeSetId, relativePath.Replace('\\', '/'), beforeHash, afterHash, !exists);
            await File.WriteAllTextAsync(Path.Combine(snapshotDirectory, "manifest.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, projectId, change.ChangeSetId, change.RelativePath,
                change.BeforeSha256, change.AfterSha256, change.IsNewFile, createdAt = DateTimeOffset.UtcNow,
            }, JsonOptions), cancellationToken).ConfigureAwait(false);
            return change;
        }
        finally { _gate.Release(); }
    }

    public async Task RestoreChangeSetAsync(string projectId, string changeSetId, CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        ValidateProjectId(changeSetId);
        var layout = await EnsureProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        var snapshotDirectory = Path.Combine(layout.SnapshotsPath, changeSetId);
        var manifestPath = Path.Combine(snapshotDirectory, "manifest.json");
        if (!File.Exists(manifestPath)) throw new FileNotFoundException("Das Änderungspaket wurde nicht gefunden.", manifestPath);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false));
        var root = manifest.RootElement;
        if (!string.Equals(root.GetProperty("projectId").GetString(), projectId, StringComparison.Ordinal)
            || !string.Equals(root.GetProperty("changeSetId").GetString(), changeSetId, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Das Änderungspaket gehört zu einem anderen Forschungsvorhaben.");
        var relativePath = root.GetProperty("relativePath").GetString()!;
        var target = ResolveWorkFile(layout.WorkPath, relativePath);
        var expectedAfter = root.GetProperty("afterSha256").GetString()!;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(target) || !string.Equals(Hash(await File.ReadAllBytesAsync(target, cancellationToken).ConfigureAwait(false)), expectedAfter, StringComparison.OrdinalIgnoreCase))
            {
                var recovery = Path.Combine(layout.SnapshotsPath, "recovery-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(recovery);
                if (File.Exists(target)) File.Copy(target, Path.Combine(recovery, "external-current.bin"));
                if (File.Exists(Path.Combine(snapshotDirectory, "before.bin")))
                    File.Copy(Path.Combine(snapshotDirectory, "before.bin"), Path.Combine(recovery, "expected-before.bin"));
                await File.WriteAllTextAsync(Path.Combine(recovery, "conflict.json"), JsonSerializer.Serialize(new { projectId, changeSetId, relativePath, expectedAfter, detectedAt = DateTimeOffset.UtcNow }), cancellationToken).ConfigureAwait(false);
                throw new IOException("Die Datei wurde nach der AI-Änderung manuell verändert. Das Tool hat nichts überschrieben und ein Recovery-Bundle erstellt.");
            }
            if (root.GetProperty("isNewFile").GetBoolean()) File.Delete(target);
            else
            {
                var before = await File.ReadAllBytesAsync(Path.Combine(snapshotDirectory, "before.bin"), cancellationToken).ConfigureAwait(false);
                var temp = target + ".restore.tmp";
                await File.WriteAllBytesAsync(temp, before, cancellationToken).ConfigureAwait(false);
                File.Move(temp, target, overwrite: true);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<ResearchSandboxRunResult> RunPythonAsync(
        string projectId,
        string relativeScriptPath,
        IReadOnlyList<string>? arguments = null,
        int timeoutSeconds = 7200,
        string? relativeWorkingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var layout = await EnsureProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (timeoutSeconds is < 1 or > 7200) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "Eine Rechenetappe darf höchstens zwei Stunden laufen.");
        var script = ResolveWorkFile(layout.WorkPath, relativeScriptPath);
        if (!File.Exists(script)) throw new FileNotFoundException("Das Python-Skript liegt nicht im Forschungsbereich work.", script);
        var workingDirectory = ResolveWorkDirectory(layout.WorkPath, relativeWorkingDirectory);
        if (await DirectoryBytesAsync(layout.RootPath, cancellationToken).ConfigureAwait(false) > MaximumProjectBytes)
            throw new IOException("Das 100-GiB-Sandboxkontingent ist ausgeschöpft.");
        await EnsureRunnerImageAsync(cancellationToken).ConfigureAwait(false);

        var runId = Guid.NewGuid().ToString("N");
        var containerName = "go-research-" + runId;
        var started = DateTimeOffset.UtcNow;
        await using var scriptStream = File.OpenRead(script);
        var scriptHash = Convert.ToHexString(await SHA256.HashDataAsync(scriptStream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        var scriptContainerPath = "/sandbox/work/" + Path.GetRelativePath(layout.WorkPath, script).Replace('\\', '/');
        var startInfo = new ProcessStartInfo("docker") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        Add(startInfo, "run"); Add(startInfo, "--rm"); Add(startInfo, "--init"); Add(startInfo, "--name"); Add(startInfo, containerName);
        Add(startInfo, "--network"); Add(startInfo, "none"); Add(startInfo, "--read-only"); Add(startInfo, "--cap-drop"); Add(startInfo, "ALL");
        Add(startInfo, "--security-opt"); Add(startInfo, "no-new-privileges:true"); Add(startInfo, "--pids-limit"); Add(startInfo, "256");
        Add(startInfo, "--cpus"); Add(startInfo, "16"); Add(startInfo, "--memory"); Add(startInfo, "48g"); Add(startInfo, "--memory-swap"); Add(startInfo, "48g");
        Add(startInfo, "--user"); Add(startInfo, "10001:10001"); Add(startInfo, "--tmpfs"); Add(startInfo, "/tmp:rw,noexec,nosuid,nodev,size=536870912");
        AddMount(startInfo, layout.InputsPath, "/sandbox/inputs", readOnly: true);
        AddMount(startInfo, layout.WorkPath, "/sandbox/work", readOnly: false);
        AddMount(startInfo, layout.ArtifactsPath, "/sandbox/artifacts", readOnly: false);
        AddMount(startInfo, layout.NotebooksPath, "/sandbox/notebooks", readOnly: false);
        AddMount(startInfo, layout.ManuscriptsPath, "/sandbox/manuscripts", readOnly: false);
        AddMount(startInfo, layout.EnvironmentPath, "/sandbox/env", readOnly: true);
        AddMount(startInfo, layout.RunsPath, "/sandbox/runs", readOnly: false);
        AddMount(startInfo, layout.SnapshotsPath, "/sandbox/snapshots", readOnly: true);
        Add(startInfo, "--workdir");
        var relativeContainerWorkingDirectory = Path.GetRelativePath(layout.WorkPath, workingDirectory).Replace('\\', '/');
        Add(startInfo, "/sandbox/work" + (relativeContainerWorkingDirectory == "." ? string.Empty : "/" + relativeContainerWorkingDirectory));
        Add(startInfo, RunnerImage); Add(startInfo, "python3"); Add(startInfo, "-I"); Add(startInfo, scriptContainerPath);
        foreach (var argument in arguments ?? [])
        {
            if (argument.Length > 4000 || argument.Any(char.IsControl)) throw new ArgumentException("Ein Python-Argument ist ungültig.", nameof(arguments));
            Add(startInfo, argument);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException("Docker konnte den isolierten Forschungscontainer nicht starten.");
        var stdoutTask = ReadBoundedAsync(process.StandardOutput, MaximumOutputCharacters);
        var stderrTask = ReadBoundedAsync(process.StandardError, MaximumOutputCharacters);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var timedOut = false;
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            await KillContainerAsync(containerName).ConfigureAwait(false);
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None).ConfigureAwait(false); }
            catch (TimeoutException) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
        }
        catch (OperationCanceledException)
        {
            await KillContainerAsync(containerName).ConfigureAwait(false);
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        var completed = DateTimeOffset.UtcNow;
        var result = new ResearchSandboxRunResult(runId, timedOut ? 124 : process.ExitCode, timedOut, stdout, stderr, started, completed,
            $"docker run {RunnerImage} python3 -I {scriptContainerPath}");
        var runRecord = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, projectId, runId, script = Path.GetRelativePath(layout.RootPath, script).Replace('\\', '/'),
            scriptSha256 = scriptHash, startedAt = started, completedAt = completed, result.ExitCode, result.TimedOut,
            result.Command, environment = RunnerImage, networkIsolation = "docker --network none",
            resourceLimits = new { cpuCores = 16, memory = "48g", pids = 256, timeoutSeconds }, result.StandardOutput, result.StandardError,
        }, JsonOptions);
        await File.WriteAllBytesAsync(Path.Combine(layout.RunsPath, runId + ".json"), runRecord, CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    public async Task<string> ArchiveProjectAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var source = Path.Combine(Root, projectId);
            if (!Directory.Exists(source)) throw new DirectoryNotFoundException("Das Forschungsprojekt wurde nicht gefunden.");
            Directory.CreateDirectory(Trash);
            var archiveId = projectId + "_" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
            var destination = Path.Combine(Trash, archiveId);
            await File.WriteAllTextAsync(Path.Combine(source, "archive.json"), JsonSerializer.Serialize(new { projectId, archivedAt = DateTimeOffset.UtcNow, purgeAfter = DateTimeOffset.UtcNow.AddDays(30) }), cancellationToken).ConfigureAwait(false);
            Directory.Move(source, destination);
            return destination;
        }
        finally { _gate.Release(); }
    }

    public async Task RestoreProjectAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var destination = Path.Combine(Root, projectId);
            if (Directory.Exists(destination)) throw new IOException("Das Forschungsprojekt ist bereits aktiv.");
            if (!Directory.Exists(Trash)) throw new DirectoryNotFoundException("Es gibt keinen Wiederherstellungsbereich.");
            var matches = Directory.GetDirectories(Trash, projectId + "_*", SearchOption.TopDirectoryOnly)
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).ToArray();
            var source = matches.FirstOrDefault() ?? throw new DirectoryNotFoundException("Es wurde kein archiviertes Forschungsprojekt gefunden.");
            Directory.Move(source, destination);
            var metadata = Path.Combine(destination, "archive.json");
            if (File.Exists(metadata)) File.Delete(metadata);
        }
        finally { _gate.Release(); }
    }

    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(Trash)) return 0;
        var removed = 0;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var directory in Directory.GetDirectories(Trash, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var metadataPath = Path.Combine(directory, "archive.json");
                if (!File.Exists(metadataPath)) continue;
                try
                {
                    using var document = JsonDocument.Parse(await File.ReadAllTextAsync(metadataPath, cancellationToken).ConfigureAwait(false));
                    if (!document.RootElement.TryGetProperty("purgeAfter", out var value) || !value.TryGetDateTimeOffset(out var purgeAfter) || purgeAfter > DateTimeOffset.UtcNow) continue;
                    Directory.Delete(directory, recursive: true);
                    removed++;
                }
                catch (JsonException) { /* Leave corrupt recovery entries untouched. */ }
            }
        }
        finally { _gate.Release(); }
        return removed;
    }

    private static async Task EnsureRunnerImageAsync(CancellationToken cancellationToken)
    {
        var image = await RunDockerCaptureAsync(["image", "inspect", RunnerImage], cancellationToken).ConfigureAwait(false);
        if (image.ExitCode == 0) return;
        var context = Path.Combine(AppContext.BaseDirectory, "research-runner");
        if (!File.Exists(Path.Combine(context, "Dockerfile")))
            throw new InvalidOperationException("Der isolierte Research-Runner fehlt in der Installation; es wird kein Host-Python als Ersatz gestartet.");
        var build = await RunDockerCaptureAsync(["build", "--tag", RunnerImage, context], cancellationToken).ConfigureAwait(false);
        if (build.ExitCode != 0) throw new InvalidOperationException("Der isolierte Research-Runner konnte nicht gebaut werden: " + build.StandardError);
    }

    private static async Task KillContainerAsync(string containerName)
    {
        try { _ = await RunDockerCaptureAsync(["kill", containerName], CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException) { }
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunDockerCaptureAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("docker") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) Add(start, argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("Docker CLI konnte nicht gestartet werden.");
        var stdout = ReadBoundedAsync(process.StandardOutput, MaximumOutputCharacters);
        var stderr = ReadBoundedAsync(process.StandardError, MaximumOutputCharacters);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximumCharacters)
    {
        var buffer = new char[8192];
        var builder = new System.Text.StringBuilder(Math.Min(maximumCharacters, 8192));
        var truncated = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (count == 0) break;
            var append = Math.Min(count, maximumCharacters - builder.Length);
            if (append > 0) builder.Append(buffer, 0, append);
            if (append < count) truncated = true;
        }
        if (truncated) builder.Append("\n[Ausgabe gekürzt]");
        return builder.ToString();
    }

    private static async Task<long> DirectoryBytesAsync(string root, CancellationToken token)
    {
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            RejectReparsePoint(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("Verknüpfungen sind innerhalb der Sandboxkontingentprüfung unzulässig.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else
                {
                    total = checked(total + new FileInfo(entry).Length);
                    if (total > MaximumProjectBytes) return total;
                }
            }
        }
        await Task.CompletedTask.ConfigureAwait(false);
        return total;
    }

    private static string ResolveWorkFile(string workRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Any(char.IsControl))
            throw new UnauthorizedAccessException("Der Skriptpfad muss relativ zum Sandbox-Arbeitsverzeichnis sein.");
        var root = Path.GetFullPath(workRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(workRoot, relativePath));
        EnsureBelowRoot(full, root);
        var relativeParent = Path.GetRelativePath(workRoot, Path.GetDirectoryName(full)!);
        var currentPath = Path.GetFullPath(workRoot);
        RejectReparsePoint(currentPath);
        if (relativeParent != ".")
        {
            foreach (var segment in relativeParent.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                currentPath = Path.Combine(currentPath, segment);
                if (Directory.Exists(currentPath)) RejectReparsePoint(currentPath);
                else break;
            }
        }
        if (File.Exists(full)) RejectReparsePoint(full);
        return full;
    }

    private static string ResolveWorkDirectory(string workRoot, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath == ".") return Path.GetFullPath(workRoot);
        return CreateSafeWorkDirectory(workRoot, relativePath);
    }

    private static string CreateSafeWorkDirectory(string workRoot, string relativePath)
    {
        if (Path.IsPathRooted(relativePath) || relativePath.Any(char.IsControl))
            throw new UnauthorizedAccessException("Der Arbeitsordner muss relativ zu work sein.");
        var root = Path.GetFullPath(workRoot);
        RejectReparsePoint(root);
        var current = root;
        foreach (var segment in relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..") throw new UnauthorizedAccessException("Punktsegmente sind in Sandboxpfaden unzulässig.");
            current = Path.Combine(current, segment);
            if (Directory.Exists(current)) RejectReparsePoint(current);
            else
            {
                Directory.CreateDirectory(current);
                RejectReparsePoint(current);
            }
            EnsureBelowRoot(current, root);
        }
        return current;
    }

    private static void AddMount(ProcessStartInfo start, string source, string target, bool readOnly)
    {
        RejectReparsePoint(source);
        Add(start, "--mount");
        Add(start, $"type=bind,source={Path.GetFullPath(source)},target={target}{(readOnly ? ",readonly" : string.Empty)}");
    }

    private static void Add(ProcessStartInfo start, string argument) => start.ArgumentList.Add(argument);
    public void Dispose() => _gate.Dispose();
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void ValidateProjectId(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId) || !SafeId.IsMatch(projectId) || projectId is "." or "..")
            throw new ArgumentException("Die Forschungsprojektkennung ist ungültig.", nameof(projectId));
    }

    private static void EnsureBelowRoot(string path, string root)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Der Sandboxpfad verlässt den Forschungsbereich.");
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Verknüpfungen sind innerhalb der Forschungs-Sandboxgrenze nicht zulässig.");
    }
}
