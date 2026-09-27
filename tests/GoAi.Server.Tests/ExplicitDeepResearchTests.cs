using GoAi.Contracts;
using GoAi.Server.Core.Gateway;
using GoAi.Server.Core.Runs;
using GoAi.Server.Core.Coding;
using GoAi.Server.Core.Models;
using System.Text.Json;

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

    [Fact]
    public void ExplicitResearchPersistsProfileAndOmitsUnsetOptionalFields()
    {
        var checkpoint = new AgentRunCheckpoint([new LmChatMessage("user", "Problem")], 0, 0, 0, 0);
        var scheduled = RunProcessor.ScheduleExplicitDeepResearch(checkpoint, "run", "Problem", new(
            Profile: DeepResearchProfile.OpenProblem,
            ProjectId: "research-1",
            AutonomyLevel: ResearchAutonomyLevel.CodingWorkspaceResearch,
            VerificationLevel: ResearchVerificationLevel.FormalWherePossible));
        var arguments = Assert.Single(scheduled.ActiveToolCalls!).Arguments;
        Assert.Equal("openProblem", arguments.GetProperty("profile").GetString());
        Assert.Equal("codingWorkspaceResearch", arguments.GetProperty("autonomyLevel").GetString());
        Assert.Equal("formalWherePossible", arguments.GetProperty("verificationLevel").GetString());
        Assert.Equal("research-1", arguments.GetProperty("projectId").GetString());
        Assert.False(arguments.TryGetProperty("maximumWorks", out _));
        Assert.False(arguments.TryGetProperty("maximumFullTexts", out _));
    }

    [Fact]
    public void ExplicitResearchCarriesResumeProtocolLanguageAndUpdateOptions()
    {
        var checkpoint = new AgentRunCheckpoint([new LmChatMessage("user", "Problem")], 0, 0, 0, 0);
        var since = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var scheduled = RunProcessor.ScheduleExplicitDeepResearch(checkpoint, "run", "Problem", new(
            ProjectId: "research-1", ProtocolVersion: 3, ResumeCheckpointId: "checkpoint-2",
            PreferredLanguages: ["de", "en"], UpdateSince: since));
        var arguments = Assert.Single(scheduled.ActiveToolCalls!).Arguments;
        Assert.Equal(3, arguments.GetProperty("protocolVersion").GetInt64());
        Assert.Equal("checkpoint-2", arguments.GetProperty("resumeCheckpointId").GetString());
        Assert.Equal(["de", "en"], arguments.GetProperty("preferredLanguages").EnumerateArray().Select(static item => item.GetString()));
        Assert.Equal(since, arguments.GetProperty("updateSince").GetDateTimeOffset());
    }

    [Fact]
    public void ExplicitResearchReportUsesOnlyVerifiedResultAndKeepsOriginalCitations()
    {
        using var document = JsonDocument.Parse("""
            {
              "success": true,
              "conclusionStatus": "stronglySupported",
              "problem": { "interpretedQuestion": "Welche Methode ist geeignet?", "assumptions": ["Python 3.11 oder neuer"] },
              "hypotheses": ["TaskGroup räumt Unteraufgaben strukturiert auf."],
              "verificationPlan": ["Zwei unabhängige Originalseiten vergleichen."],
              "findings": [{ "claim": "TaskGroup wartet auf Abbruch der Gruppe.", "sourceId": "source-1", "excerpt": "The remaining tasks in the group are cancelled." }],
              "sources": [{ "id": "source-1", "title": "Python Task Groups", "url": "https://docs.python.org/3/library/asyncio-task.html" }],
              "uncertainties": ["Verhalten älterer Python-Versionen separat prüfen."]
            }
            """);
        var result = document.RootElement;

        var report = RunProcessor.RenderExplicitDeepResearchReport(result);

        Assert.Contains("`stronglySupported`", report, StringComparison.Ordinal);
        Assert.Contains("TaskGroup wartet", report, StringComparison.Ordinal);
        Assert.Contains("The remaining tasks", report, StringComparison.Ordinal);
        Assert.Contains("https://docs.python.org/3/library/asyncio-task.html", report, StringComparison.Ordinal);
        Assert.Contains("Grenzen und offene Punkte", report, StringComparison.Ordinal);
    }
}
