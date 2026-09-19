using System.Threading.Channels;
using GoWinUI.Core.Models;

namespace GoWinUI.App.Services;

internal sealed class SpeechStreamingSession : IDisposable
{
    private readonly object _gate = new();
    private readonly SpeechStreamingTextBuffer _buffer;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        AllowSynchronousContinuations = false,
    });
    private readonly CancellationTokenSource _cancellation;
    private readonly CancellationToken _token;
    private bool _finished;

    public SpeechStreamingSession(ChatMessage initialMessage, Func<string, CancellationToken, Task> play,
        CancellationToken cancellationToken = default)
        : this(initialMessage, play, null, null, cancellationToken) { }

    public SpeechStreamingSession(ChatMessage initialMessage, Func<string, CancellationToken, Task> play,
        Func<CancellationToken, Task>? waitForResume, IDisposable? playbackLifetime = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initialMessage);
        ArgumentNullException.ThrowIfNull(play);
        MessageId = initialMessage.Id;
        SessionId = initialMessage.SessionId;
        _buffer = new(initialMessage.Content);
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _token = _cancellation.Token;
        Completion = PlayQueuedAsync(play, waitForResume, playbackLifetime);
    }

    public Guid MessageId { get; }
    public Guid SessionId { get; }
    public Task Completion { get; }
    public Exception? Failure { get; private set; }
    public bool HasPlayed { get; private set; }
    public bool WasCancelled => _token.IsCancellationRequested;

    public void Observe(GoAiAssistantUpdate update)
    {
        if (update.Message.Id != MessageId || update.Message.SessionId != SessionId) return;
        if (update.Kind is GoAiAssistantUpdateKind.Cancelled or GoAiAssistantUpdateKind.Failed)
        {
            Cancel();
            return;
        }
        var flushSentence = update.Kind == GoAiAssistantUpdateKind.Status
            && update.ToolStep is { AgentId: null, Tool: not GoAiAssistantService.ReasoningStepTool };
        if (update.Kind is not (GoAiAssistantUpdateKind.Delta or GoAiAssistantUpdateKind.Completed) && !flushSentence) return;
        lock (_gate)
        {
            if (_finished || _token.IsCancellationRequested) return;
            var complete = update.Kind == GoAiAssistantUpdateKind.Completed;
            foreach (var text in _buffer.Take(update.Message.Content, complete, flushSentence)) _queue.Writer.TryWrite(text);
            if (_buffer.HasConflictingRevision)
            {
                Cancel();
                return;
            }
            if (complete)
            {
                _finished = true;
                _queue.Writer.TryComplete();
            }
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _finished = true;
            try { _cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
            _queue.Writer.TryComplete();
        }
    }

    private async Task PlayQueuedAsync(Func<string, CancellationToken, Task> play,
        Func<CancellationToken, Task>? waitForResume, IDisposable? playbackLifetime)
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(_token).ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out var text))
                {
                    _token.ThrowIfCancellationRequested();
                    if (waitForResume is not null) await waitForResume(_token).ConfigureAwait(false);
                    _token.ThrowIfCancellationRequested();
                    HasPlayed = true;
                    await play(text, _token).ConfigureAwait(false);
                    _token.ThrowIfCancellationRequested();
                }
            }
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Failure = exception;
            Cancel();
        }
        finally
        {
            playbackLifetime?.Dispose();
            _cancellation.Dispose();
        }
    }

    public void Dispose() => Cancel();
}
