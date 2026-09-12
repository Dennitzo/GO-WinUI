using System.Text.Json;
using GoAi.Contracts;
using GoWinUI.App.Services;
using GoWinUI.App.Pages;
using GoWinUI.App.ViewModels;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using GoWinUI.Infrastructure;
using GoWinUI.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;

namespace GoWinUI.Tests;

public sealed class CodingIntegrationTests
{
    [Fact]
    public async Task CodingSettingsSurviveRestartIndependentlyFromGeneralModel()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Path.Combine(environment.Directory, "Projekt mit Leerzeichen");
        using (var coordinator = new SettingsCoordinator(environment.Get<ISettingsStore>()))
        {
            await coordinator.InitializeAsync();
            await coordinator.UpdateAsync(settings => settings with
            {
                SelectedModel = "vendor/general-model:q8_0",
                SelectedCodingModel = "  local/coder-model:q4_k_m  ",
                CodingWorkspacePath = $"  {workspace}  ",
            });
        }

        using var reopenedStore = new JsonSettingsStore(new GoInfrastructureOptions { DataDirectory = environment.Directory });
        using (var restarted = new SettingsCoordinator(reopenedStore))
        {
            await restarted.InitializeAsync();
            Assert.Equal("vendor/general-model:q8_0", restarted.Current.SelectedModel);
            Assert.Equal("local/coder-model:q4_k_m", restarted.Current.SelectedCodingModel);
            Assert.Equal(workspace, restarted.Current.CodingWorkspacePath);
            await restarted.UpdateAsync(settings => settings with { SelectedCodingModel = "local/another-coder:q6_k" });
        }

