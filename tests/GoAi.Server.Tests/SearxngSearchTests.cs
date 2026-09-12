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
    private const string Unavailable = """{"results":[],"unresponsive_engines":[["brave","HTTP error 429"],["duckduckgo","CAPTCHA"],["google","unusual traffic"]]}""";

    [Fact]
    public async Task HttpSuccessWithBlockedEnginesIsAConcreteFailureWithoutHiddenRetryOrFallback()
    {
        using var handler = new SearchHandler(Unavailable);
        var exception = await Assert.ThrowsAsync<SearxngEngineUnavailableException>(() => Service(handler).SearchAsync(new("asyncio TaskGroup"), false));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.Equal(3, exception.Failures.Count);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("searxng", handler.LastUri!.Host);
        var failure = AgentToolExecutor.DescribeResearchFailure("web.search", exception);
        Assert.Equal("web.search.engines_unavailable", failure.ErrorCode);
        Assert.False(failure.Retryable);
        Assert.Contains("429", failure.Message, StringComparison.Ordinal);
        Assert.Contains("CAPTCHA", failure.Message, StringComparison.Ordinal);
        Assert.Contains("google: unusual traffic", failure.Message, StringComparison.Ordinal);
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
        Assert.Contains("engines=discuss.python,stackoverflow", Uri.UnescapeDataString(handler.LastUri.Query), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("auto", "Python asyncio TaskGroup", "discuss.python,stackoverflow")]
    [InlineData("web", "iframe sandbox allow-scripts", "mdn,microsoft learn")]
    [InlineData("auto", "C# Task cancellation", "microsoft learn,stackoverflow")]
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
    }

    [Fact]
    public async Task GenuineEmptyGeneralSearchRemainsEmptyAndUnknownProfilesNeverReachNetwork()
    {
        using var handler = new SearchHandler("""{"results":[],"unresponsive_engines":[]}""");
        var service = Service(handler);
        var search = await service.SearchAsync(new("new programming API"), false);
        Assert.Empty(search.Results);
        Assert.DoesNotContain("engines=", handler.LastUri!.Query, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(new("API", Profile: "https://other-provider.example/"), false));
        Assert.Equal(1, handler.Calls);
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
        var exception = await Assert.ThrowsAsync<SearxngEngineUnavailableException>(() => Service(handler).SearchAsync(new("API"), false));
        Assert.Equal(8, exception.Failures.Count);
        Assert.All(exception.Failures, static failure =>
        {
            Assert.True(failure.Engine.Length <= 64);
            Assert.Equal(160, failure.Reason.Length);
            Assert.DoesNotContain('\r', failure.Engine);
            Assert.DoesNotContain('\n', failure.Engine);
        });
    }

    private static WebResearchService Service(HttpMessageHandler handler) =>
        new(new TestClientFactory(handler), Options.Create(new GoAiServerOptions()));

    private sealed class TestClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class SearchHandler(string response) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal Uri? LastUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") });
        }
    }
}
