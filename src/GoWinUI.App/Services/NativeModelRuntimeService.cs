using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;

namespace GoWinUI.App.Services;

/// <summary>Starts the installed Windows runtime when this PC also hosts the configured gateway.</summary>
public sealed class NativeModelRuntimeService : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<Uri, bool> _isLocalGateway;
    private readonly Func<CancellationToken, Task<bool>> _probe;
    private readonly Func<CancellationToken, Task> _start;
    private readonly Func<DateTimeOffset> _now;
    private DateTimeOffset _nextProbe;
    private Exception? _lastFailure;

    public NativeModelRuntimeService()
        : this(IsLocalGateway, ProbeAsync, StartInstalledRuntimeAsync, () => DateTimeOffset.UtcNow) { }

    internal NativeModelRuntimeService(Func<Uri, bool> isLocalGateway,
        Func<CancellationToken, Task<bool>> probe, Func<CancellationToken, Task> start,
        Func<DateTimeOffset>? now = null)
    {
        _isLocalGateway = isLocalGateway;
        _probe = probe;
        _start = start;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task EnsureStartedAsync(Uri gateway, CancellationToken cancellationToken)
    {
        // Publish smoke checks use an isolated data profile and must not start shared AI services.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GO_SMOKE_INSTANCE_KEY"))
            || !_isLocalGateway(gateway)) return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_now() < _nextProbe)
            {
                if (_lastFailure is not null)
                    throw new InvalidOperationException(_lastFailure.Message, _lastFailure);
                return;
            }
            if (!await _probe(cancellationToken).ConfigureAwait(false))
            {
                await _start(cancellationToken).ConfigureAwait(false);
                if (!await _probe(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("Windows llama.cpp wurde gestartet, ist auf http://127.0.0.1:8081 aber noch nicht erreichbar.");
            }
            _lastFailure = null;
            _nextProbe = _now().AddSeconds(5);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _lastFailure = exception;
            _nextProbe = _now().AddSeconds(10);
            throw;
        }
        finally { _gate.Release(); }
    }

    internal static bool IsLocalGateway(Uri gateway)
    {
        if (gateway.IsLoopback || string.Equals(gateway.Host, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!IPAddress.TryParse(gateway.Host.Trim('[', ']'), out var address)) return false;
        if (IPAddress.IsLoopback(address)) return true;
        return NetworkInterface.GetAllNetworkInterfaces().SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Any(item => item.Address.MapToIPv6().Equals(address.MapToIPv6()));
    }

    private static async Task<bool> ProbeAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        try
        {
            using var response = await http.GetAsync("http://127.0.0.1:8081/health", timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    private static async Task StartInstalledRuntimeAsync(CancellationToken cancellationToken)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var stateDirectory = Path.Combine(userProfile, ".go-winui", "native-runtime");
        // Use stable paths instead of the single-file extraction directory. The supervisor remains
        // available to the Docker gateway after GO closes and across application updates.
        var supportDirectory = Path.Combine(stateDirectory, "support");
        foreach (var relative in new[] { Path.Combine("windows", "manage-coding-llama.ps1"), Path.Combine("workers", "coding", "catalog.py") })
        {
            var source = ApplicationAssets.ResolvePath("Assets", "NativeRuntime", relative);
            if (!File.Exists(source))
                throw new FileNotFoundException("Die portable GO-Version enthält die Hilfsdateien für Windows llama.cpp nicht. GO bitte vollständig aktualisieren.", source);
            var destination = Path.Combine(supportDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var contents = await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false);
            var previous = File.Exists(destination) ? await File.ReadAllBytesAsync(destination, cancellationToken).ConfigureAwait(false) : null;
            if (previous is null || !contents.AsSpan().SequenceEqual(previous))
            {
                var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await File.WriteAllBytesAsync(temporary, contents, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, destination, overwrite: true);
            }
        }

        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = supportDirectory,
        };
        // A detached Python/llama descendant can inherit pipe handles even after the
        // PowerShell launcher exits. Never wait for its process-lifetime stdout EOF.
        var errorFile = Path.Combine(stateDirectory, "startup-" + Guid.NewGuid().ToString("N") + ".error.txt");
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(supportDirectory, "windows", "manage-coding-llama.ps1"), "-Action", "Start", "-StateDirectory", stateDirectory,
            "-ModelRoot", ResolveModelRoot(userProfile), "-ErrorFile", errorFile }) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new InvalidOperationException("Der Starthelfer für Windows llama.cpp konnte nicht gestartet werden.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            // Only stop this short-lived launcher. A supervisor that already started owns its
            // own process tree and is discovered on the next probe.
            if (!process.HasExited) process.Kill();
            if (cancellationToken.IsCancellationRequested) throw;
            throw new InvalidOperationException($"Windows llama.cpp startet nicht innerhalb von 45 Sekunden. Diagnose: {Path.Combine(stateDirectory, "stderr.log")}");
        }
        if (process.ExitCode != 0)
        {
            var error = File.Exists(errorFile) ? (await File.ReadAllTextAsync(errorFile, cancellationToken).ConfigureAwait(false)).Trim()
                : $"Starthelfer beendet mit Exitcode {process.ExitCode}. Diagnose: {Path.Combine(stateDirectory, "stderr.log")}";
            throw new InvalidOperationException($"Windows llama.cpp konnte nicht gestartet werden: {error}");
        }
    }

    internal static string ResolveModelRoot(string userProfile)
    {
        var stackEnvironment = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GO-AI-Stack", "stack.env");
        IEnumerable<string> values = File.Exists(stackEnvironment) ? File.ReadLines(stackEnvironment) : [];
        var configured = values.FirstOrDefault(line => line.StartsWith("GO_AI_NATIVE_MODEL_ROOT=", StringComparison.Ordinal))
            ?? values.FirstOrDefault(line => line.StartsWith("GO_AI_CODING_MODEL_ROOT=", StringComparison.Ordinal));
        var modelRoot = configured?[(configured.IndexOf('=') + 1)..].Trim();
        return string.IsNullOrWhiteSpace(modelRoot) ? Path.Combine(userProfile, ".cache", "huggingface", "hub") : Path.GetFullPath(modelRoot);
    }

    public void Dispose() => _gate.Dispose();
}
