using GoAi.Contracts;
using GoWinUI.App.Services;
using GoWinUI.BricsCad.Protocol;
using GoWinUI.Infrastructure;
using System.Text.Json;

namespace GoWinUI.Tests;

public sealed class LocalToolBrokerValidationTests
{
    [Theory]
    [InlineData("/")]
    [InlineData(".")]
    [InlineData("./")]
    [InlineData("workspace")]
    [InlineData("/workspace")]
    public void WorkspaceRootAliasesNormalizeToDot(string value)
    {
        Assert.Equal(".", LocalToolBroker.NormalizeWorkspaceAlias(value));
    }

    private static readonly string[] VersionArguments = ["--version"];
    private static readonly string[] LeanMainArguments = ["Main.lean"];
    private static readonly string[] VoiceSearchTerms = ["voice", "speech", "SpeechRecognition"];
    private static readonly string[] AllFilesGlob = ["**/*"];

    [Fact]
    public void SharedDocumentContractsAreAcceptedLocally()
    {
        var now = DateTimeOffset.UtcNow;
        var read = Create(
            ClientToolNames.DocumentRead,
            ToolRiskClass.ReadOnly,
            new
            {
                scope = "session",
                mode = "read",
                reference = Guid.NewGuid().ToString("D"),
                startUnit = 2,
                maximumUnits = 4,
                maximumCharacters = 12_000,
            },
            now);
        var create = Create(
            ClientToolNames.DocumentCreate,
            ToolRiskClass.LocalMutation,
            new
            {
                operation = "appendSection",
                reference = Guid.NewGuid().ToString("D"),
                format = "docx",
                sectionId = "kapitel.zwei",
                heading = "Kapitel zwei",
                content = "Begrenzter neuer Abschnitt",
                expectedSha256 = new string('a', 64),
            },
            now);

        LocalToolBroker.ValidateProposal(read, now);
        LocalToolBroker.ValidateProposal(create, now);
    }

    [Fact]
    public void SharedDocumentContractsRejectUnboundedReadsAndUnversionedEdits()
    {
        var now = DateTimeOffset.UtcNow;
        var unboundedRead = Create(
            ClientToolNames.DocumentRead,
            ToolRiskClass.ReadOnly,
            new
            {
                scope = "workspace",
                mode = "read",
                reference = "bericht.pdf",
                maximumCharacters = 40_001,
            },
            now);
        var unversionedEdit = Create(
            ClientToolNames.DocumentCreate,
            ToolRiskClass.LocalMutation,
            new
            {
                operation = "replaceSection",
                reference = "bericht.pdf",
                format = "pdf",
                sectionId = "ergebnis",
                content = "Geänderter Abschnitt",
            },
            now);

        Assert.Throws<InvalidDataException>(() => LocalToolBroker.ValidateProposal(unboundedRead, now));
        Assert.Throws<InvalidDataException>(() => LocalToolBroker.ValidateProposal(unversionedEdit, now));
    }

    [Fact]
    public void CodeRunPresetIsAcceptedWithAWorkspaceRelativeTarget()
    {
        var now = DateTimeOffset.UtcNow;
        var proposal = Create(
            ClientToolNames.ProcessRunPreset,
            ToolRiskClass.Process,
            new { preset = "code.run", target = "test.py" },
            now);

        LocalToolBroker.ValidateProposal(proposal, now);
    }

    [Fact]
    public void LeanVerifyUsesTypedProcessContract()
    {
        var now = DateTimeOffset.UtcNow;
        var proposal = Create(
            ClientToolNames.LeanProof,
            ToolRiskClass.Process,
            new
            {
                operation = "verify",
                path = "proofs/Main.lean",
                theoremName = "Main.result",
                timeoutSeconds = 120,
            },
            now);

        LocalToolBroker.ValidateProposal(proposal, now);
    }

    [Theory]
    [InlineData("lean")]
    [InlineData("lean.exe")]
    [InlineData("C:\\Tools\\lake.exe")]
    public void GenericProcessCannotBypassTypedLeanContract(string executable)
    {
        var now = DateTimeOffset.UtcNow;
        var proposal = Create(
            ClientToolNames.ProcessRun,
            ToolRiskClass.Process,
            new
            {
                executable,
                arguments = LeanMainArguments,
                purpose = "test",
                startMode = "wait",
            },
            now);

        var exception = Assert.Throws<InvalidDataException>(() => LocalToolBroker.ValidateProposal(proposal, now));
        Assert.Contains(ClientToolNames.LeanProof, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("cargo", "test")]
    [InlineData("zig", "build")]
    [InlineData("cmake", "build")]
    [InlineData("java", "start")]
    [InlineData("ruby", "start")]
    public void DirectWorkspaceProcessesAreNotRestrictedToSpecificLanguages(
        string executable,
        string purpose)
    {
        var now = DateTimeOffset.UtcNow;
        var proposal = Create(
            ClientToolNames.ProcessRun,
            ToolRiskClass.Process,
            new
            {
                executable,
                arguments = VersionArguments,
                workingDirectory = ".",
                purpose,
                startMode = "wait",
            },
            now);

        LocalToolBroker.ValidateProposal(proposal, now);
    }

    [Theory]
    [InlineData("npm.cmd", "install")]
    [InlineData("node", "--version")]
    [InlineData("pnpm.cmd", "install")]
    [InlineData("yarn.cmd", "install")]
    [InlineData("bun", "install")]
    [InlineData("cargo", "fetch")]
    [InlineData("go", "mod")]
    [InlineData("dotnet", "restore")]
    public void WorkspaceFrameworkSetupProcessesAreAccepted(string executable, string firstArgument)
    {
        var now = DateTimeOffset.UtcNow;
        var processArguments = firstArgument == "mod"
            ? new[] { "mod", "download" }
            : new[] { firstArgument };
        var proposal = Create(
            ClientToolNames.ProcessRun,
            ToolRiskClass.Process,
            new
            {
                executable,
                arguments = processArguments,
                workingDirectory = ".",
                purpose = "setup",
                startMode = "wait",
            },
            now);

        LocalToolBroker.ValidateProposal(proposal, now);
    }

    [Theory]
    [InlineData("pip", "install")]
    [InlineData("pip3.exe", "uninstall")]
    public void GlobalPythonPackageMutationsAreRejected(string executable, string command)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            LocalToolBroker.ValidatePythonEnvironmentBoundary(
                executable,
                [command, "numpy"],
                executableIsInsideWorkspace: false));

