using GoWinUI.Core.Chat;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;

namespace GoWinUI.App.Services;

/// <summary>
/// Owns the single model execution lane of the local assistant.
/// </summary>
public sealed class ProfileAssistantRunScheduler : IAssistantRunScheduler
{
    private static readonly Action<ILogger, Guid, Guid, Exception?> ScheduledRunFailed = LoggerMessage.Define<Guid, Guid>(
        LogLevel.Error,
        new EventId(5700, nameof(ScheduledRunFailed)),
        "Assistant run {TicketId} for session {SessionId} failed; the profile queue will continue.");
    private static readonly Action<ILogger, Exception?> QueueObserverFailed = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(5701, nameof(QueueObserverFailed)),
        "An assistant run queue observer failed.");
    private readonly object _sync = new();
    private readonly Queue<ScheduledWork> _pending = new();
    private readonly Dictionary<Guid, ScheduledWork> _liveByTicket = [];
    private readonly HashSet<(Guid SessionId, string RequestId)> _liveRequestIds = [];
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ILogger<ProfileAssistantRunScheduler> _logger;
    private readonly Task _worker;
    private ScheduledWork? _active;
    private bool _accepting = true;
    private int _disposed;

    public ProfileAssistantRunScheduler(ILogger<ProfileAssistantRunScheduler> logger)
    {
        _logger = logger;
        _worker = ProcessAsync();
    }

    public event EventHandler<AssistantRunScheduleChangedEventArgs>? Changed;

    public AssistantRunScheduleSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return BuildSnapshot();
            }
        }
    }

    public Guid? ActiveSessionId
    {
        get
        {
            lock (_sync)
            {
                return _active?.SessionId;
            }
        }
    }

    public bool IsActiveSession(Guid sessionId)
    {
        if (sessionId == Guid.Empty) return false;
        lock (_sync)
        {
            return _active?.SessionId == sessionId;
        }
    }

    public AssistantRunTicket<TResult> Enqueue<TResult>(
        Guid sessionId,
        string requestId,
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (sessionId == Guid.Empty) throw new ArgumentException("Eine gültige Sitzungs-ID ist erforderlich.", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(requestId)) throw new ArgumentException("Eine Request-ID ist erforderlich.", nameof(requestId));
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedRequestId = requestId.Trim();
        ScheduledWork<TResult> item;
        AssistantRunScheduleChangedEventArgs enqueued;
        int position;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(!_accepting, this);
            var requestKey = (sessionId, normalizedRequestId);
            if (!_liveRequestIds.Add(requestKey))
            {
                throw new InvalidOperationException("Dieser Sitzungsauftrag ist bereits aktiv oder eingereiht.");
            }

            item = new(
                Guid.NewGuid(),
                sessionId,
                normalizedRequestId,
                DateTimeOffset.UtcNow,
                operation);
            _pending.Enqueue(item);
            _liveByTicket.Add(item.TicketId, item);
            var snapshot = BuildSnapshot();
            position = snapshot.Pending.Single(run => run.TicketId == item.TicketId).Position;
            enqueued = new(
                AssistantRunScheduleChangeKind.Enqueued,
                item.Describe(position),
                snapshot);
        }

        // Enqueued must always be observable before Started or Cancelled.
        Publish(enqueued);
        item.RegisterCancellation(() => TryCancel(item.TicketId), cancellationToken);
        _signal.Release();
        return new(
            item.TicketId,
            item.SessionId,
            item.RequestId,
            position,
            item.EnqueuedAt,
            item.Completion);
    }

    public bool TryCancel(Guid ticketId)
    {
        ScheduledWork? item;
        AssistantRunScheduleChangedEventArgs? changed = null;
        var cancelRunning = false;
        lock (_sync)
        {
            if (!_liveByTicket.TryGetValue(ticketId, out item)) return false;
            if (item.State == AssistantRunScheduleState.Queued)
            {
                item.State = AssistantRunScheduleState.Cancelled;
                RemoveLive(item);
                item.SetCancelled();
                changed = new(
                    AssistantRunScheduleChangeKind.Cancelled,
                    item.Describe(-1),
                    BuildSnapshot());
            }
            else if (ReferenceEquals(_active, item) && item.State == AssistantRunScheduleState.Running)
            {
                item.State = AssistantRunScheduleState.Cancelling;
                cancelRunning = true;
                changed = new(
                    AssistantRunScheduleChangeKind.CancellationRequested,
                    item.Describe(0),
                    BuildSnapshot());
            }
            else
            {
                return false;
            }
        }

        Publish(changed);
        if (cancelRunning) item.RequestCancellation();
        return true;
    }

    private async Task ProcessAsync()
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                ScheduledWork? item = null;
                List<ScheduledWork>? discarded = null;
                AssistantRunScheduleChangedEventArgs? started = null;
                lock (_sync)
                {
                    while (_pending.TryDequeue(out var candidate))
                    {
                        if (candidate.State != AssistantRunScheduleState.Queued)
                        {
                            (discarded ??= []).Add(candidate);
                            continue;
                        }
                        item = candidate;
                        break;
                    }

                    if (item is not null)
                    {
                        item.State = AssistantRunScheduleState.Running;
                        item.StartedAt = DateTimeOffset.UtcNow;
                        _active = item;
                        started = new(
                            AssistantRunScheduleChangeKind.Started,
                            item.Describe(0),
                            BuildSnapshot());
                    }
                }

                if (discarded is not null)
                {
                    foreach (var removed in discarded) removed.Dispose();
                }
                if (item is null) continue;
                Publish(started!);
                await ExecuteAsync(item).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A failed scheduled run must complete its ticket and must never stop later queued sessions.")]
    private async Task ExecuteAsync(ScheduledWork item)
    {
        AssistantRunScheduleChangeKind terminalKind;
        Exception? failure = null;
        try
        {
            await item.InvokeAsync(_shutdown.Token).ConfigureAwait(false);
            terminalKind = AssistantRunScheduleChangeKind.Completed;
        }
        catch (OperationCanceledException) when (item.IsCancellationRequested || _shutdown.IsCancellationRequested)
        {
            terminalKind = AssistantRunScheduleChangeKind.Cancelled;
        }
        catch (Exception exception)
        {
            failure = exception;
            terminalKind = AssistantRunScheduleChangeKind.Failed;
        }

        AssistantRunScheduleChangedEventArgs terminal;
        lock (_sync)
        {
            _active = null;
            switch (terminalKind)
            {
                case AssistantRunScheduleChangeKind.Completed:
                    item.State = AssistantRunScheduleState.Completed;
                    item.SetSucceeded();
                    break;
                case AssistantRunScheduleChangeKind.Cancelled:
                    item.State = AssistantRunScheduleState.Cancelled;
                    item.SetCancelled();
                    break;
                default:
                    item.State = AssistantRunScheduleState.Failed;
                    item.SetFailed(failure!);
                    break;
            }

            RemoveLive(item);
            terminal = new(
                terminalKind,
                item.Describe(-1),
                BuildSnapshot(),
                failure?.Message);
        }

        if (failure is not null)
        {
            ScheduledRunFailed(_logger, item.TicketId, item.SessionId, failure);
        }
        Publish(terminal);
        item.Dispose();
    }

    private void RemoveLive(ScheduledWork item)
    {
        _liveByTicket.Remove(item.TicketId);
        _liveRequestIds.Remove((item.SessionId, item.RequestId));
    }

    private AssistantRunScheduleSnapshot BuildSnapshot()
    {
        var active = _active?.Describe(0);
        var position = 0;
        var pending = _pending
            .Where(static item => item.State == AssistantRunScheduleState.Queued)
            .Select(item => item.Describe(++position))
            .ToArray();
        return new(active, pending);
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "One UI observer must not corrupt the serial execution queue or suppress other observers.")]
    private void Publish(AssistantRunScheduleChangedEventArgs changed)
    {
        var handlers = Changed;
        if (handlers is null) return;
        foreach (EventHandler<AssistantRunScheduleChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, changed);
            }
            catch (Exception exception)
            {
                QueueObserverFailed(_logger, exception);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        List<(ScheduledWork Item, AssistantRunScheduleChangedEventArgs Changed)> cancelled = [];
        ScheduledWork? active;
        lock (_sync)
        {
            _accepting = false;
            active = _active;
            foreach (var item in _pending.Where(static candidate => candidate.State == AssistantRunScheduleState.Queued))
            {
                item.State = AssistantRunScheduleState.Cancelled;
                RemoveLive(item);
                item.SetCancelled();
                cancelled.Add((item, new(
                    AssistantRunScheduleChangeKind.Cancelled,
                    item.Describe(-1),
                    BuildSnapshot())));
            }

            if (active?.State == AssistantRunScheduleState.Running)
            {
                active.State = AssistantRunScheduleState.Cancelling;
            }
        }

        foreach (var (item, changed) in cancelled)
        {
            Publish(changed);
            item.Dispose();
        }

        active?.RequestCancellation();
        await _shutdown.CancelAsync().ConfigureAwait(false);
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        _signal.Dispose();
        _shutdown.Dispose();
    }

    private abstract class ScheduledWork : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private CancellationTokenRegistration _registration;
        private int _disposed;

        protected ScheduledWork(Guid ticketId, Guid sessionId, string requestId, DateTimeOffset enqueuedAt)
        {
            TicketId = ticketId;
            SessionId = sessionId;
            RequestId = requestId;
            EnqueuedAt = enqueuedAt;
        }

        public Guid TicketId { get; }

        public Guid SessionId { get; }

        public string RequestId { get; }

        public DateTimeOffset EnqueuedAt { get; }

        public DateTimeOffset? StartedAt { get; set; }

        public AssistantRunScheduleState State { get; set; } = AssistantRunScheduleState.Queued;

        public bool IsCancellationRequested => _cancellation.IsCancellationRequested;

        public void RegisterCancellation(Action cancel, CancellationToken cancellationToken) =>
            _registration = cancellationToken.Register(cancel);

        public void RequestCancellation() => _cancellation.Cancel();

        public async Task InvokeAsync(CancellationToken shutdownToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token, shutdownToken);
            await InvokeCoreAsync(linked.Token).ConfigureAwait(false);
        }

        public AssistantScheduledRun Describe(int position) => new(
            TicketId,
            SessionId,
            RequestId,
            State,
            position,
            EnqueuedAt,
            StartedAt);

        protected abstract Task InvokeCoreAsync(CancellationToken cancellationToken);

        public abstract void SetSucceeded();

        public abstract void SetCancelled();

        public abstract void SetFailed(Exception exception);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _registration.Dispose();
            _cancellation.Dispose();
        }
    }

    private sealed class ScheduledWork<TResult>(
        Guid ticketId,
        Guid sessionId,
        string requestId,
        DateTimeOffset enqueuedAt,
        Func<CancellationToken, Task<TResult>> operation)
        : ScheduledWork(ticketId, sessionId, requestId, enqueuedAt)
    {
        private readonly TaskCompletionSource<TResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TResult? _result;

        public Task<TResult> Completion => _completion.Task;

        protected override async Task InvokeCoreAsync(CancellationToken cancellationToken) =>
            _result = await operation(cancellationToken).ConfigureAwait(false);

        public override void SetSucceeded() => _completion.TrySetResult(_result!);

        public override void SetCancelled() => _completion.TrySetCanceled();

        public override void SetFailed(Exception exception) => _completion.TrySetException(exception);
    }
}
