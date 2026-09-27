using GoAi.Contracts;
using GoWinUI.App.Services;
using GoWinUI.Core.Coding;
using GoWinUI.Core.Contracts;
using System.Text.Json;

namespace GoWinUI.Tests;

public sealed class WorkspaceToolTests
{
    [Fact]
    public async Task ClearingSessionsRemovesOnlyEmptyProjectsForTheSelectedMode()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var retained = await chats.CreateSessionAsync("Behalten", GoWinUI.Core.Models.ChatMode.Coding);
        var deleted = await chats.CreateSessionAsync("Entfernen", GoWinUI.Core.Models.ChatMode.General);
        await chats.SetCodingWorkspacePathAsync(retained.Id, Path.Combine(environment.Directory, "A"));
        await chats.SetCodingWorkspacePathAsync(deleted.Id, Path.Combine(environment.Directory, "B"));
        Assert.Equal(1, await chats.DeleteSessionsAsync(GoWinUI.Core.Models.ChatMode.General));
        var remaining = Assert.Single(await chats.ListSessionsAsync());
        Assert.Equal(retained.Id, remaining.Id);
        Assert.Equal(remaining.SessionGroupId, Assert.Single(await chats.ListSessionGroupsAsync()).Id);
    }

}
