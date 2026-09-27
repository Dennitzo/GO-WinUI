using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using GoWinUI.Core.Extensions;
using Microsoft.Win32.SafeHandles;

namespace GoWinUI.App.Services.Extensions;

public sealed record ExtensionHostLaunchOptions(
    string ExecutablePath,
    ExtensionHostIdentity Identity,
    TimeSpan HandshakeTimeout,
    string? ContentDirectory = null,
    ExtensionEntrypointDescriptor? Entrypoint = null,
    ExtensionPermissionKind[]? Permissions = null,
    string? WorkspaceRoot = null,
    TimeSpan? ActionTimeout = null,
    int MaximumOutputBytes = 1024 * 1024,
    long MaximumProcessMemoryBytes = 512L * 1024 * 1024,
    long MaximumJobMemoryBytes = 1024L * 1024 * 1024,
    int MaximumDescendantProcesses = 16);

public interface IExtensionHostSupervisor
{
    Task<ExtensionHostConnection> StartAsync(
        ExtensionHostLaunchOptions options,
        CancellationToken cancellationToken = default);
}

public sealed class ExtensionHostSupervisor : IExtensionHostSupervisor
{
    private static readonly Encoding ProtocolEncoding = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public async Task<ExtensionHostConnection> StartAsync(
        ExtensionHostLaunchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        var executable = Path.GetFullPath(options.ExecutablePath);
        if (!File.Exists(executable)) throw new FileNotFoundException("ExtensionHost wurde nicht gefunden.", executable);

        var startInfo = CreateStartInfo(executable, options);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        WindowsExtensionProcessJob? processJob = null;
        var processStarted = false;
        try
        {
            if (!process.Start()) throw new InvalidOperationException("ExtensionHost konnte nicht gestartet werden.");
            processStarted = true;
            // Assign the trusted host before sending the handshake. ExtensionHost cannot start an
            // entrypoint until the handshake completes, so every extension process is born inside
            // this non-breakaway job and inherits its limits.
            processJob = WindowsExtensionProcessJob.CreateAndAssign(process, options);
            var errorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            var requestId = Guid.NewGuid().ToString("N");
            var challenge = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var hello = ExtensionHostProtocol.CreateHello(options.Identity, requestId, challenge);
            await process.StandardInput.WriteLineAsync(ExtensionHostProtocol.SerializeLine(hello)).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.HandshakeTimeout);
            string? responseLine;
            try
            {
                responseLine = await ReadBoundedLineAsync(
                    process.StandardOutput, ExtensionHostProtocol.MaximumLineLength, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("ExtensionHost-Handshake hat das Zeitlimit überschritten.");
            }
            if (responseLine is null)
            {
                var error = await errorTask.ConfigureAwait(false);
                throw new InvalidOperationException("ExtensionHost wurde vor dem Handshake beendet: " + error.Trim());
            }
            var response = ExtensionHostProtocol.ParseLine(responseLine);
            _ = ExtensionHostProtocol.ValidateReady(response, options.Identity, requestId, challenge);
            return new ExtensionHostConnection(process, options.Identity, errorTask, processJob);
        }
        catch
        {
            processJob?.Dispose();
            if (processStarted && !process.HasExited) process.Kill(entireProcessTree: true);
            process.Dispose();
            throw;
        }
    }

