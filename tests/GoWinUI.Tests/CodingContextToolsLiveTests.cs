using GoAi.Client;
using GoAi.Contracts;
using GoWinUI.App.Services;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using GoWinUI.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace GoWinUI.Tests;

/// <summary>Opt-in: actual model proposals recall stored user context, search an imported document and persist generated HTML.</summary>
public sealed class CodingContextToolsLiveTests(ITestOutputHelper output)
{
    private const string DefaultModel = "coding/Qwen3.8-27B-UD-Q8_K_XL~86704cb9d896";
    private static readonly JsonSerializerOptions Json = GoAiProtocol.CreateJsonOptions();
    private static readonly JsonSerializerOptions EvidenceJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const string Prompt = "Erstelle eine kleine interaktive Gegenüberstellung unserer KontextWerkzeugprobe. "
        + "Suche zuerst in den älteren Nachrichten dieser Sitzung nach 'Archiventscheidung' und in den Sitzungsdokumenten nach 'Sitzungsfreigabe'. "
        + "Die aktuelle Frage enthält die zu vergleichenden Werte absichtlich nicht. Nutze die passenden Coding-Kontextwerkzeuge, "
        + "keine Projektdateien, Programme oder Websuche. Vergleiche die in diesen beiden Quellen genannten Varianten und ihre Anzahl "
        + "gleichzeitiger Aufträge. Erzeuge anschließend mit coding.renderHtml eine vollständige HTML-Vorschau mit einer Tabelle, "
        + "beiden wörtlichen Nachweismarkern und Quellenangaben sowie einem Button, der per JavaScript eine sichtbare Erläuterung "
        + "ein- und ausblendet. Verwende ausschließlich lokale Inline-Inhalte ohne externe Ressourcen. Lies beide Quellen wirklich, "
        + "erfinde keine Werte. Zum Schluss fasse die belegten Unterschiede und die bereitgestellte Vorschau knapp auf Deutsch zusammen.";

