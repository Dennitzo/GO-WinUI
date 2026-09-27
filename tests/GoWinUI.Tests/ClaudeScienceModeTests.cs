using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;

namespace GoWinUI.Tests;

public sealed class ClaudeScienceModeTests
{
    [Fact]
    public async Task ScienceModeHasItsOwnSessionFilterAndRejectsCodingWorkspaceAssignment()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var general = await chats.CreateSessionAsync("ChatGPT session", ChatMode.General);
        var coding = await chats.CreateSessionAsync("Codex session", ChatMode.Coding);
        var science = await chats.CreateSessionAsync("Science session", ChatMode.ClaudeScience);

        Assert.Equal([general.Id], (await chats.ListSessionsAsync(ChatMode.General)).Select(item => item.Id));
        Assert.Equal([coding.Id], (await chats.ListSessionsAsync(ChatMode.Coding)).Select(item => item.Id));
        Assert.Equal([science.Id], (await chats.ListSessionsAsync(ChatMode.ClaudeScience)).Select(item => item.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            chats.SetCodingWorkspacePathAsync(science.Id, environment.Directory));
        Assert.Null((await chats.GetSessionAsync(science.Id))?.CodingWorkspacePath);
        Assert.Single(System.IO.Directory.EnumerateFiles(
            Path.Combine(environment.Directory, "DatabaseBackups"), "*.pre-v49-*.bak"));
        Assert.True(await environment.Get<IGoDatabase>().CheckIntegrityAsync());
    }
}
