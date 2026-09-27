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
        Assert.Equal(ChatMode.General, Assert.Single(await restored.ListSessionsAsync()).ChatMode);
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
        var first = await chats.CreateSessionAsync("Erste Sitzung", ChatMode.Coding);
        var second = await chats.CreateSessionAsync("Zweite Sitzung", ChatMode.Coding);
        var workspace = Path.Combine(environment.Directory, "Mein Projekt Überprüfung");
        await chats.SetCodingWorkspacePathAsync(first.Id, workspace + Path.DirectorySeparatorChar, activateCoding: true);
        await chats.SetCodingWorkspacePathAsync(second.Id, workspace.ToUpperInvariant().Replace('\\', '/'), activateCoding: true);
        var project = Assert.Single(await chats.ListSessionGroupsAsync());
        Assert.Equal("Mein Projekt Überprüfung", project.Name);
        Assert.All(await chats.ListSessionsAsync(), session => Assert.Equal(project.Id, session.SessionGroupId));
        Assert.Equal(project.Id, (await chats.GetOrCreateSessionGroupForWorkspaceAsync(workspace, ChatMode.Coding)).Id);

        await chats.DeleteSessionAsync(first.Id);
        Assert.Equal(project.Id, Assert.Single(await chats.ListSessionGroupsAsync()).Id);
        await chats.DeleteSessionAsync(second.Id);
        await chats.ApplySessionGroupingAsync([]);
        Assert.Empty(await chats.ListSessionGroupsAsync());
        Assert.NotEqual(project.Id, (await chats.GetOrCreateSessionGroupForWorkspaceAsync(workspace, ChatMode.Coding)).Id);
    }

    [Fact]
    public async Task GeneralAndCodingOwnIndependentWorkspaceGroupsForTheSamePath()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var workspace = Path.Combine(environment.Directory, "Gemeinsamer Workspace");
        var general = await chats.CreateSessionAsync("General", ChatMode.General);
        var coding = await chats.CreateSessionAsync("Coding", ChatMode.Coding);

        await chats.SetCodingWorkspacePathAsync(general.Id, workspace, activateCoding: false);
        await chats.SetCodingWorkspacePathAsync(coding.Id, workspace, activateCoding: true);

        var generalGroup = Assert.Single(await chats.ListSessionGroupsAsync(ChatMode.General));
        var codingGroup = Assert.Single(await chats.ListSessionGroupsAsync(ChatMode.Coding));
        Assert.NotEqual(generalGroup.Id, codingGroup.Id);
        Assert.Equal(ChatMode.General, generalGroup.ChatMode);
        Assert.Equal(ChatMode.Coding, codingGroup.ChatMode);
        Assert.Equal(workspace, generalGroup.WorkspacePath);
        Assert.Equal(workspace, codingGroup.WorkspacePath);
        Assert.Equal(generalGroup.Id, (await chats.GetSessionAsync(general.Id))!.SessionGroupId);
        Assert.Equal(codingGroup.Id, (await chats.GetSessionAsync(coding.Id))!.SessionGroupId);

        await chats.SetSessionGroupCollapsedAsync(generalGroup.Id, true);
        Assert.True(Assert.Single(await chats.ListSessionGroupsAsync(ChatMode.General)).IsCollapsed);
        Assert.False(Assert.Single(await chats.ListSessionGroupsAsync(ChatMode.Coding)).IsCollapsed);

        await chats.SetCodingWorkspacePathAsync(general.Id, null, activateCoding: false);
        var detachedGeneral = Assert.IsType<ChatSession>(await chats.GetSessionAsync(general.Id));
        Assert.Null(detachedGeneral.CodingWorkspacePath);
        Assert.Null(detachedGeneral.SessionGroupId);
        Assert.Empty(await chats.ListSessionGroupsAsync(ChatMode.General));
        Assert.Equal(codingGroup.Id, Assert.Single(await chats.ListSessionGroupsAsync(ChatMode.Coding)).Id);
    }

    [Fact]
    public async Task MigrationSplitsMixedModeGroupWithoutChangingConversationState()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var workspace = Path.Combine(environment.Directory, "Historischer Workspace");
        var general = await chats.CreateSessionAsync("General bleibt", ChatMode.General);
        var coding = await chats.CreateSessionAsync("Coding bleibt", ChatMode.Coding);
        await chats.SaveDraftAsync(general.Id, "General-Entwurf");
        await chats.SaveDraftAsync(coding.Id, "Coding-Entwurf");
        var generalMessage = await chats.AddMessageAsync(general.Id, ChatRole.User, "General-Nachricht", MessageStatus.Completed);
        var codingMessage = await chats.AddMessageAsync(coding.Id, ChatRole.User, "Coding-Nachricht", MessageStatus.Completed);
        var before = (await chats.ListSessionsAsync()).ToDictionary(session => session.Id);
        var historicGroupId = Guid.NewGuid();

        await using (var connection = new SqliteConnection($"Data Source={environment.Get<IGoDatabase>().DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TRIGGER IF EXISTS trg_chat_sessions_group_mode_insert;
                DROP TRIGGER IF EXISTS trg_chat_sessions_group_mode_update;
                DROP TRIGGER IF EXISTS trg_chat_session_groups_mode_update;
                DROP INDEX IF EXISTS ix_chat_session_groups_mode_created;
                UPDATE chat_sessions SET session_group_id=NULL WHERE id IN ($general,$coding);
                DELETE FROM chat_session_groups;
                ALTER TABLE chat_session_groups DROP COLUMN chat_mode;
                DELETE FROM schema_migrations WHERE version=46;
                INSERT INTO chat_session_groups(id,name,is_collapsed,created_at,workspace_path)
                VALUES($group,'Historischer Workspace',1,$created,$workspace);
                UPDATE chat_sessions
                SET session_group_id=$group,coding_workspace_path=$workspace
                WHERE id IN ($general,$coding);
                """;
            command.Parameters.AddWithValue("$general", general.Id.ToString("D"));
            command.Parameters.AddWithValue("$coding", coding.Id.ToString("D"));
            command.Parameters.AddWithValue("$group", historicGroupId.ToString("D"));
            command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$workspace", workspace);
            await command.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        await using var reopened = new SqliteDatabase(
            new GoInfrastructureOptions { DataDirectory = environment.Directory },
            NullLogger<SqliteDatabase>.Instance);
        await reopened.InitializeAsync();
        var restored = new SqliteChatRepository(reopened);
        var generalGroup = Assert.Single(await restored.ListSessionGroupsAsync(ChatMode.General));
        var codingGroup = Assert.Single(await restored.ListSessionGroupsAsync(ChatMode.Coding));
        Assert.Equal(historicGroupId, generalGroup.Id);
        Assert.NotEqual(generalGroup.Id, codingGroup.Id);
        Assert.True(generalGroup.IsCollapsed);
        Assert.True(codingGroup.IsCollapsed);
        Assert.Equal(workspace, generalGroup.WorkspacePath);
        Assert.Equal(workspace, codingGroup.WorkspacePath);

        var after = (await restored.ListSessionsAsync()).ToDictionary(session => session.Id);
        Assert.Equal(generalGroup.Id, after[general.Id].SessionGroupId);
        Assert.Equal(codingGroup.Id, after[coding.Id].SessionGroupId);
        foreach (var id in new[] { general.Id, coding.Id })
        {
            Assert.Equal(before[id].Title, after[id].Title);
            Assert.Equal(before[id].Draft, after[id].Draft);
            Assert.Equal(before[id].ConversationRevision, after[id].ConversationRevision);
            Assert.Equal(before[id].UpdatedAt, after[id].UpdatedAt);
        }
        Assert.Equal(generalMessage.Content, (await restored.GetMessageAsync(generalMessage.Id))!.Content);
        Assert.Equal(codingMessage.Content, (await restored.GetMessageAsync(codingMessage.Id))!.Content);
        Assert.True(await reopened.CheckIntegrityAsync());
        await reopened.InitializeAsync();
        Assert.Equal(2, (await restored.ListSessionGroupsAsync()).Count);
    }

    [Fact]
    public async Task MigrationRepairsInvalidProjectIdsAndSeparatesDistinctPathsWithoutChangingHistory()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var first = await chats.CreateSessionAsync("Erhalten A");
        var second = await chats.CreateSessionAsync("Erhalten B");
        var general = await chats.CreateSessionAsync("Allgemeiner Chat");
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
            Assert.Equal(before[session.Id].ChatMode, session.ChatMode);
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
        Assert.Equal(ChatMode.General, after.Single(session => session.Id == second.Id).ChatMode);
        Assert.NotNull(await chats.GetMessageAsync(message.Id));
        Assert.Equal(existing.Id, Assert.Single(await chats.ListSessionGroupsAsync()).Id);
    }
}