    [Fact]
    [Trait("Category", "Live")]
    public async Task ModelRetrievesRealSessionSourcesAndPersistsItsInteractiveHtml()
    {
        if (Environment.GetEnvironmentVariable("GO_AI_CONTEXT_LIVE") != "1") return;
        await using var history = await HistoryEnvironment.CreateAsync();
        var identifier = Guid.NewGuid().ToString("N");
        var fixture = Path.Combine(history.Directory, "TestArtifacts", "context-" + identifier);
        var workspace = Path.Combine(fixture, "workspace");
        Directory.CreateDirectory(workspace);
        var historyMarker = "HISTORY-" + identifier;
        var documentMarker = "DOCUMENT-" + identifier;
        var chats = history.Get<IChatRepository>();
        var documents = history.Get<IDocumentIngestor>();
        var session = await chats.CreateSessionAsync("Kontext- und HTML-Werkzeugtest");
        await chats.SetPersistentToolActionAsync(session.Id, PersistentToolAction.Coding);
        await chats.SetCodingWorkspacePathAsync(session.Id, workspace);
        // This fixture is a stored user-supplied note, never a fabricated assistant/model response.
        var sourceMessage = await chats.AddMessageAsync(session.Id, ChatRole.User,
            "Archiventscheidung zur KontextWerkzeugprobe: Variante Atlas erlaubt 2 gleichzeitige Aufträge. "
            + "Das ist die ursprüngliche Planung. Wörtlicher Nachweismarker: " + historyMarker + ".", MessageStatus.Completed);
        var documentText = "Sitzungsfreigabe zur KontextWerkzeugprobe: Variante Boreal erlaubt 5 gleichzeitige Aufträge. "
            + "Das ist die freigegebene Alternative. Wörtlicher Nachweismarker: " + documentMarker + ".";
        using var content = new MemoryStream(Encoding.UTF8.GetBytes(documentText), writable: false);
        var imported = await documents.ImportAsync(session.Id, "Sitzungsfreigabe.txt", content);
        Assert.True(imported.Success && imported.HasExtractableText, imported.Error);
        var sourceDocument = Assert.IsType<StoredDocument>(imported.Document);
        Assert.Contains(documentMarker, Assert.Single(await documents.ReadPagesAsync(sourceDocument.Id)).Text, StringComparison.Ordinal);
        var turn = await chats.AddTurnAsync(session.Id, Prompt);
        var assistantId = turn.AssistantMessage.Id;
        var server = Environment.GetEnvironmentVariable("GO_AI_SERVER_URL") ?? "http://127.0.0.1:8080";
        var model = Environment.GetEnvironmentVariable("GO_AI_LIVE_CODING_MODEL") ?? DefaultModel;
        using var settings = new SettingsCoordinator(new LiveConnectionSettings(server));
        await settings.InitializeAsync();
        using var connection = new GoAiConnectionService(settings, NullLogger<GoAiConnectionService>.Instance);
        // Only the three validated read-only Coding branches below are reachable. Their production
        // execution does not use UI confirmation, CAD or document creation dependencies.
        var broker = new LocalToolBroker(connection, null!, documents, null!, chats);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        using var http = new HttpClient { BaseAddress = new Uri(server.TrimEnd('/') + "/"), Timeout = Timeout.InfiniteTimeSpan };
        using var client = new GoAiClient(http, "go-context-live-" + identifier);
        var completed = new Dictionary<string, (ToolProposal Proposal, ClientToolResult Result)>(StringComparer.Ordinal);
        var answer = new StringBuilder();
        var recalledHistory = false;
        var retrievedDocument = false;
        var terminal = false;
        string? runId = null;
        RunFailedEvent? failure = null;
        try
        {
            // Source contents and markers are deliberately absent from this actual inference request.
            var accepted = await client.CreateRunAsync(new RunRequest(GoAiProtocol.Version, RunMode.Coding,
                [new("user", [new("text", Prompt)])], ClientCapabilities: ["coding"],
                Limits: new RunLimits(4_096, 32_768, 720), AllowedServerTools: [], PreferredCodingModelId: model),
                "context-live-" + identifier, timeout.Token);
            runId = accepted.RunId;
            output.WriteLine($"Context run={runId}; session={session.Id}; model={model}; document={sourceDocument.Id}");
            long cursor = 0;
            for (var attempt = 0; !terminal && attempt < 12; attempt++)
            {
                await foreach (var item in client.StreamRunEventsAsync(runId, cursor, timeout.Token))
                {
                    if (item.Type == RunEventTypes.TextDelta)
                    {
                        var delta = item.Data.Deserialize<TextDeltaEvent>(Json)?.Delta ?? string.Empty;
                        answer.Append(delta.AsSpan(0, Math.Min(delta.Length, 8_000 - answer.Length)));
                    }
                    else if (item.Type == RunEventTypes.ClientToolProposed)
                    {
                        var proposal = item.Data.Deserialize<ToolProposal>(Json) ?? throw new InvalidDataException("Missing context proposal.");
                        Assert.Equal(runId, proposal.RunId);
                        if (completed.TryGetValue(proposal.ProposalId, out var previous))
                        {
                            Assert.Equal(previous.Proposal.Name, proposal.Name);
                            Assert.True(JsonElement.DeepEquals(previous.Proposal.Arguments, proposal.Arguments));
                            await client.SubmitClientToolResultAsync(runId, previous.Result, timeout.Token);
                        }
                        else
                        {
                            Assert.True(completed.Count < 12, "Context acceptance exceeded its 12-proposal budget.");
                            var input = GoAiAssistantService.FormatToolInputDetail(proposal);
                            await chats.SaveToolStepAsync(assistantId, new(proposal.ProposalId, proposal.Name, "running", input), timeout.Token);
                            ClientToolResult result;
                            try
                            {
                                LocalToolBroker.ValidateProposal(proposal);
                                if (proposal.RiskClass != ToolRiskClass.ReadOnly || proposal.Name is not
                                    (ClientToolNames.CodingSearchHistory or ClientToolNames.CodingSearchKnowledge or ClientToolNames.CodingRenderHtml))
                                    throw new UnauthorizedAccessException("Only session history, session knowledge and HTML preview are authorized.");
                                if (proposal.Name == ClientToolNames.CodingRenderHtml && (!recalledHistory || !retrievedDocument))
                                    throw new InvalidOperationException("Retrieve both actual session sources before generating the comparison.");
                                result = await broker.ExecuteAsync(proposal, session.Id, assistantId, workspace, cancellationToken: timeout.Token);
                                if (result.Status == "completed" && proposal.Name == ClientToolNames.CodingSearchHistory)
                                    recalledHistory |= result.Result.GetProperty("matches").EnumerateArray().Any(match =>
                                        match.GetProperty("messageId").GetGuid() == sourceMessage.Id
                                        && match.GetProperty("text").GetString()!.Contains(historyMarker, StringComparison.Ordinal));
                                if (result.Status == "completed" && proposal.Name == ClientToolNames.CodingSearchKnowledge)
                                    retrievedDocument |= result.Result.GetProperty("evidence").EnumerateArray().Any(hit =>
                                        hit.GetProperty("documentId").GetGuid() == sourceDocument.Id
                                        && hit.GetProperty("pageNumber").GetInt32() == 1
                                        && hit.GetProperty("text").GetString()!.Contains(documentMarker, StringComparison.Ordinal)
                                        && hit.GetProperty("citation").GetString() == "[Sitzungsfreigabe.txt, S. 1]");
                            }
                            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
                            {
                                result = new(proposal.ProposalId, "failed", JsonSerializer.SerializeToElement(new { success = false }),
                                    "coding.context_fixture_rejected", exception.Message);
                            }
                            completed.Add(proposal.ProposalId, (proposal, result));
                            var detail = input + "\n\nErgebnis:\n" + GoAiAssistantService.FormatClientToolResultDetail(result);
                            await chats.SaveToolStepAsync(assistantId, new(proposal.ProposalId, proposal.Name,
                                GoAiAssistantService.GetToolResultStatus(result), detail,
                                result.Status == "completed" ? GoAiAssistantService.GetToolPreviewHtml(proposal) : null), timeout.Token);
                            output.WriteLine($"{proposal.Name}: {result.Status}; {result.Result.GetRawText()}");
                            await client.SubmitClientToolResultAsync(runId, result, timeout.Token);
                        }
                    }
                    else if (item.Type == RunEventTypes.RunFailed)
                    {
                        failure = item.Data.Deserialize<RunFailedEvent>(Json);
                        terminal = true;
                    }
                    else if (item.Type is RunEventTypes.RunCompleted or RunEventTypes.RunCancelled) terminal = true;
                    cursor = item.Id;
                    if (terminal) break;
                }
                if (!terminal) await Task.Delay(250, timeout.Token);
            }
            var snapshot = await client.GetRunAsync(runId, timeout.Token);
            Assert.True(snapshot.State == RunState.Completed, $"Context agent ended {snapshot.State}: {failure?.Message ?? snapshot.ErrorCode}");
            Assert.True(recalledHistory, "The model did not retrieve the actual older user message.");
            Assert.True(retrievedDocument, "The model did not retrieve the imported document with its source coordinates.");
            var previews = completed.Values.Where(value => value.Proposal.Name == ClientToolNames.CodingRenderHtml && value.Result.Status == "completed").ToArray();
            Assert.NotEmpty(previews);
            foreach (var preview in previews)
            {
                Assert.False(preview.Result.Result.GetProperty("networkAccess").GetBoolean());
                Assert.False(preview.Result.Result.TryGetProperty("code", out _));
            }
            var html = GoAiAssistantService.GetToolPreviewHtml(previews[^1].Proposal)!;
            Assert.Contains(historyMarker, html, StringComparison.Ordinal);
            Assert.Contains(documentMarker, html, StringComparison.Ordinal);
            Assert.Contains("Atlas", html, StringComparison.Ordinal);
            Assert.Contains("Boreal", html, StringComparison.Ordinal);
            Assert.Contains("2", html, StringComparison.Ordinal);
            Assert.Contains("5", html, StringComparison.Ordinal);
            Assert.Contains("<button", html, StringComparison.OrdinalIgnoreCase);
            Assert.Matches(new Regex("onclick|addEventListener", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), html);
            Assert.DoesNotMatch(new Regex("(?:src|href)\\s*=\\s*['\"](?:https?:)?//", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), html);
            Assert.False(string.IsNullOrWhiteSpace(answer.ToString()));
            Assert.Contains("Atlas", answer.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Boreal", answer.ToString(), StringComparison.OrdinalIgnoreCase);
            await chats.UpdateMessageAsync(assistantId, answer.ToString(), MessageStatus.Completed, cancellationToken: timeout.Token);
            var saved = await history.Get<IConversationSnapshotRepository>().GetAsync(session.Id, timeout.Token);
            var stored = Assert.Single(saved!.Messages, message => message.Id == assistantId);
            foreach (var preview in previews)
                Assert.Equal(GoAiAssistantService.GetToolPreviewHtml(preview.Proposal),
                    Assert.Single(stored.ToolSteps!, step => step.Id == preview.Proposal.ProposalId).PreviewHtml);
            foreach (var completion in completed.Values)
            {
                var step = Assert.Single(stored.ToolSteps!, step => step.Id == completion.Proposal.ProposalId);
                Assert.Equal(GoAiAssistantService.FormatToolInputDetail(completion.Proposal) + "\n\nErgebnis:\n"
                    + GoAiAssistantService.FormatClientToolResultDetail(completion.Result), step.Detail);
                Assert.InRange(step.Detail!.Length, 1, AssistantToolStep.MaximumDetailCharacters);
                AssertContextToolBudget(completion.Proposal, completion.Result);
            }
            Assert.InRange(stored.Content.Length, 1, 8_000);
            Assert.Empty(Directory.EnumerateFileSystemEntries(workspace));
            Assert.Equal(documentText, Assert.Single(await documents.ReadPagesAsync(sourceDocument.Id)).Text.Trim());
            await File.WriteAllTextAsync(Path.Combine(fixture, "comparison.html"), html, timeout.Token);
        }
        catch
        {
            var pending = await chats.GetMessageAsync(assistantId, CancellationToken.None);
            foreach (var step in (pending?.ToolSteps ?? []).Where(step => step.Status == "running"))
                await chats.SaveToolStepAsync(assistantId, step with { Status = timeout.IsCancellationRequested ? "cancelled" : "failed" });
            await chats.UpdateMessageAsync(assistantId, answer.ToString(), MessageStatus.Failed,
                "Der opt-in Kontextwerkzeug-Akzeptanztest wurde nicht vollständig bestanden.", CancellationToken.None);
            throw;
        }
        finally
        {
            if (runId is not null && !terminal)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await client.CancelRunAsync(runId, cleanup.Token); }
                catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException) { output.WriteLine("Cancel cleanup: " + exception.Message); }
            }
            await File.WriteAllTextAsync(Path.Combine(fixture, "evidence.json"), JsonSerializer.Serialize(new
            {
                runId, model, sessionId = session.Id, historyMarker, documentMarker, sourceMessageId = sourceMessage.Id,
                sourceDocument, recalledHistory, retrievedDocument, tools = completed.Values.Select(value => new { value.Proposal, value.Result }),
                answer = answer.ToString(),
            }, EvidenceJson));
            output.WriteLine($"Context history session={session.Id}; persistent={history.Persistent}; evidence={Path.Combine(fixture, "evidence.json")}");
        }
    }

    private static void AssertContextToolBudget(ToolProposal proposal, ClientToolResult result)
    {
        if (result.Status != "completed") return;
        var maximum = proposal.Arguments.TryGetProperty("maximumResults", out var requested) ? requested.GetInt32() : 5;
        if (proposal.Name == ClientToolNames.CodingSearchHistory)
        {
            var matches = result.Result.GetProperty("matches").EnumerateArray().ToArray();
            Assert.InRange(matches.Length, 0, maximum);
            Assert.All(matches, match => Assert.InRange(match.GetProperty("text").GetString()!.Length, 0, 654));
        }
        else if (proposal.Name == ClientToolNames.CodingSearchKnowledge)
        {
            var evidence = result.Result.GetProperty("evidence").EnumerateArray().ToArray();
            Assert.InRange(evidence.Length, 0, maximum);
            Assert.All(evidence, hit =>
            {
                Assert.InRange(hit.GetProperty("text").GetString()!.Length, 0, 804);
                Assert.InRange(hit.GetProperty("fileName").GetString()!.Length, 0, 162);
                Assert.InRange(hit.GetProperty("citation").GetString()!.Length, 0, 222);
            });
        }
        else if (proposal.Name == ClientToolNames.CodingRenderHtml)
        {
            Assert.InRange(proposal.Arguments.GetProperty("code").GetString()!.Length, 1, 16_000);
            Assert.False(result.Result.TryGetProperty("code", out _));
            Assert.False(result.Result.GetProperty("networkAccess").GetBoolean());
        }
    }

    private sealed class LiveConnectionSettings(string server) : ISettingsStore
    {
        public string SettingsPath => "in-memory context acceptance connection";
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppSettings { GoAiServerUrl = server });
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => throw new InvalidOperationException("The acceptance test must not alter profile settings.");
    }

    private sealed class HistoryEnvironment : IAsyncDisposable
    {
        private readonly TestEnvironment? _temporary;
        private readonly ServiceProvider _services;
        private HistoryEnvironment(TestEnvironment temporary)
        {
            _temporary = temporary;
            Directory = temporary.Directory;
            _services = temporary.Services;
        }
        private HistoryEnvironment(string directory, ServiceProvider services) { Directory = directory; _services = services; }
        public string Directory { get; }
        public bool Persistent => _temporary is null;
        public T Get<T>() where T : notnull => _services.GetRequiredService<T>();
        public static async Task<HistoryEnvironment> CreateAsync()
        {
            var profile = Environment.GetEnvironmentVariable("GO_AI_CONTEXT_CHAT_PROFILE");
            if (string.IsNullOrWhiteSpace(profile)) return new(await TestEnvironment.CreateAsync());
            if (!Path.IsPathFullyQualified(profile) || !System.IO.Directory.Exists(profile))
                throw new ArgumentException("GO_AI_CONTEXT_CHAT_PROFILE must be an existing absolute GO DataDirectory.");
            var root = Path.GetFullPath(profile);
            var collection = new ServiceCollection();
            collection.AddLogging();
            collection.AddGoInfrastructure(options => options.DataDirectory = root);
            var services = collection.BuildServiceProvider(validateScopes: true);
            try { await services.GetRequiredService<IGoDatabase>().InitializeAsync(); return new(root, services); }
            catch { await services.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            if (_temporary is not null) await _temporary.DisposeAsync();
            else await _services.DisposeAsync();
            // An explicitly selected real profile, its sources, HTML and TestArtifacts are preserved.
        }
    }
}
