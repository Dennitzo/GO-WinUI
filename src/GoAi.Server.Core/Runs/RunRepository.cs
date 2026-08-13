using GoAi.Contracts;
using GoAi.Server.Core.Data;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace GoAi.Server.Core.Runs;

public sealed class RunRepository
{
    private readonly GoAiDatabase _database;
    private readonly RunEventNotifier _notifier;

    public RunRepository(GoAiDatabase database, RunEventNotifier notifier)
    {
        _database = database;
        _notifier = notifier;
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
        return json is null ? null : JsonSerializer.Deserialize<RunRequest>(json, _database.JsonOptions);
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
            WHERE run_id = $id;
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
                WHERE state = $running;
                """;
            interrupt.Parameters.AddWithValue("$interrupted", RunState.Interrupted.ToString());
            interrupt.Parameters.AddWithValue("$running", RunState.Running.ToString());
            interrupt.Parameters.AddWithValue("$now", GoAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
            _ = await interrupt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    public async Task SaveClientToolResultAsync(
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
            ON CONFLICT(proposal_id) DO UPDATE SET result_json = excluded.result_json;
            """;
        command.Parameters.AddWithValue("$proposal", result.ProposalId);
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$result", JsonSerializer.Serialize(result, _database.JsonOptions));
        command.Parameters.AddWithValue("$created", GoAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        _notifier.Notify(runId);
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
        return json is null ? null : JsonSerializer.Deserialize<AgentRunCheckpoint>(json, _database.JsonOptions);
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
        Enum.Parse<RunMode>(reader.GetString(2), ignoreCase: false),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetInt64(5),
        GoAiDatabase.ParseTimestamp(reader.GetString(6)),
        GoAiDatabase.ParseTimestamp(reader.GetString(7)),
        reader.IsDBNull(8) ? null : reader.GetString(8));
}
