using System.Net;
using GoWinUI.App.Services;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using GoWinUI.Infrastructure;
using GoWinUI.Infrastructure.Repositories;
using GoWinUI.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace GoWinUI.Tests;

public sealed class CodingRunWorkspaceTests
{
    [Fact]
    public async Task OriginalWorkspaceSurvivesSessionChangesReloadsAndRunUpdatesAndCannotBeReboundByRetry()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var firstWorkspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "original-project")).FullName;
        var otherWorkspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "other-project")).FullName;
        var chats = environment.Get<IChatRepository>();
        var runs = environment.Get<IGoAiRunRepository>();
        var session = await chats.CreateSessionAsync("Durable project binding");
        await chats.SetCodingWorkspacePathAsync(session.Id, firstWorkspace, activateCoding: true);
        var turn = await chats.AddTurnAsync(session.Id, "Keep working in this project");
        var initial = await runs.BeginAttemptAsync(Create(session.Id, turn.AssistantMessage.Id, firstWorkspace));
        await runs.UpdateAsync(initial.Id, "server-original-project", 17, "waitingForClient");
        await chats.UpdateMessageAsync(turn.AssistantMessage.Id, "Already performed work", MessageStatus.Streaming);
        await chats.SetCodingWorkspacePathAsync(session.Id, otherWorkspace, activateCoding: true);

        await using var database = new SqliteDatabase(new GoInfrastructureOptions { DataDirectory = environment.Directory }, NullLogger<SqliteDatabase>.Instance);
        await database.InitializeAsync();
        var reopened = new SqliteGoAiRunRepository(database);
        var restored = Assert.IsType<GoAiRunRecord>(await reopened.GetAsync(initial.Id));
        Assert.Equal(otherWorkspace, (await chats.GetSessionAsync(session.Id))!.CodingWorkspacePath);
        Assert.Equal(firstWorkspace, restored.WorkspacePath);
        Assert.Equal(firstWorkspace, GoAiAssistantService.ResolvePersistedCodingWorkspace(restored));
        await reopened.UpdateAsync(initial.Id, restored.ServerRunId, 18, "running");
        Assert.Equal(firstWorkspace, (await reopened.GetByServerRunIdAsync(restored.ServerRunId!))!.WorkspacePath);

        await Assert.ThrowsAsync<InvalidDataException>(() => reopened.BeginAttemptAsync(
            Create(session.Id, turn.AssistantMessage.Id, otherWorkspace)));
        var unchanged = (await reopened.GetAsync(initial.Id))!;
        Assert.Equal(restored.ServerRunId, unchanged.ServerRunId);
        Assert.Equal(18, unchanged.LastEventId);
        Assert.Equal("Already performed work", (await chats.GetMessageAsync(turn.AssistantMessage.Id))!.Content);

        var retry = await reopened.BeginAttemptAsync(Create(session.Id, turn.AssistantMessage.Id, firstWorkspace));
        Assert.Equal(initial.Id, retry.Id);
        Assert.Equal(firstWorkspace, retry.WorkspacePath);
        Assert.Null(retry.ServerRunId);
    }

    [Fact]
    public async Task MigrationThirtyThreeLeavesLegacyRunWorkspaceUnknownInsteadOfUsingTheCurrentSession()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var runs = environment.Get<IGoAiRunRepository>();
        var session = await chats.CreateSessionAsync("Legacy project is unknown");
        await chats.SetCodingWorkspacePathAsync(session.Id, environment.Directory, activateCoding: true);
        var turn = await chats.AddTurnAsync(session.Id, "Old task");
        var original = await runs.CreateAsync(Create(session.Id, turn.AssistantMessage.Id, null));
        await using (var connection = new SqliteConnection($"Data Source={environment.Get<IGoDatabase>().DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE go_ai_runs DROP COLUMN workspace_path; DELETE FROM schema_migrations WHERE version=33;";
            await command.ExecuteNonQueryAsync();
        }
        for (var pass = 0; pass < 2; pass++)
        {
            await using var database = new SqliteDatabase(new GoInfrastructureOptions { DataDirectory = environment.Directory }, NullLogger<SqliteDatabase>.Instance);
            await database.InitializeAsync();
            Assert.True(await database.CheckIntegrityAsync());
            var restored = (await new SqliteGoAiRunRepository(database).GetAsync(original.Id))!;
            Assert.Null(restored.WorkspacePath);
            Assert.Equal(original.IdempotencyKey, restored.IdempotencyKey);
            Assert.Throws<InvalidDataException>(() => GoAiAssistantService.ResolvePersistedCodingWorkspace(restored));
        }
        Assert.True(SqliteDatabase.CurrentSchemaVersion >= 33);
    }

    [Fact]
    public async Task LegacyResumePersistsAnExplicitFailureAndCancelsOnlyItsRunWithoutStreamingOrDispatchingTools()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var runs = environment.Get<IGoAiRunRepository>();
        var session = await chats.CreateSessionAsync("Legacy resume");
        await chats.SetCodingWorkspacePathAsync(session.Id, environment.Directory, activateCoding: true);
        var turn = await chats.AddTurnAsync(session.Id, "Do not guess the old project");
        var legacy = await runs.CreateAsync(Create(session.Id, turn.AssistantMessage.Id, null) with
        {
            ServerRunId = "server-legacy-workspace", State = "waitingForClient",
        });
        await chats.UpdateMessageAsync(turn.AssistantMessage.Id, "Saved progress", MessageStatus.Streaming);
        await chats.SaveToolStepAsync(turn.AssistantMessage.Id, new("pending-edit", "coding.edit", "running", "Not yet acknowledged"));
        var store = environment.Get<ISettingsStore>();
        await store.SaveAsync(new AppSettings { IsAiConnectionEnabled = true, GoAiServerUrl = "http://127.0.0.1:65000" });
        using var settings = new SettingsCoordinator(store);
        await settings.InitializeAsync();
        var requests = new List<string>();
        using var connection = new GoAiConnectionService(settings, NullLogger<GoAiConnectionService>.Instance,
            () => new CancelOnlyHandler(requests));
        using var service = new GoAiAssistantService(connection, chats, null!, null!, runs, null!, null!, null!, null!, null!,
            null!, null!, null!, settings, null!, NullLogger<GoAiAssistantService>.Instance);
        var updates = new List<GoAiAssistantUpdate>();

        await service.ResumePendingAsync(update => { updates.Add(update); return Task.CompletedTask; });

        var failed = (await runs.GetAsync(legacy.Id))!;
        Assert.Equal("failed", failed.State);
        Assert.Equal("run.workspace_missing", failed.ErrorCode);
        Assert.Empty(await runs.ListResumableAsync());
        Assert.Equal("/v1/runs/server-legacy-workspace/cancel", Assert.Single(requests));
        Assert.Equal(GoAiAssistantUpdateKind.Failed, Assert.Single(updates).Kind);
        var persistedMessage = (await chats.GetMessageAsync(turn.AssistantMessage.Id))!;
        Assert.Equal(MessageStatus.Failed, persistedMessage.Status);
        Assert.Equal("Saved progress", persistedMessage.Content);
        Assert.Contains("ursprüngliche Projektordner", persistedMessage.Error!, StringComparison.Ordinal);
        Assert.Equal("failed", Assert.Single(persistedMessage.ToolSteps!).Status);
    }

    private static GoAiRunRecord Create(Guid sessionId, Guid messageId, string? workspace) => new(
        Guid.NewGuid(), sessionId, messageId, PromptTriggerAction.Coding, "workspace-" + Guid.NewGuid().ToString("N"),
        null, 0, "queued", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, WorkspacePath: workspace);

    private sealed class CancelOnlyHandler(List<string> requests) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            requests.Add(path);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith("/cancel", path, StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
