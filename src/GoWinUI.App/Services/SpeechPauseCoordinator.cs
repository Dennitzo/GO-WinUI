namespace GoWinUI.App.Services;

/// <summary>One pause state for preparation, queued text and the current audio output.</summary>
internal sealed class SpeechPauseCoordinator
{
    private readonly object _gate = new();
    private int _sessions;
    private TaskCompletionSource? _resume;

    internal bool IsActive { get { lock (_gate) return _sessions > 0; } }
    internal bool IsPaused { get { lock (_gate) return _resume is not null; } }

    internal IDisposable Begin(bool resetPause = false)
    {
        lock (_gate)
        {
            if (resetPause) Resume();
            _sessions++;
        }
        return new Session(this);
    }

    internal bool Toggle()
    {
        lock (_gate)
        {
            if (_sessions == 0) return false;
            if (_resume is null) _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
            else Resume();
            return _resume is not null;
        }
    }

    internal void Resume()
    {
        lock (_gate)
        {
            var pending = _resume;
            _resume = null;
            pending?.TrySetResult();
        }
    }

    internal async Task WaitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task? wait;
            lock (_gate) wait = _resume?.Task;
            if (wait is null) return;
            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void End()
    {
        lock (_gate)
        {
            if (--_sessions == 0) Resume();
        }
    }

    private sealed class Session(SpeechPauseCoordinator owner) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.End();
        }
    }
}
