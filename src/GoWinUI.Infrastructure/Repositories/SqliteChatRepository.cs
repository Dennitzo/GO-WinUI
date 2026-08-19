using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using GoWinUI.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace GoWinUI.Infrastructure.Repositories;

public sealed class SqliteChatRepository(SqliteDatabase database) : IChatRepository
{
    public async Task<IReadOnlyList<ChatSession>> ListSessionsAsync(string? search = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,title,created_at,updated_at,selected_workflow_id,draft,assistant_mode,workspace_path,workspace_fingerprint,is_pinned,pinned_at,persistent_tool_action
            FROM chat_sessions
            WHERE $search='' OR rowid IN (SELECT rowid FROM session_search WHERE session_search MATCH $fts)
            ORDER BY is_pinned DESC, updated_at DESC;
            """;
        command.Parameters.AddWithValue("$search", search?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$fts", SqliteMapping.ToFtsQuery(search));
        var result = new List<ChatSession>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadSession(reader));
        }

        return result;
    }

    public async Task<ChatSession?> GetSessionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,title,created_at,updated_at,selected_workflow_id,draft,assistant_mode,workspace_path,workspace_fingerprint,is_pinned,pinned_at,persistent_tool_action FROM chat_sessions WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadSession(reader) : null;
    }

    public async Task<ChatSession> CreateSessionAsync(string title, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var now = DateTimeOffset.UtcNow;
        var session = new ChatSession(Guid.NewGuid(), title.Trim(), now, now);
        await database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO chat_sessions(id,title,created_at,updated_at) VALUES($id,$title,$now,$now);";
            command.Parameters.AddWithValue("$id", session.Id.ToString("D"));
            command.Parameters.AddWithValue("$title", session.Title);
            command.Parameters.AddWithValue("$now", now.ToDb());
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return session;
    }

    public Task RenameSessionAsync(Guid id, string title, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        return UpdateSessionAsync(id, "title=$value", title.Trim(), cancellationToken);
    }

    public Task SaveDraftAsync(Guid id, string draft, CancellationToken cancellationToken = default) =>
        UpdateSessionAsync(id, "draft=$value", draft ?? string.Empty, cancellationToken);

    public Task SelectWorkflowAsync(Guid id, Guid? workflowId, CancellationToken cancellationToken = default) =>
        UpdateSessionAsync(id, "selected_workflow_id=$value", workflowId?.ToString("D"), cancellationToken);

    public Task SetAssistantContextAsync(
        Guid id,
        AssistantMode mode,
        string? workspacePath,
        string? workspaceFingerprint,
        CancellationToken cancellationToken = default) =>
        database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE chat_sessions
                SET assistant_mode=$mode,
                    workspace_path=$workspace,
                    workspace_fingerprint=$fingerprint,
                    updated_at=$now
                WHERE id=$id;
                """;
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$mode", SqliteMapping.EnumName(mode));
            command.Parameters.AddWithValue("$workspace", (object?)workspacePath ?? DBNull.Value);
            command.Parameters.AddWithValue("$fingerprint", (object?)workspaceFingerprint ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToDb());
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    public Task SetPinnedAsync(Guid id, bool isPinned, CancellationToken cancellationToken = default) =>
        database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE chat_sessions SET is_pinned=$pinned,pinned_at=$at,updated_at=$now WHERE id=$id;";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$pinned", isPinned ? 1 : 0);
            command.Parameters.AddWithValue("$at", isPinned ? DateTimeOffset.UtcNow.ToDb() : DBNull.Value);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToDb());
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    public Task DeleteSessionAsync(Guid id, CancellationToken cancellationToken = default) => database.WriteAsync(async (connection, transaction, token) =>
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT blob_id FROM documents WHERE session_id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        var blobIds = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(token).ConfigureAwait(false)) blobIds.Add(reader.GetString(0));
        }
        command.CommandText = "DELETE FROM chat_sessions WHERE id=$id;";
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        foreach (var blobId in blobIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            command.Parameters.Clear();
            command.CommandText = """
                DELETE FROM binary_objects WHERE id=$id
                  AND NOT EXISTS(SELECT 1 FROM documents WHERE blob_id=$id)
                  AND NOT EXISTS(SELECT 1 FROM project_assets WHERE blob_id=$id)
                  AND NOT EXISTS(SELECT 1 FROM project_asset_thumbnails WHERE blob_id=$id);
                """;
            command.Parameters.AddWithValue("$id", blobId);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
    }, cancellationToken);

    public async Task<IReadOnlyList<ChatMessage>> ListMessagesAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,session_id,role,content,status,created_at,updated_at,error,tool_name,tool_context,tool_status,tool_detail,tool_provider,context_summary,content_profile
            FROM chat_messages WHERE session_id=$id ORDER BY created_at,id;
            """;
        command.Parameters.AddWithValue("$id", sessionId.ToString("D"));
        var result = new List<ChatMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadMessage(reader));
        }

        return result;
    }

    public async Task<ChatMessage> AddMessageAsync(
        Guid sessionId,
        ChatRole role,
        string content,
        MessageStatus status,
        MessageContentProfile contentProfile = MessageContentProfile.General,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var message = new ChatMessage(
            Guid.NewGuid(), sessionId, role, content ?? string.Empty, status, now, now,
            ContentProfile: contentProfile);
        await database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO chat_messages(id,session_id,role,content,status,created_at,updated_at,content_profile)
                VALUES($id,$session,$role,$content,$status,$now,$now,$profile);
                UPDATE chat_sessions SET updated_at=$now WHERE id=$session;
                """;
            command.Parameters.AddWithValue("$id", message.Id.ToString("D"));
            command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
            command.Parameters.AddWithValue("$role", SqliteMapping.EnumName(role));
            command.Parameters.AddWithValue("$content", message.Content);
            command.Parameters.AddWithValue("$status", SqliteMapping.EnumName(status));
            command.Parameters.AddWithValue("$profile", SqliteMapping.EnumName(contentProfile));
            command.Parameters.AddWithValue("$now", now.ToDb());
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return message;
    }

    public Task UpdateMessageAsync(Guid messageId, string content, MessageStatus status, string? errorMessage = null, CancellationToken cancellationToken = default) =>
        database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE chat_messages SET content=$content,status=$status,error=$error,updated_at=$now WHERE id=$id;
                UPDATE chat_sessions SET updated_at=$now WHERE id=(SELECT session_id FROM chat_messages WHERE id=$id);
                """;
            command.Parameters.AddWithValue("$id", messageId.ToString("D"));
            command.Parameters.AddWithValue("$content", content ?? string.Empty);
            command.Parameters.AddWithValue("$status", SqliteMapping.EnumName(status));
            command.Parameters.AddWithValue("$error", (object?)errorMessage ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToDb());
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    public Task SetToolExecutionAsync(Guid messageId, ToolExecutionInfo execution, CancellationToken cancellationToken = default) =>
        database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE chat_messages SET tool_name=$name,tool_context=$context,tool_status=$status,tool_detail=$detail,tool_provider=$provider,updated_at=$now WHERE id=$id;";
            command.Parameters.AddWithValue("$id", messageId.ToString("D"));
            command.Parameters.AddWithValue("$name", execution.Tool);
            command.Parameters.AddWithValue("$context", execution.Context);
            command.Parameters.AddWithValue("$status", execution.Status);
            command.Parameters.AddWithValue("$detail", (object?)execution.Detail ?? DBNull.Value);
            command.Parameters.AddWithValue("$provider", (object?)execution.Provider ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToDb());
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    public Task SetMessageContextSummaryAsync(Guid messageId, string contextSummary, CancellationToken cancellationToken = default) =>
        database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE chat_messages SET context_summary=$summary,updated_at=$now WHERE id=$id;";
            command.Parameters.AddWithValue("$id", messageId.ToString("D"));
            command.Parameters.AddWithValue("$summary", contextSummary.Trim());
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToDb());
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    public Task SetPersistentToolActionAsync(
        Guid id,
        PersistentToolAction? action,
        CancellationToken cancellationToken = default) =>
        database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE chat_sessions
                SET persistent_tool_action=$action,
                    assistant_mode=$mode,
                    updated_at=$now
                WHERE id=$id;
                """;
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$action", action is null ? DBNull.Value : SqliteMapping.EnumName(action.Value));
            command.Parameters.AddWithValue("$mode", SqliteMapping.EnumName(action == PersistentToolAction.Code
                ? AssistantMode.Code
                : AssistantMode.General));
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToDb());
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    public async Task<SessionContextPreparation?> GetSessionContextPreparationAsync(
        string cacheKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT cache_key,session_id,history_revision,model_id,context_budget,
                   through_message_id,message_count,prepared_text,created_at,profile
            FROM session_context_preparations
            WHERE cache_key=$key;
            """;
        command.Parameters.AddWithValue("$key", cacheKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new SessionContextPreparation(
                reader.GetString(0),
                reader.ReadGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.ReadGuid(5),
                reader.GetInt32(6),
                reader.GetString(7),
                reader.ReadDate(8),
                reader.ReadEnum<SessionContextProfile>(9))
            : null;
    }

    public async Task<IReadOnlyList<SessionContextPreparation>> ListSessionContextPreparationsAsync(
        Guid sessionId,
        string modelId,
        int maximumMessageCount,
        SessionContextProfile profile = SessionContextProfile.General,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumMessageCount);
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT cache_key,session_id,history_revision,model_id,context_budget,
                   through_message_id,message_count,prepared_text,created_at,profile
            FROM session_context_preparations
            WHERE session_id=$session
              AND model_id=$model COLLATE NOCASE
              AND profile=$profile
              AND message_count<=$maximumCount
            ORDER BY message_count DESC, created_at DESC
            LIMIT 32;
            """;
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        command.Parameters.AddWithValue("$model", modelId.Trim());
        command.Parameters.AddWithValue("$profile", SqliteMapping.EnumName(profile));
        command.Parameters.AddWithValue("$maximumCount", maximumMessageCount);
        var result = new List<SessionContextPreparation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new SessionContextPreparation(
                reader.GetString(0),
                reader.ReadGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.ReadGuid(5),
                reader.GetInt32(6),
                reader.GetString(7),
                reader.ReadDate(8),
                reader.ReadEnum<SessionContextProfile>(9)));
        }
        return result;
    }

    public Task SaveSessionContextPreparationAsync(
        SessionContextPreparation preparation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        return database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO session_context_preparations
                    (cache_key,session_id,history_revision,model_id,context_budget,
                     through_message_id,message_count,prepared_text,created_at,profile)
                VALUES($key,$session,$revision,$model,$budget,$through,$count,$text,$created,$profile)
                ON CONFLICT(cache_key) DO UPDATE SET
                    prepared_text=excluded.prepared_text,
                    created_at=excluded.created_at;
                """;
            command.Parameters.AddWithValue("$key", preparation.CacheKey);
            command.Parameters.AddWithValue("$session", preparation.SessionId.ToString("D"));
            command.Parameters.AddWithValue("$revision", preparation.HistoryRevision);
            command.Parameters.AddWithValue("$model", preparation.ModelId);
            command.Parameters.AddWithValue("$budget", preparation.ContextBudget);
            command.Parameters.AddWithValue("$through", preparation.ThroughMessageId.ToString("D"));
            command.Parameters.AddWithValue("$count", preparation.MessageCount);
            command.Parameters.AddWithValue("$text", preparation.PreparedText);
            command.Parameters.AddWithValue("$created", preparation.CreatedAt.ToDb());
            command.Parameters.AddWithValue("$profile", SqliteMapping.EnumName(preparation.Profile));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    public async Task<SpeechPreparation?> GetSpeechPreparationAsync(
        string cacheKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT cache_key,session_id,source_message_id,source_kind,source_hash,
                   model_id,prepared_text,created_at,source_units_json,segments_json
            FROM speech_preparations
            WHERE cache_key=$key;
            """;
        command.Parameters.AddWithValue("$key", cacheKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new SpeechPreparation(
                reader.GetString(0),
                reader.ReadGuid(1),
                reader.IsDBNull(2) ? null : reader.ReadGuid(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.ReadDate(7),
                reader.GetString(8),
                reader.GetString(9))
            : null;
    }

    public Task SaveSpeechPreparationAsync(
        SpeechPreparation preparation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        return database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO speech_preparations
                    (cache_key,session_id,source_message_id,source_kind,source_hash,
                     model_id,prepared_text,created_at,source_units_json,segments_json)
                VALUES($key,$session,$message,$kind,$hash,$model,$text,$created,$units,$segments)
                ON CONFLICT(cache_key) DO UPDATE SET
                    prepared_text=excluded.prepared_text,
                    source_units_json=excluded.source_units_json,
                    segments_json=excluded.segments_json,
                    created_at=excluded.created_at;
                """;
            command.Parameters.AddWithValue("$key", preparation.CacheKey);
            command.Parameters.AddWithValue("$session", preparation.SessionId.ToString("D"));
            command.Parameters.AddWithValue("$message", preparation.SourceMessageId is { } messageId
                ? messageId.ToString("D")
                : DBNull.Value);
            command.Parameters.AddWithValue("$kind", preparation.SourceKind);
            command.Parameters.AddWithValue("$hash", preparation.SourceHash);
            command.Parameters.AddWithValue("$model", preparation.ModelId);
            command.Parameters.AddWithValue("$text", preparation.PreparedText);
            command.Parameters.AddWithValue("$created", preparation.CreatedAt.ToDb());
            command.Parameters.AddWithValue("$units", preparation.SourceUnitsJson);
            command.Parameters.AddWithValue("$segments", preparation.SegmentsJson);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<int> MarkStreamingMessagesInterruptedAsync(CancellationToken cancellationToken = default) =>
        database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE chat_messages
                SET status='interrupted',updated_at=$now
                WHERE status IN ('pending','streaming')
                  AND NOT EXISTS(
                      SELECT 1 FROM go_ai_runs r
                      WHERE r.assistant_message_id=chat_messages.id
                        AND r.server_run_id IS NOT NULL
                        AND r.state IN ('queued','running','waitingForClient')
                  );
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToDb());
            var messages = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            command.CommandText = "UPDATE chat_runs SET status='interrupted',completed_at=$now WHERE status='streaming';";
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            return messages;
        }, cancellationToken);

    private Task UpdateSessionAsync(Guid id, string assignment, object? value, CancellationToken cancellationToken) =>
        database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"UPDATE chat_sessions SET {assignment},updated_at=$now WHERE id=$id;";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$value", value ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToDb());
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    private static ChatSession ReadSession(SqliteDataReader reader) => new(
        reader.ReadGuid(0), reader.GetString(1), reader.ReadDate(2), reader.ReadDate(3),
        reader.IsDBNull(4) ? null : reader.ReadGuid(4), reader.GetString(5), reader.ReadEnum<AssistantMode>(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        !reader.IsDBNull(9) && reader.GetInt32(9) != 0,
        reader.IsDBNull(10) ? null : reader.ReadDate(10),
        reader.IsDBNull(11) ? null : reader.ReadEnum<PersistentToolAction>(11));

    private static ChatMessage ReadMessage(SqliteDataReader reader) => new(
        reader.ReadGuid(0), reader.ReadGuid(1), reader.ReadEnum<ChatRole>(2), reader.GetString(3),
        reader.ReadEnum<MessageStatus>(4), reader.ReadDate(5), reader.ReadDate(6), reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : new ToolExecutionInfo(
            reader.GetString(8), reader.GetString(9), reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetString(12)),
        reader.IsDBNull(13) ? null : reader.GetString(13),
        reader.IsDBNull(14) ? MessageContentProfile.General : reader.ReadEnum<MessageContentProfile>(14));
}
