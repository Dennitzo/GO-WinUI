using GoAi.Contracts;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runs;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class CodingAgentTests
{
    private static readonly string[] CSharpGlobs = ["**/*.cs"];
    private static readonly string[] SearchTerms = ["SpeechRecognition", "speech", "voice"];
    private static readonly string[] DotNetTestArguments = ["test"];
    private static readonly string[] OtherDotNetTestArguments = ["test", "tests/Other.Tests/Other.Tests.csproj"];
    private static readonly string[] PythonPytestArguments = ["-m", "pytest", "tests", "-q"];

    [Theory]
    [InlineData(0, false)]
    [InlineData(5, false)]
    [InlineData(6, true)]
    [InlineData(7, false)]
    [InlineData(8, false)]
    [InlineData(9, true)]
    [InlineData(12, true)]
    public void MutationProgressGuidanceIsDeterministicAndPeriodic(int rounds, bool expected)
    {
        Assert.Equal(expected, RunProcessor.ShouldAddCodingMutationProgressGuidance(rounds));
    }

    [Fact]
    public void RepeatedReplaceFailuresBlockOnlyTheAffectedTarget()
    {
        using var arguments = JsonDocument.Parse("""
            {"path":"config/settings.json","oldText":"old","newText":"new"}
            """);
        var call = new LmToolCall(
            "replace-1",
            ClientToolNames.FileSystemReplaceText,
            arguments.RootElement.Clone());

        Assert.False(RunProcessor.ShouldBlockRepeatedReplaceText(
            call,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["config/settings.json"] = 1,
            }));
        Assert.True(RunProcessor.ShouldBlockRepeatedReplaceText(
            call,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["CONFIG/SETTINGS.JSON"] = 2,
                ["other.json"] = 9,
            }));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(7, true)]
    public void RepeatedTextMutationsRequireARealProcessCheck(int mutations, bool expected)
    {
        using var arguments = JsonDocument.Parse("""
            {"path":"results/cases.json","content":"{}"}
            """);
        var call = new LmToolCall(
            "write-1",
            ClientToolNames.FileSystemWriteText,
            arguments.RootElement.Clone());

        Assert.Equal(
            expected,
            RunProcessor.ShouldRequireProcessBeforeAnotherTextMutation(
                call,
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["RESULTS/CASES.JSON"] = mutations,
                }));
    }

    [Theory]
    [InlineData("ok = actual == expected and False", "test", true)]
    [InlineData("ok = actual == expected or True", "test", true)]
    [InlineData("if False:\n    raise AssertionError()", "test", true)]
    [InlineData("assert True", "test", true)]
    [InlineData("assert actual == expected", "test", false)]
    [InlineData("assert True", "inspect", false)]
    public void VacuousInlinePythonVerificationIsRejected(
        string code,
        string purpose,
        bool expected)
    {
        var call = new LmToolCall(
            "python-check",
            ClientToolNames.ProcessRun,
            JsonSerializer.SerializeToElement(new
            {
                executable = ".venv/Scripts/python.exe",
                arguments = new[] { "-c", code },
                purpose,
            }));

        Assert.Equal(expected, RunProcessor.IsVacuousVerificationCall(call));
    }

    [Theory]
    [InlineData("Analysiere das Projekt und erkl\u00E4re, wie die Sprachsteuerung implementiert wurde.", CodingRequestIntent.Analysis)]
    [InlineData("Erstelle eine komplexe Excel-Arbeitsmappe f\u00FCr die Luftmengenberechnung.", CodingRequestIntent.Mutation)]
    [InlineData("Behebe den Fehler und teste die Anwendung.", CodingRequestIntent.Mutation)]
    [InlineData("Code starten", CodingRequestIntent.Execution)]
    [InlineData("F\u00FChre die vorhandenen Tests aus.", CodingRequestIntent.Execution)]
    public void NaturalCodingPromptsRequireTheCorrespondingObservedWork(
        string prompt,
        CodingRequestIntent expected)
    {
        var request = new RunRequest(
            GoAiProtocol.Version,
            RunMode.Code,
            [new RunMessage("user", [new ContentPart("text", prompt)])]);

        Assert.Equal(expected, RunProcessor.ClassifyCodingRequest(request));
    }

    [Fact]
    public void CodingCannotClaimCompletionWithoutRealToolEvidence()
    {
        Assert.NotNull(RunProcessor.CodingCompletionBlocker(
            CodingRequestIntent.Mutation,
            successfulToolCount: 0,
            evidencePathCount: 0,
            mutatedPathCount: 0));
        Assert.NotNull(RunProcessor.CodingCompletionBlocker(
            CodingRequestIntent.Mutation,
            successfulToolCount: 2,
            evidencePathCount: 1,
            mutatedPathCount: 0));
        Assert.NotNull(RunProcessor.CodingCompletionBlocker(
            CodingRequestIntent.Analysis,
            successfulToolCount: 1,
            evidencePathCount: 0,
            mutatedPathCount: 0));
        Assert.Null(RunProcessor.CodingCompletionBlocker(
            CodingRequestIntent.Analysis,
            successfulToolCount: 2,
            evidencePathCount: 1,
            mutatedPathCount: 0));
        Assert.Null(RunProcessor.CodingCompletionBlocker(
            CodingRequestIntent.Execution,
            successfulToolCount: 1,
            evidencePathCount: 0,
            mutatedPathCount: 0));
        Assert.Null(RunProcessor.CodingCompletionBlocker(
            CodingRequestIntent.Mutation,
            successfulToolCount: 3,
            evidencePathCount: 1,
            mutatedPathCount: 1));
    }

    [Fact]
    public void PipeAndArraySearchesHaveTheSameSemanticFingerprint()
    {
        var pipe = JsonSerializer.SerializeToElement(new
        {
            path = ".",
            query = "voice|speech|SpeechRecognition",
            matchMode = "literal",
            includeGlobs = CSharpGlobs,
        });
        var array = JsonSerializer.SerializeToElement(new
        {
            path = ".",
            queries = SearchTerms,
            matchMode = "literal",
            includeGlobs = CSharpGlobs,
        });

        Assert.Equal(
            RunProcessor.CreateSearchFingerprint(pipe),
            RunProcessor.CreateSearchFingerprint(array));
    }

    [Fact]
    public void ContextPlannerKeepsLatestPromptAndToolIdentityBelowThe262KLimit()
    {
        var toolCall = new LmToolCall(
            "tool-read-1",
            ClientToolNames.FileSystemReadMany,
            JsonSerializer.SerializeToElement(new
            {
                items = Enumerable.Range(0, 2_000).Select(index => new { path = $"src/file-{index}.cs" }).ToArray(),
            }));
        var messages = new List<LmChatMessage>
        {
            new("system", "Coding-Regeln"),
            new("user", "FrÃ¼here Aufgabe"),
            new("assistant", new string('a', 900_000), [toolCall]),
            new("tool", new string('b', 300_000), ToolCallId: "tool-read-1"),
            new("user", "Analysiere die Sprachsteuerung und nenne konkrete Dateien."),
        };

        var plan = CodingContextPlanner.Prepare(messages, 262_144, 8_192);

        Assert.True(plan.WasCompacted);
        Assert.True(plan.EstimatedInputTokens <= plan.InputTokenBudget);
        Assert.Equal(
            "Analysiere die Sprachsteuerung und nenne konkrete Dateien.",
            plan.Messages.Last(message => message.Role == "user").Content);
        Assert.Contains(plan.Messages, message => message.ToolCallId == "tool-read-1");
        Assert.Contains(
            plan.Messages.SelectMany(message => message.ToolCalls ?? []),
            call => call.Id == "tool-read-1");
    }

    [Fact]
    public void CodingCatalogAdvertisesLanguageNeutralWorkspaceAndProcessTools()
    {
        var request = new RunRequest(
            GoAiProtocol.Version,
            RunMode.Code,
            [new RunMessage("user", [new ContentPart("text", "Arbeite im Repository")])],
            ClientCapabilities: ["filesystem", "code", "process"],
            AllowedServerTools: ["math.evaluate"]);

        var tools = new AgentToolCatalog().GetAvailableTools(request);

        Assert.Contains(tools, tool => tool.Name == ClientToolNames.WorkspaceMap);
        Assert.Contains(tools, tool => tool.Name == ClientToolNames.FileSystemFindFiles);
        Assert.Contains(tools, tool => tool.Name == ClientToolNames.FileSystemReadMany);
        Assert.Contains(tools, tool => tool.Name == ClientToolNames.FileSystemWriteText);
        Assert.Contains(tools, tool => tool.Name == ClientToolNames.FileSystemReplaceText);
        Assert.Contains(tools, tool => tool.Name == ClientToolNames.ProcessRun);
        Assert.DoesNotContain(tools, tool => tool.Name == "context.embed");
    }

    [Fact]
    public void ToolFingerprintDistinguishesCorrectedArguments()
    {
        var failed = new LmToolCall(
            "call-1",
            ClientToolNames.ProcessRunPreset,
            JsonSerializer.SerializeToElement(new { preset = "code.test", target = "missing/Test.cs" }));
        var corrected = failed with
        {
            Id = "call-2",
            Arguments = JsonSerializer.SerializeToElement(new { preset = "code.test", target = "tests/Project/Test.cs" }),
        };

        Assert.Equal(RunProcessor.CreateToolFingerprint(failed), RunProcessor.CreateToolFingerprint(failed with { Id = "other" }));
        Assert.NotEqual(RunProcessor.CreateToolFingerprint(failed), RunProcessor.CreateToolFingerprint(corrected));
    }

    [Theory]
    [InlineData(ClientToolNames.WorkspaceMap)]
    [InlineData(ClientToolNames.FileSystemList)]
    [InlineData(ClientToolNames.FileSystemStat)]
    [InlineData(ClientToolNames.FileSystemReadText)]
    [InlineData(ClientToolNames.FileSystemFindFiles)]
    [InlineData(ClientToolNames.FileSystemReadMany)]
    public void StableWorkspaceReadsCanBeDeduplicatedUntilTheNextMutation(string name)
    {
        Assert.True(RunProcessor.IsStableWorkspaceRead(name));
    }

    [Fact]
    public void OverlappingReadRangesAreMergedAndMostlyRepeatedRangesAreRejected()
    {
        var ranges = new List<WorkspaceReadRange>();
        RunProcessor.AddWorkspaceReadRange(ranges, new("src/mainwindow.xaml.cs", 60, 100));

        Assert.True(RunProcessor.IsRedundantWorkspaceRead(
            new("src/mainwindow.xaml.cs", 75, 90), ranges));
        Assert.False(RunProcessor.IsRedundantWorkspaceRead(
            new("src/mainwindow.xaml.cs", 95, 105), ranges));

        RunProcessor.AddWorkspaceReadRange(ranges, new("src/mainwindow.xaml.cs", 95, 120));

        var merged = Assert.Single(ranges);
        Assert.Equal(60, merged.StartLine);
        Assert.Equal(120, merged.EndLine);
        Assert.True(RunProcessor.IsRedundantWorkspaceRead(
            new("src/mainwindow.xaml.cs", 108, 125), ranges));
        Assert.False(RunProcessor.IsRedundantWorkspaceRead(
            new("src/mainwindow.xaml.cs", 121, 145), ranges));
    }

    [Fact]
    public void ReadTextCallCreatesCanonicalPersistentRange()
    {
        var call = new LmToolCall(
            "read-1",
            ClientToolNames.FileSystemReadText,
            JsonSerializer.SerializeToElement(new
            {
                path = @"SRC\MainWindow.xaml.cs",
                startLine = 10,
                endLine = 30,
            }));

        Assert.True(RunProcessor.TryGetWorkspaceReadRange(call, out var range));
        Assert.Equal("src/mainwindow.xaml.cs", range.Path);
        Assert.Equal(10, range.StartLine);
        Assert.Equal(30, range.EndLine);
    }

    [Theory]
    [InlineData(ClientToolNames.FileSystemWriteText)]
    [InlineData(ClientToolNames.FileSystemReplaceText)]
    [InlineData(ClientToolNames.ProcessRun)]
    public void MutationsAndProcessesAreNeverClassifiedAsStableReads(string name)
    {
        Assert.False(RunProcessor.IsStableWorkspaceRead(name));
    }

    [Fact]
    public void CodingPolicyAdaptsToArbitraryRepositoriesWithoutFrameworkSpecificInstructions()
    {
        Assert.Contains("persistente Coding-Agent", GoAi.Server.Core.Policies.TgaAgentPolicies.CodeSpecialist, StringComparison.Ordinal);
        Assert.Contains("native strukturierte Tool-Calls", GoAi.Server.Core.Policies.TgaAgentPolicies.CodeSpecialist, StringComparison.Ordinal);
        Assert.Contains("nicht-denkenden Modus", GoAi.Server.Core.Policies.TgaAgentPolicies.CodeSpecialist, StringComparison.Ordinal);
        Assert.Contains("Unterstelle weder .NET", GoAi.Server.Core.Policies.TgaAgentPolicies.CodeSpecialist, StringComparison.Ordinal);
        Assert.Contains("Technologie- und Architekturadaption", GoAi.Server.Core.Policies.TgaAgentPolicies.CodeSpecialist, StringComparison.Ordinal);
        Assert.Contains("purpose test, build und start", GoAi.Server.Core.Policies.TgaAgentPolicies.CodeSpecialist, StringComparison.Ordinal);
        Assert.Contains("git.diff", GoAi.Server.Core.Policies.TgaAgentPolicies.CodeSpecialist, StringComparison.Ordinal);
        Assert.Contains("niemals selbstständig `git add`", GoAi.Server.Core.Policies.TgaAgentPolicies.CodeSpecialist, StringComparison.Ordinal);
        Assert.Contains("Deaktiviere", GoAi.Server.Core.Policies.TgaAgentPolicies.CodeSpecialist, StringComparison.Ordinal);
        Assert.Contains("Eine bereits seit der letzten Mutation", GoAi.Server.Core.Policies.TgaAgentPolicies.CodeSpecialist, StringComparison.Ordinal);
        Assert.Contains("widersprüchlicher Konsolenausgabe", GoAi.Server.Core.Policies.TgaAgentPolicies.CodeSpecialist, StringComparison.Ordinal);
        Assert.Contains("Führe den echten Renderer aus", GoAi.Server.Core.Policies.TgaAgentPolicies.CodeSpecialist, StringComparison.Ordinal);
        Assert.Contains("`repository.build` ausschließlich", GoAi.Server.Core.Policies.TgaAgentPolicies.CodeSpecialist, StringComparison.Ordinal);
        Assert.Contains("`py_compile` oder `compileall`", GoAi.Server.Core.Policies.TgaAgentPolicies.CodeSpecialist, StringComparison.Ordinal);
        Assert.Contains("Behandle einen neu geschriebenen Test, Checker oder Validator nicht automatisch als fachliche Autorität", GoAi.Server.Core.Policies.TgaAgentPolicies.CodeSpecialist, StringComparison.Ordinal);
        Assert.Contains("Ändere Produktdaten niemals nur, damit eine zu enge", GoAi.Server.Core.Policies.TgaAgentPolicies.CodeSpecialist, StringComparison.Ordinal);
        Assert.Contains("Ein Prüforakel muss vom geprüften Produktcode unabhängig sein", GoAi.Server.Core.Policies.TgaAgentPolicies.CodeSpecialist, StringComparison.Ordinal);
        Assert.Contains("Statusfeld ist selbst kein Nachweis", GoAi.Server.Core.Policies.TgaAgentPolicies.CodeSpecialist, StringComparison.Ordinal);
        Assert.Contains("Numerische Verifikation muss geschlossen fehlschlagen", GoAi.Server.Core.Policies.TgaAgentPolicies.CodeSpecialist, StringComparison.Ordinal);
        Assert.Contains("niemals in ein Nullresiduum", GoAi.Server.Core.Policies.TgaAgentPolicies.CodeSpecialist, StringComparison.Ordinal);
        Assert.DoesNotContain("Button.Flyout", GoAi.Server.Core.Policies.TgaAgentPolicies.CodeSpecialist, StringComparison.Ordinal);
        Assert.DoesNotContain("GO-WinUI", GoAi.Server.Core.Policies.TgaAgentPolicies.CodeSpecialist, StringComparison.Ordinal);
        Assert.DoesNotContain("Build-Portable.ps1", GoAi.Server.Core.Policies.TgaAgentPolicies.CodeSpecialist, StringComparison.Ordinal);
    }

    [Fact]
    public void StaleReplaceGuidanceRequiresSemanticReevaluationBeforeRetry()
    {
        var guidance = RunProcessor.CreateReplaceFailureGuidance(
            "Die Zieldatei wurde zwischenzeitlich geändert; fs.replaceText wurde nicht ausgeführt.");

        Assert.Contains("veralteten Dateirevision", guidance, StringComparison.Ordinal);
        Assert.Contains("fachlich erforderlich", guidance, StringComparison.Ordinal);
        Assert.Contains("neu geschriebenen Checker", guidance, StringComparison.Ordinal);
        Assert.Contains("erneuten Lesen", guidance, StringComparison.Ordinal);
    }

    [Fact]
    public void CodingRuntimeGuidanceSourceContainsNoMojibakeMarkers()
    {
        var source = File.ReadAllText(FindRepositoryFile("src/GoAi.Server.Core/Runs/RunProcessor.cs"));

        Assert.DoesNotContain("Ã", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Â", source, StringComparison.Ordinal);
        Assert.DoesNotContain("â€", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Repositorydatei nicht gefunden: {relativePath}");
    }

    [Theory]
    [InlineData("npm", "test")]
    [InlineData("cargo", "test")]
    [InlineData("go", "test")]
    [InlineData("python", "unittest")]
    public void TestStageRecognitionSupportsMultipleToolchains(string executable, string command)
    {
        Assert.True(RunProcessor.IsTestCommand(executable, [command]));
    }

    [Theory]
    [InlineData("npm", "build")]
    [InlineData("cargo", "check")]
    [InlineData("go", "build")]
    [InlineData("python", "compileall")]
    [InlineData("python", "py_compile")]
    [InlineData("python", "generate_report.py")]
    [InlineData("gradlew", "assemble")]
    public void BuildStageRecognitionSupportsMultipleToolchains(string executable, string command)
    {
        Assert.True(RunProcessor.IsBuildCommand(executable, [command]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("--check")]
    [InlineData("--stat")]
    public void DirectGitDiffSatisfiesTheReviewStage(string option)
    {
        var arguments = option.Length == 0 ? new[] { "diff" } : new[] { "diff", option };
        var call = new LmToolCall(
            "review-process",
            ClientToolNames.ProcessRun,
            JsonSerializer.SerializeToElement(new
            {
                executable = "git",
                arguments,
                purpose = "inspect",
            }));

        Assert.Equal(["review"], RunProcessor.VerificationStagesForCall(call));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("unknown")]
    public void NonVerifyingLeanOperationsDoNotCompleteCodingStages(string operation)
    {
        var call = new LmToolCall(
            "lean-stage",
            ClientToolNames.LeanProof,
            JsonSerializer.SerializeToElement(new { operation }));

        Assert.Empty(RunProcessor.VerificationStagesForCall(call));
    }

    [Theory]
    [InlineData("check", "build")]
    [InlineData("build", "build")]
    [InlineData("axioms", "test")]
    public void LeanOperationsCompleteOnlyTheirDeclaredCodingStage(string operation, string expectedStage)
    {
        var call = new LmToolCall(
            "lean-stage",
            ClientToolNames.LeanProof,
            JsonSerializer.SerializeToElement(new { operation }));

        Assert.Equal([expectedStage], RunProcessor.VerificationStagesForCall(call));
    }

    [Fact]
    public void LeanVerifyCompletesTheFormalProofStages()
    {
        var call = new LmToolCall(
            "lean-verify",
            ClientToolNames.LeanProof,
            JsonSerializer.SerializeToElement(new { operation = "verify" }));

        Assert.Equal(["test", "build", "start"], RunProcessor.VerificationStagesForCall(call));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void LeanToolResultRequiresInnerPassedFlag(bool passed, bool expected)
    {
        var call = new LmToolCall(
            "lean-result",
            ClientToolNames.LeanProof,
            JsonSerializer.SerializeToElement(new { operation = "verify" }));
        var result = new ClientToolResult(
            "proposal",
            "completed",
            JsonSerializer.SerializeToElement(new { passed }));

        Assert.Equal(expected, RunProcessor.IsSuccessfulClientToolResult(call, result));
    }

    [Fact]
    public void OrdinaryCompletedClientToolResultRemainsSuccessful()
    {
        var call = new LmToolCall(
            "read-result",
            ClientToolNames.FileSystemReadText,
            JsonSerializer.SerializeToElement(new { path = "README.md" }));
        var result = new ClientToolResult(
            "proposal",
            "completed",
            JsonSerializer.SerializeToElement(new { text = "ok" }));

        Assert.True(RunProcessor.IsSuccessfulClientToolResult(call, result));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(129, false)]
    public void ProcessToolResultRequiresZeroExitCode(int exitCode, bool expected)
    {
        var call = new LmToolCall(
            "process-result",
            ClientToolNames.ProcessRunPreset,
            JsonSerializer.SerializeToElement(new { preset = "git.diff" }));
        var result = new ClientToolResult(
            "proposal",
            "completed",
            JsonSerializer.SerializeToElement(new { exitCode }));

        Assert.Equal(expected, RunProcessor.IsSuccessfulClientToolResult(call, result));
    }

    [Fact]
    public void PythonTestsAlsoSatisfyTheInterpretedProjectBuildValidation()
    {
        var call = new LmToolCall(
            "python-tests",
            ClientToolNames.ProcessRun,
            JsonSerializer.SerializeToElement(new
            {
                executable = ".venv/Scripts/python.exe",
                arguments = PythonPytestArguments,
                purpose = "test",
            }));

        Assert.Equal(["test", "build"], RunProcessor.VerificationStagesForCall(call));
    }

    [Theory]
    [InlineData("einstein_engine.py", "--list")]
    [InlineData("visualize_einstein.py", "--all")]
    public void ExecutedPythonEntryPointSatisfiesRuntimeSmokeEvenWhenModelMislabelsPurpose(
        string script,
        string argument)
    {
        Assert.True(RunProcessor.IsPythonRuntimeSmokeCommand("python.exe", [script, argument]));
        var call = new LmToolCall(
            "python-smoke",
            ClientToolNames.ProcessRun,
            JsonSerializer.SerializeToElement(new
            {
                executable = ".venv/Scripts/python.exe",
                arguments = new[] { script, argument },
                purpose = "build",
            }));

        Assert.Equal(["start"], RunProcessor.VerificationStagesForCall(call));
    }

    [Theory]
    [InlineData("simulation_data/live_progress.json")]
    [InlineData("visualizations/live_progress.svg")]
    [InlineData("artifacts/report.json")]
    [InlineData("coverage/index.html")]
    public void GeneratedRuntimeArtifactsDoNotInvalidateCompletedCodeVerification(string path)
    {
        Assert.False(RunProcessor.RequiresCodingVerification(path));
    }

    [Theory]
    [InlineData("einstein_engine.py")]
    [InlineData("test_einstein_engine.py")]
    [InlineData("einstein_cases.json")]
    [InlineData("src/App.xaml.cs")]
    public void SourceTestAndConfigurationChangesStillRequireVerification(string path)
    {
        Assert.True(RunProcessor.RequiresCodingVerification(path));
    }

    [Theory]
    [InlineData("Erledigt")]
    [InlineData("GO_SESSION_TITLE: Fertig")]
    [InlineData("GO_SESSION_TITLE: Fertig\n\nDie Anzeige wurde angepasst. Tests sind erfolgreich.")]
    [InlineData("GO_SESSION_TITLE: Fertig\n\nLass mich das korrigieren:\n<tool_call><function=fs_readText></function></tool_call>")]
    public void CodingFinalRejectsMissingProcessReportsEmptyMessagesAndPseudoTools(string response)
    {
        Assert.False(RunProcessor.IsValidCodingFinalResponse(response));
    }

    [Fact]
    public void CodingFinalAcceptsAConcreteProcessReportWithoutTechnicalTitle()
    {
        Assert.True(RunProcessor.IsValidCodingFinalResponse(
            """
            ### Prozessbericht
            **Gegenstand:** Laufzeitanzeige des Clients.
            **Aktion:** Die Anzeige wurde angepasst.
            **Annahmen:** Die bestehende UI-Struktur bleibt erhalten.
            **Annahmenänderung:** Unverändert.
            **Prüfung:** Tests, Build und Smoke-Start sind erfolgreich.
            """));
    }

    [Theory]
    [InlineData(1, true, null, false)]
    [InlineData(2, false, null, false)]
    [InlineData(2, true, "Dateiänderung fehlt", false)]
    [InlineData(2, true, null, true)]
    [InlineData(5, true, null, true)]
    public void RepeatedRedundantVerificationForcesADeterministicFinalResponse(
        int redundantCalls,
        bool verificationComplete,
        string? blocker,
        bool expected)
    {
        Assert.Equal(
            expected,
            RunProcessor.ShouldForceCodingFinalizationAfterRedundantVerification(
                redundantCalls,
                verificationComplete,
                blocker));
    }

    [Fact]
    public void VerifiedCodingFallbackIsAValidConcreteFinalResponse()
    {
        var response = RunProcessor.CreateVerifiedCodingFallbackResponse(
            ["src/report.py", "tests/test_report.py"],
            ["test", "build", "start", "review"]);

        Assert.True(RunProcessor.IsValidCodingFinalResponse(response));
        Assert.Contains("### Prozessbericht", response, StringComparison.Ordinal);
        Assert.Contains("**Annahmenänderung:**", response, StringComparison.Ordinal);
        Assert.Contains("`src/report.py`", response, StringComparison.Ordinal);
        Assert.Contains("Build/Validierung", response, StringComparison.Ordinal);
        Assert.Contains("Laufzeit-Smoke", response, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifiedUntitledModelSummaryIsPreservedWithAValidSessionTitle()
    {
        const string summary = "Die Berechnung wurde erweitert. Tests, Build, Smoke und Diff sind erfolgreich.";

        var response = RunProcessor.CreateVerifiedCodingFinalResponse(
            summary,
            ["src/solver.py"],
            ["test", "build", "start", "review"]);

        Assert.True(RunProcessor.IsValidCodingFinalResponse(response));
        Assert.Contains("### Prozessbericht", response, StringComparison.Ordinal);
        Assert.Contains(summary, response, StringComparison.Ordinal);
    }

    [Fact]
    public void PseudoToolMarkupUsesTheEvidenceBasedVerifiedFallback()
    {
        var response = RunProcessor.CreateVerifiedCodingFinalResponse(
            "<tool_call><function=fs_readText></function></tool_call>",
            ["src/solver.py"],
            ["test", "build", "start", "review"]);

        Assert.True(RunProcessor.IsValidCodingFinalResponse(response));
        Assert.DoesNotContain("<tool_call", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("`src/solver.py`", response, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dotnet.test", "test")]
    [InlineData("repository.build", "build")]
    [InlineData("repository.start", "start")]
    [InlineData("git.diff", "review")]
    public void CompletedVerificationPresetIsRecognizedAsRedundant(string preset, string stage)
    {
        var call = new LmToolCall(
            "verify-1",
            ClientToolNames.ProcessRunPreset,
            JsonSerializer.SerializeToElement(new { preset }));

        Assert.True(RunProcessor.IsRedundantVerificationCall(
            call,
            new HashSet<string>([stage], StringComparer.Ordinal)));
    }

    [Fact]
    public void IntegratedVerificationRequiresAllThreeCoreStagesBeforeItIsRedundant()
    {
        var call = new LmToolCall(
            "verify-all",
            ClientToolNames.ProcessRunPreset,
            JsonSerializer.SerializeToElement(new { preset = "repository.verify" }));

        Assert.False(RunProcessor.IsRedundantVerificationCall(
            call,
            new HashSet<string>(["test", "build"], StringComparer.Ordinal)));
        Assert.True(RunProcessor.IsRedundantVerificationCall(
            call,
            new HashSet<string>(["test", "build", "start"], StringComparer.Ordinal)));
    }

    [Fact]
    public void DirectProcessVerificationRequiresAnExactSuccessfulCommandForDeduplication()
    {
        var call = new LmToolCall(
            "verify-process",
            ClientToolNames.ProcessRun,
            JsonSerializer.SerializeToElement(new
            {
                executable = "dotnet",
                arguments = DotNetTestArguments,
                purpose = "test",
            }));

        var completedStages = new HashSet<string>(["test"], StringComparer.Ordinal);
        Assert.False(RunProcessor.IsRedundantVerificationCall(
            call,
            completedStages,
            new HashSet<string>(StringComparer.Ordinal)));

        var successfulCalls = new HashSet<string>(
            [RunProcessor.CreateToolFingerprint(call)],
            StringComparer.Ordinal);
        Assert.True(RunProcessor.IsRedundantVerificationCall(call, completedStages, successfulCalls));

        var differentCommand = call with
        {
            Id = "verify-process-other",
            Arguments = JsonSerializer.SerializeToElement(new
            {
                executable = "dotnet",
                arguments = OtherDotNetTestArguments,
                purpose = "test",
            }),
        };
        Assert.False(RunProcessor.IsRedundantVerificationCall(differentCommand, completedStages, successfulCalls));
    }

    [Fact]
    public void WorkspaceMutationInvalidatesEarlierProcessAndReadFingerprintsButKeepsUnsafeMutationRetriesBlocked()
    {
        static LmToolCall ToolCall(string name, string arguments) =>
            new(Guid.NewGuid().ToString("N"), name, JsonSerializer.Deserialize<JsonElement>(arguments));

        var successful = new HashSet<string>(StringComparer.Ordinal)
        {
            RunProcessor.CreateToolFingerprint(ToolCall(
                ClientToolNames.ProcessRun,
                """{"executable":"py","arguments":["-m","pytest"],"workingDirectory":".","purpose":"test"}""")),
        };
        var failedProcess = RunProcessor.CreateToolFingerprint(ToolCall(
            ClientToolNames.ProcessRun,
            """{"executable":"py","arguments":["-m","pytest"],"workingDirectory":".","purpose":"test"}"""));
        var failedRead = RunProcessor.CreateToolFingerprint(ToolCall(
            ClientToolNames.FileSystemReadText,
            """{"path":"generated.py"}"""));
        var failedMutation = RunProcessor.CreateToolFingerprint(ToolCall(
            ClientToolNames.FileSystemReplaceText,
            """{"path":"app.py","oldText":"old","newText":"new","expectedSha256":"stale"}"""));
        var failed = new HashSet<string>(StringComparer.Ordinal)
        {
            failedProcess,
            failedRead,
            failedMutation,
        };

        RunProcessor.InvalidateToolFingerprintsAfterMutation(successful, failed);

        Assert.Empty(successful);
        Assert.DoesNotContain(failedProcess, failed);
        Assert.DoesNotContain(failedRead, failed);
        Assert.Contains(failedMutation, failed);
    }

    [Theory]
    [InlineData("restore")]
    [InlineData("clean")]
    public void RestoreAndCleanDoNotSatisfyTheBuildStage(string command)
    {
        var call = new LmToolCall(
            "maintenance-process",
            ClientToolNames.ProcessRun,
            JsonSerializer.SerializeToElement(new
            {
                executable = "dotnet",
                arguments = new[] { command, "." },
                purpose = "build",
            }));

        Assert.Empty(RunProcessor.VerificationStagesForCall(call));
        Assert.False(RunProcessor.IsRedundantVerificationCall(
            call,
            new HashSet<string>(["build"], StringComparer.Ordinal)));
    }

    [Fact]
    public void DirectStartCountsOnlyAsAnObservedSmokeStart()
    {
        var waiting = new LmToolCall(
            "start-wait",
            ClientToolNames.ProcessRun,
            JsonSerializer.SerializeToElement(new
            {
                executable = "artifacts/App.exe",
                arguments = Array.Empty<string>(),
                purpose = "start",
                startMode = "wait",
            }));
        var smoke = waiting with
        {
            Id = "start-smoke",
            Arguments = JsonSerializer.SerializeToElement(new
            {
                executable = "artifacts/App.exe",
                arguments = Array.Empty<string>(),
                purpose = "start",
                startMode = "smoke",
            }),
        };

        Assert.Empty(RunProcessor.VerificationStagesForCall(waiting));
        Assert.Equal(["start"], RunProcessor.VerificationStagesForCall(smoke));
    }

    [Theory]
    [InlineData(35, false)]
    [InlineData(36, true)]
    [InlineData(47, true)]
    public void IntegratedVerificationReservesTheLastTwelveCodingRounds(int roundCount, bool expected)
    {
        Assert.Equal(
            expected,
            RunProcessor.ShouldForceIntegratedCodingVerification(
                roundCount,
                maximumModelRounds: 48,
                verificationRequired: true,
                verificationFailed: false,
                coreVerificationComplete: false,
                hasIntegratedVerifier: true));
    }

    [Fact]
    public void IntegratedVerificationIsNotForcedAfterFailureOrCompletion()
    {
        Assert.False(RunProcessor.ShouldForceIntegratedCodingVerification(
            47, 48, true, verificationFailed: true, coreVerificationComplete: false, hasIntegratedVerifier: true));
        Assert.False(RunProcessor.ShouldForceIntegratedCodingVerification(
            47, 48, true, verificationFailed: false, coreVerificationComplete: true, hasIntegratedVerifier: true));
        Assert.False(RunProcessor.ShouldForceIntegratedCodingVerification(
            47, 48, verificationRequired: false, verificationFailed: false, coreVerificationComplete: false, hasIntegratedVerifier: true));
        Assert.False(RunProcessor.ShouldForceIntegratedCodingVerification(
            47, 48, true, verificationFailed: false, coreVerificationComplete: false, hasIntegratedVerifier: false));
    }
}
