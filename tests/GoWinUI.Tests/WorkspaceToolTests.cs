using GoAi.Contracts;
using GoWinUI.App.Services;
using GoWinUI.Core.Coding;
using GoWinUI.Core.Contracts;
using System.Text.Json;

namespace GoWinUI.Tests;

public sealed class WorkspaceToolTests
{
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

}
