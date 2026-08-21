using GoAi.Server.Core.Runs;

namespace GoAi.Server.Tests;

public sealed class RunWorkChannelTests
{
    [Fact]
    public async Task DuplicatePendingRunIsQueuedOnlyOnce()
    {
        var queue = new RunWorkChannel();
        await queue.EnqueueAsync("run-1");
        await queue.EnqueueAsync("run-1");

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await using var reader = queue.ReadAllAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal("run-1", reader.Current);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reader.MoveNextAsync().AsTask());
    }

    [Fact]
    public async Task RunCanBeQueuedAgainAfterItWasDequeued()
    {
        var queue = new RunWorkChannel();
        await queue.EnqueueAsync("run-1");

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var reader = queue.ReadAllAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        Assert.True(await reader.MoveNextAsync());
        await queue.EnqueueAsync("run-1", cancellation.Token);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal("run-1", reader.Current);
    }
}
