using GoWinUI.App.Services;
using System.Security.Cryptography;

namespace GoWinUI.Tests;

public sealed class LeanProofServiceTests
{
    private static readonly HashSet<string> AllowedAxioms = new(StringComparer.Ordinal)
    {
        "propext", "Classical.choice", "Quot.sound",
    };

    [Fact]
    public async Task StatusReturnsActionableToolchainState()
    {
        var workspace = CreateWorkspace();
        try
        {
            var result = await new LeanProofService().StatusAsync(workspace);

            Assert.Equal("status", result.Operation);
            if (result.Available)
            {
                Assert.True(result.Passed, result.Message);
                Assert.False(string.IsNullOrWhiteSpace(result.LeanVersion));
                Assert.False(string.IsNullOrWhiteSpace(result.LakeVersion));
            }
            else
            {
                Assert.Contains("install-coding-proof-tools.ps1", result.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Theory]
    [InlineData("theorem incomplete : True := by\n  sorry\n", "sorry")]
    [InlineData("theorem incomplete : True := by\n  admit\n", "admit")]
    [InlineData("axiom invented : False\ntheorem invalid : False := invented\n", "axiom")]
    [InlineData("theorem invalid : True := Lean.trustCompiler\n", "Lean.trustCompiler")]
    public async Task VerifyRejectsForbiddenConstructsBeforeExecution(string source, string expected)
    {
        var workspace = CreateWorkspace();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workspace, "Invalid.lean"), source);

            var result = await new LeanProofService().VerifyAsync(
                workspace,
                "Invalid.lean",
                "invalid",
                TimeSpan.FromSeconds(10));

            Assert.False(result.Passed);
            Assert.Contains(expected, result.ForbiddenConstructs);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task CheckRejectsPathEscape()
    {
        var workspace = CreateWorkspace();
        try
        {
            var service = new LeanProofService();
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.CheckAsync(workspace, "../Outside.lean", TimeSpan.FromSeconds(10)));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task LiveVerifyAcceptsKernelCheckedTheoremAndReportsAxioms()
    {
        var workspace = CreateWorkspace();
        try
        {
            var service = new LeanProofService();
            if (!await RequireLiveLeanAsync(service, workspace)) return;
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "Valid.lean"),
                "theorem arithmetic_identity : 2 + 2 = 4 := by decide\n");

            var result = await service.VerifyAsync(
                workspace,
                "Valid.lean",
                "arithmetic_identity",
                TimeSpan.FromSeconds(30));

            Assert.True(result.Passed, result.Message);
            Assert.Empty(result.ForbiddenConstructs);
            Assert.All(result.Axioms, axiom =>
                Assert.Contains(axiom, AllowedAxioms));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task LiveCodingProofVerifierUsesTheSameLeanVerificationService()
    {
        var workspace = CreateWorkspace();
        try
        {
            var service = new LeanProofService();
            if (!await RequireLiveLeanAsync(service, workspace)) return;
            var proofDirectory = Directory.CreateDirectory(Path.Combine(workspace, "proofs", "identity"));
            var artifact = Path.Combine(proofDirectory.FullName, "Identity.lean");
            await File.WriteAllTextAsync(artifact, "theorem Identity.result : 3 * 3 = 9 := by decide\n");
            await using var stream = File.OpenRead(artifact);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
            await File.WriteAllTextAsync(
                Path.Combine(proofDirectory.FullName, "proof.json"),
                $$"""
                {
                  "caseId": "identity",
                  "kind": "formal",
                  "statement": "Drei multipliziert mit drei ist in den natürlichen Zahlen gleich neun.",
                  "assumptions": [],
                  "validityDomain": "Natürliche Zahlen in Lean.",
                  "artifact": "proofs/identity/Identity.lean",
                  "sourceSha256": "{{hash}}",
                  "theoremName": "Identity.result"
                }
                """);

            var result = Assert.Single(await new CodingProofVerifier(service).VerifyAllAsync(workspace));

            Assert.True(result.Passed, result.Detail);
            Assert.Contains("Identity.result", result.Detail, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task FormalProofManifestRequiresConcreteTheoremName()
    {
        var workspace = CreateWorkspace();
        try
        {
            var proofDirectory = Directory.CreateDirectory(Path.Combine(workspace, "proofs", "identity"));
            var artifact = Path.Combine(proofDirectory.FullName, "Identity.lean");
            await File.WriteAllTextAsync(artifact, "theorem Identity.result : True := by trivial\n");
            await using var stream = File.OpenRead(artifact);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
            await File.WriteAllTextAsync(
                Path.Combine(proofDirectory.FullName, "proof.json"),
                $$"""
                {
                  "caseId": "identity",
                  "kind": "formal",
                  "statement": "Die IdentitÃ¤tsaussage ist innerhalb der Lean-Logik wahr.",
                  "assumptions": [],
                  "validityDomain": "Aussagenlogik in Lean.",
                  "artifact": "proofs/identity/Identity.lean",
                  "sourceSha256": "{{hash}}"
                }
                """);

            var result = Assert.Single(await new CodingProofVerifier().VerifyAllAsync(workspace));

            Assert.False(result.Passed);
            Assert.Contains("theoremName", result.Detail, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task LiveVerifyRejectsAxiomImportedFromAnotherModule()
    {
        var workspace = CreateWorkspace();
        try
        {
            var service = new LeanProofService();
            if (!await RequireLiveLeanAsync(service, workspace)) return;
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "lakefile.lean"),
                "import Lake\nopen Lake DSL\npackage ProofFixture where\nlean_lib ProofFixture where\n");
            var moduleDirectory = Directory.CreateDirectory(Path.Combine(workspace, "ProofFixture"));
            await File.WriteAllTextAsync(
                Path.Combine(moduleDirectory.FullName, "Foundation.lean"),
                "axiom invented : False\n");
            await File.WriteAllTextAsync(
                Path.Combine(moduleDirectory.FullName, "Target.lean"),
                "import ProofFixture.Foundation\ntheorem Target.result : False := invented\n");
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "ProofFixture.lean"),
                "import ProofFixture.Target\n");
            var build = await service.BuildAsync(workspace, ".", "ProofFixture", TimeSpan.FromMinutes(1));
            Assert.True(build.Passed, build.Message);

            var result = await service.VerifyAsync(
                workspace,
                "ProofFixture/Target.lean",
                "Target.result",
                TimeSpan.FromSeconds(30));

            Assert.False(result.Passed);
            Assert.True(
                result.ForbiddenConstructs.Concat(result.Axioms.Select(axiom => "axiom:" + axiom))
                    .Contains("axiom:invented", StringComparer.Ordinal),
                System.Text.Json.JsonSerializer.Serialize(result));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task LiveVerifyRejectsSyntaxErrorAndUnknownTheorem()
    {
        var workspace = CreateWorkspace();
        try
        {
            var service = new LeanProofService();
            if (!await RequireLiveLeanAsync(service, workspace)) return;
            await File.WriteAllTextAsync(Path.Combine(workspace, "Broken.lean"), "theorem broken : True := by\n  exact\n");
            var syntax = await service.CheckAsync(workspace, "Broken.lean", TimeSpan.FromSeconds(30));
            Assert.False(syntax.Passed);
            Assert.NotEmpty(syntax.Diagnostics);

            await File.WriteAllTextAsync(Path.Combine(workspace, "Valid.lean"), "theorem present : True := by trivial\n");
            var missing = await service.VerifyAsync(
                workspace,
                "Valid.lean",
                "missing",
                TimeSpan.FromSeconds(30));
            Assert.False(missing.Passed);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task LiveBuildSupportsLakeProject()
    {
        var workspace = CreateWorkspace();
        try
        {
            var service = new LeanProofService();
            if (!await RequireLiveLeanAsync(service, workspace)) return;
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "lakefile.lean"),
                "import Lake\nopen Lake DSL\npackage «ProofFixture» where\nlean_lib ProofFixture where\n");
            Directory.CreateDirectory(Path.Combine(workspace, "ProofFixture"));
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "ProofFixture", "Basic.lean"),
                "theorem lake_identity : 1 + 1 = 2 := by decide\n");

            var result = await service.BuildAsync(workspace, ".", null, TimeSpan.FromMinutes(1));

            Assert.True(result.Passed, result.Message);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task LiveCheckHonorsCancellation()
    {
        var workspace = CreateWorkspace();
        try
        {
            var service = new LeanProofService();
            if (!await RequireLiveLeanAsync(service, workspace)) return;
            await File.WriteAllTextAsync(Path.Combine(workspace, "Valid.lean"), "theorem present : True := by trivial\n");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.CheckAsync(workspace, "Valid.lean", TimeSpan.FromSeconds(30), cancellation.Token));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task LiveCheckTerminatesProcessTreeAtTimeout()
    {
        var workspace = CreateWorkspace();
        try
        {
            var service = new LeanProofService();
            if (!await RequireLiveLeanAsync(service, workspace)) return;
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "Hang.lean"),
                "import Lean\nopen Lean Elab Command\nelab \"#go_hang\" : command => do\n  while true do\n    IO.sleep 1000\n#go_hang\n");

            var result = await service.CheckAsync(workspace, "Hang.lean", TimeSpan.FromMilliseconds(500));

            Assert.True(result.TimedOut, result.Message);
            Assert.False(result.Passed);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private static async Task<bool> RequireLiveLeanAsync(LeanProofService service, string workspace)
    {
        var status = await service.StatusAsync(workspace);
        if (string.Equals(Environment.GetEnvironmentVariable("GO_RUN_LEAN_LIVE_TESTS"), "1", StringComparison.Ordinal))
        {
            Assert.True(status.Available && status.Passed, status.Message);
        }
        return status.Available && status.Passed;
    }

    private static string CreateWorkspace()
    {
        var path = Path.Combine(Path.GetTempPath(), "go-lean-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
