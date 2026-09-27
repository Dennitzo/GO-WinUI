using GoWinUI.Core.Contracts;
using GoWinUI.Core.Extensions;
using GoWinUI.Core.Models;
using GoWinUI.Infrastructure;
using GoWinUI.Infrastructure.Repositories;
using GoWinUI.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace GoWinUI.Tests;

public sealed class ExtensionActionPersistenceTests
{
    [Fact]
    public void ClaudeScienceReceivesSharedExtensionActionsWhilePlanModeRemainsCodingOnly()
    {
        var actions = BuiltInExtensionCatalog.Actions;

        Assert.All(actions.Where(action => action.ActionId != BuiltInActionIds.PlanMode), action =>
            Assert.Contains(ExtensionChatMode.ClaudeScience, action.SupportedChatModes));
        Assert.DoesNotContain(
            ExtensionChatMode.ClaudeScience,
            actions.Single(action => action.ActionId == BuiltInActionIds.PlanMode).SupportedChatModes);
    }

    [Fact]
    public async Task SessionPersistentActionUsesValidatedExtensionIdAndCanBeCleared()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Persistente Extension-Aktion");
        const string thirdPartyAction = "com.example.assistant/continue-story";

        await chats.SetPersistentExtensionActionIdAsync(session.Id, thirdPartyAction);
        Assert.Equal(thirdPartyAction, (await chats.GetSessionAsync(session.Id))!.PersistentExtensionActionId);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            chats.SetPersistentExtensionActionIdAsync(session.Id, "GO.invalid/action"));
        Assert.Equal(thirdPartyAction, (await chats.GetSessionAsync(session.Id))!.PersistentExtensionActionId);

        await chats.SetPersistentExtensionActionIdAsync(session.Id, null);
        Assert.Null((await chats.GetSessionAsync(session.Id))!.PersistentExtensionActionId);

        await using var connection = await OpenAsync(environment.Get<IGoDatabase>().DatabasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT group_concat(name,',') FROM pragma_table_info('chat_sessions');";
        var columns = Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        Assert.Contains("persistent_extension_action_id", columns, StringComparison.Ordinal);
        Assert.DoesNotContain("persistent_tool_action", columns, StringComparison.Ordinal);
        Assert.DoesNotContain("persistent_tool_variant", columns, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryLegacyPromptActionHasOneStableProductNeutralActionId()
    {
        var identifiers = PromptActionExtensionIds.EditableBuiltInActions
            .Select(PromptActionExtensionIds.FromPromptAction)
            .ToArray();

        Assert.Equal(identifiers.Length, identifiers.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(PromptTriggerAction.Extension, PromptActionExtensionIds.EditableBuiltInActions);
        Assert.DoesNotContain(PromptTriggerAction.PlanMode, PromptActionExtensionIds.EditableBuiltInActions);
        Assert.True(PromptActionExtensionIds.TryGetPromptAction(BuiltInActionIds.PlanMode, out var planMode));
        Assert.Equal(PromptTriggerAction.PlanMode, planMode);
        foreach (var action in PromptActionExtensionIds.EditableBuiltInActions)
        {
            var identifier = PromptActionExtensionIds.FromPromptAction(action);
            Assert.True(ExtensionIdentifiers.TryValidateActionId(identifier, null, out var error), error);
            Assert.False(identifier.Contains("gowinui", StringComparison.OrdinalIgnoreCase));
            Assert.False(identifier.StartsWith("go.", StringComparison.OrdinalIgnoreCase));
            Assert.True(PromptActionExtensionIds.TryGetPromptAction(identifier, out var roundTrip));
            Assert.Equal(action, roundTrip);
        }
    }

    [Fact]
    public async Task NewTriggersAndRunsPersistCanonicalExtensionActionIds()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var triggers = environment.Get<IPromptTriggerRepository>();
        var now = DateTimeOffset.UtcNow;
        var trigger = await triggers.CreateAsync(new PromptTrigger(
            Guid.NewGuid(), PromptTriggerAction.DocumentCreate, "Dokumenttest", "Erstellt ein Testdokument.",
            PromptTriggerMatchMode.Exact, true, 12, 0, now, now));

        Assert.Equal(BuiltInActionIds.CreateDocument, trigger.ExtensionActionId);
        Assert.Equal(BuiltInActionIds.CreateDocument, (await triggers.GetAsync(trigger.Id))!.ExtensionActionId);

        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Extension-Aktionslauf");
        var knownMessage = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, string.Empty, MessageStatus.Streaming);
        var unknownMessage = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, string.Empty, MessageStatus.Streaming);
        var runs = environment.Get<IGoAiRunRepository>();
        var codingRun = await runs.CreateAsync(new GoAiRunRecord(
            Guid.NewGuid(), session.Id, knownMessage.Id, PromptTriggerAction.Coding, "known-extension-action",
            null, 0, "creating", null, null, now, now));
        var thirdPartyRun = await runs.CreateAsync(new GoAiRunRecord(
            Guid.NewGuid(), session.Id, unknownMessage.Id, null, "third-party-extension-action",
            null, 0, "creating", null, null, now, now,
            ExtensionActionId: "com.example.assistant/sample-action"));

        Assert.Equal(PromptActionExtensionIds.CodingRun, codingRun.ExtensionActionId);
        Assert.Equal("com.example.assistant/sample-action", thirdPartyRun.ExtensionActionId);
        Assert.Null(thirdPartyRun.Action);
        Assert.Equal("com.example.assistant/sample-action", (await runs.GetAsync(thirdPartyRun.Id))!.ExtensionActionId);

        await using var connection = await OpenAsync(Path.Combine(environment.Directory, "GO.db"));
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT extension_action_id FROM prompt_triggers WHERE id=$trigger),
                (SELECT extension_action_id FROM go_ai_runs WHERE id=$run),
                (SELECT persistent_extension_action_id FROM chat_sessions WHERE id=$session);
            """;
        command.Parameters.AddWithValue("$trigger", trigger.Id.ToString("D"));
        command.Parameters.AddWithValue("$run", codingRun.Id.ToString("D"));
        command.Parameters.AddWithValue("$session", session.Id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(BuiltInActionIds.CreateDocument, reader.GetString(0));
        Assert.Equal(PromptActionExtensionIds.CodingRun, reader.GetString(1));
        Assert.True(reader.IsDBNull(2));

        await reader.DisposeAsync();
        command.CommandText = "SELECT action FROM prompt_triggers WHERE id=$trigger;";
        Assert.True(await command.ExecuteScalarAsync() is null or DBNull);
    }

    [Fact]
    public async Task ThirdPartyPromptTriggerPersistsOnlyItsCanonicalExtensionActionId()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var triggers = environment.Get<IPromptTriggerRepository>();
        var now = DateTimeOffset.UtcNow;
        const string actionId = "com.example.assistant/sample-action";

        await Assert.ThrowsAsync<ArgumentException>(() => triggers.CreateAsync(new PromptTrigger(
            Guid.NewGuid(), PromptTriggerAction.Extension, "Ohne ID", "Ungültig.",
            PromptTriggerMatchMode.Exact, true, 1, 0, now, now)));

        var created = await triggers.CreateAsync(new PromptTrigger(
            Guid.NewGuid(), PromptTriggerAction.Extension, "Erweiterung ausführen", "Führt die Erweiterung aus.",
            PromptTriggerMatchMode.Prefix, true, 15, 0, now, now, actionId));
        var updated = await triggers.UpdateAsync(
            created with { Phrase = "Erweiterung starten" },
            created.Revision);
        var match = await triggers.MatchAsync("Erweiterung starten: Status prüfen");

        Assert.Equal(PromptTriggerAction.Extension, updated.Action);
        Assert.Equal(actionId, updated.ExtensionActionId);
        Assert.NotNull(match);
        Assert.Equal(actionId, match.Trigger.ExtensionActionId);
        Assert.Equal("Status prüfen", match.RemainingPrompt);

        await using var connection = await OpenAsync(environment.Get<IGoDatabase>().DatabasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT extension_action_id,action FROM prompt_triggers WHERE id=$id;";
        command.Parameters.AddWithValue("$id", created.Id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(actionId, reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
    }

    [Fact]
    public async Task EditingMigratedTriggerChangesOnlyCanonicalExtensionActionId()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var triggers = environment.Get<IPromptTriggerRepository>();
        var webTrigger = (await triggers.ListAsync()).First(item => item.Action == PromptTriggerAction.WebSearch);

        var updated = await triggers.UpdateAsync(
            webTrigger with
            {
                Action = PromptTriggerAction.ImageGeneration,
                ExtensionActionId = null,
                Phrase = "Legacy-Aktion bearbeiten",
            },
            webTrigger.Revision);
        var reloaded = await triggers.GetAsync(updated.Id);

        Assert.NotNull(reloaded);
        Assert.Equal(PromptTriggerAction.ImageGeneration, reloaded.Action);
        Assert.Equal(BuiltInActionIds.GenerateImage, reloaded.ExtensionActionId);

        await using var connection = await OpenAsync(environment.Get<IGoDatabase>().DatabasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT extension_action_id,action FROM prompt_triggers WHERE id=$id;";
        command.Parameters.AddWithValue("$id", updated.Id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(BuiltInActionIds.GenerateImage, reader.GetString(0));
        Assert.Equal("webSearch", reader.GetString(1));
    }

    [Fact]
    public async Task MigrationBackfillsLegacyPromptAndRunActionsWithoutLosingRows()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"extension-action-migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var options = new GoInfrastructureOptions { DataDirectory = directory };
        try
        {
            Guid runId;
            Guid sessionId;
            await using (var database = new SqliteDatabase(options, NullLogger<SqliteDatabase>.Instance))
            {
                await database.InitializeAsync();
                var chats = new SqliteChatRepository(database);
                var session = await chats.CreateSessionAsync("Legacy-Aktionslauf");
                sessionId = session.Id;
                var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, string.Empty, MessageStatus.Streaming);
                var runs = new SqliteGoAiRunRepository(database);
                var now = DateTimeOffset.UtcNow;
                var run = await runs.CreateAsync(new GoAiRunRecord(
                    Guid.NewGuid(), session.Id, message.Id, PromptTriggerAction.Coding, "legacy-action-migration",
                    "server-legacy", 4, "running", null, null, now, now));
                runId = run.Id;
            }

            SqliteConnection.ClearAllPools();
            await using (var legacy = await OpenAsync(Path.Combine(directory, options.DatabaseFileName)))
            await using (var command = legacy.CreateCommand())
            {
                command.CommandText = """
                    DROP INDEX IF EXISTS ix_prompt_triggers_extension_action;
                    DROP INDEX IF EXISTS ux_prompt_triggers_extension_phrase;
                    DROP INDEX IF EXISTS ix_go_ai_runs_extension_action;
                    ALTER TABLE prompt_triggers DROP COLUMN extension_action_id;
                    ALTER TABLE go_ai_runs DROP COLUMN extension_action_id;
                    DELETE FROM schema_migrations WHERE version IN (43,45);
                    """;
                await command.ExecuteNonQueryAsync();
            }

            await using (var migrated = new SqliteDatabase(options, NullLogger<SqliteDatabase>.Instance))
            {
                await migrated.InitializeAsync();
                var webTrigger = (await new SqlitePromptTriggerRepository(migrated).ListAsync())
                    .First(item => item.Action == PromptTriggerAction.WebSearch);
                var run = await new SqliteGoAiRunRepository(migrated).GetAsync(runId);

                Assert.Equal(BuiltInActionIds.WebSearch, webTrigger.ExtensionActionId);
                Assert.NotNull(run);
                Assert.Equal(PromptTriggerAction.Coding, run.Action);
                Assert.Equal(PromptActionExtensionIds.CodingRun, run.ExtensionActionId);
            }

            await using var verification = await OpenAsync(Path.Combine(directory, options.DatabaseFileName));
            await using var verify = verification.CreateCommand();
            verify.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM schema_migrations WHERE version IN (43,45)),
                    (SELECT COUNT(*) FROM prompt_triggers WHERE extension_action_id IS NOT NULL),
                    (SELECT extension_action_id FROM go_ai_runs WHERE id=$run),
                    (SELECT COUNT(*) FROM chat_sessions WHERE id=$session);
                """;
            verify.Parameters.AddWithValue("$run", runId.ToString("D"));
            verify.Parameters.AddWithValue("$session", sessionId.ToString("D"));
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(2, reader.GetInt32(0));
            Assert.True(reader.GetInt32(1) > 0);
            Assert.Equal(PromptActionExtensionIds.CodingRun, reader.GetString(2));
            Assert.Equal(1, reader.GetInt32(3));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MigrationRemovesRetiredDevelopmentStateWithoutLosingConversation()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"retired-action-migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var options = new GoInfrastructureOptions { DataDirectory = directory };
        var sessionId = Guid.Empty;
        var messageId = Guid.Empty;
        var runId = Guid.Empty;
        try
        {
            await using (var database = new SqliteDatabase(options, NullLogger<SqliteDatabase>.Instance))
            {
                await database.InitializeAsync();
                var chats = new SqliteChatRepository(database);
                var session = await chats.CreateSessionAsync("Erhaltene Coding-Sitzung", ChatMode.Coding);
                sessionId = session.Id;
                var message = await chats.AddMessageAsync(
                    session.Id, ChatRole.Assistant, "Bereits erzeugte Antwort", MessageStatus.Completed);
                messageId = message.Id;
                var now = DateTimeOffset.UtcNow;
                var run = await new SqliteGoAiRunRepository(database).CreateAsync(new GoAiRunRecord(
                    Guid.NewGuid(), session.Id, message.Id, PromptTriggerAction.Coding, "retired-action-run",
                    "server-retired", 7, "completed", "coding/qwen", null, now, now));
                runId = run.Id;
            }

            SqliteConnection.ClearAllPools();
            await using (var legacy = await OpenAsync(Path.Combine(directory, options.DatabaseFileName)))
            await using (var command = legacy.CreateCommand())
            {
                command.CommandText = """
                    DELETE FROM schema_migrations WHERE version=47;
                    UPDATE chat_sessions
                    SET persistent_extension_action_id='builtin.development/self-development'
                    WHERE id=$session;
                    UPDATE go_ai_runs
                    SET action='development', extension_action_id='builtin.development/self-development', state='finalizing'
                    WHERE id=$run;
                    INSERT INTO prompt_triggers(
                        id,extension_action_id,action,phrase,description,match_mode,is_enabled,priority,revision,created_at,updated_at)
                    VALUES($trigger,'builtin.development/self-development','development','Entwickeln','Alt','exact',1,0,1,$now,$now);
                    """;
                command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
                command.Parameters.AddWithValue("$run", runId.ToString("D"));
                command.Parameters.AddWithValue("$trigger", Guid.NewGuid().ToString("D"));
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync();
            }

            await using (var migrated = new SqliteDatabase(options, NullLogger<SqliteDatabase>.Instance))
                await migrated.InitializeAsync();

            await using var verification = await OpenAsync(Path.Combine(directory, options.DatabaseFileName));
            await using var verify = verification.CreateCommand();
            verify.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM schema_migrations WHERE version=47),
                    (SELECT COUNT(*) FROM chat_sessions WHERE id=$session),
                    (SELECT COUNT(*) FROM chat_messages WHERE id=$message),
                    (SELECT persistent_extension_action_id FROM chat_sessions WHERE id=$session),
                    (SELECT state FROM go_ai_runs WHERE id=$run),
                    (SELECT action FROM go_ai_runs WHERE id=$run),
                    (SELECT extension_action_id FROM go_ai_runs WHERE id=$run),
                    (SELECT COUNT(*) FROM prompt_triggers WHERE extension_action_id='builtin.development/self-development');
                """;
            verify.Parameters.AddWithValue("$session", sessionId.ToString("D"));
            verify.Parameters.AddWithValue("$message", messageId.ToString("D"));
            verify.Parameters.AddWithValue("$run", runId.ToString("D"));
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1, reader.GetInt32(0));
            Assert.Equal(1, reader.GetInt32(1));
            Assert.Equal(1, reader.GetInt32(2));
            Assert.True(reader.IsDBNull(3));
            Assert.Equal("cancelled", reader.GetString(4));
            Assert.True(reader.IsDBNull(5));
            Assert.True(reader.IsDBNull(6));
            Assert.Equal(0, reader.GetInt32(7));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<SqliteConnection> OpenAsync(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }
}
