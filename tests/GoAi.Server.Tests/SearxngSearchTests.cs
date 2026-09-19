using GoAi.Contracts;
using GoAi.Server.Core.Coding;
using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Research;
using GoAi.Server.Core.Runs;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class SearxngSearchTests
{
    private const string Unavailable = """{"results":[],"unresponsive_engines":[["brave","HTTP error 429"],["bing","CAPTCHA"],["google cse","unusual traffic"]]}""";
    private static readonly string[] HealthyPythonEngines = ["google cse", "bing", "stackoverflow"];

    [Fact]
    public async Task HttpSuccessWithBlockedEnginesIsAConcreteFailureWithoutHiddenRetryOrFallback()
    {
        using var handler = new SearchHandler(Unavailable);
        var exception = await Assert.ThrowsAsync<SearxngEngineUnavailableException>(() => Service(handler).SearchAsync(new("asyncio TaskGroup", Profile: "general"), false));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.Equal(3, exception.Failures.Count);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("searxng", handler.LastUri!.Host);
        var failure = AgentToolExecutor.DescribeResearchFailure("web.search", exception);
        Assert.Equal("web.search.engines_unavailable", failure.ErrorCode);
        Assert.False(failure.Retryable);
        Assert.Contains("429", failure.Message, StringComparison.Ordinal);
        Assert.Contains("CAPTCHA", failure.Message, StringComparison.Ordinal);
        Assert.Contains("google cse: unusual traffic", failure.Message, StringComparison.Ordinal);
        Assert.True(AgentToolExecutor.IsRecoverableResearchFailure("web.search", exception));
    }

    [Fact]
    public async Task PartialSearchResultsKeepEngineDiagnosticsAndVerifiedProviderIdentity()
    {
        const string response = """{"results":[{"title":"TaskGroup","url":"https://discuss.python.org/t/asyncio/1","content":"Original discussion","engine":"discuss.python"}],"unresponsive_engines":[["brave","HTTP error 429"]]}""";
        using var handler = new SearchHandler(response);
        var search = await Service(handler).SearchAsync(new("asyncio TaskGroup", Profile: "python"), false);
        Assert.Equal("searxng", search.Provider);
        Assert.False(search.IsFallback);
        Assert.Equal("discuss.python", Assert.Single(search.Results).Source);
        Assert.Equal("HTTP error 429", Assert.Single(search.EngineFailures!).Reason);
        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain("categories=", handler.LastUri!.Query, StringComparison.Ordinal);
        Assert.Contains("engines=google cse,bing,brave,stackoverflow", Uri.UnescapeDataString(handler.LastUri.Query), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("auto", "Python asyncio TaskGroup", "google cse,bing,brave,stackoverflow")]
    [InlineData("auto", "ProcessPoolExecutor multiprocessing", "google cse,bing,brave,stackoverflow")]
    [InlineData("auto", "numpy threadpoolctl", "google cse,bing,brave,stackoverflow")]
    [InlineData("auto", "ThreadPoolExecutor initializer", "google cse,bing,brave,stackoverflow")]
    [InlineData("auto", "scipy.fft set_workers", "google cse,bing,brave,stackoverflow")]
    [InlineData("auto", "cupy shared memory", "google cse,bing,brave,stackoverflow")]
    [InlineData("auto", "pytorch spawn CUDA", "google cse,bing,brave,stackoverflow")]
    [InlineData("web", "iframe sandbox allow-scripts", "google cse,bing,brave,mdn,microsoft learn")]
    [InlineData("auto", "C# Task cancellation", "google cse,bing,brave,microsoft learn,stackoverflow")]
    public async Task TechnicalProfilesSelectOnlyAllowlistedLocalSearxngEngines(string profile, string query, string engines)
    {
        using var handler = new SearchHandler("""{"results":[],"unresponsive_engines":[]}""");
        var search = await Service(handler).SearchAsync(new(query, Profile: profile), false);
        Assert.Equal("searxng", search.Provider);
        Assert.False(search.IsFallback);
        Assert.Empty(search.Results);
        Assert.Null(search.EngineFailures);
        Assert.Equal(1, handler.Calls);
        Assert.Contains("engines=" + engines, Uri.UnescapeDataString(handler.LastUri!.Query), StringComparison.Ordinal);
        Assert.DoesNotContain("categories=", handler.LastUri.Query, StringComparison.Ordinal);
        Assert.Contains("language=all", handler.LastUri.Query, StringComparison.Ordinal);
        Assert.Equal("all", search.SearchLanguage);
        Assert.Equal(engines.Split(','), search.SearchedEngines);
    }

    [Theory]
    [InlineData("notnumpy custom library")]
    [InlineData("multiprocessingXYZ architecture")]
    [InlineData("scipyish API")]
    public void AutoProfileDoesNotInferPythonFromSubstrings(string query)
    {
        Assert.Equal("general", SearxngSearchProfiles.Select(query));
        Assert.Equal("de-DE", SearxngSearchProfiles.SearchLanguage("auto", query, "de-DE"));
        Assert.Equal("google cse,bing,brave", SearxngSearchProfiles.Engines("auto", query));
    }

    [Theory]
    [InlineData(null, "de-DE", "all")]
    [InlineData("general", "fr-FR", "fr-FR")]
    [InlineData("python", "de-DE", "all")]
    [InlineData("auto", "de-DE", "all")]
    public async Task TechnicalSourcesAreNotRestrictedToTheChatLanguageAndQueryIsPreserved(string? profile, string language, string expectedLanguage)
    {
        const string query = "CUDA Python multiprocessing spawn shared memory cupy worker processes GPU";
        using var handler = new SearchHandler("""{"results":[{"title":"CUDA","url":"https://docs.nvidia.com/"}],"unresponsive_engines":[]}""");
        var search = await Service(handler).SearchAsync(new(query, Language: language, Profile: profile), false);
        Assert.Equal(query, search.Query);
        Assert.Equal(expectedLanguage, search.SearchLanguage);
        Assert.Contains("q=" + query, Uri.UnescapeDataString(handler.LastUri!.Query), StringComparison.Ordinal);
        Assert.Contains("language=" + expectedLanguage, handler.LastUri.Query, StringComparison.Ordinal);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task NextTechnicalSearchSkipsRateLimitedEngineAndRetainsConcreteDiagnostics()
    {
        using var handler = new SearchHandler(
            """{"results":[],"unresponsive_engines":[["brave","too many requests"]]}""",
            """{"results":[{"title":"Multiprocessing","url":"https://docs.python.org/3/library/multiprocessing.html","engine":"google cse"}],"unresponsive_engines":[]}""");
        var service = Service(handler);
        var initial = await service.SearchAsync(new("ProcessPoolExecutor", Profile: "python"), false);
        Assert.Empty(initial.Results);
        Assert.Equal("brave", Assert.Single(initial.EngineFailures!).Engine);
        Assert.NotNull(initial.SearchGuidance);
        var result = await service.SearchAsync(new("CUDA multiprocessing spawn", Profile: "python"), false);
        Assert.Single(result.Results);
        Assert.Equal("searxng", result.Provider);
        Assert.False(result.IsFallback);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(HealthyPythonEngines, result.SearchedEngines);
        Assert.DoesNotContain("brave", handler.LastUri!.Query, StringComparison.Ordinal);
        var failure = Assert.Single(result.EngineFailures!);
        Assert.Equal("brave", failure.Engine);
        Assert.Contains("ausgelassen", failure.Reason, StringComparison.Ordinal);
        Assert.Contains("too many requests", failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpiredEngineCooldownIsReevaluatedAndDoesNotPermanentlyRemoveSearchEngines()
    {
        using var handler = new SearchHandler("""{"results":[],"unresponsive_engines":[["brave","HTTP error 429"]]}""");
        var clock = new TestTimeProvider();
        var service = new WebResearchService(new TestClientFactory(handler), Options.Create(new GoAiServerOptions()), clock);
        await service.SearchAsync(new("ProcessPoolExecutor", Profile: "python"), false);
        clock.Now += TimeSpan.FromMinutes(3);
        await service.SearchAsync(new("ProcessPoolExecutor", Profile: "python"), false);
        Assert.Contains("brave", handler.LastUri!.Query, StringComparison.Ordinal);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task HealthyEmptyResultsWithSkippedEngineRemainRefinableInsteadOfClaimingAnOutage()
    {
        using var handler = new SearchHandler(
            """{"results":[],"unresponsive_engines":[["brave","too many requests"]]}""",
            """{"results":[],"unresponsive_engines":[]}""");
        var service = Service(handler);
        await service.SearchAsync(new("ProcessPoolExecutor", Profile: "python"), false);
        var result = await service.SearchAsync(new("CUDA multiprocessing spawn", Profile: "python"), false);
        Assert.Empty(result.Results);
        Assert.Equal(HealthyPythonEngines, result.SearchedEngines);
        Assert.Equal("brave", Assert.Single(result.EngineFailures!).Engine);
        Assert.Contains("API-Namen", result.SearchGuidance!, StringComparison.Ordinal);
        Assert.Equal("searxng", result.Provider);
        Assert.False(result.IsFallback);
        Assert.DoesNotContain("brave", handler.LastUri!.Query, StringComparison.Ordinal);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task FullyBlockedProfileDoesNotIssueAnotherNetworkRequest()
    {
        using var handler = new SearchHandler("""{"results":[],"unresponsive_engines":[["google cse","HTTP error 429"],["bing","CAPTCHA"],["brave","too many requests"],["stackoverflow","CAPTCHA"]]}""");
        var service = Service(handler);
        await Assert.ThrowsAsync<SearxngEngineUnavailableException>(() => service.SearchAsync(new("ProcessPoolExecutor", Profile: "python"), false));
        var exception = await Assert.ThrowsAsync<SearxngEngineUnavailableException>(() => service.SearchAsync(new("CUDA multiprocessing spawn", Profile: "python"), false));
        Assert.Equal(4, exception.Failures.Count);
        Assert.All(exception.Failures, failure => Assert.Contains("ausgelassen", failure.Reason, StringComparison.Ordinal));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task GenuineEmptyGeneralSearchRemainsEmptyAndUnknownProfilesNeverReachNetwork()
    {
        using var handler = new SearchHandler("""{"results":[],"unresponsive_engines":[]}""");
        var service = Service(handler);
        var search = await service.SearchAsync(new("new programming API"), false);
        Assert.Empty(search.Results);
        Assert.Contains("engines=google cse,bing,brave", Uri.UnescapeDataString(handler.LastUri!.Query), StringComparison.Ordinal);
        Assert.Equal("all", search.SearchLanguage);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(new("API", Profile: "https://other-provider.example/"), false));
        Assert.Equal(2, handler.Calls);
        var catalog = new AgentToolCatalog();
        var tool = catalog.GetAvailableTools(new(GoAiProtocol.Version, RunMode.Coding, [new("user", [new("text", "API")])], AllowedServerTools: ["web.search"]))
            .Single(static tool => tool.Name == "web.search");
        catalog.Validate(tool, JsonSerializer.SerializeToElement(new { query = "API", profile = "auto" }));
        Assert.Throws<ArgumentException>(() => catalog.Validate(tool, JsonSerializer.SerializeToElement(new { query = "API", profile = "bing" })));
    }

    [Theory]
    [InlineData("Python asyncio.TaskGroup ExceptionGroup cancellation official documentation", "asyncio TaskGroup ExceptionGroup", "asyncio TaskGroup")]
    [InlineData("Python 3.11 asyncio.wait_for cancellation official documentation", "asyncio wait_for cancellation", "asyncio wait_for")]
    public void TechnicalQueriesKeepTheApiAndRetryWithTheProvenShortForm(string query, string expected, string shorter)
    {
        var normalized = CodingDeepResearchPipeline.NormalizeProfileQuery(query, "python");
        Assert.Equal(expected, normalized);
        Assert.Equal(shorter, CodingDeepResearchPipeline.NormalizeProfileQuery(normalized, "python", simplify: true));
    }

    [Fact]
    public async Task EngineDiagnosticsAreBoundedAndControlCharactersAreRemoved()
    {
        var failures = Enumerable.Range(0, 20).Select(static index => new[] { "engine\r\n" + index, new string('x', 500) }).ToArray();
        using var handler = new SearchHandler(JsonSerializer.Serialize(new { results = Array.Empty<object>(), unresponsive_engines = failures }));
        var response = await Service(handler).SearchAsync(new("API", Language: "all"), false);
        Assert.Equal(8, response.EngineFailures!.Count);
        Assert.All(response.EngineFailures, static failure =>
        {
            Assert.True(failure.Engine.Length <= 64);
            Assert.Equal(160, failure.Reason.Length);
            Assert.DoesNotContain('\r', failure.Engine);
            Assert.DoesNotContain('\n', failure.Engine);
        });
    }

    [Fact]
    public async Task GeneralSearchUsesHealthyEnginesAndSkipsKnownBlocksOnNextQuery()
    {
        using var handler = new SearchHandler(
            """{"results":[{"title":"Blender","url":"https://docs.blender.org/","engine":"google cse"}],"unresponsive_engines":[["brave","HTTP error 429"]]}""",
            """{"results":[{"title":"Blender API","url":"https://docs.blender.org/api/","engine":"bing"}],"unresponsive_engines":[]}""");
        using var service = Service(handler);
        await service.SearchAsync(new("Blender"), false);
        var next = await service.SearchAsync(new("Blender API"), false);
        Assert.Equal(2, handler.Calls);
        Assert.Equal("searxng", next.Provider);
        Assert.False(next.IsFallback);
        Assert.Equal(["google cse", "bing"], next.SearchedEngines);
        Assert.DoesNotContain("brave", handler.LastUri!.Query, StringComparison.Ordinal);
        Assert.Contains("ausgelassen", Assert.Single(next.EngineFailures!).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyLanguageFilteredQueryRetriesOnceLocallyWithoutBlockedEngines()
    {
        using var handler = new SearchHandler(
            """{"results":[],"unresponsive_engines":[["brave","CAPTCHA"]]}""",
            """{"results":[{"title":"API","url":"https://docs.blender.org/api/","engine":"google cse"}],"unresponsive_engines":[]}""");
        using var service = Service(handler);
        var result = await service.SearchAsync(new("Blender Python API", Language: "de-DE", Profile: "general"), false);
        Assert.Single(result.Results);
        Assert.Equal(2, handler.Calls);
        Assert.Equal("all", result.SearchLanguage);
        Assert.False(result.IsFallback);
        Assert.Equal("searxng", handler.LastUri!.Host);
        Assert.DoesNotContain("brave", handler.LastUri.Query, StringComparison.Ordinal);
        Assert.Contains("ohne Sprachfilter", result.SearchGuidance, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentIdenticalSearchesShareShortLivedResultsButRefreshAfterExpiry()
    {
        using var handler = new SearchHandler("""{"results":[{"title":"API","url":"https://docs.blender.org/api/"}],"unresponsive_engines":[]}""");
        var clock = new TestTimeProvider();
        using var service = new WebResearchService(new TestClientFactory(handler), Options.Create(new GoAiServerOptions()), clock);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => service.SearchAsync(new("Blender"), false)));
        Assert.Equal(1, handler.Calls);
        Assert.All(results, result => Assert.Single(result.Results));
        clock.Now += TimeSpan.FromSeconds(46);
        await service.SearchAsync(new("Blender"), false);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task SiteFilterRejectsBroadenedResultsBeforeApplyingResultLimit()
    {
        using var handler = new SearchHandler("""
            {"results":[
              {"title":"Broad result","url":"https://unrelated.example/","engine":"bing"},
              {"title":"Lookalike","url":"https://docs.python.org.attacker.example/","engine":"bing"},
              {"title":"TaskGroup","url":"https://docs.python.org/3/library/asyncio-task.html","engine":"google cse"}],
             "unresponsive_engines":[]}
            """);
        using var service = Service(handler);
        var result = await service.SearchAsync(new("site:docs.python.org asyncio TaskGroup", MaximumResults: 1), false);
        Assert.Equal("TaskGroup", Assert.Single(result.Results).Title);
        Assert.Equal("all", result.SearchLanguage);
        Assert.False(result.IsFallback);
        Assert.Equal(1, handler.Calls);
    }

    private static WebResearchService Service(HttpMessageHandler handler) =>
        new(new TestClientFactory(handler), Options.Create(new GoAiServerOptions()));

    private sealed class TestClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class SearchHandler(params string[] responses) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal Uri? LastUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responses[Math.Min(Calls - 1, responses.Length - 1)], Encoding.UTF8, "application/json") });
        }
    }
}
