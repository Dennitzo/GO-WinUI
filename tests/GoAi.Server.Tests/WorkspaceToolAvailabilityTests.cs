using System.Text.Json;
using GoAi.Contracts;
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
            ClientCapabilities: ["coding", "coding.evidence", "workspace", "documentIo", "documents", "visual-tools", "blender", "bricscad"],
            AllowedServerTools: ["media.analyze", "media.inspect", "image.generate", "web.search", "web.fetch", "web.deepResearch", "speech.synthesize", "youtube.search", "math.evaluate", "context.embed", "context.retrieve"]);
        RunRequestValidator.Validate(request);
        var catalog = new AgentToolCatalog();
        var tools = catalog.GetAvailableTools(request);
        foreach (var name in new[] { "coding.read", "coding.write", "coding.edit", "coding.command", "coding.readOutput", "coding.searchRunEvidence",
            "document.read", "document.create", "documents.list", "documents.search", "documents.readPages",
            WorkspaceTools.ImageInput, WorkspaceTools.Blender, WorkspaceTools.Open, "speech.synthesize", "media.analyze", "media.inspect",
            "image.generate", "bricscad.action", "web.search", "web.fetch", "web.deepResearch", "youtube.search", "math.evaluate", "context.embed", "context.retrieve" })
            Assert.Equal(name, catalog.ResolveSelection(JsonSerializer.SerializeToElement(new { name }), tools).Name);
        catalog.Validate(catalog.Resolve(WorkspaceTools.ImageInput, tools), JsonSerializer.SerializeToElement(new { operation = "file", path = "screenshots/app.png" }));
        catalog.Validate(catalog.Resolve(WorkspaceTools.Blender, tools), JsonSerializer.SerializeToElement(new { operation = "run", path = "scene.py", expectedSha256 = new string('a', 64) }));
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
    public void MalformedWorkspaceCallsAreRejected(string name, string json) =>
        Assert.Throws<ArgumentException>(() => WorkspaceTools.Validate(name, JsonDocument.Parse(json).RootElement));
}
