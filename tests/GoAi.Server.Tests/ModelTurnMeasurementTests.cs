using GoAi.Server.Core.Models;
using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace GoAi.Server.Tests;

public sealed class ModelTurnMeasurementTests
{
    [Fact]
    public void MissingUsageAndReasoningStayUnknown()
    {
        var measurement = new ModelTurnMeasurement(Stopwatch.StartNew());
        measurement.Observe(JsonSerializer.SerializeToElement(new { prompt_progress = new { processed = 15 } }));
        Assert.Null(measurement.Build().InputTokens);
        Assert.Null(measurement.Build().ReasoningTokens);
        Assert.Null(measurement.Build().TimeToFirstTokenMilliseconds);
        measurement.ObserveGeneratedFragment();
        Assert.NotNull(measurement.Build().TimeToFirstTokenMilliseconds);
        Assert.Null(measurement.Build().OutputTokens);
    }

    [Fact]
    public void ExplicitZeroReasoningAndNativeTimingsArePreservedAcrossHeartbeats()
    {
        var measurement = new ModelTurnMeasurement(Stopwatch.StartNew());
        measurement.Observe(JsonSerializer.SerializeToElement(new
        {
            usage = new { prompt_tokens = 1000, completion_tokens = 24,
                completion_tokens_details = new { reasoning_tokens = 0 }, prompt_tokens_details = new { cached_tokens = 800 } },
            timings = new { prompt_n = 200, prompt_ms = 120.5, predicted_ms = 900.2 },
        }));
        measurement.Observe(JsonSerializer.SerializeToElement(new { choices = Array.Empty<object>() }));
        var result = measurement.Build();
        Assert.Equal(0, result.ReasoningTokens);
        Assert.Equal(800, result.CachedPromptTokens);
        Assert.Equal(200, result.PromptEvaluatedTokens);
        Assert.Equal(120.5, result.PromptMilliseconds);
        Assert.Equal(900.2, result.GenerationMilliseconds);
        Assert.Equal(24, result.OutputTokens);
    }
}
