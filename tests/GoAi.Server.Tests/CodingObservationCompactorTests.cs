using GoAi.Contracts;
using GoAi.Server.Core.Runs;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class CodingObservationCompactorTests
{
    private static readonly string[] DotNetTestArguments = ["test"];
    private static readonly string[] SuggestedPaths = ["src/App.cs", "src/App.xaml.cs"];

    [Fact]
    public void WorkspaceReadRetainsEvidenceAndVersionButOmitsPersistedSource()
    {
        var action = Action(
            CodingAgentToolFacade.WorkspaceInspect,
            "read",
            new { path = "src/App.cs", startLine = 10, endLine = 30 });
        var result = JsonSerializer.SerializeToElement(new
        {
            fileId = "file-1",
            evidenceId = "evidence-1",
            path = "src/App.cs",
            sha256 = new string('a', 64),
            workspaceRevision = 7,
            startLine = 10,
            endLine = 30,
            text = "public sealed class App { }",
            cacheHit = true,
        });
        var observation = Observation(action, result, succeeded: true);

        var compacted = CodingObservationCompactor.CompactResult(action, observation);

        Assert.Equal("evidence-1", compacted.GetProperty("evidenceId").GetString());
        Assert.Equal("src/App.cs", compacted.GetProperty("path").GetString());
        Assert.Equal(new string('a', 64), compacted.GetProperty("sha256").GetString());
        Assert.True(compacted.GetProperty("cacheHit").GetBoolean());
        Assert.True(compacted.GetProperty("text").GetProperty("omitted").GetBoolean());
        Assert.True(compacted.GetProperty("_goCompaction").GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void ProcessObservationPreservesExitCodeAndHeadAndTailDiagnostics()
    {
        var action = Action(
            CodingAgentToolFacade.ExecutionRun,
            "command",
            new { executable = "dotnet", arguments = DotNetTestArguments, purpose = "test" });
        var output = "BEGIN\n" + new string('x', 20_000) + "\nFINAL-FAILURE";
        var result = JsonSerializer.SerializeToElement(new
        {
            exitCode = 1,
            standardOutput = output,
            standardError = "tests/AppTests.cs(42,3): error CS1002",
        });

        var compacted = CodingObservationCompactor.CompactResult(
            action,
            Observation(action, result, succeeded: false));

        Assert.Equal(1, compacted.GetProperty("exitCode").GetInt32());
        var compactOutput = compacted.GetProperty("standardOutput").GetString();
        Assert.NotNull(compactOutput);
        Assert.Contains("BEGIN", compactOutput, StringComparison.Ordinal);
        Assert.Contains("FINAL-FAILURE", compactOutput, StringComparison.Ordinal);
        Assert.Contains("tests/AppTests.cs(42,3)", compacted.GetProperty("standardError").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void MissingPathRecoveryDataSurvivesCompaction()
    {
        var action = Action(
            CodingAgentToolFacade.WorkspaceInspect,
            "read",
            new { path = "src/Aqp.cs" });
        var result = JsonSerializer.SerializeToElement(new
        {
            failed = true,
            reason = "path_not_found",
            requestedPath = "src/Aqp.cs",
            suggestedPaths = SuggestedPaths,
            recoveryTool = "fs.findFiles",
            repositoryRevision = "revision-2",
        });

        var compacted = CodingObservationCompactor.CompactResult(
            action,
            Observation(action, result, succeeded: false));

        Assert.Equal("path_not_found", compacted.GetProperty("reason").GetString());
        Assert.Equal("src/Aqp.cs", compacted.GetProperty("requestedPath").GetString());
        Assert.Equal(2, compacted.GetProperty("suggestedPaths").GetArrayLength());
        Assert.Equal("fs.findFiles", compacted.GetProperty("recoveryTool").GetString());
    }

    [Fact]
    public void MutationArgumentsRetainVersionAndTargetButNotLargePayload()
    {
        var action = Action(
            CodingAgentToolFacade.WorkspaceChange,
            "write",
            new
            {
                path = "src/App.cs",
                expectedSha256 = new string('b', 64),
                content = new string('c', 10_000),
            });

        var compacted = CodingObservationCompactor.CompactArguments(action);

        Assert.Equal("src/App.cs", compacted.GetProperty("path").GetString());
        Assert.Equal(new string('b', 64), compacted.GetProperty("expectedSha256").GetString());
        Assert.True(compacted.GetProperty("content").GetProperty("omitted").GetBoolean());
    }

    [Fact]
    public void MutationArgumentsNeverRepeatEvenSmallSourcePayloads()
    {
        var action = Action(
            CodingAgentToolFacade.WorkspaceChange,
            "create",
            new { path = "README.md", content = "Kurzer bereits geschriebener Inhalt." });

        var compacted = CodingObservationCompactor.CompactArguments(action);

        Assert.Equal("README.md", compacted.GetProperty("path").GetString());
        Assert.True(compacted.GetProperty("content").GetProperty("omitted").GetBoolean());
    }

    [Fact]
    public void CurrentModelObservationKeepsBoundedSourceButPersistenceOmitsIt()
    {
        var action = Action(
            CodingAgentToolFacade.WorkspaceInspect,
            "read",
            new { path = "src/App.cs" });
        var source = "BEGIN\n" + new string('x', 12_000) + "\nEND";
        var result = JsonSerializer.SerializeToElement(new
        {
            path = "src/App.cs",
            sha256 = new string('a', 64),
            text = source,
        });
        var observation = Observation(action, result, succeeded: true);

        var forModel = CodingObservationCompactor.CompactResultForModel(observation);
        var persisted = CodingObservationCompactor.CompactResult(action, observation);

        var modelText = forModel.GetProperty("text").GetString();
        Assert.NotNull(modelText);
        Assert.True(modelText.Length <= 8_300);
        Assert.Contains("BEGIN", modelText, StringComparison.Ordinal);
        Assert.Contains("END", modelText, StringComparison.Ordinal);
        Assert.True(persisted.GetProperty("text").GetProperty("omitted").GetBoolean());
    }

    [Fact]
    public void PersistedWebFetchKeepsABoundedExtractForFollowingAgentTurns()
    {
        var action = Action(
            CodingAgentToolFacade.ResearchQuery,
            "webFetch",
            new { operation = "webFetch", url = "https://example.test/mechanics" });
        var content = "MECHANICS-BEGIN\n" + new string('x', 12_000) + "\nMECHANICS-END";
        var result = JsonSerializer.SerializeToElement(new
        {
            url = "https://example.test/mechanics",
            mediaType = "text/html",
            content,
        });

        var persisted = CodingObservationCompactor.CompactResult(
            action,
            Observation(action, result, succeeded: true));

        var persistedContent = persisted.GetProperty("content").GetString();
        Assert.NotNull(persistedContent);
        Assert.True(persistedContent.Length <= 8_300);
        Assert.Contains("MECHANICS-BEGIN", persistedContent, StringComparison.Ordinal);
        Assert.Contains("MECHANICS-END", persistedContent, StringComparison.Ordinal);
    }

    private static AgentActionEnvelope Action(string tool, string operation, object arguments) => new(
        "action-1",
        1,
        tool,
        operation,
        JsonSerializer.SerializeToElement(arguments),
        "idempotency-1",
        "revision-1",
        DateTimeOffset.UtcNow);

    private static AgentObservation Observation(
        AgentActionEnvelope action,
        JsonElement result,
        bool succeeded) => new(
            action.ActionId,
            succeeded,
            result,
            new string('f', 64),
            succeeded ? null : "tool.failed",
            succeeded ? null : "Tool failed.",
            "evidence-1",
            "revision-1",
            false,
            DateTimeOffset.UtcNow);
}
