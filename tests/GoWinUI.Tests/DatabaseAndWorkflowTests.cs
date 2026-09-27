using System.Security.Cryptography;
using System.Text;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Extensions;
using GoWinUI.Core.Models;
using GoWinUI.Infrastructure;
using GoWinUI.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace GoWinUI.Tests;

public sealed class DatabaseAndWorkflowTests
{
    [Fact]
    public async Task FreshDatabaseStartsWithAnEmptyWorkflowLibraryIdempotently()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var database = environment.Get<IGoDatabase>();
        await database.InitializeAsync();
        await database.InitializeAsync();

        Assert.True(await database.CheckIntegrityAsync());
        Assert.Empty(await environment.Get<IWorkflowRepository>().ListAsync());
        Assert.Empty(await environment.Get<IWorkflowRepository>().ListAsync("Trinkwasserinst"));
        Assert.DoesNotContain(
            typeof(SqliteDatabase).Assembly.GetManifestResourceNames(),
            static name => name.Contains("Resources.Workflows", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MigrationsFortyTwoAndFortyFourPromoteModesAndExtensionActionsWithoutDataLoss()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var legacyCoding = await chats.CreateSessionAsync("Legacy Coding");
        var audiobook = await chats.CreateSessionAsync("Hörbuch");
        var general = await chats.CreateSessionAsync("General");
        var workspace = Path.Combine(environment.Directory, "legacy-workspace");
        await chats.SetCodingWorkspacePathAsync(legacyCoding.Id, workspace);
        await chats.SaveDraftAsync(legacyCoding.Id, "Ungesendeter Coding-Entwurf");
        var message = await chats.AddMessageAsync(
            legacyCoding.Id,
            ChatRole.User,
            "Dieser Inhalt muss die Migration überstehen.",
            MessageStatus.Completed);
        await chats.SetPersistentExtensionActionIdAsync(audiobook.Id, BuiltInActionIds.CreateAudiobook);

        await using (var connection = new SqliteConnection($"Data Source={environment.Get<IGoDatabase>().DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TRIGGER IF EXISTS trg_chat_sessions_group_mode_insert;
                DROP TRIGGER IF EXISTS trg_chat_sessions_group_mode_update;
                DROP TRIGGER IF EXISTS trg_chat_session_groups_mode_update;
                DROP INDEX IF EXISTS ix_chat_session_groups_mode_created;
                DROP INDEX IF EXISTS ix_chat_sessions_mode_updated;
                DROP INDEX IF EXISTS ix_chat_sessions_persistent_extension_action;
                ALTER TABLE chat_sessions ADD COLUMN is_pinned INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE chat_sessions ADD COLUMN pinned_at TEXT NULL;
                ALTER TABLE chat_sessions ADD COLUMN persistent_tool_action TEXT NULL;
                ALTER TABLE chat_sessions ADD COLUMN persistent_tool_variant TEXT NULL;
                ALTER TABLE chat_sessions ADD COLUMN persistent_tool_variant_v2 TEXT NULL;
                UPDATE chat_sessions SET persistent_tool_action='code',is_pinned=1,pinned_at=$now WHERE id=$coding;
                UPDATE chat_sessions SET persistent_tool_action='audiobook',is_pinned=1,pinned_at=$now WHERE id=$audiobook;
                ALTER TABLE chat_session_groups DROP COLUMN chat_mode;
                ALTER TABLE chat_sessions DROP COLUMN chat_mode;
                ALTER TABLE chat_sessions DROP COLUMN persistent_extension_action_id;
                DELETE FROM schema_migrations WHERE version IN (42,44,46);
                """;
            command.Parameters.AddWithValue("$coding", legacyCoding.Id.ToString("D"));
            command.Parameters.AddWithValue("$audiobook", audiobook.Id.ToString("D"));
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var migrated = new SqliteDatabase(
                new GoInfrastructureOptions { DataDirectory = environment.Directory },
                NullLogger<SqliteDatabase>.Instance);
            await migrated.InitializeAsync();
            Assert.True(await migrated.CheckIntegrityAsync());
        }

        await using var reopened = new SqliteDatabase(
            new GoInfrastructureOptions { DataDirectory = environment.Directory },
            NullLogger<SqliteDatabase>.Instance);
        await reopened.InitializeAsync();
        var repository = new GoWinUI.Infrastructure.Repositories.SqliteChatRepository(reopened);
        var migratedCoding = Assert.IsType<ChatSession>(await repository.GetSessionAsync(legacyCoding.Id));
        Assert.Equal(ChatMode.Coding, migratedCoding.ChatMode);
        Assert.Null(migratedCoding.PersistentExtensionActionId);
        Assert.Equal(workspace, migratedCoding.CodingWorkspacePath);
        Assert.Equal("Ungesendeter Coding-Entwurf", migratedCoding.Draft);
        Assert.Equal(message.Content, (await repository.GetMessageAsync(message.Id))?.Content);
        Assert.Equal(BuiltInActionIds.CreateAudiobook, (await repository.GetSessionAsync(audiobook.Id))?.PersistentExtensionActionId);
        Assert.Equal(ChatMode.General, (await repository.GetSessionAsync(audiobook.Id))?.ChatMode);
        Assert.Equal(ChatMode.General, (await repository.GetSessionAsync(general.Id))?.ChatMode);
        Assert.Equal(3, (await repository.ListSessionsAsync()).Count);

        await using var verification = new SqliteConnection($"Data Source={reopened.DatabasePath}");
        await verification.OpenAsync();
        await using var verify = verification.CreateCommand();
        verify.CommandText = "SELECT group_concat(name,',') FROM pragma_table_info('chat_sessions');";
        var columns = Convert.ToString(
            await verify.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        Assert.Contains("chat_mode", columns, StringComparison.Ordinal);
        Assert.Contains("persistent_extension_action_id", columns, StringComparison.Ordinal);
        Assert.DoesNotContain("is_pinned", columns, StringComparison.Ordinal);
        Assert.DoesNotContain("pinned_at", columns, StringComparison.Ordinal);
        Assert.DoesNotContain("persistent_tool_action", columns, StringComparison.Ordinal);
        Assert.DoesNotContain("persistent_tool_variant", columns, StringComparison.Ordinal);
        verify.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version IN (42,44,46);";
        Assert.Equal(3L, (long)(await verify.ExecuteScalarAsync() ?? 0L));
    }

    [Fact]
    public async Task CustomWorkflowUsesOptimisticRevisionAndBuiltInsStayReadOnly()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var repository = environment.Get<IWorkflowRepository>();
        var now = DateTimeOffset.UtcNow;
        var created = await repository.CreateAsync(new(
            Guid.Empty, "eigener-workflow", "Eigener Workflow", "Beschreibung", "Allgemein", "Kontext", "{\"schema\":\"assistant.workflow.v1\",\"blocks\":[]}", false, 0, now, now, ["Test"]));
        var updated = await repository.UpdateAsync(created with { Title = "Geändert" }, created.Revision);

        Assert.Equal(2, updated.Revision);
        await Assert.ThrowsAsync<RevisionConflictException>(() => repository.UpdateAsync(updated with { Title = "Konflikt" }, 1));
        var builtIn = await repository.CreateAsync(created with { Id = Guid.Empty, Slug = "readonly-fixture" });
        await using (var connection = new SqliteConnection($"Data Source={environment.Get<IGoDatabase>().DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE workflows SET is_built_in=1 WHERE id=$id;";
            command.Parameters.AddWithValue("$id", builtIn.Id.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<RevisionConflictException>(() => repository.DeleteAsync(builtIn.Id, builtIn.Revision));
        var clone = await repository.CloneAsync(builtIn.Id, "Arbeitskopie");
        Assert.False(clone.IsBuiltIn);
        Assert.Equal("Arbeitskopie", clone.Title);
        Assert.Equal(updated.Id, Assert.Single(await repository.ListAsync("Geänd")).Id);
    }

    [Fact]
    public async Task MigrationThirtyRemovesOnlyRetiredSeedsAndPreservesUserWorkflowsAndChats()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var database = environment.Get<IGoDatabase>();
        var workflows = environment.Get<IWorkflowRepository>();
        var chats = environment.Get<IChatRepository>();
        var now = DateTimeOffset.UtcNow;
        var seedIds = new[]
        {
            Guid.Parse("0e2fd00a-aa2e-5b23-9e06-3a0645a2ecad"),
            Guid.Parse("7ceee4f5-8c41-5ce3-9332-7ccbb7d8d3fb"),
        };
        var userCopy = await workflows.CreateAsync(new(
            Guid.Empty, "eigene-kopie", "Eigene Kopie", "Beschreibung", "Allgemein", "Kontext",
            "{\"schema\":\"go.general.workflow.v1\",\"blocks\":[]}", false, 0, now, now, ["Eigene Daten"]));
        var unrelatedBuiltIn = await workflows.CreateAsync(userCopy with { Id = Guid.Empty, Slug = "unrelated-builtin" });
        foreach (var seedId in seedIds)
        {
            await workflows.CreateAsync(userCopy with { Id = seedId, Slug = seedId.ToString("N"), Title = "LegacySeed" });
        }
        var seededSession = await chats.CreateSessionAsync("Bestehender Workflow-Chat");
        var message = await chats.AddMessageAsync(seededSession.Id, ChatRole.Assistant, "Gespeicherter Workflow-Inhalt", MessageStatus.Completed);
        var customSession = await chats.CreateSessionAsync("Eigener Workflow");

        await using (var connection = new SqliteConnection($"Data Source={database.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE workflows SET is_built_in=1 WHERE id IN ($water,$heating,$unrelated);
                UPDATE chat_sessions SET selected_workflow_id=$water WHERE id=$seededSession;
                UPDATE chat_sessions SET selected_workflow_id=$custom WHERE id=$customSession;
                DELETE FROM schema_migrations WHERE version=30;
                """;
            command.Parameters.AddWithValue("$water", seedIds[0].ToString("D"));
            command.Parameters.AddWithValue("$heating", seedIds[1].ToString("D"));
            command.Parameters.AddWithValue("$unrelated", unrelatedBuiltIn.Id.ToString("D"));
            command.Parameters.AddWithValue("$seededSession", seededSession.Id.ToString("D"));
            command.Parameters.AddWithValue("$custom", userCopy.Id.ToString("D"));
            command.Parameters.AddWithValue("$customSession", customSession.Id.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }

        // Reopen twice to exercise the persisted migration marker, not just the
        // initialized-instance shortcut.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var migrated = new SqliteDatabase(
                new GoInfrastructureOptions { DataDirectory = environment.Directory },
                NullLogger<SqliteDatabase>.Instance);
            await migrated.InitializeAsync();
            Assert.True(await migrated.CheckIntegrityAsync());
        }

        Assert.Equal(2, (await workflows.ListAsync()).Count);
        Assert.Equal(userCopy.ContentJson, (await workflows.GetAsync(userCopy.Id))?.ContentJson);
        Assert.Equal(userCopy.EffectiveTags, (await workflows.GetAsync(userCopy.Id))?.EffectiveTags);
        Assert.True((await workflows.GetAsync(unrelatedBuiltIn.Id))?.IsBuiltIn);
        foreach (var seedId in seedIds)
        {
            Assert.Null(await workflows.GetAsync(seedId));
        }
        Assert.Empty(await workflows.ListAsync("LegacySeed"));
        Assert.Equal(2, (await chats.ListSessionsAsync()).Count);
        var preserved = Assert.IsType<ChatSession>(await chats.GetSessionAsync(seededSession.Id));
        Assert.Null(preserved.SelectedWorkflowId);
        Assert.Equal(message.Content, (await chats.GetMessageAsync(message.Id))?.Content);
        Assert.Equal(userCopy.Id, (await chats.GetSessionAsync(customSession.Id))?.SelectedWorkflowId);

        await using var verification = new SqliteConnection($"Data Source={database.DatabasePath}");
        await verification.OpenAsync();
        await using var verifyCommand = verification.CreateCommand();
        verifyCommand.CommandText = "SELECT COUNT(*) FROM workflow_tags WHERE workflow_id IN ($water,$heating);";
        verifyCommand.Parameters.AddWithValue("$water", seedIds[0].ToString("D"));
        verifyCommand.Parameters.AddWithValue("$heating", seedIds[1].ToString("D"));
        Assert.Equal(0L, (long)(await verifyCommand.ExecuteScalarAsync() ?? -1L));
        verifyCommand.Parameters.Clear();
        verifyCommand.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version=30;";
        Assert.Equal(1L, (long)(await verifyCommand.ExecuteScalarAsync() ?? -1L));
    }

    [Theory]
    [InlineData("0e2fd00a-aa2e-5b23-9e06-3a0645a2ecad")]
    [InlineData("7ceee4f5-8c41-5ce3-9332-7ccbb7d8d3fb")]
    public async Task MigrationThirtyPreservesUserOwnedWorkflowEvenWithRetiredSeedId(string id)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workflows = environment.Get<IWorkflowRepository>();
        var now = DateTimeOffset.UtcNow;
        var userWorkflow = await workflows.CreateAsync(new(
            Guid.Parse(id), "user-owned", "Eigener Workflow", "Beschreibung", "Allgemein", "Kontext",
            "{\"schema\":\"go.general.workflow.v1\",\"blocks\":[]}", false, 0, now, now));
        await using (var connection = new SqliteConnection($"Data Source={environment.Get<IGoDatabase>().DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM schema_migrations WHERE version=30;";
            await command.ExecuteNonQueryAsync();
        }

        await using var migrated = new SqliteDatabase(
            new GoInfrastructureOptions { DataDirectory = environment.Directory },
            NullLogger<SqliteDatabase>.Instance);
        await migrated.InitializeAsync();

        Assert.Equal(userWorkflow.Id, Assert.Single(await workflows.ListAsync()).Id);
    }

    [Fact]
    public async Task CodingWorkspaceIsBoundToOneSessionAndPersistsAcrossDatabaseReopen()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var first = await chats.CreateSessionAsync("Projekt A");
        var second = await chats.CreateSessionAsync("Projekt B");
        var firstRoot = Path.Combine(environment.Directory, "projekt-a");
        var secondRoot = Path.Combine(environment.Directory, "projekt-b");
        await chats.SetCodingWorkspacePathAsync(first.Id, $"  {firstRoot}  ");
        await chats.SetCodingWorkspacePathAsync(second.Id, secondRoot);
        var message = await chats.AddMessageAsync(first.Id, ChatRole.User, "Nachricht aus A", MessageStatus.Completed);

        await using var reopened = new SqliteDatabase(
            new GoInfrastructureOptions { DataDirectory = environment.Directory }, NullLogger<SqliteDatabase>.Instance);
        await reopened.InitializeAsync();
        var repository = new GoWinUI.Infrastructure.Repositories.SqliteChatRepository(reopened);
        Assert.Equal(firstRoot, (await repository.GetSessionAsync(first.Id))?.CodingWorkspacePath);
        Assert.Equal(secondRoot, (await repository.GetSessionAsync(second.Id))?.CodingWorkspacePath);
        Assert.Equal(2, (await repository.ListSessionsAsync()).Count);
        var snapshotRepository = new GoWinUI.Infrastructure.Repositories.SqliteConversationSnapshotRepository(reopened);
        Assert.Equal(firstRoot, (await snapshotRepository.GetAsync(first.Id))?.Session.CodingWorkspacePath);
        Assert.Equal(message.Id, Assert.Single(await repository.ListMessagesAsync(first.Id)).Id);

        await repository.SetCodingWorkspacePathAsync(first.Id, null);
        Assert.Null((await repository.GetSessionAsync(first.Id))?.CodingWorkspacePath);
        Assert.Equal(secondRoot, (await repository.GetSessionAsync(second.Id))?.CodingWorkspacePath);
        await Assert.ThrowsAsync<ArgumentException>(() => repository.SetCodingWorkspacePathAsync(first.Id, "relative-project"));
    }

