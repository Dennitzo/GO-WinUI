using GoAi.Contracts;
using GoAi.Server.Core.Coding;
using GoAi.Server.Core.Policies;
using GoAi.Server.Core.Runs;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class BlenderVisionPolicyTests
{
    [Fact]
    public void BlenderAndVisualToolsAreAvailableForReportedWorkspaceCapabilities()
    {
        var catalog = new AgentToolCatalog();
        var request = CreateRequest(["workspace", "visual-tools", "blender"]);

        var tools = catalog.GetAvailableTools(request);

        Assert.Contains(tools, static tool => tool.Name == WorkspaceTools.Blender);
        Assert.Contains(tools, static tool => tool.Name == WorkspaceTools.ImageInput);
        Assert.Contains(tools, static tool => tool.Name == WorkspaceTools.Open);
        Assert.Contains(tools, static tool => tool.Name == "media.analyze");
    }

    [Fact]
    public void BlenderDescriptionDescribesTheVisualFeedbackLoop()
    {
        var catalog = new AgentToolCatalog();
        var blender = catalog.Resolve(WorkspaceTools.Blender,
            catalog.GetAvailableTools(CreateRequest(["blender", "visual-tools", "workspace"])));

        Assert.Contains("Rückkopplungsablauf", blender.Description, StringComparison.Ordinal);
        Assert.Contains("DeepSeek-Vision-Modell", blender.Description, StringComparison.Ordinal);
        Assert.Contains("höchstens zwei", blender.Description, StringComparison.Ordinal);
        Assert.Contains("nicht ungefragt überschreiben", blender.Description, StringComparison.Ordinal);
        Assert.Contains("keine visuelle Prüfung", blender.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void MediaAnalysisDescriptionNamesTheSelectedDeepSeekVisionModel()
    {
        var catalog = new AgentToolCatalog();
        var media = catalog.Resolve("media.analyze", catalog.GetAvailableTools(CreateRequest(null)));

        Assert.Contains("ausgewählten DeepSeek-Modell", media.Description, StringComparison.Ordinal);
        Assert.Contains("integriertem Vision", media.Description, StringComparison.Ordinal);
        Assert.Contains("Modell-ID", media.Description, StringComparison.Ordinal);
        Assert.Contains("Fallback", media.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneralAndCodingPoliciesDescribeTheBlenderFeedbackLoop()
    {
        Assert.Contains("Blender-Rückkopplung", CodingAgentPolicy.WorkspaceDependenciesPrompt, StringComparison.Ordinal);
        Assert.Contains("höchstens zwei", CodingAgentPolicy.WorkspaceDependenciesPrompt, StringComparison.Ordinal);
        Assert.Contains("nicht ungefragt überschreiben", CodingAgentPolicy.WorkspaceDependenciesPrompt, StringComparison.Ordinal);
        Assert.Contains("keine visuelle Prüfung", CodingAgentPolicy.WorkspaceDependenciesPrompt, StringComparison.Ordinal);
        Assert.Contains("Blender-Aufträge", GeneralAgentPolicies.GeneralCoordinator, StringComparison.Ordinal);
        Assert.Contains("höchstens zwei", GeneralAgentPolicies.GeneralCoordinator, StringComparison.Ordinal);
        Assert.Contains("nicht ungefragt überschreiben", GeneralAgentPolicies.GeneralCoordinator, StringComparison.Ordinal);
    }

    [Fact]
    public void BlenderValidationRejectsInvalidOperationsHashesAndTimeouts()
    {
        var invalidOperation = JsonSerializer.SerializeToElement(new
        {
            operation = "render",
            path = "scene.py",
            expectedSha256 = new string('a', 64),
            timeoutSeconds = 300,
        });
        Assert.Throws<ArgumentException>(() => WorkspaceTools.Validate(WorkspaceTools.Blender, invalidOperation));

        var shortHash = JsonSerializer.SerializeToElement(new
        {
            operation = "run",
            path = "scene.py",
            expectedSha256 = "abc",
            timeoutSeconds = 300,
        });
        Assert.Throws<ArgumentException>(() => WorkspaceTools.Validate(WorkspaceTools.Blender, shortHash));

        var zeroTimeout = JsonSerializer.SerializeToElement(new
        {
            operation = "run",
            path = "scene.py",
            expectedSha256 = new string('a', 64),
            timeoutSeconds = 0,
        });
        Assert.Throws<ArgumentException>(() => WorkspaceTools.Validate(WorkspaceTools.Blender, zeroTimeout));
    }

    [Fact]
    public void ImageInputRejectsMissingPathForFileOperation()
    {
        var missingPath = JsonSerializer.SerializeToElement(new { operation = "file" });
        Assert.Throws<ArgumentException>(() => WorkspaceTools.Validate(WorkspaceTools.ImageInput, missingPath));
    }

    private static RunRequest CreateRequest(IReadOnlyList<string>? capabilities) => new(
        GoAiProtocol.Version,
        RunMode.General,
        [new RunMessage("user", [new ContentPart("text", "Test")])],
        ClientCapabilities: capabilities);
}
