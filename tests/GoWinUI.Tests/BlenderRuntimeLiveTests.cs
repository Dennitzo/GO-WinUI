using System.Security.Cryptography;
using System.Text.Json;
using GoWinUI.App.Services;
using Xunit.Abstractions;

namespace GoWinUI.Tests;

/// <summary>Opt-in real Blender process/resource integration, without loading an AI model.</summary>
public sealed class BlenderRuntimeLiveTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] Views = ["perspective", "front", "top"];
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    [Fact]
    [Trait("Category", "Live")]
    public async Task RealBlenderRunsPackagedResourcesAndPreservesFailureEvidence()
    {
        if (Environment.GetEnvironmentVariable("GO_BLENDER_RUNTIME_LIVE") != "1") return;
        Assert.False(string.IsNullOrEmpty(BlenderToolService.FindBlender()), "Install Blender or set GO_BLENDER_EXECUTABLE.");
        var workspace = Path.GetFullPath(Path.Combine(Environment.GetEnvironmentVariable("GO_BLENDER_LIVE_EVIDENCE")
            ?? Path.Combine(Path.GetTempPath(), "go-blender-runtime-live"), "runtime-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(workspace);
        output.WriteLine("Blender runtime evidence: " + workspace);
        var results = new List<object>();
        var service = new BlenderToolService();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var token = deadline.Token;
        var passed = false;
        string? error = null;
        try
        {
            var scaffold = await ExecuteAsync(new { operation = "scaffold", path = "project/", brief = "Integration test: blue rectangular block." });
            Assert.True(scaffold.GetProperty("success").GetBoolean());
            Assert.True(File.Exists(Path.Combine(workspace, "project", "go_blender.py")));
            var scriptPath = Path.Combine(workspace, "project", "scene.py");
            await File.WriteAllTextAsync(scriptPath, """
                from pathlib import Path
                import sys
                import bpy
                sys.path.insert(0, str(Path(__file__).resolve().parent))
                import go_blender as g
                g.clear_scene()
                bpy.context.scene.unit_settings.system = 'METRIC'
                finish = g.material('Blue', (0.08, 0.25, 0.8, 1))
                g.box('RuntimeBlock', size=(3, 2, 1), location=(0, 0, 0.5), material=finish)
                g.ground(size=7)
                g.setup_review_scene(resolution=128, samples=1)
                g.save_revision('scene.blend')
                """, token);
            var run = await ExecuteAsync(new { operation = "run", path = "project/scene.py", expectedSha256 = await HashAsync(scriptPath), timeoutSeconds = 120 });
            Assert.True(run.GetProperty("success").GetBoolean(), run.GetRawText());
            Assert.Equal(0, run.GetProperty("exitCode").GetInt32());
            var scene = Path.Combine(workspace, "scene.blend");
            Assert.True(new FileInfo(scene).Length > 1_000);
            var hash = await HashAsync(scene);
            var info = await ExecuteAsync(new { operation = "info", path = "scene.blend" });
            Assert.True(info.GetProperty("available").GetBoolean());
            Assert.Equal(hash, info.GetProperty("file").GetProperty("sha256").GetString());
            var inspect = await ExecuteAsync(new { operation = "inspect", path = "scene.blend", expectedSha256 = hash, outputDirectory = "inspection/", timeoutSeconds = 120 });
            Assert.True(inspect.GetProperty("success").GetBoolean(), inspect.GetRawText());
            Assert.True(inspect.GetProperty("valid").GetBoolean());
            Assert.Contains(inspect.GetProperty("objects").EnumerateArray(), item => item.GetProperty("name").GetString() == "RuntimeBlock");
            Assert.True(File.Exists(Path.Combine(workspace, inspect.GetProperty("reportPath").GetString()!)));
            var render = await ExecuteAsync(new { operation = "render", path = "scene.blend", expectedSha256 = hash,
                outputDirectory = "review/", views = Views, resolution = 128, samples = 1, timeoutSeconds = 180 });
            Assert.True(render.GetProperty("success").GetBoolean(), render.GetRawText());
            var images = render.GetProperty("images").EnumerateArray().ToArray();
            Assert.Equal(Views.Length, images.Length);
            foreach (var image in images)
            {
                var path = Path.Combine(workspace, image.GetProperty("path").GetString()!);
                var bytes = await File.ReadAllBytesAsync(path, token);
                Assert.True(bytes.Length > 100);
                Assert.True(bytes.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature));
                Assert.Equal(128, image.GetProperty("width").GetInt32());
                Assert.Equal(128, image.GetProperty("height").GetInt32());
                Assert.Equal(await HashAsync(path), image.GetProperty("sha256").GetString());
            }
            Assert.Equal(hash, await HashAsync(scene));

            // A structurally invalid scene still has an inspectable report when
            // the Blender child exits unsuccessfully and produces no render images.
            var invalidScript = Path.Combine(workspace, "invalid.py");
            await File.WriteAllTextAsync(invalidScript, """
                import bpy
                bpy.ops.wm.open_mainfile(filepath='scene.blend')
                bpy.data.objects['RuntimeBlock'].scale.z = 0
                bpy.ops.wm.save_as_mainfile(filepath='invalid.blend')
                """, token);
            var invalidRun = await ExecuteAsync(new { operation = "run", path = "invalid.py", expectedSha256 = await HashAsync(invalidScript), timeoutSeconds = 120 });
            Assert.True(invalidRun.GetProperty("success").GetBoolean(), invalidRun.GetRawText());
            var invalidHash = await HashAsync(Path.Combine(workspace, "invalid.blend"));
            var rejected = await ExecuteAsync(new { operation = "render", path = "invalid.blend", expectedSha256 = invalidHash,
                outputDirectory = "invalid-review\\", views = Views, resolution = 128, samples = 1, timeoutSeconds = 120 });
            Assert.False(rejected.GetProperty("success").GetBoolean());
            Assert.False(rejected.GetProperty("valid").GetBoolean());
            Assert.Empty(rejected.GetProperty("images").EnumerateArray());
            Assert.Contains(rejected.GetProperty("issues").EnumerateArray(), issue => issue.GetProperty("code").GetString() == "zero_scale");
            Assert.Contains(rejected.GetProperty("issues").EnumerateArray(), issue => issue.GetProperty("code").GetString() == "render_preflight_failed");
            Assert.True(File.Exists(Path.Combine(workspace, rejected.GetProperty("reportPath").GetString()!)));
            Assert.Equal(invalidHash, await HashAsync(Path.Combine(workspace, "invalid.blend")));
            Assert.Equal(hash, await HashAsync(scene));

            var failurePath = Path.Combine(workspace, "failure.py");
            await File.WriteAllTextAsync(failurePath, "raise ValueError('GO_BLENDER_EXPECTED_FAILURE')\n", token);
            var failure = await ExecuteAsync(new { operation = "run", path = "failure.py", expectedSha256 = await HashAsync(failurePath), timeoutSeconds = 120 });
            Assert.False(failure.GetProperty("success").GetBoolean());
            Assert.NotEqual(0, failure.GetProperty("exitCode").GetInt32());
            Assert.Contains("GO_BLENDER_EXPECTED_FAILURE", failure.GetProperty("stdout").GetString() + failure.GetProperty("stderr").GetString());
            var sleepPath = Path.Combine(workspace, "sleep.py");
            await File.WriteAllTextAsync(sleepPath, "import time\ntime.sleep(10)\n", token);
            var sleepHash = await HashAsync(sleepPath);
            var timeout = await ExecuteAsync(new { operation = "run", path = "sleep.py", expectedSha256 = sleepHash, timeoutSeconds = 1 });
            Assert.False(timeout.GetProperty("success").GetBoolean());
            Assert.True(timeout.GetProperty("timedOut").GetBoolean());
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            cancellation.CancelAfter(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExecuteAsync(
                JsonSerializer.SerializeToElement(new { operation = "run", path = "sleep.py", expectedSha256 = sleepHash, timeoutSeconds = 120 }),
                workspace, null, cancellation.Token));
            results.Add(new { operation = "cancelled-run", cancelled = true, successResultReturned = false });
            passed = true;
        }
        catch (Exception exception)
        {
            error = exception.ToString();
            throw;
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(workspace, "runtime-acceptance.json"), JsonSerializer.Serialize(new { passed, error, results }, Json));
        }

        async Task<JsonElement> ExecuteAsync(object arguments)
        {
            var result = JsonSerializer.SerializeToElement(await service.ExecuteAsync(JsonSerializer.SerializeToElement(arguments), workspace, null, token), Json);
            results.Add(new { arguments, result });
            await File.WriteAllTextAsync(Path.Combine(workspace, "runtime-steps.json"), JsonSerializer.Serialize(results, Json), token);
            return result;
        }
        static async Task<string> HashAsync(string path)
        {
            await using var stream = File.OpenRead(path);
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
        }
    }
}
