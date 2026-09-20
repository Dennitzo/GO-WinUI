using System.Globalization;
using System.Text;
using System.Text.Json;
using GoAi.Client;
using GoAi.Contracts;
using GoWinUI.App.Services;
using GoWinUI.Core.Coding;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using GoWinUI.Infrastructure;
using GoWinUI.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace GoWinUI.Tests;

/// <summary>Opt-in real Coding/Blender/research regression. No model replies or tool calls are scripted.</summary>
public sealed class BlenderCodingResearchLiveTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const string Prompt = "Dies ist nur die Vorbereitung eines Blender-Projekts, noch kein Modellbau. "
        + "Initialisiere zuerst Blender mit blender.execute operation=info. Recherchiere danach echte Pikachu-Bildreferenzen "
        + "mit web.search profile=images. Prüfe mindestens eine zugehörige öffentliche Quellseite mit web.fetch und "
        + "einer passenden Suchphrase. Verwende die angebotenen Werkzeuge tatsächlich. Wähle Suchanfrage, Treffer und "
        + "Quellen selbst. Antworte nach der Quellenprüfung kurz auf Deutsch mit den gefundenen Referenzen, Quellenlinks "
        + "und den belegten charakteristischen Merkmalen für einen späteren Entwurf. Unterscheide Quellentext von einer "
        + "tatsächlichen Bildanalyse. Erstelle in diesem Auftrag keine Skripte, keine Szene und kein 3D-Modell; keine Paketinstallation.";

    [Fact]
    [Trait("Category", "Live")]
    public async Task CodingBlenderCanInitializeThenSearchImagesAndVerifySources()
    {
        if (Environment.GetEnvironmentVariable("GO_BLENDER_CODING_RESEARCH_LIVE") != "1") return;
        var settingsPath = ResolveSettingsPath();
        Assert.True(File.Exists(settingsPath), "Real settings.json is required: " + settingsPath);
        using var realStore = new JsonSettingsStore(new GoInfrastructureOptions
        {
            DataDirectory = Path.GetDirectoryName(settingsPath)!, SettingsFileName = Path.GetFileName(settingsPath),
        });
        // LoadAsync normalizes in memory but never saves or changes the user profile.
        var realSettings = await realStore.LoadAsync();
        var model = realSettings.SelectedCodingModel?.Trim();
        Assert.False(string.IsNullOrWhiteSpace(model), "Select a real Coding model in GO settings before this opt-in test.");
        var server = realSettings.GoAiServerUrl;
        var evidence = Path.GetFullPath(Path.Combine(Environment.GetEnvironmentVariable("GO_BLENDER_CODING_RESEARCH_EVIDENCE")
            ?? Path.Combine(Path.GetTempPath(), "go-blender-coding-research"),
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8]));
        var workspace = Path.Combine(evidence, "workspace");
        Directory.CreateDirectory(workspace);
        output.WriteLine("Blender Coding research evidence: " + evidence);

        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Isolierte Blender/Coding-Recherche: Pikachu");
        await chats.SetCodingWorkspacePathAsync(session.Id, workspace);
        var turn = await chats.AddTurnAsync(session.Id, Prompt);
        using var settings = new SettingsCoordinator(new ReadOnlySettings(environment.Directory, realSettings));
        await settings.InitializeAsync();
        using var connection = new GoAiConnectionService(settings, NullLogger<GoAiConnectionService>.Instance);
        using var exporter = new DocumentPdfExporter(NullLogger<DocumentPdfExporter>.Instance);
        var documentTools = new LocalDocumentToolService(environment.Get<IGeneratedDocumentRepository>(), environment.Get<IDocumentIngestor>(),
            environment.Get<IChatArtifactRepository>(), environment.Get<IBinaryObjectStore>(), chats, environment.Get<IDocumentFileCodec>(), exporter);
        var broker = new LocalToolBroker(connection, null!, environment.Get<IDocumentIngestor>(), documentTools, chats);
        using var http = new HttpClient { BaseAddress = new Uri(server.TrimEnd('/') + "/"), Timeout = Timeout.InfiniteTimeSpan };
        using var client = new GoAiClient(http, "blender-coding-research-" + session.Id.ToString("N"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(45));
        var token = deadline.Token;
        var events = new List<RunEvent>();
        var completed = new Dictionary<string, ToolEvidence>(StringComparer.Ordinal);
        var answer = new StringBuilder();
        RunRequest? request = null;
        RunSnapshot? snapshot = null;
        string? runId = null;
        string? failure = null;
        string? cancellationError = null;
        var terminal = false;
        var passed = false;
        try
        {
            var catalog = await client.GetCodingModelsAsync(token);
            Assert.True(catalog.RuntimeReachable, catalog.Message);
            var selected = Assert.Single(catalog.Models, item => item.Downloaded && string.Equals(item.Id, model, StringComparison.OrdinalIgnoreCase));
            var status = await client.GetModelStatusAsync(token);
            Assert.True(status.ProviderReachable, status.ErrorMessage);
            var reasoning = GoAiAssistantService.ResolveComposerReasoning(realSettings, selected.Id, "coding", status);
            var capabilities = await client.GetCapabilitiesAsync(token);
            var coding = GoAiAssistantService.NegotiateCodingOptions(capabilities);
            request = new(GoAiProtocol.Version, RunMode.Coding, [new("user", [new("text", Prompt)])],
                ClientCapabilities: coding.Capabilities.Concat(GoAiAssistantService.WorkspaceClientCapabilities).Distinct().ToArray(),
                Limits: GoAiAssistantService.CreateChatRunLimits(selected.ContextTokens, unlimitedDuration: true),
                SessionId: session.Id.ToString("D"), AllowedServerTools: GoAiAssistantService.GetAllowedServerTools(PromptTriggerAction.Coding),
                PreferredCodingModelId: selected.Id, ReasoningEffort: reasoning.Selected,
                CodingOptions: (coding.Options ?? new()) with { WorkspacePath = workspace, ContinueSessionContext = false });
            await SaveAsync(Path.Combine(evidence, "preflight.json"), new
            {
                settingsPath, server, selectedModel = selected.Id, reasoning,
                sessionId = session.Id, isolatedClientDataDirectory = environment.Directory, workspace,
                readiness = await client.GetReadyHealthAsync(token),
            });
            await SaveAsync(Path.Combine(evidence, "request.json"), request);
            var accepted = await client.CreateRunAsync(request, "blender-coding-research-" + session.Id.ToString("N"), token);
            runId = accepted.RunId;
            await SaveAsync(Path.Combine(evidence, "run.json"), new { runId, mode = request.Mode.ToString(), model = selected.Id, reasoning = reasoning.Selected });
            output.WriteLine("Coding run " + runId + "; model=" + selected.Id + "; reasoning=" + reasoning.Selected);
            var runEvidence = new CodingRunEvidenceStore(evidence, session.Id, runId);
            long cursor = 0;
            while (!terminal)
            {
                await foreach (var item in client.StreamRunEventsAsync(runId, cursor, token))
                {
                    if (item.Id <= cursor) continue;
                    cursor = item.Id;
                    events.Add(item);
                    await File.AppendAllTextAsync(Path.Combine(evidence, "events.jsonl"),
                        JsonSerializer.Serialize(item, GoAiProtocol.CreateJsonOptions()) + Environment.NewLine, token);
                    if (item.Type == RunEventTypes.TextDelta)
                    {
                        var delta = item.Data.Deserialize<TextDeltaEvent>(GoAiProtocol.CreateJsonOptions())!;
                        if (string.IsNullOrEmpty(delta.AgentId))
                        {
                            if (delta.ReplaceFrom is { } from) { Assert.InRange(from, 0, answer.Length); answer.Length = from; }
                            answer.Append(delta.Delta);
                        }
                    }
                    if (item.Type == RunEventTypes.ClientToolProposed)
                    {
                        var proposal = item.Data.Deserialize<ToolProposal>(GoAiProtocol.CreateJsonOptions())!;
                        if (!completed.TryGetValue(proposal.ProposalId, out var done))
                        {
                            var result = await broker.ExecuteAsync(proposal, session.Id, turn.AssistantMessage.Id, workspace,
                                evidenceStore: runEvidence, cancellationToken: token);
                            done = new(proposal, result);
                            completed.Add(proposal.ProposalId, done);
                            await SaveAsync(Path.Combine(evidence, "tools.json"), completed.Values);
                            output.WriteLine(proposal.Name + " => " + result.Status + " " + result.Message);
                        }
                        await client.SubmitClientToolResultAsync(runId, done.Result, token);
                    }
                    if (item.Type is RunEventTypes.RunCompleted or RunEventTypes.RunFailed or RunEventTypes.RunCancelled)
                    {
                        terminal = true;
                        Assert.True(item.Type == RunEventTypes.RunCompleted, item.Data.GetRawText());
                    }
                }
            }
            snapshot = await client.GetRunAsync(runId, token);
            Assert.Equal(RunMode.Coding, snapshot.Mode);
            Assert.Equal(RunState.Completed, snapshot.State);
            Assert.Equal(selected.Id, snapshot.SelectedModel);
            Assert.Contains(events, item => item.Type == RunEventTypes.ModelSelected
                && item.Data.GetProperty("role").GetString() == "coding" && item.Data.GetProperty("modelId").GetString() == selected.Id);
            var info = completed.Values.FirstOrDefault(item => item.Proposal.Name == WorkspaceTools.Blender
                && item.Proposal.Arguments.GetProperty("operation").GetString() == "info" && item.Result.Status == "completed");
            Assert.NotNull(info);
            Assert.True(info.Result.Result.GetProperty("available").GetBoolean());
            Assert.DoesNotContain(completed.Values, item => item.Proposal.Name == WorkspaceTools.Blender
                && item.Proposal.Arguments.GetProperty("operation").GetString() != "info");
            Assert.DoesNotContain(Directory.EnumerateFiles(workspace, "*.blend", SearchOption.AllDirectories),
                path => !Path.GetRelativePath(workspace, path).StartsWith(
                    ".go-blender-preview" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            var searchCalls = events.Where(item => IsServerTool(item, RunEventTypes.ServerToolStarted, "web.search")
                && item.Data.GetProperty("arguments").TryGetProperty("profile", out var profile) && profile.GetString() == "images").ToArray();
            Assert.NotEmpty(searchCalls);
            var searchResults = events.Where(item => IsServerTool(item, RunEventTypes.ServerToolCompleted, "web.search")
                && item.Data.GetProperty("success").GetBoolean()
                && searchCalls.Any(call => call.Data.GetProperty("toolCallId").GetString() == item.Data.GetProperty("toolCallId").GetString())).ToArray();
            Assert.NotEmpty(searchResults);
            Assert.All(searchResults, item =>
            {
                Assert.Equal("searxng", item.Data.GetProperty("result").GetProperty("provider").GetString());
                Assert.False(item.Data.GetProperty("result").GetProperty("isFallback").GetBoolean());
            });
            Assert.Contains(searchResults.SelectMany(item => item.Data.GetProperty("result").GetProperty("results").EnumerateArray()),
                item => item.TryGetProperty("thumbnailUrl", out var thumbnail) && thumbnail.GetString()?.StartsWith("https://", StringComparison.Ordinal) == true);
            var verified = events.Where(item => IsServerTool(item, RunEventTypes.ServerToolCompleted, "web.fetch")
                && item.Data.GetProperty("success").GetBoolean()
                && item.Data.GetProperty("result").TryGetProperty("found", out var found) && found.ValueKind == JsonValueKind.True).ToArray();
            Assert.NotEmpty(verified);
            var imageSourceUrls = searchResults.SelectMany(item => item.Data.GetProperty("result").GetProperty("results").EnumerateArray())
                .Select(item => item.GetProperty("url").GetString()).OfType<string>().ToHashSet(StringComparer.Ordinal);
            Assert.Contains(verified, item => events.Any(start => IsServerTool(start, RunEventTypes.ServerToolStarted, "web.fetch")
                && start.Data.GetProperty("toolCallId").GetString() == item.Data.GetProperty("toolCallId").GetString()
                && imageSourceUrls.Contains(start.Data.GetProperty("arguments").GetProperty("url").GetString()!)));
            var infoEvent = Assert.Single(events, item => item.Type == RunEventTypes.ClientToolProposed
                && item.Data.GetProperty("proposalId").GetString() == info.Proposal.ProposalId);
            Assert.True(infoEvent.Id < searchCalls[0].Id, "Initialize Blender before the research phase.");
            Assert.False(string.IsNullOrWhiteSpace(answer.ToString()));
            Assert.Contains("Pikachu", answer.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains(verified, item => ContainsSourceReference(answer.ToString(), item.Data.GetProperty("result").GetProperty("url").GetString()!));
            passed = true;
        }
        catch (Exception exception)
        {
            failure = exception.ToString();
            throw;
        }
        finally
        {
            if (runId is not null && !terminal)
            {
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { await client.CancelRunAsync(runId, cancel.Token); }
                catch (Exception exception) { cancellationError = exception.Message; }
            }
            await File.WriteAllTextAsync(Path.Combine(evidence, "answer.md"), answer.ToString());
            await SaveAsync(Path.Combine(evidence, "acceptance.json"), new
            {
                passed, failure, cancellationError, runId, requestedMode = request?.Mode.ToString(), snapshot,
                model, settingsPath, reasoningEffort = request?.ReasoningEffort, sessionId = session.Id, workspace,
                answer = answer.ToString(), tools = completed.Values,
                serverTools = events.Where(item => item.Type is RunEventTypes.ServerToolStarted or RunEventTypes.ServerToolCompleted),
            });
        }
    }

    private static bool IsServerTool(RunEvent item, string type, string name) => item.Type == type
        && item.Data.TryGetProperty("tool", out var tool) && tool.GetString() == name;

    private static bool ContainsSourceReference(string answer, string url) => answer.Contains(url, StringComparison.Ordinal)
        || Uri.TryCreate(url, UriKind.Absolute, out var source)
            && answer.Contains(source.Authority + source.PathAndQuery, StringComparison.Ordinal);

    private static string ResolveSettingsPath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("GO_BLENDER_CODING_RESEARCH_SETTINGS");
        if (!string.IsNullOrWhiteSpace(explicitPath)) return Path.GetFullPath(explicitPath);
        var data = Environment.GetEnvironmentVariable("GO_DATA_DIRECTORY");
        return Path.GetFullPath(Path.Combine(string.IsNullOrWhiteSpace(data)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GO") : data, "settings.json"));
    }

    private static Task SaveAsync(string path, object value) => File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, Json));
    private sealed record ToolEvidence(ToolProposal Proposal, ClientToolResult Result);
    private sealed class ReadOnlySettings(string directory, AppSettings settings) : ISettingsStore
    {
        public string SettingsPath => Path.Combine(directory, "isolated-live-settings.json");
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(settings);
        public Task SaveAsync(AppSettings value, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("This live test never writes user settings.");
    }
}
