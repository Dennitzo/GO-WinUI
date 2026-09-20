using GoAi.Contracts;
using GoAi.Server.Core.Coding;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runs;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class DeepSeekToolSchemaTests
{
    [Theory]
    [InlineData("coding/DeepSeek-V4-Flash-Vision-Exp~hash")]
    public void TransportKeepsEditFieldsWhileHostEnforcesExclusiveAlternatives(string model)
    {
        var spec = CodingToolCatalog.CreateTools().Single(t => t.Name == ClientToolNames.CodingEdit);
        var parameters = ModelRuntimeClient.PrepareToolParameters(model, new(spec.Name, spec.Description, spec.Schema));
        Assert.False(parameters.TryGetProperty("oneOf", out _));
        Assert.Equal(spec.Schema.GetProperty("properties").GetRawText(), parameters.GetProperty("properties").GetRawText());
        Assert.Equal(spec.Schema.GetProperty("required").GetRawText(), parameters.GetProperty("required").GetRawText());
        Assert.False(parameters.GetProperty("additionalProperties").GetBoolean());
        Assert.True(spec.Schema.TryGetProperty("oneOf", out _));
        var catalog = new AgentToolCatalog();
        Assert.Throws<ArgumentException>(() => catalog.Validate(spec, JsonSerializer.SerializeToElement(new { })));
        Assert.Throws<ArgumentException>(() => catalog.Validate(spec, JsonSerializer.SerializeToElement(new
        {
            path = "file.txt", expectedSha256 = new string('a', 64), oldText = "old", newText = "new",
            edits = new[] { new { oldText = "old", newText = "new" } },
        })));
    }

    [Fact]
    public void OtherModelsAndWriteSchemaRemainUnchanged()
    {
        foreach (var spec in CodingToolCatalog.CreateTools())
        {
            var tool = new LmToolDefinition(spec.Name, spec.Description, spec.Schema);
            Assert.Equal(spec.Schema.GetRawText(), ModelRuntimeClient.PrepareToolParameters("coding/Qwen3", tool).GetRawText());
            if (spec.Name != ClientToolNames.CodingEdit)
                Assert.Equal(spec.Schema.GetRawText(), ModelRuntimeClient.PrepareToolParameters("coding/DeepSeek", tool).GetRawText());
        }
    }
}
