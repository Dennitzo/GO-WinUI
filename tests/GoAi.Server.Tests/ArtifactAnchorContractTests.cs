using System.Text.Json;
using GoAi.Contracts;
using GoAi.Server.Core.Runs;

namespace GoAi.Server.Tests;

public sealed class ArtifactAnchorContractTests
{
    [Theory]
    [InlineData(RunMode.General)]
    [InlineData(RunMode.Coding)]
    public async Task ReplayedArtifactEventRetainsItsOriginEvenWhenTheToolStartIsBeforeTheCursor(RunMode mode)
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var run = await repository.CreateAsync(new(GoAiProtocol.Version, mode,
            [new("user", [new("text", "Prüfe das Renderbild.")])]), null);
        var started = await repository.AppendEventAsync(run.Snapshot.RunId, RunEventTypes.ServerToolStarted,
            new { callId = "image-call-1", tool = "media.analyze" });
        var descriptor = new ArtifactDescriptor("thumbnail-1", "render.png", "image/png", 3, new string('a', 64),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), StepId: "image-call-1");
        await repository.AppendEventAsync(run.Snapshot.RunId, RunEventTypes.ArtifactCreated, descriptor);
        await repository.AppendEventAsync(run.Snapshot.RunId, RunEventTypes.ServerToolStarted,
            new { callId = "later-call-2", tool = "web.search" });

        var reopened = new RunRepository(context.Database, new RunEventNotifier());
        var replay = await reopened.GetEventsAfterAsync(run.Snapshot.RunId, started.Id);
        Assert.DoesNotContain(replay, item => item.Id == started.Id);
        var artifactEvent = Assert.Single(replay, item => item.Type == RunEventTypes.ArtifactCreated);
        var wire = JsonSerializer.Serialize(artifactEvent, GoAiProtocol.CreateJsonOptions());
        var restoredEvent = JsonSerializer.Deserialize<RunEvent>(wire, GoAiProtocol.CreateJsonOptions())!;
        var restored = restoredEvent.Data.Deserialize<ArtifactDescriptor>(GoAiProtocol.CreateJsonOptions())!;
        Assert.Equal("image-call-1", restored.StepId);
        Assert.Equal(descriptor.ArtifactId, restored.ArtifactId);
        Assert.True(artifactEvent.Id < replay[^1].Id);
    }

    [Fact]
    public void LegacyArtifactWithoutAnchorRemainsReadable()
    {
        const string json = """
            {"artifactId":"old","fileName":"render.png","mediaType":"image/png","length":3,
             "sha256":"abc","createdAt":"2026-09-19T10:00:00Z","expiresAt":"2026-09-21T10:00:00Z"}
            """;
        var restored = JsonSerializer.Deserialize<ArtifactDescriptor>(json, GoAiProtocol.CreateJsonOptions());
        Assert.NotNull(restored);
        Assert.Null(restored.StepId);
        Assert.Equal("old", restored.ArtifactId);
    }
}
