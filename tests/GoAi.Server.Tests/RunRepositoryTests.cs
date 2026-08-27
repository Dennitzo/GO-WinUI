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
        Assert.True(await repository.SaveClientToolResultAsync(run.Snapshot.RunId, result));
        Assert.False(await repository.SaveClientToolResultAsync(run.Snapshot.RunId, result));

        Assert.Equal("completed", (await repository.GetClientToolResultAsync(proposal.ProposalId))?.Status);
    }

    [Theory]
    [InlineData(RunState.Running)]
    [InlineData(RunState.WaitingForClient)]
    public async Task PersistedClientToolResultAtomicallyQueuesItsPendingContinuation(RunState initialState)
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
        var checkpoint = new AgentRunCheckpoint(
            [],
            1,
            1,
            0,
            0,
            PendingProposalId: proposal.ProposalId,
            PendingToolCallId: "call-1",
            HasSuccessfulClientToolEvidence: true);
        var result = new ClientToolResult(proposal.ProposalId, "completed", resultJson.RootElement.Clone());

        await repository.SaveToolProposalAsync(proposal);
        await repository.SaveCheckpointAsync(run.Snapshot.RunId, checkpoint);
        await repository.UpdateStateAsync(run.Snapshot.RunId, initialState);
        Assert.True(await repository.SaveClientToolResultAsync(run.Snapshot.RunId, result));

        Assert.True(await repository.TryQueueClientToolContinuationAsync(run.Snapshot.RunId, proposal.ProposalId));
        Assert.Equal(RunState.Queued, (await repository.GetAsync(run.Snapshot.RunId))?.State);
        Assert.True((await repository.GetCheckpointAsync(run.Snapshot.RunId))?.HasSuccessfulClientToolEvidence);
        Assert.False(await repository.TryQueueClientToolContinuationAsync(run.Snapshot.RunId, proposal.ProposalId));
    }

    [Fact]
    public async Task CodingWorkCycleSurvivesCheckpointRoundTrip()
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var request = new RunRequest(
            GoAiProtocol.Version,
            RunMode.Code,
            [new RunMessage("user", [new ContentPart("text", "Erstelle und prüfe eine Datei")])]);
        var run = await repository.CreateAsync(request, null);
        var ledger = new TaskLedgerSnapshot(
            "Erstelle und prüfe eine Datei",
            CodingTaskKind.Creation,
            CodingAgentPhase.Editing,
            "revision-3",
            [], [], [], [], [],
            "Setze den ausgewählten Quellenbeleg um.",
            Research:
            [
                new AgentResearchRecord(
                    "research-key", "webFetch", "https://example.test/source", "action-fetch",
                    "evidence-fetch", "result-fetch", 0, WorkCycle: 3),
            ],
            WorkCycleStage: CodingWorkCycleStage.Implementing,
            WorkCycle: 3,
            VerifiedChangeCount: 0,
            ActiveResearchEvidenceId: "evidence-fetch",
            ActiveContextGap: "Die erste Quelle enthielt die benötigte Signatur nicht.");
        var checkpoint = new AgentRunCheckpoint([], 2, 2, 100, 20, AgentProtocolVersion: 2, TaskLedger: ledger);

        await repository.SaveCheckpointAsync(run.Snapshot.RunId, checkpoint);
        var restored = await repository.GetCheckpointAsync(run.Snapshot.RunId);

        Assert.NotNull(restored?.TaskLedger);
        Assert.Equal(CodingWorkCycleStage.Implementing, restored.TaskLedger.WorkCycleStage);
        Assert.Equal(3, restored.TaskLedger.WorkCycle);
        Assert.Equal("evidence-fetch", restored.TaskLedger.ActiveResearchEvidenceId);
        Assert.Equal(3, Assert.Single(restored.TaskLedger.Research!).WorkCycle);
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

    [Fact]
    public async Task AgentActionAndObservationArePersistedExactlyOnce()
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var request = new RunRequest(
            GoAiProtocol.Version,
            RunMode.Code,
            [new RunMessage("user", [new ContentPart("text", "Prüfe das Projekt")])]);
        var run = await repository.CreateAsync(request, null);
        using var argumentsJson = System.Text.Json.JsonDocument.Parse("""{"operation":"map"}""");
        using var resultJson = System.Text.Json.JsonDocument.Parse("""{"workspaceRevision":"revision-1","fileCount":4}""");
        var action = new AgentActionEnvelope(
            "action-1",
            1,
            "workspace.inspect",
            "map",
            argumentsJson.RootElement.Clone(),
            "run-action-1",
            "revision-1",
            DateTimeOffset.UtcNow);
        var observation = new AgentObservation(
            action.ActionId,
            true,
            resultJson.RootElement.Clone(),
            new string('a', 64),
            EvidenceId: "evidence-1",
            WorkspaceRevision: "revision-1",
            CreatedAt: DateTimeOffset.UtcNow);

        Assert.True(await repository.SaveAgentActionAsync(run.Snapshot.RunId, action));
        Assert.False(await repository.SaveAgentActionAsync(run.Snapshot.RunId, action));
        var conflictingAction = action with
        {
            ActionId = "action-conflict",
            IdempotencyKey = "run-action-conflict",
        };
        Assert.False(await repository.SaveAgentActionAsync(run.Snapshot.RunId, conflictingAction));
        Assert.Equal(
            action.ActionId,
            (await repository.GetAgentActionBySequenceAsync(run.Snapshot.RunId, action.Sequence))?.ActionId);
        Assert.True(await repository.SaveAgentObservationAsync(run.Snapshot.RunId, observation));
        Assert.False(await repository.SaveAgentObservationAsync(run.Snapshot.RunId, observation));

        var steps = await repository.GetRecentAgentStepsAsync(run.Snapshot.RunId);
        var step = Assert.Single(steps);
        Assert.Equal(action.ActionId, step.Action.ActionId);
        Assert.NotNull(step.Observation);
        Assert.Equal(observation.ResultHash, step.Observation!.ResultHash);
        Assert.Equal("evidence-1", step.Observation.EvidenceId);
    }

    [Fact]
    public async Task CommittedClientObservationRemovesRawToolExchangeAtomically()
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var request = new RunRequest(
            GoAiProtocol.Version,
            RunMode.Code,
            [new RunMessage("user", [new ContentPart("text", "Lies die Datei")])]);
        var run = await repository.CreateAsync(request, null);
        using var actionArguments = System.Text.Json.JsonDocument.Parse("""{"operation":"read","path":"Program.cs"}""");
        using var proposalArguments = System.Text.Json.JsonDocument.Parse("""{"path":"Program.cs"}""");
        using var rawResult = System.Text.Json.JsonDocument.Parse("""{"text":"vollständiger lokaler Quelltext","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""");
        using var compactResult = System.Text.Json.JsonDocument.Parse("""{"text":{"omitted":true},"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""");
        var action = new AgentActionEnvelope(
            "action-client-read",
            1,
            "workspace.inspect",
            "read",
            actionArguments.RootElement.Clone(),
            "idempotency-client-read",
            "revision-1",
            DateTimeOffset.UtcNow);
        var proposal = new ToolProposal(
            "proposal-client-read",
            run.Snapshot.RunId,
            ClientToolNames.FileSystemReadText,
            proposalArguments.RootElement.Clone(),
            ToolRiskClass.ReadOnly,
            "Datei lesen",
            DateTimeOffset.UtcNow.AddMinutes(10));
        var clientResult = new ClientToolResult(
            proposal.ProposalId,
            "completed",
            rawResult.RootElement.Clone());
        var observation = new AgentObservation(
            action.ActionId,
            true,
            compactResult.RootElement.Clone(),
            new string('b', 64),
            EvidenceId: "evidence-client-read",
            WorkspaceRevision: "revision-1",
            CreatedAt: DateTimeOffset.UtcNow);

        Assert.True(await repository.SaveAgentActionAsync(run.Snapshot.RunId, action));
        await repository.SaveToolProposalAsync(proposal);
        Assert.True(await repository.SaveClientToolResultAsync(run.Snapshot.RunId, clientResult));

        Assert.True(await repository.SaveAgentObservationAsync(run.Snapshot.RunId, observation));

        Assert.Null(await repository.GetToolProposalAsync(proposal.ProposalId, run.Snapshot.RunId));
        Assert.Null(await repository.GetClientToolResultAsync(proposal.ProposalId));
        Assert.Equal(
            compactResult.RootElement.GetRawText(),
            (await repository.GetAgentObservationAsync(run.Snapshot.RunId, action.ActionId))?.Result.GetRawText());
    }
}
