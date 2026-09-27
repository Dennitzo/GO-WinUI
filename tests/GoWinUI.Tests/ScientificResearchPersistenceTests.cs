using GoWinUI.Core.Contracts;
using GoWinUI.Core.Research;
using GoWinUI.Core.Models;
using GoWinUI.App.Services;
using GoWinUI.App.ViewModels;
using GoWinUI.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace GoWinUI.Tests;

public sealed class ScientificResearchPersistenceTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    [Fact]
    public async Task MigrationCreatesCompleteResearchSchemaAndPersistsGraphAndCheckpoint()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var session = await environment.Get<IChatRepository>().CreateSessionAsync("Forschungsprojekt");
        var repository = environment.Get<IScientificResearchRepository>();
        var now = DateTimeOffset.UtcNow;
        var project = new ScientificResearchProject(
            "research-fixture", session.Id, "mathematicalInvestigation", "Beweise die Behauptung.",
            "Prüfe die Behauptung einschließlich Definitionsbereich und Gegenbeispielen.",
            "codingWorkspaceResearch", "formalWherePossible", "active", 1, 1, now, now,
            Path.Combine(environment.Directory, "workspace"));

        await repository.UpsertProjectAsync(project);
        var question = new ResearchPlanNode("node-question", project.Id, "question", "Ausgangsfrage", "active", 100, 0.5,
            "{}", "{}", 0, now, now);
        var verification = new ResearchPlanNode("node-verification", project.Id, "verification", "Unabhängige Prüfung", "planned", 90, 0,
            "{}", "{\"minimumPaths\":2}", 0, now, now);
        var edge = new ResearchPlanEdge("edge-1", project.Id, question.Id, verification.Id, "requiresVerification", now);
        await repository.SaveGraphAsync(project.Id, [question, verification], [edge]);
        await repository.SaveResultSnapshotAsync(project.Id, new(
            [new("hypothesis-1", project.Id, "Kandidat", "newCandidateSolution", "provisionallySupported", .35, "{}", now, question.Id)],
            [new("experiment-1", project.Id, "requirements.lock", "[]", "[42]", "{}", "python experiment.py", "{}", "ok", "", "[]", "verified", now, now, "hypothesis-1")],
            [new("verification-1", project.Id, "hypothesis", "hypothesis-1", "mathematics", "Rücksubstitution", "verified", "{}", now)],
            [new("claim-1", project.Id, "Belegte Aussage", "calculated", "verified", .95, "{}", now)]));
        await repository.SaveCheckpointAsync(new("checkpoint-1", project.Id, "run-1", 2, "problemUnderstanding", "{\"valid\":true}", now));
        await repository.SaveExperimentAsync(new("experiment-2", project.Id, "toolchain.lock", "[]", "[]", "{}",
            "python verify.py", "{}", "verified", "", "[]", "KernelAccepted", now, now));
        await repository.SaveArchiveSnapshotAsync(project.Id, new(1, "{\"criteria\":[\"primary sources\"]}",
            [new("work-1", project.Id, "Originalarbeit", "https://example.org/paper", "published", "{}", "included", "fullTextExcerpt", now)],
            [new("evidence-1", project.Id, "work-1", "Original text", "Belegte Aussage", "abc", "verifiedExcerpt", "{}", now)],
            "report-1", "scientificMarkdown", "verified", "# Bericht", "{}", "research.result.persisted", "{}", "run-1", 2, now));

        var loaded = await repository.GetProjectAsync(project.Id);
        var graph = await repository.LoadGraphAsync(project.Id);
        var checkpoint = await repository.GetLatestCheckpointAsync(project.Id);
        var results = await repository.LoadResultSnapshotAsync(project.Id);
        var archive = await repository.LoadArchiveSnapshotAsync(project.Id);
        Assert.NotNull(loaded);
        Assert.Equal("checkpoint-1", loaded.LatestCheckpointId);
        Assert.Equal(2, graph.Nodes.Count);
        Assert.Single(graph.Edges);
        Assert.Equal("problemUnderstanding", checkpoint?.Stage);
        Assert.Equal("Kandidat", Assert.Single(results.Hypotheses).Statement);
        Assert.Equal(2, results.Experiments.Count);
        Assert.Contains(results.Experiments, item => item.CommandText == "python experiment.py");
        Assert.Contains(results.Experiments, item => item.CommandText == "python verify.py");
        Assert.Equal("Rücksubstitution", Assert.Single(results.Verifications).Method);
        Assert.Equal("Belegte Aussage", Assert.Single(results.Claims).Statement);
        Assert.Equal("Originalarbeit", Assert.Single(archive.Works).Title);
        Assert.Equal("Original text", Assert.Single(archive.Evidence).ExactExcerpt);
        Assert.Equal("# Bericht", archive.Report?.ContentMarkdown);
        Assert.True(await environment.Get<IGoDatabase>().CheckIntegrityAsync());

        await using var connection = new SqliteConnection($"Data Source={environment.Get<IGoDatabase>().DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name LIKE 'research_%';";
        Assert.Equal(20L, (long)(await command.ExecuteScalarAsync())!);
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version=49;";
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
        Assert.Equal(49, SqliteDatabase.CurrentSchemaVersion);
    }

    [Fact]
    public async Task GraphWriteRejectsCrossProjectNodesAndUnknownStatus()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var repository = environment.Get<IScientificResearchRepository>();
        var now = DateTimeOffset.UtcNow;
        var wrongProject = new ResearchPlanNode("node", "other", "question", "Frage", "invented", 1, 0,
            "{}", "{}", 0, now, now);

        await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveGraphAsync("expected", [wrongProject], []));
    }

    [Fact]
    public async Task ExportBundleCreatesEveryScientificFormatInsideBoundWorkspace()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Path.Combine(environment.Directory, "workspace"); Directory.CreateDirectory(workspace);
        var session = await environment.Get<IChatRepository>().CreateSessionAsync("Export", ChatMode.Coding);
        var repository = environment.Get<IScientificResearchRepository>(); var now = DateTimeOffset.UtcNow;
        var project = new ScientificResearchProject("research-export", session.Id, "scientificEvidence", "Frage",
            "Interpretierte Frage", "codingWorkspaceResearch", "multiPath", "verified", 1, 2, now, now, workspace);
        await repository.UpsertProjectAsync(project);
        await repository.SaveResultSnapshotAsync(project.Id, new([], [], [],
            [new("claim-export", project.Id, "Ergebnis", "sourceReported", "verified", .9, "{}", now)]));
        await repository.SaveArchiveSnapshotAsync(project.Id, new(1, "{}",
            [new("work-export", project.Id, "Paper", "https://example.org/paper", "published", "{}", "included", "verifiedExcerpt", now)],
            [new("evidence-export", project.Id, "work-export", "Original", "Ergebnis", "hash", "verifiedExcerpt", "{}", now)],
            "report-export", "scientificMarkdown", "verified", "# Bericht\n\nErgebnis", "{}", "research.result.persisted", "{}", "run", 2, now));

        var bundle = await environment.Get<IScientificResearchExportService>().ExportAllAsync(project.Id, workspace);

        Assert.StartsWith(Path.Combine(workspace, ".assistant", "research", project.Id, "exports"), bundle.Directory, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(12, bundle.Files.Count);
        foreach (var file in bundle.Files) Assert.True(File.Exists(Path.Combine(bundle.Directory, file)), file);
        Assert.StartsWith("%PDF-1.4", await File.ReadAllTextAsync(Path.Combine(bundle.Directory, "report.pdf")));
        Assert.Contains("Paper", await File.ReadAllTextAsync(Path.Combine(bundle.Directory, "bibliography.bib")));
        Assert.Contains("sha256", await File.ReadAllTextAsync(bundle.ManifestPath), StringComparison.OrdinalIgnoreCase);
        var wrongWorkspace = Path.Combine(environment.Directory, "other"); Directory.CreateDirectory(wrongWorkspace);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            environment.Get<IScientificResearchExportService>().ExportAllAsync(project.Id, wrongWorkspace));
    }

    [Fact]
    public async Task ResearchBridgeListsOnlySessionProjectsAndLoadsTheirPersistedGraph()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Forschung", ChatMode.Coding);
        var other = await chats.CreateSessionAsync("Andere Forschung", ChatMode.Coding);
        var repository = environment.Get<IScientificResearchRepository>();
        var now = DateTimeOffset.UtcNow;
        var project = new ScientificResearchProject("research-visible", session.Id, "openProblem", "Originalfrage",
            "Interpretierte Frage", "codingWorkspaceResearch", "multiPath", "active", 1, 2, now, now);
        var foreign = project with { Id = "research-foreign", SessionId = other.Id };
        await repository.UpsertProjectAsync(project);
        await repository.UpsertProjectAsync(foreign);
        await repository.SaveGraphAsync(project.Id,
            [new("question-1", project.Id, "question", "Ausgangsfrage", "active", 100, .75, "{}", "{}", 1, now, now)], []);
        await repository.SaveResultSnapshotAsync(project.Id, new(
            [new("hypothesis-visible", project.Id, "Kandidat", "newCandidateSolution", "provisionallySupported", .35, "{}", now)],
            [], [new("verification-visible", project.Id, "project", project.Id, "literature", "Zweite Quelle", "planned", "{}", now)],
            [new("claim-visible", project.Id, "Fundstelle belegt", "sourceReported", "stronglySupported", .8, "{}", now)]));
        await repository.SaveCheckpointAsync(new("checkpoint-visible", project.Id, "run-visible", 2, "verification", "{\"checked\":true}", now));
        await environment.Get<ISettingsStore>().SaveAsync(new AppSettings { ActiveSessionId = session.Id });
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var recent = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
        var coordinator = new AssistantCoordinator(chats, environment.Get<IWorkflowRepository>(), environment.Get<IDocumentIngestor>(),
            environment.Get<IContextAssembler>(), environment.Get<IPromptTriggerRepository>(),
            environment.Get<IAssistantAttachmentRepository>(), environment.Get<IChatArtifactRepository>(),
            environment.Get<IConversationSnapshotRepository>(), null, settings, recent, scientificResearch: repository);
        var events = new List<(string Type, JsonElement Payload)>();
        Task Emit(string type, object payload, string? _) { events.Add((type, JsonSerializer.SerializeToElement(payload, WebJson))); return Task.CompletedTask; }

        await coordinator.HandleAsync(new(AssistantWebBridge.ProtocolVersion, "research.list", "request-list",
            JsonSerializer.SerializeToElement(new { sessionId = session.Id })), Emit);
        var listed = Assert.Single(events).Payload;
        Assert.Equal("research-visible", Assert.Single(listed.GetProperty("projects").EnumerateArray()).GetProperty("id").GetString());

        events.Clear();
        await coordinator.HandleAsync(new(AssistantWebBridge.ProtocolVersion, "research.open", "request-open",
            JsonSerializer.SerializeToElement(new { sessionId = session.Id, projectId = project.Id })), Emit);
        var detail = Assert.Single(events).Payload.GetProperty("detail");
        Assert.Equal("question-1", Assert.Single(detail.GetProperty("nodes").EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal("verification", detail.GetProperty("checkpoint").GetProperty("stage").GetString());
        Assert.Equal("Kandidat", Assert.Single(detail.GetProperty("hypotheses").EnumerateArray()).GetProperty("statement").GetString());
        Assert.Equal("Zweite Quelle", Assert.Single(detail.GetProperty("verifications").EnumerateArray()).GetProperty("method").GetString());
        Assert.Equal("Fundstelle belegt", Assert.Single(detail.GetProperty("claims").EnumerateArray()).GetProperty("statement").GetString());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => coordinator.HandleAsync(
            new(AssistantWebBridge.ProtocolVersion, "research.open", "request-foreign",
                JsonSerializer.SerializeToElement(new { sessionId = session.Id, projectId = foreign.Id })), Emit));
    }
}
