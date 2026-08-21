using GoAi.Contracts;
using GoWinUI.App.Services;
using System.Text.Json;

namespace GoWinUI.Tests;

public sealed class LocalToolBrokerValidationTests
{
    private static readonly string[] VersionArguments = ["--version"];
    private static readonly string[] LeanMainArguments = ["Main.lean"];
    private static readonly string[] VoiceSearchTerms = ["voice", "speech", "SpeechRecognition"];
    private static readonly string[] AllFilesGlob = ["**/*"];

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
    public void GitStatusCollapsesGeneratedTreesButKeepsSourceEntries()
    {
        var status = string.Join('\n',
        [
            "A  .venv/Lib/site-packages/numpy/__init__.py",
            "A  .venv/Scripts/python.exe",
            "A  __pycache__/solver.cpython-311.pyc",
            " M physics_solver.py",
        ]);

        var summarized = LocalToolBroker.SummarizeGitStatus(status);

        Assert.Contains(" M physics_solver.py", summarized, StringComparison.Ordinal);
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
