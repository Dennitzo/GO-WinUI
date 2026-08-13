using System.Collections.Concurrent;
using System.Threading.Channels;

namespace GoAi.Server.Core.Runs;

public sealed class RunEventNotifier
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<bool>>> _subscriptions = new(StringComparer.Ordinal);

    public Subscription Subscribe(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        var runSubscriptions = _subscriptions.GetOrAdd(runId, static _ => new ConcurrentDictionary<Guid, Channel<bool>>());
        runSubscriptions[id] = channel;
        return new Subscription(this, runId, id, channel.Reader);
    }

    public void Notify(string runId)
    {
        if (_subscriptions.TryGetValue(runId, out var subscriptions))
        {
            foreach (var channel in subscriptions.Values)
            {
                _ = channel.Writer.TryWrite(true);
            }
        }
    }

    private void Unsubscribe(string runId, Guid id)
    {
        if (!_subscriptions.TryGetValue(runId, out var subscriptions))
        {
            return;
        }

        if (subscriptions.TryRemove(id, out var channel))
        {
            channel.Writer.TryComplete();
        }

        if (subscriptions.IsEmpty)
        {
            _ = _subscriptions.TryRemove(new KeyValuePair<string, ConcurrentDictionary<Guid, Channel<bool>>>(runId, subscriptions));
        }
    }

    public sealed class Subscription : IDisposable
    {
        private RunEventNotifier? _owner;
        private readonly string _runId;
        private readonly Guid _id;

        internal Subscription(RunEventNotifier owner, string runId, Guid id, ChannelReader<bool> reader)
        {
            _owner = owner;
            _runId = runId;
            _id = id;
            Reader = reader;
        }

        public ChannelReader<bool> Reader { get; }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Unsubscribe(_runId, _id);
        }
    }
}
