using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace GoAi.Server.Tests;

public sealed class DeepSeekVisionLiveTests(ITestOutputHelper output)
{
    private static readonly byte[] RedSquarePng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAIAAAACACAIAAABMXPacAAABLUlEQVR4nO3TMREAMAwDsST8ObcwtLwIePjzvol0dD0F0HoAVgCsAFgBsAJgBcAKgBUAKwBWAKwAWAGwAmAFwAqAFQArAFYArABYAbACYAXACoAVACsAVgCsAFgBsAJgBcAKgBUAKwBWAKwAWAGwAmAFwAqAFQArAFYArABYAbACYAXACoAVACsAVgCsAFgBsAJgBcAKgBUAKwBWAKwAWAGwAmAFwAqAFQArAFYArABYAbACYAXACoAVACsAVgCsAFgBsAJgBcAKgBUAKwBWAKwAWAGwAmAFwAqAFQArAFYArABYAbACYAXACoAVACsAVgCsAFgBsAJgBcAKgBUAKwBWAKwAWAGwAmAFwAqAFQArAFYArABYAbACYAXACoAVACsAVgCsAFgBsAKM9QHP4QH/xCXf5QAAAABJRU5ErkJggg==");

    [Fact]
    [Trait("Category", "Live")]
    public async Task DeepSeekVisionReceivesTheActualImageAndIdentifiesItsCentralColor()
    {
        if (Environment.GetEnvironmentVariable("GO_AI_DEEPSEEK_VISION_LIVE") != "1") return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        using var client = new ModelRuntimeClient(new HttpClient(), Options.Create(new GoAiServerOptions
        {
            ModelRuntimeUri = new Uri(Environment.GetEnvironmentVariable("GO_AI_NATIVE_RUNTIME_URL") ?? "http://127.0.0.1:8081"),
        }), NullLogger<ModelRuntimeClient>.Instance);
        var status = await client.GetStatusAsync(timeout.Token);
        var model = ModelRuntimeClient.ResolveModelStatus(status.Models,
            Environment.GetEnvironmentVariable("GO_AI_DEEPSEEK_VISION_MODEL")
                ?? "vision/DeepSeek-V4-Flash-Vision-Exp-UD-IQ1_S~31b467a216d0", "vision");
        Assert.NotNull(model);
        Assert.True(model.SupportsVision);
        var path = Path.Combine(Path.GetTempPath(), "deepseek-vision-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            await File.WriteAllBytesAsync(path, RedSquarePng, timeout.Token);
            var answer = await client.AnalyzeImagesAsync(model.Id,
                "Betrachte ausschließlich das tatsächliche Bild. Welche Farbe hat das große Quadrat in der Mitte? Antworte auf Deutsch mit genau einem Farbnamen.",
                [path], timeout.Token);
            output.WriteLine($"DeepSeek Vision model={model.Id}; answer={answer}");
            Assert.Contains("rot", answer, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(path); }
    }
}
