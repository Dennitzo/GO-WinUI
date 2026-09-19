using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using GoWinUI.Infrastructure;
using GoWinUI.Infrastructure.Repositories;
using GoWinUI.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace GoWinUI.Tests;

public sealed class SessionGroupingTests
{
    [Fact]
    public async Task UpgradeRemovesExistingEmptyGroupsAndPreservesPinnedHistory()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Behalten");
        await chats.SetCodingWorkspacePathAsync(session.Id, Path.Combine(environment.Directory, "Behalten"));
        await chats.SetPinnedAsync(session.Id, true);
        var message = await chats.AddMessageAsync(session.Id, ChatRole.User, "Unverändert", MessageStatus.Completed);
        await chats.GetOrCreateSessionGroupForWorkspaceAsync(Path.Combine(environment.Directory, "Leer"));
        await using (var connection = new SqliteConnection($"Data Source={environment.Get<IGoDatabase>().DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM schema_migrations WHERE version=37;";
            await command.ExecuteNonQueryAsync();
        }
        await using var reopened = new SqliteDatabase(new GoInfrastructureOptions { DataDirectory = environment.Directory }, NullLogger<SqliteDatabase>.Instance);
        await reopened.InitializeAsync();
        var restored = new SqliteChatRepository(reopened);
        Assert.Equal("Behalten", Assert.Single(await restored.ListSessionGroupsAsync()).Name);
        Assert.True(Assert.Single(await restored.ListSessionsAsync()).IsPinned);
        var restoredMessage = Assert.IsType<ChatMessage>(await restored.GetMessageAsync(message.Id));
        Assert.Equal(message with { ToolSteps = restoredMessage.ToolSteps }, restoredMessage);
        Assert.Empty(restoredMessage.ToolSteps!);
        Assert.True(await reopened.CheckIntegrityAsync());
    }

    [Fact]
    public async Task WorkspaceAssignmentKeepsGroupUntilLastSessionIsDeleted()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var first = await chats.CreateSessionAsync("Erste Sitzung");
        var second = await chats.CreateSessionAsync("Zweite Sitzung");
        var workspace = Path.Combine(environment.Directory, "Mein Projekt Überprüfung");
        await chats.SetCodingWorkspacePathAsync(first.Id, workspace + Path.DirectorySeparatorChar, activateCoding: true);
        await chats.SetCodingWorkspacePathAsync(second.Id, workspace.ToUpperInvariant().Replace('\\', '/'), activateCoding: true);
        var project = Assert.Single(await chats.ListSessionGroupsAsync());
        Assert.Equal("Mein Projekt Überprüfung", project.Name);
        Assert.All(await chats.ListSessionsAsync(), session => Assert.Equal(project.Id, session.SessionGroupId));
        Assert.Equal(project.Id, (await chats.GetOrCreateSessionGroupForWorkspaceAsync(workspace)).Id);

