using GoAi.Contracts;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Policies;
using GoAi.Server.Core.Runs;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class CodingAgentOrchestratorTests
{
    [Theory]
    [InlineData("Analysiere die Ursache des Fehlers.", CodingTaskKind.Analysis)]
    [InlineData("Behebe den Fehler in der Ansicht.", CodingTaskKind.Change)]
    [InlineData("Erstelle ein neues WinUI-Projekt.", CodingTaskKind.Creation)]
    [InlineData("Führe die vorhandenen Tests aus.", CodingTaskKind.Execution)]
    [InlineData("Führe eine Websuche zur API durch.", CodingTaskKind.Research)]
    [InlineData("Fix the failing parser and run its tests.", CodingTaskKind.Change)]
    public void TaskClassificationUsesTheCurrentGoalInsteadOfDomainRules(
        string goal,
        CodingTaskKind expected)
    {
        Assert.Equal(expected, CodingAgentOrchestrator.ClassifyTask(goal));
    }

    [Fact]
    public void ModelLedgerViewIsBoundedWithoutMutatingTheAuthoritativeLedger()
    {
        var evidence = Enumerable.Range(0, 60)
            .Select(index => new EvidenceRef($"evidence-{index:D2}", "file", $"Evidence {index}"))
            .ToArray();
        var changes = Enumerable.Range(0, 50)
            .Select(index => new AgentChangeRecord($"src/File{index:D2}.cs", null, $"sha-{index}", $"action-{index}", "changed"))
            .ToArray();
        var verifications = Enumerable.Range(0, 50)
            .Select(index => new AgentVerificationRecord($"verify-{index}", "test", ".", true, $"action-{index}", "passed"))
            .ToArray();
        var failures = Enumerable.Range(0, 30).Select(index => $"failure-{index}").ToArray();
        var ledger = new TaskLedgerSnapshot(
            "goal",
            CodingTaskKind.Change,
            CodingAgentPhase.Editing,
            "revision-1",
            ["workspace bounded"],
            evidence,
            changes,
            verifications,
            failures,
            "continue");

        var modelView = CodingAgentOrchestrator.CreateModelLedgerView(ledger);

        Assert.Equal(32, modelView.Evidence.Count);
        Assert.Equal("evidence-00", modelView.Evidence[0].EvidenceId);
        Assert.Equal("evidence-59", modelView.Evidence[^1].EvidenceId);
        Assert.Equal("src/File18.cs", modelView.Changes[0].Path);
        Assert.Equal(32, modelView.Changes.Count);
        Assert.Equal("verify-18", modelView.Verifications[0].VerificationId);
        Assert.Equal(32, modelView.Verifications.Count);
        Assert.Equal("failure-14", modelView.Failures[0]);
        Assert.Equal(16, modelView.Failures.Count);
        Assert.Equal(60, ledger.Evidence.Count);
        Assert.Equal(50, ledger.Changes.Count);
    }

    [Fact]
    public void PromptRelevantStartEvidenceRemainsAvailableUntilTheFirstMutation()
    {
        var unchanged = EmptyLedger(CodingTaskKind.Change);
        var changed = unchanged with
        {
            Changes = [new AgentChangeRecord("README.md", "before", "after", "action-1", "changed")],
        };

        Assert.True(CodingAgentOrchestrator.ShouldIncludeStableRepositoryContext(unchanged));
        Assert.False(CodingAgentOrchestrator.ShouldIncludeStableRepositoryContext(changed));
        Assert.Equal("belegter Inhalt", CodingAgentOrchestrator.BoundStableRepositoryContext("belegter Inhalt"));
        Assert.Contains(
            "Startkontext gekürzt",
            CodingAgentOrchestrator.BoundStableRepositoryContext(new string('x', 30_000)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatedModelTurnsRetainThePromptRelevantStartEvidence()
    {
        using var arguments = JsonDocument.Parse("""{"operation":"map"}""");
        var request = Request() with
        {
            Workspace = new WorkspaceDescriptor(
                "workspace",
                "fingerprint",
                "revision-1",
                "[EVIDENCE evidence-1 | README.md | sha256=abc]\nbelegter Dateiinhalt",
                1,
                1,
                24,
                DateTimeOffset.UtcNow),
        };
        var action = new AgentActionEnvelope(
            "action-1",
            1,
            CodingAgentToolFacade.WorkspaceInspect,
            "map",
            arguments.RootElement.Clone(),
            "idempotency-1",
            "revision-1",
            DateTimeOffset.UtcNow);

        var messages = CodingAgentOrchestrator.CreateModelMessages(
            request,
            EmptyLedger(CodingTaskKind.Change),
            [action],
            [],
            null,
            null,
            CodingCommentaryDirective.None);

        Assert.Contains("belegter Dateiinhalt", messages[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void StatelessModelReadsNeverSuppressSourceTextFromOldEvidenceIds()
    {
        using var arguments = JsonDocument.Parse("""{"path":"README.md"}""");
        var dispatch = new CodingToolDispatch(
            CodingAgentToolFacade.WorkspaceInspect,
            "read",
            ClientToolNames.FileSystemReadText,
            arguments.RootElement.Clone(),
            null,
            false,
            null);
        var changeReceipt = new EvidenceRef(
            "evidence-new-version",
            "change",
            "write successful",
            "README.md",
            "new-sha",
            "revision-2");

        var withoutHint = CodingAgentOrchestrator.AddKnownEvidenceHints(
            dispatch,
            [changeReceipt]);
        Assert.False(withoutHint.TryGetProperty("knownEvidenceIds", out _));

        var withReadEvidence = CodingAgentOrchestrator.AddKnownEvidenceHints(
            dispatch,
            [changeReceipt, changeReceipt with { Kind = "workspace" }]);
        Assert.False(withReadEvidence.TryGetProperty("knownEvidenceIds", out _));
    }

    [Fact]
    public void CurrentReadObservationOverridesPersistedCompactionInTheNextModelTurn()
    {
        using var actionArguments = JsonDocument.Parse("""{"operation":"read","path":"README.md"}""");
        var action = new AgentActionEnvelope(
            "action-read",
            1,
            CodingAgentToolFacade.WorkspaceInspect,
            "read",
            actionArguments.RootElement.Clone(),
            "idempotency-read",
            "revision-1",
            DateTimeOffset.UtcNow);
        var fullResult = JsonSerializer.SerializeToElement(new
        {
            evidenceId = "evidence-known-from-map",
            path = "README.md",
            sha256 = new string('a', 64),
            text = "Dieser Quelltext muss trotz Client-Cache im Modellturn sichtbar sein.",
            cacheHit = true,
        });
        var fullObservation = new AgentObservation(
            action.ActionId,
            true,
            fullResult,
            "result-hash",
            null,
            null,
            "evidence-known-from-map",
            "revision-1",
            true,
            DateTimeOffset.UtcNow);
        var persistedObservation = fullObservation with
        {
            Result = CodingObservationCompactor.CompactResult(action, fullObservation),
        };

        var messages = CodingAgentOrchestrator.CreateModelMessages(
            Request(),
            EmptyLedger(CodingTaskKind.Change) with
            {
                Evidence = [new EvidenceRef("evidence-known-from-map", "workspace", "Indexmetadaten")],
            },
            [action],
            [persistedObservation],
            fullObservation,
            null,
            CodingCommentaryDirective.None);

        Assert.Contains(
            "Dieser Quelltext muss trotz Client-Cache im Modellturn sichtbar sein.",
            messages[1].Content,
            StringComparison.Ordinal);
    }

    [Fact]
    public void V2PolicyRemainsDomainNeutralAndEvidenceDriven()
    {
        Assert.DoesNotMatch(@"(?i)\bTGA\b", TgaAgentPolicies.CodeSpecialistV2);
        Assert.DoesNotContain("Physik", TgaAgentPolicies.CodeSpecialistV2, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Mathematik", TgaAgentPolicies.CodeSpecialistV2, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GO-Belegen", TgaAgentPolicies.CodeSpecialistV2, StringComparison.Ordinal);
        Assert.Contains("pro Modellturn genau einen", TgaAgentPolicies.CodeSpecialistV2, StringComparison.Ordinal);
        Assert.Contains("aktuellen Modellkontext", TgaAgentPolicies.CodeSpecialistV2, StringComparison.Ordinal);
        Assert.Contains("repository.verify", TgaAgentPolicies.CodeSpecialistV2, StringComparison.Ordinal);
        Assert.Contains("erkanntes", TgaAgentPolicies.CodeSpecialistV2, StringComparison.Ordinal);
        Assert.Contains("task.finish", TgaAgentPolicies.CodeSpecialistV2, StringComparison.Ordinal);
        Assert.Contains("nutzerrelevanten Meilenstein", TgaAgentPolicies.CodeSpecialistV2, StringComparison.Ordinal);
        Assert.Contains("Dateiinspektionen", TgaAgentPolicies.CodeSpecialistV2, StringComparison.Ordinal);
        Assert.Contains("strikt chronologisch", TgaAgentPolicies.CodeSpecialistV2, StringComparison.Ordinal);
        Assert.Contains("genau einem gültigen Treffer", TgaAgentPolicies.CodeSpecialistV2, StringComparison.Ordinal);
        Assert.Contains("contextGap", TgaAgentPolicies.CodeSpecialistV2, StringComparison.Ordinal);
        Assert.Contains("exakt in dem erfolgreichen webSearch-Beleg", TgaAgentPolicies.CodeSpecialistV2, StringComparison.Ordinal);
        Assert.Contains("task.finish", TgaAgentPolicies.CodeSpecialistV2, StringComparison.Ordinal);
        Assert.Contains("kein internes Chain-of-Thought", TgaAgentPolicies.CodeSpecialistV2, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, false, 0)]
    [InlineData(2, false, 0)]
    [InlineData(3, false, 1)]
    [InlineData(0, true, 0)]
    [InlineData(2, true, 0)]
    [InlineData(3, true, 2)]
    public void NoProgressPolicyReorientsOnceAndThenTerminatesAsBlocked(
        int turns,
        bool reorientationUsed,
        int expected)
    {
        Assert.Equal((CodingNoProgressDecision)expected, CodingAgentOrchestrator.DetermineNoProgressDecision(turns, reorientationUsed));
    }

    [Fact]
    public void IdenticalPreviouslyFailedActionIsDetectedBeforeClientExecution()
    {
        using var arguments = JsonDocument.Parse(
            """{"operation":"command","executable":"python","arguments":["-c","print(1)"],"purpose":"inspect"}""");
        var previous = new AgentActionEnvelope(
            "action-1",
            1,
            CodingAgentToolFacade.ExecutionRun,
            "command",
            arguments.RootElement.Clone(),
            "idempotency-1",
            "revision-1",
            DateTimeOffset.UtcNow);
        var candidate = previous with
        {
            ActionId = "action-2",
            Sequence = 2,
            IdempotencyKey = "idempotency-2",
        };
        using var result = JsonDocument.Parse("""{"exitCode":1}""");
        var failed = new AgentObservation(
            previous.ActionId,
            false,
            result.RootElement.Clone(),
            "result-hash",
            "process.failed",
            "Exit-Code 1");

        var duplicate = CodingAgentOrchestrator.FindRepeatedFailedAction(
            candidate,
            [previous],
            [failed]);

        Assert.NotNull(duplicate);
        Assert.Equal(previous.ActionId, duplicate.Value.Action.ActionId);
        Assert.Equal(failed.ActionId, duplicate.Value.Observation.ActionId);
    }

    [Fact]
    public void SuccessfulOrDifferentActionsAreNotRejectedAsDuplicates()
    {
        using var firstArguments = JsonDocument.Parse("""{"operation":"read","path":"README.md"}""");
        using var secondArguments = JsonDocument.Parse("""{"operation":"read","path":"src/App.cs"}""");
        using var result = JsonDocument.Parse("""{"success":true}""");
        var previous = new AgentActionEnvelope(
            "action-1",
            1,
            CodingAgentToolFacade.WorkspaceInspect,
            "read",
            firstArguments.RootElement.Clone(),
            "idempotency-1",
            "revision-1",
            DateTimeOffset.UtcNow);
        var different = previous with
        {
            ActionId = "action-2",
            Sequence = 2,
            Arguments = secondArguments.RootElement.Clone(),
        };
        var succeeded = new AgentObservation(
            previous.ActionId,
            true,
            result.RootElement.Clone(),
            "result-hash");

        Assert.Null(CodingAgentOrchestrator.FindRepeatedFailedAction(
            previous with { ActionId = "action-3", Sequence = 3 },
            [previous],
            [succeeded]));
        Assert.Null(CodingAgentOrchestrator.FindRepeatedFailedAction(
            different,
            [previous],
            [succeeded with { Succeeded = false }]));
    }

    [Fact]
    public void ImmediatelyRepeatedSuccessfulReadIsRejectedWithoutCallingTheClientAgain()
    {
        using var arguments = JsonDocument.Parse("""{"operation":"read","path":"README.md"}""");
        using var result = JsonDocument.Parse("""{"path":"README.md","text":"content"}""");
        var previous = new AgentActionEnvelope(
            "action-1",
            1,
            CodingAgentToolFacade.WorkspaceInspect,
            "read",
            arguments.RootElement.Clone(),
            "idempotency-1",
            "revision-1",
            DateTimeOffset.UtcNow);
        var candidate = previous with
        {
            ActionId = "action-2",
            Sequence = 2,
            IdempotencyKey = "idempotency-2",
        };
        var observation = new AgentObservation(
            previous.ActionId,
            true,
            result.RootElement.Clone(),
            "result-hash",
            EvidenceId: "evidence-1");

        var duplicate = CodingAgentOrchestrator.FindCoveredSuccessfulRead(
            candidate,
            [previous],
            [observation]);

        Assert.NotNull(duplicate);
        Assert.Equal("evidence-1", duplicate.Value.Observation.EvidenceId);
    }

    [Fact]
    public void ImmediatelyContainedSuccessfulReadIsRejectedWithoutCallingTheClientAgain()
    {
        using var previousArguments = JsonDocument.Parse(
            """{"operation":"read","path":"src/App.cs","startLine":1,"endLine":120}""");
        using var candidateArguments = JsonDocument.Parse(
            """{"operation":"read","path":"src/App.cs","startLine":1,"endLine":119}""");
        using var result = JsonDocument.Parse("""{"path":"src/App.cs","text":"content"}""");
        var previous = new AgentActionEnvelope(
            "action-1", 1, CodingAgentToolFacade.WorkspaceInspect, "read",
            previousArguments.RootElement.Clone(), "idempotency-1", "revision-1", DateTimeOffset.UtcNow);
        var candidate = previous with
        {
            ActionId = "action-2",
            Sequence = 2,
            Arguments = candidateArguments.RootElement.Clone(),
            IdempotencyKey = "idempotency-2",
        };
        var observation = new AgentObservation(
            previous.ActionId,
            true,
            result.RootElement.Clone(),
            "result-hash",
            EvidenceId: "evidence-1");

        var duplicate = CodingAgentOrchestrator.FindCoveredSuccessfulRead(
            candidate,
            [previous],
            [observation]);

        Assert.NotNull(duplicate);
    }

    [Fact]
    public void ReadThroughKnownEndOfFileCoversImmediateWholeFileRequest()
    {
        using var previousArguments = JsonDocument.Parse(
            """{"operation":"read","path":"src/App.cs","startLine":1,"endLine":119}""");
        using var wholeFileArguments = JsonDocument.Parse(
            """{"operation":"read","path":"src/App.cs"}""");
        using var result = JsonDocument.Parse(
            """{"path":"src/App.cs","startLine":1,"endLine":119,"totalLines":119,"text":"content"}""");
        var previous = new AgentActionEnvelope(
            "action-1", 1, CodingAgentToolFacade.WorkspaceInspect, "read",
            previousArguments.RootElement.Clone(), "idempotency-1", "revision-1", DateTimeOffset.UtcNow);
        var candidate = previous with
        {
            ActionId = "action-2",
            Sequence = 2,
            Arguments = wholeFileArguments.RootElement.Clone(),
            IdempotencyKey = "idempotency-2",
        };
        var observation = new AgentObservation(
            previous.ActionId,
            true,
            result.RootElement.Clone(),
            "result-hash",
            EvidenceId: "evidence-1");

        Assert.NotNull(CodingAgentOrchestrator.FindCoveredSuccessfulRead(
            candidate,
            [previous],
            [observation]));
    }

    [Fact]
    public void InterveningTurnMakesAnOlderCompactedReadEligibleAgain()
    {
        using var firstArguments = JsonDocument.Parse("""{"operation":"read","path":"README.md"}""");
        using var otherArguments = JsonDocument.Parse("""{"operation":"read","path":"src/App.cs"}""");
        using var result = JsonDocument.Parse("""{"success":true}""");
        var first = new AgentActionEnvelope(
            "action-1", 1, CodingAgentToolFacade.WorkspaceInspect, "read",
            firstArguments.RootElement.Clone(), "idempotency-1", "revision-1", DateTimeOffset.UtcNow);
        var intervening = new AgentActionEnvelope(
            "action-2", 2, CodingAgentToolFacade.WorkspaceInspect, "read",
            otherArguments.RootElement.Clone(), "idempotency-2", "revision-1", DateTimeOffset.UtcNow);
        var candidate = first with { ActionId = "action-3", Sequence = 3, IdempotencyKey = "idempotency-3" };
        var firstResult = new AgentObservation(first.ActionId, true, result.RootElement.Clone(), "hash-1");
        var secondResult = new AgentObservation(intervening.ActionId, true, result.RootElement.Clone(), "hash-2");

        Assert.Null(CodingAgentOrchestrator.FindCoveredSuccessfulRead(
            candidate,
            [first, intervening],
            [firstResult, secondResult]));
    }

    [Fact]
    public void FailedReadDoesNotPoisonAnAuthoritativeRecoveryRead()
    {
        using var arguments = JsonDocument.Parse("""{"operation":"read","path":"src/App.cs"}""");
        using var failedResult = JsonDocument.Parse("""{"failed":true}""");
        var previous = new AgentActionEnvelope(
            "action-1", 1, CodingAgentToolFacade.WorkspaceInspect, "read",
            arguments.RootElement.Clone(), "idempotency-1", "revision-1", DateTimeOffset.UtcNow);
        var candidate = previous with
        {
            ActionId = "action-2",
            Sequence = 2,
            IdempotencyKey = "idempotency-2",
        };
        var failed = new AgentObservation(
            previous.ActionId,
            false,
            failedResult.RootElement.Clone(),
            "failed-hash",
            "agent.duplicate_immediate_read",
            "Bereits gelesen.");

        Assert.Null(CodingAgentOrchestrator.FindRepeatedFailedAction(
            candidate,
            [previous],
            [failed]));
    }

    [Fact]
    public void FailedWorkspaceMutationRequiresOneAuthoritativeRead()
    {
        var action = new AgentActionEnvelope(
            "action-change", 2, CodingAgentToolFacade.WorkspaceChange, "replace",
            JsonSerializer.SerializeToElement(new
            {
                operation = "replace",
                path = "physics_textbook.py",
                oldText = "alt",
                newText = "neu",
                expectedSha256 = new string('a', 64),
            }),
            "idempotency-change", "revision-1", DateTimeOffset.UtcNow);
        var observation = new AgentObservation(
            action.ActionId,
            false,
            JsonSerializer.SerializeToElement(new
            {
                failed = true,
                reason = "old_text_not_found",
                path = "physics_textbook.py",
                authoritativeReadRequired = true,
            }),
            "failed-result",
            "client.replace_text_not_found",
            "oldText wurde nicht gefunden.");

        Assert.Equal(
            CodingAgentToolFacade.WorkspaceInspect,
            CodingAgentOrchestrator.DetermineRequiredToolAfterObservation(
                EmptyLedger(CodingTaskKind.Change),
                action,
                observation,
                [action],
                [observation]));
    }

    [Fact]
    public void RecoveryReadAfterFailedMutationRequiresCorrectedWorkspaceChange()
    {
        var failedChange = new AgentActionEnvelope(
            "action-change", 1, CodingAgentToolFacade.WorkspaceChange, "replace",
            JsonSerializer.SerializeToElement(new
            {
                operation = "replace",
                path = "physics_textbook.py",
                oldText = "alt",
                newText = "neu",
                expectedSha256 = new string('a', 64),
            }),
            "idempotency-change", "revision-1", DateTimeOffset.UtcNow);
        var read = new AgentActionEnvelope(
            "action-read", 2, CodingAgentToolFacade.WorkspaceInspect, "read",
            JsonSerializer.SerializeToElement(new { operation = "read", path = "physics_textbook.py" }),
            "idempotency-read", "revision-1", DateTimeOffset.UtcNow);
        var failedObservation = new AgentObservation(
            failedChange.ActionId,
            false,
            JsonSerializer.SerializeToElement(new { failed = true, reason = "old_text_not_found" }),
            "failed-result",
            "client.replace_text_not_found");
        var readObservation = new AgentObservation(
            read.ActionId,
            true,
            JsonSerializer.SerializeToElement(new
            {
                path = "physics_textbook.py",
                sha256 = new string('b', 64),
                startLine = 1,
                endLine = 10,
                totalLines = 10,
                text = "current source",
            }),
            "read-result",
            EvidenceId: "evidence-read");

        Assert.Equal(
            CodingAgentToolFacade.WorkspaceChange,
            CodingAgentOrchestrator.DetermineRequiredToolAfterObservation(
                EmptyLedger(CodingTaskKind.Change),
                read,
                readObservation,
                [failedChange, read],
                [failedObservation, readObservation]));
    }

    [Fact]
    public void MutationRecoveryNormalizesInspectGuessToAuthoritativeReadOfSameFile()
    {
        var failedChange = new AgentActionEnvelope(
            "action-change", 1, CodingAgentToolFacade.WorkspaceChange, "replace",
            JsonSerializer.SerializeToElement(new
            {
                operation = "replace",
                path = "physics_textbook.py",
                oldText = "alt",
                newText = "neu",
                expectedSha256 = new string('a', 64),
            }),
            "idempotency-change", "revision-1", DateTimeOffset.UtcNow);
        var failedObservation = new AgentObservation(
            failedChange.ActionId,
            false,
            JsonSerializer.SerializeToElement(new
            {
                failed = true,
                authoritativeReadRequired = true,
            }),
            "failed-result",
            "client.replace_text_not_found");
        var guessedSearch = new LmToolCall(
            "call-inspect",
            CodingAgentToolFacade.WorkspaceInspect,
            JsonSerializer.SerializeToElement(new
            {
                operation = "search",
                path = ".",
                query = "physics",
            }));

        var normalized = CodingAgentOrchestrator.NormalizeMutationRecoveryRead(
            guessedSearch,
            [failedChange],
            [failedObservation]);

        Assert.Equal("read", normalized.Arguments.GetProperty("operation").GetString());
        Assert.Equal("physics_textbook.py", normalized.Arguments.GetProperty("path").GetString());
        Assert.Null(CodingAgentOrchestrator.GetMutationRecoveryPreconditionFailure(
            normalized,
            [failedChange],
            [failedObservation]));
    }

    [Fact]
    public void MutationRecoveryRejectsChangeOfAnotherFileAfterAuthoritativeRead()
    {
        var failedChange = new AgentActionEnvelope(
            "action-change", 1, CodingAgentToolFacade.WorkspaceChange, "replace",
            JsonSerializer.SerializeToElement(new
            {
                operation = "replace",
                path = "physics_textbook.py",
                oldText = "alt",
                newText = "neu",
                expectedSha256 = new string('a', 64),
            }),
            "idempotency-change", "revision-1", DateTimeOffset.UtcNow);
        var read = new AgentActionEnvelope(
            "action-read", 2, CodingAgentToolFacade.WorkspaceInspect, "read",
            JsonSerializer.SerializeToElement(new { operation = "read", path = "physics_textbook.py" }),
            "idempotency-read", "revision-1", DateTimeOffset.UtcNow);
        var failedObservation = new AgentObservation(
            failedChange.ActionId,
            false,
            JsonSerializer.SerializeToElement(new
            {
                failed = true,
                authoritativeReadRequired = true,
            }),
            "failed-result",
            "client.workspace_version_conflict");
        var readObservation = new AgentObservation(
            read.ActionId,
            true,
            JsonSerializer.SerializeToElement(new
            {
                path = "physics_textbook.py",
                sha256 = new string('b', 64),
                text = "current source",
            }),
            "read-result");
        var wrongFileChange = new LmToolCall(
            "call-change",
            CodingAgentToolFacade.WorkspaceChange,
            JsonSerializer.SerializeToElement(new
            {
                operation = "replace",
                path = "other.py",
                oldText = "current",
                newText = "updated",
                expectedSha256 = new string('b', 64),
            }));

        Assert.NotNull(CodingAgentOrchestrator.GetMutationRecoveryPreconditionFailure(
            wrongFileChange,
            [failedChange, read],
            [failedObservation, readObservation]));
    }

    [Fact]
    public void WorkspaceChangeUsesTheLatestAuthoritativeReadSha()
    {
        var read = new AgentActionEnvelope(
            "action-read", 1, CodingAgentToolFacade.WorkspaceInspect, "read",
            JsonSerializer.SerializeToElement(new { operation = "read", path = "physics_textbook.py" }),
            "idempotency-read", "revision-1", DateTimeOffset.UtcNow);
        var readObservation = new AgentObservation(
            read.ActionId,
            true,
            JsonSerializer.SerializeToElement(new
            {
                path = "physics_textbook.py",
                sha256 = new string('b', 64),
                text = "current source",
            }),
            "read-result");
        var call = new LmToolCall(
            "call-change",
            CodingAgentToolFacade.WorkspaceChange,
            JsonSerializer.SerializeToElement(new
            {
                operation = "replace",
                path = "physics_textbook.py",
                oldText = "current",
                newText = "updated",
                expectedSha256 = new string('a', 64),
            }));

        var normalized = CodingAgentOrchestrator.NormalizeWorkspaceChangeExpectedSha(
            call,
            [read],
            [readObservation]);

        Assert.Equal(
            new string('b', 64),
            normalized.Arguments.GetProperty("expectedSha256").GetString());
    }

    [Fact]
    public void CompleteSourceReadAfterResearchRequiresWorkspaceChangeForCreationTask()
    {
        var action = new AgentActionEnvelope(
            "action-read", 3, CodingAgentToolFacade.WorkspaceInspect, "read",
            JsonSerializer.SerializeToElement(new
            {
                operation = "read",
                path = "physics_textbook.py",
                startLine = 1,
                endLine = 250,
            }),
            "idempotency-read", "revision-1", DateTimeOffset.UtcNow);
        var observation = new AgentObservation(
            action.ActionId,
            true,
            JsonSerializer.SerializeToElement(new
            {
                path = "physics_textbook.py",
                startLine = 1,
                endLine = 228,
                totalLines = 228,
                text = "source",
            }),
            "result-read",
            EvidenceId: "evidence-read");
        var ledger = new TaskLedgerSnapshot(
            "Erweitere das Projekt.", CodingTaskKind.Creation, CodingAgentPhase.Editing,
            "revision-1", [], [], [], [], [], "Bearbeite die Datei.",
            Research:
            [
                new AgentResearchRecord(
                    "research-key", "webFetch", "https://example.test/source", "action-fetch",
                    "evidence-fetch", "result-fetch", 0),
            ]);

        Assert.Equal(
            CodingAgentToolFacade.WorkspaceChange,
            CodingAgentOrchestrator.DetermineRequiredToolAfterObservation(ledger, action, observation));
    }

    [Fact]
    public void RequiredToolSelectionExposesExactlyOneSchema()
    {
        var tools = CodingAgentOrchestrator.SelectModelTools(CodingAgentToolFacade.WorkspaceChange);

        var tool = Assert.Single(tools);
        Assert.Equal(CodingAgentToolFacade.WorkspaceChange, tool.Name);
    }

    [Fact]
    public void IdenticalSuccessfulWebResearchReusesTheExistingEvidence()
    {
        var arguments = JsonSerializer.SerializeToElement(new
        {
            operation = "webFetch",
            url = "https://example.test/reference.pdf",
        });
        var previous = new AgentActionEnvelope(
            "action-1", 1, CodingAgentToolFacade.ResearchQuery, "webFetch",
            arguments, "idempotency-1", "revision-1", DateTimeOffset.UtcNow);
        var intervening = new AgentActionEnvelope(
            "action-2", 2, CodingAgentToolFacade.WorkspaceInspect, "map",
            JsonSerializer.SerializeToElement(new { operation = "map" }),
            "idempotency-2", "revision-1", DateTimeOffset.UtcNow);
        var candidate = previous with
        {
            ActionId = "action-3",
            Sequence = 3,
            IdempotencyKey = "idempotency-3",
        };
        var observation = new AgentObservation(
            previous.ActionId,
            true,
            JsonSerializer.SerializeToElement(new { url = "https://example.test/reference.pdf" }),
            "result-hash",
            EvidenceId: "evidence-reference");

        var duplicate = CodingAgentOrchestrator.FindRepeatedSuccessfulResearchAction(
            candidate,
            [previous, intervening],
            [observation]);

        Assert.NotNull(duplicate);
        Assert.Equal("evidence-reference", duplicate.Value.Observation.EvidenceId);
    }

    [Fact]
    public void DifferentWebResearchRemainsAvailable()
    {
        var previous = new AgentActionEnvelope(
            "action-1", 1, CodingAgentToolFacade.ResearchQuery, "webSearch",
            JsonSerializer.SerializeToElement(new { operation = "webSearch", query = "Thema A" }),
            "idempotency-1", "revision-1", DateTimeOffset.UtcNow);
        var candidate = previous with
        {
            ActionId = "action-2",
            Sequence = 2,
            Arguments = JsonSerializer.SerializeToElement(new { operation = "webSearch", query = "Thema B" }),
            IdempotencyKey = "idempotency-2",
        };
        var observation = new AgentObservation(
            previous.ActionId,
            true,
            JsonSerializer.SerializeToElement(new { query = "Thema A" }),
            "result-hash",
            EvidenceId: "evidence-a");

        Assert.Null(CodingAgentOrchestrator.FindRepeatedSuccessfulResearchAction(
            candidate,
            [previous],
            [observation]));
    }

    [Fact]
    public void WebFetchAcceptsAnExactUrlFromASuccessfulSearchObservation()
    {
        var search = new AgentActionEnvelope(
            "action-search", 1, CodingAgentToolFacade.ResearchQuery, "webSearch",
            JsonSerializer.SerializeToElement(new { operation = "webSearch", query = "Physik" }),
            "idempotency-search", "revision-1", DateTimeOffset.UtcNow);
        var searchObservation = new AgentObservation(
            search.ActionId,
            true,
            JsonSerializer.SerializeToElement(new
            {
                query = "Physik",
                results = new[]
                {
                    new { title = "Physik", url = "https://example.test/physics/#overview" },
                },
            }),
            "result-search");
        var fetch = new AgentActionEnvelope(
            "action-fetch", 2, CodingAgentToolFacade.ResearchQuery, "webFetch",
            JsonSerializer.SerializeToElement(new { operation = "webFetch", url = "https://example.test/physics" }),
            "idempotency-fetch", "revision-1", DateTimeOffset.UtcNow);

        Assert.Null(CodingAgentOrchestrator.GetWebFetchProvenanceFailure(
            fetch,
            "Recherchiere Physik.",
            [search],
            [searchObservation]));
    }

    [Fact]
    public void WebFetchRejectsAUrlInventedAfterSearch()
    {
        var search = new AgentActionEnvelope(
            "action-search", 1, CodingAgentToolFacade.ResearchQuery, "webSearch",
            JsonSerializer.SerializeToElement(new { operation = "webSearch", query = "Physik" }),
            "idempotency-search", "revision-1", DateTimeOffset.UtcNow);
        var searchObservation = new AgentObservation(
            search.ActionId,
            true,
            JsonSerializer.SerializeToElement(new
            {
                results = new[] { new { title = "Beleg", url = "https://example.test/evidence" } },
            }),
            "result-search");
        var fetch = new AgentActionEnvelope(
            "action-fetch", 2, CodingAgentToolFacade.ResearchQuery, "webFetch",
            JsonSerializer.SerializeToElement(new { operation = "webFetch", url = "https://example.test/invented" }),
            "idempotency-fetch", "revision-1", DateTimeOffset.UtcNow);

        var failure = CodingAgentOrchestrator.GetWebFetchProvenanceFailure(
            fetch,
            "Recherchiere Physik.",
            [search],
            [searchObservation]);

        Assert.NotNull(failure);
        Assert.Contains("weder", failure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("webSearch", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void WebFetchAcceptsAUrlExplicitlyProvidedByTheUser()
    {
        var fetch = new AgentActionEnvelope(
            "action-fetch", 1, CodingAgentToolFacade.ResearchQuery, "webFetch",
            JsonSerializer.SerializeToElement(new { operation = "webFetch", url = "https://example.test/reference.pdf" }),
            "idempotency-fetch", "revision-1", DateTimeOffset.UtcNow);

        Assert.Null(CodingAgentOrchestrator.GetWebFetchProvenanceFailure(
            fetch,
            "Lies https://example.test/reference.pdf und fasse die Quelle zusammen.",
            [],
            []));
    }

    [Theory]
    [InlineData("research.query")]
    [InlineData("artifact.process")]
    public void HttpDocumentReadIsDeterministicallyNormalizedToWebFetch(string toolName)
    {
        var call = new LmToolCall(
            "call-url",
            toolName,
            JsonSerializer.SerializeToElement(new
            {
                operation = "documentRead",
                reference = "https://de.wikibooks.org/wiki/Formelsammlung_Physik:_Klassische_Mechanik",
                scope = "workspace",
                commentary = "Die Quelle wird ausgewertet.",
            }));

        var normalized = CodingAgentOrchestrator.NormalizeMisroutedDocumentRead(call);

        Assert.Equal(CodingAgentToolFacade.ResearchQuery, normalized.Name);
        Assert.Equal("webFetch", normalized.Arguments.GetProperty("operation").GetString());
        Assert.Equal(
            "https://de.wikibooks.org/wiki/Formelsammlung_Physik:_Klassische_Mechanik",
            normalized.Arguments.GetProperty("url").GetString());
        Assert.Equal("Die Quelle wird ausgewertet.", normalized.Arguments.GetProperty("commentary").GetString());
        Assert.False(normalized.Arguments.TryGetProperty("reference", out _));
        Assert.False(normalized.Arguments.TryGetProperty("scope", out _));
    }

    [Fact]
    public void LocalDocumentReadIsNotRewritten()
    {
        var call = new LmToolCall(
            "call-local",
            CodingAgentToolFacade.ResearchQuery,
            JsonSerializer.SerializeToElement(new
            {
                operation = "documentRead",
                reference = "docs/mechanics.pdf",
                scope = "workspace",
            }));

        var normalized = CodingAgentOrchestrator.NormalizeMisroutedDocumentRead(call);

        Assert.Same(call, normalized);
    }

    [Fact]
    public void SourceCodeDocumentReadIsNormalizedToWorkspaceRead()
    {
        var call = new LmToolCall(
            "call-source",
            CodingAgentToolFacade.ResearchQuery,
            JsonSerializer.SerializeToElement(new
            {
                operation = "documentRead",
                reference = "physics_textbook.py",
                scope = "workspace",
            }));

        var normalized = CodingAgentOrchestrator.NormalizeMisroutedDocumentRead(call);

        Assert.Equal(CodingAgentToolFacade.WorkspaceInspect, normalized.Name);
        Assert.Equal("read", normalized.Arguments.GetProperty("operation").GetString());
        Assert.Equal("physics_textbook.py", normalized.Arguments.GetProperty("path").GetString());
    }

    [Fact]
    public void ResearchEvidenceDocumentReadResolvesToItsFetchedUrl()
    {
        var call = new LmToolCall(
            "call-evidence",
            CodingAgentToolFacade.ResearchQuery,
            JsonSerializer.SerializeToElement(new
            {
                operation = "documentRead",
                reference = "evidence-web",
                scope = "session",
            }));
        var research = new[]
        {
            new AgentResearchRecord(
                "fetch-key", "webFetch", "https://example.test/mechanics", "action-fetch",
                "evidence-web", "result-fetch", 0),
        };

        var normalized = CodingAgentOrchestrator.NormalizeMisroutedDocumentRead(call, research);

        Assert.Equal(CodingAgentToolFacade.ResearchQuery, normalized.Name);
        Assert.Equal("webFetch", normalized.Arguments.GetProperty("operation").GetString());
        Assert.Equal("https://example.test/mechanics", normalized.Arguments.GetProperty("url").GetString());
    }

    [Fact]
    public void PersistentResearchLedgerRejectsADuplicateBeyondTheRecentTurnWindow()
    {
        var original = new AgentActionEnvelope(
            "action-original", 1, CodingAgentToolFacade.ResearchQuery, "webSearch",
            JsonSerializer.SerializeToElement(new { operation = "webSearch", query = "  Physik   Grundlagen  ", language = "de-DE" }),
            "idempotency-original", "revision-1", DateTimeOffset.UtcNow);
        var candidate = original with
        {
            ActionId = "action-late",
            Sequence = 20,
            Arguments = JsonSerializer.SerializeToElement(new { operation = "webSearch", query = "physik grundlagen", language = "de-de" }),
            IdempotencyKey = "idempotency-late",
        };
        var key = Assert.IsType<string>(CodingAgentOrchestrator.CreateResearchKey(original));
        var record = new AgentResearchRecord(
            key, "webSearch", "Physik Grundlagen", original.ActionId,
            "evidence-search", "result-search", 0, DateTimeOffset.UtcNow,
            ["https://example.test/physics"]);

        var duplicate = CodingAgentOrchestrator.FindRepeatedSuccessfulResearchRecord(candidate, [record]);

        Assert.NotNull(duplicate);
        Assert.Equal("evidence-search", duplicate.EvidenceId);
    }

    [Fact]
    public void ActiveSearchRequiresExactlyOneFetchBeforeAnotherSearch()
    {
        var candidate = new AgentActionEnvelope(
            "action-next", 10, CodingAgentToolFacade.ResearchQuery, "webSearch",
            JsonSerializer.SerializeToElement(new { operation = "webSearch", query = "weiteres Thema" }),
            "idempotency-next", "revision-1", DateTimeOffset.UtcNow);
        var research = new[]
        {
            new AgentResearchRecord(
                "key-search", "webSearch", "Erstes Thema", "action-search",
                "evidence-search", "result-search", 0, WorkCycle: 3),
        };
        var ledger = new TaskLedgerSnapshot(
            "Erstelle ein Projekt.", CodingTaskKind.Creation, CodingAgentPhase.Editing,
            "revision-1", [], [], [], [], [], "Lies einen Treffer.", Research: research,
            WorkCycleStage: CodingWorkCycleStage.Researching, WorkCycle: 3);

        var failure = CodingAgentOrchestrator.GetChronologyPreconditionFailure(candidate, ledger);
        var fetch = candidate with
        {
            ActionId = "action-fetch",
            Operation = "webFetch",
            Arguments = JsonSerializer.SerializeToElement(new
            {
                operation = "webFetch",
                url = "https://example.test/result",
            }),
            IdempotencyKey = "idempotency-fetch",
        };

        Assert.NotNull(failure);
        Assert.Contains("genau einen", failure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("webFetch", failure, StringComparison.Ordinal);
        Assert.Null(CodingAgentOrchestrator.GetChronologyPreconditionFailure(fetch, ledger));
    }

    [Fact]
    public void ActiveFetchedSourceMustBeSynthesizedBeforeAnotherSearch()
    {
        var candidate = new AgentActionEnvelope(
            "action-next", 10, CodingAgentToolFacade.ResearchQuery, "webSearch",
            JsonSerializer.SerializeToElement(new { operation = "webSearch", query = "weitere Variante" }),
            "idempotency-next", "revision-1", DateTimeOffset.UtcNow);
        var research = new[]
        {
            new AgentResearchRecord(
                "key-fetch", "webFetch", "https://example.test/source", "action-fetch",
                "evidence-fetch", "result-fetch", 0, WorkCycle: 2),
        };
        var ledger = new TaskLedgerSnapshot(
            "Analysiere das Thema.", CodingTaskKind.Analysis, CodingAgentPhase.Editing,
            "revision-1", [], [], [], [], [], "Synthetisiere.", Research: research,
            WorkCycleStage: CodingWorkCycleStage.ReadyToFinish, WorkCycle: 2,
            ActiveResearchEvidenceId: "evidence-fetch");

        var failure = CodingAgentOrchestrator.GetChronologyPreconditionFailure(candidate, ledger);

        Assert.NotNull(failure);
        Assert.Contains("task.finish", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void PersistentSearchUrlsAuthorizeFetchWithoutKeepingRawSearchResults()
    {
        var fetch = new AgentActionEnvelope(
            "action-fetch", 8, CodingAgentToolFacade.ResearchQuery, "webFetch",
            JsonSerializer.SerializeToElement(new { operation = "webFetch", url = "https://example.test/source#section" }),
            "idempotency-fetch", "revision-1", DateTimeOffset.UtcNow);
        var research = new AgentResearchRecord(
            "key-search", "webSearch", "Thema", "action-search", "evidence-search",
            "result-search", 0, SourceUrls: ["https://example.test/source"]);

        Assert.Null(CodingAgentOrchestrator.GetWebFetchProvenanceFailure(
            fetch,
            "Recherchiere das Thema.",
            [research]));
    }

    [Fact]
    public void OverviewPageCoveringEveryDistinctiveSearchTermCompletesResearch()
    {
        var search = new AgentResearchRecord(
            "search-key", "webSearch",
            "Physik Lehrbuch Themenklassen Mechanik Elektrodynamik Thermodynamik Optik Quantenmechanik Relativitätstheorie",
            "action-search", "evidence-search", "result-search", 0);
        var fetch = new AgentActionEnvelope(
            "action-fetch", 2, CodingAgentToolFacade.ResearchQuery, "webFetch",
            JsonSerializer.SerializeToElement(new { operation = "webFetch", url = "https://example.test/overview" }),
            "idempotency-fetch", "revision-1", DateTimeOffset.UtcNow);
        var result = JsonSerializer.SerializeToElement(new
        {
            url = "https://example.test/overview",
            content = "Diese Übersicht behandelt Physik, Mechanik, Elektrodynamik, Thermodynamik, Optik, Quantenmechanik und Relativitätstheorie.",
        });

        var coverage = CodingAgentOrchestrator.FindSatisfiedSearchCoverage(fetch, result, [search], 0);

        Assert.NotNull(coverage);
        Assert.Equal("search-key", coverage.Value.SearchKey);
        Assert.Contains("mechanik", coverage.Value.CoveredTerms, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("relativitätstheorie", coverage.Value.CoveredTerms, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void SearchConfirmingAnAlreadyFetchedExactPageCompletesResearch()
    {
        var research = new List<AgentResearchRecord>
        {
            new(
                "fetch-key", "webFetch",
                "https://de.wikibooks.org/wiki/Formelsammlung_Physik:_Klassische_Mechanik",
                "action-fetch", "evidence-fetch", "result-fetch", 0),
        };
        var search = new AgentActionEnvelope(
            "action-search", 2, CodingAgentToolFacade.ResearchQuery, "webSearch",
            JsonSerializer.SerializeToElement(new
            {
                operation = "webSearch",
                query = "Deutsche Formelsammlung Klassische Mechanik Wikibooks",
                language = "de-DE",
            }),
            "idempotency-search", "revision-1", DateTimeOffset.UtcNow);
        var result = JsonSerializer.SerializeToElement(new
        {
            results = new[]
            {
                new
                {
                    title = "Formelsammlung Physik: Klassische Mechanik",
                    url = "https://de.wikibooks.org/wiki/Formelsammlung_Physik:_Klassische_Mechanik#section",
                },
            },
        });

        var changed = CodingAgentOrchestrator.MarkFetchedPageConfirmedBySearch(
            search,
            result,
            research,
            0);

        Assert.True(changed);
        Assert.True(research[0].SearchCoverageSatisfied);
        Assert.Equal(CodingAgentOrchestrator.CreateResearchKey(search), research[0].CoveredSearchKey);
        Assert.Contains("mechanik", research[0].CoveredTerms!, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void PartialPageDoesNotPrematurelyCompleteResearch()
    {
        var search = new AgentResearchRecord(
            "search-key", "webSearch", "Mechanik Elektrodynamik Thermodynamik Optik",
            "action-search", "evidence-search", "result-search", 0);
        var fetch = new AgentActionEnvelope(
            "action-fetch", 2, CodingAgentToolFacade.ResearchQuery, "webFetch",
            JsonSerializer.SerializeToElement(new { operation = "webFetch", url = "https://example.test/mechanics" }),
            "idempotency-fetch", "revision-1", DateTimeOffset.UtcNow);
        var result = JsonSerializer.SerializeToElement(new { content = "Diese Seite behandelt ausschließlich Mechanik." });

        Assert.Null(CodingAgentOrchestrator.FindSatisfiedSearchCoverage(fetch, result, [search], 0));
    }

    [Fact]
    public void CompletedCoverageIsScopedToTheCurrentWorkCycle()
    {
        var completed = new AgentResearchRecord(
            "fetch-key", "webFetch", "https://example.test/overview", "action-fetch",
            "evidence-fetch", "result-fetch", 0, SearchCoverageSatisfied: true,
            CoveredSearchKey: "search-key", CoveredTerms: ["mechanik", "optik", "thermodynamik"]);
        var ledger = new TaskLedgerSnapshot(
            "Erstelle eine Übersicht.", CodingTaskKind.Creation, CodingAgentPhase.Editing,
            "revision-1", [], [], [], [], [], "Erstelle eine Datei.", Research: [completed]);

        Assert.NotNull(CodingAgentOrchestrator.FindCompletedResearchCoverage(ledger));

        var nextGeneration = ledger with
        {
            Changes = [new AgentChangeRecord("README.md", null, "sha", "change-1", "create")],
            WorkCycle = 1,
        };
        Assert.Null(CodingAgentOrchestrator.FindCompletedResearchCoverage(nextGeneration));
    }

    [Fact]
    public void NewSearchAfterFetchedSourceRequiresAConcreteContextGap()
    {
        var source = new AgentResearchRecord(
            "fetch-key", "webFetch", "https://example.test/source", "action-fetch",
            "evidence-fetch", "result-fetch", 0, WorkCycle: 4);
        var ledger = new TaskLedgerSnapshot(
            "Erstelle ein Projekt.", CodingTaskKind.Creation, CodingAgentPhase.Editing,
            "revision-1", [], [], [], [], [], "Setze die Quelle um.", Research: [source],
            WorkCycleStage: CodingWorkCycleStage.Implementing, WorkCycle: 4,
            ActiveResearchEvidenceId: "evidence-fetch");
        var withoutGap = new AgentActionEnvelope(
            "action-search-1", 2, CodingAgentToolFacade.ResearchQuery, "webSearch",
            JsonSerializer.SerializeToElement(new { operation = "webSearch", query = "weitere Quelle" }),
            "idempotency-search-1", "revision-1", DateTimeOffset.UtcNow);
        var withGap = withoutGap with
        {
            ActionId = "action-search-2",
            IdempotencyKey = "idempotency-search-2",
            Arguments = JsonSerializer.SerializeToElement(new
            {
                operation = "webSearch",
                query = "fehlende API-Spezifikation",
                contextGap = "Die Quelle enthält die benötigte API-Signatur für die Implementierung nicht.",
            }),
        };

        Assert.NotNull(CodingAgentOrchestrator.GetChronologyPreconditionFailure(withoutGap, ledger));
        Assert.Null(CodingAgentOrchestrator.GetChronologyPreconditionFailure(withGap, ledger));
        Assert.True(CodingAgentOrchestrator.StartsNewResearchCycle(withGap, ledger));
    }

    [Fact]
    public void UnverifiedChangeForcesExecutionBeforeAnotherAction()
    {
        var ledger = new TaskLedgerSnapshot(
            "Ändere das Projekt.", CodingTaskKind.Change, CodingAgentPhase.Verifying,
            "revision-2", [], [],
            [new AgentChangeRecord("README.md", "before", "after", "action-change", "write")],
            [], [], "Prüfe.", WorkCycleStage: CodingWorkCycleStage.Verifying,
            VerifiedChangeCount: 0);
        var nextChange = new AgentActionEnvelope(
            "action-next", 2, CodingAgentToolFacade.WorkspaceChange, "write",
            JsonSerializer.SerializeToElement(new { operation = "write", path = "README.md", content = "neu" }),
            "idempotency-next", "revision-2", DateTimeOffset.UtcNow);

        var failure = CodingAgentOrchestrator.GetChronologyPreconditionFailure(nextChange, ledger);
        var tools = CodingAgentOrchestrator.SelectModelTools(null, ledger);

        Assert.NotNull(failure);
        Assert.Contains("execution.run", failure, StringComparison.Ordinal);
        Assert.Equal(
            [CodingAgentToolFacade.ExecutionRun, CodingAgentToolFacade.TaskFinish],
            tools.Select(static tool => tool.Name));
    }

    [Fact]
    public void FailedVerificationMayOpenExactlyOneExplainedResearchCycle()
    {
        var ledger = new TaskLedgerSnapshot(
            "Ändere das Projekt.", CodingTaskKind.Change, CodingAgentPhase.Editing,
            "revision-2", [], [],
            [new AgentChangeRecord("src/App.cs", "before", "after", "action-change", "write")],
            [], ["execution.run/test: fehlgeschlagen"], "Behebe den Fehler.",
            WorkCycleStage: CodingWorkCycleStage.Implementing,
            WorkCycle: 2,
            VerifiedChangeCount: 0,
            ContextGapResearchAllowed: true);
        var search = new AgentActionEnvelope(
            "action-search", 3, CodingAgentToolFacade.ResearchQuery, "webSearch",
            JsonSerializer.SerializeToElement(new
            {
                operation = "webSearch",
                query = "API Fehlercode Referenz",
                contextGap = "Die Testdiagnose verweist auf einen undokumentierten API-Fehlercode.",
            }),
            "idempotency-search", "revision-2", DateTimeOffset.UtcNow);

        Assert.Null(CodingAgentOrchestrator.GetChronologyPreconditionFailure(search, ledger));
        Assert.True(CodingAgentOrchestrator.StartsNewResearchCycle(search, ledger));

        var withoutFailedVerification = ledger with { ContextGapResearchAllowed = false };
        Assert.NotNull(CodingAgentOrchestrator.GetChronologyPreconditionFailure(search, withoutFailedVerification));
    }

    [Theory]
    [InlineData("{\"exitCode\":0}", false)]
    [InlineData("{\"exitCode\":1}", true)]
    [InlineData("{\"exitCode\":9009}", true)]
    [InlineData("{\"timedOut\":true}", true)]
    [InlineData("{\"cancelled\":true}", true)]
    [InlineData("{\"success\":false}", true)]
    public void ProcessAndToolFailuresAreNotRecordedAsSuccessfulVerification(
        string json,
        bool expected)
    {
        using var result = JsonDocument.Parse(json);

        Assert.Equal(expected, CodingAgentOrchestrator.ResultExplicitlyFailed(result.RootElement));
    }

    [Theory]
    [InlineData("python", "[\"-c\",\"print('available')\"]", "build", null)]
    [InlineData("python", "[\"-m\",\"pytest\"]", "test", "test")]
    [InlineData("dotnet", "[\"build\"]", "build", "build")]
    [InlineData("cargo", "[\"check\"]", "build", "build")]
    public void OnlyRealVerificationCommandsProduceVerificationRecords(
        string executable,
        string argumentsJson,
        string purpose,
        string? expected)
    {
        using var arguments = JsonDocument.Parse(
            $$"""{"executable":"{{executable}}","arguments":{{argumentsJson}},"purpose":"{{purpose}}"}""");
        var action = new AgentActionEnvelope(
            "action-1",
            1,
            CodingAgentToolFacade.ExecutionRun,
            "command",
            arguments.RootElement.Clone(),
            "idempotency-1",
            "revision-1",
            DateTimeOffset.UtcNow);

        Assert.Equal(expected, CodingAgentOrchestrator.GetCredibleVerificationKind(action));
    }

    [Theory]
    [InlineData("status", null)]
    [InlineData("check", "lean-check")]
    [InlineData("build", "lean-build")]
    [InlineData("axioms", "lean-axioms")]
    [InlineData("verify", "verify")]
    public void LeanOperationsAreClassifiedByTheirActualProofStrength(
        string leanOperation,
        string? expected)
    {
        using var arguments = JsonDocument.Parse(
            $$"""{"operation":"lean","leanOperation":"{{leanOperation}}","target":"proofs/Test.lean"}""");
        var action = new AgentActionEnvelope(
            "action-1", 1, CodingAgentToolFacade.ExecutionRun, "lean",
            arguments.RootElement.Clone(), "idempotency-1", "revision-1", DateTimeOffset.UtcNow);

        Assert.Equal(expected, CodingAgentOrchestrator.GetCredibleVerificationKind(action));
    }

    [Fact]
    public void LeanVerificationRequiresAProofFileChangedByTheCurrentRun()
    {
        using var arguments = JsonDocument.Parse(
            """{"operation":"verify","path":"proofs/Force.lean","theoremName":"Force.second_law"}""");
        var dispatch = new CodingToolDispatch(
            CodingAgentToolFacade.ExecutionRun,
            "lean",
            ClientToolNames.LeanProof,
            arguments.RootElement.Clone(),
            null,
            false,
            null);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CodingAgentOrchestrator.ValidateToolPreconditions(
                dispatch,
                EmptyLedger(CodingTaskKind.Creation)));

        Assert.Contains("zuerst selbst erstellen oder ändern", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingLeanProofFileIsASemanticPreconditionFailureInsteadOfASchemaFailure()
    {
        using var arguments = JsonDocument.Parse(
            """{"operation":"verify","path":"proofs/Force.lean","theoremName":"Force.second_law"}""");
        var dispatch = new CodingToolDispatch(
            CodingAgentToolFacade.ExecutionRun,
            "lean",
            ClientToolNames.LeanProof,
            arguments.RootElement.Clone(),
            null,
            false,
            null);

        var failure = CodingAgentOrchestrator.GetToolPreconditionFailure(
            dispatch,
            EmptyLedger(CodingTaskKind.Creation));

        Assert.NotNull(failure);
        Assert.Contains("zuerst selbst erstellen oder ändern", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void LeanVerificationAcceptsAProofFileChangedByTheCurrentRun()
    {
        using var arguments = JsonDocument.Parse(
            """{"operation":"verify","path":"proofs/Force.lean","theoremName":"Force.second_law"}""");
        var dispatch = new CodingToolDispatch(
            CodingAgentToolFacade.ExecutionRun,
            "lean",
            ClientToolNames.LeanProof,
            arguments.RootElement.Clone(),
            null,
            false,
            null);
        var ledger = EmptyLedger(CodingTaskKind.Creation) with
        {
            Changes = [new AgentChangeRecord("proofs/Force.lean", null, "after", "action-1", "created")],
        };

        CodingAgentOrchestrator.ValidateToolPreconditions(dispatch, ledger);
    }

    [Theory]
    [InlineData("workspace", true)]
    [InlineData("change", true)]
    [InlineData("research", true)]
    [InlineData("artifact", true)]
    [InlineData("execution", false)]
    [InlineData("verification", false)]
    public void ProcessReceiptsDoNotBypassNoProgressDetection(string kind, bool expected)
    {
        var evidence = new EvidenceRef("evidence-1", kind, "result");

        Assert.Equal(expected, CodingAgentOrchestrator.CountsAsProgressEvidence(evidence));
    }

    [Fact]
    public void AnalysisCannotFinishWithoutAWorkspaceOrResearchEvidence()
    {
        using var arguments = JsonDocument.Parse("""{"status":"completed","summary":"Fertig."}""");
        var ledger = EmptyLedger(CodingTaskKind.Analysis);

        var rejection = CodingAgentOrchestrator.ValidateFinish(
            arguments.RootElement,
            ledger,
            Request(),
            actionCount: 1);

        Assert.Contains("Werkzeugbeleg", rejection, StringComparison.Ordinal);
    }

    [Fact]
    public void FinishMustReferencePersistedEvidenceChangesAndVerification()
    {
        var ledger = EmptyLedger(CodingTaskKind.Change) with
        {
            Evidence = [new EvidenceRef("evidence-1", "workspace", "source")],
            Changes = [new AgentChangeRecord("src/App.cs", "before", "after", "action-1", "changed")],
            Verifications = [new AgentVerificationRecord("verify-1", "test", ".", true, "action-2", "passed")],
        };
        using var missingClaims = JsonDocument.Parse("""{"status":"completed","summary":"Fertig."}""");
        using var complete = JsonDocument.Parse("""
            {
              "status":"completed",
              "summary":"Fertig.",
              "evidenceIds":["evidence-1"],
              "changedPaths":["src/App.cs"],
              "verificationIds":["verify-1"]
            }
            """);

        Assert.NotNull(CodingAgentOrchestrator.ValidateFinish(
            missingClaims.RootElement,
            ledger,
            Request(),
            actionCount: 3));
        Assert.Null(CodingAgentOrchestrator.ValidateFinish(
            complete.RootElement,
            ledger,
            Request(),
            actionCount: 3));
    }

    [Theory]
    [InlineData("Prüfe den Beweis mit Lean.", true)]
    [InlineData("Jede Gleichung braucht einen gültigen Lean-Beweis.", true)]
    [InlineData("Erstelle die Datei ohne Lean.", false)]
    [InlineData("Erstelle eine normale Textdatei.", false)]
    public void ExplicitLeanVerificationIsDetectedWithoutMakingLeanAUniversalRule(string goal, bool expected)
    {
        Assert.Equal(expected, CodingAgentOrchestrator.RequiresLeanVerification(goal));
    }

    [Fact]
    public void ExplicitLeanAndPdfRequirementsRejectAnUnverifiedFinish()
    {
        var goal = "Erstelle ein Lehrbuch, prüfe die Gleichungen mit Lean und exportiere es als PDF.";
        var ledger = EmptyLedger(CodingTaskKind.Creation) with
        {
            Evidence = [new EvidenceRef("evidence-1", "workspace", "source")],
            Changes = [new AgentChangeRecord("book.md", null, "after", "action-1", "created")],
        };
        using var finish = JsonDocument.Parse("""
            {
              "status":"completed",
              "summary":"Fertig.",
              "evidenceIds":["evidence-1"],
              "changedPaths":["book.md"]
            }
            """);

        var rejection = CodingAgentOrchestrator.ValidateFinish(
            finish.RootElement,
            ledger,
            Request(goal),
            actionCount: 2);

        Assert.Contains("proof.lean verify", rejection, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitLeanAndPdfRequirementsAcceptMatchingVerifiedEvidence()
    {
        var goal = "Erstelle ein Lehrbuch, prüfe die Gleichungen mit Lean und exportiere es als PDF.";
        var ledger = EmptyLedger(CodingTaskKind.Creation) with
        {
            Evidence =
            [
                new EvidenceRef("evidence-1", "workspace", "source"),
                new EvidenceRef("evidence-pdf", "artifact", "artifact.process / documentCreate erfolgreich."),
            ],
            Changes = [new AgentChangeRecord("book.md", null, "after", "action-1", "created")],
            Verifications = [new AgentVerificationRecord("verify-lean", "verify", "proofs/book.lean", true, "action-2", "passed")],
        };
        using var finish = JsonDocument.Parse("""
            {
              "status":"completed",
              "summary":"Fertig.",
              "evidenceIds":["evidence-1","evidence-pdf"],
              "changedPaths":["book.md"],
              "verificationIds":["verify-lean"]
            }
            """);

        Assert.Null(CodingAgentOrchestrator.ValidateFinish(
            finish.RootElement,
            ledger,
            Request(goal),
            actionCount: 3));
    }

    private static TaskLedgerSnapshot EmptyLedger(CodingTaskKind kind) => new(
        "goal",
        kind,
        CodingAgentPhase.Finishing,
        "revision-1",
        [],
        [],
        [],
        [],
        [],
        "finish");

    private static RunRequest Request(string goal = "Bearbeite das Projekt.") => new(
        GoAiProtocol.Version,
        RunMode.Code,
        [new RunMessage("user", [new ContentPart("text", goal)])]);
}
