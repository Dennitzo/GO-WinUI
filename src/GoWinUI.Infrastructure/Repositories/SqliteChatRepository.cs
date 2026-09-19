using GoWinUI.Core.Chat;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using GoWinUI.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace GoWinUI.Infrastructure.Repositories;

public sealed class SqliteChatRepository(SqliteDatabase database) : IChatRepository
{
    public async Task<IReadOnlyList<ChatSession>> ListSessionsAsync(string? search = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,title,created_at,updated_at,selected_workflow_id,draft,is_pinned,pinned_at,persistent_tool_action,conversation_revision,coding_workspace_path,session_group_id
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
        command.CommandText = "SELECT id,title,created_at,updated_at,selected_workflow_id,draft,is_pinned,pinned_at,persistent_tool_action,conversation_revision,coding_workspace_path,session_group_id FROM chat_sessions WHERE id=$id;";
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

    public Task ClearDraftIfMatchesAsync(Guid id, string expectedDraft, CancellationToken cancellationToken = default) =>
        database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE chat_sessions SET draft='' WHERE id=$id AND draft=$expected;";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$expected", expectedDraft);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    public Task SelectWorkflowAsync(Guid id, Guid? workflowId, CancellationToken cancellationToken = default) =>
        UpdateSessionAsync(id, "selected_workflow_id=$value", workflowId?.ToString("D"), cancellationToken);

    public Task SetCodingWorkspacePathAsync(Guid id, string? path, CancellationToken cancellationToken = default) =>
        SetCodingWorkspacePathAsync(id, path, activateCoding: false, cancellationToken);

