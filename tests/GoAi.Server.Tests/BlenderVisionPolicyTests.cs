using GoAi.Contracts;
using GoAi.Server.Core.Coding;
using GoAi.Server.Core.Policies;
using GoAi.Server.Core.Runs;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class BlenderVisionPolicyTests
{
    private static readonly string[] BlenderOperations = ["info", "scaffold", "stage", "preview", "run", "inspect", "render", "open"];
    private static readonly string[] AllViews = ["perspective", "front", "right", "top", "back", "left"];

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
        Assert.Contains(BlenderAuthoringGuide.WorkflowPrompt, CodingAgentPolicy.WorkspaceDependenciesPrompt, StringComparison.Ordinal);
        Assert.Contains(BlenderAuthoringGuide.WorkflowPrompt, GeneralAgentPolicies.GeneralCoordinator, StringComparison.Ordinal);
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
            operation = "bake",
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

    [Theory]
    [InlineData(RunMode.General)]
    [InlineData(RunMode.Coding)]
    public void BothModesExposeAndValidateTheCompleteBlenderWorkflow(RunMode mode)
    {
        var request = CreateRequest(["workspace", "coding", "blender", "visual-tools"]) with { Mode = mode };
        var catalog = new AgentToolCatalog();
        var tool = catalog.Resolve(WorkspaceTools.Blender, catalog.GetAvailableTools(request));
        var arguments = new[]
        {
            """{"operation":"info"}""",
            """{"operation":"info","path":"rover/scene_v001.blend"}""",
            """{"operation":"scaffold","path":"rover","brief":"Sechsrädriger Forschungsrover mit zwei Solarpaneelen."}""",
            """{"operation":"stage","path":"rover/01_blockout.py","expectedSha256":"HASH","outputPath":"rover/scene_v001.blend","label":"01 Hauptformen"}""",
            """{"operation":"stage","path":"rover/02_fahrwerk.py","expectedSha256":"HASH","baseScene":"rover/scene_v001.blend","baseSceneSha256":"HASH","outputPath":"rover/scene_v002.blend","label":"02 Fahrwerk","timeoutSeconds":3600}""",
            """{"operation":"preview","path":"rover/scene_v002.blend","expectedSha256":"HASH","label":"Fahrwerk prüfen"}""",
            """{"operation":"run","path":"rover/scene.py","expectedSha256":"HASH","timeoutSeconds":300}""",
            """{"operation":"inspect","path":"rover/scene_v001.blend","expectedSha256":"HASH","outputDirectory":"rover/checks/v001"}""",
            """{"operation":"render","path":"rover/scene_v001.blend","expectedSha256":"HASH","outputDirectory":"rover/renders/v001","views":["perspective","front","right","top"],"resolution":768,"samples":32,"timeoutSeconds":3600}""",
            """{"operation":"open","path":"rover/scene_v001.blend"}""",
        };

        foreach (var json in arguments)
        {
            using var document = JsonDocument.Parse(json.Replace("HASH", new string('a', 64), StringComparison.Ordinal));
            catalog.Validate(tool, document.RootElement);
        }
        Assert.Equal(BlenderAuthoringGuide.ToolDescription, tool.Description);
        Assert.Equal(BlenderOperations,
            tool.Schema.GetProperty("properties").GetProperty("operation").GetProperty("enum")
                .EnumerateArray().Select(static value => value.GetString()).ToArray());
    }

    [Theory]
    [InlineData("inspect", "expectedSha256")]
    [InlineData("inspect", "path")]
    [InlineData("inspect", "outputDirectory")]
    [InlineData("render", "expectedSha256")]
    [InlineData("render", "path")]
    [InlineData("render", "outputDirectory")]
    public void SceneInspectionAndRenderingRequireAnIdentifiedSourceAndFreshOutputTarget(string operation, string missing)
    {
        var arguments = new Dictionary<string, object>
        {
            ["operation"] = operation,
            ["path"] = "rover/scene_v001.blend",
            ["expectedSha256"] = new string('a', 64),
            ["outputDirectory"] = "rover/checks/v001",
        };
        arguments.Remove(missing);

        Assert.Throws<ArgumentException>(() => WorkspaceTools.Validate(WorkspaceTools.Blender,
            JsonSerializer.SerializeToElement(arguments)));
    }

    [Theory]
    [InlineData("views", "[]")]
    [InlineData("views", "[\"front\",\"front\"]")]
    [InlineData("views", "[\"underside\"]")]
    [InlineData("views", "[1]")]
    [InlineData("views", "\"front\"")]
    [InlineData("resolution", "127")]
    [InlineData("resolution", "2049")]
    [InlineData("resolution", "768.5")]
    [InlineData("samples", "0")]
    [InlineData("samples", "129")]
    [InlineData("samples", "\"32\"")]
    [InlineData("expectedSha256", "\"abc\"")]
    [InlineData("outputDirectory", "\"\"")]
    public void RenderRejectsInvalidViewsQualityAndSourceArguments(string property, string invalidJson)
    {
        var arguments = new Dictionary<string, object>
        {
            ["operation"] = "render",
            ["path"] = "rover/scene_v001.blend",
            ["expectedSha256"] = new string('a', 64),
            ["outputDirectory"] = "rover/renders/v001",
        };
        using var invalid = JsonDocument.Parse(invalidJson);
        arguments[property] = invalid.RootElement;

        Assert.Throws<ArgumentException>(() => WorkspaceTools.Validate(WorkspaceTools.Blender,
            JsonSerializer.SerializeToElement(arguments)));
    }

    [Theory]
    [InlineData(128, 1)]
    [InlineData(2048, 128)]
    public void RenderAcceptsQualityBoundariesAndAllDistinctViews(int resolution, int samples)
    {
        WorkspaceTools.Validate(WorkspaceTools.Blender, JsonSerializer.SerializeToElement(new
        {
            operation = "render",
            path = "rover/scene_v001.blend",
            expectedSha256 = new string('a', 64),
            outputDirectory = "rover/renders/v001",
            views = AllViews,
            resolution,
            samples,
        }));
    }

    [Fact]
    public void ScaffoldRequiresAProjectPathAndBoundsTheBrief()
    {
        WorkspaceTools.Validate(WorkspaceTools.Blender,
            JsonSerializer.SerializeToElement(new { operation = "scaffold", path = "rover", brief = new string('x', 8000) }));
        Assert.Throws<ArgumentException>(() => WorkspaceTools.Validate(WorkspaceTools.Blender,
            JsonSerializer.SerializeToElement(new { operation = "scaffold" })));
        Assert.Throws<ArgumentException>(() => WorkspaceTools.Validate(WorkspaceTools.Blender,
            JsonSerializer.SerializeToElement(new { operation = "scaffold", path = "rover", brief = new string('x', 8001) })));
    }

    [Theory]
    [InlineData("path")]
    [InlineData("expectedSha256")]
    [InlineData("outputPath")]
    [InlineData("label")]
    public void StageRequiresAnIdentifiedSmallScriptNewSceneAndVisibleLabel(string missing)
    {
        var arguments = StageArguments();
        arguments.Remove(missing);
        Assert.Throws<ArgumentException>(() => WorkspaceTools.Validate(WorkspaceTools.Blender, JsonSerializer.SerializeToElement(arguments)));
    }

    [Theory]
    [InlineData("baseScene")]
    [InlineData("baseSceneSha256")]
    public void FollowupStageRequiresBothBaseSceneAndItsActualHash(string missing)
    {
        var arguments = StageArguments();
        arguments["baseScene"] = "rover/scene_v001.blend";
        arguments["baseSceneSha256"] = new string('a', 64);
        arguments.Remove(missing);
        Assert.Throws<ArgumentException>(() => WorkspaceTools.Validate(WorkspaceTools.Blender, JsonSerializer.SerializeToElement(arguments)));
    }

    [Theory]
    [InlineData("path", "\"rover/scene.blend\"")]
    [InlineData("outputPath", "\"rover/output.py\"")]
    [InlineData("baseScene", "\"rover/base.py\"")]
    [InlineData("baseScene", "\"ROVER\\\\SCENE_V002.BLEND\"")]
    [InlineData("expectedSha256", "\"invalid\"")]
    [InlineData("baseSceneSha256", "\"invalid\"")]
    [InlineData("label", "null")]
    [InlineData("label", "\"  \"")]
    [InlineData("timeoutSeconds", "0")]
    [InlineData("outputDirectory", "\"rover/checks\"")]
    public void StageRejectsInvalidInputsAndCannotReplaceItsBaseScene(string property, string valueJson)
    {
        var arguments = StageArguments();
        arguments["baseScene"] = "rover/scene_v001.blend";
        arguments["baseSceneSha256"] = new string('a', 64);
        using var value = JsonDocument.Parse(valueJson);
        arguments[property] = value.RootElement;
        Assert.Throws<ArgumentException>(() => WorkspaceTools.Validate(WorkspaceTools.Blender, JsonSerializer.SerializeToElement(arguments)));
    }

    [Theory]
    [InlineData("stage")]
    [InlineData("preview")]
    public void StageAndPreviewAcceptReadableLabelsUpToTwoHundredCharacters(string operation)
    {
        var arguments = operation == "stage" ? StageArguments() : new Dictionary<string, object>
        {
            ["operation"] = "preview", ["path"] = "rover/scene_v002.blend", ["expectedSha256"] = new string('a', 64),
        };
        arguments["label"] = new string('x', 200);
        WorkspaceTools.Validate(WorkspaceTools.Blender, JsonSerializer.SerializeToElement(arguments));
        arguments["label"] = new string('x', 201);
        Assert.Throws<ArgumentException>(() => WorkspaceTools.Validate(WorkspaceTools.Blender, JsonSerializer.SerializeToElement(arguments)));
    }

    [Theory]
    [InlineData("path", "null")]
    [InlineData("path", "\"rover/step.py\"")]
    [InlineData("expectedSha256", "null")]
    [InlineData("expectedSha256", "\"abc\"")]
    [InlineData("outputPath", "\"rover/new.blend\"")]
    [InlineData("baseScene", "\"rover/old.blend\"")]
    [InlineData("baseSceneSha256", "\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"")]
    public void PreviewRequiresAnIdentifiedBlendAndRejectsStageOnlyFields(string property, string valueJson)
    {
        var arguments = new Dictionary<string, object>
        {
            ["operation"] = "preview", ["path"] = "rover/scene_v002.blend", ["expectedSha256"] = new string('a', 64),
        };
        using var value = JsonDocument.Parse(valueJson);
        arguments[property] = value.RootElement;
        Assert.Throws<ArgumentException>(() => WorkspaceTools.Validate(WorkspaceTools.Blender, JsonSerializer.SerializeToElement(arguments)));
    }

    [Fact]
    public void SharedPoliciesRequireVisibleIncrementalAuthoringAndPerStageValidation()
    {
        Assert.Contains("12000 Zeichen", BlenderAuthoringGuide.WorkflowPrompt, StringComparison.Ordinal);
        Assert.Contains("01_blockout.py", BlenderAuthoringGuide.WorkflowPrompt, StringComparison.Ordinal);
        Assert.Contains("02_baugruppen.py", BlenderAuthoringGuide.WorkflowPrompt, StringComparison.Ordinal);
        Assert.Contains("baseSceneSha256", BlenderAuthoringGuide.WorkflowPrompt, StringComparison.Ordinal);
        Assert.Contains("stageHistory", BlenderAuthoringGuide.WorkflowPrompt, StringComparison.Ordinal);
        Assert.Contains("preview.state", BlenderAuthoringGuide.WorkflowPrompt, StringComparison.Ordinal);
        Assert.Contains("steps/01_blockout.py (scriptPath)", BlenderAuthoringGuide.WorkflowPrompt, StringComparison.Ordinal);
        Assert.Contains("Prüfung nach jeder Etappe", BlenderAuthoringGuide.WorkflowPrompt, StringComparison.Ordinal);
        Assert.Contains("Qualität hat Vorrang vor Zeit", BlenderAuthoringGuide.WorkflowPrompt, StringComparison.Ordinal);
        Assert.Contains("kein großes Komplettskript", BlenderAuthoringGuide.WorkflowPrompt, StringComparison.Ordinal);
        Assert.Contains("neue Nutzerhinweise", BlenderAuthoringGuide.WorkflowPrompt, StringComparison.Ordinal);
        Assert.Contains("kein Gesamtlimit", BlenderAuthoringGuide.WorkflowPrompt.Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    private static Dictionary<string, object> StageArguments() => new()
    {
        ["operation"] = "stage", ["path"] = "rover/02_fahrwerk.py", ["expectedSha256"] = new string('a', 64),
        ["outputPath"] = "rover/scene_v002.blend", ["label"] = "02 Fahrwerk",
    };

    [Fact]
    public void StageAcceptsReviewViewsAndQualityForImmediateRenders()
    {
        var arguments = StageArguments();
        arguments["views"] = new[] { "perspective", "front", "right" };
        arguments["resolution"] = 512;
        arguments["samples"] = 16;
        WorkspaceTools.Validate(WorkspaceTools.Blender, JsonSerializer.SerializeToElement(arguments));
        arguments["views"] = new[] { "front", "front" };
        Assert.Throws<ArgumentException>(() => WorkspaceTools.Validate(WorkspaceTools.Blender, JsonSerializer.SerializeToElement(arguments)));
        var preview = new Dictionary<string, object>
        {
            ["operation"] = "preview", ["path"] = "rover/scene_v002.blend", ["expectedSha256"] = new string('a', 64),
            ["views"] = new[] { "front" },
        };
        Assert.Throws<ArgumentException>(() => WorkspaceTools.Validate(WorkspaceTools.Blender, JsonSerializer.SerializeToElement(preview)));
    }

    [Fact]
    public void MediaAnalysisAcceptsDistinctReferenceUploadsOnly()
    {
        var catalog = new AgentToolCatalog();
        var media = catalog.Resolve("media.analyze", catalog.GetAvailableTools(CreateRequest(null)));
        var primary = "upload-" + new string('1', 32);
        var reference = "upload-" + new string('2', 32);
        Assert.Equal(39, primary.Length);
        catalog.Validate(media, JsonSerializer.SerializeToElement(new
        {
            uploadId = primary, prompt = "Vergleiche Render und Referenz.", referenceUploadIds = new[] { reference },
        }));
        Assert.Throws<ArgumentException>(() => catalog.Validate(media, JsonSerializer.SerializeToElement(new
        {
            uploadId = primary, referenceUploadIds = new[] { primary },
        })));
        Assert.Throws<ArgumentException>(() => catalog.Validate(media, JsonSerializer.SerializeToElement(new
        {
            uploadId = primary, referenceUploadIds = new[] { reference, reference },
        })));
        Assert.Throws<ArgumentException>(() => catalog.Validate(media, JsonSerializer.SerializeToElement(new
        {
            uploadId = primary, referenceUploadIds = Array.Empty<string>(),
        })));
        Assert.Throws<ArgumentException>(() => catalog.Validate(media, JsonSerializer.SerializeToElement(new
        {
            uploadId = primary, referenceUploadIds = Enumerable.Range(0, 7).Select(index => "upload-" + new string((char)('3' + index), 32)).ToArray(),
        })));
        var inspect = catalog.Resolve("media.inspect", catalog.GetAvailableTools(CreateRequest(null)));
        Assert.Throws<ArgumentException>(() => catalog.Validate(inspect, JsonSerializer.SerializeToElement(new
        {
            uploadId = primary, referenceUploadIds = new[] { reference },
        })));
        Assert.Contains("referenceUploadIds", media.Description, StringComparison.Ordinal);
        Assert.Contains("Änderungsanweisung", media.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void VisionPoliciesDemandDetailedGeometryAndImprovements()
    {
        Assert.Contains("Verbesserungen", GeneralAgentPolicies.VisionAnalysisSystemPrompt, StringComparison.Ordinal);
        Assert.Contains("nicht voreilig", GeneralAgentPolicies.VisionAnalysisSystemPrompt, StringComparison.Ordinal);
        Assert.Contains("Größenverhältnisse", GeneralAgentPolicies.VisionAnalysisSystemPrompt, StringComparison.Ordinal);
        var comparison = GeneralAgentPolicies.VisualComparisonInstruction(2);
        Assert.Contains("ersten 2 Bilder", comparison, StringComparison.Ordinal);
        Assert.Contains("Änderungsanweisung", comparison, StringComparison.Ordinal);
        Assert.Contains("nicht beurteilbar", comparison, StringComparison.Ordinal);
        Assert.Contains("referenceUploadIds", BlenderAuthoringGuide.WorkflowPrompt, StringComparison.Ordinal);
        Assert.Contains("g.lathe", BlenderAuthoringGuide.WorkflowPrompt, StringComparison.Ordinal);
        Assert.Contains("stage mit views", BlenderAuthoringGuide.WorkflowPrompt, StringComparison.Ordinal);
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
