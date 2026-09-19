using GoAi.Contracts;
using GoAi.Server.Core.Data;
using System.Globalization;
using System.Text.Json;

namespace GoAi.Server.Core.Runs;

public sealed partial class RunRepository
{
    private readonly RunEventNotifier _steeringNotifier = new();
    public async Task<RunSteeringAccepted> AcceptSteeringAsync(string runId, RunSteeringRequest input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.SessionId) || input.SessionId.Length > 256
            || string.IsNullOrWhiteSpace(input.InputId) || input.InputId.Length > 128
            || string.IsNullOrWhiteSpace(input.Text) || input.Text.Length > 128_000)
            throw new ArgumentException("Umlenkung benötigt sessionId, eine eindeutige inputId und Text (höchstens 128000 Zeichen).");
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        // An immediate write transaction serializes acceptance with terminal completion.
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$input", input.InputId);
        command.CommandText = "SELECT state, request_json FROM runs WHERE run_id = $run;";
        string state;
        RunRequest request;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new KeyNotFoundException("Run not found.");
            state = reader.GetString(0);
            request = DeserializeRequest(reader.GetString(1), _database.JsonOptions)
                ?? throw new InvalidDataException("Persistierte Laufanfrage ist ungültig.");
        }
        if (!string.Equals(request.SessionId, input.SessionId, StringComparison.Ordinal))
            throw new InvalidOperationException("Die Umlenkung gehört nicht zur Sitzung dieses Laufs.");
        command.CommandText = "SELECT sequence, text, applied_at FROM run_steering_inputs WHERE run_id = $run AND input_id = $input;";
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!string.Equals(reader.GetString(1), input.Text, StringComparison.Ordinal))
                    throw new InvalidOperationException("inputId ist bereits an einen anderen Umlenkungstext gebunden.");
                if (reader.IsDBNull(2)) _steeringNotifier.Notify(runId);
                return new(runId, input.InputId, reader.GetInt64(0), reader.IsDBNull(2) ? "accepted" : "applied", true);
            }
        }
        if (state is not ("Queued" or "Running" or "WaitingForClient"))
            throw new InvalidOperationException("Dieser Lauf ist bereits beendet und kann nicht mehr umgelenkt werden.");
        if (request.Workload?.Kind is RunWorkloadKind.ImageGeneration or RunWorkloadKind.MediaAnalysis)
            throw new InvalidOperationException("Nur General- und Coding-Konversationen können umgelenkt werden.");
        command.Parameters.AddWithValue("$session", input.SessionId);
        command.Parameters.AddWithValue("$text", input.Text);
        command.Parameters.AddWithValue("$now", GoAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
        command.CommandText = """
            INSERT INTO run_steering_inputs(run_id, input_id, session_id, text, accepted_at)
            VALUES($run, $input, $session, $text, $now);
            SELECT last_insert_rowid();
            """;
        var sequence = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        command.CommandText = "INSERT INTO run_events(run_id, event_type, data_json, created_at) VALUES($run, $type, $data, $now);";
        command.Parameters.AddWithValue("$type", RunSteeringEventTypes.Accepted);
        command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(new RunSteeringEvent(input.InputId, sequence, input.SessionId, input.Text), _database.JsonOptions));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _notifier.Notify(runId);
        _steeringNotifier.Notify(runId);
        return new(runId, input.InputId, sequence, "accepted", false);
    }

    internal async Task<IReadOnlyList<RunSteeringEvent>> GetPendingSteeringAsync(string runId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT input_id, sequence, session_id, text FROM run_steering_inputs WHERE run_id = $run AND applied_at IS NULL ORDER BY sequence;";
        command.Parameters.AddWithValue("$run", runId);
        List<RunSteeringEvent> result = [];
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3)));
        return result;
    }

    internal async Task<bool> HasPendingSteeringAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM run_steering_inputs WHERE run_id = $run AND applied_at IS NULL);";
        command.Parameters.AddWithValue("$run", runId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
    }

    internal RunSteeringCancellation WatchSteering(string runId, CancellationToken cancellationToken) => new(this, _steeringNotifier, runId, cancellationToken);

    internal async Task<string> GetInterruptedResearchReceiptAsync(string runId, long afterEventId, CancellationToken token)
    {
        var journal = await GetEventsAfterAsync(runId, afterEventId, token).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { status = "interrupted", errorCode = "run.steered",
            message = "Recherche durch neue Nutzereingabe umgelenkt. Bereits abgeschlossene Werkzeuge und Belege:",
            completedTools = journal.Where(item => item.Type == RunEventTypes.ServerToolCompleted).Select(item => item.Data) }, _database.JsonOptions);
    }

    // Dispatch and acceptance have one ordering in SQLite. If acceptance wins, no
    // stale planned operation is emitted. If dispatch wins, its result must be drained.
    internal async Task<bool> TryJournalToolDispatchAsync<T>(string runId, string eventType, T data,
        CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO run_events(run_id, event_type, data_json, created_at)
            SELECT $run, $type, $data, $now WHERE NOT EXISTS (
                SELECT 1 FROM run_steering_inputs WHERE run_id = $run AND applied_at IS NULL)
                AND EXISTS(SELECT 1 FROM runs WHERE run_id = $run AND state IN ('Queued','Running','WaitingForClient'));
            """;
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$type", eventType);
        command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(data, _database.JsonOptions));
        command.Parameters.AddWithValue("$now", GoAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
        var accepted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (accepted) _notifier.Notify(runId);
        return accepted;
    }
}

/// <summary>Only the current model call is cancelled; tools use the owning run token.</summary>
internal sealed class RunSteeringCancellation : IAsyncDisposable
{
    private readonly CancellationTokenSource _model;
    private readonly CancellationTokenSource _watch;
    private readonly Task _watchTask;
    public CancellationToken Token => _model.Token;
    public bool SteeringRequested { get; private set; }

    internal RunSteeringCancellation(RunRepository repository, RunEventNotifier notifier, string runId, CancellationToken token)
    {
        _model = CancellationTokenSource.CreateLinkedTokenSource(token);
        _watch = CancellationTokenSource.CreateLinkedTokenSource(token);
        _watchTask = WatchAsync(repository, notifier, runId);
    }

    private async Task WatchAsync(RunRepository repository, RunEventNotifier notifier, string runId)
    {
        using var subscription = notifier.Subscribe(runId);
        try
        {
            while (true)
            {
                if (await repository.HasPendingSteeringAsync(runId, _watch.Token).ConfigureAwait(false))
                {
                    SteeringRequested = true;
                    await _model.CancelAsync().ConfigureAwait(false);
                    return;
                }
                _ = await subscription.Reader.ReadAsync(_watch.Token).ConfigureAwait(false);
                // Token streaming generates many notifications; only acceptance matters.
                while (subscription.Reader.TryRead(out _)) { }
            }
        }
        catch (OperationCanceledException) when (_watch.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _watch.CancelAsync().ConfigureAwait(false);
        try { await _watchTask.ConfigureAwait(false); }
        finally { _watch.Dispose(); _model.Dispose(); }
    }
}