    [Fact]
    public async Task MigrationThirtyOneLeavesExistingChatsUnboundAndPreservesTheirContent()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Bestehendes Coding-Projekt", ChatMode.Coding);
        var message = await chats.AddMessageAsync(session.Id, ChatRole.User, "Gespeicherter Inhalt", MessageStatus.Completed);
        await using (var connection = new SqliteConnection($"Data Source={environment.Get<IGoDatabase>().DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE chat_sessions DROP COLUMN coding_workspace_path; DELETE FROM schema_migrations WHERE version=31;";
            await command.ExecuteNonQueryAsync();
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var reopened = new SqliteDatabase(
                new GoInfrastructureOptions { DataDirectory = environment.Directory }, NullLogger<SqliteDatabase>.Instance);
            await reopened.InitializeAsync();
            Assert.True(await reopened.CheckIntegrityAsync());
        }

        var restored = Assert.IsType<ChatSession>(await chats.GetSessionAsync(session.Id));
        Assert.Null(restored.CodingWorkspacePath);
        Assert.Equal(ChatMode.Coding, restored.ChatMode);
        Assert.Null(restored.PersistentExtensionActionId);
        Assert.Equal(session.Title, restored.Title);
        Assert.Equal(message.Id, Assert.Single(await chats.ListMessagesAsync(session.Id)).Id);
    }

