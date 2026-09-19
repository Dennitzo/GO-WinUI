using System.Text.Json;
using GoAi.Contracts;
using GoAi.Server.Core.Coding;
using GoAi.Server.Core.Gateway;
using GoAi.Server.Core.Runs;

namespace GoAi.Server.Tests;

public sealed class WorkspaceToolAvailabilityTests
{
    [Theory]
    [InlineData(RunMode.General)]
    [InlineData(RunMode.Coding)]
    public void BothModesCanSelectAndValidateEveryWorkspaceTool(RunMode mode)
    {
        var request = new RunRequest(GoAiProtocol.Version, mode, [new("user", [new("text", "Prüfe das Projekt visuell und erstelle Dokumente und eine Blender-Szene.")])],
            ClientCapabilities: ["coding", "coding.evidence", "workspace", "documentIo", "documents", "document-agent", "visual-tools", "blender", "bricscad", "coding-isolated-subagents"],
            AllowedServerTools: ["media.analyze", "media.inspect", "image.generate", "web.search", "web.fetch", "web.deepResearch", "speech.synthesize", "youtube.search", "math.evaluate", "context.embed", "context.retrieve"]);
        RunRequestValidator.Validate(request);
        var catalog = new AgentToolCatalog();
        var tools = catalog.GetAvailableTools(request);
        foreach (var name in new[] { "coding.read", "coding.write", "coding.edit", "coding.command", "coding.readOutput", "coding.searchRunEvidence",
            "document.read", "document.create", "documents.list", "documents.search", "documents.readPages",
            WorkspaceTools.ImageInput, WorkspaceTools.Blender, WorkspaceTools.DocumentAgent, WorkspaceTools.Open, "speech.synthesize", "media.analyze", "media.inspect",
            "image.generate", "bricscad.action", "web.search", "web.fetch", "web.deepResearch", "youtube.search", "math.evaluate", "context.embed", "context.retrieve" })
            Assert.Equal(name, catalog.ResolveSelection(JsonSerializer.SerializeToElement(new { name }), tools).Name);
        catalog.Validate(catalog.Resolve(WorkspaceTools.ImageInput, tools), JsonSerializer.SerializeToElement(new { operation = "file", path = "screenshots/app.png" }));
        catalog.Validate(catalog.Resolve(WorkspaceTools.Blender, tools), JsonSerializer.SerializeToElement(new { operation = "run", path = "scene.py", expectedSha256 = new string('a', 64) }));
        catalog.Validate(catalog.Resolve(WorkspaceTools.DocumentAgent, tools), JsonSerializer.SerializeToElement(new { task = "Erstelle einen Projektbericht als DOCX." }));
        var child = CodingSubagentService.DelegatableTools(catalog, request);
        Assert.Contains(child, t => t.Name == WorkspaceTools.ImageInput);
        Assert.Contains(child, t => t.Name == "media.analyze");
        Assert.Contains(child, t => t.Name == WorkspaceTools.Blender);
        Assert.Contains(child, t => t.Name == ClientToolNames.DocumentCreate);
        Assert.DoesNotContain(child, t => t.Name == WorkspaceTools.DocumentAgent);
    }

    [Fact]
    public void WorkspaceToolsAreNotAdvertisedWithoutClientSupport()
    {
        var request = new RunRequest(GoAiProtocol.Version, RunMode.General, [new("user", [new("text", "Hallo")])], ClientCapabilities: [], AllowedServerTools: []);
        Assert.Empty(new AgentToolCatalog().GetAvailableTools(request));
    }

    [Theory]
    [InlineData("blender.execute", "{\"operation\":\"run\",\"path\":\"scene.py\"}")]
    [InlineData("blender.execute", "{\"operation\":\"run\",\"path\":\"scene.py\",\"expectedSha256\":\"old\"}")]
    [InlineData("image.input", "{\"operation\":\"capture\"}")]
    [InlineData("image.input", "{\"operation\":\"file\",\"path\":\"x.png\",\"command\":\"bad\"}")]
    [InlineData("document.agent", "{\"task\":\"\"}")]
    public void MalformedWorkspaceCallsAreRejected(string name, string json) =>
        Assert.Throws<ArgumentException>(() => WorkspaceTools.Validate(name, JsonDocument.Parse(json).RootElement));
}
