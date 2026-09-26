using GoAi.Contracts;
using GoAi.Server.Core.Coding;
using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Gateway;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runs;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class CodingAgentTests
{
    private static RunRequest Request() => new(GoAiProtocol.Version, RunMode.Coding,
        [new RunMessage("user", [new ContentPart("text", "Implementiere und teste eine kleine Funktion.")])],
        ClientCapabilities: ["coding"], PreferredCodingModelId: "coding/test.gguf");

    [Fact]
    public void CodingModeRequiresWorkspaceCapabilityAndAcceptsIndependentModel()
    {
        RunRequestValidator.Validate(Request());
        Assert.Throws<ArgumentException>(() => RunRequestValidator.Validate(Request() with { ClientCapabilities = [] }));
        Assert.Throws<ArgumentException>(() => RunRequestValidator.Validate(Request() with { PreferredCodingModelId = "bad\nmodel" }));
        var router = new ModelRouter(Options.Create(new GoAiServerOptions { GeneralModelId = "general-model" }));
        var selected = router.Select(Request());
        Assert.Equal("coding", selected.Role);
        Assert.Equal("coding/test.gguf", selected.ModelId);
        Assert.Equal(ModelContextProfiles.Qwen38Maximum, selected.ContextLength);
    }

    [Fact]
    public void CodingUsesOnlyItsSmallDirectCatalogByDefault()
    {
        var catalog = new AgentToolCatalog();
        var tools = catalog.GetAvailableTools(Request());
        Assert.Equal(12, tools.Count);
        Assert.All(tools, tool => Assert.StartsWith("coding.", tool.Name, StringComparison.Ordinal));
        Assert.All(tools.Where(tool => tool.Name != "coding.updatePlan"), tool => Assert.False(tool.ServerSide));
        var definitions = RunProcessor.CreateModelToolDefinitions(tools, null, directTools: true);
        Assert.Equal(12, definitions.Length);
        Assert.DoesNotContain(definitions, tool => tool.Name == AgentToolCatalog.SelectorToolName);
        var generalTools = catalog.GetAvailableTools(Request() with { Mode = RunMode.General });
        Assert.DoesNotContain(generalTools, tool => tool.Name.StartsWith("coding.", StringComparison.Ordinal));
    }

    [Fact]
    public void CodingPromptRequiresEvidenceAndAvoidsGeneralCoordinator()
    {
        var prompt = RunProcessor.CreateInitialMessages(Request(), "coding", [ClientToolNames.CodingRead])[0].Content!;
        Assert.Contains("Coding-Agent", prompt, StringComparison.Ordinal);
        Assert.Contains("sha256", prompt, StringComparison.Ordinal);
        Assert.Contains("keine Testergebnisse", prompt, StringComparison.Ordinal);
        Assert.Contains("Git-Diffs nur in erkannten Git-Projekten", prompt, StringComparison.Ordinal);
        Assert.Contains("ein reiner Dateiwerkzeug-Auftrag erfordert keinen Git-Prozess", prompt, StringComparison.Ordinal);
        Assert.Contains("vor jedem Werkzeugaufruf in einem kurzen sichtbaren Satz", prompt, StringComparison.Ordinal);
        Assert.Contains("kündige keinen Erfolg vor dem Werkzeugergebnis an", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("TGA-Fachplanung", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ProposalExplanationDescribesTheActionWithoutClaimingSuccess()
    {
        var catalog = new AgentToolCatalog();
        var edit = catalog.Resolve(ClientToolNames.CodingEdit, catalog.GetAvailableTools(Request()));
        var summary = RunProcessor.CreateProposalSummary(edit, JsonSerializer.SerializeToElement(new { path = "src/add.py" }));
        Assert.Equal("Ich wende die vorgeschlagenen Änderungen auf „src/add.py“ an.", summary);
        Assert.DoesNotContain("erfolgreich", summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ClientToolNames.CodingRead, "{\"path\":\"a.cs\",\"maximumLines\":0}")]
    [InlineData(ClientToolNames.CodingCommand, "{\"executable\":\"dotnet\",\"arguments\":[],\"timeoutSeconds\":-1}")]
    [InlineData(ClientToolNames.CodingEdit, "{\"path\":\"a.cs\",\"oldText\":\"a\",\"newText\":\"b\",\"expectedSha256\":\"wrong\"}")]
    [InlineData(ClientToolNames.CodingWrite, "{\"path\":\"a.cs\",\"content\":\"x\",\"outside\":true}")]
    public void CodingRejectsMalformedOrUnboundedArguments(string name, string json)
    {
        var catalog = new AgentToolCatalog();
        var tool = catalog.Resolve(name, catalog.GetAvailableTools(Request()));
        Assert.Throws<ArgumentException>(() => catalog.Validate(tool, JsonSerializer.Deserialize<JsonElement>(json)));
    }

    [Fact]
    public void IdenticalFailedCallsStopButChangedArgumentsAreAllowed()
    {
        var args = JsonSerializer.SerializeToElement(new { path = "missing.cs" });
        var first = new LmToolCall("call1", ClientToolNames.CodingRead, args);
        var second = first with { Id = "call2" };
        LmChatMessage[] history =
        [
            new("assistant", null, ToolCalls: [first]), new("tool", "{\"status\":\"failed\"}", ToolCallId: first.Id),
            new("assistant", null, ToolCalls: [second]), new("tool", "{\"status\":\"failed\"}", ToolCallId: second.Id),
        ];
        Assert.Throws<AgentRunLimitException>(() => CodingLoopGuard.ThrowIfRepeatedFailure(history, first with { Id = "call3" }));
        CodingLoopGuard.ThrowIfRepeatedFailure(history, first with { Arguments = JsonSerializer.SerializeToElement(new { path = "other.cs" }) });
    }

    [Fact]
    public void ToolResultBudgetRemainsValidJsonEvenForControlCharacters()
    {
        var bounded = CodingLoopGuard.BoundToolResult(new string('\u0001', 50_000));
        Assert.True(bounded.Length <= CodingLoopGuard.MaximumToolResultCharacters);
        using var parsed = JsonDocument.Parse(bounded);
        Assert.True(parsed.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void CodingTimeoutPreservesPhaseAndLimitButBoundsUntrustedDetail()
    {
        const string loading = "Das Coding-Modell wurde nicht innerhalb von 5 Minuten geladen.";
        const string generation = "Der Coding-Modellturn überschritt 180 Sekunden.";
        Assert.Equal(loading, RunProcessor.ResolveTimeoutFailureMessage(RunMode.Coding, loading));
        Assert.Equal(generation, RunProcessor.ResolveTimeoutFailureMessage(RunMode.Coding, generation));
        Assert.Equal("Der AI-Lauf hat sein Zeitlimit erreicht.", RunProcessor.ResolveTimeoutFailureMessage(RunMode.General, generation));
        var bounded = RunProcessor.ResolveTimeoutFailureMessage(RunMode.Coding, "Konkrete Ursache\r\n" + new string('x', 2_000));
        Assert.Equal(1_000, bounded.Length);
        Assert.DoesNotContain(bounded, char.IsControl);
    }

}
