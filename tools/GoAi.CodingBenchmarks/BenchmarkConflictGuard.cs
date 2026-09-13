using System.Diagnostics;
using System.Text.Json;

namespace GoAi.CodingBenchmarks;

/// <summary>Read-only observation; only this benchmark's linked token is cancelled.</summary>
internal sealed class BenchmarkConflictGuard : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HttpClient _http = new() { BaseAddress = new Uri("http://127.0.0.1:8080/"), Timeout = TimeSpan.FromSeconds(5) };
    private Task _monitor = Task.CompletedTask;

    internal static async Task<BenchmarkConflictGuard> StartAsync(string directory, Uri runtime, CancellationTokenSource benchmarkStop)
    {
        var guard = new BenchmarkConflictGuard();
        try
        {
            await guard.CheckUserGatewayAsync(benchmarkStop.Token).ConfigureAwait(false);
            await CheckNativeSlotsAsync(runtime, benchmarkStop.Token).ConfigureAwait(false);
            guard._monitor = guard.MonitorAsync(directory, benchmarkStop);
            return guard;
        }
        catch { await guard.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private async Task MonitorAsync(string directory, CancellationTokenSource benchmarkStop)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
                await CheckUserGatewayAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            try { await BenchmarkStorage.SaveAsync(Path.Combine(directory, "interruption.json"), new { reason = exception.Message, at = DateTimeOffset.UtcNow }).ConfigureAwait(false); }
            finally { await benchmarkStop.CancelAsync().ConfigureAwait(false); }
        }
    }

    private async Task CheckUserGatewayAsync(CancellationToken token)
    {
        var processes = Process.GetProcessesByName("GO");
        try { if (processes.Length > 0) throw new InvalidOperationException("GO is running; suspend this isolated benchmark before the user can create another AI run."); }
        finally { foreach (var process in processes) process.Dispose(); }
        using var response = await _http.GetAsync("v1/gpu/status", token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
        var root = document.RootElement;
        if (root.TryGetProperty("activeLease", out var active) && active.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(active.GetString())
            || root.TryGetProperty("activeWorkloads", out var workloads) && workloads.ValueKind == JsonValueKind.Array && workloads.GetArrayLength() > 0
            || root.TryGetProperty("queueLength", out var queue) && queue.ValueKind == JsonValueKind.Number && queue.GetInt32() > 0)
            throw new InvalidOperationException("The user's gateway reports active or queued GPU work. Only the benchmark is being suspended; user runs remain untouched.");
    }

    private static async Task CheckNativeSlotsAsync(Uri runtime, CancellationToken token)
    {
        using var http = new HttpClient { BaseAddress = runtime, Timeout = TimeSpan.FromSeconds(5) };
        using var inventory = JsonDocument.Parse(await http.GetStringAsync("v1/models", token).ConfigureAwait(false));
        foreach (var model in inventory.RootElement.GetProperty("data").EnumerateArray())
        {
            if (!model.TryGetProperty("status", out var status) || !status.TryGetProperty("value", out var state) || state.GetString() != "loaded") continue;
            var id = model.GetProperty("id").GetString()!;
            using var response = await http.GetAsync("slots?model=" + Uri.EscapeDataString(id), token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var slots = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            if (slots.RootElement.EnumerateArray().Any(static slot => slot.TryGetProperty("is_processing", out var active) && active.ValueKind == JsonValueKind.True))
                throw new InvalidOperationException("A native llama slot is currently processing another request. No benchmark run was started.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _monitor.ConfigureAwait(false);
        _http.Dispose();
        _lifetime.Dispose();
    }
}
