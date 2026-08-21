using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using Microsoft.Data.Sqlite;

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
    public async Task InternalCampaignMessagesStayOutOfHistoryUntilPromoted()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var repository = environment.Get<IChatRepository>();
        var session = await repository.CreateSessionAsync("Interner Coding-Lauf");
        var scratch = await repository.AddInternalMessageAsync(
            session.Id,
            ChatRole.Assistant,
            "Interner Zwischenstand",
            MessageStatus.Streaming);

        Assert.Empty(await repository.ListMessagesAsync(session.Id));
        Assert.Equal(ChatMessageVisibility.Internal, (await repository.GetMessageAsync(scratch.Id, includeInternal: true))?.Visibility);

        await repository.UpdateMessageAsync(scratch.Id, "### Prozessbericht\n\nCode wurde geändert.", MessageStatus.Completed);
        await repository.SetCodeDiffAsync(scratch.Id, "diff --git a/demo.cs b/demo.cs\n+neu\n");
        await repository.SetMessageVisibilityAsync(scratch.Id, ChatMessageVisibility.Visible);

        var visible = Assert.Single(await repository.ListMessagesAsync(session.Id));
        Assert.Equal(scratch.Id, visible.Id);
        Assert.Equal(ChatMessageVisibility.Visible, visible.Visibility);
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

        Assert.Equal(1, await repository.GetConversationRevisionAsync(session.Id));
        Assert.Equal(1, turn.UserMessage.Revision);
        Assert.Equal(1, turn.AssistantMessage.Revision);
        Assert.Equal(
            new[] { turn.UserMessage.Id, turn.AssistantMessage.Id },
            (await repository.ListMessagesAsync(session.Id)).Select(static message => message.Id).ToArray());

        await repository.UpdateMessageAsync(
            turn.AssistantMessage.Id,
            "### Prozessbericht\n\nDer Workspace wurde geprüft.",
            MessageStatus.Completed);

        Assert.Equal(2, await repository.GetConversationRevisionAsync(session.Id));
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
    public async Task ConversationSnapshotReadsVisibleMessagesArtifactsAndCodingDataFromOneDatabaseRevision()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var artifacts = environment.Get<IChatArtifactRepository>();
        var codingRuns = environment.Get<ICodingRunRepository>();
        var snapshots = environment.Get<IConversationSnapshotRepository>();
        var session = await chats.CreateSessionAsync("Konsistenter Snapshot");
        var turn = await chats.AddTurnAsync(session.Id, "Passe die Datei an.");
        await chats.UpdateMessageAsync(
            turn.AssistantMessage.Id,
            "### Prozessbericht\n\nDie Datei wurde angepasst.",
            MessageStatus.Completed);
        _ = await chats.AddInternalMessageAsync(
            session.Id,
            ChatRole.Assistant,
            "Interner Versuch",
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
                "coding-test",
                null,
                content);
        }

        var localRunId = Guid.NewGuid();
        _ = await codingRuns.AppendAsync(
            localRunId,
            "run-test",
            session.Id,
            turn.AssistantMessage.Id,
            new CodingRunTraceEntry(
                1,
                DateTimeOffset.UtcNow,
                "tool",
                "completed",
                "Datei geändert"));
        await codingRuns.SetCodeDiffAsync(localRunId, "diff --git a/demo.cs b/demo.cs\n+neu\n");

        var snapshot = await snapshots.GetAsync(session.Id);

        Assert.NotNull(snapshot);
        Assert.Equal(await chats.GetConversationRevisionAsync(session.Id), snapshot.Session.ConversationRevision);
        Assert.Equal(
            new[] { turn.UserMessage.Id, turn.AssistantMessage.Id },
            snapshot.Messages.Select(static message => message.Id).ToArray());
        Assert.Single(snapshot.Artifacts[turn.AssistantMessage.Id]);
        Assert.Equal("fortschritt.txt", snapshot.Artifacts[turn.AssistantMessage.Id][0].FileName);
        Assert.NotNull(snapshot.CodingRun);
        Assert.Single(snapshot.CodingRun.Entries);
        Assert.Contains("demo.cs", snapshot.CodingRun.CodeDiff, StringComparison.Ordinal);
        Assert.Contains(
            "demo.cs",
            snapshot.Messages.Single(message => message.Id == turn.AssistantMessage.Id).CodeDiff,
            StringComparison.Ordinal);

        Assert.Equal(1, await codingRuns.MarkRunningInterruptedAsync());
        var interrupted = await snapshots.GetAsync(session.Id);
        Assert.NotNull(interrupted?.CodingRun);
        Assert.Equal("interrupted", interrupted.CodingRun.Status);
        Assert.Equal(0, await codingRuns.MarkRunningInterruptedAsync());
    }

    [Fact]
    public async Task CurrentSchemaPersistsAudiobookStateCodeDiffAndRemovesLegacySpeechCache()
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

        await chats.SetCodeDiffAsync(message.Id, "diff --git a/demo.cs b/demo.cs\n+added\n");

        Assert.Equal(25, GoWinUI.Infrastructure.Storage.SqliteDatabase.CurrentSchemaVersion);
        await using (var connection = new SqliteConnection($"Data Source={environment.Get<IGoDatabase>().DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='speech_preparations';";
            Assert.Equal(0L, (long)(await command.ExecuteScalarAsync() ?? -1L));
        }
        var storedSession = await chats.GetSessionAsync(session.Id);
        Assert.Equal(PersistentToolAction.Audiobook, storedSession?.PersistentToolAction);
        Assert.Equal(AssistantMode.General, storedSession?.AssistantMode);
        var storedMessage = Assert.Single(await chats.ListMessagesAsync(session.Id));
        Assert.Equal(MessageContentProfile.Audiobook, storedMessage.ContentProfile);
        Assert.Contains("demo.cs", storedMessage.CodeDiff, StringComparison.Ordinal);
        Assert.Equal(SessionContextProfile.Audiobook, (await chats.GetSessionContextPreparationAsync(cacheKey))?.Profile);
        var triggers = await environment.Get<IPromptTriggerRepository>().ListAsync();
        Assert.Contains(triggers, item => item.Action == PromptTriggerAction.Audiobook && item.Phrase == "Hörbuch erstellen");
        Assert.Contains(triggers, item => item.Action == PromptTriggerAction.Audiobook && item.Phrase == "Hörbuch fortsetzen");

        await chats.SetPersistentToolActionAsync(session.Id, PersistentToolAction.BricsCad);
        Assert.Equal(PersistentToolAction.BricsCad, (await chats.GetSessionAsync(session.Id))?.PersistentToolAction);
    }
}
