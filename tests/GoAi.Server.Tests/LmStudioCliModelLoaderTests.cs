using GoAi.Server.Core.Models;

namespace GoAi.Server.Tests;

public sealed class LmStudioCliModelLoaderTests
{
    [Theory]
    [InlineData("ud", 262_144)]
    [InlineData("qwen3-coder-next", 262_144)]
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
                "262144",
                "--parallel",
                "1",
                "--identifier",
                modelId,
                "--yes",
            ],
            arguments);
    }
}
