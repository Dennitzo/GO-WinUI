using GoAi.Contracts;
using GoWinUI.App.Services;
using GoWinUI.BricsCad.Protocol;
using System.Text.Json;

namespace GoWinUI.Tests;

public sealed class LocalToolBrokerValidationTests
{
    [Fact]
    public void ToolTextNormalizationRemovesOnlyIsolatedSurrogates()
    {
        var normalized = LocalToolBroker.NormalizeUnicodeScalarText(
            "Quelle \udc81 und Emoji \ud83d\ude80 bleiben lesbar");

        Assert.Equal("Quelle  und Emoji \ud83d\ude80 bleiben lesbar", normalized);
    }

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

    [Theory]
    [InlineData("fs.list")]
    [InlineData("fs.stat")]
    public void ReadOnlyWorkspaceInspectionNeedsOnlyThePathDeclaredByItsSchema(string toolName)
    {
        var now = DateTimeOffset.UtcNow;
        var proposal = Create(
            toolName,
            ToolRiskClass.ReadOnly,
            new { path = "." },
            now);

        LocalToolBroker.ValidateProposal(proposal, now);
    }

    private static readonly string[] VersionArguments = ["--version"];
    private static readonly string[] LeanMainArguments = ["Main.lean"];
    private static readonly string[] VoiceSearchTerms = ["voice", "speech", "SpeechRecognition"];
    private static readonly string[] AllFilesGlob = ["**/*"];
    private static readonly string[] CSharpFilesGlob = ["*.cs"];

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
    public void SearchAndArbitraryFileExtensionsAreAccepted()
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
            ClientToolNames.FileSystemReadText,
            ToolRiskClass.ReadOnly,
            new { path = "config/toolchain.customlang", startLine = 1, endLine = 500 },
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
    public void MissingReadPathSuggestionsComeOnlyFromExistingTextFiles()
    {
        WorkspacePathEntry Entry(string path, bool binary = false) => new(path, false, binary, 100);
        WorkspacePathEntry[] entries =
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
    public void MissingReadPathWithoutMatchingFilesDoesNotInventASuggestion()
    {
        WorkspacePathEntry[] entries =
        [
            new("src/App.cs", false, false, 100),
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
        var confirmation = new ToolConfirmationService(null!);
        var bricsCad = new BricsCadBridgeHost();
        try
        {
            var broker = new LocalToolBroker(
                connection: null!,
                settings: null!,
                confirmation,
                bricsCad,
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
            confirmation.Dispose();
            await bricsCad.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RepeatedReadsAlwaysReturnTheCurrentUnchangedSourceText()
    {
        var root = Path.Combine(Path.GetTempPath(), "GO", "live-read-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "Program.cs");
        const string firstText = "internal static class Program { }\n";
        const string secondText = "internal static class Program { public static int Value => 1; }\n";
        await File.WriteAllTextAsync(path, firstText, new System.Text.UTF8Encoding(false));
        var confirmation = new ToolConfirmationService(null!);
        var bricsCad = new BricsCadBridgeHost();
        try
        {
            var broker = new LocalToolBroker(null!, null!, confirmation, bricsCad, documents: null!);
            var proposal = Create(
                ClientToolNames.FileSystemReadText,
                ToolRiskClass.ReadOnly,
                new { path = "Program.cs" },
                DateTimeOffset.UtcNow);

            var first = await broker.ExecuteAsync(proposal, root);
            await File.WriteAllTextAsync(path, secondText, new System.Text.UTF8Encoding(false));
            var second = await broker.ExecuteAsync(proposal with { ProposalId = "proposal-" + Guid.NewGuid().ToString("N") }, root);

            Assert.Equal(firstText, first.Result.GetProperty("text").GetString());
            Assert.Equal(secondText, second.Result.GetProperty("text").GetString());
            Assert.True(second.Result.GetProperty("completeFile").GetBoolean());
            Assert.False(second.Result.TryGetProperty("sha256", out _));
            Assert.False(second.Result.TryGetProperty("evidenceId", out _));
        }
        finally
        {
            confirmation.Dispose();
            await bricsCad.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LargeUntargetedReadReturnsSearchInstructionWithoutSourceText()
    {
        var root = Path.Combine(Path.GetTempPath(), "GO", "targeted-read-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(
            Path.Combine(root, "Large.cs"),
            new string('x', 13_000),
            new System.Text.UTF8Encoding(false));
        var confirmation = new ToolConfirmationService(null!);
        var bricsCad = new BricsCadBridgeHost();
        try
        {
            var broker = new LocalToolBroker(null!, null!, confirmation, bricsCad, documents: null!);
            var proposal = Create(
                ClientToolNames.FileSystemReadText,
                ToolRiskClass.ReadOnly,
                new { path = "Large.cs" },
                DateTimeOffset.UtcNow);

            var result = await broker.ExecuteAsync(proposal, root);

            Assert.Equal("completed", result.Status);
            Assert.True(result.Result.GetProperty("requiresTargetedRead").GetBoolean());
            Assert.Equal("search_required", result.Result.GetProperty("state").GetString());
            Assert.Equal(ClientToolNames.FileSystemSearch, result.Result.GetProperty("recommendedTool").GetString());
            Assert.False(result.Result.TryGetProperty("text", out _));
        }
        finally
        {
            confirmation.Dispose();
            await bricsCad.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SearchWithoutMatchesExplicitlyReportsThatContentDoesNotExist()
    {
        var root = Path.Combine(Path.GetTempPath(), "GO", "empty-search-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "App.cs"), "internal sealed class App { }\n");
        var confirmation = new ToolConfirmationService(null!);
        var bricsCad = new BricsCadBridgeHost();
        try
        {
            var broker = new LocalToolBroker(null!, null!, confirmation, bricsCad, documents: null!);
            var proposal = Create(
                ClientToolNames.FileSystemSearch,
                ToolRiskClass.ReadOnly,
                new { path = ".", query = "MissingFunctionForTest", includeGlobs = CSharpFilesGlob },
                DateTimeOffset.UtcNow);

            var result = await broker.ExecuteAsync(proposal, root);

            Assert.Equal("completed", result.Status);
            Assert.False(result.Result.GetProperty("found").GetBoolean());
            Assert.Equal("not_present", result.Result.GetProperty("state").GetString());
            Assert.Empty(result.Result.GetProperty("matches").EnumerateArray());
            Assert.Contains(
                "MissingFunctionForTest",
                result.Result.GetProperty("missingQueries").EnumerateArray().Select(static item => item.GetString()));
            Assert.Contains("Wiederhole dieselbe Suche nicht", result.Result.GetProperty("message").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            confirmation.Dispose();
            await bricsCad.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SearchReadRangeAndReplaceUseOneExactCrLfSourceBlock()
    {
        var root = Path.Combine(Path.GetTempPath(), "GO", "search-replace-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "Service.cs");
        const string source = "namespace Demo;\r\n\r\npublic sealed class Service\r\n{\r\n    public string State => \"old\";\r\n}\r\n";
        await File.WriteAllTextAsync(path, source, new System.Text.UTF8Encoding(false));
        var confirmation = new ToolConfirmationService(null!);
        var bricsCad = new BricsCadBridgeHost();
        try
        {
            var broker = new LocalToolBroker(null!, null!, confirmation, bricsCad, documents: null!);
            var search = await broker.ExecuteAsync(Create(
                ClientToolNames.FileSystemSearch,
                ToolRiskClass.ReadOnly,
                new { path = ".", query = "public string State", includeGlobs = CSharpFilesGlob, contextLines = 1 },
                DateTimeOffset.UtcNow), root);
            var readRequest = Assert.Single(search.Result.GetProperty("matches").EnumerateArray())
                .GetProperty("readRequest");
            var read = await broker.ExecuteAsync(Create(
                ClientToolNames.FileSystemReadText,
                ToolRiskClass.ReadOnly,
                new
                {
                    path = readRequest.GetProperty("path").GetString(),
                    startLine = readRequest.GetProperty("startLine").GetInt32(),
                    endLine = readRequest.GetProperty("endLine").GetInt32(),
                    maximumCharacters = readRequest.GetProperty("maximumCharacters").GetInt32(),
                },
                DateTimeOffset.UtcNow), root);
            var exactBlock = read.Result.GetProperty("text").GetString()!;
            var replace = await broker.ExecuteAsync(Create(
                ClientToolNames.FileSystemReplaceText,
                ToolRiskClass.LocalMutation,
                new
                {
                    path = "Service.cs",
                    oldText = "public string State => \"old\";",
                    newText = "public string State => \"ready\";",
                    expectedContent = exactBlock,
                    expectedContentMode = "fragment",
                },
                DateTimeOffset.UtcNow), root);

            Assert.True(search.Result.GetProperty("found").GetBoolean());
            Assert.Contains("\r\n", exactBlock, StringComparison.Ordinal);
            Assert.Equal("completed", replace.Status);
            Assert.Contains("State => \"ready\"", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        }
        finally
        {
            confirmation.Dispose();
            await bricsCad.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExternalChangeAfterReadPreventsAStaleWrite()
    {
        var root = Path.Combine(Path.GetTempPath(), "GO", "stale-content-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "notes.txt");
        const string readText = "alpha\nbeta\n";
        const string externalText = "alpha\nextern geändert\n";
        await File.WriteAllTextAsync(path, readText, new System.Text.UTF8Encoding(false));
        var confirmation = new ToolConfirmationService(null!);
        var bricsCad = new BricsCadBridgeHost();
        try
        {
            var broker = new LocalToolBroker(null!, null!, confirmation, bricsCad, documents: null!);
            await File.WriteAllTextAsync(path, externalText, new System.Text.UTF8Encoding(false));
            var write = Create(
                ClientToolNames.FileSystemWriteText,
                ToolRiskClass.LocalMutation,
                new { path = "notes.txt", content = "replacement\n", expectedContent = readText },
                DateTimeOffset.UtcNow);

            var result = await broker.ExecuteAsync(write, root);

            Assert.Equal("failed", result.Status);
            Assert.Equal("client.workspace_content_conflict", result.ErrorCode);
            Assert.Equal(externalText, await File.ReadAllTextAsync(path));
        }
        finally
        {
            confirmation.Dispose();
            await bricsCad.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SuccessfulMutationRecordsADirectDiffWithoutGitBaseline()
    {
        var root = Path.Combine(Path.GetTempPath(), "GO", "direct-diff-test", Guid.NewGuid().ToString("N"));
        var state = Path.Combine(root, ".go-test-state");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "notes.txt");
        const string before = "alpha\nbeta\n";
        const string after = "alpha\ngamma\n";
        await File.WriteAllTextAsync(path, before, new System.Text.UTF8Encoding(false));
        var confirmation = new ToolConfirmationService(null!);
        var bricsCad = new BricsCadBridgeHost();
        using var diffs = new CodingDiffService(new GoWinUI.Infrastructure.GoInfrastructureOptions { DataDirectory = state });
        try
        {
            var runId = Guid.NewGuid();
            Assert.True(await diffs.BeginAsync(runId, root));
            var broker = new LocalToolBroker(null!, null!, confirmation, bricsCad, documents: null!, codingDiffs: diffs);
            var replace = Create(
                ClientToolNames.FileSystemReplaceText,
                ToolRiskClass.LocalMutation,
                new { path = "notes.txt", oldText = "beta", newText = "gamma", expectedContent = before },
                DateTimeOffset.UtcNow);

            var result = await broker.ExecuteAsync(
                replace,
                root,
                Guid.NewGuid(),
                Guid.NewGuid(),
                codingMode: true,
                codingRunId: runId);
            var snapshot = Assert.IsType<CodingDiffSnapshot>(await diffs.RefreshAsync(runId, root));

            Assert.Equal("completed", result.Status);
            Assert.Equal(after, await File.ReadAllTextAsync(path));
            Assert.Contains("-beta", snapshot.Diff, StringComparison.Ordinal);
            Assert.Contains("+gamma", snapshot.Diff, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(root, ".git")));
        }
        finally
        {
            confirmation.Dispose();
            await bricsCad.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReplaceTextRequiresOneExactOccurrenceAndNeverOverwritesAmbiguously()
    {
        var root = Path.Combine(Path.GetTempPath(), "GO", "ambiguous-replace-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "notes.txt");
        const string current = "beta\nalpha\nbeta\n";
        await File.WriteAllTextAsync(path, current, new System.Text.UTF8Encoding(false));
        var confirmation = new ToolConfirmationService(null!);
        var bricsCad = new BricsCadBridgeHost();
        try
        {
            var broker = new LocalToolBroker(null!, null!, confirmation, bricsCad, documents: null!);
            var replace = Create(
                ClientToolNames.FileSystemReplaceText,
                ToolRiskClass.LocalMutation,
                new
                {
                    path = "notes.txt",
                    oldText = "beta",
                    newText = "gamma",
                    expectedContent = current,
                },
                DateTimeOffset.UtcNow);

            var result = await broker.ExecuteAsync(replace, root);

            Assert.Equal("failed", result.Status);
            Assert.Equal("client.replace_text_ambiguous", result.ErrorCode);
            Assert.Equal(current, await File.ReadAllTextAsync(path));
        }
        finally
        {
            confirmation.Dispose();
            await bricsCad.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReplaceTextRejectsWhitespaceOrLineEndingApproximation()
    {
        var root = Path.Combine(Path.GetTempPath(), "GO", "exact-replace-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "View.xaml");
        const string current = "<Grid>\r\n  <Button Content=\"Status\" />\r\n</Grid>\r\n";
        await File.WriteAllTextAsync(path, current, new System.Text.UTF8Encoding(false));
        var confirmation = new ToolConfirmationService(null!);
        var bricsCad = new BricsCadBridgeHost();
        try
        {
            var broker = new LocalToolBroker(null!, null!, confirmation, bricsCad, documents: null!);
            var replace = Create(
                ClientToolNames.FileSystemReplaceText,
                ToolRiskClass.LocalMutation,
                new
                {
                    path = "View.xaml",
                    oldText = "<Grid>\n  <Button Content=\"Status\" />\n</Grid>\n",
                    newText = "<Grid>\n  <Button Content=\"Bereit\" />\n</Grid>\n",
                    expectedContent = current,
                },
                DateTimeOffset.UtcNow);

            var result = await broker.ExecuteAsync(replace, root);

            Assert.Equal("failed", result.Status);
            Assert.Equal("client.replace_text_not_found", result.ErrorCode);
            Assert.Equal(current, await File.ReadAllTextAsync(path));
        }
        finally
        {
            confirmation.Dispose();
            await bricsCad.DisposeAsync();
            Directory.Delete(root, recursive: true);
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
                expectedContent = "public string Status",
                expectedContentMode = "fragment",
            },
            now);

        LocalToolBroker.ValidateProposal(proposal, now);
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
    public async Task InvalidPythonIsRejectedBeforeANewFileIsWritten()
    {
        if (!LocalToolBroker.TryResolveSystemExecutable("python", out _))
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "GO", "python-syntax-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var confirmation = new ToolConfirmationService(null!);
        var bricsCad = new BricsCadBridgeHost();
        try
        {
            var broker = new LocalToolBroker(null!, null!, confirmation, bricsCad, documents: null!);
            var proposal = Create(
                ClientToolNames.FileSystemProposeCreate,
                ToolRiskClass.LocalMutation,
                new { path = "Physik.py", content = "class Wurf Bewegungen:\n    pass\n" },
                DateTimeOffset.UtcNow);

            var result = await broker.ExecuteAsync(proposal, root);

            Assert.Equal("failed", result.Status);
            Assert.Contains("syntaktisch ungültig", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(root, "Physik.py")));
        }
        finally
        {
            confirmation.Dispose();
            await bricsCad.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CreateProposalRejectsContentThatCannotFitInOneModelToolCall()
    {
        var now = DateTimeOffset.UtcNow;
        var oversized = Create(
            ClientToolNames.FileSystemProposeCreate,
            ToolRiskClass.LocalMutation,
            new { path = "Physik.py", content = new string('x', 12_001) },
            now);

        Assert.Throws<InvalidDataException>(() => LocalToolBroker.ValidateProposal(oversized, now));
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
