using System.Text.Json;
using GoWinUI.App.Services;
using GoWinUI.App.ViewModels;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using GoWinUI.Infrastructure;
using GoWinUI.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace GoWinUI.Tests;

public sealed class CodingToolStepSettingsTests
{
    [Fact]
    public async Task NewAndExistingSettingsWithoutExpansionPreferenceDefaultToCollapsed()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var store = new JsonSettingsStore(new GoInfrastructureOptions { DataDirectory = environment.Directory });
        Assert.False(new AppSettings().CodingToolStepsExpanded);
        Assert.False((await store.LoadAsync()).CodingToolStepsExpanded);
        await File.WriteAllTextAsync(store.SettingsPath, JsonSerializer.Serialize(new
        {
            version = 18,
            selectedModel = "coding/existing-general~1234",
            selectedCodingModel = "coding/existing-coder~5678",
        }));

        var restored = await store.LoadAsync();

        Assert.False(restored.CodingToolStepsExpanded);
        Assert.Equal(AppSettings.CurrentVersion, restored.Version);
        Assert.Equal("coding/existing-general~1234", restored.SelectedModel);
        Assert.Equal("coding/existing-coder~5678", restored.SelectedCodingModel);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolStepExpansionSavedByViewModelSurvivesRestartAndBothWebSnapshots(bool expanded)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        await settings.UpdateAsync(current => current with
        {
            IsAiConnectionEnabled = false,
            CodingToolStepsExpanded = !expanded,
            SelectedModel = "coding/existing-general~1234",
            SelectedCodingModel = "coding/existing-coder~5678",
        });
        using var connection = new GoAiConnectionService(settings, NullLogger<GoAiConnectionService>.Instance);
        var viewModel = CreateViewModel(environment, settings, connection);
        viewModel.Initialize();
        Assert.Equal(!expanded, viewModel.CodingToolStepsExpanded);
        viewModel.CodingToolStepsExpanded = expanded;

        // This is the exact persistence operation called by the Settings Save
        // button, without unrelated window effects or remote connection probes.
        await viewModel.SavePreferencesAsync();

        using var reopenedStore = new JsonSettingsStore(new GoInfrastructureOptions { DataDirectory = environment.Directory });
        using var restarted = new SettingsCoordinator(reopenedStore);
        await restarted.InitializeAsync();
        using var reopenedConnection = new GoAiConnectionService(restarted, NullLogger<GoAiConnectionService>.Instance);
        var reopenedViewModel = CreateViewModel(environment, restarted, reopenedConnection);
        reopenedViewModel.Initialize();
        Assert.Equal(expanded, reopenedViewModel.CodingToolStepsExpanded);
        Assert.Equal(expanded, restarted.Current.CodingToolStepsExpanded);
        Assert.Equal("coding/existing-general~1234", restarted.Current.SelectedModel);
        Assert.Equal("coding/existing-coder~5678", restarted.Current.SelectedCodingModel);
        using var savedJson = JsonDocument.Parse(await File.ReadAllTextAsync(reopenedStore.SettingsPath));
        Assert.Equal(expanded, savedJson.RootElement.GetProperty("codingToolStepsExpanded").GetBoolean());

        var coordinator = CreateCoordinator(environment, restarted);
        var snapshot = JsonSerializer.SerializeToElement(await coordinator.BuildSnapshotAsync(), JsonSerializerOptions.Web);
        Assert.Equal(expanded, snapshot.GetProperty("codingToolStepsExpanded").GetBoolean());
        JsonElement? refreshed = null;
        await coordinator.HandleAsync(new WebBridgeEnvelope(
            AssistantWebBridge.ProtocolVersion, "conversation.refresh", "expansion-refresh",
            JsonSerializer.SerializeToElement(new { sessionId = snapshot.GetProperty("activeSessionId").GetGuid() })),
            (type, payload, requestId) =>
            {
                Assert.Equal("conversation.snapshot", type);
                Assert.Equal("expansion-refresh", requestId);
                refreshed = JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web);
                return Task.CompletedTask;
            });
        Assert.Equal(expanded, refreshed?.GetProperty("codingToolStepsExpanded").GetBoolean());
    }

    private static SettingsViewModel CreateViewModel(TestEnvironment environment, SettingsCoordinator settings,
        GoAiConnectionService connection) => new(settings, connection,
            environment.Get<IPromptTriggerRepository>(), environment.Get<IBackupService>(),
            new ShellViewModel(), new ModelCapabilityRegistry());

    private static AssistantCoordinator CreateCoordinator(TestEnvironment environment, SettingsCoordinator settings)
    {
        var recentActivity = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
        recentActivity.Restore();
        return new AssistantCoordinator(
            environment.Get<IChatRepository>(), environment.Get<IWorkflowRepository>(),
            environment.Get<IDocumentIngestor>(), environment.Get<IContextAssembler>(),
            environment.Get<IPromptTriggerRepository>(), environment.Get<IAssistantAttachmentRepository>(),
            environment.Get<IChatArtifactRepository>(), environment.Get<IConversationSnapshotRepository>(),
            null, settings, recentActivity);
    }
}
