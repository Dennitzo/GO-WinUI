using System.Text.Json;
using GoAi.Contracts;
using GoAi.Server.Core.Coding;
using GoAi.Server.Core.Runs;

namespace GoAi.Server.Tests;

public sealed class CodingWriteLimitTests
{
    [Theory]
    [InlineData(17_440)]
    [InlineData(64_000)]
    public void CompleteScriptsWithinExpandedBoundPassAdvertisedSchemaAndValidation(int characters)
    {
        var tool = CodingToolCatalog.CreateTools().Single(item => item.Name == ClientToolNames.CodingWrite);
        Assert.Equal(64_000, tool.Schema.GetProperty("properties").GetProperty("content").GetProperty("maxLength").GetInt32());
        Assert.Contains("64000", tool.Description, StringComparison.Ordinal);
        Assert.Contains("Module", tool.Description, StringComparison.Ordinal);
        new AgentToolCatalog().Validate(tool, JsonSerializer.SerializeToElement(new { path = "scene.py", content = new string('ä', characters) }));
    }

    [Fact]
    public void ContentBeyondExpandedWriteBoundIsRejected()
    {
        var tool = CodingToolCatalog.CreateTools().Single(item => item.Name == ClientToolNames.CodingWrite);
        Assert.Throws<ArgumentException>(() => new AgentToolCatalog().Validate(tool,
            JsonSerializer.SerializeToElement(new { path = "scene.py", content = new string('x', 64_001) })));
    }

    [Fact]
    public void ExpandedWriteBoundDoesNotExpandEditReplacementBounds()
    {
        var tool = CodingToolCatalog.CreateTools().Single(item => item.Name == ClientToolNames.CodingEdit);
        var catalog = new AgentToolCatalog();
        Assert.Equal(16_000, tool.Schema.GetProperty("properties").GetProperty("newText").GetProperty("maxLength").GetInt32());
        Assert.Throws<ArgumentException>(() => catalog.Validate(tool, JsonSerializer.SerializeToElement(new
        {
            path = "scene.py", expectedSha256 = new string('a', 64), oldText = "old", newText = new string('x', 16_001),
        })));
        Assert.Throws<ArgumentException>(() => catalog.Validate(tool, JsonSerializer.SerializeToElement(new
        {
            path = "scene.py", expectedSha256 = new string('a', 64),
            edits = new[] { new { oldText = new string('a', 16_000), newText = new string('b', 16_000) }, new { oldText = "x", newText = "y" } },
        })));
    }
}
