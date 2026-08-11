using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using Microsoft.Extensions.Logging;

namespace GoWinUI.Infrastructure.Logging;

public sealed class RingBufferLoggerProvider : ILoggerProvider, ISessionLog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly HashSet<string> SensitiveNames = new(StringComparer.OrdinalIgnoreCase)
        { "prompt", "content", "document", "token", "authorization", "apikey", "api_key" };
    private readonly ConcurrentQueue<SessionLogEntry> _entries = new();
    private readonly int _capacity;
    private long _sequence;

    public RingBufferLoggerProvider(GoInfrastructureOptions options) => _capacity = Math.Clamp(options.LogCapacity, 100, 100_000);
    public event EventHandler<SessionLogEntry>? EntryAdded;

    public ILogger CreateLogger(string categoryName) => new RingLogger(this, categoryName);
    public void Dispose() => _entries.Clear();
    public void Clear() => _entries.Clear();

    public IReadOnlyList<SessionLogEntry> Snapshot(string? minimumLevel = null, string? category = null, string? search = null)
    {
        var minimum = Enum.TryParse<LogLevel>(minimumLevel, true, out var parsed) ? parsed : LogLevel.Trace;
        return _entries.Where(entry => Enum.TryParse<LogLevel>(entry.Level, true, out var level) && level >= minimum)
            .Where(entry => string.IsNullOrWhiteSpace(category) || entry.Category.Contains(category, StringComparison.OrdinalIgnoreCase))
            .Where(entry => string.IsNullOrWhiteSpace(search) || entry.Message.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (entry.Exception?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToArray();
    }

    public async Task ExportAsync(Stream destination, bool asJson, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var snapshot = Snapshot();
        if (asJson)
        {
            await JsonSerializer.SerializeAsync(destination, snapshot, JsonOptions, cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var writer = new StreamWriter(destination, new UTF8Encoding(false), 16 * 1024, leaveOpen: true);
        foreach (var entry in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(FormattableString.Invariant($"{entry.Timestamp:O} [{entry.Level}] {entry.Category} ({entry.EventId}): {entry.Message}"))
                .ConfigureAwait(false);
            if (entry.Exception is not null) await writer.WriteLineAsync(entry.Exception).ConfigureAwait(false);
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Add(LogLevel level, string category, EventId eventId, string message, Exception? exception, IReadOnlyDictionary<string, string?> properties)
    {
        var entry = new SessionLogEntry(
            Interlocked.Increment(ref _sequence), DateTimeOffset.UtcNow, level.ToString(), category, message,
            eventId.Id, exception is null ? null : FormattableString.Invariant($"{exception.GetType().Name} (0x{exception.HResult:X8})"), properties);
        _entries.Enqueue(entry);
        while (_entries.Count > _capacity) _entries.TryDequeue(out _);
        EntryAdded?.Invoke(this, entry);
    }

    private sealed class RingLogger(RingBufferLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var properties = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var containsSensitiveValue = false;
            string? template = null;
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var (key, value) in values)
                {
                    if (key == "{OriginalFormat}") { template = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture); continue; }
                    var sensitive = SensitiveNames.Any(name => key.Contains(name, StringComparison.OrdinalIgnoreCase));
                    properties[key] = sensitive ? "[ausgelassen]" : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
                    containsSensitiveValue |= sensitive;
                }
            }

            var message = containsSensitiveValue ? $"{template ?? "Protokollereignis"} [sensible Werte ausgelassen]" : formatter(state, exception);
            provider.Add(logLevel, category, eventId, message, exception, properties);
        }
    }
}
