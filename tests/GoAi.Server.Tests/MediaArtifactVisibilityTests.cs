using GoAi.Contracts;
using GoAi.Server.Core.Runs;

namespace GoAi.Server.Tests;

public sealed class MediaArtifactVisibilityTests
{
    [Fact]
    public void VisionTransportImageIsNotReturnedAsVisibleChatArtifact()
    {
        var now = DateTimeOffset.UtcNow;
        var artifacts = new[]
        {
            new ArtifactDescriptor("model", "vision-input.jpg", "image/jpeg", 100, "a", now, now.AddHours(1),
                new Dictionary<string, string> { ["role"] = "vision_input", ["visibility"] = "internal" }),
            new ArtifactDescriptor("preview", "thumbnail.jpg", "image/jpeg", 20, "b", now, now.AddHours(1),
                new Dictionary<string, string> { ["role"] = "thumbnail", ["visibility"] = "internal" }),
            new ArtifactDescriptor("frame", "frame-001.jpg", "image/jpeg", 30, "c", now, now.AddHours(1)),
        };

        var visible = AgentToolExecutor.VisibleMediaArtifacts(artifacts);

        Assert.Collection(visible,
            item => Assert.Equal("preview", item.ArtifactId),
            item => Assert.Equal("frame", item.ArtifactId));
    }

    [Fact]
    public void VisionLoopRecoveryRequiresDirectResultWithoutFollowUpQuestion()
    {
        var prompt = AgentToolExecutor.BuildVisionLoopRecoveryPrompt("Analysiere die Fassade.");

        Assert.Contains("Antworte jetzt direkt", prompt, StringComparison.Ordinal);
        Assert.Contains("stelle keine Rückfrage", prompt, StringComparison.Ordinal);
        Assert.Contains("nicht beurteilbar", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void StaleUploadUsesTheOnlyCurrentMediaUpload()
    {
        var now = DateTimeOffset.UtcNow;
        var current = new UploadCompleted("upload-current", "reference.bmp", "image/bmp", 42, "a", now.AddHours(1));

        var resolved = AgentToolExecutor.SelectCurrentUploadFallback("upload-historical", [current]);

        Assert.Equal("upload-current", resolved);
    }

    [Fact]
    public void StaleUploadDoesNotGuessBetweenMultipleCurrentUploads()
    {
        var now = DateTimeOffset.UtcNow;
        var current = new[]
        {
            new UploadCompleted("upload-a", "front.png", "image/png", 42, "a", now.AddHours(1)),
            new UploadCompleted("upload-b", "side.png", "image/png", 42, "b", now.AddHours(1)),
        };

        Assert.Null(AgentToolExecutor.SelectCurrentUploadFallback("upload-historical", current));
    }

    [Fact]
    public void MissingMediaUploadIsARecoverableToolFailure()
    {
        var exception = new KeyNotFoundException("Completed media upload not found.");

        Assert.True(AgentToolExecutor.IsRecoverableMediaFailure("media.analyze", exception));
        Assert.Equal("media.upload_unavailable", AgentToolExecutor.DescribeMediaFailure(exception).ErrorCode);
        Assert.True(AgentToolExecutor.IsRecoverableMediaFailure(
            "media.analyze", new InvalidOperationException("Unsupported reasoning effort.")));
        Assert.False(AgentToolExecutor.IsRecoverableMediaFailure("math.evaluate", exception));
    }
}