    [Fact]
    public async Task AiMessageContextSummaryIsPersistedWithItsSessionMessage()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var repository = environment.Get<IChatRepository>();
        var session = await repository.CreateSessionAsync("Workflow-Sitzung");
        var message = await repository.AddMessageAsync(
            session.Id,
            ChatRole.Assistant,
            "## Projektstart\n\nDie Räume werden vorbereitet.",
            MessageStatus.Completed);

        await repository.SetMessageContextSummaryAsync(message.Id, "Kurzer Projektstart für die Raum-Erstellung.");

        var stored = Assert.Single(await repository.ListMessagesAsync(session.Id));
        Assert.Equal("Kurzer Projektstart für die Raum-Erstellung.", stored.ContextSummary);
    }

    [Fact]
    public async Task ChatTurnAndEveryCommittedMutationAdvanceDatabaseRevisions()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var repository = environment.Get<IChatRepository>();
        var session = await repository.CreateSessionAsync("Revisionslauf");

        var turn = await repository.AddTurnAsync(
            session.Id,
            "Prüfe den Workspace.",
            MessageContentProfile.General);

        Assert.Equal(1, (await repository.GetSessionAsync(session.Id))?.ConversationRevision);
        Assert.Equal(1, turn.UserMessage.Revision);
        Assert.Equal(1, turn.AssistantMessage.Revision);
        Assert.Equal(
            new[] { turn.UserMessage.Id, turn.AssistantMessage.Id },
            (await repository.ListMessagesAsync(session.Id)).Select(static message => message.Id).ToArray());

        await repository.UpdateMessageAsync(
            turn.AssistantMessage.Id,
            "### Prozessbericht\n\nDer Workspace wurde geprüft.",
            MessageStatus.Completed);

        Assert.Equal(2, (await repository.GetSessionAsync(session.Id))?.ConversationRevision);
        Assert.Equal(2, (await repository.GetMessageAsync(turn.AssistantMessage.Id))?.Revision);
    }

    [Fact]
    public async Task DurableChatBoundaryRemovesLegacyTitleMarkersAndEmptyTerminalCards()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var repository = environment.Get<IChatRepository>();
        var session = await repository.CreateSessionAsync("Bereinigung");
        var visible = await repository.AddMessageAsync(
            session.Id,
            ChatRole.Assistant,
            "GO_SESSION_TITLE: Unsichtbarer Titel\n\n### Prozessbericht\n**Aktion:** **GO\\_SESSION\\_TITLE:** Sichtbarer Inhalt",
            MessageStatus.Completed);
        _ = await repository.AddMessageAsync(
            session.Id,
            ChatRole.Assistant,
            string.Empty,
            MessageStatus.Failed);

        var stored = await repository.GetMessageAsync(visible.Id);
        Assert.NotNull(stored);
        Assert.DoesNotContain("SESSION", stored.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("**Aktion:** Sichtbarer Inhalt", stored.Content, StringComparison.Ordinal);
        Assert.Equal(1, await repository.DeleteEmptyTerminalMessagesAsync());
        Assert.Single(await repository.ListMessagesAsync(session.Id));
    }

    [Fact]
    public async Task ConversationSnapshotReadsVisibleMessagesAndArtifactsFromOneDatabaseRevision()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var artifacts = environment.Get<IChatArtifactRepository>();
        var snapshots = environment.Get<IConversationSnapshotRepository>();
        var session = await chats.CreateSessionAsync("Konsistenter Snapshot");
        var turn = await chats.AddTurnAsync(session.Id, "Passe die Datei an.");
        await chats.UpdateMessageAsync(
            turn.AssistantMessage.Id,
            "### Prozessbericht\n\nDie Datei wurde angepasst.",
            MessageStatus.Completed);
        var artifactBytes = Encoding.UTF8.GetBytes("Vorschauinhalt");
        var artifactSha = Convert.ToHexString(SHA256.HashData(artifactBytes)).ToLowerInvariant();
        await using (var content = new MemoryStream(artifactBytes, writable: false))
        {
            _ = await artifacts.ImportAsync(
                turn.AssistantMessage.Id,
                "artifact-test",
                "fortschritt.txt",
                "text/plain",
                artifactSha,
                artifactBytes.LongLength,
                "test",
                null,
                null,
                content);
        }

        var snapshot = await snapshots.GetAsync(session.Id);

        Assert.NotNull(snapshot);
        Assert.Equal(
            (await chats.GetSessionAsync(session.Id))?.ConversationRevision,
            snapshot.Session.ConversationRevision);
        Assert.Equal(
            new[] { turn.UserMessage.Id, turn.AssistantMessage.Id },
            snapshot.Messages.Select(static message => message.Id).ToArray());
        Assert.Single(snapshot.Artifacts[turn.AssistantMessage.Id]);
        Assert.Equal("fortschritt.txt", snapshot.Artifacts[turn.AssistantMessage.Id][0].FileName);
    }

    [Fact]
    public async Task AssistantMessageCanBeResetForAutomaticRetry()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("AI-Lauf mit Retry");
        var turn = await chats.AddTurnAsync(session.Id, "Bearbeite das Projekt.");

        await chats.UpdateMessageAsync(
            turn.AssistantMessage.Id,
            "Alter Zwischenstand.",
            MessageStatus.Interrupted);

        await chats.ResetMessageForRetryAsync(turn.AssistantMessage.Id);
        var reset = await chats.GetMessageAsync(turn.AssistantMessage.Id);
        Assert.NotNull(reset);
        Assert.Equal(string.Empty, reset.Content);
        Assert.Equal(MessageStatus.Streaming, reset.Status);
    }

    [Fact]
    public async Task AutomaticRetryRejectsUserMessageWithoutAdvancingConversationRevision()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Ungültiger Retry-Anker");
        var turn = await chats.AddTurnAsync(session.Id, "Diese Nachricht darf nicht zurückgesetzt werden.");
        var revisionBefore = (await chats.GetSessionAsync(session.Id))?.ConversationRevision;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => chats.ResetMessageForRetryAsync(turn.UserMessage.Id));

        Assert.Equal(
            revisionBefore,
            (await chats.GetSessionAsync(session.Id))?.ConversationRevision);
        Assert.Equal(
            "Diese Nachricht darf nicht zurückgesetzt werden.",
            (await chats.GetMessageAsync(turn.UserMessage.Id))?.Content);
    }

}
