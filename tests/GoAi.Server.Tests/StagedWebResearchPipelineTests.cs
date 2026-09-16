using GoAi.Contracts;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runs;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class StagedWebResearchPipelineTests
{
    private static readonly JsonSerializerOptions JsonOptions = GoAiProtocol.CreateJsonOptions();

    [Fact]
    public void ImageQueriesKeepDistinctConcreteSubjects()
    {
        var queries = StagedWebResearchPipeline.ParseImageQueries(
            "[\"Pokemon 30th Celebration RGB Mew card\",\"Pokemon 30th Celebration Pikachu card\",\"Pokemon 30th Celebration RGB Mew card\"]");
        Assert.Equal(2, queries.Count);
        Assert.Contains("RGB Mew", queries[0], StringComparison.Ordinal);
        Assert.Contains("Pikachu", queries[1], StringComparison.Ordinal);
    }

    [Fact]
    public void FailedExactFetchCanRetryAWordPresentInThePagePreview()
    {
        var phrase = StagedWebResearchPipeline.SelectPreviewPhrase(
            "Welche Pokémon-Erweiterung ist heute erschienen?",
            "Pokémon TCG 30 Jahre Kartenliste",
            "Das Pokémon-Sammelkartenspiel feiert sein Jubiläum mit neuen Karten.");
        Assert.Equal("Pokémon", phrase);
    }

    [Fact]
    public void ImageResultFromEarlierSetDoesNotPassAsAnniversaryCard()
    {
        const string query = "Arktos Zapdos Lavados Pokemon TCG 30 Jahre";
        Assert.False(StagedWebResearchPipeline.IsRelevantImageResult(query,
            new WebSearchResult("Arktos Zapdos Lavados GX Tag Team", "https://example.org/older-card", null)));
        Assert.True(StagedWebResearchPipeline.IsRelevantImageResult(query,
            new WebSearchResult("Arktos Zapdos Lavados 30 Jahre", "https://example.org/30th-card", null)));
    }

    [Fact]
    public async Task VisualCurrentQuestionAddsSearxngImageUrlsToDossier()
    {
        var catalog = new AgentToolCatalog();
        var tools = catalog.GetAvailableTools(CreateRequest());
        var searchedImages = false;
        var result = await StagedWebResearchPipeline.ExecuteAsync(
            "Zeige Bilder der schönsten Karten des heute erschienenen Pokémon-Sets.",
            "qwen3-27b", "general", catalog.Resolve("web.search", tools), catalog.Resolve("web.fetch", tools),
            (request, _) => Task.FromResult(ToolResult("web.search", new { query = "Pokemon 30th Celebration cards", language = "de-DE" })),
            (call, _) =>
            {
                if (call.Arguments.TryGetProperty("profile", out var profile) && profile.GetString() == "images")
                {
                    searchedImages = true;
                    return Task.FromResult(Result(new WebSearchResponse("Pokemon 30th Celebration cards",
                        [new WebSearchResult("Pokemon TCG 30th Celebration card", "https://pokemon.com/card", null,
                            ThumbnailUrl: "https://images.pokemon.com/card.png")],
                        "searxng", false, DateTimeOffset.UtcNow)));
                }
                return Task.FromResult(Result(new WebSearchResponse("Pokemon 30th Celebration cards", [],
                    "searxng", false, DateTimeOffset.UtcNow)));
            }, catalog.Validate);
        Assert.True(searchedImages);
        Assert.Equal(2, result.ToolCalls);
        Assert.Contains("https://images.pokemon.com/card.png", result.Dossier, StringComparison.Ordinal);
        Assert.Contains("https://pokemon.com/card", result.Dossier, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchFetchAndSynthesisUseTheSameModelAndNeverShareToolSchemas()
    {
        var catalog = new AgentToolCatalog();
        var tools = catalog.GetAvailableTools(CreateRequest());
        var search = catalog.Resolve("web.search", tools);
        var fetch = catalog.Resolve("web.fetch", tools);
        var modelRequests = new List<StagedWebResearchModelRequest>();
        var executedTools = new List<string>();
        var fetchUrls = new Queue<string>(["https://example.com/official", "https://example.org/specification"]);

        var result = await StagedWebResearchPipeline.ExecuteAsync(
            "Vergleiche die offiziellen Spezifikationen.",
            "gpt-oss-120b",
            "code",
            search,
            fetch,
            InvokeModelAsync,
            ExecuteToolAsync,
            catalog.Validate);

        Assert.Equal(4, modelRequests.Count);
        Assert.All(modelRequests, static request => Assert.Equal("gpt-oss-120b", request.ModelId));
        Assert.All(modelRequests, static request => Assert.Equal("code", request.ModelRole));
        Assert.Collection(
            modelRequests,
            request => AssertSingleRequiredTool(request, "web.search"),
            request => AssertSingleRequiredTool(request, "web.fetch"),
            request => AssertSingleRequiredTool(request, "web.fetch"),
            request =>
            {
                Assert.Empty(request.Tools);
                Assert.False(request.RequireToolCall);
                Assert.Null(request.RequiredToolName);
                Assert.False(request.DisableReasoning);
                Assert.Contains("Deutsch", request.Messages[0].Content, StringComparison.Ordinal);
            });
        Assert.Contains("language exakt auf 'de-DE'", modelRequests[0].Messages[0].Content, StringComparison.Ordinal);
        Assert.Contains("nicht ins Englische", modelRequests[0].Messages[0].Content, StringComparison.Ordinal);
        Assert.Equal(["web.search", "web.fetch", "web.fetch"], executedTools);
        Assert.Equal(2, result.FetchedSourceCount);
        Assert.False(result.UsedLocalSynthesisFallback);
        Assert.Contains(StagedWebResearchPipeline.DossierMarker, result.Dossier, StringComparison.Ordinal);
        Assert.Contains("https://example.com/official", result.Dossier, StringComparison.Ordinal);
        Assert.Contains("Aufbereitete Evidenz", result.Dossier, StringComparison.Ordinal);

        Task<LmChatResult> InvokeModelAsync(
            StagedWebResearchModelRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            modelRequests.Add(request);
            if (request.RequiredToolName == "web.search")
            {
                return Task.FromResult(ToolResult(
                    "web.search",
                    new { query = "official specifications", maximumResults = 8, language = "de-DE" }));
            }
            if (request.RequiredToolName == "web.fetch")
            {
                return Task.FromResult(ToolResult("web.fetch", new { url = fetchUrls.Dequeue() }));
            }
            return Task.FromResult(new LmChatResult(
                "Beide Primärquellen bestätigen die relevante Spezifikation.",
                [],
                120,
                40));
        }

        Task<AgentToolExecutionResult> ExecuteToolAsync(
            LmToolCall call,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            executedTools.Add(call.Name);
            if (call.Name == "web.search")
            {
                Assert.Equal("de-DE", call.Arguments.GetProperty("language").GetString());
                Assert.Equal(
                    "Vergleiche die offiziellen Spezifikationen.",
                    call.Arguments.GetProperty("query").GetString());
                return Task.FromResult(Result(new WebSearchResponse(
                    call.Arguments.GetProperty("query").GetString()!,
                    [
                        new WebSearchResult("Official", "https://example.com/official", "Primary source"),
                        new WebSearchResult("Specification", "https://example.org/specification", "Normative text"),
                    ],
                    "searxng",
                    false,
                    DateTimeOffset.UtcNow)));
            }
            var url = call.Arguments.GetProperty("url").GetString()!;
            return Task.FromResult(Result(new WebFetchResponse(
                url,
                "text/html",
                $"Authoritative content from {url}",
                true,
                DateTimeOffset.UtcNow,
                [])));
        }
    }

    [Fact]
    public async Task ModelFailuresFallBackToDeterministicToolArgumentsWithoutLosingFetchedEvidence()
    {
        var catalog = new AgentToolCatalog();
        var tools = catalog.GetAvailableTools(CreateRequest());
        var search = catalog.Resolve("web.search", tools);
        var fetch = catalog.Resolve("web.fetch", tools);
        var executed = new List<LmToolCall>();

        var longTask = "Suche im Web nach der API. " + new string('x', 700);
        var result = await StagedWebResearchPipeline.ExecuteAsync(
            longTask,
            "gpt-oss-120b",
            "general",
            search,
            fetch,
            (_, _) => throw new InvalidDataException("simulated model failure"),
            ExecuteToolAsync,
            catalog.Validate);

        Assert.Equal(3, result.ModelCalls);
        Assert.Equal(2, result.ToolCalls);
        Assert.True(result.UsedLocalSynthesisFallback);
        Assert.Equal("web.search", executed[0].Name);
        Assert.Equal("web.fetch", executed[1].Name);
        Assert.InRange(executed[0].Arguments.GetProperty("query").GetString()!.Length, 1, 500);
        Assert.Contains("Deterministischer Evidenzfallback", result.Dossier, StringComparison.Ordinal);
        Assert.Contains("https://example.com/reference", result.Dossier, StringComparison.Ordinal);

        Task<AgentToolExecutionResult> ExecuteToolAsync(
            LmToolCall call,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            executed.Add(call);
            return Task.FromResult(call.Name == "web.search"
                ? Result(new WebSearchResponse(
                    "API",
                    [new WebSearchResult("Reference", "https://example.com/reference", "Official API")],
                    "searxng",
                    false,
                    DateTimeOffset.UtcNow))
                : Result(new WebFetchResponse(
                    "https://example.com/reference",
                    "text/html",
                    "Verified API reference content.",
                    true,
                    DateTimeOffset.UtcNow,
                    [])));
        }
    }

    [Fact]
    public void PersistedDossierPreventsARepeatedResearchPreparation()
    {
        IReadOnlyList<LmChatMessage> messages =
        [
            new("user", "Suche im Web."),
            new("system", StagedWebResearchPipeline.DossierMarker + "\nBereits aufbereitet."),
        ];

        Assert.True(StagedWebResearchPipeline.HasCompletedDossier(messages));
    }

    [Fact]
    public void MainAgentNeverReceivesTheStagedSearchOrFetchSchemas()
    {
        var catalog = new AgentToolCatalog();
        var tools = catalog.GetAvailableTools(CreateRequest() with
        {
            AllowedServerTools = ["web.search", "web.fetch", "math.evaluate"],
        });

        var mainTools = StagedWebResearchPipeline.RemoveFromMainAgentTools(tools);

        Assert.DoesNotContain(mainTools, static tool => tool.Name == "web.search");
        Assert.DoesNotContain(mainTools, static tool => tool.Name == "web.fetch");
        Assert.Contains(mainTools, static tool => tool.Name == "math.evaluate");
    }

    [Fact]
    public void ResearchRunsOnlyWhenSearchAndFetchWereExplicitlyRequested()
    {
        var catalog = new AgentToolCatalog();
        var implicitRequest = CreateRequest() with { AllowedServerTools = null };
        var explicitRequest = CreateRequest();

        Assert.False(StagedWebResearchPipeline.IsRequested(
            implicitRequest,
            catalog.GetAvailableTools(implicitRequest)));
        Assert.True(StagedWebResearchPipeline.IsRequested(
            explicitRequest,
            catalog.GetAvailableTools(explicitRequest)));
    }

    [Theory]
    [InlineData("Suche nach aktuellen Informationen zur klassischen Mechanik.", "de-DE")]
    [InlineData("Search the web for current information about classical mechanics.", "en-US")]
    [InlineData("Qwen3 tool calling", "de-DE")]
    [InlineData("Suche diese API ausdrücklich auf Englisch.", "en-US")]
    public void SearchLanguageFollowsTheCurrentPromptAndDefaultsToGerman(string task, string expected)
    {
        Assert.Equal(expected, StagedWebResearchPipeline.ResolvePreferredSearchLanguage(task));
    }

    [Fact]
    public void ClearlyEnglishModelQueryIsReplacedForAGermanTask()
    {
        var diagnostics = new List<string>();
        var generated = ToolResult(
            "web.search",
            new { query = "classical mechanics equations and topics", maximumResults = 20, language = "en-US" })
            .ToolCalls[0];

        var normalized = StagedWebResearchPipeline.NormalizeSearchCall(
            generated,
            "Suche Themen und Gleichungen der klassischen Mechanik.",
            "de-DE",
            diagnostics);

        Assert.Equal("Suche Themen und Gleichungen der klassischen Mechanik.", normalized.Arguments.GetProperty("query").GetString());
        Assert.Equal("de-DE", normalized.Arguments.GetProperty("language").GetString());
        Assert.Equal(20, normalized.Arguments.GetProperty("maximumResults").GetInt32());
        Assert.Single(diagnostics);
    }

    [Fact]
    public async Task LargeFetchedEvidenceIsLosslesslySplitAndHierarchicallyCompacted()
    {
        var catalog = new AgentToolCatalog();
        var tools = catalog.GetAvailableTools(CreateRequest());
        var search = catalog.Resolve("web.search", tools);
        var fetch = catalog.Resolve("web.fetch", tools);
        var modelRequests = new List<StagedWebResearchModelRequest>();
        var sourceContent = string.Join(
            "\n\n",
            Enumerable.Range(1, 12).Select(index =>
                $"Abschnitt {index}: " + new string((char)('a' + index % 20), 6_000)))
            + "\n\nTAIL-EVIDENCE-MUST-SURVIVE";

        var result = await StagedWebResearchPipeline.ExecuteAsync(
            "Prüfe die vollständige Quelle.",
            "gpt-oss-120b",
            "code",
            search,
            fetch,
            InvokeModelAsync,
            ExecuteToolAsync,
            catalog.Validate,
            contextLength: 16_384);

        Assert.True(modelRequests.Count > 3);
        var compactionRequests = modelRequests
            .Where(request => request.RequiredToolName is null
                && request.Messages[1].Content?.Contains("Hierarchische Verdichtung:", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.NotEmpty(compactionRequests);
        Assert.Contains(
            compactionRequests,
            request => request.Messages[1].Content!.Contains("TAIL-EVIDENCE-MUST-SURVIVE", StringComparison.Ordinal));
        Assert.DoesNotContain("[gekuerzt]", string.Join("\n", compactionRequests.SelectMany(static request => request.Messages).Select(static message => message.Content)), StringComparison.Ordinal);
        Assert.Contains("Vollständige Evidenz wurde hierarchisch verdichtet.", result.Dossier, StringComparison.Ordinal);

        Task<LmChatResult> InvokeModelAsync(
            StagedWebResearchModelRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            modelRequests.Add(request);
            if (request.RequiredToolName == "web.search")
            {
                return Task.FromResult(ToolResult(
                    "web.search",
                    new { query = "vollständige Quelle", maximumResults = 20, language = "de-DE" }));
            }
            if (request.RequiredToolName == "web.fetch")
            {
                return Task.FromResult(ToolResult("web.fetch", new { url = "https://example.com/large" }));
            }
            if (request.Messages[1].Content?.Contains("Hierarchische Verdichtung:", StringComparison.Ordinal) == true)
            {
                var suffix = request.Messages[1].Content!.Contains("TAIL-EVIDENCE-MUST-SURVIVE", StringComparison.Ordinal)
                    ? " TAIL-EVIDENCE-MUST-SURVIVE"
                    : string.Empty;
                return Task.FromResult(new LmChatResult(
                    "Quelle: Large | https://example.com/large | Relevante Fakten aus diesem Block." + suffix,
                    [],
                    500,
                    50));
            }
            return Task.FromResult(new LmChatResult(
                "Vollständige Evidenz wurde hierarchisch verdichtet.",
                [],
                500,
                50));
        }

        Task<AgentToolExecutionResult> ExecuteToolAsync(
            LmToolCall call,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(call.Name == "web.search"
                ? Result(new WebSearchResponse(
                    "vollständige Quelle",
                    [new WebSearchResult("Large", "https://example.com/large", "Vollständiger Beleg")],
                    "searxng",
                    false,
                    DateTimeOffset.UtcNow))
                : Result(new WebFetchResponse(
                    "https://example.com/large",
                    "text/html",
                    sourceContent,
                    true,
                    DateTimeOffset.UtcNow,
                    [])));
        }
    }

    [Fact]
    public void EvidenceSplittingNeverDropsCharacters()
    {
        var source = string.Join("\n\n", Enumerable.Range(0, 200).Select(index => $"Absatz {index}: {new string('x', 73)}"));

        var blocks = StagedWebResearchPipeline.SplitLosslessly(source, 512);

        Assert.True(blocks.Count > 1);
        Assert.Equal(source, string.Concat(blocks));
    }

    private static RunRequest CreateRequest() => new(
        GoAiProtocol.Version,
        RunMode.General,
        [new RunMessage("user", [new ContentPart("text", Text: "[GO_WEB_RESEARCH_REQUEST]\nWebsuche")])],
        AllowedServerTools: ["web.search", "web.fetch"]);

    private static LmChatResult ToolResult(string name, object arguments) => new(
        null,
        [new LmToolCall($"call-{name}", name, JsonSerializer.SerializeToElement(arguments, JsonOptions))],
        100,
        20);

    private static AgentToolExecutionResult Result(object value) => new(
        JsonSerializer.SerializeToElement(value, JsonOptions),
        []);

    private static void AssertSingleRequiredTool(
        StagedWebResearchModelRequest request,
        string expectedName)
    {
        var tool = Assert.Single(request.Tools);
        Assert.Equal(expectedName, tool.Name);
        Assert.True(request.RequireToolCall);
        Assert.Equal(expectedName, request.RequiredToolName);
        Assert.False(request.DisableReasoning);
        Assert.Null(request.MaximumOutputTokens);
    }
}
