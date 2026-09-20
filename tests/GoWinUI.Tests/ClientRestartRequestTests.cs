using GoAi.Contracts;
using GoWinUI.App.Services;
using GoWinUI.App.ViewModels;
using GoWinUI.BricsCad.Protocol;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using GoWinUI.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace GoWinUI.Tests;

public sealed class ClientRestartRequestTests
{
    private const string GeneralModel = "fixture/restart-general";
    private const string CodingModel = "fixture/restart-coding";
    private const string NextPrompt = "Setze die Prüfung mit dem bereits festgestellten Ergebnis fort.";
    private static readonly JsonSerializerOptions Json = GoAiProtocol.CreateJsonOptions();

    [Theory]
    [InlineData(false, MessageStatus.Completed)]
    [InlineData(true, MessageStatus.Completed)]
    [InlineData(false, MessageStatus.Cancelled)]
    [InlineData(true, MessageStatus.Cancelled)]
    [InlineData(false, MessageStatus.Interrupted)]
    [InlineData(true, MessageStatus.Interrupted)]
    public async Task ReopeningTheSameProfileAndSessionKeepsTheCompleteNextRequestStable(
        bool coding, MessageStatus previousStatus)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Erhaltener Kontext nach Neustart");
        var workspace = Path.Combine(environment.Directory, "workspace");
        Directory.CreateDirectory(workspace);
        await chats.SetCodingWorkspacePathAsync(session.Id, workspace, activateCoding: coding);
        await chats.SetPinnedAsync(session.Id, true);
        await chats.AddMessageAsync(session.Id, ChatRole.User, "Prüfe src/cache.cs und merke dir den Befund.", MessageStatus.Completed);
        var assistant = await chats.AddMessageAsync(session.Id, ChatRole.Assistant,
            "Die Datei enthält eine wiederverwendbare Sitzungskennung. Der nächste Schritt ist ein Fortsetzungstest.", previousStatus);
        await chats.SetMessageContextSummaryAsync(assistant.Id, "src/cache.cs: Sitzungskennung vorhanden; Fortsetzungstest offen.");
        await chats.SaveToolStepAsync(assistant.Id, new AssistantToolStep("read-cache", "coding.read", "completed",
            "Datei geprüft", InputJson: "{\"path\":\"src/cache.cs\"}",
            OutputJson: "{\"path\":\"src/cache.cs\",\"content\":\"sessionId\"}", ContentOffset: 0));
        await environment.Get<ISettingsStore>().SaveAsync(new AppSettings
        {
            IsAiConnectionEnabled = true,
            GoAiServerUrl = "http://127.0.0.1:65000",
            ActiveSessionId = session.Id,
            SelectedModel = GeneralModel,
            SelectedCodingModel = CodingModel,
        });

        var before = await CaptureNextRequestAsync(environment.Services, session.Id, assistant.Id, coding);
        // Close all client repositories/settings and reopen the actual same SQLite
        // profile. No copying or rewriting of its history is used for comparison.
        await environment.Services.DisposeAsync();
        SqliteConnection.ClearAllPools();
        var registrations = new ServiceCollection();
        registrations.AddLogging();
        registrations.AddGoInfrastructure(options => options.DataDirectory = environment.Directory);
        await using var restarted = registrations.BuildServiceProvider(validateScopes: true);
        await restarted.GetRequiredService<IGoDatabase>().InitializeAsync();
        var after = await CaptureNextRequestAsync(restarted, session.Id, assistant.Id, coding);

