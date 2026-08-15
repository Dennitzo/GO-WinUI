using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Runtime;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace GoAi.Server.App.Services;

/// <summary>
/// Owns the user-session services that belong to the GO AI Server desktop app.
/// The gateway itself runs in-process; LM Studio and the Docker Compose project
/// are started and stopped together with the window lifetime.
/// </summary>
internal sealed class ManagedServiceController : IDisposable
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan DockerStartupTimeout = TimeSpan.FromMinutes(2);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ServerRuntimeState _runtime;
    private readonly GoAiServerOptions _options;
    private bool _disposed;

    public ManagedServiceController(ServerRuntimeState runtime, IOptions<GoAiServerOptions> options)
    {
        _runtime = runtime;
        _options = options.Value;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var failures = new List<string>();

            _runtime.SetGatewayState("Startet", "LM Studio und AI-Worker werden gestartet.");
            try
            {
                await StartLmStudioAsync(cancellationToken).ConfigureAwait(false);
                _runtime.WriteLog("Information", "services.lmstudio.started", "LM Studio wurde durch die GO-AI-Server-App gestartet.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                failures.Add("LM Studio");
                _runtime.WriteLog("Error", "services.lmstudio.start_failed", $"LM-Studio-Start fehlgeschlagen ({exception.GetType().Name}).");
            }

            try
            {
                await StartDockerServicesAsync(cancellationToken).ConfigureAwait(false);
                _runtime.WriteLog("Information", "services.docker.started", "Die GO-AI-Docker-Dienste wurden durch die Server-App gestartet.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                failures.Add("Docker-Dienste");
                _runtime.WriteLog("Error", "services.docker.start_failed", $"Docker-Dienste konnten nicht gestartet werden ({exception.GetType().Name}).");
            }

            if (failures.Count > 0)
            {
                _runtime.SetGatewayState("Nicht bereit", $"Start fehlgeschlagen: {string.Join(", ", failures)}.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _runtime.SetGatewayState("Wird beendet", "AI-Worker und LM Studio werden gestoppt.");
            await TryStopDockerServicesAsync(cancellationToken).ConfigureAwait(false);
            await TryStopLmStudioAsync(cancellationToken).ConfigureAwait(false);
            _runtime.SetGatewayState("Beendet", "Alle von der GO-AI-Server-App verwalteten Dienste wurden beendet.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }

    private async Task StartLmStudioAsync(CancellationToken cancellationToken)
    {
        var script = ResolveBundledFile(
            Path.Combine("scripts", "start-lmstudio-server.ps1"),
            Path.Combine("windows", "start-lmstudio-server.ps1"));
        var powerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var result = await RunProcessAsync(
            powerShell,
            [
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                script,
                "-DataRoot",
                _options.DataDirectory,
            ],
            CommandTimeout,
            cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(result, "LM Studio");
    }

    private async Task StartDockerServicesAsync(CancellationToken cancellationToken)
    {
        var deployment = ResolveDockerDeployment();
        var dockerDeadline = DateTimeOffset.UtcNow + DockerStartupTimeout;
        ProcessResult engineResult;
        do
        {
            engineResult = await RunProcessAsync(
                ResolveDockerExecutable(),
                ["version", "--format", "{{.Server.Version}}"],
                TimeSpan.FromSeconds(12),
                cancellationToken).ConfigureAwait(false);
            if (engineResult.ExitCode == 0 && !engineResult.TimedOut)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
        while (DateTimeOffset.UtcNow < dockerDeadline);

        EnsureSucceeded(engineResult, "Docker Desktop");
        var result = await RunProcessAsync(
            ResolveDockerExecutable(),
            BuildComposeArguments(deployment, "up", "-d", "--no-build", "--remove-orphans"),
            CommandTimeout,
            cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(result, "Docker Compose");
    }

    private async Task TryStopDockerServicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var deployment = ResolveDockerDeployment();
            var result = await RunProcessAsync(
                ResolveDockerExecutable(),
                BuildComposeArguments(deployment, "stop", "--timeout", "20"),
                TimeSpan.FromSeconds(90),
                cancellationToken).ConfigureAwait(false);
            EnsureSucceeded(result, "Docker Compose");
            _runtime.WriteLog("Information", "services.docker.stopped", "Die GO-AI-Docker-Dienste wurden gestoppt.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _runtime.WriteLog("Error", "services.docker.stop_failed", $"Docker-Dienste konnten nicht vollständig gestoppt werden ({exception.GetType().Name}).");
        }
    }

    private async Task TryStopLmStudioAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunProcessAsync(
                ResolveLmStudioExecutable(),
                ["server", "stop"],
                TimeSpan.FromSeconds(45),
                cancellationToken).ConfigureAwait(false);
            EnsureSucceeded(result, "LM Studio");
            _runtime.WriteLog("Information", "services.lmstudio.stopped", "Der LM-Studio-API-Server wurde gestoppt.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _runtime.WriteLog("Error", "services.lmstudio.stop_failed", $"LM Studio konnte nicht vollständig gestoppt werden ({exception.GetType().Name}).");
        }
    }

    private DockerDeployment ResolveDockerDeployment()
    {
        var compose = ResolveBundledFile(Path.Combine("deploy", "go-ai", "compose.yaml"));
        var environment = Path.Combine(_options.DataDirectory, "Config", "compose.env");
        if (!File.Exists(environment))
        {
            throw new FileNotFoundException("Die Docker-Umgebungsdatei fehlt. Führe zuerst das Server-Deployment aus.", environment);
        }

        return new DockerDeployment(compose, environment);
    }

    private static List<string> BuildComposeArguments(DockerDeployment deployment, params string[] command)
    {
        var arguments = new List<string>(command.Length + 6)
        {
            "compose",
            "--env-file",
            deployment.EnvironmentPath,
            "--file",
            deployment.ComposePath,
        };
        arguments.AddRange(command);
        return arguments;
    }

    private static string ResolveBundledFile(params string[] relativePaths)
    {
        string?[] searchRoots =
        [
            Path.GetDirectoryName(Environment.ProcessPath),
            Environment.CurrentDirectory,
            AppContext.BaseDirectory,
        ];
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var searchRoot in searchRoots)
        {
            if (string.IsNullOrWhiteSpace(searchRoot))
            {
                continue;
            }

            var directory = new DirectoryInfo(searchRoot);
            for (var depth = 0; directory is not null && depth < 12; depth++, directory = directory.Parent)
            {
                if (!visited.Add(directory.FullName))
                {
                    continue;
                }
                foreach (var relativePath in relativePaths)
                {
                    var candidate = Path.Combine(directory.FullName, relativePath);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        throw new FileNotFoundException($"Eine benötigte Serverdatei wurde nicht gefunden: {string.Join(" oder ", relativePaths)}");
    }

    private static string ResolveDockerExecutable()
    {
        var candidate = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Docker",
            "Docker",
            "resources",
            "bin",
            "docker.exe");
        return File.Exists(candidate) ? candidate : "docker";
    }

    private static string ResolveLmStudioExecutable()
    {
        var candidate = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".lmstudio",
            "bin",
            "lms.exe");
        return File.Exists(candidate) ? candidate : "lms";
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Der Prozess {Path.GetFileName(fileName)} konnte nicht gestartet werden.");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, TimedOut: false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return new ProcessResult(-1, TimedOut: true);
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    private static void EnsureSucceeded(ProcessResult result, string serviceName)
    {
        if (result.TimedOut)
        {
            throw new TimeoutException($"{serviceName} hat das Start-/Stopplimit überschritten.");
        }
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{serviceName} meldete Exitcode {result.ExitCode}.");
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process may have exited between the state check and Kill().
        }
    }

    private sealed record DockerDeployment(string ComposePath, string EnvironmentPath);

    private sealed record ProcessResult(int ExitCode, bool TimedOut);
}
