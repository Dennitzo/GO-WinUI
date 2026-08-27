using GoAi.Contracts;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runs;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class CodingAgentToolFacadeTests
{
    [Fact]
    public void ModelSurfaceContainsExactlySixStableToolsInFixedOrder()
    {
        Assert.Equal(
            [
                CodingAgentToolFacade.WorkspaceInspect,
                CodingAgentToolFacade.WorkspaceChange,
                CodingAgentToolFacade.ExecutionRun,
                CodingAgentToolFacade.ResearchQuery,
                CodingAgentToolFacade.ArtifactProcess,
                CodingAgentToolFacade.TaskFinish,
            ],
            CodingAgentToolFacade.Definitions.Select(static tool => tool.Name));
    }

    [Fact]
    public void SingleFileReadResolvesToVersionedLocalRead()
    {
        var dispatch = Resolve(
            CodingAgentToolFacade.WorkspaceInspect,
            """{"operation":"read","path":"src/App.cs","startLine":10,"endLine":30}""");

        Assert.Equal(ClientToolNames.FileSystemReadText, dispatch.UnderlyingTool);
        Assert.Equal("src/App.cs", dispatch.Arguments.GetProperty("path").GetString());
        Assert.Equal(10, dispatch.Arguments.GetProperty("startLine").GetInt32());
        Assert.Equal(30, dispatch.Arguments.GetProperty("endLine").GetInt32());
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/workspace")]
    [InlineData("workspace")]
    [InlineData(".")]
    public void WorkspaceRootAliasesResolveToTheBoundWorkspace(string modelPath)
    {
        var dispatch = Resolve(
            CodingAgentToolFacade.WorkspaceInspect,
            JsonSerializer.Serialize(new { operation = "list", path = modelPath }));

        Assert.Equal(ClientToolNames.FileSystemList, dispatch.UnderlyingTool);
        Assert.Equal(".", dispatch.Arguments.GetProperty("path").GetString());
    }

    [Fact]
    public void WorkspaceListDefaultsToTheBoundWorkspaceWhenPathIsOmitted()
    {
        var dispatch = Resolve(CodingAgentToolFacade.WorkspaceInspect, """{"operation":"list"}""");

        Assert.Equal(".", dispatch.Arguments.GetProperty("path").GetString());
    }

    [Fact]
    public void CommentaryIsAvailableToTheOrchestratorButNeverForwardedToTheUnderlyingTool()
    {
        var dispatch = Resolve(
            CodingAgentToolFacade.WorkspaceInspect,
            """{"operation":"read","path":"src/App.cs","commentary":"Ich prüfe jetzt die zentrale Datei und wähle danach die kleinste Änderung."}""");

        Assert.Equal(
            "Ich prüfe jetzt die zentrale Datei und wähle danach die kleinste Änderung.",
            dispatch.Commentary);
        Assert.False(dispatch.Arguments.TryGetProperty("commentary", out _));
        Assert.All(
            CodingAgentToolFacade.Definitions,
            definition => Assert.True(AllSchemaVariantsContainCommentary(definition.Parameters)));
    }

    [Fact]
    public void WorkspaceChangeSchemaExplainsOperationSpecificPayloads()
    {
        var schema = CodingAgentToolFacade.Definitions
            .Single(static definition => definition.Name == CodingAgentToolFacade.WorkspaceChange)
            .Parameters;
        var properties = schema.GetProperty("properties");

        Assert.Contains("create: neue Datei mit content", properties.GetProperty("operation").GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("Nur für replace", properties.GetProperty("oldText").GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("Für jede bestehende Quelldatei erforderlich", properties.GetProperty("expectedSha256").GetProperty("description").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ResearchSchemaRequiresAnExplicitContextGapForAReplacementSearch()
    {
        var schema = CodingAgentToolFacade.Definitions
            .Single(static definition => definition.Name == CodingAgentToolFacade.ResearchQuery)
            .Parameters;
        var contextGap = schema.GetProperty("properties").GetProperty("contextGap");

        Assert.Equal(12, contextGap.GetProperty("minLength").GetInt32());
        Assert.Contains("fehlende Information", contextGap.GetProperty("description").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void UnambiguousLmStudioChangeAliasesAreNormalizedSafely()
    {
        var create = Resolve(
            CodingAgentToolFacade.WorkspaceChange,
            """{"operation":"create","path":"README.md","newText":"Test"}""");
        Assert.Equal(ClientToolNames.FileSystemProposeCreate, create.UnderlyingTool);
        Assert.Equal("Test", create.Arguments.GetProperty("content").GetString());

        var write = Resolve(
            CodingAgentToolFacade.WorkspaceChange,
            """{"operation":"replace","path":"README.md","newText":"Vollständiger Inhalt","expectedSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""");
        Assert.Equal("write", write.Operation);
        Assert.Equal(ClientToolNames.FileSystemWriteText, write.UnderlyingTool);
        Assert.Equal("Vollständiger Inhalt", write.Arguments.GetProperty("content").GetString());
    }

    [Fact]
    public void BoundedMultiReadRemainsAnInternalTransport()
    {
        var dispatch = Resolve(
            CodingAgentToolFacade.WorkspaceInspect,
            """{"operation":"read","paths":[{"path":"a.cs"},{"path":"b.cs","startLine":3,"endLine":8}],"maximumCharacters":12000}""");

        Assert.Equal(ClientToolNames.FileSystemReadMany, dispatch.UnderlyingTool);
        Assert.Equal(2, dispatch.Arguments.GetProperty("items").GetArrayLength());
        Assert.Equal(12_000, dispatch.Arguments.GetProperty("maximumCharacters").GetInt32());
        Assert.DoesNotContain(
            CodingAgentToolFacade.Definitions,
            static definition => string.Equals(definition.Name, ClientToolNames.FileSystemReadMany, StringComparison.Ordinal));
    }

    [Fact]
    public void ExistingFileWriteRequiresExpectedVersion()
    {
        var exception = Assert.Throws<ArgumentException>(() => Resolve(
            CodingAgentToolFacade.WorkspaceChange,
            """{"operation":"write","path":"src/App.cs","content":"changed"}"""));

        Assert.Contains("expectedSha256", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("move", "\"destination\":\"archive/App.cs\"")]
    [InlineData("delete", "")]
    public void MoveAndDeleteRequireExpectedVersion(string operation, string additionalArgument)
    {
        var separator = additionalArgument.Length == 0 ? string.Empty : "," + additionalArgument;
        var exception = Assert.Throws<ArgumentException>(() => Resolve(
            CodingAgentToolFacade.WorkspaceChange,
            $"{{\"operation\":\"{operation}\",\"path\":\"src/App.cs\"{separator}}}"));

        Assert.Contains("expectedSha256", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FinishDoesNotResolveToAnImplementationTool()
    {
        var dispatch = Resolve(
            CodingAgentToolFacade.TaskFinish,
            """{"status":"completed","summary":"Geprüft abgeschlossen."}""");

        Assert.True(dispatch.IsFinish);
        Assert.Null(dispatch.UnderlyingTool);
        Assert.Null(dispatch.Spec);
    }

    [Fact]
    public void CodingAgentCannotUseLeanStatusAsAProofSubstitute()
    {
        var exception = Assert.Throws<ArgumentException>(() => Resolve(
            CodingAgentToolFacade.ExecutionRun,
            """{"operation":"lean","leanOperation":"status"}"""));

        Assert.Contains("nicht zulässig", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LeanVerificationRequiresAConcreteWorkspaceFile()
    {
        var exception = Assert.Throws<ArgumentException>(() => Resolve(
            CodingAgentToolFacade.ExecutionRun,
            """{"operation":"lean","leanOperation":"verify","theoremName":"Book.force_law"}"""));

        Assert.Contains("target", exception.Message, StringComparison.Ordinal);
    }

    private static CodingToolDispatch Resolve(string name, string arguments)
    {
        using var document = JsonDocument.Parse(arguments);
        var catalog = new AgentToolCatalog();
        var request = new RunRequest(
            GoAiProtocol.Version,
            RunMode.Code,
            [new RunMessage("user", [new ContentPart("text", "Bearbeite das Projekt")])],
            ClientCapabilities: ["code", "filesystem", "process", "documentIo"]);
        return CodingAgentToolFacade.Resolve(
            new LmToolCall("call-1", name, document.RootElement.Clone()),
            catalog.GetCodingV2ImplementationTools(request));
    }

    private static bool AllSchemaVariantsContainCommentary(JsonElement schema)
    {
        if (schema.TryGetProperty("properties", out var properties))
        {
            return properties.TryGetProperty("commentary", out _);
        }
        return schema.TryGetProperty("oneOf", out var variants)
            && variants.EnumerateArray().All(static variant =>
                variant.GetProperty("properties").TryGetProperty("commentary", out _));
    }
}
