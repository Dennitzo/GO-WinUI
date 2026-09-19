using GoAi.Contracts;
using GoWinUI.App.Services;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using GoWinUI.Infrastructure;
using GoWinUI.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Http.Json;

namespace GoWinUI.Tests;

public sealed class ReasoningSelectionTests
{
    [Theory]
    [InlineData("auto", "high")]
    [InlineData(" AUTO ", "high")]
    [InlineData("missing", "high")]
    [InlineData("low", "low")]
    public void LegacyChoicesResolveToAnExplicitSupportedModelDefault(string saved, string expected)
    {
        var settings = new AppSettings { ReasoningEffortsByModel = new()
        {
            [GoAiAssistantService.ReasoningKey("Model-A", "coding")] = saved,
        } };
        var snapshot = Snapshot(new ModelRuntimeStatus("Model-A", "coding", true, true, "loaded", 4096,
            ReasoningEfforts: ["auto", "low", "high", "low", " AUTO ", "<invalid>"], DefaultReasoningEffort: "high"));
        var options = GoAiAssistantService.ResolveComposerReasoning(settings, "Model-A", "coding", snapshot);
        Assert.Equal(expected, options.Selected);
        Assert.Equal(["low", "high"], options.Levels);
        Assert.Equal("high", options.DefaultLevel);
        Assert.True(options.Available);
        var otherRole = GoAiAssistantService.ResolveComposerReasoning(settings, "Model-A", "general", snapshot);
        Assert.Null(otherRole.Selected);
        Assert.False(otherRole.Available);
    }

    [Fact]
    public void MissingExplicitLevelsDisableSelectionWhileUnsupportedDefaultsUseTheFirstSupportedLevel()
    {
        var model = new ModelRuntimeStatus("Model-A", "coding", true, true, "loaded", 4096,
            ReasoningEfforts: ["auto"], DefaultReasoningEffort: "auto");
        var options = GoAiAssistantService.ResolveComposerReasoning(new(), model.Id, model.Role, Snapshot(model));
        Assert.Empty(options.Levels);
        Assert.Null(options.Selected);
        Assert.Null(options.DefaultLevel);
        Assert.False(options.Available);
        var selectable = model with { ReasoningEfforts = ["auto", "medium", "high"] };
        options = GoAiAssistantService.ResolveComposerReasoning(new(), model.Id, model.Role, Snapshot(selectable));
        Assert.Equal("medium", options.Selected);
        Assert.True(options.Available);
        options = GoAiAssistantService.ResolveComposerReasoning(new(), model.Id, model.Role, Snapshot(selectable) with { ProviderReachable = false });
        Assert.Empty(options.Levels);
        Assert.Null(options.Selected);
        Assert.False(options.Available);
    }

    [Fact]
    public async Task HostSnapshotAndActualRequestResolverAgreeThroughModelChangesAndSettingsReload()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var store = environment.Get<ISettingsStore>();
        await store.SaveAsync(new AppSettings { IsAiConnectionEnabled = true, GoAiServerUrl = "http://127.0.0.1:65000",
            ReasoningEffortsByModel = new() { [GoAiAssistantService.ReasoningKey("Model-A", "coding")] = "auto" } });
        using var settings = new SettingsCoordinator(store);
        await settings.InitializeAsync();
        var model = new ModelRuntimeStatus("Model-A", "coding", true, true, "loaded", 4096,
            ReasoningEfforts: ["auto", "low", "high"], DefaultReasoningEffort: "high");
        var current = Snapshot(model,
            model with { Role = "general", ReasoningEfforts = ["on"], DefaultReasoningEffort = "on" },
            model with { Id = "Model-B", ReasoningEfforts = ["auto"], DefaultReasoningEffort = "auto" });
        using var connection = new GoAiConnectionService(settings, NullLogger<GoAiConnectionService>.Instance,
            () => new ModelStatusHandler(() => current));
        using var service = new GoAiAssistantService(connection, null!, null!, null!, null!, null!, null!, null!, null!,
            null!, null!, null!, null!, settings, null!, NullLogger<GoAiAssistantService>.Instance);
        using var client = await connection.CreateClientAsync();
        (string Model, string Role, string? Expected)[] cases =
            [("Model-A", "coding", "high"), ("Model-A", "general", "on"), ("Model-B", "coding", null)];
        foreach (var item in cases)
        {
            var displayed = await service.GetReasoningOptionsAsync(item.Model, item.Role, CancellationToken.None);
            var requested = await service.ResolveRequestedReasoningAsync(client, item.Model, item.Role, CancellationToken.None);
            Assert.Equal(item.Expected, displayed.Selected);
            Assert.Equal(displayed.Selected, requested);
            Assert.Equal(item.Expected is not null, displayed.Available);
            Assert.DoesNotContain("auto", displayed.Levels);
        }
        await settings.UpdateAsync(value => value with { ReasoningEffortsByModel = new()
        {
            [GoAiAssistantService.ReasoningKey("Model-A", "coding")] = "high",
        } });
        current = Snapshot(model with { ReasoningEfforts = ["auto", "low"], DefaultReasoningEffort = "auto" });
        var changed = await service.GetReasoningOptionsAsync(model.Id, model.Role, CancellationToken.None);
        Assert.Equal("low", changed.Selected);
        Assert.Equal(changed.Selected, await service.ResolveRequestedReasoningAsync(client, model.Id, model.Role, CancellationToken.None));
    }

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

    private static ModelStatusSnapshot Snapshot(params ModelRuntimeStatus[] models) => new(true, "fixture", models, DateTimeOffset.UtcNow);

    private sealed class ModelStatusHandler(Func<ModelStatusSnapshot> snapshot) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/v1/models/status", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(snapshot(), options: GoAiProtocol.CreateJsonOptions()),
            });
        }
    }
}