        await chats.DeleteSessionAsync(first.Id);
        Assert.Equal(project.Id, Assert.Single(await chats.ListSessionGroupsAsync()).Id);
        await chats.DeleteSessionAsync(second.Id);
        await chats.ApplySessionGroupingAsync([]);
        Assert.Empty(await chats.ListSessionGroupsAsync());
        Assert.NotEqual(project.Id, (await chats.GetOrCreateSessionGroupForWorkspaceAsync(workspace)).Id);
    }

    [Fact]
    public async Task MigrationRepairsInvalidProjectIdsAndSeparatesDistinctPathsWithoutChangingHistory()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var first = await chats.CreateSessionAsync("Erhalten A");
        var second = await chats.CreateSessionAsync("Erhalten B");
        var general = await chats.CreateSessionAsync("Allgemeiner Chat");
        await chats.SetPinnedAsync(first.Id, true);
        await chats.SaveDraftAsync(first.Id, "Ungesendeter Entwurf");
        var message = await chats.AddMessageAsync(first.Id, ChatRole.User, "Nachricht bleibt erhalten", MessageStatus.Completed);
        var before = (await chats.ListSessionsAsync()).ToDictionary(session => session.Id);
        var firstPath = Path.Combine(environment.Directory, "ab", "c");
        var secondPath = Path.Combine(environment.Directory, "a", "bc");
        const string malformedId = "0123456789abcdef0123456789abcdef-1234-5678-9012-3456-789abcdef012";
        await using (var connection = new SqliteConnection($"Data Source={environment.Get<IGoDatabase>().DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM schema_migrations WHERE version=36;
                INSERT INTO chat_session_groups(id,name,workspace_path,is_collapsed,created_at)
                VALUES($group,'Alter kaputter Pfadname',$firstPath,1,$created);
                UPDATE chat_sessions SET session_group_id=$group,coding_workspace_path=$firstPath WHERE id=$first;
                UPDATE chat_sessions SET session_group_id=$group,coding_workspace_path=$secondPath WHERE id=$second;
                """;
            command.Parameters.AddWithValue("$group", malformedId);
            command.Parameters.AddWithValue("$firstPath", firstPath);
            command.Parameters.AddWithValue("$secondPath", secondPath);
            command.Parameters.AddWithValue("$first", first.Id.ToString("D"));
            command.Parameters.AddWithValue("$second", second.Id.ToString("D"));
            command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        await using var reopened = new SqliteDatabase(new GoInfrastructureOptions { DataDirectory = environment.Directory }, NullLogger<SqliteDatabase>.Instance);
        await reopened.InitializeAsync();
        var restored = new SqliteChatRepository(reopened);
        var projects = await restored.ListSessionGroupsAsync();
        Assert.Equal(2, projects.Count);
        Assert.Equal("c", projects.Single(project => project.WorkspacePath == firstPath).Name);
        Assert.Equal("bc", projects.Single(project => project.WorkspacePath == secondPath).Name);
        Assert.True(projects.Single(project => project.WorkspacePath == firstPath).IsCollapsed);
        var sessions = await restored.ListSessionsAsync();
        Assert.Equal(3, sessions.Count);
        Assert.Null(sessions.Single(session => session.Id == general.Id).SessionGroupId);
        Assert.NotEqual(sessions.Single(session => session.Id == first.Id).SessionGroupId,
            sessions.Single(session => session.Id == second.Id).SessionGroupId);
        Assert.All(sessions, session =>
        {
            Assert.Equal(before[session.Id].UpdatedAt, session.UpdatedAt);
            Assert.Equal(before[session.Id].ConversationRevision, session.ConversationRevision);
            Assert.Equal(before[session.Id].Draft, session.Draft);
            Assert.Equal(before[session.Id].IsPinned, session.IsPinned);
        });
        Assert.Equal(message.Content, (await restored.GetMessageAsync(message.Id))!.Content);
        Assert.True(await reopened.CheckIntegrityAsync());
        await reopened.InitializeAsync();
        Assert.Equal(projects.Select(project => project.Id), (await restored.ListSessionGroupsAsync()).Select(project => project.Id));
    }

    [Fact]
    public async Task RegroupingPreservesOmittedAssignmentsMessagesPinsAndConversationState()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var first = await chats.CreateSessionAsync("Erstes Projekt");
        var second = await chats.CreateSessionAsync("Zweites Projekt");
        var third = await chats.CreateSessionAsync("Drittes Projekt");
        await chats.SetPinnedAsync(second.Id, true);
        var message = await chats.AddMessageAsync(first.Id, ChatRole.User, "Projektinhalt", MessageStatus.Completed);
        await chats.ApplySessionGroupingAsync([new(null, "Alt", [first.Id]), new(null, "Bleibt", [second.Id, third.Id])]);
        var before = (await chats.ListSessionsAsync()).ToDictionary(session => session.Id);
        var existing = (await chats.ListSessionGroupsAsync()).Single(group => group.Name == "Bleibt");

        await chats.ApplySessionGroupingAsync([new(existing.Id, existing.Name, [first.Id])]);

        var after = await chats.ListSessionsAsync();
        Assert.Equal(3, after.Count);
        Assert.All(after, session =>
        {
            Assert.Equal(existing.Id, session.SessionGroupId);
            Assert.Equal(before[session.Id].UpdatedAt, session.UpdatedAt);
            Assert.Equal(before[session.Id].ConversationRevision, session.ConversationRevision);
        });
        Assert.True(after.Single(session => session.Id == second.Id).IsPinned);
        Assert.NotNull(await chats.GetMessageAsync(message.Id));
        Assert.Equal(existing.Id, Assert.Single(await chats.ListSessionGroupsAsync()).Id);
    }
}
