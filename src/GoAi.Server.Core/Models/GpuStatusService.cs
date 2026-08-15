using GoAi.Contracts;
using System.Diagnostics;
using System.Globalization;

namespace GoAi.Server.Core.Models;

public sealed class GpuStatusService
{
    private readonly GpuLeaseScheduler _scheduler;
    private readonly ServiceActivityTracker _services;

    public GpuStatusService(GpuLeaseScheduler scheduler, ServiceActivityTracker services)
    {
        _scheduler = scheduler;
        _services = services;
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
                DateTimeOffset.UtcNow,
                ActiveWorkloads: DescribeActiveWorkloads(ActiveActivities()));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new GpuStatusSnapshot(
                false,
                _scheduler.QueueLength,
                _scheduler.ActiveLease,
                [],
                DateTimeOffset.UtcNow,
                "gpu.nvidia_smi_failed",
                DescribeActiveWorkloads(ActiveActivities()));
        }
    }

    private GpuLeaseActivity[] ActiveActivities() => _scheduler.ActiveActivities
        .Concat(_services.ActiveActivities)
        .OrderBy(static activity => activity.StartedAt)
        .ToArray();

    internal static IReadOnlyList<ActiveAiWorkload> DescribeActiveWorkloads(
        IReadOnlyList<GpuLeaseActivity> activities) =>
        activities.Select(static activity =>
        {
            var (displayName, runtime) = DescribeWorkload(activity.Workload);
            return new ActiveAiWorkload(
                activity.LeaseId,
                activity.Workload,
                displayName,
                runtime,
                activity.RunId,
                activity.StartedAt);
        }).ToArray();

    internal static (string DisplayName, string Runtime) DescribeWorkload(string workload) => workload switch
    {
        "llm-general" => ("gpt-oss-20b", "LM Studio"),
        "llm-code" => ("Laguna-S-2.1", "LM Studio"),
        "speech-to-text" => ("Audio wird transkribiert", "Docker · Whisper STT"),
        "live-caption" => ("Sprache wird live transkribiert", "Docker · Whisper STT"),
        "live-caption-warmup" => ("Sprachmodell wird vorbereitet", "Docker · Whisper STT"),
        "caption-translation" => ("Live-Untertitel werden übersetzt", "LM Studio · gpt-oss-20b"),
        "text-to-speech" => ("Antwort wird vorgelesen", "Docker · Piper MLS weiblich"),
        "image-generation" => ("Bild wird erstellt", "Docker · Image"),
        "media-analysis" => ("Medien werden analysiert", "Docker · Media"),
        "vision" => ("Bild wird analysiert", "LM Studio · Vision"),
        "audio-analysis" => ("Audio wird analysiert", "Docker + LM Studio"),
        "video-audio-fusion" => ("Video und Audio werden zusammengeführt", "LM Studio · gpt-oss-20b"),
        "embedding" => ("Kontext wird indiziert", "LM Studio · Embeddings"),
        "web-search" => ("Websuche wird ausgeführt", "Docker · SearXNG"),
        "youtube-search" => ("YouTube wird durchsucht", "YouTube API / SearXNG"),
        "web-fetch" => ("Webquelle wird geladen", "GO AI Server"),
        _ => (workload, "GO AI Server"),
    };

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

public sealed class ServiceActivityTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<string, GpuLeaseActivity> _active = new(StringComparer.Ordinal);

    public IReadOnlyList<GpuLeaseActivity> ActiveActivities
    {
        get
        {
            lock (_gate)
            {
                return _active.Values.OrderBy(static activity => activity.StartedAt).ToArray();
            }
        }
    }

    public IDisposable Begin(string workload, string? runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workload);
        var activity = new GpuLeaseActivity(
            $"service-{Guid.NewGuid():N}",
            workload,
            runId,
            GpuLeaseMode.Shared,
            DateTimeOffset.UtcNow);
        lock (_gate) _active[activity.LeaseId] = activity;
        return new ActivityScope(this, activity.LeaseId);
    }

    private void End(string id)
    {
        lock (_gate) _ = _active.Remove(id);
    }

    private sealed class ActivityScope(ServiceActivityTracker owner, string id) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.End(id);
        }
    }
}
