using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using GoWinUI.Infrastructure;
using GoWinUI.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace GoWinUI.Tests;

public sealed class DatabaseAndWorkflowTests
{
    [Fact]
    public async Task FreshDatabaseSeedsBothCompleteGeneralWorkflowsIdempotently()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var database = environment.Get<IGoDatabase>();
        await database.InitializeAsync();
        await database.InitializeAsync();

        Assert.True(await database.CheckIntegrityAsync());
        var workflows = await environment.Get<IWorkflowRepository>().ListAsync();
        Assert.Equal(2, workflows.Count);
        Assert.All(workflows, static workflow => Assert.True(workflow.IsBuiltIn));
        Assert.Contains(workflows, static workflow => workflow.Slug == "bemessung_der_trinkwasserinstallation_nach_din_1988_300");
        Assert.Contains(workflows, static workflow => workflow.Slug == "heizlastberechnung_nach_din_en_12831");
        Assert.Single(await environment.Get<IWorkflowRepository>().ListAsync("Trinkwasserinst"));
        foreach (var workflow in workflows)
        {
            using var json = JsonDocument.Parse(workflow.ContentJson);
            Assert.Equal("barebone.general.workflow.v1", json.RootElement.GetProperty("schema").GetString());
            Assert.True(json.RootElement.GetProperty("display").GetProperty("blocks").GetArrayLength() >= 8);
            Assert.True(json.RootElement.GetProperty("formulas").GetArrayLength() > 0);
            Assert.True(json.RootElement.GetProperty("sourceRefs").GetArrayLength() > 0);
            Assert.NotEmpty(workflow.EffectiveTags);
        }
    }

    [Fact]
    public async Task CustomWorkflowUsesOptimisticRevisionAndBuiltInsStayReadOnly()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var repository = environment.Get<IWorkflowRepository>();
        var now = DateTimeOffset.UtcNow;
        var created = await repository.CreateAsync(new(
            Guid.Empty, "eigener-workflow", "Eigener Workflow", "Beschreibung", "Allgemein", "Kontext", "{\"schema\":\"go.general.workflow.v1\",\"blocks\":[]}", false, 0, now, now, ["Test"]));
        var updated = await repository.UpdateAsync(created with { Title = "Geändert" }, created.Revision);

        Assert.Equal(2, updated.Revision);
        await Assert.ThrowsAsync<RevisionConflictException>(() => repository.UpdateAsync(updated with { Title = "Konflikt" }, 1));
        var builtIn = (await repository.ListAsync()).First(static item => item.IsBuiltIn);
        await Assert.ThrowsAsync<RevisionConflictException>(() => repository.DeleteAsync(builtIn.Id, builtIn.Revision));
        var clone = await repository.CloneAsync(builtIn.Id, "Arbeitskopie");
        Assert.False(clone.IsBuiltIn);
        Assert.Equal("Arbeitskopie", clone.Title);
        Assert.Equal(updated.Id, Assert.Single(await repository.ListAsync("Geänd")).Id);
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

    [Fact]
    public async Task CurrentSchemaPersistsAudiobookStateAndRemovesLegacySpeechCache()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Hörbuch");
        await chats.SetPersistentToolActionAsync(session.Id, PersistentToolAction.Audiobook);
        var message = await chats.AddMessageAsync(
            session.Id,
            ChatRole.Assistant,
            "Der Regen strich über die Dächer.",
            MessageStatus.Completed,
            MessageContentProfile.Audiobook);
        var revision = new string('a', 64);
        var cacheKey = new string('b', 64);
        await chats.SaveSessionContextPreparationAsync(new(
            cacheKey,
            session.Id,
            revision,
            "openai/gpt-oss-20b",
            12_000,
            message.Id,
            1,
            "STORY_CHRONICLE\nCONTINUATION_ANCHOR: Der Regen strich über die Dächer.",
            DateTimeOffset.UtcNow,
            SessionContextProfile.Audiobook));

        Assert.Equal(29, GoWinUI.Infrastructure.Storage.SqliteDatabase.CurrentSchemaVersion);
        await using (var connection = new SqliteConnection($"Data Source={environment.Get<IGoDatabase>().DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='speech_preparations';";
            Assert.Equal(0L, (long)(await command.ExecuteScalarAsync() ?? -1L));
        }
        var storedSession = await chats.GetSessionAsync(session.Id);
        Assert.Equal(PersistentToolAction.Audiobook, storedSession?.PersistentToolAction);
        var storedMessage = Assert.Single(await chats.ListMessagesAsync(session.Id));
        Assert.Equal(MessageContentProfile.Audiobook, storedMessage.ContentProfile);
        Assert.Equal(SessionContextProfile.Audiobook, (await chats.GetSessionContextPreparationAsync(cacheKey))?.Profile);
        var triggers = await environment.Get<IPromptTriggerRepository>().ListAsync();
        Assert.Contains(triggers, item => item.Action == PromptTriggerAction.Audiobook && item.Phrase == "Hörbuch erstellen");
        Assert.Contains(triggers, item => item.Action == PromptTriggerAction.Audiobook && item.Phrase == "Hörbuch fortsetzen");

        await chats.SetPersistentToolActionAsync(session.Id, PersistentToolAction.BricsCad);
        Assert.Equal(PersistentToolAction.BricsCad, (await chats.GetSessionAsync(session.Id))?.PersistentToolAction);
    }

    [Fact]
    public async Task MigrationTwentyNineRemovesLegacyCodingSchemaAndPreservesVisibleChatAndGeneralRuns()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var runs = environment.Get<IGoAiRunRepository>();
        var session = await chats.CreateSessionAsync("Bestehende Sitzung");
        var visibleMessage = await chats.AddMessageAsync(
            session.Id,
            ChatRole.Assistant,
            "Dieser bestehende Chattext muss erhalten bleiben.",
            MessageStatus.Completed);
        var legacyCodeMessage = await chats.AddMessageAsync(
            session.Id,
            ChatRole.Assistant,
            "Auch sichtbarer Text eines alten Coding-Laufs bleibt erhalten.",
            MessageStatus.Completed);
        var internalMessage = await chats.AddMessageAsync(
            session.Id,
            ChatRole.Assistant,
            "Interner Coding-Zwischenstand",
            MessageStatus.Completed);
        var cacheKey = new string('c', 64);
        await chats.SaveSessionContextPreparationAsync(new(
            cacheKey,
            session.Id,
            new string('d', 64),
            "test-model",
            4_096,
            visibleMessage.Id,
            1,
            "Vorbereiteter Kontext",
            DateTimeOffset.UtcNow,
            SessionContextProfile.General));
        var now = DateTimeOffset.UtcNow;
        var generalRun = await runs.CreateAsync(new GoAiRunRecord(
            Guid.NewGuid(),
            session.Id,
            visibleMessage.Id,
            PromptTriggerAction.BricsCad,
            "general-run",
            "server-general",
            4,
            "completed",
            "general-model",
            null,
            now,
            now));
        var legacyCodeRun = await runs.CreateAsync(new GoAiRunRecord(
            Guid.NewGuid(),
            session.Id,
            legacyCodeMessage.Id,
            PromptTriggerAction.BricsCad,
            "legacy-code-run",
            "server-legacy-code",
            5,
            "running",
            "legacy-model",
            null,
            now,
            now));

        await using (var connection = new SqliteConnection($"Data Source={environment.Get<IGoDatabase>().DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE chat_sessions
                    ADD COLUMN assistant_mode TEXT NOT NULL DEFAULT 'general'
                    CHECK(assistant_mode IN ('general','code'));
                ALTER TABLE chat_sessions ADD COLUMN workspace_path TEXT NULL;
                ALTER TABLE chat_sessions ADD COLUMN workspace_fingerprint TEXT NULL;

                ALTER TABLE chat_messages ADD COLUMN code_diff TEXT NULL;
                ALTER TABLE chat_messages
                    ADD COLUMN visibility TEXT NOT NULL DEFAULT 'visible'
                    CHECK(visibility IN ('visible','internal'));
                ALTER TABLE chat_messages
                    ADD COLUMN message_phase TEXT NULL
                    CHECK(message_phase IS NULL OR message_phase IN ('commentary','finalanswer'));
                ALTER TABLE chat_messages ADD COLUMN source_run_id TEXT NULL;
                ALTER TABLE chat_messages ADD COLUMN source_item_id TEXT NULL;
                ALTER TABLE chat_messages
                    ADD COLUMN source_delta_sequence INTEGER NOT NULL DEFAULT 0
                    CHECK(source_delta_sequence>=0);
                ALTER TABLE go_ai_runs
                    ADD COLUMN final_message_id TEXT NULL REFERENCES chat_messages(id) ON DELETE SET NULL;

                CREATE TABLE coding_campaigns(id TEXT PRIMARY KEY) STRICT;
                CREATE TABLE coding_campaign_iterations(
                    id TEXT PRIMARY KEY,
                    campaign_id TEXT REFERENCES coding_campaigns(id) ON DELETE CASCADE
                ) STRICT;
                CREATE TABLE coding_campaign_solution_messages(
                    id TEXT PRIMARY KEY,
                    campaign_id TEXT REFERENCES coding_campaigns(id) ON DELETE CASCADE
                ) STRICT;
                CREATE TABLE coding_runs(id TEXT PRIMARY KEY) STRICT;
                CREATE TABLE coding_run_entries(
                    id TEXT PRIMARY KEY,
                    run_id TEXT REFERENCES coding_runs(id) ON DELETE CASCADE
                ) STRICT;

                UPDATE chat_sessions
                SET assistant_mode='code',
                    workspace_path='C:\legacy-workspace',
                    workspace_fingerprint='legacy-fingerprint',
                    persistent_tool_action='code'
                WHERE id=$sessionId;
                UPDATE session_context_preparations
                SET profile='code'
                WHERE cache_key=$cacheKey;
                UPDATE chat_messages
                SET visibility='internal',
                    message_phase='commentary',
                    source_run_id='legacy-run',
                    source_item_id='legacy-item',
                    source_delta_sequence=7,
                    code_diff='legacy diff'
                WHERE id=$internalMessageId;
                UPDATE go_ai_runs
                SET action='code', state='running', final_message_id=$legacyCodeMessageId
                WHERE id=$legacyCodeRunId;
                INSERT INTO prompt_triggers(
                    id,action,phrase,description,match_mode,is_enabled,priority,revision,created_at,updated_at)
                VALUES($triggerId,'code','Legacy-Code','Alter Trigger','prefix',1,999,1,$now,$now);
                DELETE FROM schema_migrations WHERE version IN (28,29);
                """;
            command.Parameters.AddWithValue("$sessionId", session.Id.ToString("D"));
            command.Parameters.AddWithValue("$cacheKey", cacheKey);
            command.Parameters.AddWithValue("$internalMessageId", internalMessage.Id.ToString("D"));
            command.Parameters.AddWithValue("$legacyCodeMessageId", legacyCodeMessage.Id.ToString("D"));
            command.Parameters.AddWithValue("$legacyCodeRunId", legacyCodeRun.Id.ToString("D"));
            command.Parameters.AddWithValue("$triggerId", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        await using (var migratedDatabase = new SqliteDatabase(
                         new GoInfrastructureOptions { DataDirectory = environment.Directory },
                         NullLogger<SqliteDatabase>.Instance))
        {
            await migratedDatabase.InitializeAsync();
        }

        var restoredSession = Assert.IsType<ChatSession>(await chats.GetSessionAsync(session.Id));
        Assert.Null(restoredSession.PersistentToolAction);
        Assert.Equal(
            [visibleMessage.Id, legacyCodeMessage.Id],
            (await chats.ListMessagesAsync(session.Id)).Select(static message => message.Id).ToArray());
        Assert.Null(await chats.GetMessageAsync(internalMessage.Id));
        Assert.Equal(
            SessionContextProfile.General,
            (await chats.GetSessionContextPreparationAsync(cacheKey))?.Profile);
        Assert.DoesNotContain(
            await environment.Get<IPromptTriggerRepository>().ListAsync(),
            static trigger => trigger.Phrase == "Legacy-Code");
        var restoredGeneralRun = Assert.IsType<GoAiRunRecord>(await runs.GetAsync(generalRun.Id));
        Assert.Equal(PromptTriggerAction.BricsCad, restoredGeneralRun.Action);
        Assert.Equal("completed", restoredGeneralRun.State);
        Assert.Equal("general-model", restoredGeneralRun.SelectedModel);
        var restoredLegacyRun = Assert.IsType<GoAiRunRecord>(await runs.GetAsync(legacyCodeRun.Id));
        Assert.Null(restoredLegacyRun.Action);
        Assert.Equal("cancelled", restoredLegacyRun.State);
        Assert.Equal("client.coding_mode_removed", restoredLegacyRun.ErrorCode);
        Assert.DoesNotContain(await runs.ListResumableAsync(), candidate => candidate.Id == legacyCodeRun.Id);

        await using var verification = new SqliteConnection($"Data Source={environment.Get<IGoDatabase>().DatabasePath}");
        await verification.OpenAsync();
        await using var verificationCommand = verification.CreateCommand();
        verificationCommand.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM pragma_table_info('chat_sessions')
                 WHERE name IN ('assistant_mode','workspace_path','workspace_fingerprint')) +
                (SELECT COUNT(*) FROM pragma_table_info('chat_messages')
                 WHERE name IN ('code_diff','visibility','message_phase','source_run_id','source_item_id','source_delta_sequence')) +
                (SELECT COUNT(*) FROM pragma_table_info('go_ai_runs')
                 WHERE name='final_message_id');
            """;
        Assert.Equal(0L, (long)(await verificationCommand.ExecuteScalarAsync() ?? -1L));

        verificationCommand.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type='table'
              AND name IN ('coding_campaigns','coding_campaign_iterations',
                           'coding_campaign_solution_messages','coding_runs','coding_run_entries');
            """;
        Assert.Equal(0L, (long)(await verificationCommand.ExecuteScalarAsync() ?? -1L));
        verificationCommand.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version=29;";
        Assert.Equal(1L, (long)(await verificationCommand.ExecuteScalarAsync() ?? -1L));
    }
}
