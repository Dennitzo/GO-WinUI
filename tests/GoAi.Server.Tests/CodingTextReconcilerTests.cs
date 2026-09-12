using GoAi.Contracts;
using GoAi.Server.Core.Coding;
using GoAi.Server.Core.Gateway;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class CodingTextReconcilerTests
{
    [Fact]
    public void ReplayedPrefixAcrossDifferentChunksIsSilentAndOnlyTheNewSuffixIsAppended()
    {
        var turn = new CodingTextReconciler(20);
        Assert.Equal(new TextDeltaEvent("Die Datei wird geprüft."), turn.Push("Die Datei wird geprüft."));
        turn.RestartAttempt();
        Assert.Null(turn.Push("Die Da"));
        Assert.Null(turn.Push("tei wird geprüft."));
        Assert.Equal(new TextDeltaEvent(" Danach folgt der Test."), turn.Push(" Danach folgt der Test."));
        Assert.Null(turn.Complete());
        Assert.Equal("Die Datei wird geprüft. Danach folgt der Test.", turn.VisibleText);
    }

    [Fact]
    public void DivergenceAndShorterCompletionReviseOnlyThisTurnAndProduceSafeFullTextOnTheWire()
    {
        const string previous = "Frühere Erklärung.\n";
        var turn = new CodingTextReconciler(previous.Length, "Die alte Antwort bleibt lang.");
        var patch = Assert.IsType<TextDeltaEvent>(turn.Push("Die neue Antwort."));
        var full = CodingTextReconciler.ToAuthoritativeRevision(previous + "Die alte Antwort bleibt lang.", patch);
        Assert.Equal(0, full.ReplaceFrom);
        Assert.Equal(previous + "Die neue Antwort.", full.Delta);
        turn.RestartAttempt();
        Assert.Null(turn.Push("Die neue"));
        var shorter = Assert.IsType<TextDeltaEvent>(turn.Complete());
        Assert.Equal(previous + "Die neue", CodingTextReconciler.ToAuthoritativeRevision(full.Delta, shorter).Delta);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualNativeRetryKeepsEarlierNarrationAndReconcilesItsVisibleTurn(bool diverge)
    {
        using var harness = new Harness(diverge);
        var runId = await harness.CreateRunAsync();
        await harness.Processor.ProcessAsync(runId, CancellationToken.None);
        var events = await harness.Repository.GetEventsAfterAsync(runId, 0);
        Assert.Equal(2, harness.Handler.ChatCalls);
        Assert.Equal(Harness.Previous + harness.Handler.FinalText, CodingTextReconciler.Project(events));
        Assert.Contains(events, item => item.Type == RunEventTypes.ModelGeneration
            && item.Data.GetProperty("state").GetString() == "generationRetry");
        var revisions = events.Where(item => item.Type == RunEventTypes.TextDelta
            && item.Data.TryGetProperty("replaceFrom", out var value) && value.ValueKind == JsonValueKind.Number).ToArray();
        if (diverge)
        {
            var revision = Assert.Single(revisions);
            Assert.Equal(0, revision.Data.GetProperty("replaceFrom").GetInt32());
            Assert.StartsWith(Harness.Previous, revision.Data.GetProperty("delta").GetString());
        }
        else Assert.Empty(revisions);
        Assert.DoesNotContain(events, item => item.Type == RunEventTypes.ClientToolProposed);
        Assert.Equal(RunState.Completed, (await harness.Repository.GetAsync(runId))!.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryReadsTheJournalAfterThePersistedTurnBoundaryWithoutReplayingPreviousText(bool diverge)
    {
        using var harness = new Harness(diverge);
        harness.Handler.HoldRetry = true;
        var runId = await harness.CreateRunAsync();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var interrupted = harness.Processor.ProcessAsync(runId, stop.Token);
        await harness.Handler.RetryEntered.Task.WaitAsync(stop.Token);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interrupted);
        var checkpoint = (await harness.Repository.GetCheckpointAsync(runId))!;
        Assert.NotNull(checkpoint.StreamingTurnStartEventId);
        Assert.Equal(Harness.Previous.Length, checkpoint.VisibleTextLength);
        Assert.Equal(Harness.Previous + NativeHandler.FirstText,
            CodingTextReconciler.Project(await harness.Repository.GetEventsAfterAsync(runId, 0)));
        Assert.Equal("read-completed", Assert.Single(checkpoint.Messages, item => item.Role == "tool").ToolCallId);

        harness.Handler.HoldRetry = false;
        await harness.Processor.ProcessAsync(runId, CancellationToken.None);

        Assert.Equal(Harness.Previous + harness.Handler.FinalText,
            CodingTextReconciler.Project(await harness.Repository.GetEventsAfterAsync(runId, 0)));
        Assert.Equal(3, harness.Handler.ChatCalls);
        Assert.Equal(RunState.Completed, (await harness.Repository.GetAsync(runId))!.State);
        Assert.Null(await harness.Repository.GetCheckpointAsync(runId));
    }

    private sealed class Harness : IDisposable
    {
        internal const string Previous = "Die vorherige Datei wurde bereits gelesen.\n\n";
        private readonly TestServerContext _context = new();
        private readonly ServiceProvider _services;
        private readonly HttpClient _http;
        internal NativeHandler Handler { get; }
        internal RunRepository Repository { get; }
        internal RunProcessor Processor { get; }

        internal Harness(bool diverge)
        {
            _context.Options.ModelRuntimeUri = new Uri("http://native.test");
            Handler = new(diverge);
            _http = new(Handler);
            var runtime = new ModelRuntimeClient(_http, _context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddGoAiServerServices(_context.Options, includeHostedServices: false);
            services.AddSingleton(_context.Database);
            services.AddSingleton(runtime);
            _services = services.BuildServiceProvider();
            Repository = _services.GetRequiredService<RunRepository>();
            Processor = _services.GetRequiredService<RunProcessor>();
        }

        internal async Task<string> CreateRunAsync()
        {
            const string prompt = "Setze die Projektprüfung fort und fasse das Ergebnis zusammen.";
            var runId = (await Repository.CreateAsync(new RunRequest(GoAiProtocol.Version, RunMode.Coding,
                [new RunMessage("user", [new ContentPart("text", prompt)])], ClientCapabilities: ["coding"],
                Limits: new RunLimits(TimeoutSeconds: 0), AllowedServerTools: [], PreferredCodingModelId: NativeHandler.ModelId), null)).Snapshot.RunId;
            var call = new LmToolCall("read-completed", ClientToolNames.CodingRead, JsonSerializer.SerializeToElement(new { path = "sample.cs" }));
            await Repository.AppendEventAsync(runId, RunEventTypes.TextDelta, new TextDeltaEvent(Previous));
            await Repository.SaveCheckpointAsync(runId, new AgentRunCheckpoint(
                [new("user", prompt), new("assistant", Previous, ToolCalls: [call]), new("tool", "{\"content\":\"verified\"}", ToolCallId: call.Id)],
                1, 1, 10, 10));
            return runId;
        }

        public void Dispose()
        {
            _services.Dispose();
            _http.Dispose();
            _context.Dispose();
        }
    }

    private sealed class NativeHandler(bool diverge) : HttpMessageHandler
    {
        internal const string ModelId = "coding/RetryFixture-Q4~abc123";
        internal const string FirstText = "Ich prüfe die nächste Funktion und ihre Aufrufstellen sorgfältig. Die bisherige Annahme ist noch unbestätigt und bleibt deshalb vorläufig.";
        private static readonly string[] ModelTags = ["go-context-train:32768"];
        internal string FinalText { get; } = diverge ? "Die neue Prüfung zeigt einen anderen Zusammenhang. Die Antwort wurde anhand der Datei korrigiert." : FirstText + " Die Prüfung ist jetzt abgeschlossen.";
        internal int ChatCalls { get; private set; }
        internal bool HoldRetry { get; set; }
        internal TaskCompletionSource RetryEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/models") return Json(new { data = new[] { new { id = ModelId, tags = ModelTags, status = new { value = "loaded" } } } });
            if (path == "/props") return Json(new { default_generation_settings = new { n_ctx = 32768 } });
            if (path == "/v1/chat/completions/input_tokens") return Json(new { input_tokens = 32 });
            if (path != "/v1/chat/completions") throw new InvalidOperationException("Unexpected endpoint " + path);
            ChatCalls++;
            if (ChatCalls > 1 && HoldRetry)
            {
                RetryEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            var incomplete = ChatCalls == 1;
            var frame = JsonSerializer.Serialize(new
            {
                choices = new[] { new { index = 0, delta = new { content = incomplete ? FirstText : FinalText }, finish_reason = incomplete ? null : "stop" } },
                usage = new { prompt_tokens = 32, completion_tokens = 32 },
            });
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("data: " + frame + "\n\n" + (incomplete ? "" : "data: [DONE]\n\n"), Encoding.UTF8, "text/event-stream"),
            };
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
