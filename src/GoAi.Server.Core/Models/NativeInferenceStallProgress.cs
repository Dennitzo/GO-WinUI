namespace GoAi.Server.Core.Models;

// Heartbeats and unchanged counters cannot keep a stalled inference alive.
// Real prompt/generation progress renews the Coding idle deadline independently
// of whether the caller subscribes to visible progress events.
internal sealed class NativeInferenceStallProgress(Action renewDeadline)
{
    private int _promptTokens;
    private int _generatedTokens;

    internal void Observe(ModelRuntimeProgress progress)
    {
        if (progress.State == "generationRetry")
        {
            _promptTokens = 0;
            _generatedTokens = 0;
            return;
        }
        var prompt = progress.ProcessedPromptTokens.GetValueOrDefault();
        var generated = progress.GeneratedTokens.GetValueOrDefault();
        if (progress.State is "promptProcessing" or "tokenProgress"
            && (prompt > _promptTokens || generated > _generatedTokens)
            || progress.State == "contentDelta" && !string.IsNullOrEmpty(progress.ContentDelta)
            || progress.State == "reasoningDelta" && !string.IsNullOrEmpty(progress.ReasoningDelta))
            renewDeadline();
        _promptTokens = Math.Max(_promptTokens, prompt);
        _generatedTokens = Math.Max(_generatedTokens, generated);
    }
}
