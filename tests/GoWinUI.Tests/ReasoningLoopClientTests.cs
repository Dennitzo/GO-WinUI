using System.Text.Json;
using GoAi.Contracts;
using GoWinUI.App.Services;
using GoWinUI.Core.Models;

namespace GoWinUI.Tests;

public sealed class ReasoningLoopClientTests
{
    [Theory]
    [InlineData("provider.reasoning_loop")]
    [InlineData("provider.reasoning_watchdog")]
    public void TerminalGuardEventDoesNotRestartTheGeneralPrompt(string errorCode)
    {
        // The SSE path preserves the server's non-retryable failure contract.
        var wire = JsonSerializer.Serialize(new RunFailedEvent(errorCode, "Reasoning stopped without useful progress.", Retryable: false),
            GoAiProtocol.CreateJsonOptions());
        var failure = JsonSerializer.Deserialize<RunFailedEvent>(wire, GoAiProtocol.CreateJsonOptions())!;
        var terminal = new GoAiRunTerminalException(failure.ErrorCode, failure.Message, failure.Retryable);

        Assert.Equal(errorCode, terminal.ErrorCode);
        Assert.False(terminal.Retryable);
        Assert.False(GoAiAssistantService.ShouldRetryCurrentPrompt(action: null, terminal));
        Assert.False(GoAiAssistantService.ShouldRetryCurrentPrompt(PromptTriggerAction.Blender, terminal));
        Assert.False(GoAiAssistantService.ShouldRetryCurrentPrompt(PromptTriggerAction.Coding, terminal));
    }

    [Theory]
    [InlineData("provider.reasoning_loop")]
    [InlineData("provider.reasoning_watchdog")]
    public void ReconnectedFailedSnapshotDoesNotConvertGuardFailureIntoPromptRetry(string errorCode)
    {
        // The snapshot path has no Retryable field. It reconstructs this decision
        // from the durable error code when the terminal SSE event was missed.
        var now = DateTimeOffset.UtcNow;
        var snapshot = new RunSnapshot("run-reasoning-guard", RunState.Failed, RunMode.General,
            "local-model", null, 120, now, now, errorCode);
        var retryable = GoAiAssistantService.IsRetryableServerErrorCode(snapshot.ErrorCode);
        var terminal = new GoAiRunTerminalException(snapshot.ErrorCode!, "Persisted reasoning guard failure.", retryable);

        Assert.False(retryable);
        Assert.False(GoAiAssistantService.ShouldRetryCurrentPrompt(action: null, terminal));
        Assert.False(GoAiAssistantService.ShouldRetryCurrentPrompt(PromptTriggerAction.Blender, terminal));
    }
}
