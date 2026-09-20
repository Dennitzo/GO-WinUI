using GoAi.Server.Core.Coding;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runs;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class WorkingStateConsistencyTests
{
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void NewRunExplainsResetAndUnknownIdsInsteadOfDemandingTitleForExistingItems()
    {
        var previous = CodingWorkingStateReducer.ApplyPlanUpdate(CodingWorkingState.Create("Old task"), Args(new
        {
            steps = new[] { new { id = "policies", title = "Policies", status = "pending" } },
        }));
        var checkpoint = new AgentRunCheckpoint([new LmChatMessage("user", "Old task")], 1, 1, 0, 0,
            WorkingState: previous, PreserveSessionPromptPrefix: true);
        var state = CodingSessionContext.ContinueWorkingState(checkpoint, "Continue")!;
        var messages = CodingSessionContext.Continue(checkpoint, [new("system", "Policy"), new("user", "Continue")]);
        Assert.Contains(messages, m => m.Content?.Contains("steps und acceptanceCriteria starten leer", StringComparison.Ordinal) == true);
        Assert.Equal("Continue", messages[^1].Content);
        Assert.Empty(state.Plan);
        var error = Assert.Throws<ArgumentException>(() => CodingWorkingStateReducer.ApplyPlanUpdate(state,
            Args(new { steps = new[] { new { id = "policies", status = "in_progress" } } })));
        Assert.Contains("policies", error.Message, StringComparison.Ordinal);
        Assert.Contains("nicht gespeichert", error.Message, StringComparison.Ordinal);
        Assert.Contains("steps: []", error.Message, StringComparison.Ordinal);
        var updated = CodingWorkingStateReducer.ApplyPlanUpdate(previous,
            Args(new { steps = new[] { new { id = "policies", status = "in_progress" } } }));
        Assert.Equal("Policies", Assert.Single(updated.Plan).Title);
    }

    [Fact]
    public void SeparateNamespacesAndAtomicFailureAreExplicit()
    {
        var state = CodingWorkingStateReducer.ApplyPlanUpdate(CodingWorkingState.Create("Task"), Args(new
        {
            steps = new[] { new { id = "known", title = "Known", status = "pending" } },
        }));
        var before = JsonSerializer.Serialize(state);
        var error = Assert.Throws<ArgumentException>(() => CodingWorkingStateReducer.ApplyPlanUpdate(state, Args(new
        {
            steps = new[] { new { id = "known", status = "in_progress" } },
            acceptanceCriteria = new[] { new { id = "known", status = "pending" } },
        })));
        Assert.Contains("acceptanceCriteria", error.Message, StringComparison.Ordinal);
        Assert.Contains("kein Feld wurde geändert", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, JsonSerializer.Serialize(state));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ExplicitInvalidTitleIsNotAnOmittedTitle(string? title)
    {
        var state = CodingWorkingStateReducer.ApplyPlanUpdate(CodingWorkingState.Create("Task"), Args(new
        {
            steps = new[] { new { id = "work", title = "Work", status = "pending" } },
        }));
        var error = Assert.Throws<ArgumentException>(() => CodingWorkingStateReducer.ApplyPlanUpdate(state,
            Args(new { steps = new[] { new { id = "work", title } } })));
        Assert.Contains("title wurde mitgesendet", error.Message, StringComparison.Ordinal);
        Assert.Equal("Work", Assert.Single(state.Plan).Title);
    }

    [Fact]
    public void ReceiptListsActualIdsAndDirectReducerRejectsInvalidShape()
    {
        var args = Args(new { steps = new[] { new { id = "work", title = "Work", status = "pending" } } });
        var state = CodingWorkingStateReducer.ApplyPlanUpdate(CodingWorkingState.Create("Task"), args);
        var receipt = CodingWorkingStateTools.CreatePlanReceipt(state, args);
        Assert.Equal("work", receipt.GetProperty("storedStepIds")[0].GetString());
        Assert.Empty(receipt.GetProperty("storedAcceptanceCriterionIds").EnumerateArray());
        Assert.Throws<ArgumentException>(() => CodingWorkingStateReducer.ApplyPlanUpdate(state, Args(new { })));
        Assert.Throws<ArgumentException>(() => CodingWorkingStateReducer.ApplyPlanUpdate(state,
            Args(new { steps = new[] { new { id = "work", unexpected = true } } })));
    }
}
