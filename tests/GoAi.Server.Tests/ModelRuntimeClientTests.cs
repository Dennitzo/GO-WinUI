using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class ModelRuntimeClientTests
{
    private static readonly string[] RequiredPath = ["path"];

    [Fact]
    public async Task CodingTurnUsesNonStreamingSingleToolLlamaRequest()
    {
        var handler = new RouterHandler();
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { path = new { type = "string" } },
            required = RequiredPath,
        });

        var result = await client.CompleteChatAsync(
            "gpt-oss-120b",
            [new LmChatMessage("user", "Lies README.md")],
            [new LmToolDefinition("fs.readText", "Datei lesen", schema)],
            modelRole: "code",
            reasoningEffort: "high",
            requireToolCall: true,
            requiredToolName: "fs.readText");

        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("fs.readText", call.Name);
        Assert.Equal("README.md", call.Arguments.GetProperty("path").GetString());
        using var body = JsonDocument.Parse(Assert.Single(handler.ChatBodies));
        Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(body.RootElement.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.Equal("high", body.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Equal("required", body.RootElement.GetProperty("tool_choice").GetString());
        Assert.Empty(handler.ModelOperations);
    }

    [Fact]
    public async Task GeneralAndCodingRolesReuseTheSameLoadedModel()
    {
        var handler = new RouterHandler(returnToolCall: false);
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);

        _ = await client.CompleteChatAsync(
            "gpt-oss-120b",
            [new LmChatMessage("user", "Erkläre kurz.")],
            [],
            modelRole: "general");
        _ = await client.CompleteChatAsync(
            "gpt-oss-120b",
            [new LmChatMessage("user", "Prüfe Code.")],
            [],
            modelRole: "code");

        Assert.Equal(2, handler.ChatBodies.Count);
        Assert.Empty(handler.ModelOperations);
    }

    [Fact]
    public async Task QwenCodingTurnSwitchesModelsAndNeverSendsReasoningEffort()
    {
        var handler = new RouterHandler(returnToolCall: false);
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);

        _ = await client.CompleteChatAsync(
            CodingModelCatalog.Qwen3CoderNextQ8Id,
            [new LmChatMessage("user", "PrÃ¼fe das Projekt.")],
            [],
            modelRole: "code",
            reasoningEffort: "high");

        Assert.Equal(2, handler.ModelOperations.Count);
        Assert.StartsWith("/models/unload:", handler.ModelOperations[0], StringComparison.Ordinal);
        Assert.Contains("gpt-oss-120b", handler.ModelOperations[0], StringComparison.Ordinal);
        Assert.StartsWith("/models/load:", handler.ModelOperations[1], StringComparison.Ordinal);
        Assert.Contains(CodingModelCatalog.Qwen3CoderNextQ8Id, handler.ModelOperations[1], StringComparison.Ordinal);
        using var body = JsonDocument.Parse(Assert.Single(handler.ChatBodies));
        Assert.Equal(CodingModelCatalog.Qwen3CoderNextQ8Id, body.RootElement.GetProperty("model").GetString());
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
        Assert.Equal(1.0, body.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(0.95, body.RootElement.GetProperty("top_p").GetDouble());
        Assert.Equal(40, body.RootElement.GetProperty("top_k").GetInt32());
        Assert.Equal(0.0, body.RootElement.GetProperty("min_p").GetDouble());
    }

    [Fact]
    public async Task QwenSwitchCancelsAStillLoadingGeneralModelBeforeClaimingTheOnlyRouterSlot()
    {
        var handler = new LoadingModelSwitchHandler();
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);

        var result = await client.CompleteChatAsync(
            CodingModelCatalog.Qwen3CoderNextQ8Id,
            [new LmChatMessage("user", "Pruefe das Projekt.")],
            [],
            modelRole: "code");

        Assert.Equal("Fertig", result.Content);
        Assert.Equal(
            ["/models/unload:gpt-oss-120b", "/models/load:qwen3-coder-next-q8_0"],
            handler.ModelOperations);
        Assert.True(handler.ModelStatusRequests >= 5);
    }

    [Fact]
    public async Task StartupPreloadIsAwaitedWithoutIssuingASecondLoadRequest()
    {
        var handler = new RouterHandler(
            returnToolCall: false,
            modelStates: ["loading", "loaded"]);
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        var loadingWasReported = false;

        var preparation = await client.EnsureModelPreparedAsync(
            "gpt-oss-120b",
            131_072,
            _ =>
            {
                loadingWasReported = true;
                return Task.CompletedTask;
            });

        Assert.False(preparation.WasAlreadyLoaded);
        Assert.True(loadingWasReported);
        Assert.Empty(handler.ModelOperations);
        Assert.True(handler.ModelStatusRequests >= 2);
    }

    [Fact]
    public async Task TransientInferenceFailureIsRetriedExactlyOnce()
    {
        var handler = new RouterHandler(returnToolCall: false, transientChatFailures: 1);
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);

        var result = await client.CompleteChatAsync(
            "gpt-oss-120b",
            [new LmChatMessage("user", "Test")],
            []);

        Assert.Equal("Fertig", result.Content);
        Assert.Equal(2, handler.ChatAttempts);
    }

    [Fact]
    public async Task NonStreamingTurnReportsLiveLlamaSlotTokenProgress()
    {
        var handler = new RouterHandler(returnToolCall: false, chatDelayMilliseconds: 1_500);
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        var progress = new List<ModelRuntimeProgress>();

        _ = await client.CompleteChatAsync(
            "gpt-oss-120b",
            [new LmChatMessage("user", "Test")],
            [],
            nativeProgress: (value, _) =>
            {
                progress.Add(value);
                return ValueTask.CompletedTask;
            });

        var tokens = Assert.Single(progress, item => item.State == "tokenProgress");
        Assert.Equal(1_205, tokens.PromptTokens);
        Assert.Equal(1_103, tokens.ProcessedPromptTokens);
        Assert.Equal(83, tokens.GeneratedTokens);
        Assert.Equal(1_186, tokens.CurrentTokens);
        Assert.InRange(tokens.PromptProgress!.Value, 0.91, 0.92);
        Assert.True(handler.SlotRequests >= 1);
    }

    [Fact]
    public void ActiveSlotParserIgnoresIdleSlotsAndReadsCurrentTokenCounters()
    {
        using var document = JsonDocument.Parse("""
            [
              {"id":0,"is_processing":false,"n_prompt_tokens":0,"n_prompt_tokens_processed":0},
              {"id":1,"is_processing":true,"n_prompt_tokens":927,"n_prompt_tokens_processed":250,
               "n_prompt_tokens_cache":200,
               "next_token":[{"n_decoded":27}]}
            ]
            """);

        var progress = ModelRuntimeClient.ReadActiveSlotProgress(document.RootElement);

        Assert.NotNull(progress);
        Assert.Equal("tokenProgress", progress.State);
        Assert.Equal(900, progress.PromptTokens);
        Assert.Equal(450, progress.ProcessedPromptTokens);
        Assert.Equal(27, progress.GeneratedTokens);
        Assert.Equal(477, progress.CurrentTokens);
        Assert.Equal(0.5, progress.PromptProgress);
    }

    [Fact]
    public void ActiveSlotParserPrefersTheNativeLlamaCurrentTokenCounter()
    {
        using var document = JsonDocument.Parse("""
            [
              {"id":0,"is_processing":true,"n_tokens":13331,"n_prompt_tokens":16000,
               "n_prompt_tokens_processed":12000,"next_token":[{"n_decoded":0}]}
            ]
            """);

        var progress = ModelRuntimeClient.ReadActiveSlotProgress(document.RootElement);

        Assert.NotNull(progress);
        Assert.Equal(13_331, progress.CurrentTokens);
        Assert.Equal(0, progress.GeneratedTokens);
    }

    private static ModelRuntimeClient CreateClient(HttpClient http) => new(
        http,
        Options.Create(new GoAiServerOptions
        {
            ModelRuntimeUri = new Uri("http://llm.test:8080", UriKind.Absolute),
            GeneralModelId = "gpt-oss-120b",
            CodeModelId = CodingModelCatalog.DefaultModelId,
        }),
        NullLogger<ModelRuntimeClient>.Instance);

    private sealed class RouterHandler(
        bool returnToolCall = true,
        int transientChatFailures = 0,
        int chatDelayMilliseconds = 0,
        IReadOnlyList<string>? modelStates = null,
        string initiallyLoadedModelId = "gpt-oss-120b") : HttpMessageHandler
    {
        private string? _loadedModelId = initiallyLoadedModelId;
        public List<string> ChatBodies { get; } = [];
        public List<string> ModelOperations { get; } = [];
        public int ChatAttempts { get; private set; }
        public int SlotRequests { get; private set; }
        public int ModelStatusRequests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (request.Method == HttpMethod.Get && path == "/models")
            {
                var stateIndex = Math.Min(ModelStatusRequests, Math.Max(0, (modelStates?.Count ?? 1) - 1));
                var state = modelStates is { Count: > 0 }
                    ? modelStates[stateIndex]
                    : string.Equals(_loadedModelId, "gpt-oss-120b", StringComparison.OrdinalIgnoreCase)
                        ? "loaded"
                        : "unloaded";
                var qwenState = string.Equals(
                    _loadedModelId,
                    CodingModelCatalog.Qwen3CoderNextQ8Id,
                    StringComparison.OrdinalIgnoreCase)
                        ? "loaded"
                        : "unloaded";
                ModelStatusRequests++;
                return Json(JsonSerializer.Serialize(new
                {
                    data = new object[]
                    {
                        new { id = "gpt-oss-120b", status = new { value = state } },
                        new { id = CodingModelCatalog.Qwen3CoderNextQ8Id, status = new { value = qwenState } },
                        new { id = "qwen3-vl-30b-a3b-instruct", status = new { value = "unloaded" } },
                        new { id = "text-embedding-bge-m3", status = new { value = "unloaded" } },
                    },
                }));
            }
            if (request.Method == HttpMethod.Get && path == "/slots")
            {
                SlotRequests++;
                return Json("""
                    [{"id":0,"is_processing":true,"n_prompt_tokens":1288,"n_prompt_tokens_processed":1103,
                      "next_token":[{"n_decoded":83}]}]
                    """);
            }

            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            if (path is "/models/load" or "/models/unload")
            {
                ModelOperations.Add(path + ":" + body);
                using var operation = JsonDocument.Parse(body);
                var modelId = operation.RootElement.GetProperty("model").GetString();
                if (path == "/models/load")
                {
                    _loadedModelId = modelId;
                }
                else if (string.Equals(_loadedModelId, modelId, StringComparison.OrdinalIgnoreCase))
                {
                    _loadedModelId = null;
                }
                return Json("""{"success":true}""");
            }
            if (path == "/v1/chat/completions")
            {
                ChatAttempts++;
                if (chatDelayMilliseconds > 0)
                {
                    await Task.Delay(chatDelayMilliseconds, cancellationToken);
                }
                if (ChatAttempts <= transientChatFailures)
                {
                    throw new HttpRequestException("transient");
                }
                ChatBodies.Add(body);
                return returnToolCall
                    ? Json("""
                        {"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call-1","type":"function","function":{"name":"fs.readText","arguments":"{\"path\":\"README.md\"}"}}]}}],"usage":{"prompt_tokens":100,"completion_tokens":20}}
                        """)
                    : Json("""
                        {"choices":[{"message":{"role":"assistant","content":"Fertig"}}],"usage":{"prompt_tokens":10,"completion_tokens":2}}
                        """);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class LoadingModelSwitchHandler : HttpMessageHandler
    {
        private bool _generalUnloadRequested;
        private bool _qwenLoadRequested;
        private int _generalDrainPollsRemaining = 1;
        private int _qwenLoadPollsRemaining = 1;

        public List<string> ModelOperations { get; } = [];
        public int ModelStatusRequests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (request.Method == HttpMethod.Get && path == "/models")
            {
                ModelStatusRequests++;
                var generalState = !_generalUnloadRequested
                    ? "loading"
                    : _generalDrainPollsRemaining-- > 0
                        ? "loading"
                        : "unloaded";
                var qwenState = !_qwenLoadRequested
                    ? "unloaded"
                    : _qwenLoadPollsRemaining-- > 0
                        ? "loading"
                        : "loaded";
                return Json(JsonSerializer.Serialize(new
                {
                    data = new object[]
                    {
                        new { id = "gpt-oss-120b", status = new { value = generalState } },
                        new { id = CodingModelCatalog.Qwen3CoderNextQ8Id, status = new { value = qwenState } },
                        new { id = "qwen3-vl-30b-a3b-instruct", status = new { value = "unloaded" } },
                        new { id = "text-embedding-bge-m3", status = new { value = "unloaded" } },
                    },
                }));
            }

            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            if (path is "/models/load" or "/models/unload")
            {
                using var operation = JsonDocument.Parse(body);
                var modelId = operation.RootElement.GetProperty("model").GetString() ?? string.Empty;
                ModelOperations.Add(path + ":" + modelId);
                if (path == "/models/unload")
                {
                    _generalUnloadRequested = true;
                    return Json("""{"success":true}""");
                }

                if (_generalDrainPollsRemaining >= 0)
                {
                    return Json(
                        """{"error":{"code":500,"message":"model limit reached","type":"server_error"}}""",
                        HttpStatusCode.InternalServerError);
                }
                _qwenLoadRequested = true;
                return Json("""{"success":true}""");
            }

            if (path == "/v1/chat/completions")
            {
                return Json("""
                    {"choices":[{"message":{"role":"assistant","content":"Fertig"}}],"usage":{"prompt_tokens":10,"completion_tokens":2}}
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(
            string json,
            HttpStatusCode statusCode = HttpStatusCode.OK) => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }
}
