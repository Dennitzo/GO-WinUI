using GoAi.Server.Core.Models;
using System.Diagnostics;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class SessionCacheIdentityTests
{
    [Fact]
    public void StableScopeIgnoresWindowsWorkspaceSpellingAndDoesNotExposePrivatePaths()
    {
        var first = ModelRuntimeClient.BuildSessionCacheKey("session", "coding", "C:\\Users\\AMD\\Project\\");
        Assert.Equal(first, ModelRuntimeClient.BuildSessionCacheKey("session", "CODING", "c:/users/amd/project"));
        Assert.DoesNotContain("AMD", first);
        Assert.NotEqual(first, ModelRuntimeClient.BuildSessionCacheKey("other-session", "coding", "C:/Users/AMD/Project"));
        Assert.NotEqual(first, ModelRuntimeClient.BuildSessionCacheKey("session", "general", "C:/Users/AMD/Project"));
        Assert.NotEqual(first, ModelRuntimeClient.BuildSessionCacheKey("session", "coding", "C:/Other"));
    }

    [Fact]
    public void UntrustedSeparatorsCannotMakeDistinctScopesCollide()
    {
        Assert.NotEqual(ModelRuntimeClient.BuildSessionCacheKey("one\ntwo", "coding", "three"),
            ModelRuntimeClient.BuildSessionCacheKey("one", "coding", "two\nthree"));
    }

    [Fact]
    public void ProgressCacheMeasurementSurvivesHeartbeatButIsOverriddenByFinalUsage()
    {
        var measurement = new ModelTurnMeasurement(Stopwatch.StartNew());
        measurement.Observe(JsonSerializer.SerializeToElement(new { prompt_progress = new { total = 1000, cache = 800, processed = 900 } }));
        measurement.Observe(JsonSerializer.SerializeToElement(new { choices = Array.Empty<object>() }));
        Assert.Equal(800, measurement.Build().CachedPromptTokens);
        Assert.Equal(1000, measurement.Build().InputTokens);
        Assert.Null(measurement.Build().PromptEvaluatedTokens);
        measurement.Observe(JsonSerializer.SerializeToElement(new { usage = new { prompt_tokens_details = new { cached_tokens = 0 } } }));
        Assert.Equal(0, measurement.Build().CachedPromptTokens);
    }

    [Fact]
    public void CacheRequestedOrSnapshotRestoredNeverInventsTokenMeasurement()
    {
        var measurement = new ModelTurnMeasurement(Stopwatch.StartNew());
        measurement.Observe(JsonSerializer.SerializeToElement(new { cache_prompt = true, status = "restored", restoredTokens = 1000 }));
        Assert.Null(measurement.Build().CachedPromptTokens);
    }
}
