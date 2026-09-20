using GoAi.Contracts;
using GoAi.Server.Core.Coding;
using GoAi.Server.Core.Data;
using GoAi.Server.Core.Gateway;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class GeneralSessionCacheIntegrationTests
{
    private static readonly int[] ContinuedRequestIndices = [2, 3, 4];

    [Fact]
    public async Task CompletedToolCallSavesNativeSessionBeforeTheClientToolCanStart()
    {
        using var context = new TestServerContext();
        context.Options.ModelRuntimeUri = new("http://native.test:19090");
        using var native = new NativeHandler { ReturnToolCall = true };
        using var http = new HttpClient(native);
        using var runtime = new ModelRuntimeClient(http, context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance);
        var result = await runtime.CompleteChatAsync(NativeHandler.ModelA,
            [new("user", "Lies die Datei.")], [new("coding.read", "Read", JsonSerializer.SerializeToElement(new
            {
                type = "object", properties = new { path = new { type = "string" } },
            }))], modelRole: "coding", sessionCacheKey: "tool-session");
        Assert.Single(result.ToolCalls);
        var call = Assert.Single(native.Requests);
        var saved = native.Operations[call.OperationIndex + 1];
        Assert.Equal("/sessions/save", saved.Path);
        Assert.Equal("tool-session", saved.SessionKey);
    }

    [Theory]
    [InlineData(RunMode.General)]
    [InlineData(RunMode.Coding)]
    public async Task StopDuringFirstInferencePreservesNativePrefixAcrossNewRunsInSameSession(RunMode mode)
    {
        using var context = new TestServerContext();
        context.Options.ModelRuntimeUri = new("http://native.test:19090");
        context.Options.GeneralModelId = NativeHandler.ModelA;
        context.Options.CodingModelId = NativeHandler.ModelA;
        using var native = new NativeHandler { HoldFirstCompletion = true };
        using var services = new Services(context, native);
        var request = Request("stopped-session", [Message("user", "Merke dir EICHE-42 und erkläre ausführlich.")], NativeHandler.ModelA) with
        {
            Mode = mode, PreferredCodingModelId = NativeHandler.ModelA,
            ClientCapabilities = mode == RunMode.Coding ? ["coding"] : [],
            CodingOptions = mode == RunMode.Coding ? new(WorkspacePath: "C:/cache-test", ContinueSessionContext: true) : null,
        };
        var stopped = await services.StopDuringInferenceAsync(request, native.InferenceStarted.Task);
        var checkpoint = await services.Repository.GetCheckpointAsync(stopped);
        Assert.NotNull(checkpoint);
        Assert.True(checkpoint.PreserveSessionPromptPrefix);
        Assert.Equal(RunState.Cancelled, (await services.Repository.GetAsync(stopped))!.State);
        Assert.Contains(native.Operations, operation => operation.Path == "/sessions/save"
            && operation.SessionKey == native.Requests[0].SessionKey);

        var next = request with { Messages = [.. request.Messages, Message("user", "Stop beendet. Nenne nur die Kennung.")] };
        await services.CompleteAsync(next);
        AssertNativePrefix(native.Requests[0].Body, native.Requests[1].Body);
        var third = next with { Messages = [.. next.Messages, Message("assistant", native.Answers[^1]), Message("user", "Bestätige sie noch einmal.")] };
        await services.CompleteAsync(third);
        AssertNativePrefix(native.Requests[1].Body, native.Requests[2].Body);
        Assert.All(native.Requests, item => Assert.Equal(native.Requests[0].SessionKey, item.SessionKey));
        Assert.Equal(3, native.Requests.Count);
    }
    [Fact]
    public async Task GeneralRunsReuseExactNativePrefixAndReasoningAcrossSessionsModelsAndServiceRecreation()
    {
        using var context = new TestServerContext();
        context.Options.ModelRuntimeUri = new("http://native.test:19090");
        context.Options.GeneralModelId = NativeHandler.ModelA;
        using var native = new NativeHandler();
        var history = new List<RunMessage> { Message("user", "Die Projektkennung ist SONNE-42. Merke sie dir für die nächsten Fragen.") };
        string lastRun;
        using (var firstServices = new Services(context, native))
        {
            lastRun = await firstServices.CompleteAsync(Request("project-session", history, NativeHandler.ModelA));
            history.Add(Message("assistant", native.Answers[0]));
            history.Add(Message("user", "Welche Kennung habe ich genannt?"));

            // A different visible chat between prompts must select another native
            // session identity and must never inherit project-session reasoning.
            await firstServices.CompleteAsync(Request("different-session", [Message("user", "Eine unabhängige Frage zu REGEN-9.")], NativeHandler.ModelA));
            lastRun = await firstServices.CompleteAsync(Request("project-session", history, NativeHandler.ModelA));
            AssertNativePrefix(native.Requests[0].Body, native.Requests[2].Body);
            Assert.Contains(native.Requests[2].Body.GetProperty("messages").EnumerateArray(), message =>
                message.TryGetProperty("reasoning_content", out var reasoning) && reasoning.GetString() == NativeHandler.Reasoning(1));

            history.Add(Message("assistant", native.Answers[2]));
            history.Add(Message("user", "Bestätige die Kennung mit dem anderen Modell."));
            lastRun = await firstServices.CompleteAsync(Request("project-session", history, NativeHandler.ModelB));
            AssertNativePrefix(native.Requests[2].Body, native.Requests[3].Body);
            Assert.Equal(NativeHandler.ModelB, native.Requests[3].Body.GetProperty("model").GetString());
            Assert.Contains(native.Operations, operation => operation.Path == "/models/unload" && operation.Model == NativeHandler.ModelA);
            Assert.Contains(native.Operations, operation => operation.Path == "/models/load" && operation.Model == NativeHandler.ModelB);
            Assert.Null(await firstServices.Repository.GetCheckpointAsync(lastRun));
        }

        // This creates a new database wrapper, repository, processor and runtime
        // client. Continuation must therefore come from SQLite, not an object cache.
        using (var restartedServices = new Services(context, native))
        {
            history.Add(Message("assistant", native.Answers[3]));
            history.Add(Message("user", "Bestätige nach dem Neustart erneut die ursprüngliche Kennung."));
            var restored = await restartedServices.CompleteAsync(Request("project-session", history, NativeHandler.ModelA));
            Assert.NotEqual(lastRun, restored);
            AssertNativePrefix(native.Requests[3].Body, native.Requests[4].Body);
            Assert.Equal(NativeHandler.ModelA, native.Requests[4].Body.GetProperty("model").GetString());
            Assert.Contains(native.Requests[4].Body.GetProperty("messages").EnumerateArray(), message =>
                message.TryGetProperty("reasoning_content", out var reasoning) && reasoning.GetString() == NativeHandler.Reasoning(4));
        }

        Assert.Equal(5, native.Requests.Count);
        var key = native.Requests[0].SessionKey;
        Assert.False(string.IsNullOrWhiteSpace(key));
        Assert.NotEqual(key, native.Requests[1].SessionKey);
        Assert.All(ContinuedRequestIndices, index => Assert.Equal(key, native.Requests[index].SessionKey));
        Assert.DoesNotContain("SONNE-42", native.Requests[1].Body.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(native.Requests[1].Body.GetProperty("messages").EnumerateArray(),
            message => message.TryGetProperty("reasoning_content", out _));
        Assert.All(native.Requests, request =>
        {
            Assert.True(request.Body.GetProperty("cache_prompt").GetBoolean());
            Assert.Equal("system", request.Body.GetProperty("messages")[0].GetProperty("role").GetString());
            Assert.Contains(CodingAgentPolicy.ReasoningLanguagePrompt,
                request.Body.GetProperty("messages")[0].GetProperty("content").GetString(), StringComparison.Ordinal);
            Assert.Contains("ausschließlich auf Deutsch", request.Body.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString(), StringComparison.Ordinal);
            var before = native.Operations[request.OperationIndex - 1];
            var after = native.Operations[request.OperationIndex + 1];
            Assert.Equal("/sessions/prepare", before.Path);
            Assert.Equal(request.SessionKey, before.SessionKey);
            Assert.Equal("/sessions/save", after.Path);
            Assert.Equal(request.SessionKey, after.SessionKey);
            Assert.Equal(request.Body.GetProperty("model").GetString(), after.Model);
        });
        // The fixture proves native requests and durable exact-prefix continuity.
        // Hardware KV cache hits are deliberately not fabricated by this handler.
    }

    [Fact]
    public async Task EditedVisibleGeneralHistoryNeverRecoversAnObsoleteProviderReasoningTail()
    {
        using var context = new TestServerContext();
        context.Options.ModelRuntimeUri = new("http://native.test:19090");
        context.Options.GeneralModelId = NativeHandler.ModelA;
        using var native = new NativeHandler();
        using var services = new Services(context, native);
        await services.CompleteAsync(Request("edited-session", [Message("user", "Behalte den ursprünglichen Inhalt ALT-123.")], NativeHandler.ModelA));
        await services.CompleteAsync(Request("edited-session",
            [Message("user", "Der sichtbare Verlauf wurde zu NEU-789 geändert."), Message("assistant", native.Answers[0]),
                Message("user", "Nutze nur den bearbeiteten Verlauf.")], NativeHandler.ModelA));
        var sent = native.Requests[1].Body.GetProperty("messages");
        Assert.DoesNotContain("ALT-123", sent.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("NEU-789", sent.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(sent.EnumerateArray(), message => message.TryGetProperty("reasoning_content", out _));
        Assert.Equal(native.Requests[0].SessionKey, native.Requests[1].SessionKey);
    }

    private static RunMessage Message(string role, string text) => new(role, [new("text", text)]);

    private static RunRequest Request(string session, IEnumerable<RunMessage> history, string model) => new(
        GoAiProtocol.Version, RunMode.General, history.ToArray(), SessionId: session,
        ClientCapabilities: [], AllowedServerTools: [], PreferredGeneralModelId: model);

    private static void AssertNativePrefix(JsonElement previous, JsonElement next)
    {
        var prefix = previous.GetProperty("messages").EnumerateArray().Select(message => message.GetRawText()).ToArray();
        var continued = next.GetProperty("messages").EnumerateArray().Select(message => message.GetRawText()).ToArray();
        Assert.True(continued.Length > prefix.Length);
        Assert.Equal(prefix, continued.Take(prefix.Length));
    }

    private sealed class Services : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly HttpClient _http;
        private readonly GoAiDatabase _database;
        private readonly ModelRuntimeClient _runtime;
        internal RunRepository Repository { get; }

        internal Services(TestServerContext context, NativeHandler native)
        {
            _database = new GoAiDatabase(context.WrappedOptions);
            _http = new(native, disposeHandler: false);
            _runtime = new(_http, context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddGoAiServerServices(context.Options, includeHostedServices: false);
            services.AddSingleton(_database);
            services.AddSingleton(_runtime);
            _provider = services.BuildServiceProvider();
            Repository = _provider.GetRequiredService<RunRepository>();
        }

        internal async Task<string> CompleteAsync(RunRequest request)
        {
            var id = (await Repository.CreateAsync(request, null)).Snapshot.RunId;
            await _provider.GetRequiredService<RunProcessor>().ProcessAsync(id, CancellationToken.None);
            Assert.Equal(RunState.Completed, (await Repository.GetAsync(id))!.State);
            var events = await Repository.GetEventsAfterAsync(id, 0);
            Assert.Single(events, item => item.Type == RunEventTypes.RunCompleted);
            Assert.DoesNotContain(events, item => item.Type == RunEventTypes.RunFailed);
            Assert.DoesNotContain(events, item => item.Type == RunEventTypes.ContextChanged && item.Data.GetProperty("wasCompacted").GetBoolean());
            return id;
        }

        internal async Task<string> StopDuringInferenceAsync(RunRequest request, Task started)
        {
            var id = (await Repository.CreateAsync(request, null)).Snapshot.RunId;
            using var stop = new CancellationTokenSource();
            var processing = _provider.GetRequiredService<RunProcessor>().ProcessAsync(id, stop.Token);
            await started.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(await Repository.CancelAsync(id));
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
            return id;
        }

        public void Dispose() { _provider.Dispose(); _runtime.Dispose(); _http.Dispose(); _database.Dispose(); }
    }

    private sealed record NativeRequest(JsonElement Body, string? SessionKey, int OperationIndex);
    private sealed record Operation(string Path, string? Model, string? SessionKey);

    private sealed class NativeHandler : HttpMessageHandler
    {
        // The native catalog exposes text GGUFs under coding/, including models
        // selected by the General role (BuildRuntimeStatuses publishes both roles).
        internal const string ModelA = "coding/Qwen3-General-Fixture-A~cachetest";
        internal const string ModelB = "coding/Qwen3-General-Fixture-B~cachetest";
        private static readonly string[] ModelIds = [ModelA, ModelB];
        private static readonly string[] ModelTags = ["go-context-train:32768"];
        private string? _loaded = ModelA;
        private readonly Dictionary<string, string?> _sessions = new(StringComparer.Ordinal);
        internal List<NativeRequest> Requests { get; } = [];
        internal List<Operation> Operations { get; } = [];
        internal List<string> Answers { get; } = [];
        internal bool HoldFirstCompletion { get; init; }
        internal bool ReturnToolCall { get; init; }
        internal TaskCompletionSource InferenceStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal static string Reasoning(int ordinal) => $"Ich prüfe den erhaltenen Sitzungskontext im Testschritt {ordinal}.";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/models") return Json(new { data = ModelIds.Select(id =>
                new { id, tags = ModelTags, status = new { value = id == _loaded ? "loaded" : "unloaded" } }) });
            if (path == "/props") return Json(new { default_generation_settings = new { n_ctx = 32768 } });
            if (path == "/v1/chat/completions/input_tokens") return Json(new { input_tokens = 256 });
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var body = document.RootElement;
            var model = body.GetProperty("model").GetString()!;
            var session = body.TryGetProperty("sessionKey", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            Operations.Add(new(path, model, session));
            if (path == "/models/unload") { _loaded = null; return Json(new { success = true }); }
            if (path == "/models/load") { _loaded = model; return Json(new { success = true }); }
            if (path == "/sessions/prepare") { _sessions[model] = session; return Json(new { status = "prepared" }); }
            if (path == "/sessions/save") return Json(new { status = "saved" });
            if (path != "/v1/chat/completions") throw new InvalidOperationException("Unexpected native endpoint: " + path);
            Assert.Equal(_loaded, model);
            Requests.Add(new(body.Clone(), _sessions.GetValueOrDefault(model), Operations.Count - 1));
            if (HoldFirstCompletion && Requests.Count == 1)
            {
                InferenceStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            if (ReturnToolCall)
                return Json(new
                {
                    choices = new[] { new { message = new { role = "assistant", content = (string?)null,
                        tool_calls = new[] { new { id = "read-source", type = "function",
                            function = new { name = ModelRuntimeClient.ToTransportToolName("coding.read"), arguments = "{\"path\":\"source.cs\"}" } } } }, finish_reason = "tool_calls" } },
                    usage = new { prompt_tokens = 256, completion_tokens = 32 },
                });
            var answer = $"Die geprüfte Antwort aus dem bereitgestellten Verlauf ist abgeschlossen (Testschritt {Requests.Count}).";
            Answers.Add(answer);
            return Json(new
            {
                choices = new[] { new { message = new { role = "assistant", content = answer, reasoning_content = Reasoning(Requests.Count) }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 256, completion_tokens = 32 },
            });
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
