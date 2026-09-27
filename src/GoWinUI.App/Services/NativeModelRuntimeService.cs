using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace GoWinUI.App.Services;

/// <summary>Starts the installed Windows runtime when this PC also hosts the configured gateway.</summary>
public sealed class NativeModelRuntimeService : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<Uri, bool> _isLocalGateway;
    private readonly Func<CancellationToken, Task<bool>> _probe;
    private readonly Func<CancellationToken, Task> _start;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<CancellationToken, Task> _stop;
    private readonly Func<Uri, CancellationToken, Task<bool?>> _gatewayIdle;
    private readonly TimeSpan _shutdownDrainTimeout;
    private readonly string _runtimeEndpoint;
    private int _stopping;
    private bool _stopped;
    private DateTimeOffset _nextProbe;
    private Exception? _lastFailure;

    public NativeModelRuntimeService()
        : this(AssistantRuntimeProfile.Resolve()) { }

    public NativeModelRuntimeService(AssistantRuntimeProfile profile)
        : this(
            IsLocalGateway,
            token => ProbeAsync(profile.NativePort, token),
            token => RunInstalledRuntimeAsync(profile, "Start", token),
            () => DateTimeOffset.UtcNow,
            token => RunInstalledRuntimeAsync(profile, "Stop", token),
            runtimeEndpoint: $"http://127.0.0.1:{profile.NativePort}") { }

    internal NativeModelRuntimeService(Func<Uri, bool> isLocalGateway,
        Func<CancellationToken, Task<bool>> probe, Func<CancellationToken, Task> start,
        Func<DateTimeOffset>? now = null, Func<CancellationToken, Task>? stop = null,
        Func<Uri, CancellationToken, Task<bool?>>? gatewayIdle = null, TimeSpan? shutdownDrainTimeout = null,
        string runtimeEndpoint = "http://127.0.0.1:8081")
    {
        _isLocalGateway = isLocalGateway;
        _probe = probe;
        _start = start;
        _stop = stop ?? StopDefaultRuntimeAsync;
        _gatewayIdle = gatewayIdle ?? ProbeGatewayIdleAsync;
        _shutdownDrainTimeout = shutdownDrainTimeout ?? TimeSpan.FromSeconds(15);
        if (_shutdownDrainTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(shutdownDrainTimeout));
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _runtimeEndpoint = runtimeEndpoint;
    }

    public async Task EnsureStartedAsync(Uri gateway, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _stopping) != 0) return;
        // Publish smoke checks use an isolated data profile and must not start shared AI services.
        if (AutostartDisabled()
            || !_isLocalGateway(gateway)) return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _stopping) != 0) return;
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
                    throw new InvalidOperationException($"Windows llama.cpp wurde gestartet, ist auf {_runtimeEndpoint} aber noch nicht erreichbar.");
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

    public void BeginShutdown() => Interlocked.Exchange(ref _stopping, 1);

    public async Task StopAsync(Uri gateway, CancellationToken cancellationToken = default)
    {
        BeginShutdown();
        if (AutostartDisabled()
            || !_isLocalGateway(gateway)) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopped) return;
            // The HTTP cancel acknowledgement precedes native cancellation and
            // its durable KV snapshot. Keep the shared runtime alive until the
            // gateway confirms all leases, workloads and queued work drained.
            // Unknown status must not kill another portable instance's run.
            if (!await WaitForGatewayIdleAsync(gateway, cancellationToken).ConfigureAwait(false)) return;
            await _stop(cancellationToken).ConfigureAwait(false);
            _stopped = true;
        }
        finally { _gate.Release(); }
    }

    private async Task<bool> WaitForGatewayIdleAsync(Uri gateway, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_shutdownDrainTimeout);
        try
        {
            while (true)
            {
                var idle = await _gatewayIdle(gateway, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
                if (idle is null) return false;
                if (idle.Value) return true;
                await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return false; }
    }

    private static async Task<bool?> ProbeGatewayIdleAsync(Uri gateway, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
        try
        {
            using var response = await http.GetAsync(new Uri(gateway, "v1/gpu/status"), timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false),
                cancellationToken: timeout.Token).ConfigureAwait(false);
            return ReadGatewayIdle(json.RootElement);
        }
        catch (HttpRequestException) { return null; }
        catch (JsonException) { return null; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
    }

    internal static bool? ReadGatewayIdle(JsonElement status)
    {
        if (status.ValueKind != JsonValueKind.Object
            || !status.TryGetProperty("queueLength", out var queue) || queue.ValueKind != JsonValueKind.Number
            || !queue.TryGetInt32(out var queued) || queued < 0
            || !status.TryGetProperty("activeWorkloads", out var workloads) || workloads.ValueKind != JsonValueKind.Array)
            return null;
        if (queued > 0 || workloads.GetArrayLength() > 0) return false;
        // GoAiProtocol omits null-valued fields. An absent activeLease is the
        // normal null representation only after both explicit counters are idle.
        if (!status.TryGetProperty("activeLease", out var lease) || lease.ValueKind == JsonValueKind.Null) return true;
        return lease.ValueKind == JsonValueKind.String ? false : null;
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

    private static bool AutostartDisabled() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASSISTANT_DISABLE_RUNTIME_AUTOSTART"))
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GO_SMOKE_INSTANCE_KEY"));

    private static Task StopDefaultRuntimeAsync(CancellationToken cancellationToken) =>
        RunInstalledRuntimeAsync(AssistantRuntimeProfile.Resolve(), "Stop", cancellationToken);

    private static async Task<bool> ProbeAsync(int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        try
        {
            using var response = await http.GetAsync($"http://127.0.0.1:{port}/health", timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    private static async Task RunInstalledRuntimeAsync(
        AssistantRuntimeProfile profile,
        string action,
        CancellationToken cancellationToken)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var stateDirectory = profile.NativeStateDirectory;
        // Use stable paths instead of the single-file extraction directory. The supervisor remains
        // identifiable for shutdown and across application updates.
        var supportDirectory = Path.Combine(stateDirectory, "support");
        foreach (var relative in new[] { Path.Combine("windows", "manage-coding-llama.ps1"), Path.Combine("windows", "manage-llama-server.ps1"), Path.Combine("workers", "coding", "session_cache.py"), Path.Combine("workers", "coding", "catalog.py") })
        {
            var source = ApplicationAssets.ResolvePath("Assets", "NativeRuntime", relative);
            if (!File.Exists(source))
                throw new FileNotFoundException("Die portable Anwendung enthält die Hilfsdateien für Windows llama.cpp nicht. Bitte die Anwendung vollständig aktualisieren.", source);
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

        if (string.Equals(action, "Start", StringComparison.OrdinalIgnoreCase)
            && !profile.HasExplicitNativeBinaryPath)
            profile = profile with { NativeBinaryPath = await EnsureOfficialLlamaRuntimeAsync(profile, supportDirectory, cancellationToken).ConfigureAwait(false) };

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
        foreach (var argument in BuildRuntimeManagerArguments(profile, action, supportDirectory, errorFile, userProfile))
            info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new InvalidOperationException("Der Starthelfer für Windows llama.cpp konnte nicht gestartet werden.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(action == "Stop" ? 660 : 45));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            // Only stop this short-lived launcher. A supervisor that already started owns its
            // own process tree and is discovered on the next probe.
            if (!process.HasExited) process.Kill();
            if (cancellationToken.IsCancellationRequested) throw;
            throw new InvalidOperationException($"Windows llama.cpp: Aktion {action} hat das Zeitlimit überschritten. Diagnose: {Path.Combine(stateDirectory, "stderr.log")}");
        }
        if (process.ExitCode != 0)
        {
            var error = File.Exists(errorFile) ? (await File.ReadAllTextAsync(errorFile, cancellationToken).ConfigureAwait(false)).Trim()
                : $"Starthelfer beendet mit Exitcode {process.ExitCode}. Diagnose: {Path.Combine(stateDirectory, "stderr.log")}";
            throw new InvalidOperationException($"Windows llama.cpp: Aktion {action} fehlgeschlagen: {error}");
        }
    }

    private static async Task<string> EnsureOfficialLlamaRuntimeAsync(
        AssistantRuntimeProfile profile,
        string supportDirectory,
        CancellationToken cancellationToken)
    {
        var installRoot = Environment.GetEnvironmentVariable("ASSISTANT_LLAMA_INSTALL_ROOT");
        if (string.IsNullOrWhiteSpace(installRoot))
            installRoot = Path.Combine(profile.DataDirectory, "NativeRuntime", "llama.cpp");
        installRoot = Path.GetFullPath(installRoot);
        var resolvedPathFile = Path.Combine(profile.NativeStateDirectory, "official-llama-server.path");
        var updater = Path.Combine(supportDirectory, "windows", "manage-llama-server.ps1");
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = supportDirectory,
        };
        foreach (var argument in BuildRuntimeUpdaterArguments(updater, installRoot, resolvedPathFile))
            info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new InvalidOperationException("Der GitHub-Updater für Windows llama.cpp konnte nicht gestartet werden.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Das automatische llama.cpp-Update hat das Zeitlimit von 30 Minuten überschritten.");
        }
        if (process.ExitCode != 0 || !File.Exists(resolvedPathFile))
            throw new InvalidOperationException($"Das automatische llama.cpp-Update ist fehlgeschlagen (Exitcode {process.ExitCode}).");
        var binary = (await File.ReadAllTextAsync(resolvedPathFile, cancellationToken).ConfigureAwait(false)).Trim();
        if (string.IsNullOrWhiteSpace(binary) || !Path.IsPathFullyQualified(binary) || !File.Exists(binary)
            || !string.Equals(Path.GetFileName(binary), "llama-server.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Der llama.cpp-Updater lieferte keinen gültigen nativen Serverpfad.");
        var root = installRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        binary = Path.GetFullPath(binary);
        if (!binary.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Der llama.cpp-Updater lieferte einen Pfad außerhalb seines Installationsverzeichnisses.");
        return binary;
    }

    internal static string[] BuildRuntimeUpdaterArguments(string updater, string installRoot, string resolvedPathFile) =>
    [
        "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", updater,
        "-Action", "Update", "-InstallRoot", installRoot, "-ResolvedPathFile", resolvedPathFile, "-SkipFirewall",
    ];

    internal static string[] BuildRuntimeManagerArguments(
        AssistantRuntimeProfile profile,
        string action,
        string supportDirectory,
        string errorFile,
        string userProfile)
    {
        return
        [
            "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(supportDirectory, "windows", "manage-coding-llama.ps1"),
            "-Action", action,
            "-StateDirectory", profile.NativeStateDirectory,
            "-ModelRoot", ResolveModelRoot(userProfile, profile),
            "-BinaryPath", profile.NativeBinaryPath,
            "-Port", profile.NativePort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-ErrorFile", errorFile,
        ];
    }

    internal static string ResolveModelRoot(string userProfile)
    {
        return ResolveModelRoot(userProfile, AssistantRuntimeProfile.Resolve());
    }

    private static string ResolveModelRoot(string userProfile, AssistantRuntimeProfile profile)
    {
        var stackEnvironment = Path.Combine(profile.StackDataRoot, "stack.env");
        if (!File.Exists(stackEnvironment))
            stackEnvironment = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GO-AI-Stack", "stack.env");
        IEnumerable<string> values = File.Exists(stackEnvironment) ? File.ReadLines(stackEnvironment) : [];
        var configured = values.FirstOrDefault(line => line.StartsWith("GO_AI_NATIVE_MODEL_ROOT=", StringComparison.Ordinal))
            ?? values.FirstOrDefault(line => line.StartsWith("GO_AI_CODING_MODEL_ROOT=", StringComparison.Ordinal));
        var modelRoot = configured?[(configured.IndexOf('=') + 1)..].Trim();
        return string.IsNullOrWhiteSpace(modelRoot) ? Path.Combine(userProfile, ".cache", "huggingface", "hub") : Path.GetFullPath(modelRoot);
    }

    public void Dispose() => _gate.Dispose();
}
