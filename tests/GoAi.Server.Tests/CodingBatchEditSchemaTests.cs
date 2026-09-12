using GoAi.Contracts;
using GoAi.Server.Core.Coding;
using GoAi.Server.Core.Runs;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class CodingBatchEditSchemaTests
{
    private static readonly AgentToolSpec Edit = CodingToolCatalog.CreateTools().Single(static tool => tool.Name == ClientToolNames.CodingEdit);
    private static readonly string Hash = new('a', 64);
    private readonly AgentToolCatalog _catalog = new();

    [Fact]
    public void SingleAndAtomicBatchAreExclusiveSchemaAlternatives()
    {
        Assert.Equal(2, Edit.Schema.GetProperty("oneOf").GetArrayLength());
        Assert.Equal(100, Edit.Schema.GetProperty("properties").GetProperty("edits").GetProperty("maxItems").GetInt32());
        _catalog.Validate(Edit, Arguments(new { oldText = "original", newText = "changed" }));
        _catalog.Validate(Edit, Arguments(new { edits = new[] { new { oldText = "original", newText = "" }, new { oldText = "second", newText = "new" } } }));
        Assert.Throws<ArgumentException>(() => _catalog.Validate(Edit, Arguments(new { oldText = "a", newText = "b", edits = new[] { new { oldText = "c", newText = "d" } } })));
        Assert.Throws<ArgumentException>(() => _catalog.Validate(Edit, Arguments(new { oldText = "a" })));
        Assert.Throws<ArgumentException>(() => _catalog.Validate(Edit, Arguments(new { newText = "a" })));
        Assert.Throws<ArgumentException>(() => _catalog.Validate(Edit, Arguments(new { })));
    }

    [Fact]
    public void BatchRequiresBoundedStrictReplacementsAndCombinedCharacterBudget()
    {
        _catalog.Validate(Edit, Arguments(new { edits = Enumerable.Range(0, 100).Select(static index => new { oldText = index.ToString(System.Globalization.CultureInfo.InvariantCulture), newText = "" }).ToArray() }));
        _catalog.Validate(Edit, Arguments(new { edits = new[] { new { oldText = new string('a', 16_000), newText = new string('b', 16_000) } } }));
        Assert.Throws<ArgumentException>(() => _catalog.Validate(Edit, Arguments(new { edits = Array.Empty<object>() })));
        Assert.Throws<ArgumentException>(() => _catalog.Validate(Edit, Arguments(new { edits = Enumerable.Range(0, 101).Select(static index => new { oldText = index.ToString(System.Globalization.CultureInfo.InvariantCulture), newText = "" }).ToArray() })));
        Assert.Throws<ArgumentException>(() => _catalog.Validate(Edit, Arguments(new { edits = new[] { new { oldText = new string('a', 16_000), newText = new string('b', 16_000) }, new { oldText = "c", newText = "" } } })));
        Assert.Throws<ArgumentException>(() => _catalog.Validate(Edit, Arguments(new { edits = new[] { new { oldText = "", newText = "b" } } })));
        Assert.Throws<ArgumentException>(() => _catalog.Validate(Edit, Arguments(new { edits = new[] { new { oldText = "a", newText = "b", replaceAll = true } } })));
        Assert.Throws<ArgumentException>(() => _catalog.Validate(Edit, Arguments(new { edits = new[] { new { oldText = "a" } } })));
    }

    private static JsonElement Arguments(object replacements)
    {
        var properties = JsonSerializer.SerializeToElement(replacements).EnumerateObject()
            .ToDictionary(static property => property.Name, static property => property.Value, StringComparer.Ordinal);
        properties["path"] = JsonSerializer.SerializeToElement("file.py");
        properties["expectedSha256"] = JsonSerializer.SerializeToElement(Hash);
        return JsonSerializer.SerializeToElement(properties);
    }
}
