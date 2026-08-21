using GoAi.Server.Core.Models;

namespace GoAi.Server.Tests;

public sealed class LmStudioCliModelLoaderTests
{
    [Theory]
    [InlineData("ud", 262_144)]
    [InlineData("qwen3-coder-next", 262_144)]
    [InlineData("openai/gpt-oss-120b", 131_072)]
    public void CodingLoadArgumentsRequireMaximumGpuOffload(string modelId, int contextLength)
    {
        var arguments = LmStudioCliModelLoader.CreateLoadArguments(modelId, contextLength);

        Assert.Equal(
            [
                "load",
                modelId,
                "--gpu",
                "max",
                "--context-length",
                contextLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--parallel",
                "1",
                "--identifier",
                modelId,
                "--yes",
            ],
            arguments);
    }
}
