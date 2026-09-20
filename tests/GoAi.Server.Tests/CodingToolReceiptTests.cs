using GoAi.Contracts;
using GoAi.Server.Core.Coding;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runs;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class CodingToolReceiptTests
{
    private static readonly string[] BlenderViews = ["perspective", "front", "right", "top", "back", "left"];
    [Fact]
    public void CompleteReadBeyondFormerReceiptLimitIsPreserved()
    {
        var raw = JsonSerializer.Serialize(new { content = new string('x', 500_000), truncated = false });
        Assert.Equal(raw, CodingLoopGuard.BoundToolResult(raw, "coding.read"));
    }

    [Theory]
    [InlineData("coding.read")]
    [InlineData("coding.readOutput")]
    public void FullReadPageSurvivesReceiptEscaping(string tool)
    {
        var raw = JsonSerializer.Serialize(new { result = new { content = new string('\t', 32000), nextLine = 301 } });
        Assert.Equal(raw, CodingLoopGuard.BoundToolResult(raw, tool));
        Assert.NotEqual(raw, CodingLoopGuard.BoundToolResult(raw, "coding.command"));
    }

    [Fact]
    public void BoundedFailureRetainsOutcomeAndArchiveReferenceWithoutWorkingState()
    {
        var evidenceId = "ev-" + new string('a', 32);
        var raw = JsonSerializer.Serialize(new
        {
            status = "failed", errorCode = "client.coding_tool_failed",
            result = new { success = false, exitCode = 7, stdout = new string('x', 11950), stderr = "actual failure",
                evidence = new { evidenceId, tool = "coding.command", storedBytes = 12050, truncated = false } },
        });
        Assert.True(raw.Length > CodingLoopGuard.MaximumToolResultCharacters);
        var bounded = CodingLoopGuard.BoundToolResult(raw);
        var receipt = JsonSerializer.Deserialize<JsonElement>(bounded);
        Assert.True(bounded.Length <= CodingLoopGuard.MaximumToolResultCharacters);
        Assert.Equal("failed", receipt.GetProperty("status").GetString());
        Assert.False(receipt.GetProperty("result").GetProperty("success").GetBoolean());
        Assert.Equal(7, receipt.GetProperty("result").GetProperty("exitCode").GetInt32());
        Assert.Equal(evidenceId, receipt.GetProperty("result").GetProperty("evidence").GetProperty("evidenceId").GetString());
        var first = new LmToolCall("first", "coding.command", JsonSerializer.SerializeToElement(new { executable = "python", arguments = "diagnose.py" }));
        var second = first with { Id = "second" };
        LmChatMessage[] messages = [new("assistant", ToolCalls: [first]), new("tool", bounded, ToolCallId: first.Id),
            new("assistant", ToolCalls: [second]), new("tool", bounded, ToolCallId: second.Id)];
        Assert.Throws<AgentRunLimitException>(() => CodingLoopGuard.ThrowIfRepeatedFailure(messages, first with { Id = "third" }));
        Assert.Equal(11950, JsonSerializer.Deserialize<JsonElement>(raw).GetProperty("result").GetProperty("stdout").GetString()!.Length);
    }

    [Fact]
    public void UnicodeOutputPageKeepsRealPagingAndDoesNotInventExecutionSuccess()
    {
        var evidenceId = "ev-" + new string('b', 32);
        var raw = JsonSerializer.Serialize(new { status = "completed", result = new
        {
            evidenceId, stream = "stdout", offset = 4000, nextOffset = 12000, hasMore = true,
            storedCharacters = 90000, truncated = false, text = new string('界', 8000),
        } });
        var bounded = CodingLoopGuard.BoundToolResult(raw);
        var result = JsonSerializer.Deserialize<JsonElement>(bounded).GetProperty("result");
        Assert.True(bounded.Length <= CodingLoopGuard.MaximumToolResultCharacters);
        Assert.Equal(evidenceId, result.GetProperty("evidenceId").GetString());
        Assert.Equal(4000, result.GetProperty("offset").GetInt32());
        Assert.Equal(12000, result.GetProperty("nextOffset").GetInt32());
        Assert.True(result.GetProperty("hasMore").GetBoolean());
        Assert.False(result.GetProperty("truncated").GetBoolean());
        Assert.False(result.TryGetProperty("success", out _));
        Assert.False(result.TryGetProperty("exitCode", out _));
    }

    [Fact]
    public void AppliedFileReceiptKeepsExactHashAndUnknownOutcomeAcrossUnicodeBudget()
    {
        var hash = new string('c', 64);
        var raw = JsonSerializer.Serialize(new { status = "failed", outcomeUnknown = true,
            result = new { applied = true, path = "source.cs", sha256 = hash, diff = new string('\u0001', 10000) } });
        var bounded = CodingLoopGuard.BoundToolResult(raw);
        var receipt = JsonSerializer.Deserialize<JsonElement>(bounded);
        Assert.True(bounded.Length <= CodingLoopGuard.MaximumToolResultCharacters);
        Assert.True(receipt.GetProperty("outcomeUnknown").GetBoolean());
        Assert.True(receipt.GetProperty("result").GetProperty("applied").GetBoolean());
        Assert.Equal(hash, receipt.GetProperty("result").GetProperty("sha256").GetString());
        Assert.Equal("source.cs", receipt.GetProperty("result").GetProperty("path").GetString());
    }

    [Fact]
    public void OverlargeMetadataIsMarkedIncompleteAndSmallReceiptRemainsExact()
    {
        const string small = "{\"status\":\"failed\",\"result\":{\"exitCode\":7}}";
        Assert.Equal(small, CodingLoopGuard.BoundToolResult(small));
        var bounded = CodingLoopGuard.BoundToolResult(JsonSerializer.Serialize(new { status = "failed",
            result = new { exitCode = 7, path = new string('界', 20000), stdout = new string('x', 14000) } }));
        var result = JsonSerializer.Deserialize<JsonElement>(bounded);
        Assert.True(bounded.Length <= CodingLoopGuard.MaximumToolResultCharacters);
        Assert.True(result.GetProperty("metadataTruncated").GetBoolean());
        Assert.Equal("failed", result.GetProperty("status").GetString());
        Assert.Equal(7, result.GetProperty("result").GetProperty("exitCode").GetInt32());
        Assert.False(result.GetProperty("result").TryGetProperty("path", out _));
    }

    [Fact]
    public void OversizedBlenderStageReceiptKeepsExactSceneAndPreviewStateWithoutGeometryOrProcessLogs()
    {
        var sceneHash = new string('a', 64);
        var baseHash = new string('b', 64);
        var scriptHash = new string('c', 64);
        var raw = JsonSerializer.Serialize(new { status = "completed", result = new
        {
            success = true, operation = "stage", stageId = "verified-stage", label = "02 Fahrwerk",
            outputPath = "rover/scenes/02_fahrwerk.blend", sceneSha256 = sceneHash,
            baseScene = "rover/scenes/01_blockout.blend", baseSceneSha256 = baseHash,
            scriptPath = "rover/steps/02_fahrwerk.py", scriptSha256 = scriptHash,
            reportPath = "rover/checks/02_fahrwerk/report.json", valid = false, inspectionCompleted = true, issueCount = 32,
            preview = new { success = false, operation = "preview", state = "blocked", revision = 2,
                path = "rover/scenes/02_fahrwerk.blend", message = "Ungespeicherte Benutzeränderungen bleiben erhalten." },
            objects = Enumerable.Range(0, 64).Select(index => new { name = "Part" + index, detail = new string('x', 512) }).ToArray(),
            execution = new { success = true, exitCode = 0, timedOut = false, stdout = new string('x', 18_000) },
        } });

        var bounded = CodingLoopGuard.BoundToolResult(raw, WorkspaceTools.Blender);
        var receipt = JsonSerializer.Deserialize<JsonElement>(bounded);
        var result = receipt.GetProperty("result");
        Assert.True(bounded.Length <= CodingLoopGuard.MaximumToolResultCharacters);
        Assert.True(receipt.GetProperty("truncated").GetBoolean());
        Assert.Equal(sceneHash, result.GetProperty("sceneSha256").GetString());
        Assert.Equal(baseHash, result.GetProperty("baseSceneSha256").GetString());
        Assert.Equal(scriptHash, result.GetProperty("scriptSha256").GetString());
        Assert.Equal("rover/scenes/02_fahrwerk.blend", result.GetProperty("outputPath").GetString());
        Assert.Equal("rover/checks/02_fahrwerk/report.json", result.GetProperty("reportPath").GetString());
        Assert.False(result.GetProperty("valid").GetBoolean());
        Assert.True(result.GetProperty("inspectionCompleted").GetBoolean());
        Assert.Equal("blocked", result.GetProperty("preview").GetProperty("state").GetString());
        Assert.Contains("Benutzeränderungen", result.GetProperty("preview").GetProperty("message").GetString());
        Assert.Equal(JsonValueKind.String, receipt.GetProperty("preview").ValueKind);
        Assert.Equal(0, result.GetProperty("execution").GetProperty("exitCode").GetInt32());
        Assert.False(result.TryGetProperty("objects", out _));
        Assert.False(result.GetProperty("execution").TryGetProperty("stdout", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OversizedBlenderRenderRetainsAllSixImagesAndNeverOverwritesActualPreview(bool wrapped)
    {
        var hash = new string('d', 64);
        var report = new
        {
            success = true, operation = "render", sourceSha256 = hash, reportPath = "rover/renders/v003/report.json",
            valid = true, truncatedObjects = true,
            preview = new { state = "ready", revision = 3, path = "rover/scene_v003.blend" },
            images = BlenderViews.Select(view => new { view, path = "rover/renders/v003/" + view + ".png", sha256 = hash, bytes = 54321 }).ToArray(),
            objects = Enumerable.Range(0, 64).Select(index => new { name = "Part" + index, detail = new string('界', 512) }).ToArray(),
        };
        var raw = wrapped ? JsonSerializer.Serialize(new { status = "completed", result = report }) : JsonSerializer.Serialize(report);
        var bounded = CodingLoopGuard.BoundToolResult(raw, WorkspaceTools.Blender);
        var receipt = JsonSerializer.Deserialize<JsonElement>(bounded);
        var result = wrapped ? receipt.GetProperty("result") : receipt;

        Assert.True(bounded.Length <= CodingLoopGuard.MaximumToolResultCharacters);
        Assert.Equal(hash, result.GetProperty("sourceSha256").GetString());
        Assert.Equal("rover/renders/v003/report.json", result.GetProperty("reportPath").GetString());
        Assert.Equal("ready", result.GetProperty("preview").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.String, receipt.GetProperty(wrapped ? "preview" : "textPreview").ValueKind);
        Assert.False(result.TryGetProperty("objects", out _));
        Assert.False(result.TryGetProperty("imagesTruncated", out _));
        Assert.Equal(BlenderViews, result.GetProperty("images").EnumerateArray().Select(image => image.GetProperty("view").GetString()).ToArray());
        Assert.All(result.GetProperty("images").EnumerateArray(), image =>
        {
            Assert.Equal("rover/renders/v003/" + image.GetProperty("view").GetString() + ".png", image.GetProperty("path").GetString());
            Assert.Equal(hash, image.GetProperty("sha256").GetString());
            Assert.Equal(54321, image.GetProperty("bytes").GetInt32());
        });
    }

    [Fact]
    public void BlenderInfoRetainsFileHashAndAvailabilityWhenTheAuthoringGuideIsLong()
    {
        var hash = new string('e', 64);
        var raw = JsonSerializer.Serialize(new { status = "completed", result = new
        {
            available = true, toolkitVersion = 2,
            file = new { path = "rover/scene_v003.blend", bytes = 234567, sha256 = hash },
            preview = new { success = true, state = "ready", revision = 3 },
            instruction = BlenderAuthoringGuide.WorkflowPrompt + new string('x', 12_000),
        } });
        var bounded = CodingLoopGuard.BoundToolResult(raw, WorkspaceTools.Blender);
        var result = JsonSerializer.Deserialize<JsonElement>(bounded).GetProperty("result");

        Assert.True(bounded.Length <= CodingLoopGuard.MaximumToolResultCharacters);
        Assert.True(result.GetProperty("available").GetBoolean());
        Assert.Equal(2, result.GetProperty("toolkitVersion").GetInt32());
        Assert.Equal(hash, result.GetProperty("file").GetProperty("sha256").GetString());
        Assert.Equal("rover/scene_v003.blend", result.GetProperty("file").GetProperty("path").GetString());
        Assert.Equal(234567, result.GetProperty("file").GetProperty("bytes").GetInt32());
    }

    [Fact]
    public void OversizedBlenderMetadataCannotExceedTheHardBoundOrPublishBrokenImagePaths()
    {
        var raw = JsonSerializer.Serialize(new
        {
            operation = "render", reportPath = "rover/checks/report.json", sourceSha256 = new string('f', 64),
            preview = new { state = "error", message = new string('界', 5000) },
            images = Enumerable.Range(0, 20).Select(index => new
            {
                view = "view" + index, path = new string('界', 1024) + ".png", sha256 = new string('a', 64), bytes = 12345,
            }).ToArray(),
            stdout = new string('\u0001', 14_000),
        });
        var bounded = CodingLoopGuard.BoundToolResult(raw, WorkspaceTools.Blender);
        var result = JsonSerializer.Deserialize<JsonElement>(bounded);

        Assert.True(bounded.Length <= CodingLoopGuard.MaximumToolResultCharacters);
        Assert.True(result.GetProperty("metadataTruncated").GetBoolean());
        Assert.True(result.GetProperty("imagesTruncated").GetBoolean());
        Assert.Empty(result.GetProperty("images").EnumerateArray());
        Assert.Equal("rover/checks/report.json", result.GetProperty("reportPath").GetString());
        Assert.Equal("error", result.GetProperty("preview").GetProperty("state").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BlenderRenderKeepsExactMeasurementsAndPrioritizesErrorsAfterManyWarnings(bool wrapped)
    {
        var source = BlenderReceiptFixture.Render();
        var raw = wrapped ? JsonSerializer.Serialize(new { status = "completed", result = source }) : source.GetRawText();
        Assert.True(raw.Length > CodingLoopGuard.MaximumToolResultCharacters);

        var bounded = CodingLoopGuard.BoundToolResult(raw, WorkspaceTools.Blender);
        var receipt = JsonSerializer.Deserialize<JsonElement>(bounded);
        var result = wrapped ? receipt.GetProperty("result") : receipt;

        Assert.True(bounded.Length <= CodingLoopGuard.MaximumToolResultCharacters);
        Assert.True(JsonElement.DeepEquals(source.GetProperty("counts"), result.GetProperty("counts")));
        Assert.True(JsonElement.DeepEquals(source.GetProperty("bounds"), result.GetProperty("bounds")));
        Assert.True(JsonElement.DeepEquals(source.GetProperty("units"), result.GetProperty("units")));
        Assert.False(result.GetProperty("valid").GetBoolean());
        Assert.Equal(16, result.GetProperty("issueCount").GetInt32());
        Assert.True(result.GetProperty("issuesTruncated").GetBoolean());
        var issues = result.GetProperty("issues");
        Assert.Equal(8, issues.GetArrayLength());
        Assert.Equal("zero_scale", issues[0].GetProperty("code").GetString());
        Assert.Equal("Habitat_Base", issues[0].GetProperty("object").GetString());
        Assert.Equal("A scale axis collapses the geometry", issues[0].GetProperty("message").GetString());
        Assert.Equal("nonfinite_vertices", issues[1].GetProperty("code").GetString());
        Assert.Equal("error", issues[1].GetProperty("severity").GetString());
        Assert.Equal("renders_blockout_v01/report.json", result.GetProperty("reportPath").GetString());
        Assert.Equal(BlenderReceiptFixture.SourceHash, result.GetProperty("sourceSha256").GetString());
        Assert.Equal("station_blockout_v01.blend", result.GetProperty("preview").GetProperty("path").GetString());
        Assert.True(JsonElement.DeepEquals(source.GetProperty("images"), result.GetProperty("images")));
        Assert.False(result.TryGetProperty("objects", out _));
    }

    [Fact]
    public void BlenderEmptyIssuesDoNotInventFailuresAndOversizedSectionsRemainExplicitlyIncomplete()
    {
        var raw = JsonSerializer.Serialize(new
        {
            valid = true, issueCount = 0, issues = Array.Empty<object>(), reportPath = "checks/report.json",
            counts = new { objects = 27, unexpected = new string('界', 4000) },
            bounds = new { unexpected = new string('\u0001', 4000) }, units = new { system = "METRIC", scaleLength = 1 },
            execution = new { success = true, exitCode = 0, stdout = new string('x', 14000) },
        });
        var bounded = CodingLoopGuard.BoundToolResult(raw, WorkspaceTools.Blender);
        var result = JsonSerializer.Deserialize<JsonElement>(bounded);

        Assert.True(bounded.Length <= CodingLoopGuard.MaximumToolResultCharacters);
        Assert.True(result.GetProperty("metadataTruncated").GetBoolean());
        Assert.True(result.GetProperty("valid").GetBoolean());
        Assert.Empty(result.GetProperty("issues").EnumerateArray());
        Assert.False(result.TryGetProperty("issuesTruncated", out _));
        Assert.False(result.TryGetProperty("counts", out _));
        Assert.False(result.TryGetProperty("bounds", out _));
        Assert.Equal("METRIC", result.GetProperty("units").GetProperty("system").GetString());
        Assert.Equal("checks/report.json", result.GetProperty("reportPath").GetString());
    }
}

/// <summary>The shape and size of a real 27-object render receipt, with late structural failures.</summary>
internal static class BlenderReceiptFixture
{
    internal static readonly string SourceHash = new('a', 64);
    private static readonly string[] Views = ["perspective", "front", "right", "top", "back", "left"];
    private static readonly string[] Collections = ["Habitat"];
    private static readonly string[] Materials = ["Metal_Grey"];
    private static readonly double[] UnitScale = [1.0, 1.0, 1.0];

    internal static JsonElement Render()
    {
        var bounds = JsonSerializer.Deserialize<JsonElement>("""{"min":[-11.600000381469727,-5.480000019073486,0],"max":[10.5,2.5,5.300000190734863],"dimensions":[22.100000381469727,7.980000019073486,5.300000190734863]}""");
        var mesh = JsonSerializer.Deserialize<JsonElement>("""{"vertices":96,"edges":192,"faces":98,"triangles":188,"finiteCoordinates":true,"degenerateFaces":0,"looseVertices":0,"looseEdges":0,"boundaryEdges":0,"nonManifoldEdges":0}""");
        return JsonSerializer.SerializeToElement(new
        {
            success = true, operation = "render", sourceSha256 = SourceHash, reportPath = "renders_blockout_v01/report.json",
            valid = false, counts = new { objects = 27, sceneObjects = 27, meshObjects = 23, vertices = 1506, edges = 2856,
                faces = 1396, triangles = 2920, materials = 8, hiddenMeshes = 0, curves = 0, estimatedEvaluatedVertices = 8608,
                evaluatedVertices = 3634, evaluatedEdges = 7176, evaluatedFaces = 3586, evaluatedMeshInstances = 22, evaluatedObjectInstances = 27 },
            bounds, units = new { system = "METRIC", scaleLength = 1.0, lengthUnit = "METERS" },
            issues = Enumerable.Range(0, 14).Select(index => new
                { severity = "warning", code = "nonuniform_scale", @object = "Panel" + index, message = "Non-uniform object scale; check modifiers/export dimensions" })
                .Concat([
                    new { severity = "error", code = "zero_scale", @object = "Habitat_Base", message = "A scale axis collapses the geometry" },
                    new { severity = "error", code = "nonfinite_vertices", @object = "Solar_Array", message = "Mesh contains non-finite coordinates" },
                ]).ToArray(),
            issueCount = 16,
            objects = Enumerable.Range(0, 27).Select(index => new
            {
                name = "Habitat_Part" + index, type = "MESH", collections = Collections, materials = Materials,
                hiddenRender = false, hiddenViewport = false, instancedPrototype = false, environment = false,
                scale = UnitScale, component = "Habitat", bounds, mesh, diagnosticScope = "source_mesh",
                estimatedEvaluatedVertices = 152, visibleInstanceCount = 1, evaluatedMesh = mesh,
            }).ToArray(),
            truncatedObjects = false,
            preview = new { success = true, state = "ready", revision = 1, path = "station_blockout_v01.blend", sha256 = SourceHash },
            images = Views.Select(view => new { view, path = "renders_blockout_v01/" + view + ".png", sha256 = SourceHash, bytes = 569954 }).ToArray(),
            execution = new { success = true, exitCode = 0, timedOut = false, stdout = "Blender rendered six views. " + new string('x', 2000) },
        });
    }
}
