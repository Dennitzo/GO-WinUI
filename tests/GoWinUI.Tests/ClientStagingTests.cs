using System.Security.Cryptography;
using System.Text.Json;
using GoAi.Contracts;
using GoWinUI.App.Services;

namespace GoWinUI.Tests;

/// <summary>Real client IO guards without launching Blender or modifying a user's open scene.</summary>
public sealed class ClientStagingTests
{
    [Fact]
    public async Task ChangedStageScriptCannotStartBlenderOrCreateRevision()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var args = await CreateStageAsync(environment.Directory);
        await File.AppendAllTextAsync(Path.Combine(environment.Directory, "step.py"), "\n# user edit");

        var error = await Assert.ThrowsAsync<IOException>(() => ExecuteAsync(environment.Directory, args));

        Assert.Contains("verändert", error.Message);
        AssertNoStageArtifacts(environment.Directory);
        Assert.EndsWith("# user edit", await File.ReadAllTextAsync(Path.Combine(environment.Directory, "step.py")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UserModifiedBaseSceneIsPreservedAndRequiresFreshHash()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var args = await CreateStageAsync(environment.Directory);
        var basePath = Path.Combine(environment.Directory, "accepted.blend");
        await File.WriteAllTextAsync(basePath, "accepted revision");
        args["baseScene"] = "accepted.blend";
        args["baseSceneSha256"] = await HashAsync(basePath);
        const string userEdit = "A newer scene saved by the user in Blender.";
        await File.WriteAllTextAsync(basePath, userEdit);

        var error = await Assert.ThrowsAsync<IOException>(() => ExecuteAsync(environment.Directory, args));

        Assert.Contains("Ausgangsszene", error.Message);
        Assert.Contains("verändert", error.Message);
        Assert.Equal(userEdit, await File.ReadAllTextAsync(basePath));
        AssertNoStageArtifacts(environment.Directory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingRevisionOrDirectoryCannotBeOverwritten(bool directory)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var args = await CreateStageAsync(environment.Directory);
        var target = Path.Combine(environment.Directory, "next.blend");
        if (directory) Directory.CreateDirectory(target);
        else await File.WriteAllTextAsync(target, "keep this accepted scene");

        var error = await Assert.ThrowsAsync<IOException>(() => ExecuteAsync(environment.Directory, args));

        Assert.Contains("existiert bereits", error.Message);
        if (directory) Assert.True(Directory.Exists(target));
        else Assert.Equal("keep this accepted scene", await File.ReadAllTextAsync(target));
        Assert.False(Directory.Exists(Path.Combine(environment.Directory, ".go-blender-stages")));
    }

    [Theory]
    [InlineData(12_000)]
    [InlineData(12_001)]
    public async Task StageLimitCountsUnicodeCharactersAndRejectsOversizedStepsBeforeProcessStart(int characters)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var args = await CreateStageAsync(environment.Directory, new string('ä', characters));
        // The occupied destination is the next guard. Reaching it at the exact limit
        // proves acceptance without launching a native process in a client unit test.
        var target = Path.Combine(environment.Directory, "next.blend");
        await File.WriteAllTextAsync(target, "protected revision");

        if (characters > WorkspaceTools.MaximumBlenderStageScriptCharacters)
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => ExecuteAsync(environment.Directory, args));
            Assert.Contains("12000", error.Message);
        }
        else
        {
            var error = await Assert.ThrowsAsync<IOException>(() => ExecuteAsync(environment.Directory, args));
            Assert.Contains("existiert bereits", error.Message);
        }
        Assert.Equal("protected revision", await File.ReadAllTextAsync(target));
        Assert.False(Directory.Exists(Path.Combine(environment.Directory, ".go-blender-stages")));
    }

    [Theory]
    [InlineData("path", "../outside.py")]
    [InlineData("outputPath", "../outside.blend")]
    [InlineData("outputPath", ".git/next.blend")]
    [InlineData("baseScene", "../outside.blend")]
    [InlineData("baseScene", ".git/source.blend")]
    public async Task EveryStagePathStaysInsideWorkspace(string property, string path)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var args = await CreateStageAsync(environment.Directory);
        args[property] = path;
        if (property == "baseScene") args["baseSceneSha256"] = new string('a', 64);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ExecuteAsync(environment.Directory, args));

        AssertNoStageArtifacts(environment.Directory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BaseSceneAndHashAreAnAtomicInputPair(bool provideHashOnly)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var args = await CreateStageAsync(environment.Directory);
        if (provideHashOnly) args["baseSceneSha256"] = new string('a', 64);
        else args["baseScene"] = "accepted.blend";

        await Assert.ThrowsAsync<ArgumentException>(() => ExecuteAsync(environment.Directory, args));

        AssertNoStageArtifacts(environment.Directory);
    }

    [Fact]
    public async Task CancelledStageDoesNotStartOrLeavePartialRevision()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var args = await CreateStageAsync(environment.Directory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExecuteAsync(environment.Directory, args, cancellation.Token));

        AssertNoStageArtifacts(environment.Directory);
    }

    [Fact]
    public async Task StalePreviewCannotReplaceTheVisibleScene()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var scene = Path.Combine(environment.Directory, "accepted.blend");
        const string current = "latest user revision";
        await File.WriteAllTextAsync(scene, current);
        var args = new Dictionary<string, object>
        {
            ["operation"] = "preview", ["path"] = "accepted.blend", ["expectedSha256"] = new string('a', 64),
        };

        await Assert.ThrowsAsync<IOException>(() => ExecuteAsync(environment.Directory, args));

        Assert.Equal(current, await File.ReadAllTextAsync(scene));
        AssertNoStageArtifacts(environment.Directory);
    }

    private static Task<object> ExecuteAsync(string workspace, Dictionary<string, object> args, CancellationToken token = default) =>
        new BlenderToolService(() => "must-not-start.exe").ExecuteAsync(JsonSerializer.SerializeToElement(args), workspace, null, token);

    private static async Task<Dictionary<string, object>> CreateStageAsync(string workspace, string content = "# one small geometry stage")
    {
        var script = Path.Combine(workspace, "step.py");
        await File.WriteAllTextAsync(script, content);
        return new Dictionary<string, object>
        {
            ["operation"] = "stage", ["path"] = "step.py", ["expectedSha256"] = await HashAsync(script),
            ["outputPath"] = "next.blend", ["label"] = "Habitat shell", ["timeoutSeconds"] = 60,
        };
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file));
    }

    private static void AssertNoStageArtifacts(string workspace)
    {
        Assert.False(File.Exists(Path.Combine(workspace, "next.blend")));
        Assert.False(Directory.Exists(Path.Combine(workspace, ".go-blender-stages")));
    }
}
