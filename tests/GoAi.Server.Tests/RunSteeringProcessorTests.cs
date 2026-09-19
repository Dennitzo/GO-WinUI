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

public sealed class RunSteeringProcessorTests
{
    [Theory]
    [InlineData("Zeile eins\r\nZeile zwei\rEnde", "Zeile eins\nZeile zwei\nEnde")]
    [InlineData("GO_SESSION_TITLE: Versteckt\nErgebnis", "Ergebnis")]
    [InlineData("**GO_SESSION_TITLE:** Versteckt\n\n\nErgebnis", "Ergebnis")]
    [InlineData("Ergebnis GO_SESSION_TITLE: Anhang", "Ergebnis Anhang")]
    public void DurableVisibleHistoryUsesTheDesktopCanonicalText(string raw, string expected) =>
        Assert.Equal(expected, RunVisibleText.Canonicalize(raw));

    [Theory]
    [InlineData(RunMode.General)]
    [InlineData(RunMode.Coding)]
    public async Task SteeringInterruptsRealReasoningStreamAndContinuesTheSameRunAndNativePrefix(RunMode mode)
    {
        using var harness = new Harness(mode, blockFirst: true);
        var run = await harness.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var processing = harness.Processor.ProcessAsync(run, deadline.Token);
        await harness.Handler.Blocked.Task.WaitAsync(deadline.Token);
        var input = await harness.Repository.AcceptSteeringAsync(run, new("steering-session", "input-1", "Die Ausgabe soll jetzt blau sein."), deadline.Token);
        await processing.WaitAsync(deadline.Token);
        var events = await harness.Repository.GetEventsAfterAsync(run, 0, deadline.Token);
        Assert.Equal(RunState.Completed, (await harness.Repository.GetAsync(run, deadline.Token))!.State);
        Assert.Equal(2, harness.Handler.Prompts.Count);
        Assert.Single(events, item => item.Type == RunEventTypes.RunStarted);
        Assert.Single(events, item => item.Type == RunEventTypes.RunCompleted);
        Assert.Single(events, item => item.Type == RunSteeringEventTypes.Applied);
        Assert.Contains(events, item => item.Type == RunEventTypes.ReasoningDelta && item.Data.GetProperty("state").GetString() == "steered");
        Assert.DoesNotContain(events, item => item.Type is RunEventTypes.RunCancelled or RunEventTypes.RunFailed);
        Assert.Equal("Blau ist gespeichert.", CodingTextReconciler.Project(events));
        Assert.True(harness.Handler.FirstGenerationCancelled);
        var old = harness.Handler.Prompts[0];
        var resumed = harness.Handler.Prompts[1];
        Assert.Equal(old, resumed.Take(old.Length));
        Assert.Contains(resumed, message => message.Role == "user" && message.Content == "Die Ausgabe soll jetzt blau sein.");
        Assert.Equal("applied", (await harness.Repository.AcceptSteeringAsync(run,
            new("steering-session", "input-1", "Die Ausgabe soll jetzt blau sein."), deadline.Token)).State);
        Assert.True(input.Sequence > 0);
    }

    [Fact]
    public async Task SteeringWaitsForPublishedToolReceiptAndDiscardsOnlyUndispatchedSiblingCalls()
    {
        using var harness = new Harness(RunMode.Coding);
        var run = await harness.CreateAsync();
        var first = ReadCall("read-in-flight", "first.cs");
        var stale = ReadCall("read-stale", "stale.cs");
        var proposal = new ToolProposal("proposal-in-flight", run, first.Name, first.Arguments, ToolRiskClass.ReadOnly,
            "Datei lesen", DateTimeOffset.MaxValue);
        await harness.Repository.SaveToolProposalAsync(proposal);
        await harness.Repository.SaveCheckpointAsync(run, new([new("user", "Dateien prüfen"),
            new("assistant", null, ToolCalls: [first, stale])], 1, 2, 0, 0,
            ActiveToolCalls: [first, stale], PendingProposalId: proposal.ProposalId, PendingToolCallId: first.Id));
        await harness.Repository.EnsureClientToolProposedEventAsync(run, proposal.ProposalId);
        await harness.Repository.AcceptSteeringAsync(run, new("steering-session", "input-tool", "Keine weiteren Dateien lesen; Ergebnis blau."));
        await Assert.ThrowsAsync<RunWaitingForClientException>(() => harness.Processor.ProcessAsync(run, CancellationToken.None));
        Assert.Empty(harness.Handler.Prompts);
        Assert.Single(await harness.Repository.GetPendingSteeringAsync(run));
        Assert.Equal(proposal.ProposalId, (await harness.Repository.GetCheckpointAsync(run))!.PendingProposalId);

        await harness.Repository.SaveClientToolResultAsync(run, new(proposal.ProposalId, "completed", JsonSerializer.SerializeToElement(new { content = "Beleg first.cs" })));
        await harness.Processor.ProcessAsync(run, CancellationToken.None);
        var prompt = Assert.Single(harness.Handler.Prompts);
        var receipt = Assert.Single(prompt, message => message.ToolCallId == first.Id);
        Assert.Contains("Beleg first.cs", receipt.Content);
        Assert.Contains("not_executed", Assert.Single(prompt, message => message.ToolCallId == stale.Id).Content);
        Assert.True(Array.FindIndex(prompt, message => message.ToolCallId == first.Id)
            < Array.FindIndex(prompt, message => message.Role == "user" && message.Content!.Contains("Keine weiteren", StringComparison.Ordinal)));
        var events = await harness.Repository.GetEventsAfterAsync(run, 0);
        Assert.Single(events, item => item.Type == RunEventTypes.ClientToolProposed);
        Assert.Equal(RunState.Completed, (await harness.Repository.GetAsync(run))!.State);
    }

