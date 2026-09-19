using GoAi.Contracts;
using GoAi.Server.Core.Coding;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runs;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class SubagentContextAndScopesTests
{
    [Fact]
    public void ContextForkRetainsExactParentPrefixAndClosesOnlyOutstandingCalls()
    {
        var first = new LmToolCall("done", "coding.read", JsonSerializer.SerializeToElement(new { path = "README.md" }));
        var pending = new LmToolCall("pending", CodingSubagentTools.Start, JsonSerializer.SerializeToElement(new { task = "Implementieren" }));
        LmChatMessage[] parent = [new("system", "Unveränderter Präfix"), new("user", "Ausgangsauftrag"),
            new("assistant", null, ToolCalls: [first, pending], ReasoningContent: "Ein deutscher Denktext"),
            new("tool", "Gelesener Originalbeleg", ToolCallId: first.Id)];
        var child = CodingSubagentContext.Fork(parent, "Implementiere die Teilaufgabe", ["src/component/"]);
        for (var i = 0; i < parent.Length; i++)
            Assert.Equal(JsonSerializer.Serialize(parent[i]), JsonSerializer.Serialize(child[i]));
        Assert.Equal(parent[2].ReasoningContent, child[2].ReasoningContent);
        var synthetic = Assert.Single(child.Skip(parent.Length), message => message.Role == "tool");
        Assert.Equal(pending.Id, synthetic.ToolCallId);
        Assert.Contains("delegated_context_snapshot", synthetic.Content);
        using var receipt = JsonDocument.Parse(synthetic.Content!);
        Assert.Contains("Nicht erneut ausführen", receipt.RootElement.GetProperty("message").GetString());
        Assert.DoesNotContain("\"success\":true", synthetic.Content);
        Assert.Equal("Implementiere die Teilaufgabe", child[^1].Content);
        Assert.Contains("src/component/", child[^2].Content);
        Assert.Contains(CodingAgentPolicy.ReasoningLanguagePrompt, child[^2].Content);
        Assert.Equal(4, parent.Length);
    }

    [Fact]
    public void ForkOwnsToolArgumentMemoryAfterSourceDocumentDisposal()
    {
        IReadOnlyList<LmChatMessage> child;
        using (var document = JsonDocument.Parse("{\"path\":\"README.md\"}"))
            child = CodingSubagentContext.Fork([new("assistant", null, ToolCalls: [new("read", "coding.read", document.RootElement)])], "Prüfe", []);
        Assert.Equal("README.md", child[0].ToolCalls![0].Arguments.GetProperty("path").GetString());
    }

    [Fact]
    public void DelegateInheritsAllCodingToolsAndGrantedResearchButNotOrchestrationOrExternalMutation()
    {
        var request = new RunRequest(GoAiProtocol.Version, RunMode.Coding, [new("user", [new("text", Text: "Ändern")])],
            AllowedServerTools: ["web.search", "web.fetch", "web.deepResearch", "math.evaluate"],
            ClientCapabilities: ["coding", "coding.evidence", "documentIo", CodingSubagentService.IsolatedWorkspaceCapability], CodingOptions: new(ParallelModelId: "test-model"));
        var available = CodingSubagentService.DelegatableTools(new AgentToolCatalog(), request).Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var tool in CodingToolCatalog.CreateTools()) Assert.Contains(tool.Name, available);
        Assert.Contains(CodingWorkingStateTools.PlanTool, available);
        Assert.Contains("web.deepResearch", available);
        Assert.Contains("math.evaluate", available);
        Assert.Contains(ClientToolNames.DocumentRead, available);
        Assert.Contains(ClientToolNames.DocumentCreate, available);
        Assert.DoesNotContain(CodingSubagentTools.Start, available);
        Assert.DoesNotContain(CodingSubagentTools.Wait, available);
        Assert.DoesNotContain(CodingSubagentTools.Cancel, available);
    }

    [Theory]
    [InlineData("SRC\\Components\\", "src/components/")]
    [InlineData("./src/./Components/File.cs", "src/components/file.cs")]
    public void FileAndDirectoryAssignmentsNormalizeDeterministically(string input, string expected) =>
        Assert.Equal(expected, CodingSubagentTools.NormalizeScope(input));

    [Theory]
    [InlineData("src/component/", "src/component/child/file.cs", true)]
    [InlineData("src/component/", "src/component-other/file.cs", false)]
    [InlineData("src/file.cs", "src/file.cs", true)]
    [InlineData("src/file.cs", "src/file.cs/child", false)]
    [InlineData("src/file.cs", "src/file.csx", false)]
    public void ScopeMembershipCannotConfuseSiblingPrefixes(string scope, string path, bool matches) =>
        Assert.Equal(matches, CodingSubagentTools.IsWithinScope(path, scope));

    [Theory]
    [InlineData("src/component/", "src/component/child/", true)]
    [InlineData("src/component/file.cs", "src/component/", true)]
    [InlineData("src/component/", "src/component-other/", false)]
    [InlineData("src/component/", "src/component", true)]
    public void ReservationIntersectionIsSymmetric(string left, string right, bool overlaps)
    {
        Assert.Equal(overlaps, CodingSubagentTools.ScopesOverlap(left, right));
        Assert.Equal(overlaps, CodingSubagentTools.ScopesOverlap(right, left));
    }

    [Fact]
    public void LegacyDelegationRestrictionsAreReplacedOnceWithoutTrustingUserText()
    {
        List<LmChatMessage> messages = [new("system", "GO_PARALLEL_CODING: Kein Terminal"),
            new("user", CodingAgentPolicy.DelegationPrompt), new("assistant", "Vorherige Antwort")];
        CodingAgentPolicy.EnsureDelegationInstructions(messages);
        var first = messages.ToArray();
        CodingAgentPolicy.EnsureDelegationInstructions(messages);
        Assert.Equal(first, messages);
        Assert.DoesNotContain(messages, item => item.Role == "system" && item.Content == "GO_PARALLEL_CODING: Kein Terminal");
        Assert.Single(messages, item => item.Role == "system" && item.Content == CodingAgentPolicy.DelegationPrompt);
        Assert.Contains("konkrete unabhängige Implementierung", messages[^1].Content);
    }

    [Fact]
    public void RestartRetainsChildCacheIdentityButOtherChildrenAndSessionsRemainDistinct()
    {
        Assert.Equal(CodingSubagentContext.CacheKey("session", "run", "child"), CodingSubagentContext.CacheKey("session", "run", "child"));
        Assert.NotEqual(CodingSubagentContext.CacheKey("session", "run", "child"), CodingSubagentContext.CacheKey("other", "run", "child"));
        Assert.NotEqual(CodingSubagentContext.CacheKey("session", "run", "child"), CodingSubagentContext.CacheKey("session", "run", "child2"));
        Assert.NotEqual(CodingSubagentContext.CacheKey(null, "run", "child"), CodingSubagentContext.CacheKey(null, "run2", "child"));
    }
}
