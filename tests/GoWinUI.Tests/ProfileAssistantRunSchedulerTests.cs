using GoWinUI.App.Services;
using GoWinUI.Core.Chat;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GoWinUI.Tests;

public sealed class ProfileAssistantRunSchedulerTests
{
    [Fact]
    public async Task DifferentSessionsRunStrictlyInFifoOrderWithoutOverlap()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var scheduler = CreateScheduler();
        var releases = Enumerable.Range(0, 3)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var starts = Enumerable.Range(0, 3)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var order = new List<int>();
        var active = 0;
        var maximumActive = 0;

        var tickets = Enumerable.Range(0, 3).Select(index => scheduler.Enqueue(
            Guid.NewGuid(),
            "request-" + index,
            async token =>
            {
                var current = Interlocked.Increment(ref active);
                maximumActive = Math.Max(maximumActive, current);
                lock (order) order.Add(index);
                starts[index].TrySetResult();
                try
                {
                    await releases[index].Task.WaitAsync(token);
                    return index;
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            },
            timeout.Token)).ToArray();

        await starts[0].Task.WaitAsync(timeout.Token);
        Assert.Equal(2, scheduler.Snapshot.QueueDepth);
        Assert.False(starts[1].Task.IsCompleted);
        releases[0].SetResult();
        await starts[1].Task.WaitAsync(timeout.Token);
        Assert.False(starts[2].Task.IsCompleted);
        releases[1].SetResult();
        await starts[2].Task.WaitAsync(timeout.Token);
        releases[2].SetResult();

        var results = await Task.WhenAll(tickets.Select(ticket => ticket.Completion)).WaitAsync(timeout.Token);
        Assert.Equal([0, 1, 2], results);
        Assert.Equal([0, 1, 2], order);
        Assert.Equal(1, maximumActive);
        Assert.True(scheduler.Snapshot.IsIdle);
    }

    [Fact]
    public async Task ActiveSessionIsExposedForSteeringWhileAnotherSessionRemainsQueued()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var scheduler = CreateScheduler();
        var firstSession = Guid.NewGuid();
        var secondSession = Guid.NewGuid();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = scheduler.Enqueue(firstSession, "first", async token =>
        {
            firstStarted.SetResult();
            await release.Task.WaitAsync(token);
            return 1;
        });
        await firstStarted.Task.WaitAsync(timeout.Token);
        var second = scheduler.Enqueue(secondSession, "second", _ => Task.FromResult(2));

        Assert.True(scheduler.IsActiveSession(firstSession));
        Assert.False(scheduler.IsActiveSession(secondSession));
        Assert.Equal(firstSession, scheduler.ActiveSessionId);
        Assert.Equal(secondSession, Assert.Single(scheduler.Snapshot.Pending).SessionId);
        release.SetResult();
        Assert.Equal(1, await first.Completion.WaitAsync(timeout.Token));
        Assert.Equal(2, await second.Completion.WaitAsync(timeout.Token));
    }

    [Fact]
    public async Task CancellingAQueuedRunNeverInvokesItAndRecalculatesPositions()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var scheduler = CreateScheduler();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = scheduler.Enqueue(Guid.NewGuid(), "first", async token =>
        {
            started.SetResult();
            await release.Task.WaitAsync(token);
            return true;
        });
        await started.Task.WaitAsync(timeout.Token);
        var invoked = false;
        var cancelled = scheduler.Enqueue(Guid.NewGuid(), "cancelled", _ =>
        {
            invoked = true;
            return Task.FromResult(false);
        });
        var last = scheduler.Enqueue(Guid.NewGuid(), "last", _ => Task.FromResult(true));

        Assert.True(scheduler.TryCancel(cancelled.TicketId));
        Assert.False(scheduler.TryCancel(cancelled.TicketId));
        var pending = Assert.Single(scheduler.Snapshot.Pending);
        Assert.Equal(last.TicketId, pending.TicketId);
        Assert.Equal(1, pending.Position);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.Completion);
        Assert.False(invoked);

        release.SetResult();
        Assert.True(await first.Completion.WaitAsync(timeout.Token));
        Assert.True(await last.Completion.WaitAsync(timeout.Token));
    }

    [Fact]
    public async Task CancellingTheActiveRunReleasesTheNextSession()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var scheduler = CreateScheduler();
        var activeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = scheduler.Enqueue(Guid.NewGuid(), "active", async token =>
        {
            activeStarted.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return false;
            }
            finally
            {
                cancellationObserved.SetResult();
            }
        });
        await activeStarted.Task.WaitAsync(timeout.Token);
        var next = scheduler.Enqueue(Guid.NewGuid(), "next", _ =>
        {
            nextStarted.SetResult();
            return Task.FromResult(true);
        });

        Assert.True(scheduler.TryCancel(active.TicketId));
        await cancellationObserved.Task.WaitAsync(timeout.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active.Completion);
        await nextStarted.Task.WaitAsync(timeout.Token);
        Assert.True(await next.Completion.WaitAsync(timeout.Token));
    }

    [Fact]
    public async Task FailuresAreReportedAndDoNotStopLaterRuns()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var scheduler = CreateScheduler();
        var changes = new List<AssistantRunScheduleChangedEventArgs>();
        var successPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.Changed += (_, change) =>
        {
            lock (changes) changes.Add(change);
            if (change.Kind == AssistantRunScheduleChangeKind.Completed && change.Run.RequestId == "good")
                successPublished.TrySetResult();
        };
        var failure = scheduler.Enqueue<int>(Guid.NewGuid(), "bad", _ => throw new InvalidOperationException("kaputt"));
        var success = scheduler.Enqueue(Guid.NewGuid(), "good", _ => Task.FromResult(42));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => failure.Completion);
        Assert.Equal("kaputt", exception.Message);
        Assert.Equal(42, await success.Completion.WaitAsync(timeout.Token));
        await successPublished.Task.WaitAsync(timeout.Token);
        lock (changes)
        {
            Assert.Contains(changes, change => change.Kind == AssistantRunScheduleChangeKind.Failed && change.Error == "kaputt");
            Assert.Contains(changes, change => change.Kind == AssistantRunScheduleChangeKind.Completed && change.Run.RequestId == "good");
        }
    }

    [Fact]
    public async Task DuplicateLiveRequestIsRejectedButCanBeUsedAgainAfterCompletion()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var scheduler = CreateScheduler();
        var session = Guid.NewGuid();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = scheduler.Enqueue(session, "same", async token =>
        {
            started.SetResult();
            await release.Task.WaitAsync(token);
            return 1;
        });
        await started.Task.WaitAsync(timeout.Token);

        Assert.Throws<InvalidOperationException>(() => scheduler.Enqueue(session, "same", _ => Task.FromResult(2)));
        release.SetResult();
        Assert.Equal(1, await first.Completion.WaitAsync(timeout.Token));
        var repeated = scheduler.Enqueue(session, "same", _ => Task.FromResult(3));
        Assert.Equal(3, await repeated.Completion.WaitAsync(timeout.Token));
    }

    private static ProfileAssistantRunScheduler CreateScheduler() =>
        new(NullLogger<ProfileAssistantRunScheduler>.Instance);
}
