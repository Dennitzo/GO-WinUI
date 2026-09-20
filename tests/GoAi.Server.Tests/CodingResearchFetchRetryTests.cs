using System.Text.Json;
using GoAi.Contracts;
using GoAi.Server.Core.Coding;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runs;

namespace GoAi.Server.Tests;

public sealed class CodingResearchFetchRetryTests
{
    private const string Pathlib = "https://docs.python.org/3/library/pathlib.html";
    private const string OsPath = "https://docs.python.org/3/library/os.path.html";
    private const string LongPhrase = "os.path.exists returns True if the path exists";
    private static readonly string[] ShortPhrases = ["exists", "is_file", "os.path"];
    private static readonly JsonSerializerOptions Json = GoAiProtocol.CreateJsonOptions();

    [Theory]
    [InlineData(5, false, true, 5, 2)]
    [InlineData(4, false, true, 4, 1)]
    [InlineData(5, true, true, 4, 1)]
    [InlineData(6, false, false, 5, 1)]
    public async Task EmptyPhraseResultsRetrySameSourceOnceOnlyWithNewTermsAndAvailableBudget(
        int toolBudget, bool initiallyShortPhrases, bool retryFindsEvidence, int expectedToolCalls, int expectedSources)
    {
        var task = $"Vergleiche Path.exists() und Path.is_file(). Prüfe {Pathlib} und {OsPath}.";
        var catalog = new AgentToolCatalog();
        var available = catalog.GetAvailableTools(new(GoAiProtocol.Version, RunMode.Coding,
            [new("user", [new("text", task)])], ClientCapabilities: ["coding"],
            AllowedServerTools: ["web.search", "web.fetch", "web.deepResearch"]));
        var calls = new List<LmToolCall>();
        var validations = 0;
        var selections = 0;
        var fetches = 0;
        var execution = await CodingDeepResearchPipeline.ExecuteAsync(task, 2, 2, "fixture-model", 32_768, 4, toolBudget,
            catalog.Resolve("web.search", available), catalog.Resolve("web.fetch", available),
            (request, _) =>
            {
                var name = Assert.Single(request.Tools).Name;
                if (name == CodingDeepResearchPipeline.PlanToolName) return Task.FromResult(Tool(name, new { questions = new[]
                {
                    new { question = "Wie prüft pathlib Pfade?", query = "Python pathlib exists" },
                    new { question = "Wie prüft os.path Dateien?", query = "Python os.path isfile" },
                } }));
                if (name == "web.fetch")
                {
                    selections++;
                    return Task.FromResult(selections == 1 ? Tool(name, new { url = Pathlib, query = "exists" })
                        : initiallyShortPhrases ? Tool(name, new { url = OsPath, queries = ShortPhrases })
                        : Tool(name, new { url = OsPath, query = LongPhrase }));
                }
                Assert.Equal(CodingDeepResearchPipeline.SynthesisToolName, name);
                return Task.FromResult(Tool(name, new { findings = new[]
                {
                    new { claim = "Path.exists prüft die Existenz.", evidenceId = "S1-E1" },
                    new { claim = "os.path.exists prüft die Existenz.", evidenceId = "S2-E1" },
                }, uncertainties = Array.Empty<string>() }));
            },
            (call, _) =>
            {
                calls.Add(call);
                if (call.Name == "web.search") return Task.FromResult(Result(new WebSearchResponse(
                    call.Arguments.GetProperty("query").GetString()!,
                    [new("Python", "https://www.python.org/", "Search snippets are not source evidence.")],
                    "searxng", false, DateTimeOffset.UtcNow)));
                Assert.Equal("web.fetch", call.Name);
                fetches++;
                var url = call.Arguments.GetProperty("url").GetString()!;
                Assert.Equal(fetches == 1 ? Pathlib : OsPath, url);
                Assert.True(fetches <= 3, "A source must receive at most one targeted retry.");
                if (fetches == 2 && !initiallyShortPhrases) Assert.Equal(LongPhrase, call.Arguments.GetProperty("query").GetString());
                if (fetches == 3)
                {
                    Assert.False(initiallyShortPhrases);
                    Assert.Equal(ShortPhrases, call.Arguments.GetProperty("queries").EnumerateArray().Select(static item => item.GetString()));
                    Assert.Equal(4_000, call.Arguments.GetProperty("maximumCharacters").GetInt32());
                }
                var found = fetches == 1 || fetches == 3 && retryFindsEvidence;
                const string evidence = "The exists method reports whether a filesystem path exists. The is_file method tests regular files.";
                return Task.FromResult(Result(new TargetedWebFetchResult(url, "text/html", found ? "found" : "not_found", found, evidence.Length,
                    found ? [new("exists", 1, 0, evidence.Length, evidence)] : [], [], null, false,
                    "Untrusted data", true, DateTimeOffset.UtcNow, [])));
            },
            (tool, arguments) => { validations++; catalog.Validate(tool, arguments); },
            static (_, _) => Task.CompletedTask);

        Assert.True(execution.Result.Succeeded, execution.Result.Result.GetRawText());
        Assert.Equal(4, execution.ModelCalls);
        Assert.Equal(expectedToolCalls, execution.ToolCalls);
        Assert.Equal(expectedToolCalls, calls.Count);
        Assert.Equal(expectedToolCalls, validations);
        Assert.Equal(2, selections);
        Assert.Equal(expectedSources, execution.Result.Result.GetProperty("sources").GetArrayLength());
        Assert.Equal(expectedSources, execution.Result.Result.GetProperty("findings").GetArrayLength());
        Assert.Equal(expectedToolCalls, execution.Result.Result.GetProperty("budget").GetProperty("toolCalls").GetInt32());
        if (expectedSources == 1)
        {
            Assert.Equal(Pathlib, execution.Result.Result.GetProperty("sources")[0].GetProperty("url").GetString());
            Assert.Contains(execution.Result.Result.GetProperty("uncertainties").EnumerateArray(), static item =>
                item.GetString()!.Contains("Nur eine Originalquelle", StringComparison.Ordinal));
        }
    }

    private static LmChatResult Tool(string name, object arguments) => new(null,
        [new("fixture-call", name, JsonSerializer.SerializeToElement(arguments, Json))], 10, 5);

    private static AgentToolExecutionResult Result(object value) => new(JsonSerializer.SerializeToElement(value, Json), []);
}