        var persisted = await reopenedStore.LoadAsync();
        Assert.Equal("vendor/general-model:q8_0", persisted.SelectedModel);
        Assert.Equal("local/another-coder:q6_k", persisted.SelectedCodingModel);
        Assert.Equal(workspace, persisted.CodingWorkspacePath);
    }

    [Fact]
    public async Task EmptyCodingSettingsNormalizeWithoutChangingGeneralSelection()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var store = environment.Get<ISettingsStore>();
        await store.SaveAsync(new AppSettings
        {
            SelectedModel = "vendor/general-model",
            SelectedCodingModel = "  ",
            CodingWorkspacePath = "\t",
        });

        var restored = await store.LoadAsync();
        Assert.Equal("vendor/general-model", restored.SelectedModel);
        Assert.Null(restored.SelectedCodingModel);
        Assert.Null(restored.CodingWorkspacePath);
    }

    [Fact]
    public void CodingChipCreatesTheCodingTriggerWithoutRequiringAPromptPrefix()
    {
        var match = AssistantCoordinator.CreateToolMatch("coding", "  Repariere den fehlenden Import.  ");

        Assert.Equal(PromptTriggerAction.Coding, match.Trigger.Action);
        Assert.Equal("Repariere den fehlenden Import.", match.RemainingPrompt);
    }

    [Fact]
    public async Task NativeCatalogPopulatesBothTextSelectorsWithoutReplacingTheirSelections()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        const string generalId = "coding/qwen3-coder-next-Q8_0~c012";
        const string codingId = "coding/gpt-oss-120b-MXFP4~f00a";
        await settings.UpdateAsync(current => current with
        {
            SelectedModel = generalId,
            SelectedCodingModel = codingId,
        });
        ModelRuntimeStatus[] models =
        [
            new(generalId, "general", true, false, "unloaded", 32_768),
            new(generalId, "coding", true, false, "unloaded", 32_768),
            new(codingId, "general", true, false, "unloaded", 32_768),
            new(codingId, "coding", true, false, "unloaded", 32_768),
            new("vision/qwen3-vl~local", "vision", true, false, "unloaded", 32_768),
            new("embedding/bge-m3~local", "embedding", true, false, "unloaded", 8_192),
        ];
        using var connection = new GoAiConnectionService(settings, NullLogger<GoAiConnectionService>.Instance,
            () => new NativeCatalogHandler(models));
        var viewModel = new SettingsViewModel(settings, connection,
            environment.Get<IPromptTriggerRepository>(), environment.Get<IBackupService>(),
            new ShellViewModel(), new ModelCapabilityRegistry());
        viewModel.Initialize();

        var status = await viewModel.RefreshModelsAsync();

        Assert.True(status?.IsReady);
        Assert.Equal(new[] { generalId, codingId }, viewModel.Models.Select(model => model.Id));
        Assert.Equal(new[] { generalId, codingId }, viewModel.CodingModels.Select(model => model.Id));
        Assert.Equal(generalId, viewModel.SelectedGeneralModelItem?.Id);
        Assert.Equal(codingId, viewModel.SelectedCodingModelItem?.Id);
        Assert.Equal(generalId, settings.Current.SelectedModel);
        Assert.Equal(codingId, settings.Current.SelectedCodingModel);
    }

    [Fact]
    public async Task CodingChipPersistsToTheSessionAndSurvivesSnapshotRecreation()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var store = environment.Get<ISettingsStore>();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Bestehendes Projekt");
        await chats.SetPinnedAsync(session.Id, true);
        var message = await chats.AddMessageAsync(session.Id, ChatRole.User, "Vorhandener Chattext", MessageStatus.Completed);
        var workspace = Path.Combine(environment.Directory, "workspace");
        await chats.SetCodingWorkspacePathAsync(session.Id, workspace);

        using (var settings = new SettingsCoordinator(store))
        {
            await settings.InitializeAsync();
            await settings.UpdateAsync(current => current with { ActiveSessionId = session.Id, CodingWorkspacePath = Path.Combine(environment.Directory, "last-used-other-workspace") });
            var coordinator = CreateCoordinator(environment, settings);
            var snapshot = await SelectToolAsync(coordinator, "coding");
            Assert.Equal("coding", snapshot.GetProperty("selectedToolAction").GetString());
            Assert.Equal(workspace, snapshot.GetProperty("codingWorkspacePath").GetString());
            Assert.Equal(session.Id, snapshot.GetProperty("activeSessionId").GetGuid());
        }

        using var restartedSettings = new SettingsCoordinator(store);
        await restartedSettings.InitializeAsync();
        var restartedCoordinator = CreateCoordinator(environment, restartedSettings);
        var restoredSnapshot = JsonSerializer.SerializeToElement(await restartedCoordinator.BuildSnapshotAsync(), JsonSerializerOptions.Web);
        Assert.Equal("coding", restoredSnapshot.GetProperty("selectedToolAction").GetString());
        Assert.Equal(PersistentToolAction.Coding, (await chats.GetSessionAsync(session.Id))?.PersistentToolAction);
        Assert.Equal(message.Content, Assert.Single(await chats.ListMessagesAsync(session.Id)).Content);
        Assert.True((await chats.GetSessionAsync(session.Id))?.IsPinned);

        var clearedSnapshot = await SelectToolAsync(restartedCoordinator, null);
        Assert.Equal(JsonValueKind.Null, clearedSnapshot.GetProperty("selectedToolAction").ValueKind);
        Assert.Null((await chats.GetSessionAsync(session.Id))?.PersistentToolAction);
        Assert.Equal(message.Id, Assert.Single(await chats.ListMessagesAsync(session.Id)).Id);
    }

    [Fact]
    public async Task CodingSnapshotRestoresItsRuntimeContextWindowIndependentlyFromGeneralModel()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var store = environment.Get<ISettingsStore>();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Coding-Kontext");
        await chats.AddMessageAsync(session.Id, ChatRole.User, "Prüfe die Projektdateien.", MessageStatus.Completed);
        const string codingModel = "coding/Qwen3.8-27B-Q4_K_M~local";
        int contextUsed;
        using (var settings = new SettingsCoordinator(store))
        {
            await settings.InitializeAsync();
            await settings.UpdateAsync(current => current with
            {
                ActiveSessionId = session.Id,
                SelectedModel = "openai/gpt-oss-120b",
                SelectedCodingModel = codingModel,
            });
            var snapshot = await SelectToolAsync(CreateCoordinator(environment, settings), "coding");
            Assert.Equal(ModelContextProfiles.Qwen38Maximum, snapshot.GetProperty("contextLimit").GetInt32());
            Assert.Equal(codingModel, snapshot.GetProperty("model").GetString());
            contextUsed = snapshot.GetProperty("contextUsed").GetInt32();
            Assert.Equal(("Prüfe die Projektdateien.".Length + 3) / 4, contextUsed);
            Assert.Contains("Geschätzter Coding-Kontext", snapshot.GetProperty("contextNotice").GetString(), StringComparison.Ordinal);
        }

        using var restartedSettings = new SettingsCoordinator(store);
        await restartedSettings.InitializeAsync();
        var coordinator = CreateCoordinator(environment, restartedSettings);
        var reopened = JsonSerializer.SerializeToElement(await coordinator.BuildSnapshotAsync(), JsonSerializerOptions.Web);
        Assert.Equal(ModelContextProfiles.Qwen38Maximum, reopened.GetProperty("contextLimit").GetInt32());
        Assert.Equal(contextUsed, reopened.GetProperty("contextUsed").GetInt32());
        Assert.Equal(codingModel, reopened.GetProperty("model").GetString());

        var general = await SelectToolAsync(coordinator, null);
        Assert.Equal(ModelContextProfiles.GptOss120BMaximum, general.GetProperty("contextLimit").GetInt32());
        Assert.Equal(AppSettings.DefaultSelectedModel, restartedSettings.Current.SelectedModel);
    }

    [Fact]
    public async Task SessionSwitchRestoresOnlyThatSessionsCodingWorkspace()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var first = await chats.CreateSessionAsync("Projekt A");
        var second = await chats.CreateSessionAsync("Projekt B");
        var unbound = await chats.CreateSessionAsync("Noch ohne Projekt");
        var firstRoot = Path.Combine(environment.Directory, "projekt-a");
        var secondRoot = Path.Combine(environment.Directory, "projekt-b");
        await chats.SetCodingWorkspacePathAsync(first.Id, firstRoot);
        await chats.SetCodingWorkspacePathAsync(second.Id, secondRoot);
        await chats.SetPersistentToolActionAsync(first.Id, PersistentToolAction.Coding);
        await chats.SetPersistentToolActionAsync(second.Id, PersistentToolAction.Coding);
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        await settings.UpdateAsync(current => current with { CodingWorkspacePath = secondRoot });
        var coordinator = CreateCoordinator(environment, settings);

        foreach (var (sessionId, expectedRoot) in new (Guid, string?)[]
                 { (second.Id, secondRoot), (first.Id, firstRoot), (unbound.Id, null), (first.Id, firstRoot) })
        {
            var envelope = new WebBridgeEnvelope(
                AssistantWebBridge.ProtocolVersion, "session.open", Guid.NewGuid().ToString("D"),
                JsonSerializer.SerializeToElement(new { sessionId }));
            JsonElement? snapshot = null;
            await coordinator.HandleAsync(envelope, (type, payload, _) =>
            {
                if (type == "session.changed") snapshot = JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web);
                return Task.CompletedTask;
            });
            Assert.NotNull(snapshot);
            Assert.Equal(expectedRoot, snapshot.Value.GetProperty("codingWorkspacePath").GetString());
        }
    }

    [Fact]
    public async Task ToolSelectionUsesItsRequestedSessionAfterTheActiveSessionChanges()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var first = await chats.CreateSessionAsync("Noch sichtbare Sitzung A");
        var second = await chats.CreateSessionAsync("Bereits aktive Sitzung B");
        await chats.SetPersistentToolActionAsync(second.Id, PersistentToolAction.Audiobook);
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        await settings.UpdateAsync(current => current with { ActiveSessionId = second.Id });
        var coordinator = CreateCoordinator(environment, settings);

        var snapshot = await SelectToolAsync(coordinator, "coding", first.Id);

        Assert.Equal(PersistentToolAction.Coding, (await chats.GetSessionAsync(first.Id))?.PersistentToolAction);
        Assert.Equal(PersistentToolAction.Audiobook, (await chats.GetSessionAsync(second.Id))?.PersistentToolAction);
        Assert.Equal(second.Id, snapshot.GetProperty("activeSessionId").GetGuid());
        Assert.Equal("audiobook", snapshot.GetProperty("selectedToolAction").GetString());
        await SelectToolAsync(coordinator, null, first.Id);
        Assert.Null((await chats.GetSessionAsync(first.Id))?.PersistentToolAction);
        Assert.Equal(PersistentToolAction.Audiobook, (await chats.GetSessionAsync(second.Id))?.PersistentToolAction);
    }

    [Fact]
    public async Task FolderPickerResultStaysBoundToItsCapturedSession()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var first = await chats.CreateSessionAsync("Picker-Sitzung A");
        var second = await chats.CreateSessionAsync("Aktuelle Sitzung B");
        var firstRoot = Path.Combine(environment.Directory, "projekt-a");
        var secondRoot = Path.Combine(environment.Directory, "projekt-b");
        Directory.CreateDirectory(firstRoot);
        await chats.SetCodingWorkspacePathAsync(second.Id, secondRoot);
        await chats.SetPersistentToolActionAsync(second.Id, PersistentToolAction.Audiobook);
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        await settings.UpdateAsync(current => current with { ActiveSessionId = first.Id });
        var capturedSessionId = first.Id;
        var coordinator = CreateCoordinator(environment, settings);
        await settings.UpdateAsync(current => current with { ActiveSessionId = second.Id });

        await coordinator.SetCodingWorkspacePathAsync(capturedSessionId, firstRoot);

        Assert.Equal(firstRoot, (await chats.GetSessionAsync(first.Id))?.CodingWorkspacePath);
        Assert.Equal(PersistentToolAction.Coding, (await chats.GetSessionAsync(first.Id))?.PersistentToolAction);
        Assert.Equal(secondRoot, (await chats.GetSessionAsync(second.Id))?.CodingWorkspacePath);
        Assert.Equal(PersistentToolAction.Audiobook, (await chats.GetSessionAsync(second.Id))?.PersistentToolAction);
        Assert.Equal(second.Id, settings.Current.ActiveSessionId);

        await chats.DeleteSessionAsync(first.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.SetCodingWorkspacePathAsync(capturedSessionId, firstRoot));
        Assert.Equal(secondRoot, (await chats.GetSessionAsync(second.Id))?.CodingWorkspacePath);
    }

    [Fact]
    public async Task InvalidWorkspaceDoesNotChangeTheSelectedModeOrExistingFolder()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Unveränderte Sitzung");
        var original = Path.Combine(environment.Directory, "existing");
        Directory.CreateDirectory(original);
        await chats.SetCodingWorkspacePathAsync(session.Id, original);
        await chats.SetPersistentToolActionAsync(session.Id, PersistentToolAction.Audiobook);
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var coordinator = CreateCoordinator(environment, settings);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => coordinator.SetCodingWorkspacePathAsync(
            session.Id, Path.Combine(environment.Directory, "missing")));

        var restored = await chats.GetSessionAsync(session.Id);
        Assert.Equal(original, restored?.CodingWorkspacePath);
        Assert.Equal(PersistentToolAction.Audiobook, restored?.PersistentToolAction);
    }

    [Fact]
    public async Task WorkspaceAndCodingModeRollbackTogetherWhenActivationFails()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Atomarer Workspace-Wechsel");
        var original = Path.Combine(environment.Directory, "original");
        var replacement = Path.Combine(environment.Directory, "replacement");
        Directory.CreateDirectory(original);
        Directory.CreateDirectory(replacement);
        await chats.SetCodingWorkspacePathAsync(session.Id, original);
        await chats.SetPersistentToolActionAsync(session.Id, PersistentToolAction.Audiobook);
        await using (var connection = new SqliteConnection($"Data Source={environment.Get<IGoDatabase>().DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var trigger = connection.CreateCommand();
            trigger.CommandText = """
                CREATE TRIGGER reject_coding_activation
                BEFORE UPDATE OF persistent_tool_action ON chat_sessions
                WHEN NEW.persistent_tool_action='code'
                BEGIN SELECT RAISE(ABORT, 'injected activation failure'); END;
                """;
            await trigger.ExecuteNonQueryAsync();
        }
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var coordinator = CreateCoordinator(environment, settings);

        await Assert.ThrowsAsync<SqliteException>(() => coordinator.SetCodingWorkspacePathAsync(session.Id, replacement));

        var restored = await chats.GetSessionAsync(session.Id);
        Assert.Equal(original, restored?.CodingWorkspacePath);
        Assert.Equal(PersistentToolAction.Audiobook, restored?.PersistentToolAction);
    }

    [Fact]
    public async Task CancelledOrDeletedWorkspaceActivationCannotPartiallyChangeTheSession()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Abgebrochener Workspace-Wechsel");
        var original = Path.Combine(environment.Directory, "original");
        var replacement = Path.Combine(environment.Directory, "replacement");
        await chats.SetCodingWorkspacePathAsync(session.Id, original);
        await chats.SetPersistentToolActionAsync(session.Id, PersistentToolAction.Audiobook);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => chats.SetCodingWorkspacePathAsync(
            session.Id, replacement, activateCoding: true, cancellation.Token));
        var restored = await chats.GetSessionAsync(session.Id);
        Assert.Equal(original, restored?.CodingWorkspacePath);
        Assert.Equal(PersistentToolAction.Audiobook, restored?.PersistentToolAction);

        await chats.DeleteSessionAsync(session.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => chats.SetCodingWorkspacePathAsync(session.Id, replacement, activateCoding: true));
        Assert.Null(await chats.GetSessionAsync(session.Id));
    }

    [Fact]
    public async Task WorkspaceCommitHoldsTheChatGateUntilTheAtomicSaveFinishes()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commit = AssistantPage.CommitCodingWorkspaceAsync(gate, static () => false, async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(await gate.WaitAsync(0));
            Assert.False(commit.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await commit;
        }
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task WorkspaceCommitRejectsPendingOrActiveRunsAndAlwaysReleasesItsGate()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var committed = false;
        Task Commit(CancellationToken _) { committed = true; return Task.CompletedTask; }
        await gate.WaitAsync();
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => AssistantPage.CommitCodingWorkspaceAsync(gate, static () => false, Commit));
            Assert.Equal(0, gate.CurrentCount);
        }
        finally { gate.Release(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => AssistantPage.CommitCodingWorkspaceAsync(gate, static () => true, Commit));
        Assert.False(committed);
        Assert.Equal(1, gate.CurrentCount);
        await Assert.ThrowsAsync<IOException>(() => AssistantPage.CommitCodingWorkspaceAsync(gate, static () => false,
            static _ => throw new IOException("injected save failure")));
        Assert.Equal(1, gate.CurrentCount);
    }

    [Theory]
    [InlineData("coding.list", ToolRiskClass.ReadOnly)]
    [InlineData("coding.search", ToolRiskClass.ReadOnly)]
    [InlineData("coding.read", ToolRiskClass.ReadOnly)]
    [InlineData("coding.gitDiff", ToolRiskClass.ReadOnly)]
    [InlineData("coding.write", ToolRiskClass.LocalMutation)]
    [InlineData("coding.edit", ToolRiskClass.LocalMutation)]
    [InlineData("coding.command", ToolRiskClass.Process)]
    public void CodingToolRiskMustMatchTheLocalContract(string tool, ToolRiskClass expectedRisk)
    {
        var proposal = CreateProposal(tool, expectedRisk);
        LocalToolBroker.ValidateProposal(proposal);

        foreach (var claimedRisk in Enum.GetValues<ToolRiskClass>().Where(risk => risk != expectedRisk))
        {
            Assert.Throws<InvalidDataException>(() => LocalToolBroker.ValidateProposal(proposal with { RiskClass = claimedRisk }));
        }
    }

    [Fact]
    public void UnknownAndExpiredCodingProposalsAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => LocalToolBroker.ValidateProposal(CreateProposal("coding.unrestrictedShell", ToolRiskClass.Process)));
        var now = DateTimeOffset.UtcNow;
        var expired = CreateProposal("coding.command", ToolRiskClass.Process) with { ExpiresAt = now.AddSeconds(-1) };
        Assert.Throws<InvalidDataException>(() => LocalToolBroker.ValidateProposal(expired, now));
    }

    [Fact]
    public void CodingWaitingShowsElapsedTimeBeforeTheFirstTokenWithoutInventingProgress()
    {
        var counter = new GoAiAssistantService.ModelTokenProgressState();
        var waiting = GoAiAssistantService.FormatModelTokenProgress(
            new ModelGenerationEvent("codingWaiting", Attempt: 2, ElapsedSeconds: 37),
            counter);

        Assert.Equal("Runde 2 · 37 s · 0 Token", waiting);
        Assert.False(counter.HasStarted);
        Assert.Equal(0, counter.ActiveTokens);

        _ = GoAiAssistantService.FormatModelTokenProgress(
            new ModelGenerationEvent("tokenProgress", ProcessedPromptTokens: 150, GeneratedTokens: 12),
            counter);
        var continuedWaiting = GoAiAssistantService.FormatModelTokenProgress(
            new ModelGenerationEvent("codingWaiting", Attempt: 2, ElapsedSeconds: 42),
            counter);
        Assert.Equal("Runde 2 · 42 s · 150 Kontexttoken · ca. 12 erzeugte Token", continuedWaiting);
        Assert.True(counter.HasStarted);
        Assert.Equal(162, counter.ActiveTokens);
    }

    private static ToolProposal CreateProposal(string name, ToolRiskClass risk) => new(
        "coding-proposal",
        "coding-run",
        name,
        JsonSerializer.SerializeToElement(new { path = "src/example.cs" }),
        risk,
        "Coding-Werkzeug ausführen",
        DateTimeOffset.UtcNow.AddMinutes(5));

    private sealed class NativeCatalogHandler(IReadOnlyList<ModelRuntimeStatus> models) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            object payload = request.RequestUri?.AbsolutePath switch
            {
                "/v1/models/coding" => new CodingModelCatalogResponse(models.Where(model => model.Role == "coding").ToArray(),
                    "C:\\Users\\AMD\\.cache\\huggingface\\hub", true, null, now),
                "/v1/models/status" => new ModelStatusSnapshot(true, "http://host.docker.internal:8081", models, now),
                "/v1/health/live" => new HealthSnapshot("live", GoAiProtocol.Version, now),
                "/v1/health/ready" => new HealthSnapshot("modelNotLoaded", GoAiProtocol.Version, now),
                "/v1/capabilities" => new CapabilitySnapshot(GoAiProtocol.Version, "1.0", [], [], [],
                    new Dictionary<string, long>(), [], true, 8_388_608),
                _ => throw new InvalidOperationException($"Unexpected gateway request: {request.RequestUri}"),
            };
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, payload.GetType(), GoAiProtocol.CreateJsonOptions()),
                    System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private static AssistantCoordinator CreateCoordinator(TestEnvironment environment, SettingsCoordinator settings)
    {
        var recentActivity = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
        recentActivity.Restore();
        return new AssistantCoordinator(
            environment.Get<IChatRepository>(),
            environment.Get<IWorkflowRepository>(),
            environment.Get<IDocumentIngestor>(),
            environment.Get<IContextAssembler>(),
            environment.Get<IPromptTriggerRepository>(),
            environment.Get<IAssistantAttachmentRepository>(),
            environment.Get<IChatArtifactRepository>(),
            environment.Get<IConversationSnapshotRepository>(),
            null,
            settings,
            recentActivity);
    }

    private static async Task<JsonElement> SelectToolAsync(AssistantCoordinator coordinator, string? action, Guid? sessionId = null)
    {
        sessionId ??= JsonSerializer.SerializeToElement(await coordinator.BuildSnapshotAsync(), JsonSerializerOptions.Web)
            .GetProperty("activeSessionId").GetGuid();
        var requestId = Guid.NewGuid().ToString("D");
        var envelope = new WebBridgeEnvelope(
            AssistantWebBridge.ProtocolVersion,
            "session.tool",
            requestId,
            JsonSerializer.SerializeToElement(new { action, sessionId }));
        JsonElement? snapshot = null;
        await coordinator.HandleAsync(envelope, (type, payload, responseId) =>
        {
            if (type == "session.changed")
            {
                Assert.Equal(requestId, responseId);
                snapshot = JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web);
            }
            return Task.CompletedTask;
        });
        return snapshot ?? throw new InvalidOperationException("session.tool hat keinen Snapshot erzeugt.");
    }
}
