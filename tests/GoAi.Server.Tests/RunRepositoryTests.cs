using GoAi.Contracts;
using GoAi.Server.Core.Runs;

namespace GoAi.Server.Tests;

public sealed class RunRepositoryTests
{
    [Fact]
    public async Task IdempotencyReturnsOriginalRunAndEventsAreMonotonic()
    {
        using var context = new TestServerContext();
        var notifier = new RunEventNotifier();
        var repository = new RunRepository(context.Database, notifier);
        var request = new RunRequest(
            GoAiProtocol.Version,
            RunMode.General,
            [new RunMessage("user", [new ContentPart("text", "Heizlast prüfen")])]);

        var first = await repository.CreateAsync(request, "same-request");
        var second = await repository.CreateAsync(request, "same-request");
        var eventOne = await repository.AppendEventAsync(first.Snapshot.RunId, RunEventTypes.RunStarted, new { value = 1 });
        var eventTwo = await repository.AppendEventAsync(first.Snapshot.RunId, RunEventTypes.TextDelta, new TextDeltaEvent("Hallo"));

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.Snapshot.RunId, second.Snapshot.RunId);
        Assert.True(eventTwo.Id > eventOne.Id);
        var resumed = await repository.GetEventsAfterAsync(first.Snapshot.RunId, eventOne.Id);
        Assert.Single(resumed);
        Assert.Equal(RunEventTypes.TextDelta, resumed[0].Type);
    }

    [Fact]
    public async Task RecoveryInterruptsRunningButKeepsWaitingRuns()
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var request = new RunRequest(
            GoAiProtocol.Version,
            RunMode.General,
            [new RunMessage("user", [new ContentPart("text", "Test")])]);
        var running = await repository.CreateAsync(request, null);
        var waiting = await repository.CreateAsync(request, null);
        await repository.UpdateStateAsync(running.Snapshot.RunId, RunState.Running);
        await repository.UpdateStateAsync(waiting.Snapshot.RunId, RunState.WaitingForClient);

        var recovered = await repository.RecoverAsync();

        Assert.Equal(RunState.Interrupted, (await repository.GetAsync(running.Snapshot.RunId))?.State);
        Assert.Equal(RunState.WaitingForClient, (await repository.GetAsync(waiting.Snapshot.RunId))?.State);
        Assert.Contains(waiting.Snapshot.RunId, recovered);
    }

    [Fact]
    public async Task ClientToolResultRequiresMatchingPersistedProposal()
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var request = new RunRequest(
            GoAiProtocol.Version,
            RunMode.Code,
            [new RunMessage("user", [new ContentPart("text", "Build")])]);
        var run = await repository.CreateAsync(request, null);
        using var arguments = System.Text.Json.JsonDocument.Parse("""{"preset":"dotnet.build"}""");
        using var resultJson = System.Text.Json.JsonDocument.Parse("""{"exitCode":0}""");
        var proposal = new ToolProposal(
            "proposal-" + Guid.NewGuid().ToString("N"),
            run.Snapshot.RunId,
            ClientToolNames.ProcessRunPreset,
            arguments.RootElement.Clone(),
            ToolRiskClass.Process,
            "Build ausführen",
            DateTimeOffset.UtcNow.AddMinutes(10));
        var result = new ClientToolResult(proposal.ProposalId, "completed", resultJson.RootElement.Clone());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.SaveClientToolResultAsync(run.Snapshot.RunId, result));
        await repository.SaveToolProposalAsync(proposal);
        await repository.SaveClientToolResultAsync(run.Snapshot.RunId, result);

        Assert.Equal("completed", (await repository.GetClientToolResultAsync(proposal.ProposalId))?.Status);
    }

    [Fact]
    public async Task InterruptedRunCanBeIdempotentlyRequeuedButKeyCannotChangeRequest()
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var request = new RunRequest(
            GoAiProtocol.Version,
            RunMode.General,
            [new RunMessage("user", [new ContentPart("text", "Heizlast prüfen")])]);
        var first = await repository.CreateAsync(request, "restartable-request");
        await repository.UpdateStateAsync(first.Snapshot.RunId, RunState.Interrupted, errorCode: "run.gateway_stopped");

        var restarted = await repository.CreateAsync(request, "restartable-request");

        Assert.True(restarted.Created);
        Assert.Equal(first.Snapshot.RunId, restarted.Snapshot.RunId);
        Assert.Equal(RunState.Queued, restarted.Snapshot.State);
        Assert.Null(restarted.Snapshot.ErrorCode);

        var different = request with
        {
            Messages = [new RunMessage("user", [new ContentPart("text", "Lüftung auslegen")])],
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.CreateAsync(different, "restartable-request"));
    }
}
