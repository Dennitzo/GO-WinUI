using GoAi.Contracts;
using System.Diagnostics;
using System.Globalization;

namespace GoAi.Server.Core.Models;

public sealed class GpuStatusService
{
    private readonly GpuLeaseScheduler _scheduler;

    public GpuStatusService(GpuLeaseScheduler scheduler)
    {
        _scheduler = scheduler;
    }

    public async Task<GpuStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "nvidia-smi.exe",
                Arguments = "--query-gpu=index,name,memory.total,memory.used,utilization.gpu,temperature.gpu --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("nvidia-smi could not be started.");
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"nvidia-smi returned {process.ExitCode}: {error.Trim()}");
            }

            var devices = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(ParseDevice)
                .ToArray();
            return new GpuStatusSnapshot(
                devices.Length > 0,
                _scheduler.QueueLength,
                _scheduler.ActiveLease,
                devices,
                DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new GpuStatusSnapshot(
                false,
                _scheduler.QueueLength,
                _scheduler.ActiveLease,
                [],
                DateTimeOffset.UtcNow,
                "gpu.nvidia_smi_failed");
        }
    }

    private static GpuDeviceStatus ParseDevice(string line)
    {
        var fields = line.Split(',', StringSplitOptions.TrimEntries);
        if (fields.Length != 6)
        {
            throw new FormatException("Unexpected nvidia-smi output.");
        }

        return new GpuDeviceStatus(
            int.Parse(fields[0], CultureInfo.InvariantCulture),
            fields[1],
            long.Parse(fields[2], CultureInfo.InvariantCulture),
            long.Parse(fields[3], CultureInfo.InvariantCulture),
            int.Parse(fields[4], CultureInfo.InvariantCulture),
            int.Parse(fields[5], CultureInfo.InvariantCulture));
    }
}
