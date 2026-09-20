using System.Security.Cryptography;
using System.Text.Json;
using GoWinUI.App.Services;

namespace GoWinUI.Tests;

public sealed class BlenderToolServiceTests
{
    [Theory]
    [InlineData("station")]
    [InlineData("station/")]
    [InlineData("station\\")]
    public async Task ScaffoldCreatesEditableProjectAndNeverOverwritesExistingWork(string path)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var service = new BlenderToolService(() => null);
        var args = JsonSerializer.SerializeToElement(new { operation = "scaffold", path, brief = "Eine kleine Forschungsstation" });
        var result = JsonSerializer.SerializeToElement(await service.ExecuteAsync(args, environment.Directory, null, default));
        Assert.True(result.GetProperty("success").GetBoolean());
        var design = Path.Combine(environment.Directory, "station", "design.json");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(design));
        Assert.Equal("Eine kleine Forschungsstation", manifest.RootElement.GetProperty("brief").GetString());
        Assert.Equal(JsonValueKind.Null, manifest.RootElement.GetProperty("currentScene").ValueKind);
        Assert.NotEmpty(await File.ReadAllTextAsync(Path.Combine(environment.Directory, "station", "go_blender.py")));
        var guidePath = result.GetProperty("modelingGuidePath").GetString()!;
        var guide = await File.ReadAllTextAsync(Path.Combine(environment.Directory, guidePath));
        Assert.True(guide.Length > 3000, "The packaged project must contain the complete practical modeling guide.");
        Assert.Empty(manifest.RootElement.GetProperty("stagePlan").EnumerateArray());
        var customized = "Keep this independently edited brief";
        await File.WriteAllTextAsync(design, customized);
        await Assert.ThrowsAsync<IOException>(() => service.ExecuteAsync(args, environment.Directory, null, default));
        Assert.Equal(customized, await File.ReadAllTextAsync(design));
    }

    [Fact]
    public async Task InfoHashesBinaryScenesWithoutReadingThemAsText()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var bytes = new byte[] { 66, 76, 69, 78, 68, 69, 82, 0, 255, 17 };
        await File.WriteAllBytesAsync(Path.Combine(environment.Directory, "test.blend"), bytes);
        var result = JsonSerializer.SerializeToElement(await new BlenderToolService(() => null).ExecuteAsync(
            JsonSerializer.SerializeToElement(new { operation = "info", path = "test.blend" }), environment.Directory, null, default));
        Assert.False(result.GetProperty("available").GetBoolean());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), result.GetProperty("file").GetProperty("sha256").GetString());
        Assert.Equal(bytes.Length, result.GetProperty("file").GetProperty("bytes").GetInt64());
        Assert.True(result.GetProperty("instruction").GetString()!.Length < 1000,
            "Repeated file-hash queries must not duplicate the complete modeling guide in the AI context.");
    }

    [Theory]
    [InlineData("scaffold", "../outside")]
    [InlineData("info", "../outside.blend")]
    [InlineData("info", ".git/scene.blend")]
    public async Task ProjectAndMetadataPathsCannotEscapeWorkspace(string operation, string path)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new BlenderToolService(() => null).ExecuteAsync(
            JsonSerializer.SerializeToElement(new { operation, path }), environment.Directory, null, default));
    }

    [Theory]
    [InlineData("run", "scene.py")]
    [InlineData("inspect", "scene.blend")]
    [InlineData("render", "scene.blend")]
    public async Task StaleInputNeverStartsProcessOrCreatesReviewDirectory(string operation, string path)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(environment.Directory, path), "changed after inspection");
        var values = new Dictionary<string, object> { ["operation"] = operation, ["path"] = path, ["expectedSha256"] = new string('a', 64) };
        if (operation != "run") values["outputDirectory"] = "review";
        var error = await Assert.ThrowsAsync<IOException>(() => new BlenderToolService(() => "must-not-start.exe").ExecuteAsync(
            JsonSerializer.SerializeToElement(values), environment.Directory, null, default));
        Assert.Contains("verändert", error.Message);
        Assert.False(Directory.Exists(Path.Combine(environment.Directory, "review")));
    }

    [Fact]
    public async Task MissingInstallationAndCancellationAreActionableAndDoNotWrite()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var service = new BlenderToolService(() => null);
        var error = await Assert.ThrowsAsync<FileNotFoundException>(() => service.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { operation = "open", path = "scene.blend" }), environment.Directory, null, default));
        Assert.Contains("GO_BLENDER_EXECUTABLE", error.Message);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { operation = "scaffold", path = "cancelled" }), environment.Directory, null, cancellation.Token));
        Assert.False(Directory.Exists(Path.Combine(environment.Directory, "cancelled")));
    }
}
