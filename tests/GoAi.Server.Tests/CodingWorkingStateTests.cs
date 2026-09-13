using GoAi.Server.Core.Coding;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runs;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class CodingWorkingStateTests
{
    private static readonly string[] CompletedEvidenceIds = ["read-1"];
    private static readonly string[] UnknownEvidenceIds = ["invented"];
    private static readonly string[] FailedEvidenceIds = ["failed-1"];
    private static readonly string[] PlanEvidenceIds = ["plan"];
    private static readonly string[] VerificationArguments = ["-m", "unittest"];
    private static readonly string[] MissingInterpreterArguments = ["-c", new string('#', 2048)];

    [Fact]
    public void OutputEvidenceAliasesResolveToPersistedCanonicalPlanAndFactIdsAfterCheckpointRecovery()
    {
        const string outputId = "ev-0123456789abcdef0123456789abcdef";
        const string canonicalId = "server-actual-operation";
        var state = CodingWorkingStateReducer.ObserveToolResult(CodingWorkingState.Create("Read source and complete the plan."),
            Call(canonicalId, "coding.read", new { path = "source.py" }),
            Json(new { status = "completed", result = new { content = "verified source", evidence = new { evidenceId = outputId } } }));
        state = CodingWorkingStateReducer.ObserveToolResult(state, Call("server-output-read", "coding.readOutput", new { evidenceId = outputId }),
            Json(new { status = "completed", result = new { evidenceId = outputId, text = "verified source" } }));
        state = JsonSerializer.Deserialize<CodingWorkingState>(Json(state), JsonSerializerOptions.Web)!;
        string[] aliases = [outputId, canonicalId];
        state = CodingWorkingStateReducer.ApplyPlanUpdate(state, Args(new
        {
            steps = new[] { new { id = "inspect", title = "Read source", status = "completed", evidenceIds = aliases } },
            acceptanceCriteria = new[] { new { id = "source", title = "Current source inspected", status = "completed", evidenceIds = aliases } },
            facts = new[] { new { text = "The source was read.", evidenceIds = aliases } },
        }));
        Assert.Equal(canonicalId, Assert.Single(Assert.Single(state.Plan).EvidenceIds));
        Assert.Equal(canonicalId, Assert.Single(Assert.Single(state.AcceptanceCriteria).EvidenceIds));
        Assert.Equal(canonicalId, Assert.Single(Assert.Single(state.Facts).EvidenceIds));
    }

    [Fact]
    public void HistoricalOutputProvenanceSurvivesLongTextSummaryCheckpointAndContextCompaction()
    {
        const string outputId = "ev-0123456789abcdef0123456789abcdef";
        var state = CodingWorkingStateReducer.ObserveToolResult(CodingWorkingState.Create("Continue the existing project."),
            Call("read-history", "coding.readOutput", new { evidenceId = outputId, stream = "result" }),
            Json(new { status = "completed", result = new
            {
                evidenceId = outputId,
                text = new string('x', 6000),
                sourceRunId = "run-previous",
                historical = true,
            } }));
        state = JsonSerializer.Deserialize<CodingWorkingState>(Json(state), JsonSerializerOptions.Web)!;
        var evidence = Assert.Single(state.Evidence);
        Assert.True(evidence.Historical);
        Assert.Equal("run-previous", evidence.SourceRunId);
        Assert.Equal(outputId, evidence.OutputEvidenceId);
        Assert.True(evidence.Summary.Length <= 1600);
        Assert.Empty(state.Tests);
        Assert.Empty(state.ActiveFiles);

        var context = CodingEvidenceContext.Build(state).Content!;
        Assert.Contains("\"Historical\":true", context, StringComparison.Ordinal);
        Assert.Contains("\"SourceRunId\":\"run-previous\"", context, StringComparison.Ordinal);
        Assert.Contains(outputId, context, StringComparison.Ordinal);
    }

    [Fact]
    public void SuccessfulOutputReadCannotPromoteTheFailedOriginalCommandReferencedByAnAlias()
    {
        const string outputId = "ev-0123456789abcdef0123456789abcdef";
        var state = CodingWorkingStateReducer.ObserveToolResult(CodingWorkingState.Create("Verify tests."),
            Call("server-command", "coding.command", new { executable = "python" }),
            Json(new { status = "failed", result = new { success = false, exitCode = 7, evidence = new { evidenceId = outputId } } }));
        state = CodingWorkingStateReducer.ObserveToolResult(state, Call("server-read-output", "coding.readOutput", new { evidenceId = outputId }),
            Json(new { status = "completed", result = new { evidenceId = outputId, text = "actual failed test output" } }));
        var failed = Assert.Throws<ArgumentException>(() => CompleteWithAlias(state, outputId));
        Assert.Contains(outputId, failed.Message, StringComparison.Ordinal);
        Assert.Contains("keinen erfolgreichen Werkzeugabschluss", failed.Message, StringComparison.Ordinal);
        const string invented = "ev-ffffffffffffffffffffffffffffffff";
        var unknown = Assert.Throws<ArgumentException>(() => CompleteWithAlias(state, invented));
        Assert.Contains(invented, unknown.Message, StringComparison.Ordinal);
        Assert.Contains("unbekannt", unknown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AmbiguousOutputAliasesAndPlanReceiptsCannotProveCompletion()
    {
        const string outputId = "ev-0123456789abcdef0123456789abcdef";
        var state = CodingWorkingStateReducer.ObserveToolResult(CodingWorkingState.Create("Read evidence."),
            Call("server-first", "coding.read", new { path = "one.py" }), Json(new { status = "completed", evidenceId = outputId, content = "one" }));
        state = CodingWorkingStateReducer.ObserveToolResult(state, Call("server-second", "coding.read", new { path = "two.py" }),
            Json(new { status = "completed", evidenceId = outputId, content = "two" }));
        var ambiguous = Assert.Throws<ArgumentException>(() => CompleteWithAlias(state, outputId));
        Assert.Contains(outputId, ambiguous.Message, StringComparison.Ordinal);
        Assert.Contains("eindeutigen Werkzeugbeleg", ambiguous.Message, StringComparison.Ordinal);
        var planning = CodingWorkingStateReducer.ObserveToolResult(CodingWorkingState.Create("Do the work."),
            Call("server-plan", "coding.updatePlan", new { }), Json(new { status = "completed", evidenceId = outputId }));
        var plan = Assert.Throws<ArgumentException>(() => CompleteWithAlias(planning, outputId));
        Assert.Contains(outputId, plan.Message, StringComparison.Ordinal);
        Assert.Contains("Plan-Receipt", plan.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MistakenHashEvidenceIdIsNamedExactlyAndOnlyTheActualReceiptCanCompleteThePlan()
    {
        var hash = new string('a', 64);
        const string actualId = "ev-0123456789abcdef0123456789abcdef";
        var state = CodingWorkingStateReducer.ObserveToolResult(CodingWorkingState.Create("Verify the change."),
            Call("actual-edit-operation", "coding.edit", new { path = "source.py" }),
            Json(new { success = true, sha256 = hash, evidence = new { evidenceId = actualId } }));
        var mistakenId = "ev-" + hash;
        var failure = Assert.Throws<ArgumentException>(() => CompleteWithAlias(state, mistakenId));
        Assert.Contains(Json(mistakenId), failure.Message, StringComparison.Ordinal);
        Assert.Contains("eine Datei-SHA ist keine Beleg-ID", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Evidence.Id oder OutputEvidenceId", failure.Message, StringComparison.Ordinal);
        Assert.Empty(state.Plan);
        var corrected = CompleteWithAlias(state, actualId);
        Assert.Equal("actual-edit-operation", Assert.Single(Assert.Single(corrected.Plan).EvidenceIds));
    }

    [Fact]
    public void InvalidEvidenceDiagnosisIsBoundedAndEscapesUntrustedIdText()
    {
        var id = "\n\"" + new string('界', 10_000);
        var state = CodingWorkingState.Create("Use actual evidence.");
        var failure = Assert.Throws<ArgumentException>(() => CompleteWithAlias(state, id));
        Assert.True(failure.Message.Length < 1600);
        Assert.DoesNotContain('\n', failure.Message);
        Assert.Contains(JsonSerializer.Serialize(id[..160] + "… [gekürzt]"), failure.Message, StringComparison.Ordinal);
        Assert.Empty(state.Plan);
    }

    private static CodingWorkingState CompleteWithAlias(CodingWorkingState state, string alias) =>
        CodingWorkingStateReducer.ApplyPlanUpdate(state, Args(new
        { steps = new[] { new { id = "complete", title = "Verified completion", status = "completed", evidenceIds = new[] { alias } } } }));

    [Fact]
    public void IncrementalPlanUpdatesPreserveUnmentionedItemsTitlesAndCanonicalCompletionEvidence()
    {
        const string originalTitle = "Mit best\u00C3\u00A4tigtem Interpreter gepr\u00C3\u00BCft";
        const string outputId = "ev-0123456789abcdef0123456789abcdef";
        var state = CodingWorkingStateReducer.ApplyPlanUpdate(CodingWorkingState.Create("Preserve every acceptance condition."), Args(new
        {
            steps = new[] { new { id = "read", title = "Inspect source", status = "in_progress" }, new { id = "edit", title = "Correct source", status = "pending" } },
            acceptanceCriteria = new[] { new { id = "tested", title = originalTitle, status = "pending" }, new { id = "preserve", title = "Keep user files", status = "pending" } },
        }));
        state = CodingWorkingStateReducer.ObserveToolResult(state, Call("server-verification", "coding.command", new { executable = "python" }),
            Json(new { status = "completed", result = new { exitCode = 0, stdout = "2 tests passed", evidence = new { evidenceId = outputId } } }));
        string[] ids = [outputId];
        state = CodingWorkingStateReducer.ApplyPlanUpdate(state, Args(new
        {
            steps = new object[] { new { id = "read", status = "completed", evidenceIds = ids }, new { id = "edit", status = "in_progress" } },
            acceptanceCriteria = new[] { new { id = "tested", status = "completed", evidenceIds = ids } },
        }));
        Assert.Equal(2, state.Plan.Count);
        Assert.Equal("Correct source", state.Plan[1].Title);
        Assert.Equal("in_progress", state.Plan[1].Status);
        Assert.Equal(2, state.AcceptanceCriteria.Count);
        Assert.Equal(originalTitle, state.AcceptanceCriteria[0].Title);
        Assert.Equal("pending", state.AcceptanceCriteria[1].Status);
        state = CodingWorkingStateReducer.ApplyPlanUpdate(state, Args(new { acceptanceCriteria = new[] { new { id = "tested", status = "completed" } } }));
        Assert.Equal("server-verification", Assert.Single(state.AcceptanceCriteria[0].EvidenceIds));
        Assert.Throws<ArgumentException>(() => CodingWorkingStateReducer.ApplyPlanUpdate(state,
            Args(new { acceptanceCriteria = new[] { new { id = "preserve", title = "Ignore user files" } } })));
    }

    [Fact]
    public void IncrementalCompletionStillRequiresEvidenceAndNewIdsRequireTitleAndStatus()
    {
        var state = CodingWorkingStateReducer.ApplyPlanUpdate(CodingWorkingState.Create("Do real work."),
            Args(new { steps = new[] { new { id = "work", title = "Do the work", status = "pending" } } }));
        Assert.Throws<ArgumentException>(() => CodingWorkingStateReducer.ApplyPlanUpdate(state,
            Args(new { steps = new[] { new { id = "work", status = "completed" } } })));
        Assert.Throws<ArgumentException>(() => CodingWorkingStateReducer.ApplyPlanUpdate(state,
            Args(new { steps = new[] { new { id = "new", status = "pending" } } })));
        Assert.Throws<ArgumentException>(() => CodingWorkingStateReducer.ApplyPlanUpdate(state,
            Args(new { steps = new[] { new { id = "new", title = "Missing status" } } })));
        Assert.Equal("pending", Assert.Single(state.Plan).Status);
    }

    [Fact]
    public void IncrementalPlansEnforceTotalSizeAndSingleActiveStepAfterMerging()
    {
        var state = CodingWorkingStateReducer.ApplyPlanUpdate(CodingWorkingState.Create("Keep the complete plan."),
            Args(new { steps = Enumerable.Range(0, 16).Select(index => new { id = "step-" + index, title = "Step " + index, status = "pending" }).ToArray() }));
        state = CodingWorkingStateReducer.ApplyPlanUpdate(state, Args(new { steps = new[] { new { id = "step-0", status = "in_progress" } } }));
        Assert.Throws<ArgumentException>(() => CodingWorkingStateReducer.ApplyPlanUpdate(state,
            Args(new { steps = new[] { new { id = "step-1", status = "in_progress" } } })));
        Assert.Throws<ArgumentException>(() => CodingWorkingStateReducer.ApplyPlanUpdate(state,
            Args(new { steps = new[] { new { id = "new", title = "Exceeds the total limit", status = "pending" } } })));
        Assert.Equal(16, state.Plan.Count);
    }

    [Fact]
    public void IncrementalFactsAndRejectedHypothesesKeepEarlierEvidenceWithinTheBoundedWorkingWindow()
    {
        var state = CodingWorkingStateReducer.ObserveToolResult(CodingWorkingState.Create("Keep the observed facts."),
            Call("read-1", "coding.read", new { path = "source.py" }), "{\"content\":\"actual source\"}");
        state = CodingWorkingStateReducer.ApplyPlanUpdate(state, Args(new
        {
            facts = new[] { new { text = "First confirmed fact", evidenceIds = CompletedEvidenceIds } },
            rejectedHypotheses = new[] { new { text = "First rejected hypothesis", evidenceIds = CompletedEvidenceIds } },
        }));
        state = CodingWorkingStateReducer.ApplyPlanUpdate(state, Args(new
        {
            facts = new[] { new { text = "Second confirmed fact", evidenceIds = CompletedEvidenceIds } },
            rejectedHypotheses = new[] { new { text = "Second rejected hypothesis", evidenceIds = CompletedEvidenceIds } },
        }));
        Assert.Equal(2, state.Facts.Count);
        Assert.Equal(2, state.RejectedHypotheses.Count);
        Assert.Equal("First confirmed fact", state.Facts[0].Text);
        Assert.Equal("First rejected hypothesis", state.RejectedHypotheses[0].Text);
        for (var index = 0; index < 12; index++)
            state = CodingWorkingStateReducer.ApplyPlanUpdate(state, Args(new
            { facts = new[] { new { text = "New observed fact " + index, evidenceIds = CompletedEvidenceIds } } }));
        Assert.Equal(12, state.Facts.Count);
        Assert.Equal("New observed fact 11", state.Facts[^1].Text);
        Assert.Equal(2, state.RejectedHypotheses.Count);
    }

    [Fact]
    public void ReadableOutputIdsStaySeparateFromReplayAndPlanCallIdsAcrossCheckpointRecovery()
    {
        const string outputId = "ev-0123456789abcdef0123456789abcdef";
        var call = Call("read-1", "coding.read", new { path = "source.py" });
        var result = Json(new { status = "completed", result = new { evidence = new { evidenceId = outputId }, path = "source.py", content = "actual source", sha256 = new string('a', 64) } });
        var state = CodingWorkingStateReducer.ObserveToolResult(CodingWorkingState.Create("Read the original evidence."), call, result);
        state = JsonSerializer.Deserialize<CodingWorkingState>(Json(state), JsonSerializerOptions.Web)!;
        Assert.Equal(call.Id, Assert.Single(state.Evidence).Id);
        Assert.Equal(outputId, state.Evidence[0].OutputEvidenceId);
        Assert.Equal(call.Id, Assert.Single(state.ActiveFiles).EvidenceId);
        Assert.Equal(outputId, state.ActiveFiles[0].OutputEvidenceId);
        Assert.Same(state, CodingWorkingStateReducer.ObserveToolResult(state, call, result));
        state = CodingWorkingStateReducer.ApplyPlanUpdate(state, Args(new
        { steps = new[] { new { id = "read", title = "Inspect source", status = "completed", evidenceIds = CompletedEvidenceIds } } }));
        Assert.Contains(outputId, CodingEvidenceContext.Build(state).Content, StringComparison.Ordinal);
        var withoutOutputId = CodingWorkingStateReducer.ObserveToolResult(state, call with { Id = "read-2" }, "{\"content\":\"source\",\"evidenceId\":\"not-an-output-id\"}");
        Assert.Null(withoutOutputId.Evidence[^1].OutputEvidenceId);
    }

    [Fact]
    public void PlanCompletionRequiresRealSuccessfulEvidenceAndKeepsOriginalAcceptanceCriteria()
    {
        var state = CodingWorkingState.Create("Original task: preserve user data and verify behavior.");
        state = CodingWorkingStateReducer.ObserveToolResult(state, Call("read-1", "coding.read", new { path = "source.py" }),
            Json(new { path = "source.py", content = "1: actual source", sha256 = new string('a', 64), startLine = 1 }));
        state = CodingWorkingStateReducer.ObserveToolResult(state, Call("failed-1", "coding.command", new { executable = "python", arguments = Array.Empty<string>() }),
            Json(new { success = false, exitCode = 7, stderr = "actual failure" }));
        Assert.Throws<ArgumentException>(() => CodingWorkingStateReducer.ApplyPlanUpdate(state, Args(new
        { steps = new[] { new { id = "one", title = "Inspect source", status = "completed" } } })));
        foreach (var evidenceIds in new[] { UnknownEvidenceIds, FailedEvidenceIds })
            Assert.Throws<ArgumentException>(() => CodingWorkingStateReducer.ApplyPlanUpdate(state, Args(new
            { steps = new[] { new { id = "one", title = "Inspect source", status = "completed", evidenceIds } } })));
        state = CodingWorkingStateReducer.ApplyPlanUpdate(state, Args(new
        {
            steps = new[] { new { id = "one", title = "Inspect source", status = "completed", evidenceIds = CompletedEvidenceIds } },
            acceptanceCriteria = new[] { new { id = "preserve", title = "Preserve user data", status = "pending" } },
            nextStep = "Change only the confirmed source line", phase = "editing",
        }));
        Assert.Equal("completed", Assert.Single(state.Plan).Status);
        Assert.Equal("read-1", Assert.Single(state.Plan[0].EvidenceIds));
        var extended = CodingWorkingStateReducer.ApplyPlanUpdate(state, Args(new
        { acceptanceCriteria = new[] { new { id = "additional", title = "An additional check", status = "pending" } } }));
        Assert.Equal(2, extended.AcceptanceCriteria.Count);
        Assert.Contains(extended.AcceptanceCriteria, item => item.Id == "preserve" && item.Title == "Preserve user data");
        Assert.Throws<ArgumentException>(() => CodingWorkingStateReducer.ApplyPlanUpdate(state, Args(new
        { acceptanceCriteria = new[] { new { id = "preserve", title = "A different easier target", status = "pending" } } })));
        var restored = JsonSerializer.Deserialize<CodingWorkingState>(Json(state), JsonSerializerOptions.Web)!;
        Assert.Equal(state.OriginalTask, restored.OriginalTask);
        Assert.Equal(state.NextStep, restored.NextStep);
        Assert.Equal("Preserve user data", Assert.Single(restored.AcceptanceCriteria).Title);
    }

    [Fact]
    public void ErrorSignaturesSurviveCheckpointAndCompactionButReceiptReplayIsIdempotent()
    {
        var state = CodingWorkingState.Create("Inspect missing source.");
        var first = Call("fail-1", "coding.read", new { path = "missing.py", maximumLines = 10 });
        var second = Call("fail-2", "coding.read", new { maximumLines = 10, path = "missing.py" });
        const string failure = "{\"status\":\"failed\",\"errorCode\":\"file.not_found\",\"result\":null}";
        state = CodingWorkingStateReducer.ObserveToolResult(state, first, failure);
        state = CodingWorkingStateReducer.ObserveToolResult(state, second, failure);
        var replayed = CodingWorkingStateReducer.ObserveToolResult(state, second, failure);
        Assert.Same(state, replayed);
        Assert.Equal(2, Assert.Single(state.Failures).Count);
        state = JsonSerializer.Deserialize<CodingWorkingState>(Json(state), JsonSerializerOptions.Web)!;
        Assert.Throws<AgentRunLimitException>(() => CodingLoopGuard.ThrowIfRepeatedFailure([], first with { Id = "third" }, state));
        CodingLoopGuard.ThrowIfRepeatedFailure([], Call("other", "coding.read", new { path = "another.py" }), state);
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("environment")]
    [InlineData("file")]
    public void ChangedFilesOrEnvironmentInvalidateOldFailures(string change)
    {
        var state = CodingWorkingState.Create("Repair and retry.");
        state = CodingWorkingStateReducer.ObserveToolResult(state, Call("env-0", "coding.list", new { }),
            Json(new { entries = Array.Empty<string>(), environment = new { pythonExists = false } }));
        state = CodingWorkingStateReducer.ObserveToolResult(state, Call("read-0", "coding.read", new { path = "source.py" }),
            Json(new { content = "1: original", sha256 = new string('a', 64), path = "source.py" }));
        var candidate = Call("fail-1", "coding.read", new { path = "missing.py" });
        state = CodingWorkingStateReducer.ObserveToolResult(state, candidate, "{\"success\":false,\"message\":\"not found\"}");
        state = CodingWorkingStateReducer.ObserveToolResult(state, candidate with { Id = "fail-2" }, "{\"success\":false,\"message\":\"not found\"}");
        var before = state.EnvironmentRevision;
        state = change switch
        {
            "edit" => CodingWorkingStateReducer.ObserveToolResult(state, Call("changed", "coding.edit", new { path = "source.py" }), Json(new { success = true, path = "source.py", sha256 = new string('b', 64) })),
            "environment" => CodingWorkingStateReducer.ObserveToolResult(state, Call("changed", "coding.list", new { }), Json(new { entries = Array.Empty<string>(), environment = new { pythonExists = true } })),
            _ => CodingWorkingStateReducer.ObserveToolResult(state, Call("changed", "coding.read", new { path = "source.py" }), Json(new { content = "1: external change", path = "source.py", sha256 = new string('c', 64) })),
        };
        Assert.True(state.EnvironmentRevision > before);
        Assert.Empty(state.Failures);
        CodingLoopGuard.ThrowIfRepeatedFailure([], candidate with { Id = "retry" }, state);
    }

    [Fact]
    public void SuccessfulUnrelatedTestPreservesMissingInterpreterFailureAcrossCompactionAndMatchingSuccessClearsOnlyItsOwnFailure()
    {
        const string missing = "{\"status\":\"failed\",\"errorCode\":\"client.tool_failed\",\"message\":\"Interpreter not found\",\"result\":{\"failed\":true}}";
        var call = Call("missing-1", "coding.command", new { executable = "missing-python.exe", arguments = MissingInterpreterArguments });
        var state = CodingWorkingStateReducer.ObserveToolResult(CodingWorkingState.Create("Fix and verify the project."), call, missing);
        var revision = state.EnvironmentRevision;
        state = CodingWorkingStateReducer.ObserveToolResult(state,
            Call("successful-tests", "coding.command", new { executable = "available-python.exe", arguments = VerificationArguments }),
            "{\"success\":true,\"exitCode\":0,\"stdout\":\"Tests passed\"}");
        Assert.Equal(revision, state.EnvironmentRevision);
        Assert.Equal("missing-1", Assert.Single(state.Failures).EvidenceId);

        var compacted = CodingEvidenceContext.CompactCompletedCalls(
            [new("assistant", ToolCalls: [call]), new("tool", missing, ToolCallId: call.Id)]);
        Assert.True(compacted[0].ToolCalls![0].Arguments.GetProperty("_goCompletedArguments").GetBoolean());
        state = JsonSerializer.Deserialize<CodingWorkingState>(Json(state), JsonSerializerOptions.Web)!;
        state = CodingWorkingStateReducer.ObserveToolResult(state, call with { Id = "missing-2" }, missing);
        Assert.Equal(2, Assert.Single(state.Failures).Count);
        Assert.Throws<AgentRunLimitException>(() => CodingLoopGuard.ThrowIfRepeatedFailure(compacted, call with { Id = "missing-3" }, state));

        state = CodingWorkingStateReducer.ObserveToolResult(state, Call("other-failure", "coding.read", new { path = "missing-file.py" }),
            "{\"status\":\"failed\",\"errorCode\":\"file.not_found\"}");
        state = CodingWorkingStateReducer.ObserveToolResult(state, call with { Id = "interpreter-now-available" },
            "{\"success\":true,\"exitCode\":0,\"stdout\":\"Now available\"}");
        Assert.Equal(revision, state.EnvironmentRevision);
        Assert.Equal("other-failure", Assert.Single(state.Failures).EvidenceId);
        CodingLoopGuard.ThrowIfRepeatedFailure(compacted, call with { Id = "allowed-after-success" }, state);
    }

    [Fact]
    public void SuccessfulRepeatedCommandsRemainDistinctReceiptsAndFailedCommandsInvalidateSnippets()
    {
        var state = CodingWorkingState.Create("Run the verification when needed.");
        state = CodingWorkingStateReducer.ObserveToolResult(state, Call("read", "coding.read", new { path = "source.py" }),
            Json(new { content = "10: before\n11: target\n12: after", path = "source.py", startLine = 10, sha256 = new string('a', 64) }));
        Assert.Equal("11: target", CodingEvidenceContext.SelectSnippet(Assert.Single(state.ActiveFiles), 11, 1));
        var command = Call("command-1", "coding.command", new { executable = "python", arguments = VerificationArguments });
        const string result = "{\"success\":true,\"exitCode\":0,\"stdout\":\"3 tests passed\",\"stderr\":\"\"}";
        state = CodingWorkingStateReducer.ObserveToolResult(state, command, result);
        state = CodingWorkingStateReducer.ObserveToolResult(state, command with { Id = "command-2" }, result);
        Assert.Equal(2, state.Tests.Count);
        Assert.Empty(state.Failures);
        Assert.True(Assert.Single(state.ActiveFiles).NeedsRead);
        Assert.Empty(CodingEvidenceContext.SelectSnippet(state.ActiveFiles[0], 10));
        state = CodingWorkingStateReducer.ObserveToolResult(state, command with { Id = "command-3" }, "{\"success\":false,\"exitCode\":7,\"stderr\":\"failed after partial changes\"}");
        Assert.Equal(3, state.Tests.Count);
        Assert.Equal(1, Assert.Single(state.Failures).Count);
    }

    [Fact]
    public void UnknownOrNullResultFieldsDoNotInventFailureOrSuccessAndPlanReceiptsCannotProveCompletion()
    {
        var state = CodingWorkingState.Create("Read evidence.");
        state = CodingWorkingStateReducer.ObserveToolResult(state, Call("unknown", "coding.gitDiff", new { }), "{\"exitCode\":null,\"result\":null}");
        Assert.False(Assert.Single(state.Evidence).Success);
        Assert.Empty(state.Failures);
        state = CodingWorkingStateReducer.ObserveToolResult(state, Call("plan", "coding.updatePlan", new { }), "{\"success\":true}");
        Assert.Throws<ArgumentException>(() => CodingWorkingStateReducer.ApplyPlanUpdate(state, Args(new
        { steps = new[] { new { id = "work", title = "Everything complete", status = "completed", evidenceIds = PlanEvidenceIds } } })));
        var call = Call("git", "coding.gitDiff", new { });
        CodingLoopGuard.ThrowIfRepeatedFailure([new("assistant", ToolCalls: [call]), new("tool", "{\"exitCode\":null}", ToolCallId: "git")], call);
    }

    [Theory]
    [InlineData("{\"outcomeUnknown\":true}")]
    [InlineData("{\"status\":\"cancelled\",\"result\":null}")]
    [InlineData("{\"status\":\"failed\",\"result\":{\"outcomeUnknown\":true,\"exitCode\":null}}")]
    [InlineData("{\"status\":\"failed\",\"errorCode\":\"client.tool_failed\",\"result\":{\"failed\":true}}")]
    public void PossiblyExecutedCommandWithoutExitCodeInvalidatesSnippetsWithoutInventingSuccess(string receipt)
    {
        var state = CodingWorkingStateReducer.ObserveToolResult(CodingWorkingState.Create("Inspect source."),
            Call("read", "coding.read", new { path = "source.py" }), Json(new { content = "before", sha256 = new string('a', 64) }));
        state = CodingWorkingStateReducer.ObserveToolResult(state, Call("old-failure", "coding.read", new { path = "missing.py" }),
            "{\"status\":\"failed\",\"errorCode\":\"file.not_found\"}");
        var revision = state.EnvironmentRevision;
        state = CodingWorkingStateReducer.ObserveToolResult(state, Call("command", "coding.command", new { executable = "python" }), receipt);
        var file = Assert.Single(state.ActiveFiles);
        Assert.True(file.NeedsRead);
        Assert.Empty(file.Snippet);
        Assert.Equal(new string('a', 64), file.Sha256);
        Assert.Equal(revision, state.EnvironmentRevision);
        Assert.Empty(state.Tests);
        Assert.False(state.Evidence[^1].Success);
        Assert.Contains(state.Failures, failure => failure.EvidenceId == "old-failure" && failure.Count == 1);
    }

    [Theory]
    [InlineData("{\"status\":\"rejected\",\"result\":null}")]
    [InlineData("{\"status\":\"failed\",\"errorCode\":\"agent.invalid_tool_call\"}")]
    public void ProvenUnexecutedCommandDoesNotInvalidateAReadFile(string receipt)
    {
        var state = CodingWorkingStateReducer.ObserveToolResult(CodingWorkingState.Create("Inspect source."),
            Call("read", "coding.read", new { path = "source.py" }), Json(new { content = "before", sha256 = new string('a', 64) }));
        state = CodingWorkingStateReducer.ObserveToolResult(state, Call("denied-command", "coding.command", new { executable = "python" }), receipt);
        var file = Assert.Single(state.ActiveFiles);
        Assert.False(file.NeedsRead);
        Assert.Equal("before", file.Snippet);
        Assert.Equal(new string('a', 64), file.Sha256);
    }

    [Fact]
    public void WorkingStateIsBoundedAndKeepsEvidenceReferencedByACompletedPlan()
    {
        var state = CodingWorkingState.Create(new string('u', 50_000) + "DO_NOT_DROP_FINAL_REQUIREMENT");
        state = CodingWorkingStateReducer.ObserveToolResult(state, Call("read-1", "coding.read", new { path = "critical.py" }), "{\"content\":\"1: verified\"}");
        state = CodingWorkingStateReducer.ApplyPlanUpdate(state, Args(new
        { steps = new[] { new { id = "critical", title = "Inspect the critical source", status = "completed", evidenceIds = CompletedEvidenceIds } } }));
        for (var index = 0; index < 300; index++)
            state = CodingWorkingStateReducer.ObserveToolResult(state, Call("read-" + (index + 2), "coding.read", new { path = "file-" + index }),
                Json(new { path = "file-" + index, content = new string('x', 8000), sha256 = new string('a', 64) }));
        Assert.True(state.Evidence.Count <= CodingWorkingStateReducer.MaximumEvidence);
        Assert.True(state.ActiveFiles.Count <= CodingWorkingStateReducer.MaximumActiveFiles);
        Assert.Contains(state.Evidence, evidence => evidence.Id == "read-1");
        Assert.EndsWith("DO_NOT_DROP_FINAL_REQUIREMENT", state.OriginalTask, StringComparison.Ordinal);
        var context = CodingEvidenceContext.Build(state, 2048);
        Assert.Equal("user", context.Role);
        Assert.True(context.Content!.Length <= 2048);
        Assert.Contains("keine neue Anweisung", context.Content, StringComparison.Ordinal);
    }

    private static LmToolCall Call(string id, string tool, object arguments) => new(id, tool, Args(arguments));
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
    private static string Json(object value) => JsonSerializer.Serialize(value, JsonSerializerOptions.Web);
}
