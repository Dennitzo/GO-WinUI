using GoAi.Contracts;
using GoAi.Server.Core.Gateway;
using GoAi.Server.Core.Runs;
using GoAi.Server.Core.Coding;
using GoAi.Server.Core.Models;

namespace GoAi.Server.Tests;

public sealed class ExplicitDeepResearchTests
{
    private static readonly string[] ResearchTools = ["web.search", "web.fetch", "web.deepResearch"];
    private static readonly string[] CodingCapabilities = ["coding"];

    [Fact]
    public void GeneralSelectionRequestsExistingPipelineWithoutPromptMagicAndDeselectionDoesNot()
    {
        var request = new RunRequest(GoAiProtocol.Version, RunMode.General, [new("user", [new("text", "Vergleiche Verfahren")])],
            AllowedServerTools: ResearchTools, DeepResearch: true);
        RunRequestValidator.Validate(request);
        var tools = new AgentToolCatalog().GetAvailableTools(request);
        Assert.True(StagedWebResearchPipeline.IsRequested(request, tools));
        Assert.False(StagedWebResearchPipeline.IsRequested(request with { DeepResearch = false }, tools));
        Assert.Throws<ArgumentException>(() => RunRequestValidator.Validate(request with { AllowedServerTools = [] }));
    }

    [Fact]
    public void CodingSelectionSchedulesRealResearchToolAndPreservesConversation()
    {
        var request = new RunRequest(GoAiProtocol.Version, RunMode.Coding, [new("user", [new("text", "Vergleiche APIs")])],
            ClientCapabilities: CodingCapabilities, AllowedServerTools: ResearchTools, DeepResearch: true);
        RunRequestValidator.Validate(request);
        var original = new AgentRunCheckpoint([new LmChatMessage("user", "Vergleiche APIs")], 0, 0, 0, 0);
        var scheduled = RunProcessor.ScheduleExplicitDeepResearch(original, "fixture", "Vergleiche APIs");
        var call = Assert.Single(scheduled.ActiveToolCalls!);
        Assert.Equal(CodingDeepResearchPipeline.ToolName, call.Name);
        Assert.Equal("Vergleiche APIs", call.Arguments.GetProperty("task").GetString());
        Assert.Equal(original.Messages[0], scheduled.Messages[0]);
        var catalog = new AgentToolCatalog();
        catalog.Validate(catalog.GetAvailableTools(request).Single(t => t.Name == call.Name), call.Arguments);
    }
}
