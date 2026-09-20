using GoAi.Server.Core.Coding;
using GoAi.Server.Core.Models;
using System.Text.Json;

namespace GoAi.Server.Core.Runs;

public sealed partial class RunProcessor
{
    internal static AgentRunCheckpoint ScheduleExplicitDeepResearch(AgentRunCheckpoint checkpoint, string runId, string task)
    {
        // Persist the requested operation once when creating the run. Resume
        // consumes the existing tool checkpoint instead of scheduling it again.
        var call = new LmToolCall("deep-research-" + runId, CodingDeepResearchPipeline.ToolName,
            JsonSerializer.SerializeToElement(new { task = task.Length <= 4000 ? task : task[..4000] }));
        return checkpoint with
        {
            Messages = [.. checkpoint.Messages, new LmChatMessage("assistant", null, ToolCalls: [call])],
            ActiveToolCalls = [call],
            ActiveCallRound = 0,
            ToolCallCount = checkpoint.ToolCallCount + 1,
        };
    }
}
