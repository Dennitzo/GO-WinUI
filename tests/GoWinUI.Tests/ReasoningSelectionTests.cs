using GoWinUI.App.Services;
using GoWinUI.Core.Models;
using GoWinUI.Infrastructure;
using GoWinUI.Infrastructure.Settings;

namespace GoWinUI.Tests;

public sealed class ReasoningSelectionTests
{
    [Fact]
    public async Task ChoicesSurviveSettingsReloadAndStayScopedToModelAndRole()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var store = new JsonSettingsStore(new GoInfrastructureOptions { DataDirectory = environment.Directory });
        using (var settings = new SettingsCoordinator(store))
        {
            await settings.InitializeAsync();
            await settings.UpdateAsync(current => current with {
                ReasoningEffortsByModel = new() {
                    [GoAiAssistantService.ReasoningKey("Model-A", "coding")] = "low",
                    [GoAiAssistantService.ReasoningKey("Model-A", "general")] = "high",
                    [GoAiAssistantService.ReasoningKey("Model-B", "coding")] = "future_level"
                }
            });
        }
        using var reloadedStore = new JsonSettingsStore(new GoInfrastructureOptions { DataDirectory = environment.Directory });
        using var reloaded = new SettingsCoordinator(reloadedStore);
        await reloaded.InitializeAsync();
        Assert.Equal("low", GoAiAssistantService.StoredReasoning(reloaded.Current, "MODEL-A", "coding"));
        Assert.Equal("high", GoAiAssistantService.StoredReasoning(reloaded.Current, "Model-A", "general"));
        Assert.Equal("future_level", GoAiAssistantService.StoredReasoning(reloaded.Current, "Model-B", "coding"));
        Assert.Null(GoAiAssistantService.StoredReasoning(reloaded.Current, "Model-B", "general"));
        Assert.Null(GoAiAssistantService.StoredReasoning(new AppSettings(), "new-model", "coding"));
    }
}