        Assert.Contains(".venv", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PythonLauncherCannotMutateGlobalPackages()
    {
        Assert.Throws<InvalidOperationException>(() =>
            LocalToolBroker.ValidatePythonEnvironmentBoundary(
                "py.exe",
                ["-3.11", "-m", "pip", "install", "scipy"],
                executableIsInsideWorkspace: false));
    }

    [Fact]
    public void WorkspacePythonMayInstallItsOwnDependencies()
    {
        LocalToolBroker.ValidatePythonEnvironmentBoundary(
            @"C:\Workspace\.venv\Scripts\python.exe",
            ["-m", "pip", "install", "numpy"],
            executableIsInsideWorkspace: true);
    }

    [Fact]
    public void ReadOnlyGlobalPipInspectionRemainsAvailable()
    {
        LocalToolBroker.ValidatePythonEnvironmentBoundary(
            "pip",
            ["show", "numpy"],
            executableIsInsideWorkspace: false);
    }

    [Theory]
    [InlineData("add", "einstein_engine.py")]
    [InlineData("reset", "--hard")]
    [InlineData("restore", "einstein_engine.py")]
    [InlineData("checkout", "--", "einstein_engine.py")]
    [InlineData("commit", "-m", "agent change")]
    [InlineData("stash", "push")]
    [InlineData("clean", "-fd")]
    [InlineData("update-index", "--refresh")]
    public void AutonomousProcessCannotMutateGitState(string command, params string[] arguments)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            LocalToolBroker.ValidateGitProcessBoundary("git.exe", [command, .. arguments]));

        Assert.Contains("nicht erlaubt", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("diff", "--", "einstein_engine.py")]
    [InlineData("log", "-5", "--oneline")]
    [InlineData("show", "HEAD:einstein_engine.py")]
    [InlineData("grep", "Ricci")]
    [InlineData("--no-pager", "diff", "--", "einstein_engine.py")]
    public void ReadOnlyGitInspectionRemainsAvailable(string command, params string[] arguments)
    {
        LocalToolBroker.ValidateGitProcessBoundary("git", [command, .. arguments]);
    }

    [Fact]
    public void GitCannotRedirectOutputOrSelectAnAlternateRepositoryBoundary()
    {
        Assert.Throws<InvalidOperationException>(() =>
            LocalToolBroker.ValidateGitProcessBoundary("git", ["diff", "--output=changes.patch"]));
        Assert.Throws<InvalidOperationException>(() =>
            LocalToolBroker.ValidateGitProcessBoundary("git", ["-C", "..", "status"]));
    }

