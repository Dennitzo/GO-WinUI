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
            SELECT id,title,created_at,updated_at,selected_workflow_id,draft
            FROM chat_sessions
            WHERE $search='' OR rowid IN (SELECT rowid FROM session_search WHERE session_search MATCH $fts)
            ORDER BY updated_at DESC;
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
        command.CommandText = "SELECT id,title,created_at,updated_at,selected_workflow_id,draft FROM chat_sessions WHERE id=$id;";
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
            SELECT id,session_id,role,content,status,created_at,updated_at,error
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

    public async Task<ChatMessage> AddMessageAsync(Guid sessionId, ChatRole role, string content, MessageStatus status, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var message = new ChatMessage(Guid.NewGuid(), sessionId, role, content ?? string.Empty, status, now, now);
        await database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO chat_messages(id,session_id,role,content,status,created_at,updated_at)
                VALUES($id,$session,$role,$content,$status,$now,$now);
                UPDATE chat_sessions SET updated_at=$now WHERE id=$session;
                """;
            command.Parameters.AddWithValue("$id", message.Id.ToString("D"));
            command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
            command.Parameters.AddWithValue("$role", SqliteMapping.EnumName(role));
            command.Parameters.AddWithValue("$content", message.Content);
            command.Parameters.AddWithValue("$status", SqliteMapping.EnumName(status));
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

    public Task<int> MarkStreamingMessagesInterruptedAsync(CancellationToken cancellationToken = default) =>
        database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE chat_messages SET status='interrupted',updated_at=$now WHERE status IN ('pending','streaming');";
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
        reader.IsDBNull(4) ? null : reader.ReadGuid(4), reader.GetString(5));

    private static ChatMessage ReadMessage(SqliteDataReader reader) => new(
        reader.ReadGuid(0), reader.ReadGuid(1), reader.ReadEnum<ChatRole>(2), reader.GetString(3),
        reader.ReadEnum<MessageStatus>(4), reader.ReadDate(5), reader.ReadDate(6), reader.IsDBNull(7) ? null : reader.GetString(7));
}
