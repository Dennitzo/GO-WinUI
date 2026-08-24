using GoAi.Contracts;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runs;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class StagedWebResearchPipelineTests
{
    private static readonly JsonSerializerOptions JsonOptions = GoAiProtocol.CreateJsonOptions();

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
            "qwen3-coder-next",
            "code",
            search,
            fetch,
            InvokeModelAsync,
            ExecuteToolAsync,
            catalog.Validate);

        Assert.Equal(4, modelRequests.Count);
        Assert.All(modelRequests, static request => Assert.Equal("qwen3-coder-next", request.ModelId));
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
            });
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
                return Task.FromResult(Result(new WebSearchResponse(
                    "official specifications",
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

        var result = await StagedWebResearchPipeline.ExecuteAsync(
            "Suche im Web nach der API.",
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

    private static RunRequest CreateRequest() => new(
        GoAiProtocol.Version,
        RunMode.Code,
        [new RunMessage("user", [new ContentPart("text", Text: "Websuche")])],
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
        Assert.True(request.DisableReasoning);
    }
}
