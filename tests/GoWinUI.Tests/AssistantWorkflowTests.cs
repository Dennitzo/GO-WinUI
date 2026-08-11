using GoWinUI.App.Services;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using System.Text.Json;

namespace GoWinUI.Tests;

public sealed class AssistantWorkflowTests
{
    [Fact]
    public async Task SelectingWorkflowInsertsVisibleMessageWithoutSessionAttachment()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        using var coordinator = new AssistantCoordinator(
            environment.Get<IChatRepository>(),
            environment.Get<IWorkflowRepository>(),
            environment.Get<IDocumentIngestor>(),
            environment.Get<ILmStudioClient>(),
            environment.Get<IContextAssembler>(),
            environment.Get<IChatOrchestrator>(),
            settings);
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
}
