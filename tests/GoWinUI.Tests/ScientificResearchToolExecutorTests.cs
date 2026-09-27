using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GoAi.Contracts;
using GoWinUI.Core.Coding;

namespace GoWinUI.Tests;

public sealed class ScientificResearchToolExecutorTests : IAsyncLifetime
{
    private static readonly string[] EchoArguments = ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Write('SCIENTIFIC_OK')"];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "go-scientific-tools-" + Guid.NewGuid().ToString("N"));
    private ScientificResearchToolExecutor _executor = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _executor = new ScientificResearchToolExecutor(_root);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task WriteAndRestoreCreatedFileUsesHashBoundChangeSet()
    {
        var written = await Execute(ClientToolNames.ResearchCodeWrite,
            new { projectId = "proof-1", path = "src/candidate.py", content = "print(42)\n" });
        var changeSetId = written.GetProperty("changeSetId").GetString()!;
        var path = Path.Combine(_root, ".assistant", "research", "proof-1", "src", "candidate.py");
        Assert.Equal("print(42)\n", await File.ReadAllTextAsync(path));

        var restored = await Execute(ClientToolNames.ResearchCodeRestore, new { projectId = "proof-1", changeSetId });

        Assert.True(restored.GetProperty("success").GetBoolean());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task ExistingFileRequiresCurrentHashAndRestoresOriginalBytes()
    {
        var root = Path.Combine(_root, ".assistant", "research", "replication");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "model.py");
        var original = Encoding.UTF8.GetBytes("value = 1\n");
        await File.WriteAllBytesAsync(path, original);
        var hash = Convert.ToHexStringLower(SHA256.HashData(original));
        var written = await Execute(ClientToolNames.ResearchCodeWrite,
            new { projectId = "replication", path = "model.py", content = "value = 2\n", expectedSha256 = hash });

        await Execute(ClientToolNames.ResearchCodeRestore,
            new { projectId = "replication", changeSetId = written.GetProperty("changeSetId").GetString() });

        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task RestoreDoesNotOverwriteManualChangesAndCreatesRecoveryBundle()
    {
        var written = await Execute(ClientToolNames.ResearchCodeWrite,
            new { projectId = "manual-edit", path = "result.txt", content = "AI result" });
        var path = Path.Combine(_root, ".assistant", "research", "manual-edit", "result.txt");
        await File.WriteAllTextAsync(path, "manual result");

        var restored = await Execute(ClientToolNames.ResearchCodeRestore,
            new { projectId = "manual-edit", changeSetId = written.GetProperty("changeSetId").GetString() });

        Assert.False(restored.GetProperty("success").GetBoolean());
        Assert.True(restored.GetProperty("conflict").GetBoolean());
        Assert.Equal("manual result", await File.ReadAllTextAsync(path));
        Assert.True(Directory.Exists(Path.Combine(_root, ".assistant", "research", "manual-edit", ".state", "recovery",
            restored.GetProperty("recoveryId").GetString()!)));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData(".git/config")]
    [InlineData(".state/changesets/forged/manifest.json")]
    public async Task WriteRejectsPathsOutsideResearchProject(string path)
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Execute(ClientToolNames.ResearchCodeWrite,
            new { projectId = "safe", path, content = "forbidden" }));
    }

    [Fact]
    public async Task ResearchProjectIdentifierCannotCollapseItsStorageRoot()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Execute(ClientToolNames.ResearchCodeWrite,
            new { projectId = "..", path = "escape.txt", content = "forbidden" }));
    }

    [Fact]
    public async Task ResearchExecutionWritesReproducibleManifest()
    {
        var result = await Execute(ClientToolNames.ResearchCodeExecute, new
        {
            projectId = "experiment",
            experimentId = "echo-1",
            executable = "powershell.exe",
            arguments = EchoArguments,
            timeoutSeconds = 30,
        });

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal("WindowsJobObjectProcessTree", result.GetProperty("processIsolation").GetString());
        Assert.Equal("NotEnforced", result.GetProperty("networkIsolation").GetString());
        var manifest = Path.Combine(_root, result.GetProperty("manifestPath").GetString()!);
        Assert.True(File.Exists(manifest));
        Assert.Contains("SCIENTIFIC_OK", await File.ReadAllTextAsync(manifest), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SymbolicAndNumericAdaptersExecuteRealLocalToolchainsWhenAvailable()
    {
        var symbolic = await Execute(ClientToolNames.MathSymbolic,
            new { projectId = "math", expression = "(x + 1)**2", operation = "expand", symbol = "x", timeoutSeconds = 30 });
        var numeric = await Execute(ClientToolNames.MathNumeric,
            new { projectId = "math", expression = "sqrt(2)", precision = 60, timeoutSeconds = 30 });

        Assert.True(symbolic.GetProperty("success").GetBoolean());
        Assert.Contains("x**2 + 2*x + 1", symbolic.GetProperty("runs")[0].GetProperty("stdout").GetString(), StringComparison.Ordinal);
        Assert.True(numeric.GetProperty("success").GetBoolean());
        Assert.Contains("1.414213562373095", numeric.GetProperty("runs")[0].GetProperty("stdout").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeanAdapterOnlyMarksSuccessfulKernelRunAsFormalVerification()
    {
        var result = await Execute(ClientToolNames.MathFormalProof,
            new { projectId = "formal", source = "example : True := by\n  trivial\n", timeoutSeconds = 30 });

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal("KernelAccepted", result.GetProperty("formalVerification").GetString());
    }

    private Task<JsonElement> Execute(string name, object arguments) =>
        _executor.ExecuteAsync(name, JsonSerializer.SerializeToElement(arguments));

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }
}
