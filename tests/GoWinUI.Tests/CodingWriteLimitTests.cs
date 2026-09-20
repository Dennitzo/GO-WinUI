using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GoWinUI.Core.Coding;

namespace GoWinUI.Tests;

public sealed class CodingWriteLimitTests
{
    [Theory]
    [InlineData(17_440)]
    [InlineData(64_000)]
    public async Task CompleteScriptsWithinExpandedBoundAreSavedWithExactContentAndHash(int characters)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var content = new string('ä', characters);
        var result = await new LocalCodingToolExecutor(environment.Directory).ExecuteAsync("coding.write",
            JsonSerializer.SerializeToElement(new { path = "scene.py", content }));

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal(content, await File.ReadAllTextAsync(Path.Combine(environment.Directory, "scene.py")));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))), result.GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task ContentBeyondExpandedBoundIsRejectedBeforeWriting()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var executor = new LocalCodingToolExecutor(environment.Directory);
        await Assert.ThrowsAsync<ArgumentException>(() => executor.ExecuteAsync("coding.write",
            JsonSerializer.SerializeToElement(new { path = "too-large.py", content = new string('x', 64_001) })));
        Assert.False(File.Exists(Path.Combine(environment.Directory, "too-large.py")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingFileStillRequiresItsCurrentHashBeforeLargeWrite(bool staleHash)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var path = Path.Combine(environment.Directory, "scene.py");
        const string original = "Existing user scene must survive a conflicting write.";
        await File.WriteAllTextAsync(path, original);
        var executor = new LocalCodingToolExecutor(environment.Directory);
        var arguments = new Dictionary<string, object> { ["path"] = "scene.py", ["content"] = new string('x', 64_000) };
        if (staleHash) arguments["expectedSha256"] = new string('a', 64);
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync("coding.write", JsonSerializer.SerializeToElement(arguments)));
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        var read = await executor.ExecuteAsync("coding.read", JsonSerializer.SerializeToElement(new { path = "scene.py" }));
        arguments["expectedSha256"] = read.GetProperty("sha256").GetString()!;
        var saved = await executor.ExecuteAsync("coding.write", JsonSerializer.SerializeToElement(arguments));
        Assert.True(saved.GetProperty("success").GetBoolean());
        Assert.Equal((string)arguments["content"], await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ExpandedWritesKeepWorkspacePathProtection()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var executor = new LocalCodingToolExecutor(environment.Directory);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => executor.ExecuteAsync("coding.write",
            JsonSerializer.SerializeToElement(new { path = "../outside.py", content = new string('x', 64_000) })));
    }

    [Fact]
    public async Task ExpandedWritesDoNotExpandEditReplacementBounds()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var path = Path.Combine(environment.Directory, "scene.py");
        const string original = "unchanged";
        await File.WriteAllTextAsync(path, original);
        var executor = new LocalCodingToolExecutor(environment.Directory);
        var read = await executor.ExecuteAsync("coding.read", JsonSerializer.SerializeToElement(new { path = "scene.py" }));
        await Assert.ThrowsAsync<ArgumentException>(() => executor.ExecuteAsync("coding.edit", JsonSerializer.SerializeToElement(new
        {
            path = "scene.py", expectedSha256 = read.GetProperty("sha256").GetString(), oldText = original, newText = new string('x', 16_001),
        })));
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }
}
