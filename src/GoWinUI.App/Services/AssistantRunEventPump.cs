using GoAi.Contracts;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace GoWinUI.App.Services;

/// <summary>One ordered UI consumer; network reception and local execution remain independent.</summary>
internal sealed class AssistantRunEventPump : IAsyncDisposable
{
    internal sealed record Signal(RunEvent? Event = null, Func<Task>? Apply = null, Exception? Error = null);
    private readonly Channel<Signal> _channel = Channel.CreateBounded<Signal>(new BoundedChannelOptions(256)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _stop;
    private readonly Dictionary<string, Task> _workers = new(StringComparer.Ordinal);
    private readonly Task _receiver;
    private int _disposed;

    public AssistantRunEventPump(Func<long, CancellationToken, IAsyncEnumerable<RunEvent>> source,
        long cursor, CancellationToken cancellationToken)
    {
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _receiver = ReceiveAsync(source, cursor);
    }

    public CancellationToken Token => _stop.Token;
    public void Stop() => _stop.Cancel();

    public async IAsyncEnumerable<Signal> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var signal in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return signal;
    }

    public ValueTask PostAsync(Func<Task> apply) => _channel.Writer.WriteAsync(new(Apply: apply), Token);

    // Only the consumer calls Start. A durable journal claim must be awaited by the caller before acknowledging the event.
    public bool Start(string id, Func<CancellationToken, Task> work)
    {
        foreach (var completed in _workers.Where(static item => item.Value.IsCompleted).Select(static item => item.Key).ToArray())
            _workers.Remove(completed);
        if (_workers.ContainsKey(id)) return false;
        _workers.Add(id, ExecuteAsync(work));
        return true;
    }

    private async Task ExecuteAsync(Func<CancellationToken, Task> work)
    {
        try { await work(Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (Token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            try { await _channel.Writer.WriteAsync(new(Error: exception), Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (Token.IsCancellationRequested) { }
        }
    }

    private async Task ReceiveAsync(Func<long, CancellationToken, IAsyncEnumerable<RunEvent>> source, long cursor)
    {
        var attempts = 0;
        try
        {
            while (!Token.IsCancellationRequested)
            {
                try
                {
                    await foreach (var item in source(cursor, Token).WithCancellation(Token).ConfigureAwait(false))
                    {
                        if (item.Id <= cursor) continue;
                        await _channel.Writer.WriteAsync(new(Event: item), Token).ConfigureAwait(false);
                        cursor = item.Id;
                        attempts = 0;
                        if (item.Type is RunEventTypes.RunCompleted or RunEventTypes.RunFailed or RunEventTypes.RunCancelled) return;
                    }
                }
                catch (Exception exception) when (!Token.IsCancellationRequested && exception is IOException or HttpRequestException or OperationCanceledException) { }
                // A disconnected event stream does not cancel an executing command or recreate its journal claim.
                await Task.Delay(GoAiAssistantService.StreamReconnectDelay(attempts++), Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (Token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await _channel.Writer.WriteAsync(new(Error: exception), Token).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stop.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(_workers.Values.Append(_receiver)).ConfigureAwait(false);
        _channel.Writer.TryComplete();
        _stop.Dispose();
    }
}
