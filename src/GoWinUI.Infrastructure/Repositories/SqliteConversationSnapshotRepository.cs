using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using GoWinUI.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace GoWinUI.Infrastructure.Repositories;

/// <summary>
/// Reads every durable surface of one conversation from the same SQLite snapshot.
/// This prevents the WebView from observing a message revision together with stale
/// artifacts or coding output while a write transaction is being committed.
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
                SELECT id,title,created_at,updated_at,selected_workflow_id,draft,assistant_mode,
                       workspace_path,workspace_fingerprint,is_pinned,pinned_at,persistent_tool_action,
                       conversation_revision
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
                       content_profile,code_diff,visibility,revision
                FROM chat_messages
                WHERE session_id=$session AND visibility='visible'
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
                WHERE message.session_id=$session AND message.visibility='visible'
                ORDER BY a.created_at,a.id;
                """;
            command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
            artifactMap = (await SqliteChatArtifactRepository.ReadAsync(command, cancellationToken).ConfigureAwait(false))
                .GroupBy(static artifact => artifact.MessageId)
                .ToDictionary(
                    static group => group.Key,
                    static group => (IReadOnlyList<ChatArtifact>)group.ToArray());
        }

        CodingRunSnapshot? codingRun;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT id,local_run_id,server_run_id,session_id,message_id,status,code_diff,
                       started_at,updated_at,revision
                FROM coding_runs
                WHERE session_id=$session
                ORDER BY CASE status WHEN 'running' THEN 0 ELSE 1 END,updated_at DESC,id DESC
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
            codingRun = await SqliteCodingRunRepository.ReadSnapshotAsync(
                connection,
                command,
                cancellationToken,
                transaction).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ConversationSnapshot(session, messages, artifactMap, codingRun);
    }
}
