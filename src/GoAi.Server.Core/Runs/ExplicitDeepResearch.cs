using GoAi.Server.Core.Coding;
using GoAi.Server.Core.Models;
using GoAi.Contracts;
using System.Text.Json;

namespace GoAi.Server.Core.Runs;

public sealed partial class RunProcessor
{
    internal static AgentRunCheckpoint ScheduleExplicitDeepResearch(
        AgentRunCheckpoint checkpoint,
        string runId,
        string task,
        DeepResearchOptions? options = null)
    {
        // Persist the requested operation once when creating the run. Resume
        // consumes the existing tool checkpoint instead of scheduling it again.
        options ??= new();
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["task"] = task.Length <= 4000 ? task : task[..4000],
            ["profile"] = JsonNamingPolicy.CamelCase.ConvertName(options.Profile.ToString()),
            ["autonomyLevel"] = JsonNamingPolicy.CamelCase.ConvertName(options.AutonomyLevel.ToString()),
            ["verificationLevel"] = JsonNamingPolicy.CamelCase.ConvertName(options.VerificationLevel.ToString()),
        };
        if (!string.IsNullOrWhiteSpace(options.ProjectId)) arguments["projectId"] = options.ProjectId;
        if (options.MaximumWorks is { } maximumWorks) arguments["maximumWorks"] = maximumWorks;
        if (options.MaximumFullTexts is { } maximumFullTexts) arguments["maximumFullTexts"] = maximumFullTexts;
        if (!string.IsNullOrWhiteSpace(options.ResumeCheckpointId)) arguments["resumeCheckpointId"] = options.ResumeCheckpointId;
        if (options.ProtocolVersion is { } protocolVersion) arguments["protocolVersion"] = protocolVersion;
        if (options.PreferredLanguages is { Count: > 0 }) arguments["preferredLanguages"] = options.PreferredLanguages;
        if (options.UpdateSince is { } updateSince) arguments["updateSince"] = updateSince;
        var call = new LmToolCall("deep-research-" + runId, CodingDeepResearchPipeline.ToolName,
            JsonSerializer.SerializeToElement(arguments));
        return checkpoint with
        {
            Messages = [.. checkpoint.Messages, new LmChatMessage("assistant", null, ToolCalls: [call])],
            ActiveToolCalls = [call],
            ActiveCallRound = 0,
            ToolCallCount = checkpoint.ToolCallCount + 1,
        };
    }
}
