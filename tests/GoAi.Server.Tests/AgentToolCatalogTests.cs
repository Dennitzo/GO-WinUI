using GoAi.Contracts;
using GoAi.Server.Core.Runs;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class AgentToolCatalogTests
{
    [Fact]
    public void ClientToolsAreOnlyAdvertisedForReportedCapabilities()
    {
        var catalog = new AgentToolCatalog();
        var withoutClient = catalog.GetAvailableTools(CreateRequest(null));
        var withCode = catalog.GetAvailableTools(CreateRequest(["code"]));

        Assert.DoesNotContain(withoutClient, static tool => !tool.ServerSide);
        Assert.Contains(withCode, static tool => tool.Name == ClientToolNames.FileSystemReadText);
        Assert.Contains(withCode, static tool => tool.Name == ClientToolNames.FileSystemReplaceText);
        Assert.Contains(withCode, static tool => tool.Name == ClientToolNames.ProcessRunPreset);
        Assert.DoesNotContain(withCode, static tool => tool.Name == ClientToolNames.BricsCadMove);

        var withDocuments = catalog.GetAvailableTools(CreateRequest(["documents"]));
        Assert.Contains(withDocuments, static tool => tool.Name == ClientToolNames.DocumentsList);
        Assert.Contains(withDocuments, static tool => tool.Name == ClientToolNames.DocumentsSearch);
        Assert.Contains(withDocuments, static tool => tool.Name == ClientToolNames.DocumentsReadPages);
        Assert.DoesNotContain(withDocuments, static tool => tool.Name == ClientToolNames.FileSystemWriteText);
    }

    [Fact]
    public void ReplaceTextRequiresExactBlocksAndRejectsUnknownProperties()
    {
        var catalog = new AgentToolCatalog();
        var tools = catalog.GetAvailableTools(CreateRequest(["code"]));
        var replace = catalog.Resolve(ClientToolNames.FileSystemReplaceText, tools);
        using var valid = JsonDocument.Parse("""{"path":"ViewModels/ShellViewModel.cs","oldText":"public string Name","newText":"public string DisplayName","replaceAll":false}""");
        using var invalid = JsonDocument.Parse("""{"path":"ViewModels/ShellViewModel.cs","oldText":"","newText":"x","shell":true}""");

        catalog.Validate(replace, valid.RootElement);
        Assert.Throws<ArgumentException>(() => catalog.Validate(replace, invalid.RootElement));
    }

    [Fact]
    public void UnknownPropertiesAndUnknownToolsAreRejected()
    {
        var catalog = new AgentToolCatalog();
        var tools = catalog.GetAvailableTools(CreateRequest(null));
        var search = catalog.Resolve("web.search", tools);
        using var arguments = JsonDocument.Parse("""{"query":"TGA","unexpected":true}""");

        Assert.Throws<ArgumentException>(() => catalog.Validate(search, arguments.RootElement));
        Assert.Throws<InvalidOperationException>(() => catalog.Resolve("shell.execute", tools));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    public void WorkspaceRootCanBeAddressedConsistently(string path)
    {
        var catalog = new AgentToolCatalog();
        var tools = catalog.GetAvailableTools(CreateRequest(["code"]));
        var list = catalog.Resolve(ClientToolNames.FileSystemList, tools);
        var search = catalog.Resolve(ClientToolNames.FileSystemSearch, tools);
        using var listArguments = JsonDocument.Parse(JsonSerializer.Serialize(new { path }));
        using var searchArguments = JsonDocument.Parse(JsonSerializer.Serialize(new { path, query = "test" }));

        catalog.Validate(list, listArguments.RootElement);
        catalog.Validate(search, searchArguments.RootElement);
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
    public void NullServerToolAllowListRetainsProtocolCompatibility()
    {
        var tools = new AgentToolCatalog().GetAvailableTools(CreateRequest(null));
        Assert.Contains(tools, static tool => tool.Name == "image.generate");
    }

    private static RunRequest CreateRequest(IReadOnlyList<string>? capabilities) => new(
        GoAiProtocol.Version,
        RunMode.General,
        [new RunMessage("user", [new ContentPart("text", "Test")])],
        ClientCapabilities: capabilities);
}
