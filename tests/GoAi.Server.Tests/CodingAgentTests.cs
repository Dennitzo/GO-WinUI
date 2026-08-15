using GoAi.Contracts;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runs;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class CodingAgentTests
{
    private static readonly string[] CSharpGlobs = ["**/*.cs"];
    private static readonly string[] SearchTerms = ["SpeechRecognition", "speech", "voice"];

    [Fact]
    public void PipeAndArraySearchesHaveTheSameSemanticFingerprint()
    {
        var pipe = JsonSerializer.SerializeToElement(new
        {
            path = ".",
            query = "voice|speech|SpeechRecognition",
            matchMode = "literal",
            includeGlobs = CSharpGlobs,
        });
        var array = JsonSerializer.SerializeToElement(new
        {
            path = ".",
            queries = SearchTerms,
            matchMode = "literal",
            includeGlobs = CSharpGlobs,
        });

        Assert.Equal(
            RunProcessor.CreateSearchFingerprint(pipe),
            RunProcessor.CreateSearchFingerprint(array));
    }

    [Fact]
    public void ContextPlannerKeepsLatestPromptAndToolIdentityBelowThe262KLimit()
    {
        var toolCall = new LmToolCall(
            "tool-read-1",
            ClientToolNames.FileSystemReadMany,
            JsonSerializer.SerializeToElement(new
            {
                items = Enumerable.Range(0, 2_000).Select(index => new { path = $"src/file-{index}.cs" }).ToArray(),
            }));
        var messages = new List<LmChatMessage>
        {
            new("system", "Coding-Regeln"),
            new("user", "FrÃ¼here Aufgabe"),
            new("assistant", new string('a', 900_000), [toolCall]),
            new("tool", new string('b', 300_000), ToolCallId: "tool-read-1"),
            new("user", "Analysiere die Sprachsteuerung und nenne konkrete Dateien."),
        };

        var plan = CodingContextPlanner.Prepare(messages, 262_144, 8_192);

        Assert.True(plan.WasCompacted);
        Assert.True(plan.EstimatedInputTokens <= plan.InputTokenBudget);
        Assert.Equal(
            "Analysiere die Sprachsteuerung und nenne konkrete Dateien.",
            plan.Messages.Last(message => message.Role == "user").Content);
        Assert.Contains(plan.Messages, message => message.ToolCallId == "tool-read-1");
        Assert.Contains(
            plan.Messages.SelectMany(message => message.ToolCalls ?? []),
            call => call.Id == "tool-read-1");
    }

    [Fact]
    public void CodingCatalogAdvertisesLanguageNeutralWorkspaceAndProcessTools()
    {
        var request = new RunRequest(
            GoAiProtocol.Version,
            RunMode.Code,
            [new RunMessage("user", [new ContentPart("text", "Arbeite im Repository")])],
            ClientCapabilities: ["filesystem", "code", "process"],
            AllowedServerTools: ["math.evaluate"]);

        var tools = new AgentToolCatalog().GetAvailableTools(request);

        Assert.Contains(tools, tool => tool.Name == ClientToolNames.WorkspaceMap);
        Assert.Contains(tools, tool => tool.Name == ClientToolNames.FileSystemFindFiles);
        Assert.Contains(tools, tool => tool.Name == ClientToolNames.FileSystemReadMany);
        Assert.Contains(tools, tool => tool.Name == ClientToolNames.FileSystemWriteText);
        Assert.Contains(tools, tool => tool.Name == ClientToolNames.ProcessRun);
        Assert.DoesNotContain(tools, tool => tool.Name == "context.embed");
    }
}
