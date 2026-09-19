using System.Text.Json;
using GoAi.Contracts;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runs;
using Microsoft.Data.Sqlite;

namespace GoAi.Server.Tests;

public sealed class RunSteeringTests
{
    private static readonly string[] OrderedInputIds = ["input-one", "input-two"];
    [Theory]
    [InlineData(RunMode.General, RunState.Queued)]
    [InlineData(RunMode.Auto, RunState.Running)]
    [InlineData(RunMode.Coding, RunState.Running)]
    [InlineData(RunMode.Coding, RunState.WaitingForClient)]
    public async Task AcceptedInputKeepsRunAndSessionAndSurvivesRepositoryReopen(RunMode mode, RunState state)
    {
        using var context = new TestServerContext();
        var repository = Repository(context);
        var run = await CreateAsync(repository, mode, state);
        var original = (await repository.GetRequestAsync(run))!;
        var request = new RunSteeringRequest("session-fixture", "input-one", "Bitte zuerst die Sidebar reparieren.");

        var accepted = await repository.AcceptSteeringAsync(run, request);

        Assert.Equal(run, accepted.RunId);
        Assert.Equal(request.InputId, accepted.InputId);
        Assert.False(accepted.Duplicate);
        Assert.True(accepted.Sequence > 0);
        var reopened = Repository(context);
        Assert.Equal(state, (await reopened.GetAsync(run))!.State);
        var pending = Assert.Single(await reopened.GetPendingSteeringAsync(run));
        Assert.Equal(request.SessionId, pending.SessionId);
        Assert.Equal(request.Text, pending.Text);
        Assert.Equal(accepted.Sequence, pending.Sequence);
        Assert.Equal(request.InputId, pending.InputId);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(await reopened.GetRequestAsync(run)));
        var receipt = Assert.Single(await reopened.GetEventsAfterAsync(run, 0), item => item.Type == RunSteeringEventTypes.Accepted);
        Assert.Equal(pending, receipt.Data.Deserialize<RunSteeringEvent>(GoAiProtocol.CreateJsonOptions()));
    }

    [Fact]
    public async Task ConcurrentRetriesHaveOneDurableSequenceAndOneAcceptedEvent()
    {
        using var context = new TestServerContext();
        var repository = Repository(context);
        var run = await CreateAsync(repository);
        var request = new RunSteeringRequest("session-fixture", "stable-input", "Weiter im selben Projekt.");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await ready.Task;
            return await Repository(context).AcceptSteeringAsync(run, request);
        })).ToArray();
        ready.SetResult();

        var receipts = await Task.WhenAll(requests);

        Assert.Single(receipts, receipt => !receipt.Duplicate);
        Assert.Single(receipts.Select(receipt => receipt.Sequence).Distinct());
        Assert.Single(await repository.GetPendingSteeringAsync(run));
        Assert.Single(await repository.GetEventsAfterAsync(run, 0), item => item.Type == RunSteeringEventTypes.Accepted);
    }

    [Fact]
    public async Task DifferentInputsStayOrderedAndCannotLeakIntoAnotherRunOfTheSameSession()
    {
        using var context = new TestServerContext();
        var repository = Repository(context);
        var run = await CreateAsync(repository);
        var other = await CreateAsync(repository);
        var first = await repository.AcceptSteeringAsync(run, new("session-fixture", "input-one", "Erste Ergänzung"));
        var second = await repository.AcceptSteeringAsync(run, new("session-fixture", "input-two", "Zweite Ergänzung"));

        Assert.True(second.Sequence > first.Sequence);
        Assert.Equal(OrderedInputIds, (await repository.GetPendingSteeringAsync(run)).Select(input => input.InputId));
        Assert.Empty(await repository.GetPendingSteeringAsync(other));
        Assert.Empty(await repository.GetEventsAfterAsync(other, 0));
    }

    [Fact]
    public async Task SessionMismatchAndReusedIdWithDifferentTextAreRejectedWithoutChangingExistingInput()
    {
        using var context = new TestServerContext();
        var repository = Repository(context);
        var run = await CreateAsync(repository);
        var original = new RunSteeringRequest("session-fixture", "input-one", "Originale Anweisung");
        await repository.AcceptSteeringAsync(run, original);

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.AcceptSteeringAsync(run, original with { SessionId = "other-session" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.AcceptSteeringAsync(run, original with { Text = "Andere Anweisung" }));

        var pending = Assert.Single(await repository.GetPendingSteeringAsync(run));
        Assert.Equal(original.Text, pending.Text);
        Assert.Equal(original.SessionId, pending.SessionId);
        Assert.Single(await repository.GetEventsAfterAsync(run, 0), item => item.Type == RunSteeringEventTypes.Accepted);
    }

    [Theory]
    [InlineData(RunState.Completed)]
    [InlineData(RunState.Cancelled)]
    [InlineData(RunState.Failed)]
    [InlineData(RunState.Interrupted)]
    public async Task TerminalRunRejectsNewInputButReturnsTheSameReceiptForAnAcceptedRetry(RunState terminal)
    {
        using var context = new TestServerContext();
        var repository = Repository(context);
        var run = await CreateAsync(repository);
        var request = new RunSteeringRequest("session-fixture", "input-one", "Schon angenommen");
        var accepted = await repository.AcceptSteeringAsync(run, request);
        await repository.UpdateStateAsync(run, terminal);

        var retry = await Repository(context).AcceptSteeringAsync(run, request);

        Assert.True(retry.Duplicate);
        Assert.Equal(accepted.Sequence, retry.Sequence);
        Assert.Equal(accepted.RunId, retry.RunId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.AcceptSteeringAsync(run, request with { InputId = "new-input" }));
        Assert.Equal(terminal, (await repository.GetAsync(run))!.State);
        Assert.Single(await repository.GetEventsAfterAsync(run, 0), item => item.Type == RunSteeringEventTypes.Accepted);
    }

    [Fact]
    public async Task PendingSteeringPreventsFinalizationAndRetainsRecoveryCheckpoint()
    {
        using var context = new TestServerContext();
        var repository = Repository(context);
        var run = await CreateAsync(repository);
        await repository.SaveCheckpointAsync(run, new AgentRunCheckpoint([new LmChatMessage("user", "Originale Aufgabe")], 1, 1, 10, 2));
        await repository.AcceptSteeringAsync(run, new("session-fixture", "input-one", "Noch nicht abschließen."));

        Assert.False(await repository.FinalizeConversationAsync(run, new("Zu früh", "coding/fixture", 10, 2)));

        Assert.Equal(RunState.Running, (await repository.GetAsync(run))!.State);
        Assert.NotNull(await repository.GetCheckpointAsync(run));
        Assert.Single(await repository.GetPendingSteeringAsync(run));
        Assert.DoesNotContain(await repository.GetEventsAfterAsync(run, 0), item => item.Type == RunEventTypes.RunCompleted);
    }

    [Fact]
    public async Task AcceptAndFinalizeRaceNeverCompletesWithAnUnappliedAcceptedInput()
    {
        using var context = new TestServerContext();
        var repository = Repository(context);
        // Multiple real concurrent SQLite transactions exercise both legal
        // outcomes: input wins and completion waits, or completion wins and
        // the client receives a conflict instead of a misleading acceptance.
        for (var iteration = 0; iteration < 8; iteration++)
        {
            var run = await CreateAsync(repository);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var accept = Task.Run(async () =>
            {
                await start.Task;
                try { return await Repository(context).AcceptSteeringAsync(run, new("session-fixture", "race-input", "Neue Priorität")); }
                catch (InvalidOperationException) { return null; }
            });
            var finalize = Task.Run(async () =>
            {
                await start.Task;
                return await Repository(context).FinalizeConversationAsync(run, new("Fertig", "coding/fixture", 10, 2));
            });
            start.SetResult();
            await Task.WhenAll(accept, finalize);
            var acceptance = await accept;
            var finalized = await finalize;
            var pending = await repository.GetPendingSteeringAsync(run);
            var snapshot = (await repository.GetAsync(run))!;
            if (acceptance is not null)
            {
                Assert.False(finalized);
                Assert.Equal(RunState.Running, snapshot.State);
                Assert.Single(pending);
            }
            else
            {
                Assert.True(finalized);
                Assert.Equal(RunState.Completed, snapshot.State);
                Assert.Empty(pending);
            }
        }
    }

    [Fact]
    public async Task FailedAcceptedEventRollsBackInputSoAnExactRetryCanBeAcceptedNormally()
    {
        using var context = new TestServerContext();
        var repository = Repository(context);
        var run = await CreateAsync(repository);
        var request = new RunSteeringRequest("session-fixture", "input-one", "Atomar speichern");
        await ExecuteSqlAsync(context, $"""
            CREATE TRIGGER fail_steering_event BEFORE INSERT ON run_events
            WHEN NEW.event_type = '{RunSteeringEventTypes.Accepted}' BEGIN
                SELECT RAISE(ABORT, 'injected steering event failure');
            END;
            """);

        await Assert.ThrowsAsync<SqliteException>(() => repository.AcceptSteeringAsync(run, request));

        Assert.Empty(await repository.GetPendingSteeringAsync(run));
        Assert.Empty(await repository.GetEventsAfterAsync(run, 0));
        await ExecuteSqlAsync(context, "DROP TRIGGER fail_steering_event;");
        Assert.False((await repository.AcceptSteeringAsync(run, request)).Duplicate);
        Assert.Single(await repository.GetPendingSteeringAsync(run));
    }

    [Fact]
    public async Task CheckpointAppliesOnlyIncludedInputsAndPersistsTheirVisibleBoundaryOnce()
    {
        using var context = new TestServerContext();
        var repository = Repository(context);
        var run = await CreateAsync(repository);
        var first = await repository.AcceptSteeringAsync(run, new("session-fixture", "first", "Erste Änderung"));
        var second = await repository.AcceptSteeringAsync(run, new("session-fixture", "second", "Spätere Änderung"));
        await repository.AppendEventAsync(run, RunEventTypes.TextDelta, new TextDeltaEvent(new string('a', 73)));
        var checkpoint = new AgentRunCheckpoint([new LmChatMessage("user", "Erste Änderung")], 2, 0, 50, 20,
            VisibleTextLength: 73, AppliedSteeringSequence: first.Sequence);

        await repository.SaveCheckpointAsync(run, checkpoint);
        await Repository(context).SaveCheckpointAsync(run, checkpoint);

        Assert.Equal("second", Assert.Single(await repository.GetPendingSteeringAsync(run)).InputId);
        var applied = Assert.Single(await repository.GetEventsAfterAsync(run, 0), item => item.Type == RunSteeringEventTypes.Applied)
            .Data.Deserialize<RunSteeringEvent>(GoAiProtocol.CreateJsonOptions())!;
        Assert.Equal(first.Sequence, applied.Sequence);
        Assert.Equal(73, applied.VisibleTextOffset);
        Assert.False(await repository.FinalizeConversationAsync(run, new("Zu früh", null, 50, 20)));
        await repository.AppendEventAsync(run, RunEventTypes.TextDelta, new TextDeltaEvent(new string('b', 26)));
        await repository.SaveCheckpointAsync(run, checkpoint with { AppliedSteeringSequence = second.Sequence, VisibleTextLength = 99 });
        Assert.Empty(await repository.GetPendingSteeringAsync(run));
        Assert.True(await repository.FinalizeConversationAsync(run, new("Fertig", null, 50, 20)));
        var duplicate = await repository.AcceptSteeringAsync(run, new("session-fixture", "first", "Erste Änderung"));
        Assert.True(duplicate.Duplicate);
        Assert.Equal("applied", duplicate.State);
        Assert.Equal(2, (await repository.GetEventsAfterAsync(run, 0)).Count(item => item.Type == RunSteeringEventTypes.Applied));
    }

    [Fact]
    public async Task FailedAppliedEventCannotAdvanceCheckpointOrLosePendingInput()
    {
        using var context = new TestServerContext();
        var repository = Repository(context);
        var run = await CreateAsync(repository);
        var original = new AgentRunCheckpoint([new LmChatMessage("user", "Vorher")], 1, 0, 10, 0);
        await repository.SaveCheckpointAsync(run, original);
        var accepted = await repository.AcceptSteeringAsync(run, new("session-fixture", "input", "Neue Anweisung"));
        await ExecuteSqlAsync(context, $"""
            CREATE TRIGGER fail_applied_event BEFORE INSERT ON run_events
            WHEN NEW.event_type = '{RunSteeringEventTypes.Applied}' BEGIN
                SELECT RAISE(ABORT, 'injected applied event failure');
            END;
            """);

        await Assert.ThrowsAsync<SqliteException>(() => repository.SaveCheckpointAsync(run,
            original with { RoundCount = 2, AppliedSteeringSequence = accepted.Sequence }));

        var persisted = (await Repository(context).GetCheckpointAsync(run))!;
        Assert.Equal(1, persisted.RoundCount);
        Assert.Equal(0, persisted.AppliedSteeringSequence);
        Assert.Single(await repository.GetPendingSteeringAsync(run));
        Assert.DoesNotContain(await repository.GetEventsAfterAsync(run, 0), item => item.Type == RunSteeringEventTypes.Applied);
    }

    [Theory]
    [InlineData(RunMode.General)]
    [InlineData(RunMode.Coding)]
    public async Task RecoveryQueuesTheSameRunAndPreservesAnUnappliedSteeringInput(RunMode mode)
    {
        using var context = new TestServerContext();
        var repository = Repository(context);
        var run = await CreateAsync(repository, mode);
        await repository.SaveCheckpointAsync(run, new AgentRunCheckpoint([new LmChatMessage("user", "Original")], 1, 0, 0, 0));
        var accepted = await repository.AcceptSteeringAsync(run, new("session-fixture", "input", "Nach Neustart fortsetzen"));
        var reopened = Repository(context);

        Assert.Contains(run, await reopened.RecoverAsync());

        Assert.Equal(RunState.Queued, (await reopened.GetAsync(run))!.State);
        Assert.Equal(accepted.Sequence, Assert.Single(await reopened.GetPendingSteeringAsync(run)).Sequence);
        Assert.NotNull(await reopened.GetCheckpointAsync(run));
        Assert.Single(await reopened.GetEventsAfterAsync(run, 0), item => item.Type == RunSteeringEventTypes.Accepted);
    }

    [Fact]
    public async Task AcceptedSteeringStopsModelGenerationButLeavesRunTokenAndAlreadyDispatchedToolIntact()
    {
        using var context = new TestServerContext();
        var repository = Repository(context);
        var run = await CreateAsync(repository);
        using var runCancellation = new CancellationTokenSource();
        Assert.True(await repository.TryJournalToolDispatchAsync(run, RunEventTypes.ServerToolStarted,
            new { name = "fixture-tool" }, runCancellation.Token));
        await using var steering = repository.WatchSteering(run, runCancellation.Token);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = steering.Token.Register(() => cancelled.TrySetResult());

        await repository.AcceptSteeringAsync(run, new("session-fixture", "input", "Neue Priorität"));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(steering.SteeringRequested);
        Assert.False(runCancellation.IsCancellationRequested);
        Assert.False(await repository.TryJournalToolDispatchAsync(run, RunEventTypes.ServerToolStarted,
            new { name = "stale-next-tool" }, runCancellation.Token));
        Assert.Single(await repository.GetEventsAfterAsync(run, 0), item => item.Type == RunEventTypes.ServerToolStarted);
    }

    private static RunRepository Repository(TestServerContext context) => new(context.Database, new RunEventNotifier());

    private static async Task<string> CreateAsync(RunRepository repository, RunMode mode = RunMode.Coding, RunState state = RunState.Running)
    {
        var created = await repository.CreateAsync(new RunRequest(GoAiProtocol.Version, mode,
            [new RunMessage("user", [new ContentPart("text", "Originale Aufgabe")])], SessionId: "session-fixture",
            PreferredCodingModelId: "coding/fixture", CodingOptions: new(WorkspacePath: "C:/fixture/project")), null);
        await repository.UpdateStateAsync(created.Snapshot.RunId, state);
        return created.Snapshot.RunId;
    }

    private static async Task ExecuteSqlAsync(TestServerContext context, string sql)
    {
        await using var connection = await context.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync();
    }
}
