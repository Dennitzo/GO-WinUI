using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using GoWinUI.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace GoWinUI.Infrastructure.Repositories;

public sealed class SqliteCodingCampaignRepository(SqliteDatabase database) : ICodingCampaignRepository
{
    public Task<CodingCampaignState?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        ReadSingleAsync("c.id=$value", id.ToString("D"), cancellationToken);

    public Task<CodingCampaignState?> GetForSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        ReadSingleAsync("c.session_id=$value", sessionId.ToString("D"), cancellationToken);

    public async Task<IReadOnlyList<CodingCampaignState>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectCampaignSql + " ORDER BY c.updated_at DESC;";
        return await ReadCampaignsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public Task SaveAsync(CodingCampaignState campaign, CancellationToken cancellationToken = default) =>
        database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO coding_campaigns(
                    id,session_id,definition_id,title,workspace_path,workspace_fingerprint,model_id,status,phase,
                    iteration,current_challenge,last_error,validation_json,restart_count,created_at,updated_at)
                VALUES(
                    $id,$session,$definition,$title,$workspace,$fingerprint,$model,$status,$phase,
                    $iteration,$challenge,$error,$validation,$restarts,$created,$updated)
                ON CONFLICT(id) DO UPDATE SET
                    session_id=excluded.session_id,
                    definition_id=excluded.definition_id,
                    title=excluded.title,
                    workspace_path=excluded.workspace_path,
                    workspace_fingerprint=excluded.workspace_fingerprint,
                    model_id=excluded.model_id,
                    status=excluded.status,
                    phase=excluded.phase,
                    iteration=excluded.iteration,
                    current_challenge=excluded.current_challenge,
                    last_error=excluded.last_error,
                    validation_json=excluded.validation_json,
                    restart_count=excluded.restart_count,
                    updated_at=excluded.updated_at;
                """;
            BindCampaign(command, campaign);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    public Task SaveIterationAsync(CodingCampaignIteration iteration, CancellationToken cancellationToken = default) =>
        database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO coding_campaign_iterations(
                    id,campaign_id,iteration,phase,challenge,assistant_message_id,status,error,validation_json,created_at,updated_at)
                VALUES($id,$campaign,$iteration,$phase,$challenge,$message,$status,$error,$validation,$created,$updated)
                ON CONFLICT(id) DO UPDATE SET
                    assistant_message_id=excluded.assistant_message_id,
                    status=excluded.status,
                    error=excluded.error,
                    validation_json=excluded.validation_json,
                    updated_at=excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$id", iteration.Id.ToString("D"));
            command.Parameters.AddWithValue("$campaign", iteration.CampaignId.ToString("D"));
            command.Parameters.AddWithValue("$iteration", iteration.Iteration);
            command.Parameters.AddWithValue("$phase", SqliteMapping.EnumName(iteration.Phase));
            command.Parameters.AddWithValue("$challenge", iteration.Challenge);
            command.Parameters.AddWithValue("$message", iteration.AssistantMessageId?.ToString("D") ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$status", iteration.Status);
            command.Parameters.AddWithValue("$error", iteration.Error ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$validation", iteration.ValidationJson);
            command.Parameters.AddWithValue("$created", iteration.CreatedAt.ToDb());
            command.Parameters.AddWithValue("$updated", iteration.UpdatedAt.ToDb());
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    public async Task<IReadOnlyList<CodingCampaignIteration>> ListIterationsAsync(
        Guid campaignId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,campaign_id,iteration,phase,challenge,assistant_message_id,status,error,validation_json,created_at,updated_at
            FROM coding_campaign_iterations
            WHERE campaign_id=$campaign
            ORDER BY iteration,created_at;
            """;
        command.Parameters.AddWithValue("$campaign", campaignId.ToString("D"));
        var result = new List<CodingCampaignIteration>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new CodingCampaignIteration(
                reader.ReadGuid(0),
                reader.ReadGuid(1),
                reader.GetInt32(2),
                reader.ReadEnum<CodingCampaignPhase>(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.ReadGuid(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetString(8),
                reader.ReadDate(9),
                reader.ReadDate(10)));
        }
        return result;
    }

    public async Task<bool> IsSolutionPublishedAsync(
        Guid campaignId,
        string relativePath,
        string contentSha256,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM coding_campaign_solution_messages
            WHERE campaign_id=$campaign AND relative_path=$path AND content_sha256=$sha;
            """;
        command.Parameters.AddWithValue("$campaign", campaignId.ToString("D"));
        command.Parameters.AddWithValue("$path", relativePath);
        command.Parameters.AddWithValue("$sha", contentSha256);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    public Task SaveSolutionPublicationAsync(
        Guid campaignId,
        string relativePath,
        string contentSha256,
        Guid messageId,
        DateTimeOffset publishedAt,
        CancellationToken cancellationToken = default) =>
        database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO coding_campaign_solution_messages(
                    campaign_id,relative_path,content_sha256,message_id,published_at)
                VALUES($campaign,$path,$sha,$message,$published);
                """;
            command.Parameters.AddWithValue("$campaign", campaignId.ToString("D"));
            command.Parameters.AddWithValue("$path", relativePath);
            command.Parameters.AddWithValue("$sha", contentSha256);
            command.Parameters.AddWithValue("$message", messageId.ToString("D"));
            command.Parameters.AddWithValue("$published", publishedAt.ToDb());
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    public Task DeleteForSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM coding_campaigns WHERE session_id=$session;";
            command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    private async Task<CodingCampaignState?> ReadSingleAsync(string predicate, object value, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectCampaignSql + $" WHERE {predicate};";
        command.Parameters.AddWithValue("$value", value);
        return (await ReadCampaignsAsync(command, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    private static async Task<IReadOnlyList<CodingCampaignState>> ReadCampaignsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var result = new List<CodingCampaignState>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new CodingCampaignState(
                reader.ReadGuid(0),
                reader.ReadGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.ReadEnum<CodingCampaignStatus>(7),
                reader.ReadEnum<CodingCampaignPhase>(8),
                reader.GetInt32(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.GetString(12),
                reader.GetInt32(13),
                reader.ReadDate(14),
                reader.ReadDate(15)));
        }
        return result;
    }

    private static void BindCampaign(SqliteCommand command, CodingCampaignState campaign)
    {
        command.Parameters.AddWithValue("$id", campaign.Id.ToString("D"));
        command.Parameters.AddWithValue("$session", campaign.SessionId.ToString("D"));
        command.Parameters.AddWithValue("$definition", campaign.DefinitionId);
        command.Parameters.AddWithValue("$title", campaign.Title);
        command.Parameters.AddWithValue("$workspace", campaign.WorkspacePath);
        command.Parameters.AddWithValue("$fingerprint", campaign.WorkspaceFingerprint);
        command.Parameters.AddWithValue("$model", campaign.ModelId);
        command.Parameters.AddWithValue("$status", SqliteMapping.EnumName(campaign.Status));
        command.Parameters.AddWithValue("$phase", SqliteMapping.EnumName(campaign.Phase));
        command.Parameters.AddWithValue("$iteration", campaign.Iteration);
        command.Parameters.AddWithValue("$challenge", campaign.CurrentChallenge ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$error", campaign.LastError ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$validation", campaign.ValidationJson);
        command.Parameters.AddWithValue("$restarts", campaign.RestartCount);
        command.Parameters.AddWithValue("$created", campaign.CreatedAt.ToDb());
        command.Parameters.AddWithValue("$updated", campaign.UpdatedAt.ToDb());
    }

    private const string SelectCampaignSql = """
        SELECT c.id,c.session_id,c.definition_id,c.title,c.workspace_path,c.workspace_fingerprint,c.model_id,
               c.status,c.phase,c.iteration,c.current_challenge,c.last_error,c.validation_json,c.restart_count,
               c.created_at,c.updated_at
        FROM coding_campaigns c
        """;
}
