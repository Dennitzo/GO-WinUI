using System.Text.Json;
using GoAi.Server.Core.Models;
using GoAi.Contracts;

namespace GoAi.Server.Tests;

public sealed class IntegratedVisionTests
{
    private static readonly string[] VisionTags = ["go-vision:projector"];
    [Fact]
    public void DirectMediaButtonReusesResidentMainInstanceOfTheSelectedModel()
    {
        const string id = "coding/DeepSeek-Vision~hash";
        var baseModel = new ModelRuntimeStatus(id, "general", true, false, "unloaded", 32768, SupportsVision: true);
        Assert.Equal(id, ModelRuntimeClient.SelectIntegratedVision(
            [baseModel with { Loaded = true, State = "loaded" }], id));
    }
    [Theory]
    [InlineData("")]
    public void SelectedDeepSeekKeepsExactNativeInstanceForVision(string suffix)
    {
        var id = "coding/DeepSeek-Vision~hash" + suffix;
        var root = JsonSerializer.SerializeToElement(new { data = new[] {
            new { id, status = new { value = "loaded" }, tags = VisionTags } } });
        var model = Assert.Single(ModelRuntimeClient.ReadRuntimeModels(root));
        Assert.True(model.SupportsVision);
        var statuses = ModelRuntimeClient.ReadCodingModels(root, 32768);
        Assert.Equal(id, ModelRuntimeClient.SelectIntegratedVision(statuses, id));
    }

    [Fact]
    public void TextOnlyQwenDoesNotBorrowAnotherInstalledVisionModel()
    {
        var root = JsonDocument.Parse("""
            {"data":[{"id":"coding/qwen3~text","status":{"value":"loaded"}},
            {"id":"vision/DeepSeek~hash","status":{"value":"unloaded"}}]}
            """).RootElement;
        var models = ModelRuntimeClient.ReadCodingModels(root, 32768);
        Assert.Null(ModelRuntimeClient.SelectIntegratedVision(models, "coding/qwen3~text"));
        Assert.Null(ModelRuntimeClient.SelectIntegratedVision(models, "coding/DeepSeek~missing"));
    }
}
