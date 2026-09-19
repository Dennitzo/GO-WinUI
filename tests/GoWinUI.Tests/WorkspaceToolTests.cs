using GoAi.Contracts;
using GoWinUI.App.Services;
using GoWinUI.Core.Coding;
using GoWinUI.Core.Contracts;
using System.Text.Json;

namespace GoWinUI.Tests;

public sealed class WorkspaceToolTests
{
    [Theory]
    [InlineData("../other/image.png")]
    [InlineData("C:/private/image.png")]
    [InlineData(".git/config")]
    [InlineData("image.png:secret")]
    public async Task ImageAndBlenderPathsStayInsideWorkspace(string path)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        Assert.Throws<UnauthorizedAccessException>(() => WorkspaceFilePath.Resolve(environment.Directory, path));
    }

    [Fact]
    public async Task ClearingSessionsRemovesOnlyEmptyProjectsAndPreservesPinnedMessages()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var pinned = await chats.CreateSessionAsync("Behalten");
        var deleted = await chats.CreateSessionAsync("Entfernen");
        await chats.SetCodingWorkspacePathAsync(pinned.Id, Path.Combine(environment.Directory, "A"));
        await chats.SetCodingWorkspacePathAsync(deleted.Id, Path.Combine(environment.Directory, "B"));
        await chats.SetPinnedAsync(pinned.Id, true);
        Assert.Equal(1, await chats.DeleteUnpinnedSessionsAsync());
        var remaining = Assert.Single(await chats.ListSessionsAsync());
        Assert.Equal(pinned.Id, remaining.Id);
        Assert.Equal(remaining.SessionGroupId, Assert.Single(await chats.ListSessionGroupsAsync()).Id);
    }

    [Fact]
    public void LocalBrokerAcceptsScopedBlenderAndImageToolsWithCorrectRisk()
    {
        var scope = new CodingExecutionScope("agent-test", ["scene.py", "scene.blend"]);
        var blender = new ToolProposal("proposal-test", "run-test", WorkspaceTools.Blender,
            JsonSerializer.SerializeToElement(new { operation = "info" }), ToolRiskClass.Process, "Blender prüfen", DateTimeOffset.MaxValue, scope);
        LocalToolBroker.ValidateProposal(blender);
        Assert.Throws<InvalidDataException>(() => LocalToolBroker.ValidateProposal(blender with { RiskClass = ToolRiskClass.ReadOnly }));
        LocalToolBroker.ValidateProposal(blender with { Name = WorkspaceTools.ImageInput,
            Arguments = JsonSerializer.SerializeToElement(new { operation = "windows" }), RiskClass = ToolRiskClass.ReadOnly });
    }
}
