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
    private static readonly string[] CheckerArguments = ["proofs/check.py"];
    private static readonly string[] FastCheckerArguments = ["proofs/check_fast.py"];

    [Fact]
    public void ReasoningOnlyResponseAtTheOutputLimitIsDetected()
    {
        var exhausted = new LmChatResult(
            Content: null,
            ToolCalls: [],
            InputTokens: 59_000,
            OutputTokens: 8_191,
            HadReasoning: true,
            ReasoningTokens: 8_191);
        var useful = exhausted with
        {
            ToolCalls =
            [
                new LmToolCall(
                    "call-1",
                    ClientToolNames.FileSystemReadText,
                    JsonSerializer.SerializeToElement(new { path = "README.md" })),
            ],
        };

        Assert.True(RunProcessor.IsReasoningBudgetExhausted(exhausted, 8_192));
        Assert.False(RunProcessor.IsReasoningBudgetExhausted(useful, 8_192));
    }

    [Fact]
    public void ReasoningOnlyResponseAccountsForLlamaCppTokenAccountingOverhead()
    {
        var exhausted = new LmChatResult(
            Content: null,
            ToolCalls: [],
            InputTokens: 16_141,
            OutputTokens: 8_143,
            HadReasoning: true,
            ReasoningTokens: 8_143);

        Assert.True(RunProcessor.IsEmptyModelResponse(exhausted));
        Assert.True(RunProcessor.IsReasoningBudgetExhausted(exhausted, 8_192));
    }

    [Fact]
    public void RequiredToolCallWithPartialContentAtOutputLimitIsRecoverable()
    {
        var exhausted = new LmChatResult(
            Content: "{\"operation\":\"verify\"",
            ToolCalls: [],
            InputTokens: 16_141,
            OutputTokens: 8_192,
            HadReasoning: true,
            ReasoningTokens: 8_192);
        var completed = exhausted with
        {
            ToolCalls =
            [
                new LmToolCall(
                    "call-lean",
                    ClientToolNames.LeanProof,
                    JsonSerializer.SerializeToElement(new
                    {
                        operation = "verify",
                        path = "proofs/example.lean",
                    })),
            ],
        };

        Assert.False(RunProcessor.IsEmptyModelResponse(exhausted));
        Assert.True(RunProcessor.IsRequiredToolCallOutputBudgetExhausted(exhausted, 8_192));
        Assert.True(RunProcessor.IsRequiredToolCallOutputBudgetExhausted(
            exhausted with { HadReasoning = false, ReasoningTokens = 0 },
            8_192));
        Assert.False(RunProcessor.IsRequiredToolCallOutputBudgetExhausted(completed, 8_192));
    }

    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    public void GptOssReasoningSelectionRemainsInvariant(string selectedEffort)
    {
        Assert.Equal(
            selectedEffort,
            RunProcessor.ResolveReasoningEffortForRound(
                "openai/gpt-oss-120b",
                "code",
                selectedEffort));
    }

    [Fact]
    public void SelectedToolTurnsReceiveAnIndependentOutputBudget()
    {
        Assert.Equal(
            8_192,
            RunProcessor.ResolveModelTurnMaximumOutputTokens(
                selectedToolName: null,
                configuredMaximumOutputTokens: 8_192,
                contextLength: 131_072,
                requireToolCall: false));
        Assert.Equal(
            4_096,
            RunProcessor.ResolveModelTurnMaximumOutputTokens(
                selectedToolName: null,
                configuredMaximumOutputTokens: 8_192,
                contextLength: 131_072,
                requireToolCall: true));
        Assert.Equal(
            8_192,
            RunProcessor.ResolveModelTurnMaximumOutputTokens(
                ClientToolNames.FileSystemReadText,
                configuredMaximumOutputTokens: 8_192,
                contextLength: 131_072,
                requireToolCall: false));
        Assert.Equal(
            8_192,
            RunProcessor.ResolveModelTurnMaximumOutputTokens(
                ClientToolNames.FileSystemReadText,
                configuredMaximumOutputTokens: 8_192,
                contextLength: 131_072,
                requireToolCall: true));
        Assert.Equal(
            32_768,
            RunProcessor.ResolveModelTurnMaximumOutputTokens(
                ClientToolNames.FileSystemWriteText,
                configuredMaximumOutputTokens: 8_192,
                contextLength: 131_072,
                requireToolCall: false));
        Assert.Equal(
            32_768,
            RunProcessor.ResolveModelTurnMaximumOutputTokens(
                ClientToolNames.FileSystemWriteText,
                configuredMaximumOutputTokens: 8_192,
                contextLength: 131_072,
                requireToolCall: true));
    }

    [Fact]
    public void SelectedToolBudgetRemainsInsideACompactContextWindow()
    {
        Assert.Equal(
            30_720,
            RunProcessor.ResolveModelTurnMaximumOutputTokens(
                ClientToolNames.FileSystemWriteText,
                configuredMaximumOutputTokens: 8_192,
                contextLength: 32_768,
                requireToolCall: true));
    }

    [Fact]
    public void RequiredToolCallRecoveryAlsoRejectsSpeculativeText()
    {
        var textOnly = new LmChatResult("Ich bin fertig.", [], 100, 50);
        var toolCall = new LmChatResult(
            null,
            [new LmToolCall(
                "call-read",
                ClientToolNames.FileSystemReadText,
                JsonSerializer.SerializeToElement(new { path = "README.md" }))],
            100,
            50);

        Assert.True(RunProcessor.IsMissingRequiredToolCall(requireToolCall: true, textOnly));
        Assert.False(RunProcessor.IsMissingRequiredToolCall(requireToolCall: false, textOnly));
        Assert.False(RunProcessor.IsMissingRequiredToolCall(requireToolCall: true, toolCall));
    }

    [Fact]
    public void ToolCallHistoryDropsSpeculativeAssistantText()
    {
        var call = new LmToolCall(
            "call-read",
            ClientToolNames.FileSystemReadText,
            JsonSerializer.SerializeToElement(new { path = "chapters/classical_mechanics.md" }));

        var history = RunProcessor.CreateToolCallHistoryMessage([call]);

        Assert.Null(history.Content);
        Assert.Single(history.ToolCalls!);
        Assert.Equal(call, history.ToolCalls![0]);
    }

    [Fact]
    public void MissingWorkspacePathFailureIsRecognizedForAgentRecovery()
    {
        var failure = new ClientToolResult(
            "proposal-1",
            "failed",
            JsonSerializer.SerializeToElement(new { failed = true }),
            "client.tool_failed",
            "Der angeforderte Workspace-Pfad wurde nicht gefunden.");

        Assert.True(RunProcessor.IsMissingWorkspacePathFailure(failure));
        Assert.False(RunProcessor.IsMissingWorkspacePathFailure(failure with { Status = "completed" }));
        Assert.True(RunProcessor.IsMissingWorkspacePathFailure(failure with
        {
            ErrorCode = "client.workspace_path_not_found",
            Message = "Path unavailable.",
        }));
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
    public void WorkspaceDescriptorCannotHideTheUsersMutationIntent()
    {
        var request = new RunRequest(
            GoAiProtocol.Version,
            RunMode.Code,
            [new RunMessage(
                "user",
                [
                    new ContentPart("text", "Behebe die Ursachen in Code und Tests."),
                    new ContentPart(
                        "text",
                        "[GO_WORKSPACE]\nDer gebundene Workspace ist aktiv. Verwende relative Pfade ab '.'."),
                ])]);

        Assert.Equal(CodingRequestIntent.Mutation, RunProcessor.ClassifyCodingRequest(request));
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
        Assert.Null(RunProcessor.CodingCompletionBlocker(
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
        // PDF generation is a deterministic GO post-processing step and no
        // longer a completion gate for the model-facing coding loop.
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
            new("user", "Frühere Aufgabe"),
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
    public void ContextPlannerRemovesRepeatedCampaignPromptsAndTransientWorkflowNoise()
    {
        var messages = new List<LmChatMessage>
        {
            new("system", "Coding-Regeln"),
            new("user", "Erweitere das Lehrbuch autonom."),
            new("assistant", "### Prozessbericht\n\nErster Fortschritt"),
            new("assistant", "**Workflow-Schritt verifiziert**\n\nVersuch 1"),
            new("user", "  Erweitere   das Lehrbuch autonom.  "),
            new("assistant", "### Prozessbericht\n\nZweiter Fortschritt"),
        };

        var plan = CodingContextPlanner.Prepare(messages, 262_144, 8_192);

        Assert.True(plan.WasCompacted);
        Assert.Single(plan.Messages, message => message.Role == "user");
        Assert.DoesNotContain(
            plan.Messages,
            message => message.Content?.Contains("Workflow-Schritt verifiziert", StringComparison.Ordinal) == true);
        Assert.Contains(plan.Messages, message => message.Content?.Contains("Erster Fortschritt", StringComparison.Ordinal) == true);
        Assert.Contains(plan.Messages, message => message.Content?.Contains("Zweiter Fortschritt", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void CodingRunCompactionKeepsRequestAnchorAndReplacesToolTurnsWithMemory()
    {
        var call = new LmToolCall(
            "tool-1",
            ClientToolNames.FileSystemReadText,
            JsonSerializer.SerializeToElement(new { path = "src/App.xaml.cs" }));
        var messages = new List<LmChatMessage>
        {
            new("system", "Coding-Regeln"),
            new("system", "Repositorykarte folgt"),
            new("user", "src/App.xaml.cs\nsrc/MainWindow.xaml"),
            new("user", "Behebe den UI-Fehler."),
            new("assistant", ToolCalls: [call]),
            new("tool", "Dateiinhalt", ToolCallId: "tool-1"),
            new("assistant", "Zwischenergebnis"),
        };

        var compacted = RunProcessor.CreateCompactedCodingMessages(messages, "src/App.xaml.cs wurde gelesen.");

        Assert.Equal(5, compacted.Count);
        Assert.Equal(messages.Take(4), compacted.Take(4));
        Assert.Equal("system", compacted[^1].Role);
        Assert.Contains("GO_CODING_RUN_MEMORY", compacted[^1].Content, StringComparison.Ordinal);
        Assert.Contains("src/App.xaml.cs wurde gelesen.", compacted[^1].Content, StringComparison.Ordinal);
        Assert.DoesNotContain(compacted, message => string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            compacted.SelectMany(message => message.ToolCalls ?? []),
            toolCall => toolCall.Id == "tool-1");
    }

    [Theory]
    [InlineData(true, false, false, false, "Workspace-Evidenz fehlt", true)]
    [InlineData(true, false, true, false, null, true)]
    [InlineData(true, false, true, true, null, false)]
    [InlineData(true, true, true, false, "Verifikation fehlt", false)]
    [InlineData(false, false, true, false, "Verifikation fehlt", false)]
    public void CodingToolRequirementEndsOnlyForSynthesisOrCompletedWork(
        bool codingRun,
        bool finalSynthesisRequested,
        bool verificationRequired,
        bool verificationComplete,
        string? completionBlocker,
        bool expected)
    {
        Assert.Equal(
            expected,
            RunProcessor.ShouldRequireCodingToolCall(
                codingRun,
                finalSynthesisRequested,
                verificationRequired,
                verificationComplete,
                completionBlocker));
    }

    [Fact]
    public void LibraryVerificationDoesNotRequireAStartableApplication()
    {
        Assert.True(RunProcessor.CoreCodingVerificationComplete(
            new HashSet<string>(["test", "build"], StringComparer.Ordinal)));
        Assert.True(RunProcessor.CoreCodingVerificationComplete(
            new HashSet<string>(["test", "build", "start"], StringComparer.Ordinal)));
        Assert.False(RunProcessor.CoreCodingVerificationComplete(
            new HashSet<string>(["test"], StringComparer.Ordinal)));
        Assert.False(RunProcessor.CoreCodingVerificationComplete(
            new HashSet<string>(["build", "start"], StringComparer.Ordinal)));
    }

    [Fact]
    public void ContextPreparationUsesTheCodingModelWithoutActivatingTheCodingAgentLoop()
    {
        Assert.True(RunProcessor.IsCodingAgentRun("code", ConversationProfile.General));
        Assert.False(RunProcessor.IsCodingAgentRun("code", ConversationProfile.ContextPreparation));

        var request = new RunRequest(
            GoAiProtocol.Version,
            RunMode.Code,
            [new RunMessage("user", [new ContentPart("text", "Verdichte diese Historie.")])],
            ClientCapabilities: [],
            AllowedServerTools: [],
            PreferredCodeModelId: GoAi.Server.Core.Configuration.CodingModelCatalog.GptOss120BId,
            ConversationProfile: ConversationProfile.ContextPreparation);
        var policy = GoAi.Server.Core.Policies.TgaAgentPolicies.ForConversation("code", request, []);

        Assert.Contains("verdichtest ausschließlich", policy, StringComparison.Ordinal);
        Assert.Contains("verwende keine Werkzeuge", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("### Prozessbericht", policy, StringComparison.Ordinal);
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
        Assert.Contains(tools, tool => tool.Name == ClientToolNames.FileSystemReadText);
        Assert.DoesNotContain(tools, tool => tool.Name == ClientToolNames.FileSystemReadMany);
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

    [Fact]
    public void ProcessFingerprintCannotBeBypassedByRelabellingTheSameCommandPurpose()
    {
        var failedTest = new LmToolCall(
            "call-1",
            ClientToolNames.ProcessRun,
            JsonSerializer.SerializeToElement(new
            {
                executable = "py",
                arguments = CheckerArguments,
                workingDirectory = ".",
                purpose = "test",
                startMode = "wait",
            }));
        var relabelledAsStart = failedTest with
        {
            Id = "call-2",
            Arguments = JsonSerializer.SerializeToElement(new
            {
                purpose = "start",
                startMode = "wait",
                workingDirectory = ".",
                arguments = CheckerArguments,
                executable = "PY.EXE",
            }),
        };
        var correctedCommand = failedTest with
        {
            Id = "call-3",
            Arguments = JsonSerializer.SerializeToElement(new
            {
                executable = "py",
                arguments = FastCheckerArguments,
                workingDirectory = ".",
                purpose = "test",
                startMode = "wait",
            }),
        };

        Assert.Equal(
            RunProcessor.CreateToolFingerprint(failedTest),
            RunProcessor.CreateToolFingerprint(relabelledAsStart));
        Assert.NotEqual(
            RunProcessor.CreateToolFingerprint(failedTest),
            RunProcessor.CreateToolFingerprint(correctedCommand));
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
    public void CodingPolicyIsCompactGeneralAndContainsNoDomainSpecificRules()
    {
        var policy = GoAi.Server.Core.Policies.TgaAgentPolicies.CodeSpecialist;

        Assert.InRange(policy.Length, 500, 4_000);
        Assert.Contains("autonome Coding-Agent", policy, StringComparison.Ordinal);
        Assert.Contains("workspace.map", policy, StringComparison.Ordinal);
        Assert.Contains("zunächst nur ihre Namen", policy, StringComparison.Ordinal);
        Assert.Contains("Tests und Build-/Validierungsschritte", policy, StringComparison.Ordinal);
        Assert.Contains("belegter externer Blocker", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("TGA", policy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mathematische", policy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("physikalische", policy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ricci", policy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Tensor", policy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OOXML", policy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("KaTeX", policy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SpreadsheetPromptDoesNotInjectArtifactSpecificRulesBeforeToolSelection()
    {
        var request = CreateCodingPolicyRequest(
            "Erstelle eine visuell aufbereitete Excel-Arbeitsmappe mit Formeln und Diagrammen.");

        var policy = GoAi.Server.Core.Policies.TgaAgentPolicies.ForConversation("code", request, []);

        Assert.Contains("autonome Coding-Agent", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("Tabellen- und Excel-Artefakte", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("OOXML-Formeln", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("Arbeitsblaetter", policy, StringComparison.Ordinal);
    }

    [Fact]
    public void ScientificPublicationPromptReceivesNoAutomaticDomainPolicy()
    {
        var request = CreateCodingPolicyRequest(
            "Erstelle fortlaufend ein PDF-Lehrbuch zur Mathematik und Physik mit Lean-Beweisen und numerischen Simulationen.");

        var policy = GoAi.Server.Core.Policies.TgaAgentPolicies.ForConversation("code", request, []);

        Assert.Contains("autonome Coding-Agent", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("Mathematische und physikalische Arbeit", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("analytische Cross-Checks", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("Differentialgeometrie", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("Buch-, Bericht- und PDF-Ausgabe", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("proof.lean", policy, StringComparison.Ordinal);
    }

    [Fact]
    public void GermanCodingPromptRequiresGermanReadableArtifactsWithoutTranslatingCodeSyntax()
    {
        var request = CreateCodingPolicyRequest(
            "Erstelle ein Lehrbuchkapitel zur klassischen Mechanik mit Markdown, Lean und KaTeX.");

        var policy = GoAi.Server.Core.Policies.TgaAgentPolicies.ForConversation("code", request, []);

        Assert.Contains("Sprache des aktuellen Prompts", policy, StringComparison.Ordinal);
        Assert.Contains("Code, Bezeichner, APIs und Formate", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("Markdown- und TeX-Dokumente", policy, StringComparison.Ordinal);
    }

    [Fact]
    public void RelativityPromptReceivesNoHardCodedRelativityInstructions()
    {
        var request = CreateCodingPolicyRequest(
            "Untersuche eine Raumzeitmetrik und validiere Ricci- und Einstein-Tensor mathematisch.");

        var policy = GoAi.Server.Core.Policies.TgaAgentPolicies.ForConversation("code", request, []);

        Assert.Contains("autonome Coding-Agent", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("Differentialgeometrie", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("Ricci", policy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Einstein-Tensor", policy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WebResearchEventsExposeOnlyTheAuditableQueryOrUrlAsTarget()
    {
        using var search = JsonDocument.Parse("""{"query":"official WinUI documentation","maximumResults":5}""");
        using var fetch = JsonDocument.Parse("""{"url":"https://learn.microsoft.com/windows/apps/"}""");

        Assert.Equal(
            "official WinUI documentation",
            RunProcessor.CreateServerToolTarget("web.search", search.RootElement));
        Assert.Equal(
            "https://learn.microsoft.com/windows/apps/",
            RunProcessor.CreateServerToolTarget("web.fetch", fetch.RootElement));
        Assert.Null(RunProcessor.CreateServerToolTarget("math.evaluate", search.RootElement));
    }

    [Fact]
    public void GeneralWebResearchPolicyDescribesTheIsolatedSdkStages()
    {
        var request = new RunRequest(
            GoAiProtocol.Version,
            RunMode.General,
            [new RunMessage("user", [new ContentPart("text", "Suche aktuelle Dokumentation")])],
            AllowedServerTools: ["web.search", "web.fetch"]);

        var policy = GoAi.Server.Core.Policies.TgaAgentPolicies.ForConversation(
            "general",
            request,
            ["web.search", "web.fetch"]);

        Assert.Contains("Gestufte Webrecherche", policy, StringComparison.Ordinal);
        Assert.Contains("hierarchische Evidenzverdichtung", policy, StringComparison.Ordinal);
        Assert.Contains("web.fetch", policy, StringComparison.Ordinal);
        Assert.Contains("Titel sowie URL", policy, StringComparison.Ordinal);
        Assert.Contains("statt der Web-Werkzeugschemas", policy, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdinaryGeneralChatDoesNotReceiveMandatoryWebResearchPolicy()
    {
        var request = new RunRequest(
            GoAiProtocol.Version,
            RunMode.Auto,
            [new RunMessage("user", [new ContentPart("text", "Erkläre den Begriff Volumenstrom")])],
            AllowedServerTools: ["math.evaluate"]);

        var policy = GoAi.Server.Core.Policies.TgaAgentPolicies.ForConversation(
            "general",
            request,
            ["math.evaluate"]);

        Assert.DoesNotContain("Gestufte Webrecherche", policy, StringComparison.Ordinal);
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

    [Fact]
    public void InitialCodingMessagesPlaceWorkspaceMapBeforeTheUserPrompt()
    {
        var request = CreateCodingPolicyRequest("Bearbeite src/App.cs.") with
        {
            Workspace = new WorkspaceDescriptor(
                "workspace",
                "fingerprint",
                "revision",
                "[GO_REPOSITORY_MAP_V1]\nDateien: 1 (Text: 1)\n- src/App.cs | C# | 120\n",
                1,
                0,
                120,
                DateTimeOffset.UtcNow),
        };

        var messages = RunProcessor.CreateInitialMessages(request, "code", ["fs.readText"]);

        Assert.Equal(3, messages.Count);
        Assert.Equal("system", messages[1].Role);
        Assert.Contains("tool: workspace.map", messages[1].Content, StringComparison.Ordinal);
        Assert.Contains("workspaceEmpty: false", messages[1].Content, StringComparison.Ordinal);
        Assert.Contains("src/App.cs", messages[1].Content, StringComparison.Ordinal);
        Assert.Equal("user", messages[2].Role);
        Assert.Equal("Bearbeite src/App.cs.", messages[2].Content);
    }

    [Fact]
    public void InitialWorkspaceMapExplicitlyMarksAnEmptyDirectory()
    {
        var workspace = new WorkspaceDescriptor(
            "empty",
            "fingerprint",
            "revision",
            "[GO_REPOSITORY_MAP_V1]\nDateien: 0 (Text: 0)\n",
            0,
            0,
            0,
            DateTimeOffset.UtcNow);

        var context = RunProcessor.CreateInitialWorkspaceMapContext(workspace);

        Assert.Contains("tool: workspace.map", context, StringComparison.Ordinal);
        Assert.Contains("workspaceEmpty: true", context, StringComparison.Ordinal);
    }

    private static RunRequest CreateCodingPolicyRequest(string prompt) => new(
        GoAiProtocol.Version,
        RunMode.Code,
        [new RunMessage("user", [new ContentPart("text", prompt)])],
        ClientCapabilities: ["filesystem", "code", "process"],
        Workspace: new WorkspaceDescriptor(
            "workspace",
            "fingerprint",
            "revision",
            "README.md\nsrc/\ntests/",
            3,
            1,
            128,
            DateTimeOffset.UtcNow));

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
    [InlineData("README.md")]
    [InlineData("book.pdf")]
    [InlineData("solutions/formal-proof.tex")]
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
    [InlineData(ClientToolNames.FileSystemProposePatch, 0, false)]
    [InlineData(ClientToolNames.FileSystemProposePatch, 1, false)]
    [InlineData(ClientToolNames.FileSystemProposePatch, 2, true)]
    [InlineData(ClientToolNames.FileSystemWriteText, 2, false)]
    public void RepeatedMalformedPatchesSuppressOnlyThePatchTool(
        string toolName,
        int failedPatchAttemptCount,
        bool expected)
    {
        Assert.Equal(
            expected,
            RunProcessor.IsToolSuppressedAfterRepeatedFailure(toolName, failedPatchAttemptCount));
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

    [Theory]
    [InlineData(true, 96, 96, true)]
    [InlineData(true, 9_600, 96, true)]
    [InlineData(false, 95, 96, true)]
    [InlineData(false, 96, 96, false)]
    public void CodingLoopContinuesPastPerCycleRoundBudgetUntilStoppedOrCompleted(
        bool codingRun,
        int roundCount,
        int maximumModelRounds,
        bool expected)
    {
        Assert.Equal(expected, RunProcessor.ShouldContinueAgentLoop(codingRun, roundCount, maximumModelRounds));
    }

    [Theory]
    [InlineData("curl", "-s", "https://example.com", true)]
    [InlineData("wget.exe", "https://example.com", "", true)]
    [InlineData("web", "", "", true)]
    [InlineData("pwsh", "-Command", "Invoke-RestMethod https://example.com", true)]
    [InlineData("powershell.exe", "-Command", "dotnet test", false)]
    [InlineData("dotnet", "test", "", false)]
    public void StagedWebResearchBlocksOnlyDirectProcessBasedHttpBypasses(
        string executable,
        string firstArgument,
        string secondArgument,
        bool expected)
    {
        var arguments = string.IsNullOrEmpty(secondArgument)
            ? new[] { firstArgument }
            : new[] { firstArgument, secondArgument };
        var call = new LmToolCall(
            "process-call",
            ClientToolNames.ProcessRun,
            JsonSerializer.SerializeToElement(new
            {
                executable,
                arguments,
                purpose = "inspect",
            }));

        Assert.Equal(expected, RunProcessor.IsStagedWebResearchProcessBypass(call));
    }
}
