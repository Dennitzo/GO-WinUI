using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using GoWinUI.App.Services;
using GoWinUI.Core.Coding;
using Xunit.Abstractions;

namespace GoWinUI.Tests;

/// <summary>Opt-in packaged stage/preview pipeline with real Blender, without an AI model.</summary>
public sealed class BlenderStagingLiveTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] Views = ["perspective"];
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    [Fact]
    [Trait("Category", "Live")]
    public async Task RealStagesReuseVisibleBlenderAndPreservePriorRevisionOnFailure()
    {
        if (Environment.GetEnvironmentVariable("GO_BLENDER_RUNTIME_LIVE") != "1") return;
        var blender = BlenderToolService.FindBlender();
        Assert.False(string.IsNullOrEmpty(blender), "Install Blender or set GO_BLENDER_EXECUTABLE.");
        var evidenceRoot = Environment.GetEnvironmentVariable("GO_BLENDER_RUNTIME_EVIDENCE")
            ?? Environment.GetEnvironmentVariable("GO_BLENDER_LIVE_EVIDENCE")
            ?? Path.Combine(Path.GetTempPath(), "go-blender-runtime-live");
        var workspace = Path.GetFullPath(Path.Combine(evidenceRoot, "staging-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(workspace);
        output.WriteLine("Blender staging evidence: " + workspace);
        var service = new BlenderToolService();
        var results = new List<object>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var token = deadline.Token;
        var passed = false;
        string? error = null;
        try
        {
            var scaffold = await ExecuteAsync(new { operation = "scaffold", path = "project/", brief = "Two visible, preserved construction stages." });
            Assert.True(scaffold.GetProperty("success").GetBoolean());
            Assert.True(File.Exists(Path.Combine(workspace, "project", "steps", "01_blockout.py")));
            var initialPreview = scaffold.GetProperty("preview");
            Assert.Equal("ready", initialPreview.GetProperty("state").GetString());
            await AssertVisibleAsync(initialPreview);

            const string firstScript = """
                import go_blender as g
                shell = g.material('StageShell', (0.1, 0.3, 0.7, 1))
                g.box('HabitatShell', size=(3, 2, 2), location=(0, 0, 1), material=shell)
                """;
            var first = await StageAsync("project/steps/10_habitat.py", firstScript, "stages/01-habitat.blend", "Habitat shell");
            AssertSavedStage(first, expectedMeshes: 1);
            var firstPreview = first.GetProperty("preview");
            Assert.Equal(initialPreview.GetProperty("processId").GetInt32(), firstPreview.GetProperty("processId").GetInt32());
            Assert.True(firstPreview.GetProperty("revision").GetInt32() > initialPreview.GetProperty("revision").GetInt32());
            await AssertVisibleAsync(firstPreview);
            var firstPath = WorkspaceFilePath.Resolve(workspace, first.GetProperty("outputPath").GetString()!);
            var firstHash = await HashAsync(firstPath, token);
            Assert.Equal(firstHash, first.GetProperty("sceneSha256").GetString());

            const string secondScript = """
                import go_blender as g
                frame = g.material('StageFrame', (0.8, 0.35, 0.05, 1))
                g.box('AirlockShell', size=(1.2, 1.5, 1.6), location=(2.1, 0, 0.8), material=frame)
                """;
            var second = await StageAsync("project/steps/20_airlock.py", secondScript, "stages/02-airlock.blend", "Connected airlock",
                first.GetProperty("outputPath").GetString(), firstHash);
            AssertSavedStage(second, expectedMeshes: 2);
            var secondPreview = second.GetProperty("preview");
            Assert.Equal(firstPreview.GetProperty("processId").GetInt32(), secondPreview.GetProperty("processId").GetInt32());
            Assert.True(secondPreview.GetProperty("revision").GetInt32() > firstPreview.GetProperty("revision").GetInt32());
            await AssertVisibleAsync(secondPreview);
            Assert.Equal(firstHash, await HashAsync(firstPath, token));
            var secondPath = WorkspaceFilePath.Resolve(workspace, second.GetProperty("outputPath").GetString()!);
            var secondHash = await HashAsync(secondPath, token);
            Assert.Equal(secondHash, second.GetProperty("sceneSha256").GetString());

            // A separate Blender invocation loads the promoted file and verifies that
            // stage two really retained the first stage's geometry on disk.
            var inspect = await ExecuteAsync(new
            {
                operation = "inspect", path = "stages/02-airlock.blend", expectedSha256 = secondHash,
                outputDirectory = "inspection/", timeoutSeconds = 120,
            });
            Assert.True(inspect.GetProperty("success").GetBoolean(), inspect.GetRawText());
            Assert.True(inspect.GetProperty("valid").GetBoolean());
            Assert.Equal(2, inspect.GetProperty("counts").GetProperty("meshObjects").GetInt32());
            var objects = inspect.GetProperty("objects").EnumerateArray().ToArray();
            Assert.Contains(objects, item => item.GetProperty("name").GetString() == "HabitatShell");
            Assert.Contains(objects, item => item.GetProperty("name").GetString() == "AirlockShell");
            var render = await ExecuteAsync(new
            {
                operation = "render", path = "stages/02-airlock.blend", expectedSha256 = secondHash,
                outputDirectory = "review/", views = Views, resolution = 128, samples = 1, timeoutSeconds = 180,
            });
            Assert.True(render.GetProperty("success").GetBoolean(), render.GetRawText());
            var image = Assert.Single(render.GetProperty("images").EnumerateArray());
            var imagePath = WorkspaceFilePath.Resolve(workspace, image.GetProperty("path").GetString()!);
            var imageBytes = await File.ReadAllBytesAsync(imagePath, token);
            Assert.True(imageBytes.Length > 100);
            Assert.True(imageBytes.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature));
            Assert.Equal(128, image.GetProperty("width").GetInt32());
            Assert.Equal(128, image.GetProperty("height").GetInt32());
            Assert.Equal(await HashAsync(imagePath, token), image.GetProperty("sha256").GetString());

            const string failingScript = """
                import go_blender as g
                g.box('MustNotBePromoted', location=(5, 0, 0))
                raise ValueError('GO_STAGE_EXPECTED_FAILURE')
                """;
            var failed = await StageAsync("project/steps/30_failure.py", failingScript, "stages/03-must-not-exist.blend", "Expected failed stage",
                second.GetProperty("outputPath").GetString(), secondHash);
            Assert.False(failed.GetProperty("success").GetBoolean());
            Assert.False(File.Exists(Path.Combine(workspace, "stages", "03-must-not-exist.blend")));
            Assert.False(failed.GetProperty("execution").GetProperty("success").GetBoolean());
            var failedReportPath = WorkspaceFilePath.Resolve(workspace, failed.GetProperty("reportPath").GetString()!);
            using (var failedReport = JsonDocument.Parse(await File.ReadAllTextAsync(failedReportPath, token)))
            {
                Assert.False(failedReport.RootElement.GetProperty("success").GetBoolean());
                Assert.Contains(failedReport.RootElement.GetProperty("issues").EnumerateArray(),
                    item => item.GetProperty("code").GetString() == "stage_failed");
                Assert.Contains("GO_STAGE_EXPECTED_FAILURE", failedReport.RootElement.GetRawText());
            }
            Assert.Equal(firstHash, await HashAsync(firstPath, token));
            Assert.Equal(secondHash, await HashAsync(secondPath, token));
            var stillVisible = await ExecuteAsync(new
            {
                operation = "preview", path = "stages/02-airlock.blend", expectedSha256 = secondHash,
            });
            Assert.Equal(secondPreview.GetProperty("processId").GetInt32(), stillVisible.GetProperty("processId").GetInt32());
            Assert.Equal(secondPreview.GetProperty("revision").GetInt32(), stillVisible.GetProperty("revision").GetInt32());
            await AssertVisibleAsync(stillVisible);

            // Windows may temporarily prevent replacement of the watcher state.
            // A failed publication must remain retryable for the same scene/hash.
            var retryPath = Path.Combine(workspace, "stages", "02-airlock-preview-retry.blend");
            File.Copy(secondPath, retryPath);
            var previewState = Assert.Single(Directory.EnumerateFiles(
                Path.Combine(workspace, ".go-blender-preview"), "state.json", SearchOption.AllDirectories));
            var retryArguments = new
            {
                operation = "preview", path = "stages/02-airlock-preview-retry.blend", expectedSha256 = secondHash,
            };
            using (var lockedState = new FileStream(previewState, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var publicationError = await Record.ExceptionAsync(() => ExecuteAsync(retryArguments));
                Assert.True(publicationError is IOException or UnauthorizedAccessException, publicationError?.ToString());
            }
            var recoveredPreview = await ExecuteAsync(retryArguments);
            Assert.Equal(secondPreview.GetProperty("revision").GetInt32() + 1, recoveredPreview.GetProperty("revision").GetInt32());
            Assert.Equal(secondPreview.GetProperty("processId").GetInt32(), recoveredPreview.GetProperty("processId").GetInt32());
            Assert.Equal(retryArguments.path, recoveredPreview.GetProperty("path").GetString());
            await AssertVisibleAsync(recoveredPreview);
            passed = true;
        }
        catch (Exception exception)
        {
            error = exception.ToString();
            throw;
        }
        finally
        {
            // Preserve the workspace and its dedicated visible preview for review.
            await File.WriteAllTextAsync(Path.Combine(workspace, "staging-acceptance.json"),
                JsonSerializer.Serialize(new { passed, error, blender, workspace, results }, Json));
        }

        async Task<JsonElement> StageAsync(string path, string source, string outputPath, string label,
            string? baseScene = null, string? baseSceneSha256 = null)
        {
            var script = WorkspaceFilePath.Resolve(workspace, path);
            await File.WriteAllTextAsync(script, source, token);
            var arguments = new Dictionary<string, object>
            {
                ["operation"] = "stage", ["path"] = path, ["expectedSha256"] = await HashAsync(script, token),
                ["outputPath"] = outputPath, ["label"] = label, ["timeoutSeconds"] = 120,
            };
            if (baseScene is not null)
            {
                arguments["baseScene"] = baseScene;
                arguments["baseSceneSha256"] = baseSceneSha256!;
            }
            return await ExecuteAsync(arguments);
        }

        async Task<JsonElement> ExecuteAsync(object arguments)
        {
            var result = JsonSerializer.SerializeToElement(await service.ExecuteAsync(JsonSerializer.SerializeToElement(arguments), workspace, null, token), Json);
            results.Add(new { arguments, result });
            await File.WriteAllTextAsync(Path.Combine(workspace, "staging-steps.json"), JsonSerializer.Serialize(results, Json), token);
            return result;
        }

        async Task AssertVisibleAsync(JsonElement preview)
        {
            Assert.True(preview.GetProperty("success").GetBoolean(), preview.GetRawText());
            Assert.Equal("ready", preview.GetProperty("state").GetString());
            var processId = preview.GetProperty("processId").GetInt32();
            using var process = Process.GetProcessById(processId);
            var timeout = Stopwatch.StartNew();
            do
            {
                process.Refresh();
                if (process.HasExited || process.MainWindowHandle != IntPtr.Zero) break;
                await Task.Delay(200, token);
            }
            while (timeout.Elapsed < TimeSpan.FromSeconds(15));
            Assert.False(process.HasExited, "The dedicated Blender preview exited before it became visible.");
            Assert.NotEqual(IntPtr.Zero, process.MainWindowHandle);
            results.Add(new
            {
                operation = "native-window-check", processId, revision = preview.GetProperty("revision").GetInt32(),
                mainWindowHandle = process.MainWindowHandle.ToInt64().ToString("X", CultureInfo.InvariantCulture),
                processName = process.ProcessName, visibleWindow = true,
            });
        }
    }

    private static void AssertSavedStage(JsonElement stage, int expectedMeshes)
    {
        Assert.True(stage.GetProperty("success").GetBoolean(), stage.GetRawText());
        Assert.True(stage.GetProperty("inspectionCompleted").GetBoolean());
        Assert.True(stage.GetProperty("valid").GetBoolean());
        Assert.Equal(expectedMeshes, stage.GetProperty("counts").GetProperty("meshObjects").GetInt32());
        Assert.Equal(stage.GetProperty("outputPath").GetString(), stage.GetProperty("preview").GetProperty("path").GetString());
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
    }
}