    internal static async Task<string?> ReadBoundedLineAsync(
        TextReader reader,
        int maximumLength,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLength);
        var builder = new StringBuilder(Math.Min(maximumLength, 4_096));
        var buffer = new char[1];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) return builder.Length == 0 ? null : builder.ToString();
            if (buffer[0] == '\n') return builder.ToString().TrimEnd('\r');
            if (builder.Length >= maximumLength)
                throw new InvalidDataException("ExtensionHost-Nachricht überschreitet das Größenlimit.");
            builder.Append(buffer[0]);
        }
    }

    private static ProcessStartInfo CreateStartInfo(string executable, ExtensionHostLaunchOptions options)
    {
        var isAssembly = string.Equals(Path.GetExtension(executable), ".dll", StringComparison.OrdinalIgnoreCase);
        var info = new ProcessStartInfo
        {
            FileName = isAssembly ? "dotnet" : executable,
            WorkingDirectory = Path.GetDirectoryName(executable)
                ?? throw new InvalidOperationException("ExtensionHost-Arbeitsverzeichnis fehlt."),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = ProtocolEncoding,
            StandardOutputEncoding = ProtocolEncoding,
            StandardErrorEncoding = ProtocolEncoding,
        };
        if (isAssembly) info.ArgumentList.Add(executable);
        info.ArgumentList.Add("--extension-id");
        info.ArgumentList.Add(options.Identity.ExtensionId);
        info.ArgumentList.Add("--package-sha256");
        info.ArgumentList.Add(options.Identity.PackageSha256);
        if (options.ContentDirectory is not null && options.Entrypoint is not null)
        {
            info.ArgumentList.Add("--content-root");
            info.ArgumentList.Add(Path.GetFullPath(options.ContentDirectory));
            info.ArgumentList.Add("--entrypoint");
            info.ArgumentList.Add(options.Entrypoint.Path);
            info.ArgumentList.Add("--entrypoint-kind");
            info.ArgumentList.Add(options.Entrypoint.Kind.ToString());
            info.ArgumentList.Add("--permissions");
            info.ArgumentList.Add(string.Join(',', options.Permissions ?? []));
            if (!string.IsNullOrWhiteSpace(options.WorkspaceRoot))
            {
                info.ArgumentList.Add("--workspace-root");
                info.ArgumentList.Add(Path.GetFullPath(options.WorkspaceRoot));
            }
            info.ArgumentList.Add("--action-timeout-ms");
            info.ArgumentList.Add(((long)(options.ActionTimeout ?? TimeSpan.FromSeconds(60)).TotalMilliseconds)
                .ToString(System.Globalization.CultureInfo.InvariantCulture));
            info.ArgumentList.Add("--maximum-output-bytes");
            info.ArgumentList.Add(options.MaximumOutputBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return info;
    }

    private static void ValidateOptions(ExtensionHostLaunchOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ExecutablePath);
        ArgumentNullException.ThrowIfNull(options.Identity);
        if (!ExtensionIdentifiers.IsValidExtensionId(options.Identity.ExtensionId))
            throw new ArgumentException("ExtensionHost-Extension-ID ist ungültig.", nameof(options));
        if (!ExtensionHashes.IsSha256(options.Identity.PackageSha256))
            throw new ArgumentException("ExtensionHost-Paket-Hash ist ungültig.", nameof(options));
        if (options.HandshakeTimeout <= TimeSpan.Zero || options.HandshakeTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(options), "HandshakeTimeout muss zwischen null und zwei Minuten liegen.");
        if ((options.ContentDirectory is null) != (options.Entrypoint is null))
            throw new ArgumentException("ContentDirectory und Entrypoint müssen gemeinsam angegeben werden.", nameof(options));
        if (options.ContentDirectory is not null && options.Entrypoint is not null)
        {
            var contentRoot = Path.GetFullPath(options.ContentDirectory);
            if (!Directory.Exists(contentRoot)) throw new DirectoryNotFoundException("Extension-Inhaltsverzeichnis fehlt.");
            if (!ExtensionPathRules.TryNormalizeRelativePath(options.Entrypoint.Path, out var relative, out var error)
                || relative.EndsWith('/')) throw new ArgumentException(error, nameof(options));
            var entrypoint = ExtensionPathRules.ResolveInside(contentRoot, relative);
            if (!File.Exists(entrypoint)) throw new FileNotFoundException("Extension-Entrypoint fehlt.", entrypoint);
            var timeout = options.ActionTimeout ?? TimeSpan.FromSeconds(60);
            if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromHours(1))
                throw new ArgumentOutOfRangeException(nameof(options), "ActionTimeout muss zwischen null und einer Stunde liegen.");
            if (options.MaximumOutputBytes is < 1_024 or > 64 * 1024 * 1024)
                throw new ArgumentOutOfRangeException(nameof(options), "MaximumOutputBytes liegt außerhalb der erlaubten Grenzen.");
            if ((options.Permissions ?? []).Distinct().Count() != (options.Permissions ?? []).Length)
                throw new ArgumentException("Extension-Berechtigungen enthalten Duplikate.", nameof(options));
        }
        _ = ExtensionHostJobPolicy.Create(options);
    }
}

