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
        Assert.Contains(withCode, static tool => tool.Name == ClientToolNames.ProcessRunPreset);
        Assert.DoesNotContain(withCode, static tool => tool.Name == ClientToolNames.BricsCadMove);
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

    private static RunRequest CreateRequest(IReadOnlyList<string>? capabilities) => new(
        GoAiProtocol.Version,
        RunMode.General,
        [new RunMessage("user", [new ContentPart("text", "Test")])],
        ClientCapabilities: capabilities);
}
