using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using GoWinUI.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace GoWinUI.Infrastructure.Repositories;

/// <summary>
/// Reads every durable surface of one conversation from the same SQLite snapshot.
/// This prevents the WebView from observing a message revision together with stale
/// artifacts while a write transaction is being committed.
/// </summary>
public sealed class SqliteConversationSnapshotRepository(SqliteDatabase database)
    : IConversationSnapshotRepository
{
    public async Task<ConversationSnapshot?> GetAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        ChatSession? session;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT id,title,created_at,updated_at,selected_workflow_id,draft,
                       is_pinned,pinned_at,persistent_tool_action,conversation_revision
                FROM chat_sessions WHERE id=$id;
                """;
            command.Parameters.AddWithValue("$id", sessionId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            session = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? SqliteChatRepository.ReadSession(reader)
                : null;
        }
        if (session is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var messages = new List<ChatMessage>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT id,session_id,role,content,status,created_at,updated_at,error,
                       tool_name,tool_context,tool_status,tool_detail,tool_provider,context_summary,
                       content_profile,revision
                FROM chat_messages
                WHERE session_id=$session
                ORDER BY created_at,id;
                """;
            command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                messages.Add(SqliteChatRepository.ReadMessage(reader));
            }
        }

        IReadOnlyDictionary<Guid, IReadOnlyList<ChatArtifact>> artifactMap;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = SqliteChatArtifactRepository.SelectSql + """

                JOIN chat_messages message ON message.id=a.message_id
                WHERE message.session_id=$session
                ORDER BY a.created_at,a.id;
                """;
            command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
            artifactMap = (await SqliteChatArtifactRepository.ReadAsync(command, cancellationToken).ConfigureAwait(false))
                .GroupBy(static artifact => artifact.MessageId)
                .ToDictionary(
                    static group => group.Key,
                    static group => (IReadOnlyList<ChatArtifact>)group.ToArray());
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ConversationSnapshot(session, messages, artifactMap);
    }
}
