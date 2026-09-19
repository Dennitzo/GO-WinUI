using GoAi.Contracts;
using GoAi.Server.Core.Runs;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class AgentToolCatalogTests
{
    [Fact]
    public void CompactSelectorExposesNamesWithoutFullToolSchemas()
    {
        var catalog = new AgentToolCatalog();
        var available = catalog.GetAvailableTools(CreateRequest([]));

        var selector = AgentToolCatalog.CreateSelectorDefinition(available);

        Assert.Equal(AgentToolCatalog.SelectorToolName, selector.Name);
        var names = selector.Parameters.GetProperty("properties").GetProperty("name").GetProperty("enum");
        Assert.Equal(available.Count, names.GetArrayLength());
        Assert.DoesNotContain("maximumResults", selector.Parameters.GetRawText(), StringComparison.Ordinal);
        Assert.All(available, tool =>
        {
            Assert.Contains($"- {tool.Name}", selector.Description, StringComparison.Ordinal);
            Assert.Contains(tool.Description[..Math.Min(180, tool.Description.Length)], selector.Description, StringComparison.Ordinal);
        });
        Assert.Contains(": ", selector.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectorResolvesExactlyOneAvailableTool()
    {
        var catalog = new AgentToolCatalog();
        var available = catalog.GetAvailableTools(CreateRequest([]));
        var selection = JsonSerializer.SerializeToElement(new { name = "web.search" });

        var tool = catalog.ResolveSelection(selection, available);

        Assert.Equal("web.search", tool.Name);
        Assert.Throws<ArgumentException>(() => catalog.ResolveSelection(
            JsonSerializer.SerializeToElement(new { name = "web.search", extra = true }),
            available));
    }

    [Fact]
    public void ModelReceivesNamesFirstAndOnlyTheSelectedFullSchemaAfterward()
    {
        var catalog = new AgentToolCatalog();
        var available = catalog.GetAvailableTools(CreateRequest(["documentIo"]));
        var selected = catalog.Resolve(ClientToolNames.DocumentRead, available);

        var firstStage = Assert.Single(RunProcessor.CreateModelToolDefinitions(available, selectedToolName: null));
        var secondStage = Assert.Single(RunProcessor.CreateModelToolDefinitions(available, selected.Name));

        Assert.Equal(AgentToolCatalog.SelectorToolName, firstStage.Name);
        Assert.DoesNotContain("startLine", firstStage.Parameters.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(selected.Name, secondStage.Name);
        Assert.Equal(selected.Description, secondStage.Description);
        Assert.Equal(selected.Schema.GetRawText(), secondStage.Parameters.GetRawText());
    }

    [Fact]
    public void ClientToolsAreOnlyAdvertisedForReportedCapabilities()
    {
        var catalog = new AgentToolCatalog();
        var withoutClient = catalog.GetAvailableTools(CreateRequest(null));
        var withBricsCad = catalog.GetAvailableTools(CreateRequest(["bricscad"]));

        Assert.DoesNotContain(withoutClient, static tool => !tool.ServerSide);
        Assert.Contains(withBricsCad, static tool => tool.Name == ClientToolNames.BricsCadMove);
        Assert.DoesNotContain(withBricsCad, static tool => tool.Name == ClientToolNames.DocumentRead);

        var withDocuments = catalog.GetAvailableTools(CreateRequest(["documents"]));
        Assert.Contains(withDocuments, static tool => tool.Name == ClientToolNames.DocumentsList);
        Assert.Contains(withDocuments, static tool => tool.Name == ClientToolNames.DocumentsSearch);
        Assert.Contains(withDocuments, static tool => tool.Name == ClientToolNames.DocumentsReadPages);
        Assert.DoesNotContain(withDocuments, static tool => tool.Name == ClientToolNames.DocumentCreate);

        var withDocumentIo = catalog.GetAvailableTools(CreateRequest(["documentIo"]));
        Assert.Contains(withDocumentIo, static tool => tool.Name == ClientToolNames.DocumentRead);
        Assert.Contains(withDocumentIo, static tool => tool.Name == ClientToolNames.DocumentCreate);
    }

    [Fact]
    public void DocumentToolsUseBoundedSectionContracts()
    {
        var catalog = new AgentToolCatalog();
        var tools = catalog.GetAvailableTools(CreateRequest(["documentIo"]));
        var read = catalog.Resolve(ClientToolNames.DocumentRead, tools);
        var create = catalog.Resolve(ClientToolNames.DocumentCreate, tools);
        using var readWindow = JsonDocument.Parse("""{"scope":"session","mode":"read","reference":"00000000-0000-0000-0000-000000000001","startUnit":2,"maximumUnits":3,"maximumCharacters":12000}""");
        using var append = JsonDocument.Parse("""{"operation":"appendSection","reference":"00000000-0000-0000-0000-000000000001","format":"pdf","sectionId":"kapitel-2","heading":"Kapitel 2","content":"Text","expectedSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""");
        using var unbounded = JsonDocument.Parse("""{"scope":"session","mode":"read","reference":"00000000-0000-0000-0000-000000000001","maximumCharacters":40001}""");
        using var staleEdit = JsonDocument.Parse("""{"operation":"replaceSection","reference":"00000000-0000-0000-0000-000000000001","format":"pdf","sectionId":"kapitel-2","content":"Text"}""");
        using var workspaceRead = JsonDocument.Parse("""{"scope":"workspace","mode":"read","reference":"notes.txt"}""");

        catalog.Validate(read, readWindow.RootElement);
        catalog.Validate(create, append.RootElement);
        Assert.Throws<ArgumentException>(() => catalog.Validate(read, unbounded.RootElement));
        Assert.Throws<ArgumentException>(() => catalog.Validate(create, staleEdit.RootElement));
        Assert.Throws<ArgumentException>(() => catalog.Validate(read, workspaceRead.RootElement));
    }

    [Fact]
    public void WebFetchExposesBoundedPhraseSearchInsteadOfWholePages()
    {
        var catalog = new AgentToolCatalog();
        var tools = catalog.GetAvailableTools(CreateRequest([]));
        var fetch = catalog.Resolve("web.fetch", tools);
        using var valid = JsonDocument.Parse("""
            {"url":"https://example.com/reference","query":"Newtons zweites Gesetz","maximumCharacters":8000}
            """);
        using var oversized = JsonDocument.Parse("""
            {"url":"https://example.com/reference","query":"Newtons zweites Gesetz","maximumCharacters":12001}
            """);

        catalog.Validate(fetch, valid.RootElement);
        Assert.Throws<ArgumentException>(() => catalog.Validate(fetch, oversized.RootElement));
        Assert.True(fetch.Schema.GetProperty("properties").TryGetProperty("query", out _));
        Assert.Equal(
            12_000,
            fetch.Schema.GetProperty("properties").GetProperty("maximumCharacters").GetProperty("maximum").GetInt32());
        Assert.Contains("Trefferfenster", fetch.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyPdfRenderToolIsNotAdvertised()
    {
        var catalog = new AgentToolCatalog();
        var request = CreateRequest(["pdf"]);
        Assert.Throws<InvalidOperationException>(() => catalog.Resolve(
            "document.renderPdf",
            catalog.GetAvailableTools(request)));
    }

    [Fact]
    public void UnknownPropertiesAndUnknownToolsAreRejected()
    {
        var catalog = new AgentToolCatalog();
        var tools = catalog.GetAvailableTools(CreateRequest(null));
        var search = catalog.Resolve("web.search", tools);
        using var arguments = JsonDocument.Parse("""{"query":"Wissenschaft","unexpected":true}""");

        Assert.Throws<ArgumentException>(() => catalog.Validate(search, arguments.RootElement));
        Assert.Throws<InvalidOperationException>(() => catalog.Resolve("shell.execute", tools));
    }

    [Fact]
    public void MediaDetailWindowsAreStrictAndBounded()
    {
        var catalog = new AgentToolCatalog();
        var tools = catalog.GetAvailableTools(CreateRequest(null));
        var media = catalog.Resolve("media.analyze", tools);
        using var valid = JsonDocument.Parse("""{"uploadId":"upload-0123456789abcdef0123456789abcdef","detailWindows":[{"start":10,"end":20}]}""");
        using var invalid = JsonDocument.Parse("""{"uploadId":"upload-0123456789abcdef0123456789abcdef","detailWindows":[{"start":20,"end":10,"extra":true}]}""");

        catalog.Validate(media, valid.RootElement);
        Assert.Throws<ArgumentException>(() => catalog.Validate(media, invalid.RootElement));
    }

    [Fact]
    public void ExplicitServerToolAllowListPreventsInheritedImageGeneration()
    {
        var catalog = new AgentToolCatalog();
        var request = CreateRequest(null) with
        {
            AllowedServerTools = ["math.evaluate", "context.retrieve"],
        };

        var tools = catalog.GetAvailableTools(request);

        Assert.Contains(tools, static tool => tool.Name == "math.evaluate");
        Assert.DoesNotContain(tools, static tool => tool.Name == "image.generate");
        Assert.DoesNotContain(tools, static tool => tool.Name == "web.search");
    }

    [Fact]
    public void EmptyServerToolAllowListExcludesWebResearch()
    {
        var catalog = new AgentToolCatalog();
        var request = CreateRequest([]) with
        {
            AllowedServerTools = [],
        };

        var tools = catalog.GetAvailableTools(request);

        Assert.DoesNotContain(tools, static tool => tool.Name == "web.search");
        Assert.DoesNotContain(tools, static tool => tool.Name == "web.fetch");
        Assert.DoesNotContain(tools, static tool => tool.Name == "youtube.search");
        Assert.DoesNotContain(tools, static tool => tool.Name == "image.generate");
    }

    [Fact]
    public void GeneralRunsAcceptExplicitStagedWebResearchTools()
    {
        var catalog = new AgentToolCatalog();
        var request = CreateRequest([]) with
        {
            AllowedServerTools = ["web.search", "web.fetch", "math.evaluate"],
            Messages = [new RunMessage("user", [new ContentPart("text", "[GO_WEB_RESEARCH_REQUEST]\nFrage")])],
        };

        var tools = catalog.GetAvailableTools(request);

        Assert.Contains(tools, static tool => tool.Name == "web.search");
        Assert.Contains(tools, static tool => tool.Name == "web.fetch");
        Assert.True(StagedWebResearchPipeline.IsRequested(request, tools));
    }

    [Fact]
    public void NullServerToolAllowListRetainsProtocolCompatibility()
    {
        var tools = new AgentToolCatalog().GetAvailableTools(CreateRequest(null));
        Assert.Contains(tools, static tool => tool.Name == "image.generate");
    }

    [Fact]
    public void ContextPreparationAdvertisesNoTools()
    {
        var request = CreateRequest([]) with
        {
            AllowedServerTools = [],
            PreferredGeneralModelId = "gpt-oss-120b",
            ConversationProfile = ConversationProfile.ContextPreparation,
        };

        Assert.Empty(new AgentToolCatalog().GetAvailableTools(request));
    }

    private static RunRequest CreateRequest(IReadOnlyList<string>? capabilities) => new(
        GoAiProtocol.Version,
        RunMode.General,
        [new RunMessage("user", [new ContentPart("text", "Test")])],
        ClientCapabilities: capabilities);
}
