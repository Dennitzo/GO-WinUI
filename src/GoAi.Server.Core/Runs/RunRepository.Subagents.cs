using GoAi.Server.Core.Coding;
using System.Text.Json;

namespace GoAi.Server.Core.Runs;
public sealed partial class RunRepository
{
    internal async Task SaveAgentAsync(CodingSubagentState state, CancellationToken token)
    {
        await using var connection = await _database.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO coding_subagents(agent_id,run_id,state_json) VALUES($id,$run,$json) ON CONFLICT(agent_id) DO UPDATE SET state_json=excluded.state_json";
        command.Parameters.AddWithValue("$id", state.Id);
        command.Parameters.AddWithValue("$run", state.RunId);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(state, _database.JsonOptions));
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
    internal async Task<IReadOnlyList<CodingSubagentState>> GetAgentsAsync(string runId, CancellationToken token)
    {
        await using var connection = await _database.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT state_json FROM coding_subagents WHERE run_id=$run ORDER BY rowid";
        command.Parameters.AddWithValue("$run", runId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        List<CodingSubagentState> states = [];
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            states.Add(JsonSerializer.Deserialize<CodingSubagentState>(reader.GetString(0), _checkpointJsonOptions)!);
        return states;
    }
    internal async Task<bool> HasProposalEventAsync(string runId, string proposalId, CancellationToken token)
    {
        await using var connection = await _database.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM run_events WHERE run_id=$run AND event_type=$type AND json_extract(data_json, '$.proposalId')=$proposal)";
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$type", GoAi.Contracts.RunEventTypes.ClientToolProposed);
        command.Parameters.AddWithValue("$proposal", proposalId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }
}