    public Task SetCodingWorkspacePathAsync(Guid id, string? path, bool activateCoding, CancellationToken cancellationToken = default)
    {
        var normalized = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        if (normalized is not null)
        {
            if (!Path.IsPathFullyQualified(normalized) || normalized.Any(char.IsControl))
            {
                throw new ArgumentException("Der Coding-Projektordner muss ein absoluter Pfad sein.", nameof(path));
            }
            normalized = SqliteDatabase.NormalizeWorkspaceProjectPath(normalized);
        }
        if (activateCoding && normalized is null)
            throw new ArgumentException("Zum Aktivieren von Coding ist ein Projektordner erforderlich.", nameof(path));
        return database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE chat_sessions
                SET coding_workspace_path=$path,
                    persistent_tool_action=CASE WHEN $activate=1 THEN 'code' ELSE persistent_tool_action END,
                    updated_at=$now
                WHERE id=$id;
                """;
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$path", (object?)normalized ?? DBNull.Value);
            command.Parameters.AddWithValue("$activate", activateCoding ? 1 : 0);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToDb());
            var changed = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            if (activateCoding && changed != 1)
                throw new InvalidOperationException("Die ausgewählte Coding-Sitzung wurde nicht gefunden.");
            if (changed == 1 && normalized is not null)
            {
                var group = await GetOrCreateWorkspaceGroupAsync(connection, transaction, normalized, token).ConfigureAwait(false);
                command.Parameters.Clear();
                command.CommandText = "UPDATE chat_sessions SET session_group_id=$group WHERE id=$id;";
                command.Parameters.AddWithValue("$id", id.ToString("D"));
                command.Parameters.AddWithValue("$group", group.Id.ToString("D"));
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }, cancellationToken);
    }

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
        // Remove only the now-empty group of this deletion, in the same transaction.
        command.CommandText = "SELECT session_group_id FROM chat_sessions WHERE id=$id;";
        var deletedGroup = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
        command.CommandText = "DELETE FROM chat_sessions WHERE id=$id;";
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        command.CommandText = "DELETE FROM chat_session_groups WHERE id=$group AND NOT EXISTS (SELECT 1 FROM chat_sessions WHERE session_group_id=$group);";
        command.Parameters.AddWithValue("$group", deletedGroup ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await DeleteOrphanedBinaryObjectsAsync(command, blobIds, token).ConfigureAwait(false);
    }, cancellationToken);

    public Task<int> DeleteUnpinnedSessionsAsync(CancellationToken cancellationToken = default) =>
        database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT documents.blob_id
                FROM documents
                INNER JOIN chat_sessions ON chat_sessions.id=documents.session_id
                WHERE chat_sessions.is_pinned=0;
                """;
            var blobIds = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    blobIds.Add(reader.GetString(0));
                }
            }

            command.CommandText = "DELETE FROM chat_sessions WHERE is_pinned=0;";
            var deletedCount = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            command.CommandText = "DELETE FROM chat_session_groups WHERE NOT EXISTS (SELECT 1 FROM chat_sessions WHERE session_group_id=chat_session_groups.id);";
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            await DeleteOrphanedBinaryObjectsAsync(command, blobIds, token).ConfigureAwait(false);
            return deletedCount;
        }, cancellationToken);

    public async Task<IReadOnlyList<ChatMessage>> ListMessagesAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,session_id,role,content,status,created_at,updated_at,error,tool_name,tool_context,tool_status,tool_detail,tool_provider,context_summary,content_profile,revision,tool_steps_json
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

    private static async Task DeleteOrphanedBinaryObjectsAsync(
        SqliteCommand command,
        IEnumerable<string> blobIds,
        CancellationToken cancellationToken)
    {
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
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<ChatMessage> AddMessageAsync(
        Guid sessionId,
        ChatRole role,
        string content,
        MessageStatus status,
        MessageContentProfile contentProfile = MessageContentProfile.General,
        CancellationToken cancellationToken = default) =>
        AddMessageCoreAsync(sessionId, role, content, status, contentProfile, cancellationToken);

    private async Task<ChatMessage> AddMessageCoreAsync(
        Guid sessionId,
        ChatRole role,
        string content,
        MessageStatus status,
        MessageContentProfile contentProfile,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var message = new ChatMessage(
            Guid.NewGuid(), sessionId, role, ChatContentSanitizer.Sanitize(content), status, now, now,
            ContentProfile: contentProfile);
        await database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO chat_messages(id,session_id,role,content,status,created_at,updated_at,content_profile,revision)
                VALUES($id,$session,$role,$content,$status,$now,$now,$profile,1);
                UPDATE chat_sessions
                SET updated_at=$now,conversation_revision=conversation_revision+1
                WHERE id=$session;
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

    public Task<int> DeleteEmptyTerminalMessagesAsync(CancellationToken cancellationToken = default) =>
        database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            const string predicate = """
                role='assistant'
                AND status IN ('completed','cancelled','failed','interrupted')
                AND trim(content)=''
                AND tool_name IS NULL
                AND COALESCE(json_array_length(tool_steps_json),0)=0
                AND NOT EXISTS(SELECT 1 FROM chat_artifacts artifact WHERE artifact.message_id=chat_messages.id)
                """;
            command.CommandText = $"SELECT COUNT(*) FROM chat_messages WHERE {predicate};";
            var count = Convert.ToInt32(
                await command.ExecuteScalarAsync(token).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            command.CommandText = $"""
                UPDATE chat_sessions
                SET conversation_revision=conversation_revision+1,updated_at=$now
                WHERE id IN (SELECT DISTINCT session_id FROM chat_messages WHERE {predicate});
                DELETE FROM chat_messages WHERE {predicate};
            """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToDb());
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            return count;
        }, cancellationToken);

    public Task UpdateMessageAsync(Guid messageId, string content, MessageStatus status, string? errorMessage = null, CancellationToken cancellationToken = default) =>
        database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE chat_messages
                SET content=$content,status=$status,error=$error,revision=revision+1,updated_at=$now
                WHERE id=$id;
                UPDATE chat_sessions
                SET updated_at=$now,conversation_revision=conversation_revision+1
                WHERE id=(SELECT session_id FROM chat_messages WHERE id=$id);
                """;
            command.Parameters.AddWithValue("$id", messageId.ToString("D"));
            command.Parameters.AddWithValue("$content", ChatContentSanitizer.Sanitize(content));
            command.Parameters.AddWithValue("$status", SqliteMapping.EnumName(status));
            command.Parameters.AddWithValue("$error", (object?)errorMessage ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToDb());
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    public Task<IReadOnlyList<AssistantToolStep>> SaveToolStepAsync(Guid messageId, AssistantToolStep toolStep, CancellationToken cancellationToken = default)
    {
        ValidateToolStep(toolStep);
        return database.WriteAsync<IReadOnlyList<AssistantToolStep>>(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT tool_steps_json FROM chat_messages WHERE id=$id;";
            command.Parameters.AddWithValue("$id", messageId.ToString("D"));
            var json = await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string
                ?? throw new InvalidOperationException("Die Werkzeugnachricht wurde nicht gefunden.");
            var steps = JsonSerializer.Deserialize<List<AssistantToolStep>>(json, JsonSerializerOptions.Web) ?? [];
            var index = steps.FindIndex(value => string.Equals(value.Id, toolStep.Id, StringComparison.Ordinal));
            if (index >= 0)
            {
                var merged = AssistantToolStep.Merge(steps[index], toolStep);
                if (steps[index] == merged) return steps;
                steps[index] = merged;
            }
            else
            {
                steps.Add(toolStep);
            }
            command.CommandText = """
                UPDATE chat_messages SET tool_steps_json=$steps,revision=revision+1,updated_at=$now WHERE id=$id;
                UPDATE chat_sessions SET conversation_revision=conversation_revision+1,updated_at=$now
                    WHERE id=(SELECT session_id FROM chat_messages WHERE id=$id);
                """;
            command.Parameters.AddWithValue("$steps", JsonSerializer.Serialize(steps, JsonSerializerOptions.Web));
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToDb());
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            return steps;
        }, cancellationToken);
    }

    public Task UpdateMessageWithToolStepsAsync(Guid messageId, string content, MessageStatus status,
        IReadOnlyList<AssistantToolStep> toolSteps, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolSteps);
        if (toolSteps.Select(static step => step.Id).Distinct(StringComparer.Ordinal).Count() != toolSteps.Count)
            throw new ArgumentException("Ungültige Werkzeugchronologie.", nameof(toolSteps));
        foreach (var step in toolSteps) ValidateToolStep(step);
        var visibleContent = ChatContentSanitizer.Sanitize(content);
        if (toolSteps.Any(step => step.ContentOffset > visibleContent.Length))
            throw new ArgumentException("Werkzeugposition liegt außerhalb des Nachrichtentextes.", nameof(toolSteps));
        return database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT tool_steps_json FROM chat_messages WHERE id=$id;";
            command.Parameters.AddWithValue("$id", messageId.ToString("D"));
            var storedJson = await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string
                ?? throw new InvalidOperationException("Die Werkzeugnachricht wurde nicht gefunden.");
            var mergedSteps = JsonSerializer.Deserialize<List<AssistantToolStep>>(storedJson, JsonSerializerOptions.Web) ?? [];
            foreach (var step in toolSteps)
            {
                var index = mergedSteps.FindIndex(value => value.Id == step.Id);
                if (index < 0) mergedSteps.Add(step);
                else mergedSteps[index] = AssistantToolStep.Merge(mergedSteps[index], step);
            }
            if (mergedSteps.Any(step => step.ContentOffset > visibleContent.Length))
                throw new InvalidOperationException("Die Werkzeugchronologie passt nicht zum neuen Nachrichtentext.");
            command.CommandText = """
                UPDATE chat_messages SET content=$content,status=$status,tool_steps_json=$steps,revision=revision+1,updated_at=$now WHERE id=$id;
                UPDATE chat_sessions SET conversation_revision=conversation_revision+1,updated_at=$now
                    WHERE id=(SELECT session_id FROM chat_messages WHERE id=$id);
                """;
            command.Parameters.AddWithValue("$content", visibleContent);
            command.Parameters.AddWithValue("$status", SqliteMapping.EnumName(status));
            command.Parameters.AddWithValue("$steps", JsonSerializer.Serialize(mergedSteps, JsonSerializerOptions.Web));
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToDb());
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private static void ValidateToolStep(AssistantToolStep toolStep)
    {
        ArgumentNullException.ThrowIfNull(toolStep);
        if (string.IsNullOrWhiteSpace(toolStep.Id) || toolStep.Id.Length > 200 || string.IsNullOrWhiteSpace(toolStep.Tool)
            || toolStep.Tool.Length > 128 || toolStep.Detail?.Length > AssistantToolStep.MaximumDetailCharacters || toolStep.PreviewHtml?.Length > 16_000
            || toolStep.InputJson?.Length > AssistantToolStep.MaximumStructuredJsonCharacters
            || toolStep.OutputJson?.Length > AssistantToolStep.MaximumStructuredJsonCharacters
            || toolStep.Explanation?.Length > AssistantToolStep.MaximumExplanationCharacters || toolStep.ContentOffset < 0
            || toolStep.AgentId?.Length > 128
            || toolStep.Status is not ("running" or "completed" or "failed" or "denied" or "cancelled" or "interrupted"))
            throw new ArgumentException("Ungültiger oder zu großer Werkzeugschritt.", nameof(toolStep));
        foreach (var json in new[] { toolStep.InputJson, toolStep.OutputJson }.Where(static value => value is not null))
        {
            try { using var document = JsonDocument.Parse(json!); }
            catch (JsonException exception) { throw new ArgumentException("Werkzeugdaten müssen gültiges JSON enthalten.", nameof(toolStep), exception); }
        }
    }

    public Task SetMessageContextSummaryAsync(Guid messageId, string contextSummary, CancellationToken cancellationToken = default) =>
        database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE chat_messages SET context_summary=$summary,revision=revision+1,updated_at=$now WHERE id=$id;
                UPDATE chat_sessions
                SET conversation_revision=conversation_revision+1,updated_at=$now
                WHERE id=(SELECT session_id FROM chat_messages WHERE id=$id);
                """;
            command.Parameters.AddWithValue("$id", messageId.ToString("D"));
            command.Parameters.AddWithValue("$summary", ChatContentSanitizer.Sanitize(contextSummary));
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToDb());
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    public Task ResetMessageForRetryAsync(
        Guid messageId,
        CancellationToken cancellationToken = default) =>
        database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE chat_messages
                SET content='',
                    status='streaming',
                    error=NULL,
                    revision=revision+1,
                    updated_at=$now
                WHERE id=$id AND role='assistant'
                RETURNING session_id;
                """;
            command.Parameters.AddWithValue("$id", messageId.ToString("D"));
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToDb());
            var sessionId = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
            if (sessionId is null or DBNull)
            {
                throw new InvalidOperationException("Der AI-Laufanker wurde nicht gefunden.");
            }

            command.Parameters.Clear();
            command.CommandText = """
                UPDATE chat_sessions
                SET updated_at=$now,conversation_revision=conversation_revision+1
                WHERE id=$session;
                """;
            command.Parameters.AddWithValue("$session", Convert.ToString(
                sessionId,
                System.Globalization.CultureInfo.InvariantCulture)!);
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
                    updated_at=$now
                WHERE id=$id;
                """;
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$action", action switch
            {
                null => DBNull.Value,
                PersistentToolAction.Coding => "code",
                _ => SqliteMapping.EnumName(action.Value),
            });
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

    public async Task<ChatTurn> AddTurnAsync(
        Guid sessionId,
        string userContent,
        MessageContentProfile assistantContentProfile = MessageContentProfile.General,
        CancellationToken cancellationToken = default) =>
        await AddTurnCoreAsync(sessionId, userContent, assistantContentProfile, cancellationToken)
            .ConfigureAwait(false);

    private async Task<ChatTurn> AddTurnCoreAsync(
        Guid sessionId,
        string userContent,
        MessageContentProfile assistantContentProfile,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var assistantNow = now.AddTicks(1);
        var user = new ChatMessage(
            Guid.NewGuid(), sessionId, ChatRole.User,
            ChatContentSanitizer.Sanitize(userContent), MessageStatus.Completed, now, now);
        var assistant = new ChatMessage(
            Guid.NewGuid(), sessionId, ChatRole.Assistant, string.Empty,
            MessageStatus.Streaming, assistantNow, assistantNow,
            ContentProfile: assistantContentProfile);
        await database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO chat_messages(id,session_id,role,content,status,created_at,updated_at,content_profile,revision)
                VALUES($user,$session,'user',$userContent,'completed',$now,$now,'general',1);
                INSERT INTO chat_messages(id,session_id,role,content,status,created_at,updated_at,content_profile,revision)
                VALUES($assistant,$session,'assistant','',$assistantStatus,$assistantNow,$assistantNow,$profile,1);
                UPDATE chat_sessions
                SET updated_at=$assistantNow,conversation_revision=conversation_revision+1
                WHERE id=$session;
                """;
            command.Parameters.AddWithValue("$user", user.Id.ToString("D"));
            command.Parameters.AddWithValue("$assistant", assistant.Id.ToString("D"));
            command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
            command.Parameters.AddWithValue("$userContent", user.Content);
            command.Parameters.AddWithValue("$assistantStatus", SqliteMapping.EnumName(MessageStatus.Streaming));
            command.Parameters.AddWithValue("$profile", SqliteMapping.EnumName(assistantContentProfile));
            command.Parameters.AddWithValue("$now", now.ToDb());
            command.Parameters.AddWithValue("$assistantNow", assistantNow.ToDb());
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return new ChatTurn(user, assistant);
    }

    public async Task<ChatMessage?> GetMessageAsync(
        Guid messageId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,session_id,role,content,status,created_at,updated_at,error,tool_name,tool_context,tool_status,tool_detail,tool_provider,context_summary,content_profile,revision,tool_steps_json
            FROM chat_messages
            WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$id", messageId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadMessage(reader) : null;
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

    public Task<int> MarkStreamingMessagesInterruptedAsync(CancellationToken cancellationToken = default) =>
        database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE chat_sessions
                SET conversation_revision=conversation_revision+1,updated_at=$now
                WHERE id IN (
                    SELECT DISTINCT session_id FROM chat_messages
                    WHERE status IN ('pending','streaming')
                      AND NOT EXISTS(
                          SELECT 1 FROM go_ai_runs r
                          WHERE r.assistant_message_id=chat_messages.id
                            AND r.server_run_id IS NOT NULL
                            AND r.state IN ('queued','running','waitingForClient')
                      )
                );
                UPDATE chat_messages
                SET status='interrupted',revision=revision+1,updated_at=$now
                WHERE status IN ('pending','streaming')
                  AND NOT EXISTS(
                      SELECT 1 FROM go_ai_runs r
                      WHERE r.assistant_message_id=chat_messages.id
                        AND r.server_run_id IS NOT NULL
                        AND r.state IN ('queued','running','waitingForClient')
                  );
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToDb());
            _ = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            command.CommandText = "SELECT changes();";
            var messages = Convert.ToInt32(
                await command.ExecuteScalarAsync(token).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
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

    internal static ChatSession ReadSession(SqliteDataReader reader) => new(
        reader.ReadGuid(0), reader.GetString(1), reader.ReadDate(2), reader.ReadDate(3),
        reader.IsDBNull(4) ? null : reader.ReadGuid(4), reader.GetString(5),
        !reader.IsDBNull(6) && reader.GetInt32(6) != 0,
        reader.IsDBNull(7) ? null : reader.ReadDate(7),
        reader.IsDBNull(8) ? null : reader.GetString(8) == "code" ? PersistentToolAction.Coding : reader.ReadEnum<PersistentToolAction>(8),
        reader.IsDBNull(9) ? 0 : reader.GetInt64(9),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.IsDBNull(11) ? null : reader.ReadGuid(11));

    internal static ChatMessage ReadMessage(SqliteDataReader reader) => new(
        reader.ReadGuid(0), reader.ReadGuid(1), reader.ReadEnum<ChatRole>(2), reader.GetString(3),
        reader.ReadEnum<MessageStatus>(4), reader.ReadDate(5), reader.ReadDate(6), reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : new ToolExecutionInfo(
            reader.GetString(8), reader.GetString(9), reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetString(12)),
        reader.IsDBNull(13) ? null : reader.GetString(13),
        reader.IsDBNull(14) ? MessageContentProfile.General : reader.ReadEnum<MessageContentProfile>(14),
        reader.IsDBNull(15) ? 1 : reader.GetInt64(15),
        reader.FieldCount < 17 || reader.IsDBNull(16) ? [] : JsonSerializer.Deserialize<AssistantToolStep[]>(reader.GetString(16), JsonSerializerOptions.Web) ?? []);

    public async Task<IReadOnlyList<ChatSessionGroup>> ListSessionGroupsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name,is_collapsed,created_at,workspace_path FROM chat_session_groups ORDER BY created_at DESC;";
        var result = new List<ChatSessionGroup>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ChatSessionGroup(
                reader.ReadGuid(0),
                reader.GetString(1),
                !reader.IsDBNull(2) && reader.GetInt32(2) != 0,
                reader.ReadDate(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return result;
    }

    public Task<ChatSessionGroup> GetOrCreateSessionGroupForWorkspaceAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        var normalized = SqliteDatabase.NormalizeWorkspaceProjectPath(workspacePath)
            ?? throw new ArgumentException("Der Projektordner muss ein absoluter Pfad sein.", nameof(workspacePath));
        return database.WriteAsync((connection, transaction, token) =>
            GetOrCreateWorkspaceGroupAsync(connection, transaction, normalized, token), cancellationToken);
    }

    private static async Task<ChatSessionGroup> GetOrCreateWorkspaceGroupAsync(
        SqliteConnection connection, SqliteTransaction transaction, string normalized, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // SQLite NOCASE covers ASCII only; Windows project names also include
        // characters such as ü/Ü. Use the same path comparison as migration 36.
        command.CommandText = "SELECT id,name,is_collapsed,created_at,workspace_path FROM chat_session_groups WHERE workspace_path IS NOT NULL;";
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                if (string.Equals(reader.GetString(4), normalized, StringComparison.OrdinalIgnoreCase))
                    return new ChatSessionGroup(reader.ReadGuid(0), reader.GetString(1), reader.GetBoolean(2), reader.ReadDate(3), reader.GetString(4));
        }
        var now = DateTimeOffset.UtcNow;
        var group = new ChatSessionGroup(Guid.NewGuid(), SqliteDatabase.WorkspaceProjectName(normalized), false, now, normalized);
        command.Parameters.Clear();
        command.CommandText = "INSERT INTO chat_session_groups(id,name,workspace_path,is_collapsed,created_at) VALUES($id,$name,$path,0,$now);";
        command.Parameters.AddWithValue("$id", group.Id.ToString("D"));
        command.Parameters.AddWithValue("$name", group.Name);
        command.Parameters.AddWithValue("$path", normalized);
        command.Parameters.AddWithValue("$now", now.ToDb());
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        return group;
    }

    public Task ApplySessionGroupingAsync(IReadOnlyList<SessionGroupAssignment> groups, CancellationToken cancellationToken = default) =>
        database.WriteAsync(async (connection, transaction, token) =>
        {
            ArgumentNullException.ThrowIfNull(groups);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var now = DateTimeOffset.UtcNow;
            foreach (var group in groups)
            {
                if (group.SessionIds is null || group.SessionIds.Count == 0) continue;
                var name = group.Name?.Trim() ?? string.Empty;
                if (name.Length == 0 || name.Length > 120)
                    throw new ArgumentException("Ungültiger Gruppenname.");
                command.CommandText = "INSERT INTO chat_session_groups(id,name,is_collapsed,created_at) VALUES($id,$name,0,$now) ON CONFLICT(id) DO UPDATE SET name=excluded.name;";
                command.Parameters.Clear();
                var id = group.Id is null ? Guid.NewGuid().ToString("D") : group.Id.Value.ToString("D");
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$name", name);
                command.Parameters.AddWithValue("$now", now.ToDb());
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                command.CommandText = "UPDATE chat_sessions SET session_group_id=$group WHERE id=$id;";
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$group", id);
                var sessionParameter = command.Parameters.Add("$id", SqliteType.Text);
                foreach (var sessionId in group.SessionIds.Distinct())
                {
                    sessionParameter.Value = sessionId.ToString("D");
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
            // Grouping is an incremental assignment operation. Unmentioned and
            // concurrently-created sessions retain their current group.
            command.CommandText = "DELETE FROM chat_session_groups WHERE workspace_path IS NULL AND NOT EXISTS (SELECT 1 FROM chat_sessions WHERE session_group_id=chat_session_groups.id);";
            command.Parameters.Clear();
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    public Task SetSessionGroupCollapsedAsync(Guid groupId, bool collapsed, CancellationToken cancellationToken = default) =>
        database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE chat_session_groups SET is_collapsed=$collapsed WHERE id=$id;";
            command.Parameters.AddWithValue("$id", groupId.ToString("D"));
            command.Parameters.AddWithValue("$collapsed", collapsed ? 1 : 0);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);
}
