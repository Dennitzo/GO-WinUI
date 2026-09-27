using GoWinUI.App.Services;

namespace GoWinUI.Tests;

public sealed class NativeModelRuntimeServiceTests
{
    [Fact]
    public void RuntimeManagerUsesTheResolvedBinaryStateAndPort()
    {
        var root = Path.Combine(Path.GetTempPath(), "native-profile-" + Guid.NewGuid().ToString("N"));
        var profile = new AssistantRuntimeProfile(
            "stable",
            "AI Assistent",
            Path.Combine(root, "app"),
            new Uri("http://127.0.0.1:8080"),
            8081,
            8082,
            Path.Combine(root, "state"),
            Path.Combine(root, "stack", "bin", "llama-server.exe"),
            Path.Combine(root, "stack"),
            "LocalAssistant.stable");

        var arguments = NativeModelRuntimeService.BuildRuntimeManagerArguments(
            profile,
            "Start",
            Path.Combine(root, "support"),
            Path.Combine(root, "startup.error.txt"),
            Path.Combine(root, "user"));

        Assert.Equal(profile.NativeStateDirectory, ReadOption(arguments, "-StateDirectory"));
        Assert.Equal(profile.NativeBinaryPath, ReadOption(arguments, "-BinaryPath"));
        Assert.Equal("8081", ReadOption(arguments, "-Port"));
        Assert.DoesNotContain("-CudaVisibleDevices", arguments);
    }

    [Fact]
    public void RuntimeUpdaterUsesProductOwnedInstallRootAndResultFile()
    {
        var arguments = NativeModelRuntimeService.BuildRuntimeUpdaterArguments(
            @"C:\app\manage-llama-server.ps1", @"C:\data\llama.cpp", @"C:\state\resolved.path");

        Assert.Equal("Update", ReadOption(arguments, "-Action"));
        Assert.Equal(@"C:\data\llama.cpp", ReadOption(arguments, "-InstallRoot"));
        Assert.Equal(@"C:\state\resolved.path", ReadOption(arguments, "-ResolvedPathFile"));
        Assert.Contains("-SkipFirewall", arguments);
    }

    [Fact]
    public async Task ShutdownStopsOnceAndPreventsRestart()
    {
        var stops = 0;
        var starts = 0;
        using var service = new NativeModelRuntimeService(_ => true, _ => Task.FromResult(true),
            _ => { starts++; return Task.CompletedTask; }, stop: _ => { stops++; return Task.CompletedTask; },
            gatewayIdle: (_, _) => Task.FromResult<bool?>(true));
        var gateway = new Uri("http://localhost:8080");
        await service.EnsureStartedAsync(gateway, CancellationToken.None);
        service.BeginShutdown();
        await Task.WhenAll(service.StopAsync(gateway), service.StopAsync(gateway),
            service.EnsureStartedAsync(gateway, CancellationToken.None));
        Assert.Equal(1, stops);
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task ShutdownWaitsForPendingStart()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = false;
        using var service = new NativeModelRuntimeService(_ => true, _ => Task.FromResult(running), async _ =>
        {
            entered.SetResult();
            await release.Task;
            running = true;
        }, stop: _ => { running = false; return Task.CompletedTask; },
            gatewayIdle: (_, _) => Task.FromResult<bool?>(true));
        var gateway = new Uri("http://localhost:8080");
        var start = service.EnsureStartedAsync(gateway, CancellationToken.None);
        await entered.Task;
        var stop = service.StopAsync(gateway);
        Assert.False(stop.IsCompleted);
        release.SetResult();
        await Task.WhenAll(start, stop);
        await service.EnsureStartedAsync(gateway, CancellationToken.None);
        Assert.False(running);
    }

