using GoAi.Client;
using GoAi.Contracts;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace GoAi.Server.Tests;

/// <summary>Natural-task acceptance through the deployed gateway and local Qwen27 model; no tool call is forced.</summary>
public sealed class CodingResearchLiveTests(ITestOutputHelper output)
{
    private static readonly string[] AllowedResearchTools = ["web.search", "web.fetch", "web.deepResearch"];
    private static readonly JsonSerializerOptions Json = GoAiProtocol.CreateJsonOptions();
    private static readonly Regex CitationUrl = new("""https?://[^\s<>"'`\[\]()]+""", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly char[] CitationTrailingPunctuation = ['.', ',', ';', ':', '!', '?'];

    [Fact]
    [Trait("Category", "Live")]
    public Task Qwen27VerifiesSimpleQuestionFromOriginalDocumentation() => RunConfiguredTaskAsync(requireDeepResearch: false);

    [Fact]
    [Trait("Category", "Live")]
    public Task Qwen27ChoosesDeepResearchFromComplexNaturalTask() => RunConfiguredTaskAsync(requireDeepResearch: true);

    private async Task RunConfiguredTaskAsync(bool requireDeepResearch)
    {
        if (Environment.GetEnvironmentVariable("GO_AI_CODING_RESEARCH_LIVE") != "1") return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        using var http = new HttpClient
        {
            BaseAddress = new Uri((Environment.GetEnvironmentVariable("GO_AI_SERVER_URL") ?? "http://127.0.0.1:8080").TrimEnd('/') + "/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var client = new GoAiClient(http, "go-coding-research-live-" + Guid.NewGuid().ToString("N"));
        var catalog = await client.GetCodingModelsAsync(timeout.Token);
        Assert.True(catalog.RuntimeReachable, catalog.Message);
        var selectedId = Environment.GetEnvironmentVariable("GO_AI_LIVE_CODING_MODEL")
            ?? catalog.Models.FirstOrDefault(static model => model.Id.Contains("Qwen3.8-27B", StringComparison.OrdinalIgnoreCase))?.Id;
        Assert.False(string.IsNullOrWhiteSpace(selectedId), "The installed Qwen27 coding model was not found.");
        Assert.Contains(catalog.Models, model => model.Id == selectedId);
        output.WriteLine("Coding research model: " + selectedId);

        var prompt = requireDeepResearch
            ? "Ich plane einen lokalen Coding-Agenten mit Python: Vergleiche auf Grundlage aktueller Primärquellen asyncio.wait_for, "
                + "asyncio.timeout und TaskGroup für Abbruch, Zeitlimits und das Aufräumen gestarteter Teilaufgaben. Untersuche mehrere "
                + "Teilfragen und mehrere Originalquellen gründlich, berücksichtige Versionsänderungen und widersprüchliche Hinweise. "
                + "Leite eine kurze belegte Empfehlung ab und kennzeichne offene Punkte. Die Aufgabe betrifft nur öffentliche APIs; "
                + "es werden keine lokalen Dateien gelesen, verändert oder ausgeführt."
            : "Prüfe die aktuelle offizielle Python-Dokumentation: Welche Exception wird beim Ablauf von asyncio.timeout ausgelöst, "
            + "und an welcher Stelle soll sie abgefangen werden? Eine kurze Antwort mit einem Link zur tatsächlich geprüften "
            + "offiziellen Quelle genügt. Dies ist ausschließlich eine Frage zu öffentlichen APIs; lokale Projektdateien sind dafür nicht erforderlich.";
        await RunTaskAsync(client, selectedId!, prompt, requireDeepResearch, timeout.Token);
    }

    private async Task RunTaskAsync(GoAiClient client, string modelId, string prompt, bool requireDeepResearch, CancellationToken cancellationToken)
    {
        var accepted = await client.CreateRunAsync(new RunRequest(GoAiProtocol.Version, RunMode.Coding,
            [new("user", [new("text", prompt)])], ClientCapabilities: ["coding"],
            Limits: new RunLimits(4_096, 32_768, 720), AllowedServerTools: AllowedResearchTools,
            PreferredCodingModelId: modelId), "coding-research-live-" + Guid.NewGuid().ToString("N"), cancellationToken);
        var runId = accepted.RunId;
        var terminal = false;
        RunFailedEvent? failure = null;
        var calls = new List<string>();
        var started = new Dictionary<string, string>(StringComparer.Ordinal);
        var completed = new HashSet<string>(StringComparer.Ordinal);
        var verifiedSources = new HashSet<string>(StringComparer.Ordinal);
        var states = new HashSet<string>(StringComparer.Ordinal);
        var finalText = new StringBuilder();
        var searchCount = 0;
        var fetchedCount = 0;
        var deepCompleted = false;
        long cursor = 0;
        try
        {
            output.WriteLine($"Research run={runId}; expectDeep={requireDeepResearch}");
            for (var reconnect = 0; !terminal && reconnect < 12; reconnect++)
            {
                await foreach (var item in client.StreamRunEventsAsync(runId, cursor, cancellationToken))
                {
                    Assert.True(item.Id > cursor, "SSE IDs must increase across reconnections.");
                    cursor = item.Id;
                    switch (item.Type)
                    {
                        case RunEventTypes.ClientToolProposed:
                            throw new InvalidOperationException("Public research unexpectedly requested a local client tool: "
                                + item.Data.GetProperty("name").GetString());
                        case RunEventTypes.ModelGeneration:
                            states.Add(item.Data.GetProperty("state").GetString()!);
                            break;
                        case RunEventTypes.TextDelta:
                            var delta = item.Data.Deserialize<TextDeltaEvent>(Json)!.Delta;
                            Assert.True(finalText.Length + delta.Length <= 32_000, "The final answer exceeded the live-test text bound.");
                            finalText.Append(delta);
                            break;
                        case RunEventTypes.ServerToolStarted:
                            var name = item.Data.GetProperty("tool").GetString()!;
                            var callId = item.Data.GetProperty("callId").GetString()!;
                            Assert.Equal(callId, item.Data.GetProperty("toolCallId").GetString());
                            Assert.Contains(name, AllowedResearchTools);
                            Assert.True(started.TryAdd(callId, name), "A server tool started twice with the same call ID.");
                            Assert.True(started.Count <= 24, "Research exceeded the bounded acceptance tool count.");
                            calls.Add(name);
                            finalText.Clear();
                            break;
                        case RunEventTypes.ServerToolCompleted:
                            var finishedId = item.Data.GetProperty("callId").GetString()!;
                            var finishedName = item.Data.GetProperty("tool").GetString()!;
                            Assert.Equal(finishedId, item.Data.GetProperty("toolCallId").GetString());
                            Assert.Equal(finishedName, started[finishedId]);
                            Assert.True(completed.Add(finishedId));
                            var success = item.Data.GetProperty("success").GetBoolean();
                            var result = item.Data.GetProperty("result");
                            if (finishedName == "web.search" && success)
                            {
                                Assert.Equal("searxng", result.GetProperty("provider").GetString());
                                Assert.False(result.GetProperty("isFallback").GetBoolean());
                                searchCount++;
                            }
                            else if (finishedName == "web.fetch" && success && result.GetProperty("found").GetBoolean())
                            {
                                Assert.True(result.GetProperty("isUntrusted").GetBoolean());
                                Assert.True(result.GetProperty("matches").GetArrayLength() > 0);
                                verifiedSources.Add(NormalizeUrl(result.GetProperty("url").GetString()!));
                                fetchedCount++;
                            }
                            else if (finishedName == "web.deepResearch")
                            {
                                Assert.True(success, result.GetRawText());
                                Assert.Equal("searxng", result.GetProperty("provider").GetString());
                                Assert.False(result.GetProperty("isFallback").GetBoolean());
                                Assert.True(result.GetProperty("findings").GetArrayLength() > 0);
                                foreach (var source in result.GetProperty("sources").EnumerateArray())
                                    Assert.Contains(NormalizeUrl(source.GetProperty("url").GetString()!), verifiedSources);
                                deepCompleted = true;
                            }
                            break;
                        case RunEventTypes.RunFailed:
                            failure = item.Data.Deserialize<RunFailedEvent>(Json);
                            terminal = true;
                            break;
                        case RunEventTypes.RunCompleted:
                        case RunEventTypes.RunCancelled:
                            terminal = true;
                            break;
                    }
                    if (terminal) break;
                }
                if (!terminal) await Task.Delay(250, cancellationToken);
            }
            var snapshot = await client.GetRunAsync(runId, cancellationToken);
            output.WriteLine($"State={snapshot.State}; calls={string.Join(',', calls)}; phases={string.Join(',', states)}; "
                + $"searches={searchCount}; fetched={fetchedCount}; final={finalText.ToString()[..Math.Min(finalText.Length, 5_000)]}");
            Assert.True(snapshot.State == RunState.Completed, $"Research ended {snapshot.State}: {failure?.ErrorCode ?? snapshot.ErrorCode}; {failure?.Message}");
            Assert.Equal(started.Count, completed.Count);
            // A known official documentation URL can be fetched directly without an unnecessary search.
            Assert.True(fetchedCount > 0, "No original-source evidence was observed.");
            Assert.True(finalText.Length > 0, "The coding agent did not stream its final cited answer.");
            // Link formatting is not evidence: both Markdown targets and plain HTTPS references are checked against actual fetches.
            var citations = CitationUrl.Matches(finalText.ToString())
                .Select(static match => match.Value.TrimEnd(CitationTrailingPunctuation))
                .Where(static value => Uri.TryCreate(value, UriKind.Absolute, out _)).Select(NormalizeUrl).ToArray();
            Assert.Contains(citations, citation => verifiedSources.Contains(citation));
            if (requireDeepResearch)
            {
                Assert.Contains("web.deepResearch", calls);
                Assert.Contains("web.search", calls);
                Assert.True(deepCompleted);
                Assert.True(searchCount >= 2 && fetchedCount >= 2, "Deep Research must verify multiple searches and original sources.");
                Assert.Contains("deepResearchPlanning", states);
                Assert.Contains("deepResearchSearch", states);
                Assert.Contains("deepResearchFetch", states);
                Assert.Contains("deepResearchSynthesis", states);
            }
        }
        finally
        {
            if (!terminal)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await client.CancelRunAsync(runId, cleanup.Token); }
                catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
                {
                    output.WriteLine("Live-test cancellation cleanup: " + exception.Message);
                }
            }
        }
    }

    private static string NormalizeUrl(string value) => new UriBuilder(value) { Fragment = "" }.Uri.AbsoluteUri.TrimEnd('/');
}