public sealed class ExtensionHostConnection : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _errorTask;
    private readonly WindowsExtensionProcessJob _processJob;
    private readonly SemaphoreSlim _exchangeGate = new(1, 1);
    private int _disposed;

    internal ExtensionHostConnection(
        Process process,
        ExtensionHostIdentity identity,
        Task<string> errorTask,
        WindowsExtensionProcessJob processJob)
    {
        _process = process;
        Identity = identity;
        _errorTask = errorTask;
        _processJob = processJob;
    }

    public ExtensionHostIdentity Identity { get; }
    public int ProcessId => _process.Id;
    internal ExtensionHostJobPolicy JobPolicy => _processJob.Policy;

    public async Task<ExtensionHostMessage> ExchangeAsync(
        ExtensionHostMessage request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _exchangeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.WriteLineAsync(ExtensionHostProtocol.SerializeLine(request)).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            var line = await ExtensionHostSupervisor.ReadBoundedLineAsync(
                _process.StandardOutput, ExtensionHostProtocol.MaximumLineLength, cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                var error = await _errorTask.ConfigureAwait(false);
                throw new InvalidOperationException("ExtensionHost wurde unerwartet beendet: " + error.Trim());
            }
            var response = ExtensionHostProtocol.ParseLine(line);
            if (!string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal))
                throw new InvalidDataException("ExtensionHost-Antwort gehört zu einer anderen Anfrage.");
            return response;
        }
        finally { _exchangeGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (!_process.HasExited)
            {
                var request = ExtensionHostProtocol.Create(
                    ExtensionHostMessageTypes.Shutdown, Guid.NewGuid().ToString("N"), new { reason = "host-dispose" });
                await _process.StandardInput.WriteLineAsync(ExtensionHostProtocol.SerializeLine(request)).ConfigureAwait(false);
                await _process.StandardInput.FlushAsync().ConfigureAwait(false);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { _process.Kill(entireProcessTree: true); }
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        finally
        {
            // Closing the last job handle is the authoritative cleanup path. It terminates the
            // host, the active entrypoint and every permitted descendant even if graceful
            // shutdown or Process.Kill could not observe the complete process tree.
            _processJob.Dispose();
            _exchangeGate.Dispose();
            _process.Dispose();
        }
    }
}

[Flags]
internal enum ExtensionHostJobLimitFlags : uint
{
    DieOnUnhandledException = 0x00000400,
    ActiveProcess = 0x00000008,
    ProcessMemory = 0x00000100,
    JobMemory = 0x00000200,
    KillOnJobClose = 0x00002000,
}

