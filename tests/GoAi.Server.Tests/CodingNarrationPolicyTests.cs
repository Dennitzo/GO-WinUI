using GoAi.Server.Core.Coding;
using GoAi.Server.Core.Models;

namespace GoAi.Server.Tests;

public sealed class CodingNarrationPolicyTests
{
    [Fact]
    public void CompleteNarrationIsReleasedTogetherBeforeToolDispatch()
    {
        var gate = new GoAi.Server.Core.Runs.IncrementalVisibleTextGate(true, bufferUntilComplete: true);
        const string first = "Die neuen Tests bestehen. ";
        const string second = "Ich prüfe jetzt zusätzlich die vorhandenen Modellrouting-Tests, um die Regression zu bestätigen.";
        Assert.Null(gate.Push(first));
        Assert.Null(gate.Push(second));
        Assert.False(gate.HasStreamed);
        Assert.Equal(first + second, gate.Flush());
        Assert.Null(gate.Flush());
    }

    [Fact]
    public void NewCodingPolicyIncludesStagesAndSpeakableNarration()
    {
        var policy = CodingAgentPolicy.ForWorkingState(true);
        Assert.Contains(CodingAgentPolicy.StagedExecutionAndNarrationPrompt, policy, StringComparison.Ordinal);
        Assert.Contains(CodingAgentPolicy.WorkspaceDependenciesPrompt, policy, StringComparison.Ordinal);
        Assert.Contains("höchstens einer Etappe in_progress", policy, StringComparison.Ordinal);
        Assert.Contains("beziehungsweise", policy, StringComparison.Ordinal);
        Assert.Contains("Diese Sprachregel ändert weder Code noch Werkzeugargumente", policy, StringComparison.Ordinal);
    }

    [Fact]
    public void ResumedPolicyAddsInstructionsOnceWithoutTrustingUserDataOrRewritingHistory()
    {
        var original = new LmChatMessage("system", "Stored policy");
        List<LmChatMessage> messages = [original, new("user", CodingAgentPolicy.StagedExecutionAndNarrationPrompt + CodingAgentPolicy.WorkspaceDependenciesPrompt)];
        CodingAgentPolicy.EnsureCurrentInstructions(messages);
        var prefix = messages.ToArray();
        CodingAgentPolicy.EnsureCurrentInstructions(messages);
        Assert.Equal(prefix, messages);
        Assert.Single(messages, item => item.Role == "system"
            && item.Content!.Contains(CodingAgentPolicy.WorkspaceDependenciesPrompt, StringComparison.Ordinal));
        Assert.StartsWith(original.Content!, messages[0].Content, StringComparison.Ordinal);
        Assert.Single(messages, item => item.Role == "system"
            && item.Content!.Contains(CodingAgentPolicy.StagedExecutionAndNarrationPrompt, StringComparison.Ordinal));
    }
}
