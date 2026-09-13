using GoAi.Contracts;
using GoWinUI.App.Services;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Xunit;

namespace GoWinUI.Tests;

public sealed class AssistantRunEventPumpTests
{
    [Fact]
    public async Task SlowToolDoesNotBlockEventsAndCompletionIsAppliedByConsumer()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = false;
        await using var pump = new AssistantRunEventPump(Source, 0, timeout.Token);
        Assert.True(pump.Start("command", async token =>
        {
            await release.Task.WaitAsync(token);
            await pump.PostAsync(() => { applied = true; return Task.CompletedTask; });
        }));
        Assert.False(pump.Start("command", _ => throw new InvalidOperationException("duplicate")));
        await using var reader = pump.ReadAllAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(RunEventTypes.ModelGeneration, reader.Current.Event?.Type);
        Assert.False(applied);
        release.SetResult();
        Assert.True(await reader.MoveNextAsync());
        Assert.False(applied);
        await reader.Current.Apply!();
        Assert.True(applied);
    }

    [Fact]
    public async Task ReconnectKeepsToolAliveAndSkipsReplayedEvents()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connections = 0;
        var cancelled = false;
        async IAsyncEnumerable<RunEvent> Disconnecting(long cursor, [EnumeratorCancellation] CancellationToken token)
        {
            var attempt = Interlocked.Increment(ref connections);
            yield return Event(1, RunEventTypes.ModelGeneration);
            if (attempt == 1) throw new IOException("disconnect");
            Assert.Equal(1, cursor);
            yield return Event(2, RunEventTypes.ModelGeneration);
            await Task.Delay(Timeout.Infinite, token);
        }
        await using var pump = new AssistantRunEventPump(Disconnecting, 0, timeout.Token);
        pump.Start("command", async token =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { cancelled = true; }
        });
        await using var reader = pump.ReadAllAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(1, reader.Current.Event?.Id);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(2, reader.Current.Event?.Id);
        Assert.False(cancelled);
        await pump.DisposeAsync();
        Assert.True(cancelled);
    }

    [Fact]
    public async Task StopCancelsEveryActiveWorkerAndWaitsForCleanup()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stopped = 0;
        await using var pump = new AssistantRunEventPump(Source, 0, timeout.Token);
        for (var i = 0; i < 3; i++)
            pump.Start("worker-" + i, async token =>
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { Interlocked.Increment(ref stopped); }
            });
        await pump.DisposeAsync();
        Assert.Equal(3, stopped);
    }

    [Fact]
    public async Task TerminalStopCancelsCommandBeforeSlowUiFinalizationOrDisposal()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pump = new AssistantRunEventPump(Source, 0, timeout.Token);
        pump.Start("running-command", async token =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { stopped.TrySetResult(); }
        });

        // The UI consumer stops the pump immediately on a terminal server event.
        // It can then await database/UI work without leaving a process running.
        pump.Stop();
        await stopped.Task.WaitAsync(timeout.Token);
        Assert.True(pump.Token.IsCancellationRequested);
    }

    private static async IAsyncEnumerable<RunEvent> Source(long cursor, [EnumeratorCancellation] CancellationToken token)
    {
        yield return Event(cursor + 1, RunEventTypes.ModelGeneration);
        await Task.Delay(Timeout.Infinite, token);
    }

    private static RunEvent Event(long id, string type) => new(id, "run-test", type, DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(new { }));
}
