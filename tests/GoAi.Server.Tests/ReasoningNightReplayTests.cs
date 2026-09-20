using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using GoAi.Server.Core.Models;

namespace GoAi.Server.Tests;

public sealed class ReasoningNightReplayTests
{
    private static readonly JsonSerializerOptions ReportJson = new() { WriteIndented = true };

    [Fact]
    public async Task OptInOriginalNightStreamStopsBeforeTheHoursLongLoop()
    {
        var sample = Environment.GetEnvironmentVariable("GO_REASONING_NIGHT_SAMPLE");
        if (string.IsNullOrWhiteSpace(sample)) return;
        var samplePath = Path.GetFullPath(sample);
        Assert.True(File.Exists(samplePath), "GO_REASONING_NIGHT_SAMPLE must name the extracted JSONL event sample.");
        var guard = new ReasoningLoopGuard();
        ReasoningLoopDetectedException? failure = null;
        DateTimeOffset? started = null;
        var elapsed = TimeSpan.Zero;
        var processed = 0;
        long eventId = 0;
        string? runId = null;
        using (var reader = File.OpenText(samplePath))
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                using var record = JsonDocument.Parse(line);
                var root = record.RootElement;
                var timestamp = DateTimeOffset.Parse(root.GetProperty("createdAt").GetString()!, CultureInfo.InvariantCulture);
                started ??= timestamp;
                elapsed = timestamp - started.Value;
                eventId = root.GetProperty("id").GetInt64();
                runId ??= root.GetProperty("runId").GetString();
                processed++;
                var delta = root.GetProperty("data").GetProperty("delta").GetString();
                if (string.IsNullOrEmpty(delta)) continue;
                try { guard.ObserveReasoning(delta, elapsed); }
                catch (ReasoningLoopDetectedException exception) { failure = exception; break; }
            }
        }
        Assert.NotNull(failure);
        Assert.Equal("reasoning_repetition", failure.FailureKind);
        Assert.InRange(elapsed.TotalMinutes, 0, 30);
        Assert.True(processed < 22_381);
        var digest = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(samplePath))).ToLowerInvariant();
        var report = new
        {
            source = samplePath, sha256 = digest, runId, eventId, processedEvents = processed,
            elapsedSeconds = elapsed.TotalSeconds, failureKind = failure.FailureKind,
            failure.ReasoningCharacters, failure.ReasoningWords, retryable = false,
            checkedAt = DateTimeOffset.UtcNow,
        };
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(samplePath)!, "reasoning-night-guard-replay.json"),
            JsonSerializer.Serialize(report, ReportJson));
    }
}
