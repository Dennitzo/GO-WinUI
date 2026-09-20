using GoAi.Contracts;
using GoWinUI.App.Services;
using System.Text.Json;

namespace GoWinUI.Tests;

public sealed class NativeRuntimeShutdownDrainTests
{
    private static readonly Uri Gateway = new("http://localhost:8080");

    [Fact]
    public async Task ShutdownWaitsForTheSnapshotWorkloadToDrainBeforeStopping()
    {
        var waitingForSnapshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshotSaved = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = 0;
        var stops = 0;
        using var service = Create(_ => { stops++; return Task.CompletedTask; }, async (_, token) =>
        {
            if (Interlocked.Increment(ref checks) == 1) return false;
            waitingForSnapshot.TrySetResult();
            return await snapshotSaved.Task.WaitAsync(token);
        });
        var shutdown = service.StopAsync(Gateway);
        await waitingForSnapshot.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(shutdown.IsCompleted);
        Assert.Equal(0, stops);
        snapshotSaved.SetResult(true);
        await shutdown;
        Assert.Equal(1, stops);
        Assert.Equal(2, checks);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task UnknownOrPersistentlyBusyGatewayLeavesSharedRuntimeAlive(bool? idle)
    {
        var stops = 0;
        using var service = Create(_ => { stops++; return Task.CompletedTask; },
            (_, _) => Task.FromResult(idle), TimeSpan.FromMilliseconds(30));
        await service.StopAsync(Gateway).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, stops);
    }

    [Fact]
    public async Task AlreadyIdleGatewayStopsNormallyOnlyOnce()
    {
        var stops = 0;
        var checks = 0;
        using var service = Create(_ => { stops++; return Task.CompletedTask; }, (_, _) =>
        {
            checks++;
            return Task.FromResult<bool?>(true);
        });
        await service.StopAsync(Gateway);
        await service.StopAsync(Gateway);
        Assert.Equal(1, stops);
        Assert.Equal(1, checks);
    }

    [Fact]
    public async Task UnresponsiveProbeCannotHangShutdownOrStopRuntime()
    {
        var pending = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stops = 0;
        using var service = Create(_ => { stops++; return Task.CompletedTask; },
            (_, _) => pending.Task, TimeSpan.FromMilliseconds(30));
        await service.StopAsync(Gateway).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, stops);
    }

    [Fact]
    public async Task UnreachableGatewayNeverAuthorizesRuntimeStop()
    {
        var stops = 0;
        using var service = Create(_ => { stops++; return Task.CompletedTask; },
            (_, _) => throw new HttpRequestException("Gateway is restarting"));
        await service.StopAsync(Gateway);
        Assert.Equal(0, stops);
    }

    [Fact]
    public void ActualProtocolSerializerConfirmsIdleWithOmittedNullLease()
    {
        var snapshot = new GpuStatusSnapshot(true, 0, null, [], DateTimeOffset.UtcNow, ActiveWorkloads: []);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(snapshot, GoAiProtocol.CreateJsonOptions()));
        Assert.False(json.RootElement.TryGetProperty("activeLease", out _));
        Assert.Equal(true, NativeModelRuntimeService.ReadGatewayIdle(json.RootElement));
    }

    [Theory]
    [InlineData("{\"queueLength\":0,\"activeWorkloads\":[],\"activeLease\":null}", true)]
    [InlineData("{\"queueLength\":1,\"activeWorkloads\":[]}", false)]
    [InlineData("{\"queueLength\":0,\"activeWorkloads\":[{}]}", false)]
    [InlineData("{\"queueLength\":0,\"activeWorkloads\":[],\"activeLease\":\"llm-coding\"}", false)]
    [InlineData("{\"queueLength\":0,\"activeWorkloads\":[],\"activeLease\":\"\"}", false)]
    [InlineData("{\"queueLength\":0,\"activeWorkloads\":[],\"activeLease\":{}}", null)]
    [InlineData("{\"queueLength\":0,\"activeWorkloads\":[],\"activeLease\":false}", null)]
    [InlineData("{\"queueLength\":0,\"activeLease\":null}", null)]
    [InlineData("{\"activeWorkloads\":[],\"activeLease\":null}", null)]
    [InlineData("{\"queueLength\":0,\"activeWorkloads\":null}", null)]
    [InlineData("{\"queueLength\":-1,\"activeWorkloads\":[]}", null)]
    [InlineData("null", null)]
    public void IncompleteOrBusyGpuStatusNeverConfirmsIdle(string payload, bool? expected)
    {
        using var json = JsonDocument.Parse(payload);
        Assert.Equal(expected, NativeModelRuntimeService.ReadGatewayIdle(json.RootElement));
    }

    private static NativeModelRuntimeService Create(Func<CancellationToken, Task> stop,
        Func<Uri, CancellationToken, Task<bool?>> idle, TimeSpan? timeout = null) =>
        new(_ => true, _ => Task.FromResult(true), _ => Task.CompletedTask, stop: stop,
            gatewayIdle: idle, shutdownDrainTimeout: timeout ?? TimeSpan.FromSeconds(5));
}
