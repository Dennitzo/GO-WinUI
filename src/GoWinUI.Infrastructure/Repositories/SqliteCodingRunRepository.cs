using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using GoWinUI.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace GoWinUI.Infrastructure.Repositories;

public sealed class SqliteCodingRunRepository(SqliteDatabase database) : ICodingRunRepository
{
    public async Task<CodingRunTraceEntry> AppendAsync(
        Guid localRunId,
        string? serverRunId,
        Guid sessionId,
        Guid messageId,
        CodingRunTraceEntry entry,
        CancellationToken cancellationToken = default)
    {
        await database.WriteAsync(async (connection, transaction, token) =>
        {
            await UpsertRunAndEntryAsync(
                connection, transaction, localRunId, serverRunId, sessionId, messageId, entry, token)
                .ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return entry;
    }

    public async Task<IReadOnlyList<CodingRunTraceEntry>> ListForMessageAsync(
        Guid messageId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT entry.sequence,entry.timestamp,entry.stage,entry.status,entry.title,entry.detail,
                   entry.tool,entry.target,entry.duration_milliseconds,entry.server_event_id,
                   entry.process_operation_id,entry.process_command,entry.process_working_directory,
                   entry.process_purpose,entry.process_status,entry.process_exit_code,
                   entry.process_stdout,entry.process_stderr
            FROM coding_run_entries entry
            JOIN coding_runs run ON run.id=entry.run_id
            WHERE run.message_id=$message
            ORDER BY run.updated_at DESC,entry.sequence;
            """;
        command.Parameters.AddWithValue("$message", messageId.ToString("D"));
        return await ReadEntriesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CodingRunSnapshot?> GetLatestForSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,local_run_id,server_run_id,session_id,message_id,status,code_diff,
                   started_at,updated_at,revision
            FROM coding_runs
            WHERE session_id=$session
            ORDER BY CASE status WHEN 'running' THEN 0 ELSE 1 END,updated_at DESC,id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        return await ReadSnapshotAsync(connection, command, cancellationToken).ConfigureAwait(false);
    }

    public Task SetCodeDiffAsync(
        Guid localRunId,
        string? codeDiff,
        CancellationToken cancellationToken = default) =>
        database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE coding_runs
                SET code_diff=$diff,updated_at=$now,revision=revision+1
                WHERE local_run_id=$local;
                UPDATE chat_messages
                SET code_diff=$diff,updated_at=$now,revision=revision+1
                WHERE id=(SELECT message_id FROM coding_runs WHERE local_run_id=$local);
                UPDATE chat_sessions
                SET conversation_revision=conversation_revision+1,updated_at=$now
                WHERE id=(SELECT session_id FROM coding_runs WHERE local_run_id=$local);
                """;
            command.Parameters.AddWithValue("$local", localRunId.ToString("D"));
            command.Parameters.AddWithValue("$diff", string.IsNullOrWhiteSpace(codeDiff) ? DBNull.Value : codeDiff);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToDb());
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    public Task<int> MarkRunningInterruptedAsync(CancellationToken cancellationToken = default) =>
        database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            const string predicate = """
                status='running'
                AND NOT EXISTS(
                    SELECT 1 FROM go_ai_runs active
                    WHERE active.assistant_message_id=coding_runs.message_id
                      AND active.server_run_id IS NOT NULL
                      AND active.state IN ('queued','running','waitingForClient')
                )
                """;
            command.CommandText = $"SELECT COUNT(*) FROM coding_runs WHERE {predicate};";
            var count = Convert.ToInt32(
                await command.ExecuteScalarAsync(token).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            if (count == 0)
            {
                return 0;
            }

            command.CommandText = $"""
                UPDATE chat_sessions
                SET conversation_revision=conversation_revision+1,updated_at=$now
                WHERE id IN (SELECT DISTINCT session_id FROM coding_runs WHERE {predicate});
                UPDATE coding_runs
                SET status='interrupted',revision=revision+1,updated_at=$now
                WHERE {predicate};
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToDb());
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            return count;
        }, cancellationToken);

    public async Task ImportAsync(
        Guid localRunId,
        string? serverRunId,
        Guid sessionId,
        Guid messageId,
        IReadOnlyList<CodingRunTraceEntry> entries,
        CancellationToken cancellationToken = default)
    {
        var orderedEntries = entries.OrderBy(static value => value.Sequence).ToArray();
        var existing = await GetByLocalRunIdAsync(localRunId, cancellationToken).ConfigureAwait(false);
        if (existing is not null
            && existing.SessionId == sessionId
            && existing.MessageId == messageId
            && existing.Entries.SequenceEqual(orderedEntries))
        {
            return;
        }

        await database.WriteAsync(async (connection, transaction, token) =>
        {
            foreach (var entry in orderedEntries)
            {
                await UpsertRunAndEntryAsync(
                    connection, transaction, localRunId, serverRunId, sessionId, messageId, entry, token)
                    .ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CodingRunSnapshot?> GetByLocalRunIdAsync(Guid localRunId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,local_run_id,server_run_id,session_id,message_id,status,code_diff,
                   started_at,updated_at,revision
            FROM coding_runs WHERE local_run_id=$local;
            """;
        command.Parameters.AddWithValue("$local", localRunId.ToString("D"));
        return await ReadSnapshotAsync(connection, command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertRunAndEntryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid localRunId,
        string? serverRunId,
        Guid sessionId,
        Guid messageId,
        CodingRunTraceEntry entry,
        CancellationToken cancellationToken)
    {
        var runStatus = string.Equals(entry.Stage, "run", StringComparison.OrdinalIgnoreCase)
            ? entry.Status
            : "running";
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO coding_runs(
                id,local_run_id,server_run_id,session_id,message_id,status,started_at,updated_at,revision)
            VALUES($id,$local,$server,$session,$message,$runStatus,$started,$updated,1)
            ON CONFLICT(local_run_id) DO UPDATE SET
                server_run_id=COALESCE(excluded.server_run_id,coding_runs.server_run_id),
                session_id=excluded.session_id,
                message_id=excluded.message_id,
                status=CASE WHEN $isRun=1 THEN excluded.status ELSE coding_runs.status END,
                updated_at=excluded.updated_at,
                revision=coding_runs.revision+1;

            INSERT INTO coding_run_entries(
                run_id,sequence,timestamp,stage,status,title,detail,tool,target,duration_milliseconds,
                server_event_id,process_operation_id,process_command,process_working_directory,
                process_purpose,process_status,process_exit_code,process_stdout,process_stderr)
            VALUES(
                $id,$sequence,$timestamp,$stage,$entryStatus,$title,$detail,$tool,$target,$duration,
                $event,$operation,$command,$workingDirectory,$purpose,$processStatus,$exitCode,$stdout,$stderr)
            ON CONFLICT(run_id,sequence) DO UPDATE SET
                timestamp=excluded.timestamp,stage=excluded.stage,status=excluded.status,title=excluded.title,
                detail=excluded.detail,tool=excluded.tool,target=excluded.target,
                duration_milliseconds=excluded.duration_milliseconds,server_event_id=excluded.server_event_id,
                process_operation_id=excluded.process_operation_id,process_command=excluded.process_command,
                process_working_directory=excluded.process_working_directory,process_purpose=excluded.process_purpose,
                process_status=excluded.process_status,process_exit_code=excluded.process_exit_code,
                process_stdout=excluded.process_stdout,process_stderr=excluded.process_stderr;

            UPDATE chat_sessions
            SET conversation_revision=conversation_revision+1,updated_at=$updated
            WHERE id=$session;
            """;
        command.Parameters.AddWithValue("$id", localRunId.ToString("D"));
        command.Parameters.AddWithValue("$local", localRunId.ToString("D"));
        command.Parameters.AddWithValue("$server", (object?)serverRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        command.Parameters.AddWithValue("$message", messageId.ToString("D"));
        command.Parameters.AddWithValue("$runStatus", runStatus);
        command.Parameters.AddWithValue("$isRun", string.Equals(entry.Stage, "run", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
        command.Parameters.AddWithValue("$started", entry.Timestamp.ToDb());
        command.Parameters.AddWithValue("$updated", entry.Timestamp.ToDb());
        command.Parameters.AddWithValue("$sequence", entry.Sequence);
        command.Parameters.AddWithValue("$timestamp", entry.Timestamp.ToDb());
        command.Parameters.AddWithValue("$stage", entry.Stage);
        command.Parameters.AddWithValue("$entryStatus", entry.Status);
        command.Parameters.AddWithValue("$title", entry.Title);
        command.Parameters.AddWithValue("$detail", (object?)entry.Detail ?? DBNull.Value);
        command.Parameters.AddWithValue("$tool", (object?)entry.Tool ?? DBNull.Value);
        command.Parameters.AddWithValue("$target", (object?)entry.Target ?? DBNull.Value);
        command.Parameters.AddWithValue("$duration", (object?)entry.DurationMilliseconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$event", (object?)entry.ServerEventId ?? DBNull.Value);
        command.Parameters.AddWithValue("$operation", (object?)entry.ProcessConsole?.OperationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$command", (object?)entry.ProcessConsole?.Command ?? DBNull.Value);
        command.Parameters.AddWithValue("$workingDirectory", (object?)entry.ProcessConsole?.WorkingDirectory ?? DBNull.Value);
        command.Parameters.AddWithValue("$purpose", (object?)entry.ProcessConsole?.Purpose ?? DBNull.Value);
        command.Parameters.AddWithValue("$processStatus", (object?)entry.ProcessConsole?.Status ?? DBNull.Value);
        command.Parameters.AddWithValue("$exitCode", (object?)entry.ProcessConsole?.ExitCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$stdout", (object?)entry.ProcessConsole?.StandardOutput ?? DBNull.Value);
        command.Parameters.AddWithValue("$stderr", (object?)entry.ProcessConsole?.StandardError ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<CodingRunSnapshot?> ReadSnapshotAsync(
        SqliteConnection connection,
        SqliteCommand command,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        string? id = null;
        Guid localRunId = default;
        string? serverRunId = null;
        Guid sessionId = default;
        Guid? messageId = null;
        string status = "completed";
        string? codeDiff = null;
        DateTimeOffset startedAt = default;
        DateTimeOffset updatedAt = default;
        long revision = 1;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
            id = reader.GetString(0);
            localRunId = reader.ReadGuid(1);
            serverRunId = reader.IsDBNull(2) ? null : reader.GetString(2);
            sessionId = reader.ReadGuid(3);
            messageId = reader.IsDBNull(4) ? null : reader.ReadGuid(4);
            status = reader.GetString(5);
            codeDiff = reader.IsDBNull(6) ? null : reader.GetString(6);
            startedAt = reader.ReadDate(7);
            updatedAt = reader.ReadDate(8);
            revision = reader.GetInt64(9);
        }

        await using var entriesCommand = connection.CreateCommand();
        entriesCommand.Transaction = transaction;
        entriesCommand.CommandText = """
            SELECT sequence,timestamp,stage,status,title,detail,tool,target,duration_milliseconds,server_event_id,
                   process_operation_id,process_command,process_working_directory,process_purpose,process_status,
                   process_exit_code,process_stdout,process_stderr
            FROM coding_run_entries WHERE run_id=$run ORDER BY sequence;
            """;
        entriesCommand.Parameters.AddWithValue("$run", id);
        var entries = await ReadEntriesAsync(entriesCommand, cancellationToken).ConfigureAwait(false);
        return new CodingRunSnapshot(
            Guid.Parse(id), localRunId, serverRunId, sessionId, messageId, status, codeDiff,
            startedAt, updatedAt, revision, entries);
    }

    private static async Task<IReadOnlyList<CodingRunTraceEntry>> ReadEntriesAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var result = new List<CodingRunTraceEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var process = reader.IsDBNull(10)
                ? null
                : new CodingProcessConsole(
                    reader.GetString(10),
                    reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
                    reader.IsDBNull(12) ? "." : reader.GetString(12),
                    reader.IsDBNull(13) ? "inspect" : reader.GetString(13),
                    reader.IsDBNull(14) ? "completed" : reader.GetString(14),
                    reader.IsDBNull(15) ? null : reader.GetInt32(15),
                    reader.IsDBNull(16) ? null : reader.GetString(16),
                    reader.IsDBNull(17) ? null : reader.GetString(17));
            result.Add(new CodingRunTraceEntry(
                reader.GetInt64(0), reader.ReadDate(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetInt64(8),
                reader.IsDBNull(9) ? null : reader.GetInt64(9),
                process));
        }
        return result;
    }
}
