using GoAi.Contracts;
using GoWinUI.App.Services;

namespace GoWinUI.Tests;

public sealed class ModelTokenProgressTests
{
    [Fact]
    public void NativeFragmentsAdvanceImmediatelyAfterLargePromptProcessing()
    {
        var counter = new GoAiAssistantService.ModelTokenProgressState();
        _ = GoAiAssistantService.FormatModelTokenProgress(new("generationStarted"), counter);
        _ = GoAiAssistantService.FormatModelTokenProgress(new("promptProcessing",
            PromptProgress: 1, PromptTokens: 5_544, ProcessedPromptTokens: 5_544), counter);

        var first = GoAiAssistantService.FormatModelTokenProgress(new("tokenProgress",
            GeneratedTokens: 8, CurrentTokens: 8), counter);
        Assert.Equal($"{5_544:N0} Kontexttoken · ca. 8 erzeugte Token", first);
        Assert.Equal(5_552, counter.ActiveTokens);
        var later = GoAiAssistantService.FormatModelTokenProgress(new("tokenProgress",
            GeneratedTokens: 2_300, CurrentTokens: 2_300), counter);
        Assert.Equal($"{5_544:N0} Kontexttoken · ca. {2_300:N0} erzeugte Token", later);
        Assert.Equal(7_844, counter.ActiveTokens);
        Assert.NotEqual(first, later);
        Assert.Contains(later!, GoAiAssistantService.FormatModelTokenProgress(
            new("codingWaiting", Attempt: 2, ElapsedSeconds: 150), counter)!, StringComparison.Ordinal);

        // Final native usage includes the prompt; it must not be added a second time.
        var usage = GoAiAssistantService.FormatModelTokenProgress(new("tokenProgress",
            PromptTokens: 5_544, ProcessedPromptTokens: 5_544, GeneratedTokens: 2_298, CurrentTokens: 7_842), counter);
        Assert.Equal($"{5_544:N0} Kontexttoken · ca. {2_298:N0} erzeugte Token", usage);
        Assert.Equal(7_842, counter.ActiveTokens);
    }

    [Theory]
    [InlineData("generationStarted")]
    [InlineData("generationRetry")]
    public void ANewModelAttemptResetsBothPromptAndGeneratedCounts(string state)
    {
        var counter = new GoAiAssistantService.ModelTokenProgressState();
        _ = GoAiAssistantService.FormatModelTokenProgress(new("tokenProgress",
            ProcessedPromptTokens: 5_544, GeneratedTokens: 2_300, CurrentTokens: 7_844), counter);
        Assert.Equal("0 Token", GoAiAssistantService.FormatModelTokenProgress(new(state), counter));
        Assert.Equal(0, counter.ProcessedPromptTokens);
        Assert.Equal(0, counter.GeneratedTokens);
        Assert.Equal(0, counter.ActiveTokens);

        _ = GoAiAssistantService.FormatModelTokenProgress(new("promptProcessing",
            ProcessedPromptTokens: 300), counter);
        var detail = GoAiAssistantService.FormatModelTokenProgress(new("tokenProgress",
            GeneratedTokens: 8, CurrentTokens: 8), counter);
        Assert.Equal("300 Kontexttoken · ca. 8 erzeugte Token", detail);
        Assert.Equal(308, counter.ActiveTokens);
    }

    [Fact]
    public void ToolCallCompletionWithoutUsageRetainsTheKnownPromptCount()
    {
        var counter = new GoAiAssistantService.ModelTokenProgressState();
        _ = GoAiAssistantService.FormatModelTokenProgress(new("promptProcessing",
            PromptTokens: 5_544, ProcessedPromptTokens: 5_544), counter);
        _ = GoAiAssistantService.FormatModelTokenProgress(new("tokenProgress",
            GeneratedTokens: 2_300, CurrentTokens: 2_300), counter);

        var detail = GoAiAssistantService.FormatModelTokenProgress(new("tokenProgress",
            PromptTokens: 0, ProcessedPromptTokens: 0, GeneratedTokens: 2_308, CurrentTokens: 2_308), counter);
        Assert.Equal($"{5_544:N0} Kontexttoken · ca. {2_308:N0} erzeugte Token", detail);
        Assert.Equal(5_544, counter.ProcessedPromptTokens);
        Assert.Equal(7_852, counter.ActiveTokens);
    }

    [Fact]
    public void GeneratedOnlyAndLegacyAggregateEventsDoNotInventPromptCounts()
    {
        var counter = new GoAiAssistantService.ModelTokenProgressState();
        Assert.Null(GoAiAssistantService.FormatModelTokenProgress(new("toolSelected"), counter));
        var generated = GoAiAssistantService.FormatModelTokenProgress(new("tokenProgress",
            GeneratedTokens: 12, CurrentTokens: 12), counter);
        Assert.Equal("ca. 12 erzeugte Token", generated);
        Assert.Equal(0, counter.ProcessedPromptTokens);

        _ = GoAiAssistantService.FormatModelTokenProgress(new("generationStarted"), counter);
        _ = GoAiAssistantService.FormatModelTokenProgress(new("promptProcessing", ProcessedPromptTokens: 100), counter);
        var legacy = GoAiAssistantService.FormatModelTokenProgress(new ModelGenerationEvent("tokenProgress",
            CurrentTokens: 112), counter);
        Assert.Equal("100 Kontexttoken · ca. 12 erzeugte Token", legacy);
        Assert.Equal(112, counter.ActiveTokens);
    }
}
