using GoWinUI.App.Services;
using GoWinUI.App.ViewModels;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace GoWinUI.Tests;

public sealed class AssistantWorkflowTests
{
    [Fact]
    public async Task BuildingSessionSnapshotDoesNotContactLocalAi()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var localAi = new UnexpectedLmStudioClient();
        using var coordinator = new AssistantCoordinator(
            environment.Get<IChatRepository>(),
            environment.Get<IWorkflowRepository>(),
            environment.Get<IDocumentIngestor>(),
            localAi,
            environment.Get<IContextAssembler>(),
            environment.Get<IChatOrchestrator>(),
            settings,
            CreateRecentActivity(settings));

        _ = await coordinator.BuildSnapshotAsync();

        Assert.Equal(0, localAi.ListModelsCallCount);
    }

    [Fact]
    public async Task SelectingWorkflowInsertsVisibleMessageWithoutSessionAttachment()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var recentActivity = CreateRecentActivity(settings);
        using var coordinator = new AssistantCoordinator(
            environment.Get<IChatRepository>(),
            environment.Get<IWorkflowRepository>(),
            environment.Get<IDocumentIngestor>(),
            environment.Get<ILmStudioClient>(),
            environment.Get<IContextAssembler>(),
            environment.Get<IChatOrchestrator>(),
            settings,
            recentActivity);
        var workflows = await environment.Get<IWorkflowRepository>().ListAsync();
        var workflow = workflows[0];
        using var payloadDocument = JsonDocument.Parse(JsonSerializer.Serialize(new { workflowId = workflow.Id }));
        var envelope = new WebBridgeEnvelope(
            AssistantWebBridge.ProtocolVersion,
            "workflow.insert",
            Guid.NewGuid().ToString("D"),
            payloadDocument.RootElement.Clone());
        var emittedTypes = new List<string>();

        await coordinator.HandleAsync(
            envelope,
            (type, _, _) =>
            {
                emittedTypes.Add(type);
                return Task.CompletedTask;
            });

        var sessionId = Assert.IsType<Guid>(settings.Current.ActiveSessionId);
        var session = await environment.Get<IChatRepository>().GetSessionAsync(sessionId);
        var messages = await environment.Get<IChatRepository>().ListMessagesAsync(sessionId);
        Assert.NotNull(session);
        Assert.Null(session.SelectedWorkflowId);
        var message = Assert.Single(messages);
        Assert.Equal(ChatRole.Assistant, message.Role);
        Assert.Equal(MessageStatus.Completed, message.Status);
        Assert.Contains($"Workflow: {workflow.Title}", message.Content, StringComparison.Ordinal);
        Assert.Contains("Nutze diesen Workflow als Kontext", message.Content, StringComparison.Ordinal);
        Assert.Contains("session.changed", emittedTypes);
    }

    [Fact]
    public async Task SessionActionsUseTheNewDefaultTitleAndUpdateRecentActivity()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var recentActivity = CreateRecentActivity(settings);
        using var coordinator = new AssistantCoordinator(
            environment.Get<IChatRepository>(),
            environment.Get<IWorkflowRepository>(),
            environment.Get<IDocumentIngestor>(),
            environment.Get<ILmStudioClient>(),
            environment.Get<IContextAssembler>(),
            environment.Get<IChatOrchestrator>(),
            settings,
            recentActivity);

        await HandleAsync(coordinator, "session.create", new { });

        var sessionId = Assert.IsType<Guid>(settings.Current.ActiveSessionId);
        var session = await environment.Get<IChatRepository>().GetSessionAsync(sessionId);
        Assert.NotNull(session);
        Assert.Equal("Neue Sitzung", session.Title);
        Assert.Equal("AI-Sitzung „Neue Sitzung“ erstellt", settings.Current.LastActivityText);

        await HandleAsync(coordinator, "session.rename", new { sessionId, title = "Planung" });
        Assert.Equal("AI-Sitzung in „Planung“ umbenannt", settings.Current.LastActivityText);

        await HandleAsync(coordinator, "session.open", new { sessionId });
        Assert.Equal("AI-Sitzung „Planung“ geöffnet", settings.Current.LastActivityText);

        await HandleAsync(coordinator, "session.delete", new { sessionId });
        Assert.Equal("AI-Sitzung „Planung“ gelöscht", settings.Current.LastActivityText);
        var replacementId = Assert.IsType<Guid>(settings.Current.ActiveSessionId);
        var replacement = await environment.Get<IChatRepository>().GetSessionAsync(replacementId);
        Assert.NotNull(replacement);
        Assert.Equal("Neue Sitzung", replacement.Title);
    }

    private static RecentActivityService CreateRecentActivity(SettingsCoordinator settings)
    {
        var service = new RecentActivityService(
            settings,
            new ShellViewModel(),
            NullLogger<RecentActivityService>.Instance);
        service.Restore();
        return service;
    }

    private static async Task HandleAsync(
        AssistantCoordinator coordinator,
        string type,
        object payload)
    {
        using var payloadDocument = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var envelope = new WebBridgeEnvelope(
            AssistantWebBridge.ProtocolVersion,
            type,
            Guid.NewGuid().ToString("D"),
            payloadDocument.RootElement.Clone());
        await coordinator.HandleAsync(envelope, static (_, _, _) => Task.CompletedTask);
    }

    private sealed class UnexpectedLmStudioClient : ILmStudioClient
    {
        public int ListModelsCallCount { get; private set; }

        public Task<IReadOnlyList<LmModel>> ListModelsAsync(CancellationToken cancellationToken = default)
        {
            ListModelsCallCount++;
            throw new InvalidOperationException("A local UI snapshot must not query LM Studio.");
        }

        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public async IAsyncEnumerable<LmDelta> StreamAsync(
            LmChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