        Assert.NotEqual(before.ClientId, after.ClientId);
        Assert.Equal(before.RequestJson, after.RequestJson);
        Assert.Equal(before.HistoryJson, after.HistoryJson);
        Assert.Equal(session.Id.ToString("D"), after.Request.SessionId);
        Assert.False(after.Request.DeepResearch);
        Assert.Equal(coding ? RunMode.Coding : RunMode.Auto, after.Request.Mode);
        Assert.Equal(coding ? CodingModel : null, after.Request.PreferredCodingModelId);
        Assert.Equal(coding ? null : GeneralModel, after.Request.PreferredGeneralModelId);
        Assert.Contains(after.Request.Messages, static message => message.Role == "assistant"
            && message.Content.Any(static part => part.Text?.Contains("wiederverwendbare Sitzungskennung", StringComparison.Ordinal) == true));
        if (coding)
        {
            Assert.Equal(workspace, after.Request.CodingOptions?.WorkspacePath);
            Assert.True(after.Request.CodingOptions?.ContinueSessionContext);
            Assert.Contains(after.Request.Messages, static message => message.Content.Any(static part =>
                part.Text?.Contains("read-cache", StringComparison.Ordinal) == true));
        }
        else
        {
            Assert.NotNull(after.Request.SessionContext);
            Assert.Equal(2, after.Request.SessionContext.OriginalMessageCount);
            Assert.False(after.Request.SessionContext.PreparedByAi);
        }
    }

    private static async Task<Capture> CaptureNextRequestAsync(ServiceProvider provider, Guid sessionId,
        Guid assistantId, bool coding)
    {
        var chats = provider.GetRequiredService<IChatRepository>();
        var documents = provider.GetRequiredService<IDocumentIngestor>();
        using var settings = new SettingsCoordinator(provider.GetRequiredService<ISettingsStore>());
        await settings.InitializeAsync();
        await settings.UpdateAsync(static current => current);
        var clientIds = new HashSet<string>(StringComparer.Ordinal);
        using var connection = new GoAiConnectionService(settings, NullLogger<GoAiConnectionService>.Instance,
            () => new CatalogHandler(clientIds));
        await using var bricsCad = new BricsCadBridgeHost();
        var broker = new LocalToolBroker(connection, bricsCad, documents, null!, chats);
        var recent = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
        using var service = new GoAiAssistantService(connection, chats,
            provider.GetRequiredService<IAssistantAttachmentRepository>(), provider.GetRequiredService<IChatArtifactRepository>(),
            provider.GetRequiredService<IGoAiRunRepository>(), provider.GetRequiredService<IClientToolExecutionRepository>(),
            provider.GetRequiredService<IBinaryObjectStore>(), documents, new DocumentContextPreparationService(documents),
            new SessionContextPreparationService(chats), broker, null!, null!, settings, recent,
            NullLogger<GoAiAssistantService>.Instance);
        // The same history maintenance executed by App.OnLaunched must leave
        // completed/stopped turns intact, including their timestamps and receipts.
        Assert.Equal(0, await chats.MarkStreamingMessagesInterruptedAsync());
        await service.StopPersistedRunsAtStartupAsync();
        Assert.Equal(0, await chats.DeleteEmptyTerminalMessagesAsync());
        var coordinator = new AssistantCoordinator(chats, provider.GetRequiredService<IWorkflowRepository>(), documents,
            provider.GetRequiredService<IContextAssembler>(), provider.GetRequiredService<IPromptTriggerRepository>(),
            provider.GetRequiredService<IAssistantAttachmentRepository>(), provider.GetRequiredService<IChatArtifactRepository>(),
            provider.GetRequiredService<IConversationSnapshotRepository>(), service, settings, recent);
        await coordinator.HandleAsync(new WebBridgeEnvelope(AssistantWebBridge.ProtocolVersion, "session.open", "reopen",
            JsonSerializer.SerializeToElement(new { sessionId })), static (_, _, _) => Task.CompletedTask);
        Assert.Equal(sessionId, settings.Current.ActiveSessionId);
        Assert.True((await chats.GetSessionAsync(sessionId))?.IsPinned);

        var history = await chats.ListMessagesAsync(sessionId);
        var assistant = (await chats.GetMessageAsync(assistantId))!;
        using var client = await connection.CreateClientAsync();
        var trigger = coding ? AssistantCoordinator.CreateToolMatch("coding", NextPrompt) : null;
        // Build the real request without SendAsync adding another user/assistant
        // turn. This isolates restart from legitimate changes to conversation input.
        var builder = typeof(GoAiAssistantService).GetMethod("BuildRunRequestAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The request builder was not found.");
        var uploadedType = builder.GetParameters()[6].ParameterType.GenericTypeArguments.Single();
        var requestTask = (Task<RunRequest>)builder.Invoke(service,
        [
            client, sessionId, NextPrompt, trigger, Array.Empty<AssistantAttachment>(), history,
            Array.CreateInstance(uploadedType, 0), assistant,
            (Func<GoAiAssistantUpdate, Task>)(static _ => Task.CompletedTask), CancellationToken.None,
        ])!;
        var request = await requestTask;
        return new Capture(request, JsonSerializer.Serialize(request, Json), JsonSerializer.Serialize(history, Json),
            Assert.Single(clientIds));
    }

    private sealed record Capture(RunRequest Request, string RequestJson, string HistoryJson, string ClientId);

    private sealed class CatalogHandler(HashSet<string> clientIds) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            clientIds.Add(request.Headers.GetValues(GoAiHeaders.ClientId).Single());
            var now = DateTimeOffset.UtcNow;
            ModelRuntimeStatus[] models =
            [
                new(GeneralModel, "general", true, true, "loaded", 32_768),
                new(CodingModel, "coding", true, true, "loaded", 32_768),
            ];
            object value = request.RequestUri!.AbsolutePath switch
            {
                "/v1/models/status" => new ModelStatusSnapshot(true, "fixture", models, now),
                "/v1/models/coding" => new CodingModelCatalogResponse(models.Where(static model => model.Role == "coding").ToArray(),
                    "fixture", true, null, now),
                "/v1/capabilities" => new CapabilitySnapshot(GoAiProtocol.Version, "fixture", [],
                    ["coding.updatePlan"], [ClientToolNames.CodingReadOutput, ClientToolNames.CodingSearchRunEvidence],
                    new Dictionary<string, long>(), [], true, GoAiProtocol.UploadChunkSize, SupportsCodingSessionContext: true),
                _ => throw new InvalidOperationException("No inference is allowed in the restart fixture: " + request.RequestUri),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(value, Json), Encoding.UTF8, "application/json"),
            });
        }
    }
}
