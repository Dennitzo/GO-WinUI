using GoAi.Contracts;
using GoAi.Server.Core.Runs;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class CodingCommentaryTests
{
    [Fact]
    public void NormalizerAllowsShortMilestoneTextButRejectsReasoningAndStructuredPayloads()
    {
        Assert.Equal(
            "Die Ursache ist eingegrenzt. Ich ändere jetzt die betroffene Validierung und führe danach den Test erneut aus.",
            CodingCommentary.Normalize(
                "  Die Ursache ist eingegrenzt.  Ich ändere jetzt die betroffene Validierung und führe danach den Test erneut aus.  "));
        Assert.Null(CodingCommentary.Normalize("Analysis: Ich denke Schritt für Schritt über alle Alternativen nach."));
        Assert.Null(CodingCommentary.Normalize("{\"operation\":\"read\"}"));
        Assert.Null(CodingCommentary.Normalize("- Datei lesen\n- Datei ändern"));
    }

    [Fact]
    public void CommentaryDeltasAreBoundedAndReconstructTheAuthoritativeText()
    {
        var text = string.Join(' ', Enumerable.Repeat(
            "Die Änderung ist gespeichert und wird nun mit dem passenden Projekttest geprüft.",
            7));

        var deltas = CodingCommentary.Split(text);

        Assert.InRange(deltas.Count, 1, CodingCommentary.MaximumDeltas);
        Assert.Equal(text, string.Concat(deltas));
    }

    [Fact]
    public void HostFallbackContainsNoToolArgumentsOrInternalReasoning()
    {
        using var arguments = JsonDocument.Parse("""{"operation":"read","path":"src/App.cs"}""");
        var dispatch = new CodingToolDispatch(
            CodingAgentToolFacade.WorkspaceInspect,
            "read",
            ClientToolNames.FileSystemReadText,
            arguments.RootElement.Clone(),
            null,
            false,
            null);
        var ledger = new TaskLedgerSnapshot(
            "Behebe den Fehler.",
            CodingTaskKind.Change,
            CodingAgentPhase.Orienting,
            "revision-1",
            [],
            [],
            [],
            [],
            [],
            "Datei prüfen.");

        var fallback = CodingCommentary.CreateFallback(
            new CodingCommentaryDirective(true, "initial", "initial:revision-1"),
            ledger,
            null,
            null,
            dispatch);

        Assert.NotNull(CodingCommentary.Normalize(fallback));
        Assert.DoesNotContain("{\"", fallback, StringComparison.Ordinal);
        Assert.DoesNotContain("Gedankengang", fallback, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("workspace.inspect", "read", true, false)]
    [InlineData("workspace.change", "write", true, false)]
    [InlineData("execution.run", "command", false, false)]
    [InlineData("execution.run", "command", true, true)]
    [InlineData("execution.run", "lean", true, true)]
    [InlineData("research.query", "webSearch", true, true)]
    [InlineData("artifact.process", "documentCreate", true, true)]
    public void OnlySuccessfulUserRelevantResultsBecomeVisibleMilestones(
        string tool,
        string operation,
        bool succeeded,
        bool expectedVisible)
    {
        var arguments = tool == "execution.run" && operation == "command"
            ? JsonSerializer.SerializeToElement(new { operation, purpose = "test", target = "tests/App.Tests.csproj" })
            : tool == "execution.run" && operation == "lean"
                ? JsonSerializer.SerializeToElement(new { operation, leanOperation = "verify", target = "proofs/Test.lean" })
                : JsonSerializer.SerializeToElement(new { operation, path = "README.md", query = "aktuelle Dokumentation" });
        var action = new AgentActionEnvelope(
            "action-1", 1, tool, operation, arguments, "idempotency", "revision-1", DateTimeOffset.UtcNow);
        var observation = new AgentObservation(
            action.ActionId,
            succeeded,
            JsonSerializer.SerializeToElement(new { success = succeeded }),
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            succeeded ? null : "client.tool_failed",
            succeeded ? null : "fehlgeschlagen",
            succeeded ? "evidence-1" : null,
            "revision-1",
            false,
            DateTimeOffset.UtcNow);

        var summary = CodingAgentOrchestrator.CreateMilestoneSummary(action, observation);

        Assert.Equal(expectedVisible, summary is not null);
    }

    [Fact]
    public void LeanStatusDoesNotBecomeAProofMilestone()
    {
        var arguments = JsonSerializer.SerializeToElement(new
        {
            operation = "lean",
            leanOperation = "status",
        });
        var action = new AgentActionEnvelope(
            "action-1", 1, CodingAgentToolFacade.ExecutionRun, "lean",
            arguments, "idempotency", "revision-1", DateTimeOffset.UtcNow);
        var observation = new AgentObservation(
            action.ActionId,
            true,
            JsonSerializer.SerializeToElement(new { success = true }),
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

        Assert.Null(CodingAgentOrchestrator.CreateMilestoneSummary(action, observation));
    }
}