    [Fact]
    public async Task RecoveryDoesNotPublishAStaleProposalThatNeverCrossedDispatchBoundary()
    {
        using var harness = new Harness(RunMode.Coding);
        var run = await harness.CreateAsync();
        var call = ReadCall("not-dispatched", "stale.cs");
        var proposal = new ToolProposal("proposal-unpublished", run, call.Name, call.Arguments, ToolRiskClass.ReadOnly,
            "Datei lesen", DateTimeOffset.MaxValue);
        await harness.Repository.SaveToolProposalAsync(proposal);
        await harness.Repository.SaveCheckpointAsync(run, new([new("user", "Datei lesen"), new("assistant", null, ToolCalls: [call])],
            1, 1, 0, 0, ActiveToolCalls: [call], PendingProposalId: proposal.ProposalId, PendingToolCallId: call.Id));
        await harness.Repository.AcceptSteeringAsync(run, new("steering-session", "input-recover", "Nicht mehr lesen, antworte blau."));
        await harness.Processor.ProcessAsync(run, CancellationToken.None);
        Assert.DoesNotContain(await harness.Repository.GetEventsAfterAsync(run, 0), item => item.Type == RunEventTypes.ClientToolProposed);
        Assert.Contains("not_executed", Assert.Single(Assert.Single(harness.Handler.Prompts), message => message.ToolCallId == call.Id).Content);
        Assert.Equal(RunState.Completed, (await harness.Repository.GetAsync(run))!.State);
    }

    [Theory]
    [InlineData("Erstes Ergebnis. ", "Erstes Ergebnis. ")]
    [InlineData("Erstes Ergebnis.\r\nWeiter.\r\n", "Erstes Ergebnis.\nWeiter.\n")]
    public async Task GeneralFollowUpMatchesSegmentedSteeringHistoryAndRetainsNativePrefix(string rawPrefix, string visiblePrefix)
    {
        using var harness = new Harness(RunMode.General);
        var run = await harness.CreateAsync();
        await harness.Repository.AppendEventAsync(run, RunEventTypes.TextDelta, new TextDeltaEvent(rawPrefix));
        var originalNative = ModelRuntimeClient.PrepareLanguageBoundMessages(RunProcessor.CreateInitialMessages(harness.Request, "general", []));
        await harness.Repository.SaveCheckpointAsync(run, new([.. originalNative, new("assistant", rawPrefix)],
            1, 0, 0, 0, VisibleTextLength: rawPrefix.Length));
        await harness.Repository.AcceptSteeringAsync(run, new("steering-session", "input-history", "Jetzt blau."));
        await harness.Processor.ProcessAsync(run, CancellationToken.None);
        var nextRequest = harness.Request with { Messages = [.. harness.Request.Messages,
            new("assistant", [new("text", Text: visiblePrefix)]), new("user", [new("text", Text: "Jetzt blau.")]),
            new("assistant", [new("text", Text: "Blau ist gespeichert.")]), new("user", [new("text", Text: "Welche Farbe?")])] };
        var next = (await harness.Repository.CreateAsync(nextRequest, null)).Snapshot.RunId;
        var previous = await harness.Repository.GetGeneralSessionContextAsync(next, nextRequest);
        Assert.NotNull(previous);
        Assert.True(GeneralSessionContext.TryContinue(previous, nextRequest,
            RunProcessor.CreateInitialMessages(nextRequest, "general", []), out var continued));
        Assert.Equal(previous.Checkpoint.Messages, ModelRuntimeClient.PrepareLanguageBoundMessages(continued).Take(previous.Checkpoint.Messages.Count));
        Assert.Equal("Welche Farbe?", continued[^1].Content);
    }

