using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace GoAi.Server.Core.Runs;

public sealed class RunWorkChannel
{
    private readonly ConcurrentDictionary<string, byte> _queued = new(StringComparer.Ordinal);
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
    });

    public ValueTask EnqueueAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (!_queued.TryAdd(runId, 0))
        {
            return ValueTask.CompletedTask;
        }

        return WriteAsync(runId, cancellationToken);
    }

    public async IAsyncEnumerable<string> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var runId in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            _queued.TryRemove(runId, out _);
            yield return runId;
        }
    }

    private async ValueTask WriteAsync(string runId, CancellationToken cancellationToken)
    {
        try
        {
            await _channel.Writer.WriteAsync(runId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _queued.TryRemove(runId, out _);
            throw;
        }
    }
}
