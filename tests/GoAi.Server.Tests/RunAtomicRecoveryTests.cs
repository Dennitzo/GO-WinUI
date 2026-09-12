using System.Text.Json;
using GoAi.Contracts;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runs;
using Microsoft.Data.Sqlite;

namespace GoAi.Server.Tests;

public sealed class RunAtomicRecoveryTests
{
    [Fact]
    public async Task FinalizationFailureRollsBackStateEventAndCheckpointTogetherThenCompletesOnce()
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var run = await CreateRunningAsync(repository);
        var checkpoint = new AgentRunCheckpoint([new LmChatMessage("user", "Already edited the fixture")], 4, 3, 100, 20);
        await repository.SaveCheckpointAsync(run, checkpoint);
        await ExecuteSqlAsync(context, """
            CREATE TRIGGER fail_completion_delete BEFORE DELETE ON run_checkpoints BEGIN
                SELECT RAISE(ABORT, 'injected checkpoint deletion failure');
            END;
            """);
        var completed = new RunCompletedEvent("Verified work", "coding/fixture", 100, 20);

        await Assert.ThrowsAsync<SqliteException>(() => repository.FinalizeConversationAsync(run, completed));

        Assert.Equal(RunState.Running, (await repository.GetAsync(run))!.State);
        Assert.NotNull(await repository.GetCheckpointAsync(run));
        Assert.DoesNotContain(await repository.GetEventsAfterAsync(run, 0), item => item.Type == RunEventTypes.RunCompleted);
        await ExecuteSqlAsync(context, "DROP TRIGGER fail_completion_delete;");
        Assert.True(await repository.FinalizeConversationAsync(run, completed));
        var reopened = new RunRepository(context.Database, new RunEventNotifier());
        Assert.False(await reopened.FinalizeConversationAsync(run, completed));
        var snapshot = (await reopened.GetAsync(run))!;
        Assert.Equal(RunState.Completed, snapshot.State);
        Assert.Equal("coding/fixture", snapshot.SelectedModel);
        Assert.Equal("Verified work", snapshot.SessionTitle);
        Assert.Null(await reopened.GetCheckpointAsync(run));
        var receipt = Assert.Single(await reopened.GetEventsAfterAsync(run, 0), item => item.Type == RunEventTypes.RunCompleted);
        Assert.Equal(completed, receipt.Data.Deserialize<RunCompletedEvent>(GoAiProtocol.CreateJsonOptions()));
        Assert.DoesNotContain(run, await reopened.RecoverAsync());
    }

    [Theory]
    [InlineData(RunState.Cancelled)]
    [InlineData(RunState.Failed)]
    public async Task FinalizationCannotOverwriteAnAlreadyTerminalRunOrDeleteItsRecoveryEvidence(RunState terminal)
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var run = await CreateRunningAsync(repository);
        await repository.SaveCheckpointAsync(run, new AgentRunCheckpoint([], 1, 1, 0, 0));
        await repository.UpdateStateAsync(run, terminal, errorCode: "expected.terminal");

        Assert.False(await repository.FinalizeConversationAsync(run, new RunCompletedEvent("Must not complete", null, 0, 0)));

        Assert.Equal(terminal, (await repository.GetAsync(run))!.State);
        Assert.Equal("expected.terminal", (await repository.GetAsync(run))!.ErrorCode);
        Assert.NotNull(await repository.GetCheckpointAsync(run));
        Assert.Empty(await repository.GetEventsAfterAsync(run, 0));
    }

    [Fact]
    public async Task RecoveryPublishesACheckpointedButUnannouncedProposalOnceWithStableEventIdentity()
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var (run, proposal) = await CreatePendingAsync(repository);
        Assert.Empty(await repository.GetEventsAfterAsync(run, 0));
        Assert.Contains(run, await repository.RecoverAsync());

        var restored = new RunRepository(context.Database, new RunEventNotifier());
        var published = Assert.IsType<RunEvent>(await restored.EnsureClientToolProposedEventAsync(run, proposal.ProposalId));
        var repeated = Assert.IsType<RunEvent>(await restored.EnsureClientToolProposedEventAsync(run, proposal.ProposalId));
        Assert.Equal(published.Id, repeated.Id);
        Assert.Equal(published.CreatedAt, repeated.CreatedAt);
        Assert.True(JsonElement.DeepEquals(published.Data, JsonSerializer.SerializeToElement(proposal, GoAiProtocol.CreateJsonOptions())));
        Assert.Equal(RunState.WaitingForClient, (await restored.GetAsync(run))!.State);
        var events = await restored.GetEventsAfterAsync(run, 0);
        Assert.Single(events, item => item.Type == RunEventTypes.ClientToolProposed);
        Assert.Single(events, item => item.Type == RunEventTypes.RunWaitingForClient);
        Assert.Equal(proposal.ProposalId, (await restored.GetCheckpointAsync(run))!.PendingProposalId);

        var result = new ClientToolResult(proposal.ProposalId, "completed", JsonSerializer.SerializeToElement(new { applied = true }));
        Assert.True(await restored.SaveClientToolResultAsync(run, result));
        Assert.True(await restored.TryQueueClientToolContinuationAsync(run, proposal.ProposalId));
        var acknowledged = Assert.IsType<RunEvent>(await restored.EnsureClientToolProposedEventAsync(run, proposal.ProposalId));
        Assert.Equal(published.Id, acknowledged.Id);
        Assert.True((await restored.GetAsync(run))!.State == RunState.Queued, "an acknowledged result must not be changed back to waiting");
        Assert.NotNull(await restored.GetClientToolResultAsync(proposal.ProposalId));
    }

    [Fact]
    public async Task ConcurrentProposalRecoveryKeepsAnExistingClientJournalEventId()
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var (run, proposal) = await CreatePendingAsync(repository);
        var original = await repository.AppendEventAsync(run, RunEventTypes.ClientToolProposed, proposal);

        var recovered = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            new RunRepository(context.Database, new RunEventNotifier()).EnsureClientToolProposedEventAsync(run, proposal.ProposalId)));

        Assert.All(recovered, item => Assert.Equal(original.Id, Assert.IsType<RunEvent>(item).Id));
        Assert.Single(await repository.GetEventsAfterAsync(run, 0), item => item.Type == RunEventTypes.ClientToolProposed);
    }

    [Fact]
    public async Task ProposalPublicationRollsBackWaitingStateAndBothEventsWhenSecondEventFails()
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var (run, proposal) = await CreatePendingAsync(repository);
        await ExecuteSqlAsync(context, $"""
            CREATE TRIGGER fail_waiting_event BEFORE INSERT ON run_events
            WHEN NEW.event_type = '{RunEventTypes.RunWaitingForClient}' BEGIN
                SELECT RAISE(ABORT, 'injected waiting event failure');
            END;
            """);

        await Assert.ThrowsAsync<SqliteException>(() => repository.EnsureClientToolProposedEventAsync(run, proposal.ProposalId));

        Assert.Equal(RunState.Running, (await repository.GetAsync(run))!.State);
        Assert.Empty(await repository.GetEventsAfterAsync(run, 0));
        Assert.NotNull(await repository.GetCheckpointAsync(run));
        await ExecuteSqlAsync(context, "DROP TRIGGER fail_waiting_event;");
        Assert.NotNull(await repository.EnsureClientToolProposedEventAsync(run, proposal.ProposalId));
        Assert.Equal(2, (await repository.GetEventsAfterAsync(run, 0)).Count);
    }

    [Fact]
    public async Task CancelledOrSupersededPendingProposalIsNeverPublishedForExecution()
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var (run, proposal) = await CreatePendingAsync(repository);
        await repository.UpdateStateAsync(run, RunState.Cancelled);

        Assert.Null(await repository.EnsureClientToolProposedEventAsync(run, proposal.ProposalId));
        Assert.Empty(await repository.GetEventsAfterAsync(run, 0));
        Assert.Equal(RunState.Cancelled, (await repository.GetAsync(run))!.State);

        var (otherRun, otherProposal) = await CreatePendingAsync(repository);
        await repository.SaveCheckpointAsync(otherRun, new AgentRunCheckpoint([], 2, 2, 0, 0));
        Assert.Null(await repository.EnsureClientToolProposedEventAsync(otherRun, otherProposal.ProposalId));
        Assert.Empty(await repository.GetEventsAfterAsync(otherRun, 0));
    }

    private static async Task<string> CreateRunningAsync(RunRepository repository)
    {
        var created = await repository.CreateAsync(new RunRequest(GoAiProtocol.Version, RunMode.Coding,
            [new RunMessage("user", [new ContentPart("text", "Perform the fixture task")])]), null);
        await repository.UpdateStateAsync(created.Snapshot.RunId, RunState.Running);
        return created.Snapshot.RunId;
    }

    private static async Task<(string Run, ToolProposal Proposal)> CreatePendingAsync(RunRepository repository)
    {
        var run = await CreateRunningAsync(repository);
        var arguments = JsonSerializer.SerializeToElement(new { path = "fixture.txt", content = "new content" });
        var proposal = new ToolProposal("proposal-" + Guid.NewGuid().ToString("N"), run, ClientToolNames.CodingWrite,
            arguments, ToolRiskClass.LocalMutation, "Write the fixture", DateTimeOffset.MaxValue);
        await repository.SaveToolProposalAsync(proposal);
        var call = new LmToolCall("call-fixture", proposal.Name, arguments);
        await repository.SaveCheckpointAsync(run, new AgentRunCheckpoint([], 1, 1, 0, 0, [call], 0, proposal.ProposalId, call.Id));
        return (run, proposal);
    }

    private static async Task ExecuteSqlAsync(TestServerContext context, string sql)
    {
        await using var connection = await context.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync();
    }
}