    [Fact]
    public async Task RemoteGatewayIsNotStopped()
    {
        using var service = new NativeModelRuntimeService(uri => uri.IsLoopback, _ => Task.FromResult(true),
            _ => Task.CompletedTask, stop: _ => throw new InvalidOperationException("Must not stop remote runtime"));
        await service.StopAsync(new Uri("http://external.example:8080"));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080", true)]
    [InlineData("http://localhost:8080", true)]
    [InlineData("http://[::1]:8080", true)]
    [InlineData("http://203.0.113.99:8080", false)]
    [InlineData("https://external.example:8080", false)]
    public void OnlyThisComputerCanAutoStartTheRuntime(string url, bool expected) =>
        Assert.Equal(expected, NativeModelRuntimeService.IsLocalGateway(new Uri(url)));

    [Fact]
    public async Task ConcurrentFirstConnectionsStartOnceAndRecoverAfterRuntimeStops()
    {
        var now = DateTimeOffset.UtcNow;
        var running = false;
        var starts = 0;
        using var service = new NativeModelRuntimeService(_ => true, _ => Task.FromResult(running), async token =>
        {
            Interlocked.Increment(ref starts);
            await Task.Delay(20, token);
            running = true;
        }, () => now);
        var gateway = new Uri("http://127.0.0.1:8080");
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.EnsureStartedAsync(gateway, CancellationToken.None)));
        Assert.Equal(1, starts);
        running = false;
        now = now.AddSeconds(6);
        await service.EnsureStartedAsync(gateway, CancellationToken.None);
        Assert.Equal(2, starts);
    }

    [Fact]
    public async Task ExistingRuntimeAndRemoteGatewayNeverLaunchAnotherProcess()
    {
        var starts = 0;
        var probes = 0;
        using var service = new NativeModelRuntimeService(uri => uri.IsLoopback, _ =>
        {
            probes++;
            return Task.FromResult(true);
        }, _ => { starts++; return Task.CompletedTask; });
        await service.EnsureStartedAsync(new Uri("http://external.example:8080"), CancellationToken.None);
        Assert.Equal(0, probes);
        await service.EnsureStartedAsync(new Uri("http://localhost:8080"), CancellationToken.None);
        Assert.Equal(1, probes);
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task FailedStartRetainsConcreteErrorAndRetriesAfterBackoff()
    {
        var now = DateTimeOffset.UtcNow;
        var starts = 0;
        using var service = new NativeModelRuntimeService(_ => true, _ => Task.FromResult(false), _ =>
        {
            starts++;
            throw new FileNotFoundException("Native llama-server.exe not found: C:/missing/llama-server.exe");
        }, () => now);
        var gateway = new Uri("http://localhost:8080");
        var first = await Assert.ThrowsAsync<FileNotFoundException>(() => service.EnsureStartedAsync(gateway, CancellationToken.None));
        var second = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureStartedAsync(gateway, CancellationToken.None));
        Assert.Equal(first.Message, second.Message);
        Assert.Equal(1, starts);
        now = now.AddSeconds(11);
        await Assert.ThrowsAsync<FileNotFoundException>(() => service.EnsureStartedAsync(gateway, CancellationToken.None));
        Assert.Equal(2, starts);
    }

    [Fact]
    public async Task CancellationDoesNotPoisonTheNextConnection()
    {
        var starts = 0;
        var running = false;
        using var cancellation = new CancellationTokenSource();
        using var service = new NativeModelRuntimeService(_ => true, _ => Task.FromResult(running), token =>
        {
            starts++;
            if (starts == 1)
            {
                cancellation.Cancel();
                throw new OperationCanceledException(token);
            }
            running = true;
            return Task.CompletedTask;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.EnsureStartedAsync(new Uri("http://localhost:8080"), cancellation.Token));
        Assert.Equal(1, starts);
        await service.EnsureStartedAsync(new Uri("http://localhost:8080"), CancellationToken.None);
        Assert.Equal(2, starts);
        Assert.True(running);
    }

    private static string ReadOption(string[] arguments, string option)
    {
        var index = Array.IndexOf(arguments, option);
        Assert.InRange(index, 0, arguments.Length - 2);
        return arguments[index + 1];
    }
}
