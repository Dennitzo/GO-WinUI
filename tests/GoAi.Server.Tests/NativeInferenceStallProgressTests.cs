using GoAi.Server.Core.Models;

namespace GoAi.Server.Tests;

public sealed class NativeInferenceStallProgressTests
{
    [Fact]
    public void OnlyIncreasingNativeWorkAndActualTextRenewIdleDeadline()
    {
        var renewals = 0;
        var tracker = new NativeInferenceStallProgress(() => renewals++);
        tracker.Observe(new ModelRuntimeProgress("generationStarted"));
        tracker.Observe(new ModelRuntimeProgress("codingWaiting"));
        tracker.Observe(new ModelRuntimeProgress("promptProcessing", ProcessedPromptTokens: 0));
        Assert.Equal(0, renewals);
        tracker.Observe(new ModelRuntimeProgress("promptProcessing", ProcessedPromptTokens: 500));
        tracker.Observe(new ModelRuntimeProgress("promptProcessing", ProcessedPromptTokens: 500));
        tracker.Observe(new ModelRuntimeProgress("tokenProgress", GeneratedTokens: 8));
        tracker.Observe(new ModelRuntimeProgress("tokenProgress", GeneratedTokens: 8));
        tracker.Observe(new ModelRuntimeProgress("tokenProgress", GeneratedTokens: 16));
        tracker.Observe(new ModelRuntimeProgress("contentDelta", ContentDelta: "visible text"));
        Assert.Equal(4, renewals);
        tracker.Observe(new ModelRuntimeProgress("generationRetry"));
        Assert.Equal(4, renewals);
        tracker.Observe(new ModelRuntimeProgress("promptProcessing", ProcessedPromptTokens: 100));
        tracker.Observe(new ModelRuntimeProgress("tokenProgress", GeneratedTokens: 1));
        Assert.Equal(6, renewals);
    }
}
