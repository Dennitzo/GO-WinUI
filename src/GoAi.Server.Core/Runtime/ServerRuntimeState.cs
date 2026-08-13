using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace GoAi.Server.Core.Runtime;

public sealed class ServerRuntimeState
{
    private const int MaximumLogEntries = 2000;
    private const long MaximumLogFileBytes = 10L * 1024 * 1024;
    private static readonly JsonSerializerOptions LogJsonOptions = GoAiProtocol.CreateJsonOptions();
    private readonly ConcurrentQueue<ServerLogEntry> _logs = new();
    private readonly object _fileGate = new();
    private readonly string? _logFilePath;
    private string _gatewayState = "Startet";
    private string _readinessReason = "Initialisierung läuft";
    private string? _oneTimeBootstrapKey;
    private int _logCount;

    public ServerRuntimeState()
        : this(null)
    {
    }

    public ServerRuntimeState(IOptions<GoAiServerOptions>? options)
    {
        StartedAt = DateTimeOffset.UtcNow;
        _logFilePath = options is null
            ? null
            : Path.Combine(options.Value.LogDirectory, "server-events.jsonl");
    }

    public DateTimeOffset StartedAt { get; }

    public string GatewayState => Volatile.Read(ref _gatewayState);

    public string ReadinessReason => Volatile.Read(ref _readinessReason);

    public string? OneTimeBootstrapKey => Volatile.Read(ref _oneTimeBootstrapKey);

    public event EventHandler? Changed;

    public event EventHandler<ServerLogEntry>? LogAdded;

    public IReadOnlyList<ServerLogEntry> GetLogs()
    {
        var persisted = ReadPersistedLogs();
        return persisted.Length > 0 ? persisted : _logs.ToArray();
    }

    public void SetGatewayState(string state, string reason)
    {
        Volatile.Write(ref _gatewayState, state);
        Volatile.Write(ref _readinessReason, reason);
        NotifyChanged();
    }

    public void SetOneTimeBootstrapKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Volatile.Write(ref _oneTimeBootstrapKey, key);
        NotifyChanged();
    }

    public void ClearOneTimeBootstrapKey()
    {
        Volatile.Write(ref _oneTimeBootstrapKey, null);
        NotifyChanged();
    }

    public void WriteLog(string level, string eventId, string message)
    {
        var entry = new ServerLogEntry(DateTimeOffset.Now, level, eventId, message);
        _logs.Enqueue(entry);
        var count = Interlocked.Increment(ref _logCount);
        while (count > MaximumLogEntries && _logs.TryDequeue(out _))
        {
            count = Interlocked.Decrement(ref _logCount);
        }

        TryAppendLog(entry);
        NotifyLogAdded(entry);
    }

    private void NotifyChanged()
    {
        foreach (EventHandler handler in Changed?.GetInvocationList() ?? [])
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Runtime observers are diagnostic only and must never interrupt server work.
            }
        }
    }

    private void NotifyLogAdded(ServerLogEntry entry)
    {
        foreach (EventHandler<ServerLogEntry> handler in LogAdded?.GetInvocationList() ?? [])
        {
            try
            {
                handler(this, entry);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Runtime observers are diagnostic only and must never interrupt server work.
            }
        }
    }

    private void TryAppendLog(ServerLogEntry entry)
    {
        if (string.IsNullOrWhiteSpace(_logFilePath))
        {
            return;
        }

        try
        {
            lock (_fileGate)
            {
                var directory = Path.GetDirectoryName(_logFilePath)!;
                Directory.CreateDirectory(directory);
                RotateLogIfRequired(_logFilePath);
                var line = JsonSerializer.Serialize(entry, LogJsonOptions) + Environment.NewLine;
                var bytes = Encoding.UTF8.GetBytes(line);
                using var stream = new FileStream(
                    _logFilePath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                stream.Write(bytes);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Logging is diagnostic and must never interrupt server work.
        }
    }

    private ServerLogEntry[] ReadPersistedLogs()
    {
        if (string.IsNullOrWhiteSpace(_logFilePath) || !File.Exists(_logFilePath))
        {
            return [];
        }

        try
        {
            lock (_fileGate)
            {
                using var stream = new FileStream(
                    _logFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var entries = new Queue<ServerLogEntry>(MaximumLogEntries);
                while (reader.ReadLine() is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }
                    try
                    {
                        var entry = JsonSerializer.Deserialize<ServerLogEntry>(line, LogJsonOptions);
                        if (entry is not null)
                        {
                            if (entries.Count == MaximumLogEntries)
                            {
                                _ = entries.Dequeue();
                            }
                            entries.Enqueue(entry);
                        }
                    }
                    catch (JsonException)
                    {
                        // Ignore a single incomplete line written during a concurrent read.
                    }
                }
                return entries.ToArray();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void RotateLogIfRequired(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < MaximumLogFileBytes)
        {
            return;
        }

        var archive = path + ".1";
        File.Move(path, archive, overwrite: true);
    }

    public static HealthSnapshot CreateLiveSnapshot() => new(
        "live",
        GoAiProtocol.Version,
        DateTimeOffset.UtcNow);
}