    [Fact]
    public void EmptyPythonInvocationCannotMasqueradeAsVerification()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            LocalToolBroker.ValidatePythonProcessHasEntryPoint("python.exe", []));

        Assert.Contains("keine ausführbare Test-, Build- oder Startprüfung", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-m", "pytest")]
    [InlineData("-m", "py_compile")]
    [InlineData("-c", "import app")]
    public void ConcretePythonEntryPointsRemainAllowed(string first, string second)
    {
        LocalToolBroker.ValidatePythonProcessHasEntryPoint("python.exe", [first, second]);
    }

    [Fact]
    public void InlinePythonCommandIsSeparatedAndCapturedOutputSuppressionIsRemoved()
    {
        var normalized = LocalToolBroker.NormalizeInlineProcessRequest(
            "py -3.11 -m json.tool einstein_cases.json >nul 2>&1",
            []);

        Assert.Equal("py", normalized.Executable);
        Assert.Equal(["-3.11", "-m", "json.tool", "einstein_cases.json"], normalized.Arguments);
    }

    [Fact]
    public void InlinePythonCodeRemainsOneArgumentEvenWhenTheModelOmittedQuotes()
    {
        var normalized = LocalToolBroker.NormalizeInlineProcessRequest(
            "python -c import json; print(json.load(open('einstein_cases.json')))",
            []);

        Assert.Equal("python", normalized.Executable);
        Assert.Equal(
            ["-c", "import json; print(json.load(open('einstein_cases.json')))"],
            normalized.Arguments);
    }

    [Fact]
    public void SafeCmdWrapperIsConvertedToADirectProcess()
    {
        var normalized = LocalToolBroker.NormalizeInlineProcessRequest(
            "cmd /c \"py -0p\"",
            []);

        Assert.Equal("py", normalized.Executable);
        Assert.Equal(["-0p"], normalized.Arguments);
    }

    [Fact]
    public void ShellChainingInsideCmdWrapperRemainsRejected()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            LocalToolBroker.NormalizeInlineProcessRequest(
                "cmd /c \"python verify.py & whoami\"",
                []));

        Assert.Contains("Shell-Verkettungen", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingExecutablePathWithSpacesIsNotSplit()
    {
        var root = Path.Combine(Path.GetTempPath(), "go-process-path-" + Guid.NewGuid().ToString("N"));
        var executable = Path.Combine(root, "Tool Folder", "tool.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllBytes(executable, []);
        try
        {
            var normalized = LocalToolBroker.NormalizeInlineProcessRequest(executable, ["--version"]);

            Assert.Equal(executable, normalized.Executable);
            Assert.Equal(["--version"], normalized.Arguments);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BarePythonCommandsUseAnExistingWorkspaceEnvironment()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "go-python-command-" + Guid.NewGuid().ToString("N"));
        var python = Path.Combine(workspace, ".venv", "Scripts", "python.exe");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(python)!);
            File.WriteAllBytes(python, []);

            var resolved = LocalToolBroker.ResolveWorkspacePythonCommand(
                "pip",
                "pip",
                ["install", "numpy"],
                workspace);

            Assert.True(resolved.Isolated);
            Assert.Equal(Path.GetFullPath(python), resolved.Executable);
            Assert.Equal(["-m", "pip", "install", "numpy"], resolved.Arguments);
        }
        finally
        {
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("python3", "python")]
    [InlineData("python3.exe", "python")]
    public void Python3AliasesUseTheInstalledWindowsPythonWhenNoVenvExists(
        string requested,
        string expected)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "go-system-python-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var normalized = LocalToolBroker.NormalizePythonProcessRequest(
                requested,
                ["--version"],
                workspace);

            Assert.Equal(expected, normalized.Executable);
            Assert.Equal(["--version"], normalized.Arguments);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void SystemRuntimeAliasesRemainPlatformAppropriate()
    {
        Assert.Equal("node", LocalToolBroker.NormalizeSystemRuntimeAlias("nodejs"));
        Assert.Equal("node", LocalToolBroker.NormalizeSystemRuntimeAlias("nodejs.exe"));
        Assert.Equal("cargo", LocalToolBroker.NormalizeSystemRuntimeAlias("cargo"));
        Assert.Equal(@"tools\nodejs.exe", LocalToolBroker.NormalizeSystemRuntimeAlias(@"tools\nodejs.exe"));
    }

    [Theory]
    [InlineData(@"C:\Users\AMD\AppData\Local\Programs\Python\Python311\python.exe")]
    [InlineData("python311")]
    public void PythonBootstrapPathsAndInventedAliasesUseTheVersionedLauncher(string executable)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "go-python-bootstrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var normalized = LocalToolBroker.NormalizePythonProcessRequest(
                executable,
                ["-m", "venv", ".venv"],
                workspace);

            Assert.Equal("py", normalized.Executable);
            Assert.Equal(["-3.11", "-m", "venv", ".venv"], normalized.Arguments);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void GlobalPipPathUsesExistingWorkspaceEnvironmentAndDropsItsPythonSelector()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "go-python-pip-" + Guid.NewGuid().ToString("N"));
        var python = Path.Combine(workspace, ".venv", "Scripts", "python.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(python)!);
        File.WriteAllBytes(python, []);
        try
        {
            var normalized = LocalToolBroker.NormalizePythonProcessRequest(
                @"C:\Users\AMD\AppData\Local\Programs\Python\Python311\Scripts\pip.exe",
                ["--python", @".venv\Scripts\python.exe", "install", "sympy"],
                workspace);

            Assert.Equal(Path.GetFullPath(python), normalized.Executable);
            Assert.Equal(["-m", "pip", "install", "sympy"], normalized.Arguments);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void MultiSearchAndArbitraryFileExtensionsAreAccepted()
    {
        var now = DateTimeOffset.UtcNow;
        var search = Create(
            ClientToolNames.FileSystemSearch,
            ToolRiskClass.ReadOnly,
            new
            {
                path = ".",
                queries = VoiceSearchTerms,
                matchMode = "literal",
                includeGlobs = AllFilesGlob,
                contextLines = 2,
            },
            now);
        var read = Create(
            ClientToolNames.FileSystemReadMany,
            ToolRiskClass.ReadOnly,
            new
            {
                items = new[]
                {
                    new { path = "firmware/main.zig", startLine = 1, endLine = 500 },
                    new { path = "config/toolchain.customlang", startLine = 1, endLine = 500 },
                },
            },
            now);

        LocalToolBroker.ValidateProposal(search, now);
        LocalToolBroker.ValidateProposal(read, now);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    public void WorkspaceRootPathsAreAcceptedByReadOnlyTools(string path)
    {
        var now = DateTimeOffset.UtcNow;
        var list = Create(
            ClientToolNames.FileSystemList,
            ToolRiskClass.ReadOnly,
            new { path },
            now);
        var stat = Create(
            ClientToolNames.FileSystemStat,
            ToolRiskClass.ReadOnly,
            new { path },
            now);
        var search = Create(
            ClientToolNames.FileSystemSearch,
            ToolRiskClass.ReadOnly,
            new { path, query = "test" },
            now);

        LocalToolBroker.ValidateProposal(list, now);
        LocalToolBroker.ValidateProposal(stat, now);
        LocalToolBroker.ValidateProposal(search, now);
    }

    [Theory]
    [InlineData("/workspace/primzahlen_bis_1000.py", "primzahlen_bis_1000.py")]
    [InlineData("workspace/primzahlen_bis_1000.py", "primzahlen_bis_1000.py")]
    [InlineData("primzahlen_bis_1000.py", "primzahlen_bis_1000.py")]
    public void ModelWorkspaceAliasesBecomeClientRelativePaths(string input, string expected)
    {
        Assert.Equal(expected, LocalToolBroker.NormalizeWorkspaceAlias(input));
    }

    [Fact]
    public void MissingReadPathSuggestionsComeOnlyFromActuallyIndexedTextFiles()
    {
        var now = DateTimeOffset.UtcNow;
        WorkspaceIndexEntry Entry(string path, bool binary = false) => new(
            path,
            100,
            now,
            new string('a', 64),
            binary,
            true,
            binary ? "binary" : "markdown");
        WorkspaceIndexEntry[] entries =
        [
            Entry("docs/harmonic_oscillator.md"),
            Entry("chapters/harmonic-oscillator-de.md"),
            Entry("chapters/thermodynamik.md"),
            Entry("chapters/harmonic_oscillator.pdf", binary: true),
            Entry("src/Oscillator.cs"),
        ];

        var suggestions = LocalToolBroker.FindWorkspacePathSuggestions(
            "chapters/harmonic_oscillator.md",
            entries);

        Assert.Equal("docs/harmonic_oscillator.md", suggestions[0]);
        Assert.Contains("chapters/harmonic-oscillator-de.md", suggestions);
        Assert.Contains("chapters/thermodynamik.md", suggestions);
        Assert.DoesNotContain("chapters/harmonic_oscillator.pdf", suggestions);
        Assert.All(suggestions, path => Assert.Contains(entries, entry => entry.Path == path));
    }

    [Fact]
    public void MissingReadPathWithoutEvidenceDoesNotInventASuggestion()
    {
        WorkspaceIndexEntry[] entries =
        [
            new(
                "src/App.cs",
                100,
                DateTimeOffset.UtcNow,
                new string('a', 64),
                false,
                true,
                "csharp"),
        ];

        var suggestions = LocalToolBroker.FindWorkspacePathSuggestions(
            "chapters/harmonic_oscillator.md",
            entries);

        Assert.Empty(suggestions);
    }

    [Fact]
    public async Task MissingReadTextReturnsStructuredExistingCandidatesToTheAgent()
    {
        var root = Path.Combine(Path.GetTempPath(), "GO", "missing-path-test", Guid.NewGuid().ToString("N"));
        var chapterDirectory = Path.Combine(root, "chapters");
        Directory.CreateDirectory(chapterDirectory);
        await File.WriteAllTextAsync(Path.Combine(chapterDirectory, "harmonic-oscillator-de.md"), "# Harmonischer Oszillator");
        var index = new WorkspaceRepositoryIndex(new GoInfrastructureOptions
        {
            DataDirectory = Path.Combine(root, ".go-test-cache"),
        });
        var confirmation = new ToolConfirmationService(null!);
        var bricsCad = new BricsCadBridgeHost();
        try
        {
            var broker = new LocalToolBroker(
                connection: null!,
                settings: null!,
                confirmation,
                bricsCad,
                index,
                documents: null!);
            var proposal = Create(
                ClientToolNames.FileSystemReadText,
                ToolRiskClass.ReadOnly,
                new { path = "chapters/harmonic_oscillator.md" },
                DateTimeOffset.UtcNow);

            var result = await broker.ExecuteAsync(proposal, root);

            Assert.Equal("failed", result.Status);
            Assert.Equal("client.workspace_path_not_found", result.ErrorCode);
            Assert.Equal("path_not_found", result.Result.GetProperty("reason").GetString());
            Assert.Contains(
                "chapters/harmonic-oscillator-de.md",
                result.Result.GetProperty("suggestedPaths").EnumerateArray().Select(static item => item.GetString()));
        }
        finally
        {
            index.Dispose();
            confirmation.Dispose();
            await bricsCad.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MissingBoundedMultiReadReturnsStructuredExistingCandidatesToTheAgent()
    {
        var root = Path.Combine(Path.GetTempPath(), "GO", "missing-multi-path-test", Guid.NewGuid().ToString("N"));
        var sourceDirectory = Path.Combine(root, "src");
        Directory.CreateDirectory(sourceDirectory);
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "TemperatureController.cs"), "internal sealed class TemperatureController {}\n");
        var index = new WorkspaceRepositoryIndex(new GoInfrastructureOptions
        {
            DataDirectory = Path.Combine(root, ".go-test-cache"),
        });
        var confirmation = new ToolConfirmationService(null!);
        var bricsCad = new BricsCadBridgeHost();
        try
        {
            var broker = new LocalToolBroker(
                connection: null!,
                settings: null!,
                confirmation,
                bricsCad,
                index,
                documents: null!);
            var proposal = Create(
                ClientToolNames.FileSystemReadMany,
                ToolRiskClass.ReadOnly,
                new
                {
                    items = new[]
                    {
                        new { path = "src/TempratureController.cs", startLine = 1, endLine = 20 },
                    },
                    maximumCharacters = 12_000,
                },
                DateTimeOffset.UtcNow);

            var result = await broker.ExecuteAsync(proposal, root);

            Assert.Equal("failed", result.Status);
            Assert.Equal("client.workspace_path_not_found", result.ErrorCode);
            Assert.Equal("path_not_found", result.Result.GetProperty("reason").GetString());
            Assert.Contains(
                "src/TemperatureController.cs",
                result.Result.GetProperty("suggestedPaths").EnumerateArray().Select(static item => item.GetString()));
        }
        finally
        {
            index.Dispose();
            confirmation.Dispose();
            await bricsCad.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspaceWriteReturnsVersionEvidenceAndInvalidatesTheReadCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "GO", "write-version-test", Guid.NewGuid().ToString("N"));
        var cacheRoot = root + "-cache";
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "Program.cs");
        const string original = "internal static class Program { }\n";
        const string updated = "internal static class Program { public static int Value => 1; }\n";
        await File.WriteAllTextAsync(file, original, new System.Text.UTF8Encoding(false));
        var beforeSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(original))).ToLowerInvariant();
        var afterSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(updated))).ToLowerInvariant();
        var index = new WorkspaceRepositoryIndex(new GoInfrastructureOptions
        {
            DataDirectory = cacheRoot,
        });
        var confirmation = new ToolConfirmationService(null!);
        var bricsCad = new BricsCadBridgeHost();
        try
        {
            _ = await index.GetSnapshotForRunAsync(root);
            var broker = new LocalToolBroker(null!, null!, confirmation, bricsCad, index, null!);
            var write = Create(
                ClientToolNames.FileSystemWriteText,
                ToolRiskClass.LocalMutation,
                new { path = "Program.cs", content = updated, expectedSha256 = beforeSha },
                DateTimeOffset.UtcNow);

            var writeResult = await broker.ExecuteAsync(write, root);

            Assert.Equal("completed", writeResult.Status);
            Assert.Equal(beforeSha, writeResult.Result.GetProperty("beforeSha256").GetString());
            Assert.Equal(afterSha, writeResult.Result.GetProperty("afterSha256").GetString());

            var read = Create(
                ClientToolNames.FileSystemReadText,
                ToolRiskClass.ReadOnly,
                new { path = "Program.cs", startLine = 1, endLine = 20 },
                DateTimeOffset.UtcNow);
            var readResult = await broker.ExecuteAsync(read, root);
            Assert.Equal("completed", readResult.Status);
            Assert.Equal(afterSha, readResult.Result.GetProperty("sha256").GetString());
            Assert.Contains("Value", readResult.Result.GetProperty("text").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            index.Dispose();
            confirmation.Dispose();
            await bricsCad.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
            if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ReplaceVersionConflictReturnsStructuredAuthoritativeReadRecovery()
    {
        var root = Path.Combine(Path.GetTempPath(), "GO", "replace-version-recovery", Guid.NewGuid().ToString("N"));
        var cacheRoot = root + "-cache";
        Directory.CreateDirectory(root);
        const string current = "alpha\nbeta\n";
        await File.WriteAllTextAsync(
            Path.Combine(root, "notes.txt"),
            current,
            new System.Text.UTF8Encoding(false));
        var actualSha = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(current))).ToLowerInvariant();
        var index = new WorkspaceRepositoryIndex(new GoInfrastructureOptions { DataDirectory = cacheRoot });
        var confirmation = new ToolConfirmationService(null!);
        var bricsCad = new BricsCadBridgeHost();
        try
        {
            var broker = new LocalToolBroker(null!, null!, confirmation, bricsCad, index, null!);
            var replace = Create(
                ClientToolNames.FileSystemReplaceText,
                ToolRiskClass.LocalMutation,
                new
                {
                    path = "notes.txt",
                    oldText = "beta",
                    newText = "gamma",
                    expectedSha256 = new string('a', 64),
                    replaceAll = false,
                },
                DateTimeOffset.UtcNow);

            var result = await broker.ExecuteAsync(replace, root);

            Assert.Equal("failed", result.Status);
            Assert.Equal("client.workspace_version_conflict", result.ErrorCode);
            Assert.Equal("stale_file_version", result.Result.GetProperty("reason").GetString());
            Assert.Equal(actualSha, result.Result.GetProperty("actualSha256").GetString());
            Assert.Equal("workspace.inspect", result.Result.GetProperty("requiredAgentTool").GetString());
            Assert.Equal("read", result.Result.GetProperty("requiredOperation").GetString());
            Assert.Equal(current, await File.ReadAllTextAsync(Path.Combine(root, "notes.txt")));
        }
        finally
        {
            index.Dispose();
            confirmation.Dispose();
            await bricsCad.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
            if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task MissingReplaceTextReturnsStructuredRecoveryInsteadOfGenericToolFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "GO", "replace-text-recovery", Guid.NewGuid().ToString("N"));
        var cacheRoot = root + "-cache";
        Directory.CreateDirectory(root);
        const string current = "alpha\nbeta\n";
        await File.WriteAllTextAsync(
            Path.Combine(root, "notes.txt"),
            current,
            new System.Text.UTF8Encoding(false));
        var actualSha = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(current))).ToLowerInvariant();
        var index = new WorkspaceRepositoryIndex(new GoInfrastructureOptions { DataDirectory = cacheRoot });
        var confirmation = new ToolConfirmationService(null!);
        var bricsCad = new BricsCadBridgeHost();
        try
        {
            var broker = new LocalToolBroker(null!, null!, confirmation, bricsCad, index, null!);
            var replace = Create(
                ClientToolNames.FileSystemReplaceText,
                ToolRiskClass.LocalMutation,
                new
                {
                    path = "notes.txt",
                    oldText = "nicht vorhanden",
                    newText = "gamma",
                    expectedSha256 = actualSha,
                    replaceAll = false,
                },
                DateTimeOffset.UtcNow);

            var result = await broker.ExecuteAsync(replace, root);

            Assert.Equal("failed", result.Status);
            Assert.Equal("client.replace_text_not_found", result.ErrorCode);
            Assert.Equal("old_text_not_found", result.Result.GetProperty("reason").GetString());
            Assert.Equal(actualSha, result.Result.GetProperty("actualSha256").GetString());
            Assert.True(result.Result.GetProperty("authoritativeReadRequired").GetBoolean());
            Assert.False(result.Result.TryGetProperty("text", out _));
            Assert.Equal(current, await File.ReadAllTextAsync(Path.Combine(root, "notes.txt")));
        }
        finally
        {
            index.Dispose();
            confirmation.Dispose();
            await bricsCad.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
            if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RepeatedVersionedReadReturnsEvidenceMetadataWithoutSourceText()
    {
        var root = Path.Combine(Path.GetTempPath(), "GO", "read-evidence-cache-test", Guid.NewGuid().ToString("N"));
        var cacheRoot = root + "-cache";
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(
            Path.Combine(root, "Program.cs"),
            "internal static class Program { public static int Value => 1; }\n",
            new System.Text.UTF8Encoding(false));
        var index = new WorkspaceRepositoryIndex(new GoInfrastructureOptions { DataDirectory = cacheRoot });
        var confirmation = new ToolConfirmationService(null!);
        var bricsCad = new BricsCadBridgeHost();
        try
        {
            var broker = new LocalToolBroker(null!, null!, confirmation, bricsCad, index, null!);
            var first = Create(
                ClientToolNames.FileSystemReadText,
                ToolRiskClass.ReadOnly,
                new { path = "Program.cs", startLine = 1, endLine = 20 },
                DateTimeOffset.UtcNow);
            var firstResult = await broker.ExecuteAsync(first, root);
            var evidenceId = firstResult.Result.GetProperty("evidenceId").GetString();

            var repeated = Create(
                ClientToolNames.FileSystemReadText,
                ToolRiskClass.ReadOnly,
                new { path = "Program.cs", startLine = 1, endLine = 20, knownEvidenceIds = new[] { evidenceId } },
                DateTimeOffset.UtcNow);
            var repeatedResult = await broker.ExecuteAsync(repeated, root);

            Assert.Equal("completed", repeatedResult.Status);
            Assert.Equal(evidenceId, repeatedResult.Result.GetProperty("evidenceId").GetString());
            Assert.True(repeatedResult.Result.GetProperty("contentReused").GetBoolean());
            Assert.True(repeatedResult.Result.GetProperty("textOmitted").GetBoolean());
            Assert.False(repeatedResult.Result.TryGetProperty("text", out _));
        }
        finally
        {
            index.Dispose();
            confirmation.Dispose();
            await bricsCad.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
            if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public void ReadOnlyFileProposalWithContractArgumentsIsAccepted()
    {
        var now = DateTimeOffset.UtcNow;
        var proposal = Create(
            ClientToolNames.FileSystemReadText,
            ToolRiskClass.ReadOnly,
            new { path = "planung/heizung.txt" },
            now);

        LocalToolBroker.ValidateProposal(proposal, now);
    }

    [Fact]
    public void ExactTextReplacementIsAcceptedAsWorkspaceMutation()
    {
        var now = DateTimeOffset.UtcNow;
        var proposal = Create(
            ClientToolNames.FileSystemReplaceText,
            ToolRiskClass.LocalMutation,
            new
            {
                path = "src/TwitchAI.App/ViewModels/ShellViewModel.cs",
                oldText = "public string Status",
                newText = "public string RuntimeStatus",
                expectedSha256 = new string('a', 64),
                replaceAll = false,
            },
            now);

        LocalToolBroker.ValidateProposal(proposal, now);
    }

    [Fact]
    public void EmptyContentHashCanRepresentAnAtomicallyMissingWriteTarget()
    {
        Assert.True(LocalToolBroker.ExpectedHashRepresentsMissingTarget(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            targetExists: false));
        Assert.False(LocalToolBroker.ExpectedHashRepresentsMissingTarget(
            new string('a', 64),
            targetExists: false));
        Assert.False(LocalToolBroker.ExpectedHashRepresentsMissingTarget(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            targetExists: true));
    }

    [Fact]
    public void ReplacementTextAdoptsCrLfFromAnExistingWinUiFile()
    {
        var existing = "<Grid>\r\n  <TextBlock />\r\n</Grid>\r\n";
        var modelText = "<Grid>\n  <TextBlock />\n</Grid>";

        var normalized = LocalToolBroker.NormalizeReplacementLineEndings(modelText, existing);

        Assert.Equal("<Grid>\r\n  <TextBlock />\r\n</Grid>", normalized);
    }

    [Fact]
    public void ReplacementTextPreservesLfFromAnExistingRepositoryFile()
    {
        var existing = "first\nsecond\n";
        var modelText = "first\r\nreplacement\r\n";

        var normalized = LocalToolBroker.NormalizeReplacementLineEndings(modelText, existing);

        Assert.Equal("first\nreplacement\n", normalized);
    }

    [Fact]
    public void XamlReplacementFindsOneElementDespiteDifferentAttributeWhitespace()
    {
        const string existing = "<Grid>\r\n  <Button Grid.Column=\"2\" VerticalAlignment=\"Center\" HorizontalAlignment=\"Right\" Content=\"Status\" />\r\n</Grid>\r\n";
        const string requested = "<Button\n    Grid.Column=\"2\"\n    VerticalAlignment=\"Center\"\n    HorizontalAlignment=\"Right\"\n    Content=\"Status\" />";

        var match = LocalToolBroker.FindUniqueWhitespaceTolerantMatch(existing, requested, out var occurrences);

        Assert.Equal(1, occurrences);
        Assert.Equal("<Button Grid.Column=\"2\" VerticalAlignment=\"Center\" HorizontalAlignment=\"Right\" Content=\"Status\" />", match);
    }

    [Fact]
    public void WhitespaceTolerantReplacementRejectsAmbiguousShortcuts()
    {
        const string existing = "<TextBlock Text=\"Status\" />\n<TextBlock   Text=\"Status\" />\n";
        const string requested = "<TextBlock Text=\"Status\" />";

        var match = LocalToolBroker.FindUniqueWhitespaceTolerantMatch(existing, requested, out var occurrences);

        Assert.Null(match);
        Assert.Equal(2, occurrences);
    }

    [Fact]
    public void NewlyIntroducedAttachedFlyoutIsRejectedBeforeWritingXaml()
    {
        const string original = "<Window><Grid><Button /></Grid></Window>";
        const string invalid = "<Window><Grid><Button /><FlyoutBase.AttachedFlyout><Flyout /></FlyoutBase.AttachedFlyout></Grid></Window>";

        var exception = Assert.Throws<InvalidDataException>(() =>
            LocalToolBroker.ValidateSourceMutation("MainWindow.xaml", original, invalid, isFullWrite: false));

        Assert.Contains("Button.Flyout", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalButtonFlyoutPassesFastXamlValidation()
    {
        const string original = "<Window><Grid><Button /></Grid></Window>";
        const string valid = "<Window><Grid><Button><Button.Flyout><Flyout /></Button.Flyout></Button></Grid></Window>";

        LocalToolBroker.ValidateSourceMutation("MainWindow.xaml", original, valid, isFullWrite: false);
    }

    [Fact]
    public void CoherentFullSourceRewriteIsAllowedInsideTheBoundWorkspace()
    {
        var original = string.Join('\n', Enumerable.Range(1, 60).Select(index => $"public string Property{index} => \"{index}\";"));
        var rewritten = original + "\npublic string RuntimeStatus => \"Ready\";\n";

        LocalToolBroker.ValidateSourceMutation("ShellViewModel.cs", original, rewritten, isFullWrite: true);
    }

    [Fact]
    public void JsonUnicodeEscapesCopiedFromToolOutputCanBeNormalized()
    {
        const string copied = @"value = \u0022Bereit\u0022; unit = \u0022m\u00B3/h\u0022;";

        var normalized = LocalToolBroker.DecodeCopiedJsonUnicodeEscapes(copied);

        Assert.Equal("value = \"Bereit\"; unit = \"m³/h\";", normalized);
    }

    [Fact]
    public void DoubleEscapedLineBreaksAndHtmlEntitiesCanBeNormalized()
    {
        const string copied = @"if ready:\n    return \u0022value -&gt; valid\u0022\n\nnext_step()";

        var normalized = LocalToolBroker.DecodeCopiedJsonTextEscapes(copied);

        Assert.Equal("if ready:\n    return \"value -> valid\"\n\nnext_step()", normalized);
    }

    [Fact]
    public void SingleFilePatchGetsGitHeaderAndTerminalNewline()
    {
        const string patch = "--- a/src/App.xaml\n+++ b/src/App.xaml\n@@ -1 +1 @@\n-<Grid />\n+<Grid Padding=\"8\" />";

        var normalized = LocalToolBroker.NormalizeSingleFilePatch(patch, "src/App.xaml");

        Assert.StartsWith("diff --git a/src/App.xaml b/src/App.xaml\n", normalized, StringComparison.Ordinal);
        Assert.EndsWith("\n", normalized, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tests/App.Tests/RuntimeTests.cs", "tmp/RuntimeTests.cs.disabled")]
    [InlineData("tests/App.Tests/RuntimeTests.cs", "src/App/RuntimeTests.cs")]
    public void TestFilesCannotBeMovedOutOfTheRegularTestTree(string source, string destination)
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            LocalToolBroker.ValidateVerificationAssetMove(source, destination));

        Assert.Contains("Testdateien", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TestFilesCanBeRenamedInsideTheRegularTestTree()
    {
        LocalToolBroker.ValidateVerificationAssetMove(
            "tests/App.Tests/OldRuntimeTests.cs",
            "tests/App.Tests/RuntimeDescriptionTests.cs");
    }

    [Fact]
    public void DotNetTestPresetUsesTheRequestedProjectTarget()
    {
        var arguments = LocalToolBroker.BuildDotNetPresetArguments(
            "test",
            "tests/App.Tests/App.Tests.csproj");

        Assert.Equal(
            ["test", "tests/App.Tests/App.Tests.csproj", "--nologo"],
            arguments);
    }

    [Fact]
    public void DotNetBuildPresetRemainsRepositoryWideWithoutATarget()
    {
        Assert.Equal(
            ["build", "--nologo"],
            LocalToolBroker.BuildDotNetPresetArguments("build", null));
    }

    [Fact]
    public void PythonUnittestFileUsesTheStandardLibraryRunner()
    {
        var path = Path.Combine(Path.GetTempPath(), $"go-unittest-{Guid.NewGuid():N}.py");
        try
        {
            File.WriteAllText(path, "import unittest\n\nclass SolverTests(unittest.TestCase):\n    pass\n");

            Assert.Equal(
                ["-m", "unittest", path],
                LocalToolBroker.BuildPythonTestArguments(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PythonPytestFileKeepsThePytestRunner()
    {
        var path = Path.Combine(Path.GetTempPath(), $"go-pytest-{Guid.NewGuid():N}.py");
        try
        {
            File.WriteAllText(path, "def test_solver():\n    assert 1 + 1 == 2\n");

            Assert.Equal(
                ["-m", "pytest", path],
                LocalToolBroker.BuildPythonTestArguments(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void BarePythonUnittestInvocationUsesWorkspaceDiscovery()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"go-unittest-discovery-{Guid.NewGuid():N}");
        var tests = Path.Combine(workspace, "tests");
        Directory.CreateDirectory(tests);
        File.WriteAllText(Path.Combine(tests, "test_solver.py"), "import unittest\n");
        try
        {
            var normalized = LocalToolBroker.NormalizePythonProcessRequest(
                "python",
                ["-m", "unittest", "-v"],
                workspace);

            Assert.Equal("python", normalized.Executable);
            Assert.Equal(["-m", "unittest", "discover", "-s", "tests", "-v"], normalized.Arguments);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void ExplicitPythonUnittestTargetIsNotRewritten()
    {
        Assert.False(LocalToolBroker.IsBareUnittestInvocation(
            ["-m", "unittest", "tests/test_solver.py", "-v"]));
        Assert.False(LocalToolBroker.IsBareUnittestInvocation(
            ["unittest", "-v"]));
        Assert.Equal(
            ["-3.11", "-m", "unittest", "discover", "-s", "tests", "-v"],
            LocalToolBroker.BuildPythonUnittestDiscoveryArguments(
                ["-3.11", "-m", "unittest", "-v"],
                "tests"));
    }

    [Fact]
    public void GitStatusCollapsesGeneratedTreesButKeepsSourceEntries()
    {
        var status = string.Join('\n',
        [
            "?? .lake/packages/Cli/",
            "?? .lake/packages/mathlib/Mathlib.lean",
            "A  .venv/Lib/site-packages/numpy/__init__.py",
            "A  .venv/Scripts/python.exe",
            "A  __pycache__/solver.cpython-311.pyc",
            "?? target/debug/app.exe",
            " M physics_solver.py",
        ]);

        var summarized = LocalToolBroker.SummarizeGitStatus(status);

        Assert.Contains(" M physics_solver.py", summarized, StringComparison.Ordinal);
        Assert.Contains(".lake", summarized, StringComparison.Ordinal);
        Assert.Contains("target", summarized, StringComparison.Ordinal);
        Assert.DoesNotContain("Cli", summarized, StringComparison.Ordinal);
        Assert.Contains("[2 Git-Status-Einträge unter '.venv' zusammengefasst]", summarized, StringComparison.Ordinal);
        Assert.Contains("[1 Git-Status-Einträge unter '__pycache__' zusammengefasst]", summarized, StringComparison.Ordinal);
        Assert.DoesNotContain("site-packages", summarized, StringComparison.Ordinal);
    }

    [Fact]
    public void UntrackedTextDiffUsesARealNewFilePatchWithoutLosingTheFinalLine()
    {
        var diff = LocalToolBroker.FormatUntrackedTextDiff(
            @"proofs\minkowski\proof.py",
            "from sympy import simplify\nassert simplify(1 - 1) == 0");

        Assert.Contains("diff --git a/proofs/minkowski/proof.py b/proofs/minkowski/proof.py", diff, StringComparison.Ordinal);
        Assert.Contains("+++ b/proofs/minkowski/proof.py", diff, StringComparison.Ordinal);
        Assert.Contains("+assert simplify(1 - 1) == 0", diff, StringComparison.Ordinal);
        Assert.Contains("\\ No newline at end of file", diff, StringComparison.Ordinal);
    }

    [Fact]
    public void GitDiffPresetExcludesGeneratedFrameworkTrees()
    {
        var arguments = LocalToolBroker.BuildGitDiffArguments(@"C:\Workspace", "HEAD");

        Assert.Contains(":(exclude).lake/**", arguments);
        Assert.Contains(":(exclude)**/.lake/**", arguments);
        Assert.Contains(":(exclude)node_modules/**", arguments);
        Assert.Contains(":(exclude)target/**", arguments);
    }

    [Fact]
    public void AdditionalPropertiesAndRiskDowngradesAreRejectedLocally()
    {
        var now = DateTimeOffset.UtcNow;
        var additional = Create(
            ClientToolNames.FileSystemReadText,
            ToolRiskClass.ReadOnly,
            new { path = "planung.txt", unexpected = true },
            now);
        var downgraded = Create(
            ClientToolNames.FileSystemProposeDelete,
            ToolRiskClass.ReadOnly,
            new { path = "planung.txt" },
            now);

        Assert.Throws<InvalidDataException>(() => LocalToolBroker.ValidateProposal(additional, now));
        Assert.Throws<InvalidDataException>(() => LocalToolBroker.ValidateProposal(downgraded, now));
    }

    [Fact]
    public void ExpiredAndUnversionedProcessProposalsAreRejectedLocally()
    {
        var now = DateTimeOffset.UtcNow;
        var expired = Create(
            ClientToolNames.FileSystemList,
            ToolRiskClass.ReadOnly,
            new { path = "." },
            now) with { ExpiresAt = now.AddSeconds(-1) };
        var freeShell = Create(
            ClientToolNames.ProcessRunPreset,
            ToolRiskClass.Process,
            new { preset = "powershell.freeShell" },
            now);

        Assert.Throws<InvalidDataException>(() => LocalToolBroker.ValidateProposal(expired, now));
        Assert.Throws<InvalidDataException>(() => LocalToolBroker.ValidateProposal(freeShell, now));
    }

    [Fact]
    public void BricsCadOperationsMustMatchTheTypedClientTool()
    {
        var now = DateTimeOffset.UtcNow;
        var valid = Create(
            ClientToolNames.BricsCadAction,
            ToolRiskClass.CadMutation,
            new { operation = "layers.create", arguments = new { name = "TGA" } },
            now);
        var invalid = Create(
            ClientToolNames.BricsCadMeasure,
            ToolRiskClass.ReadOnly,
            new { operation = "measurement.runAnything", arguments = new { } },
            now);

        LocalToolBroker.ValidateProposal(valid, now);
        Assert.Throws<InvalidOperationException>(() => LocalToolBroker.ValidateProposal(invalid, now));
    }

    private static ToolProposal Create(
        string name,
        ToolRiskClass risk,
        object arguments,
        DateTimeOffset now) => new(
            "proposal-" + Guid.NewGuid().ToString("N"),
            "run-" + Guid.NewGuid().ToString("N"),
            name,
            JsonSerializer.SerializeToElement(arguments, GoAiProtocol.CreateJsonOptions()),
            risk,
            "Lokalen Vertrag prüfen",
            now.AddMinutes(5));
}
