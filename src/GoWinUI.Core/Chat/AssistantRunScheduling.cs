namespace GoWinUI.Core.Chat;

/// <summary>
/// Describes the lifecycle of one model run inside the current runtime profile.
/// Exactly one scheduled run may be <see cref="Running"/> or <see cref="Cancelling"/> at a time.
/// </summary>
public enum AssistantRunScheduleState
{
    Queued,
    Running,
    Cancelling,
    Completed,
    Cancelled,
    Failed,
}

public enum AssistantRunScheduleChangeKind
{
    Enqueued,
    Started,
    CancellationRequested,
    Completed,
    Cancelled,
    Failed,
}

/// <param name="Position">
/// Zero for the active run; otherwise the current one-based position among waiting runs.
/// </param>
public sealed record AssistantScheduledRun(
    Guid TicketId,
    Guid SessionId,
    string RequestId,
    AssistantRunScheduleState State,
    int Position,
    DateTimeOffset EnqueuedAt,
    DateTimeOffset? StartedAt = null);

public sealed record AssistantRunScheduleSnapshot(
    AssistantScheduledRun? Active,
    IReadOnlyList<AssistantScheduledRun> Pending)
{
    public int QueueDepth => Pending.Count;

    public bool IsIdle => Active is null && Pending.Count == 0;
}

public sealed class AssistantRunScheduleChangedEventArgs : EventArgs
{
    public AssistantRunScheduleChangedEventArgs(
        AssistantRunScheduleChangeKind kind,
        AssistantScheduledRun run,
        AssistantRunScheduleSnapshot snapshot,
        string? error = null)
    {
        Kind = kind;
        Run = run;
        Snapshot = snapshot;
        Error = error;
    }

    public AssistantRunScheduleChangeKind Kind { get; }

    public AssistantScheduledRun Run { get; }

    public AssistantRunScheduleSnapshot Snapshot { get; }

    public string? Error { get; }
}

public sealed class AssistantRunTicket<TResult>
{
    public AssistantRunTicket(
        Guid ticketId,
        Guid sessionId,
        string requestId,
        int initialPosition,
        DateTimeOffset enqueuedAt,
        Task<TResult> completion)
    {
        TicketId = ticketId;
        SessionId = sessionId;
        RequestId = requestId;
        InitialPosition = initialPosition;
        EnqueuedAt = enqueuedAt;
        Completion = completion ?? throw new ArgumentNullException(nameof(completion));
    }

    public Guid TicketId { get; }

    public Guid SessionId { get; }

    public string RequestId { get; }

    public int InitialPosition { get; }

    public DateTimeOffset EnqueuedAt { get; }

    public Task<TResult> Completion { get; }
}

/// <summary>
/// Serializes model work for the local assistant. The service is intended to be
/// registered as a singleton. Callers should handle an input for <see cref="ActiveSessionId"/>
/// as steering; inputs for other sessions can be enqueued immediately.
/// </summary>
public interface IAssistantRunScheduler : IAsyncDisposable
{
    event EventHandler<AssistantRunScheduleChangedEventArgs>? Changed;

    AssistantRunScheduleSnapshot Snapshot { get; }

    Guid? ActiveSessionId { get; }

    bool IsActiveSession(Guid sessionId);

    AssistantRunTicket<TResult> Enqueue<TResult>(
        Guid sessionId,
        string requestId,
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default);

    bool TryCancel(Guid ticketId);
}
