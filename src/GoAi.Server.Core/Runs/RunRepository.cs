using GoAi.Contracts;
using GoAi.Server.Core.Data;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GoAi.Server.Core.Runs;

public sealed class RunRepository
{
    private readonly GoAiDatabase _database;
    private readonly RunEventNotifier _notifier;
    private readonly JsonSerializerOptions _checkpointJsonOptions;

    public RunRepository(GoAiDatabase database, RunEventNotifier notifier)
    {
        _database = database;
        _notifier = notifier;
        _checkpointJsonOptions = new JsonSerializerOptions(database.JsonOptions)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        };
    }

    public async Task<(RunSnapshot Snapshot, bool Created)> CreateAsync(
        RunRequest request,
        string? idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var existing = await FindByIdempotencyKeyAsync(idempotencyKey, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                await EnsureSameIdempotentRequestAsync(existing.RunId, request, cancellationToken).ConfigureAwait(false);
                return existing.State == RunState.Interrupted
                    ? await TryRestartInterruptedAsync(existing, cancellationToken).ConfigureAwait(false)
                    : (existing, false);
            }
        }

        var now = DateTimeOffset.UtcNow;
        var runId = $"run-{Guid.NewGuid():N}";
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO runs(
                run_id, idempotency_key, state, mode, request_json, created_at, updated_at)
            VALUES($id, $key, $state, $mode, $request, $created, $updated);
            """;
        command.Parameters.AddWithValue("$id", runId);
        command.Parameters.AddWithValue("$key", (object?)idempotencyKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$state", RunState.Queued.ToString());
        command.Parameters.AddWithValue("$mode", request.Mode.ToString());
        command.Parameters.AddWithValue("$request", JsonSerializer.Serialize(request, _database.JsonOptions));
        command.Parameters.AddWithValue("$created", GoAiDatabase.FormatTimestamp(now));
        command.Parameters.AddWithValue("$updated", GoAiDatabase.FormatTimestamp(now));
        try
        {
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19 && !string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var existing = await FindByIdempotencyKeyAsync(idempotencyKey, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                await EnsureSameIdempotentRequestAsync(existing.RunId, request, cancellationToken).ConfigureAwait(false);
                return existing.State == RunState.Interrupted
                    ? await TryRestartInterruptedAsync(existing, cancellationToken).ConfigureAwait(false)
                    : (existing, false);
            }

            throw;
        }

        return (new RunSnapshot(runId, RunState.Queued, request.Mode, null, null, 0, now, now), true);
    }

    private async Task EnsureSameIdempotentRequestAsync(
        string runId,
        RunRequest request,
        CancellationToken cancellationToken)
    {
        var existingRequest = await GetRequestAsync(runId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Idempotent run request disappeared from storage.");
        var existingJson = JsonSerializer.Serialize(existingRequest, _database.JsonOptions);
        var requestedJson = JsonSerializer.Serialize(request, _database.JsonOptions);
        if (!string.Equals(existingJson, requestedJson, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Idempotency-Key is already bound to a different run request.");
        }
    }

    private async Task<(RunSnapshot Snapshot, bool Created)> TryRestartInterruptedAsync(
        RunSnapshot existing,
        CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE runs
            SET state = $queued, error_code = NULL, updated_at = $updated
            WHERE run_id = $id AND state = $interrupted;
            """;
        command.Parameters.AddWithValue("$queued", RunState.Queued.ToString());
        command.Parameters.AddWithValue("$interrupted", RunState.Interrupted.ToString());
        command.Parameters.AddWithValue("$updated", GoAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", existing.RunId);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await GetAsync(existing.RunId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Interrupted run disappeared while restarting.");
        if (changed == 1)
        {
            _notifier.Notify(existing.RunId);
        }
        return (snapshot, changed == 1);
    }

    public async Task<RunRequest?> GetRequestAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT request_json FROM runs WHERE run_id = $id;";
        command.Parameters.AddWithValue("$id", runId);
        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return json is null ? null : DeserializeRequest(json, _database.JsonOptions);
    }

    public async Task<RunSnapshot?> GetAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.run_id, r.state, r.mode, r.selected_model, r.session_title,
                   COALESCE(MAX(e.id), 0), r.created_at, r.updated_at, r.error_code
            FROM runs r
            LEFT JOIN run_events e ON e.run_id = r.run_id
            WHERE r.run_id = $id
            GROUP BY r.run_id;
            """;
        command.Parameters.AddWithValue("$id", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadSnapshot(reader)
            : null;
    }

    public async Task<IReadOnlyList<RunEvent>> GetEventsAfterAsync(
        string runId,
        long lastEventId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, event_type, data_json, created_at
            FROM run_events
            WHERE run_id = $run AND id > $after
            ORDER BY id ASC;
            """;
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$after", lastEventId);
        var events = new List<RunEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            using var document = JsonDocument.Parse(reader.GetString(2));
            events.Add(new RunEvent(
                reader.GetInt64(0),
                runId,
                reader.GetString(1),
                GoAiDatabase.ParseTimestamp(reader.GetString(3)),
                document.RootElement.Clone()));
        }

        return events;
    }

    public async Task<RunEvent> AppendEventAsync<T>(
        string runId,
        string eventType,
        T data,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(data, _database.JsonOptions);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO run_events(run_id, event_type, data_json, created_at)
            VALUES($run, $type, $data, $created);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$type", eventType);
        command.Parameters.AddWithValue("$data", json);
        command.Parameters.AddWithValue("$created", GoAiDatabase.FormatTimestamp(now));
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        using var document = JsonDocument.Parse(json);
        var result = new RunEvent(id, runId, eventType, now, document.RootElement.Clone());
        _notifier.Notify(runId);
        return result;
    }

    internal async Task<bool> FinalizeConversationAsync(
        string runId, RunCompletedEvent completed, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE runs SET state = 'Completed', selected_model = COALESCE($model, selected_model),
                session_title = COALESCE($title, session_title), error_code = NULL, updated_at = $now
            WHERE run_id = $run AND state IN ('Queued', 'Running', 'WaitingForClient');
            """;
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$model", (object?)completed.ModelId ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", (object?)completed.SessionTitle ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", GoAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
        // A committed cancel/failure or an earlier completion is authoritative.
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1) return false;
        command.CommandText = """
            INSERT INTO run_events(run_id, event_type, data_json, created_at)
                VALUES($run, $type, $data, $now);
            DELETE FROM run_checkpoints WHERE run_id = $run;
            """;
        command.Parameters.AddWithValue("$type", RunEventTypes.RunCompleted);
        command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(completed, _database.JsonOptions));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        // None of state, completion receipt and checkpoint deletion is visible
        // on its own. Recovery can never restart a completed run without its checkpoint.
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _notifier.Notify(runId);
        return true;
    }

    internal async Task<RunEvent?> EnsureClientToolProposedEventAsync(
        string runId, string proposalId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$proposal", proposalId);
        command.Parameters.AddWithValue("$type", RunEventTypes.ClientToolProposed);
        command.CommandText = """
            SELECT id, data_json, created_at FROM run_events
            WHERE run_id = $run AND event_type = $type AND json_extract(data_json, '$.proposalId') = $proposal
            ORDER BY id LIMIT 1;
            """;
        RunEvent? published = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                published = new RunEvent(reader.GetInt64(0), runId, RunEventTypes.ClientToolProposed,
                    GoAiDatabase.ParseTimestamp(reader.GetString(2)), JsonSerializer.Deserialize<JsonElement>(reader.GetString(1)));
        }
        command.CommandText = """
            SELECT proposal.proposal_json FROM client_tool_proposals proposal
            JOIN runs run ON run.run_id = proposal.run_id
            JOIN run_checkpoints checkpoint ON checkpoint.run_id = run.run_id
            WHERE proposal.run_id = $run AND proposal.proposal_id = $proposal
              AND json_extract(checkpoint.checkpoint_json, '$.pendingProposalId') = $proposal
              AND run.state IN ('Queued', 'Running', 'WaitingForClient')
              AND NOT EXISTS(SELECT 1 FROM client_tool_results result WHERE result.proposal_id = $proposal);
            """;
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string proposalJson)
            return published;
        var now = DateTimeOffset.UtcNow;
        command.Parameters.AddWithValue("$now", GoAiDatabase.FormatTimestamp(now));
        command.CommandText = "UPDATE runs SET state = 'WaitingForClient', updated_at = $now WHERE run_id = $run;";
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (published is null)
        {
            command.CommandText = """
                INSERT INTO run_events(run_id, event_type, data_json, created_at)
                    VALUES($run, $type, $data, $now);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$data", proposalJson);
            var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            var proposal = JsonSerializer.Deserialize<ToolProposal>(proposalJson, _database.JsonOptions)
                ?? throw new InvalidDataException("The persisted client tool proposal is invalid.");
            published = new RunEvent(id, runId, RunEventTypes.ClientToolProposed, now, JsonSerializer.Deserialize<JsonElement>(proposalJson));
            command.CommandText = "INSERT INTO run_events(run_id, event_type, data_json, created_at) VALUES($run, $waiting, $waitingData, $now);";
            command.Parameters.AddWithValue("$waiting", RunEventTypes.RunWaitingForClient);
            command.Parameters.AddWithValue("$waitingData", JsonSerializer.Serialize(new
            {
                proposalId, tool = proposal.Name, expiresAt = proposal.ExpiresAt,
            }, _database.JsonOptions));
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _notifier.Notify(runId);
        return published;
    }

    public async Task UpdateStateAsync(
        string runId,
        RunState state,
        string? selectedModel = null,
        string? sessionTitle = null,
        string? errorCode = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE runs
            SET state = $state,
                selected_model = COALESCE($model, selected_model),
                session_title = COALESCE($title, session_title),
                error_code = $error,
                updated_at = $updated
            WHERE run_id = $id AND (state != 'Cancelled' OR $state = 'Cancelled');
            """;
        command.Parameters.AddWithValue("$state", state.ToString());
        command.Parameters.AddWithValue("$model", (object?)selectedModel ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", (object?)sessionTitle ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)errorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", GoAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", runId);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        _notifier.Notify(runId);
    }

    public async Task<IReadOnlyList<string>> RecoverAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var interrupt = connection.CreateCommand())
        {
            interrupt.Transaction = (SqliteTransaction)transaction;
            interrupt.CommandText = """
                UPDATE runs SET state = $interrupted, error_code = 'run.gateway_restarted', updated_at = $now
                WHERE state = $running AND mode != 'Coding';
                """;
            interrupt.Parameters.AddWithValue("$interrupted", RunState.Interrupted.ToString());
            interrupt.Parameters.AddWithValue("$running", RunState.Running.ToString());
            interrupt.Parameters.AddWithValue("$now", GoAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
            _ = await interrupt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var resumeCoding = connection.CreateCommand())
        {
            resumeCoding.Transaction = (SqliteTransaction)transaction;
            resumeCoding.CommandText = """
                UPDATE runs SET state = $queued, error_code = NULL, updated_at = $now
                WHERE mode = 'Coding' AND (
                    state = $running OR (state = $interrupted AND error_code IN ('run.gateway_stopped', 'run.gateway_restarted'))
                );
                """;
            resumeCoding.Parameters.AddWithValue("$queued", RunState.Queued.ToString());
            resumeCoding.Parameters.AddWithValue("$running", RunState.Running.ToString());
            resumeCoding.Parameters.AddWithValue("$interrupted", RunState.Interrupted.ToString());
            resumeCoding.Parameters.AddWithValue("$now", GoAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
            _ = await resumeCoding.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var retireRemovedModes = connection.CreateCommand())
        {
            retireRemovedModes.Transaction = (SqliteTransaction)transaction;
            retireRemovedModes.CommandText = """
                UPDATE runs
                SET state = $failed, error_code = 'run.mode_removed', updated_at = $now
                WHERE mode = 'Code' AND state IN ($queued, $running, $waiting, $interrupted);
                """;
            retireRemovedModes.Parameters.AddWithValue("$failed", RunState.Failed.ToString());
            retireRemovedModes.Parameters.AddWithValue("$queued", RunState.Queued.ToString());
            retireRemovedModes.Parameters.AddWithValue("$running", RunState.Running.ToString());
            retireRemovedModes.Parameters.AddWithValue("$waiting", RunState.WaitingForClient.ToString());
            retireRemovedModes.Parameters.AddWithValue("$interrupted", RunState.Interrupted.ToString());
            retireRemovedModes.Parameters.AddWithValue("$now", GoAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
            _ = await retireRemovedModes.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var queued = new List<string>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = (SqliteTransaction)transaction;
            select.CommandText = "SELECT run_id FROM runs WHERE state IN ($state, $waiting) ORDER BY created_at;";
            select.Parameters.AddWithValue("$state", RunState.Queued.ToString());
            select.Parameters.AddWithValue("$waiting", RunState.WaitingForClient.ToString());
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                queued.Add(reader.GetString(0));
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return queued;
    }

    public async Task<bool> SaveClientToolResultAsync(
        string runId,
        ClientToolResult result,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var validate = connection.CreateCommand())
        {
            validate.CommandText = """
                SELECT COUNT(*) FROM client_tool_proposals
                WHERE proposal_id = $proposal AND run_id = $run AND expires_at > $now;
                """;
            validate.Parameters.AddWithValue("$proposal", result.ProposalId);
            validate.Parameters.AddWithValue("$run", runId);
            validate.Parameters.AddWithValue("$now", GoAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
            var valid = Convert.ToInt64(await validate.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (valid != 1)
            {
                throw new InvalidOperationException("Client tool proposal is unknown, expired, or belongs to another run.");
            }
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO client_tool_results(proposal_id, run_id, result_json, created_at)
            VALUES($proposal, $run, $result, $created)
            ON CONFLICT(proposal_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$proposal", result.ProposalId);
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$result", JsonSerializer.Serialize(result, _database.JsonOptions));
        command.Parameters.AddWithValue("$created", GoAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
        var inserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        if (inserted)
        {
            _notifier.Notify(runId);
        }
        return inserted;
    }

    public async Task<bool> TryQueueClientToolContinuationAsync(
        string runId,
        string proposalId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE runs
            SET state = $queued, updated_at = $updated, error_code = NULL
            WHERE run_id = $run
              AND state IN ($running, $waiting)
              AND EXISTS (
                  SELECT 1
                  FROM run_checkpoints checkpoint
                  WHERE checkpoint.run_id = $run
                    AND json_extract(checkpoint.checkpoint_json, '$.pendingProposalId') = $proposal
              )
              AND EXISTS (
                  SELECT 1
                  FROM client_tool_results result
                  WHERE result.run_id = $run
                    AND result.proposal_id = $proposal
              );
            """;
        command.Parameters.AddWithValue("$queued", RunState.Queued.ToString());
        command.Parameters.AddWithValue("$running", RunState.Running.ToString());
        command.Parameters.AddWithValue("$waiting", RunState.WaitingForClient.ToString());
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$proposal", proposalId);
        command.Parameters.AddWithValue("$updated", GoAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task SaveToolProposalAsync(ToolProposal proposal, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO client_tool_proposals(proposal_id, run_id, name, proposal_json, expires_at, created_at)
            VALUES($proposal, $run, $name, $json, $expires, $created);
            """;
        command.Parameters.AddWithValue("$proposal", proposal.ProposalId);
        command.Parameters.AddWithValue("$run", proposal.RunId);
        command.Parameters.AddWithValue("$name", proposal.Name);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(proposal, _database.JsonOptions));
        command.Parameters.AddWithValue("$expires", GoAiDatabase.FormatTimestamp(proposal.ExpiresAt));
        command.Parameters.AddWithValue("$created", GoAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<string>> QueueExpiredWaitingRunsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // The conditional UPDATE claims each waiting run only once. Replayed client
        // results and this sweep share the same queue and cannot restart terminal runs.
        command.CommandText = """
            UPDATE runs
            SET state = $queued, updated_at = $now
            WHERE state = $waiting AND run_id IN (
                SELECT run.run_id
                FROM runs run
                LEFT JOIN run_checkpoints checkpoint ON checkpoint.run_id = run.run_id
                LEFT JOIN client_tool_proposals proposal
                  ON proposal.run_id = run.run_id
                 AND proposal.proposal_id = json_extract(checkpoint.checkpoint_json, '$.pendingProposalId')
                WHERE run.state = $waiting AND (
                    (COALESCE(json_extract(run.request_json, '$.limits.timeoutSeconds'), CASE WHEN run.mode = 'Coding' THEN 0 ELSE 1800 END) > 0
                     AND julianday(run.created_at)
                        + COALESCE(json_extract(run.request_json, '$.limits.timeoutSeconds'), CASE WHEN run.mode = 'Coding' THEN 0 ELSE 1800 END) / 86400.0 <= julianday($now))
                    OR (proposal.expires_at <= $now AND NOT EXISTS (
                        SELECT 1 FROM client_tool_results result WHERE result.proposal_id = proposal.proposal_id
                    ))
                )
                ORDER BY run.created_at
                LIMIT 64
            )
            RETURNING run_id;
            """;
        command.Parameters.AddWithValue("$queued", RunState.Queued.ToString());
        command.Parameters.AddWithValue("$waiting", RunState.WaitingForClient.ToString());
        command.Parameters.AddWithValue("$now", GoAiDatabase.FormatTimestamp(now));
        var runIds = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) runIds.Add(reader.GetString(0));
        return runIds;
    }

    internal async Task<bool> CancelAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE runs SET state = 'Cancelled', error_code = 'run.cancelled', updated_at = $now
            WHERE run_id = $run AND state IN ('Queued', 'Running', 'WaitingForClient');
            """;
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$now", GoAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0) return false;
        command.CommandText = """
            INSERT INTO run_events(run_id, event_type, data_json, created_at) VALUES($run, $event, '{"reason":"client"}', $now);
            DELETE FROM run_provider_retries WHERE run_id = $run;
            """;
        command.Parameters.AddWithValue("$event", RunEventTypes.RunCancelled);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _notifier.Notify(runId);
        return true;
    }

    internal async Task<DateTimeOffset?> GetProviderRetryTimeAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT next_attempt_at FROM run_provider_retries WHERE run_id = $run;";
        command.Parameters.AddWithValue("$run", runId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string timestamp
            ? GoAiDatabase.ParseTimestamp(timestamp) : null;
    }

    internal async Task<(long Attempt, DateTimeOffset NotBefore, TimeSpan Delay)?> ScheduleProviderRetryAsync(
        string runId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE runs SET state = 'Queued', error_code = NULL, updated_at = $now WHERE run_id = $run AND state IN ('Running', 'Queued');";
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$now", GoAiDatabase.FormatTimestamp(now));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0) return null;
        command.CommandText = "SELECT attempt FROM run_provider_retries WHERE run_id = $run;";
        var prior = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var attempt = prior is long value ? value + 1 : 1;
        var delay = TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Min(attempt - 1, 6))));
        var notBefore = now + delay;
        command.CommandText = """
            INSERT INTO run_provider_retries(run_id, attempt, next_attempt_at) VALUES($run, $attempt, $next)
            ON CONFLICT(run_id) DO UPDATE SET attempt = $attempt, next_attempt_at = $next;
            """;
        command.Parameters.AddWithValue("$attempt", attempt);
        command.Parameters.AddWithValue("$next", GoAiDatabase.FormatTimestamp(notBefore));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _notifier.Notify(runId);
        return (attempt, notBefore, delay);
    }

    internal async Task<IReadOnlyList<string>> QueueDueProviderRetriesAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE run_provider_retries SET next_attempt_at = NULL
            WHERE run_id IN (
                SELECT retry.run_id FROM run_provider_retries retry JOIN runs run ON run.run_id = retry.run_id
                WHERE run.state = 'Queued' AND retry.next_attempt_at <= $now
                ORDER BY retry.next_attempt_at LIMIT 64
            ) RETURNING run_id;
            """;
        command.Parameters.AddWithValue("$now", GoAiDatabase.FormatTimestamp(now));
        var runs = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) runs.Add(reader.GetString(0));
        return runs;
    }

    internal async Task ClearProviderRetryAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM run_provider_retries WHERE run_id = $run;";
        command.Parameters.AddWithValue("$run", runId);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ClientToolResult?> GetClientToolResultAsync(
        string proposalId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT result_json FROM client_tool_results WHERE proposal_id = $proposal;";
        command.Parameters.AddWithValue("$proposal", proposalId);
        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return json is null ? null : JsonSerializer.Deserialize<ClientToolResult>(json, _database.JsonOptions);
    }

    public async Task DeleteClientToolExchangeAsync(
        string runId,
        string proposalId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var result = connection.CreateCommand())
        {
            result.Transaction = transaction;
            result.CommandText = "DELETE FROM client_tool_results WHERE proposal_id = $proposal AND run_id = $run;";
            result.Parameters.AddWithValue("$proposal", proposalId);
            result.Parameters.AddWithValue("$run", runId);
            _ = await result.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var proposal = connection.CreateCommand())
        {
            proposal.Transaction = transaction;
            proposal.CommandText = "DELETE FROM client_tool_proposals WHERE proposal_id = $proposal AND run_id = $run;";
            proposal.Parameters.AddWithValue("$proposal", proposalId);
            proposal.Parameters.AddWithValue("$run", runId);
            _ = await proposal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ToolProposal?> GetToolProposalAsync(
        string proposalId,
        string runId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT proposal_json FROM client_tool_proposals
            WHERE proposal_id = $proposal AND run_id = $run;
            """;
        command.Parameters.AddWithValue("$proposal", proposalId);
        command.Parameters.AddWithValue("$run", runId);
        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return json is null ? null : JsonSerializer.Deserialize<ToolProposal>(json, _database.JsonOptions);
    }

    public async Task SaveCheckpointAsync(
        string runId,
        AgentRunCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO run_checkpoints(run_id, checkpoint_json, updated_at)
            VALUES($run, $json, $updated)
            ON CONFLICT(run_id) DO UPDATE SET
                checkpoint_json = excluded.checkpoint_json,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(checkpoint, _database.JsonOptions));
        command.Parameters.AddWithValue("$updated", GoAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentRunCheckpoint?> GetCheckpointAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT checkpoint_json FROM run_checkpoints WHERE run_id = $run;";
        command.Parameters.AddWithValue("$run", runId);
        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return json is null
            ? null
            : JsonSerializer.Deserialize<AgentRunCheckpoint>(json, _checkpointJsonOptions);
    }

    public async Task DeleteCheckpointAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM run_checkpoints WHERE run_id = $run;";
        command.Parameters.AddWithValue("$run", runId);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<RunSnapshot?> FindByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.run_id, r.state, r.mode, r.selected_model, r.session_title,
                   COALESCE(MAX(e.id), 0), r.created_at, r.updated_at, r.error_code
            FROM runs r
            LEFT JOIN run_events e ON e.run_id = r.run_id
            WHERE r.idempotency_key = $key
            GROUP BY r.run_id;
            """;
        command.Parameters.AddWithValue("$key", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadSnapshot(reader)
            : null;
    }

    private static RunSnapshot ReadSnapshot(SqliteDataReader reader) => new(
        reader.GetString(0),
        Enum.Parse<RunState>(reader.GetString(1), ignoreCase: false),
        ReadRunMode(reader.GetString(2)),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetInt64(5),
        GoAiDatabase.ParseTimestamp(reader.GetString(6)),
        GoAiDatabase.ParseTimestamp(reader.GetString(7)),
        reader.IsDBNull(8) ? null : reader.GetString(8));

    private static RunMode ReadRunMode(string value) =>
        string.Equals(value, "Code", StringComparison.OrdinalIgnoreCase)
            ? RunMode.General
            : Enum.Parse<RunMode>(value, ignoreCase: false);

    private static RunRequest? DeserializeRequest(string json, JsonSerializerOptions options)
    {
        try
        {
            return JsonSerializer.Deserialize<RunRequest>(json, options);
        }
        catch (JsonException)
        {
            var root = JsonNode.Parse(json)?.AsObject();
            if (root is null
                || !string.Equals(root["mode"]?.GetValue<string>(), "code", StringComparison.OrdinalIgnoreCase))
            {
                throw;
            }

            root["mode"] = "general";
            // Legacy coding payloads may still contain this field; normalize it away while recovering stored runs.
            _ = root.Remove("preferredCodeModelId");
            return JsonSerializer.Deserialize<RunRequest>(root.ToJsonString(), options);
        }
    }

}