internal sealed record ExtensionHostJobPolicy(
    uint ActiveProcessLimit,
    long MaximumProcessMemoryBytes,
    long MaximumJobMemoryBytes,
    ExtensionHostJobLimitFlags LimitFlags)
{
    private const long Mebibyte = 1024L * 1024;

    internal bool KillsOnClose => LimitFlags.HasFlag(ExtensionHostJobLimitFlags.KillOnJobClose);

    internal static bool AllowsBreakaway => false;

    internal static ExtensionHostJobPolicy Create(ExtensionHostLaunchOptions options)
    {
        if (options.MaximumProcessMemoryBytes is < 128L * Mebibyte or > 8L * 1024 * Mebibyte)
            throw new ArgumentOutOfRangeException(nameof(options), "MaximumProcessMemoryBytes muss zwischen 128 MiB und 8 GiB liegen.");
        if (options.MaximumJobMemoryBytes < options.MaximumProcessMemoryBytes
            || options.MaximumJobMemoryBytes > 32L * 1024 * Mebibyte)
            throw new ArgumentOutOfRangeException(nameof(options), "MaximumJobMemoryBytes muss mindestens dem Prozesslimit entsprechen und darf 32 GiB nicht überschreiten.");
        if (options.MaximumDescendantProcesses is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(options), "MaximumDescendantProcesses muss zwischen 1 und 64 liegen.");

        var processPermission = (options.Permissions ?? []).Contains(ExtensionPermissionKind.Process);
        // The host itself and one entrypoint consume two slots. Without Process permission this
        // exact limit makes CreateProcess fail inside the entrypoint. With permission, descendants
        // remain bounded and cannot break away from the job.
        var activeProcessLimit = processPermission
            ? checked((uint)(2 + options.MaximumDescendantProcesses))
            : 2u;
        const ExtensionHostJobLimitFlags limits = ExtensionHostJobLimitFlags.DieOnUnhandledException
            | ExtensionHostJobLimitFlags.ActiveProcess
            | ExtensionHostJobLimitFlags.ProcessMemory
            | ExtensionHostJobLimitFlags.JobMemory
            | ExtensionHostJobLimitFlags.KillOnJobClose;
        return new(activeProcessLimit, options.MaximumProcessMemoryBytes, options.MaximumJobMemoryBytes, limits);
    }
}

internal sealed partial class WindowsExtensionProcessJob : IDisposable
{
    private readonly SafeFileHandle _handle;
    private int _disposed;

    private WindowsExtensionProcessJob(SafeFileHandle handle, ExtensionHostJobPolicy policy)
    {
        _handle = handle;
        Policy = policy;
    }

    internal ExtensionHostJobPolicy Policy { get; }

    internal static WindowsExtensionProcessJob CreateAndAssign(
        Process process,
        ExtensionHostLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("ExtensionHost-Prozessgrenzen benötigen Windows-Jobobjekte.");

        var policy = ExtensionHostJobPolicy.Create(options);
        var handle = NativeMethods.CreateJobObject(0, null);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error, "Das Windows-Jobobjekt für ExtensionHost konnte nicht erstellt werden.");
        }

        try
        {
            var limits = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = (uint)policy.LimitFlags,
                    ActiveProcessLimit = policy.ActiveProcessLimit,
                },
                ProcessMemoryLimit = checked((nuint)policy.MaximumProcessMemoryBytes),
                JobMemoryLimit = checked((nuint)policy.MaximumJobMemoryBytes),
            };
            if (NativeMethods.SetInformationJobObject(
                    handle,
                    JobObjectInformationClass.ExtendedLimitInformation,
                    ref limits,
                    (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Die ExtensionHost-Jobgrenzen konnten nicht gesetzt werden.");
            if (NativeMethods.AssignProcessToJobObject(handle, process.SafeHandle) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "ExtensionHost konnte nicht seiner Windows-Jobgrenze zugeordnet werden.");
            return new(handle, policy);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _handle.Dispose();
    }

    private enum JobObjectInformationClass
    {
        ExtendedLimitInformation = 9,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal nuint MinimumWorkingSetSize;
        internal nuint MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal nuint Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        internal JobObjectBasicLimitInformation BasicLimitInformation;
        internal IoCounters IoInfo;
        internal nuint ProcessMemoryLimit;
        internal nuint JobMemoryLimit;
        internal nuint PeakProcessMemoryUsed;
        internal nuint PeakJobMemoryUsed;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static partial SafeFileHandle CreateJobObject(nint jobAttributes, string? name);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static partial int SetInformationJobObject(
            SafeFileHandle job,
            JobObjectInformationClass informationClass,
            ref JobObjectExtendedLimitInformation information,
            uint informationLength);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static partial int AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
    }
}