    [Theory]
    [InlineData(RunMode.Coding)]
    [InlineData(RunMode.General)]
    public async Task RecoveryRetainsAlreadyVisibleInterruptedTailBeforeApplyingSteering(RunMode mode)
    {
        using var harness = new Harness(mode);
        var run = await harness.CreateAsync();
        await harness.Repository.SaveCheckpointAsync(run, new([new("user", "Ursprünglicher Auftrag")], 0, 0, 0, 0,
            VisibleTextLength: 0, StreamingTurnStartEventId: 0));
        await harness.Repository.UpdateStateAsync(run, RunState.Running);
        await harness.Repository.AppendEventAsync(run, RunEventTypes.TextDelta, new TextDeltaEvent("Sichtbarer Zwischenstand."));
        await harness.Repository.AcceptSteeringAsync(run, new("steering-session", "restart-input", "Jetzt blau."));
        Assert.Contains(run, await harness.Repository.RecoverAsync());
        await harness.Processor.ProcessAsync(run, CancellationToken.None);
        var prompt = Assert.Single(harness.Handler.Prompts);
        var previous = Array.FindIndex(prompt, message => message.Role == "assistant" && message.Content == "Sichtbarer Zwischenstand.");
        var input = Array.FindIndex(prompt, message => message.Role == "user" && message.Content == "Jetzt blau.");
        Assert.True(previous >= 0 && previous < input);
        var applied = Assert.Single(await harness.Repository.GetEventsAfterAsync(run, 0), item => item.Type == RunSteeringEventTypes.Applied);
        Assert.Equal("Sichtbarer Zwischenstand.".Length, applied.Data.GetProperty("visibleTextOffset").GetInt32());
    }

    private static LmToolCall ReadCall(string id, string path) => new(id, ClientToolNames.CodingRead, JsonSerializer.SerializeToElement(new { path }));

    private sealed class Harness : IDisposable
    {
        private readonly TestServerContext _context = new();
        private readonly ServiceProvider _services;
        private readonly HttpClient _http;
        internal NativeHandler Handler { get; }
        internal RunRepository Repository { get; }
        internal RunProcessor Processor { get; }
        internal RunRequest Request { get; }

        internal Harness(RunMode mode, bool blockFirst = false)
        {
            _context.Options.ModelRuntimeUri = new("http://native.test");
            Handler = new(blockFirst);
            _http = new(Handler);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddGoAiServerServices(_context.Options, includeHostedServices: false);
            services.AddSingleton(_context.Database);
            services.AddSingleton(new ModelRuntimeClient(_http, _context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance));
            _services = services.BuildServiceProvider();
            Repository = _services.GetRequiredService<RunRepository>();
            Processor = _services.GetRequiredService<RunProcessor>();
            Request = new(GoAiProtocol.Version, mode, [new("user", [new("text", Text: "Ursprünglicher Auftrag")])],
                ClientCapabilities: mode == RunMode.Coding ? ["coding"] : [], AllowedServerTools: [], SessionId: "steering-session",
                PreferredGeneralModelId: NativeHandler.ModelId, PreferredCodingModelId: NativeHandler.ModelId,
                Limits: new(TimeoutSeconds: 0));
        }

        internal async Task<string> CreateAsync() => (await Repository.CreateAsync(Request, null)).Snapshot.RunId;
        public void Dispose() { _services.Dispose(); _http.Dispose(); _context.Dispose(); }
    }

    private sealed class NativeHandler(bool blockFirst) : HttpMessageHandler
    {
        internal const string ModelId = "coding/SteerFixture-Q4~abc123";
        private static readonly string[] ModelTags = ["go-context-train:32768"];
        internal List<LmChatMessage[]> Prompts { get; } = [];
        internal TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool FirstGenerationCancelled { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path is "/sessions/prepare" or "/sessions/save") return new(HttpStatusCode.NotFound);
            if (path == "/v1/models") return Json(new { data = new[] { new { id = ModelId, tags = ModelTags, status = new { value = "loaded" } } } });
            if (path == "/props") return Json(new { default_generation_settings = new { n_ctx = 32768 } });
            if (path == "/v1/chat/completions/input_tokens") return Json(new { input_tokens = 32 });
            if (path != "/v1/chat/completions") throw new InvalidOperationException("Unexpected endpoint " + path);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Prompts.Add(body.RootElement.GetProperty("messages").EnumerateArray().Select(message => new LmChatMessage(
                message.GetProperty("role").GetString()!, message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String ? content.GetString() : null,
                ToolCallId: message.TryGetProperty("tool_call_id", out var id) ? id.GetString() : null)).ToArray());
            if (blockFirst && Prompts.Count == 1)
                return new(HttpStatusCode.OK) { Content = new StreamContent(new BlockingStream(Blocked, () => FirstGenerationCancelled = true))
                    { Headers = { ContentType = new("text/event-stream") } } };
            return Json(new { choices = new[] { new { message = new { content = "Blau ist gespeichert." }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 32, completion_tokens = 8 } });
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }

    private sealed class BlockingStream(TaskCompletionSource blocked, Action cancelled)
        : MemoryStream(Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"Ich prüfe den bisherigen Auftrag.\"}}]}\n\n"))
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < Length) return await base.ReadAsync(buffer, cancellationToken);
            blocked.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { cancelled(); throw; }
            return 0;
        }
    }
}
