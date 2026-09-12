using GoAi.Contracts;
using GoAi.Server.Core.Coding;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runs;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class CodingContextToolTests
{
    private static readonly string[] ContextTools = [ClientToolNames.CodingSearchHistory, ClientToolNames.CodingSearchKnowledge, ClientToolNames.CodingRenderHtml];
    private static RunRequest Request(RunMode mode = RunMode.Coding) => new(GoAiProtocol.Version, mode,
        [new("user", [new("text", "Nutze den relevanten Kontext dieser Sitzung.")])], ClientCapabilities: ["coding"], AllowedServerTools: []);

    [Fact]
    public void SessionContextAndPreviewToolsRequireOnlyCodingCapabilityAndRemainReadOnly()
    {
        var catalog = new AgentToolCatalog();
        var coding = catalog.GetAvailableTools(Request());
        foreach (var name in ContextTools)
        {
            var tool = catalog.Resolve(name, coding);
            Assert.False(tool.ServerSide);
            Assert.Equal(ToolRiskClass.ReadOnly, tool.RiskClass);
        }
        Assert.DoesNotContain(catalog.GetAvailableTools(Request(RunMode.General)), tool => ContextTools.Contains(tool.Name, StringComparer.Ordinal));
        Assert.Contains("aktuellen GO-Sitzung", catalog.Resolve(ClientToolNames.CodingSearchHistory, coding).Description, StringComparison.Ordinal);
        Assert.Contains("aktuellen GO-Sitzung", catalog.Resolve(ClientToolNames.CodingSearchKnowledge, coding).Description, StringComparison.Ordinal);
        Assert.Contains("keine globalen Suchrechte", CodingAgentPolicy.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("Python- und PowerShell-Aufgaben", CodingAgentPolicy.SystemPrompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ClientToolNames.CodingSearchHistory)]
    [InlineData(ClientToolNames.CodingSearchKnowledge)]
    public void SessionSearchAcceptsEightResultsButCannotSelectAnotherScope(string name)
    {
        var catalog = new AgentToolCatalog();
        var tool = catalog.Resolve(name, catalog.GetAvailableTools(Request()));
        catalog.Validate(tool, JsonSerializer.SerializeToElement(new { query = "API decision", maximumResults = 8 }));
        Assert.Throws<ArgumentException>(() => catalog.Validate(tool, JsonSerializer.SerializeToElement(new { query = "API", maximumResults = 9 })));
        Assert.Throws<ArgumentException>(() => catalog.Validate(tool, JsonSerializer.SerializeToElement(new { query = new string('x', 513) })));
        Assert.Throws<ArgumentException>(() => catalog.Validate(tool, JsonSerializer.SerializeToElement(new { query = "API", sessionId = "another-session" })));
        Assert.Throws<ArgumentException>(() => catalog.Validate(tool, JsonSerializer.SerializeToElement(new { query = "API", scope = "global" })));
    }

    [Fact]
    public void HtmlPreviewIsBoundedAndCannotChooseFilesOrNetworkPrivileges()
    {
        var catalog = new AgentToolCatalog();
        var tool = catalog.Resolve(ClientToolNames.CodingRenderHtml, catalog.GetAvailableTools(Request()));
        catalog.Validate(tool, JsonSerializer.SerializeToElement(new { code = "<h1>Vorschau</h1>", title = "Beispiel" }));
        Assert.Throws<ArgumentException>(() => catalog.Validate(tool, JsonSerializer.SerializeToElement(new { code = "" })));
        Assert.Throws<ArgumentException>(() => catalog.Validate(tool, JsonSerializer.SerializeToElement(new { code = new string('x', 16_001) })));
        Assert.Throws<ArgumentException>(() => catalog.Validate(tool, JsonSerializer.SerializeToElement(new { code = "<p>ok</p>", title = new string('x', 101) })));
        Assert.Throws<ArgumentException>(() => catalog.Validate(tool, JsonSerializer.SerializeToElement(new { code = "<p>ok</p>", path = "output.html" })));
        Assert.Throws<ArgumentException>(() => catalog.Validate(tool, JsonSerializer.SerializeToElement(new { code = "<p>ok</p>", allowNetwork = true })));
    }

    [Fact]
    public void OnePreviewReceiptBlocksFurtherRendersWhilePendingReplayAndOtherToolsRemainValid()
    {
        var first = new LmToolCall("render-1", ClientToolNames.CodingRenderHtml, JsonSerializer.SerializeToElement(new { code = "<p>one</p>" }));
        LmChatMessage[] pending = [new("assistant", null, ToolCalls: [first])];
        CodingLoopGuard.ThrowIfRenderAlreadyUsed(pending, first);
        LmChatMessage[] completed = [.. pending, new("tool", "{\"status\":\"completed\",\"result\":{\"rendered\":true}}", ToolCallId: first.Id)];
        Assert.Throws<ArgumentException>(() => CodingLoopGuard.ThrowIfRenderAlreadyUsed(completed, first with { Id = "render-2" }));
        Assert.Throws<ArgumentException>(() => CodingLoopGuard.ThrowIfRenderAlreadyUsed(completed.ToArray(), first));
        CodingLoopGuard.ThrowIfRenderAlreadyUsed(completed, first with { Name = ClientToolNames.CodingRead });
    }

    [Fact]
    public void ReusedProviderCallIdFromEarlierNonRenderToolDoesNotConsumePreviewAllowance()
    {
        var current = new LmToolCall("reused-id", ClientToolNames.CodingRenderHtml, JsonSerializer.SerializeToElement(new { code = "<p>one</p>" }));
        LmChatMessage[] history =
        [
            new("assistant", null, ToolCalls: [current with { Name = ClientToolNames.CodingRead }]),
            new("tool", "{\"status\":\"completed\"}", ToolCallId: current.Id),
            new("assistant", null, ToolCalls: [current]),
        ];
        CodingLoopGuard.ThrowIfRenderAlreadyUsed(history, current);
    }
}
