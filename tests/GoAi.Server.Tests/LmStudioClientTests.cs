using GoAi.Server.Core.Models;
using GoAi.Server.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class LmStudioClientTests
{
    [Fact]
    public async Task RepeatedAndConcurrentStatusRequestsUseOneProviderCall()
    {
        using var context = new TestServerContext();
        var handler = new ModelStatusHandler();
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);

        var requests = Enumerable.Range(0, 8)
            .Select(_ => client.GetStatusAsync())
            .ToArray();
        var snapshots = await Task.WhenAll(requests);
        var repeated = await client.GetStatusAsync();

        Assert.All(snapshots, static snapshot => Assert.True(snapshot.ProviderReachable));
        Assert.True(repeated.ProviderReachable);
        Assert.Equal(1, handler.ModelRequests);
    }

    [Theory]
    [InlineData("89504E470D0A1A0A00000000", "image/png")]
    [InlineData("FFD8FFE000104A464946", "image/jpeg")]
    [InlineData("524946460400000057454250", "image/webp")]
    public void VisionMediaTypeUsesThePayloadSignatureInsteadOfTheUploadFileExtension(
        string hex,
        string expected)
    {
        Assert.Equal(expected, LmStudioClient.DetectImageMediaType(Convert.FromHexString(hex)));
        Assert.Throws<InvalidDataException>(() => LmStudioClient.DetectImageMediaType([1, 2, 3, 4]));
    }

    [Fact]
    public async Task AgentCompletionUsesResponsesFunctionProtocolAndIgnoresReasoning()
    {
        using var context = new TestServerContext();
        var handler = new RecordingHandler("""
            {
              "id": "resp_test",
              "status": "completed",
              "output": [
                {
                  "id": "reasoning_test",
                  "type": "reasoning",
                  "content": [{ "type": "reasoning_text", "text": "must stay private" }]
                },
                {
                  "id": "message_test",
                  "type": "message",
                  "content": [{ "type": "output_text", "text": "GO_SESSION_TITLE: Test\n\nSichtbare Antwort" }]
                },
                {
                  "id": "function_test",
                  "call_id": "call_next",
                  "type": "function_call",
                  "name": "math.evaluate",
                  "arguments": "{\"operation\":\"add\",\"left\":[2],\"right\":[3]}"
                }
              ],
              "usage": { "input_tokens": 120, "output_tokens": 24 }
            }
            """);
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);
        using var schemaDocument = JsonDocument.Parse("""
            {"type":"object","properties":{"operation":{"type":"string"}},"required":["operation"],"additionalProperties":false}
            """);
        using var previousArguments = JsonDocument.Parse("""{"operation":"add","left":[1],"right":[1]}""");

        var result = await client.CompleteChatAsync(
            "openai/gpt-oss-20b",
            [
                new LmChatMessage("system", "Systemregeln"),
                new LmChatMessage("user", "Bitte rechnen"),
                new LmChatMessage(
                    "assistant",
                    ToolCalls: [new LmToolCall("call_previous", "math.evaluate", previousArguments.RootElement.Clone())]),
                new LmChatMessage("tool", "{\"result\":[2]}", ToolCallId: "call_previous"),
            ],
            [new LmToolDefinition("math.evaluate", "Rechnet", schemaDocument.RootElement.Clone())]);

        Assert.Equal("GO_SESSION_TITLE: Test\n\nSichtbare Antwort", result.Content);
        Assert.DoesNotContain("must stay private", result.Content, StringComparison.Ordinal);
        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("call_next", call.Id);
        Assert.Equal("math.evaluate", call.Name);
        Assert.Equal(120, result.InputTokens);
        Assert.Equal(24, result.OutputTokens);

        using var request = JsonDocument.Parse(Assert.IsType<string>(handler.RequestBody));
        var root = request.RootElement;
        Assert.Equal("/v1/responses", handler.RequestPath);
        Assert.False(root.TryGetProperty("messages", out _));
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.Equal(8192, root.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal(0.2, root.GetProperty("temperature").GetDouble(), 3);
        Assert.True(root.TryGetProperty("reasoning", out _));
        Assert.Equal("math.evaluate", root.GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.False(root.GetProperty("parallel_tool_calls").GetBoolean());
        var input = root.GetProperty("input");
        Assert.Contains(input.EnumerateArray(), static item =>
            item.TryGetProperty("type", out var type) && type.GetString() == "function_call");
        Assert.Contains(input.EnumerateArray(), static item =>
            item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output");
    }

    [Fact]
    public async Task EmbeddingLoadOmitsUnsupportedLlmConfiguration()
    {
        using var context = new TestServerContext();
        var handler = new EmbeddingLoadHandler();
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);

        var instance = await client.EnsureModelLoadedAsync("text-embedding-bge-m3", 8192);

        Assert.Equal("embedding-test", instance);
        using var request = JsonDocument.Parse(Assert.IsType<string>(handler.LoadRequestBody));
        Assert.Equal("text-embedding-bge-m3", request.RootElement.GetProperty("model").GetString());
        Assert.Equal(8192, request.RootElement.GetProperty("context_length").GetInt32());
        Assert.False(request.RootElement.TryGetProperty("ttl", out _));
        Assert.False(request.RootElement.TryGetProperty("parallel", out _));
        Assert.False(request.RootElement.TryGetProperty("flash_attention", out _));
        Assert.False(request.RootElement.TryGetProperty("offload_kv_cache_to_gpu", out _));
    }

    [Fact]
    public async Task ModelLoadRetriesOneTransientFailureWithoutChangingTheModel()
    {
        using var context = new TestServerContext();
        var handler = new EmbeddingLoadHandler(failuresBeforeSuccess: 1);
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);

        var instance = await client.EnsureModelLoadedAsync("text-embedding-bge-m3", 8192);

        Assert.Equal("embedding-test", instance);
        Assert.Equal(2, handler.LoadRequests);
        using var request = JsonDocument.Parse(Assert.IsType<string>(handler.LoadRequestBody));
        Assert.Equal("text-embedding-bge-m3", request.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task QwenCoderLoadsExclusivelyAndUnloadsTheGeneralModel()
    {
        using var context = new TestServerContext();
        var handler = new ResidentCoreLoadHandler();
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);

        var instance = await client.EnsureModelLoadedAsync("qwen3-coder-next", 262_144);

        Assert.Equal("qwen-coder-test", instance);
        Assert.Equal(1, handler.UnloadRequests);
        Assert.Equal(1, handler.LoadRequests);
    }

    [Fact]
    public async Task QwenCoderUsesPublishedNonThinkingSamplingProfile()
    {
        using var context = new TestServerContext();
        var handler = new RecordingHandler("""
            {
              "status": "completed",
              "output": [{
                "type": "message",
                "content": [{ "type": "output_text", "text": "GO_SESSION_TITLE: Fertig\n\nErledigt" }]
              }],
              "usage": { "input_tokens": 20, "output_tokens": 4 }
            }
            """);
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);

        _ = await client.CompleteChatAsync(
            "qwen3-coder-next",
            [new LmChatMessage("user", "Ändere die Datei und teste sie.")],
            []);

        using var request = JsonDocument.Parse(Assert.IsType<string>(handler.RequestBody));
        var root = request.RootElement;
        Assert.Equal(1.0, root.GetProperty("temperature").GetDouble(), 3);
        Assert.Equal(0.95, root.GetProperty("top_p").GetDouble(), 3);
        Assert.Equal(40, root.GetProperty("top_k").GetInt32());
        Assert.False(root.TryGetProperty("reasoning", out _));
    }

    [Fact]
    public async Task DeepSeekCoderUsesItsPublishedAgentSamplingProfile()
    {
        using var context = new TestServerContext();
        var handler = new RecordingHandler("""
            {
              "status": "completed",
              "output": [{
                "type": "message",
                "content": [{ "type": "output_text", "text": "Erledigt" }]
              }],
              "usage": { "input_tokens": 20, "output_tokens": 4 }
            }
            """);
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);

        _ = await client.CompleteChatAsync(
            "ud",
            [new LmChatMessage("user", "Analysiere und ändere das Projekt.")],
            []);

        using var request = JsonDocument.Parse(Assert.IsType<string>(handler.RequestBody));
        var root = request.RootElement;
        Assert.Equal(1.0, root.GetProperty("temperature").GetDouble(), 3);
        Assert.Equal(0.95, root.GetProperty("top_p").GetDouble(), 3);
        Assert.False(root.TryGetProperty("top_k", out _));
        Assert.False(root.TryGetProperty("reasoning", out _));
    }

    [Fact]
    public async Task VisionLoadEjectsTheGeneralModelForTheOptionalRun()
    {
        using var context = new TestServerContext();
        var handler = new ResidentCoreLoadHandler();
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);

        _ = await client.EnsureModelLoadedAsync("qwen3-vl-30b-a3b-instruct", 65_536);

        Assert.Equal(1, handler.UnloadRequests);
        Assert.Equal(1, handler.LoadRequests);
    }

    [Fact]
    public async Task ResponsesRetriesOneTransientFailureWithTheSameModel()
    {
        using var context = new TestServerContext();
        var handler = new RecordingHandler("""
            {
              "status": "completed",
              "output": [{
                "type": "message",
                "content": [{ "type": "output_text", "text": "Erfolgreich" }]
              }],
              "usage": { "input_tokens": 2, "output_tokens": 1 }
            }
            """, failuresBeforeSuccess: 1);
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);

        var result = await client.CompleteChatAsync(
            "openai/gpt-oss-20b",
            [new LmChatMessage("user", "Test")],
            []);

        Assert.Equal("Erfolgreich", result.Content);
        Assert.Equal(2, handler.RequestCount);
        using var request = JsonDocument.Parse(Assert.IsType<string>(handler.RequestBody));
        Assert.Equal("openai/gpt-oss-20b", request.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task GptOssResponsesTolerateThreeTransientHarmonyFailures()
    {
        using var context = new TestServerContext();
        var handler = new RecordingHandler("""
            {
              "status": "completed",
              "output": [{
                "type": "message",
                "content": [{ "type": "output_text", "text": "Erfolgreich" }]
              }],
              "usage": { "input_tokens": 2, "output_tokens": 1 }
            }
            """, failuresBeforeSuccess: 3);
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);

        var result = await client.CompleteChatAsync(
            "openai/gpt-oss-20b",
            [new LmChatMessage("user", "Test")],
            []);

        Assert.Equal("Erfolgreich", result.Content);
        Assert.Equal(4, handler.RequestCount);
    }

    [Fact]
    public async Task PegNativeFailureUsesChatCompletionsWithTheSameModelAndTypedTools()
    {
        using var context = new TestServerContext();
        var handler = new ResponsesCompatibilityHandler();
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);
        using var schemaDocument = JsonDocument.Parse("""
            {"type":"object","properties":{"operation":{"type":"string"}},"required":["operation"],"additionalProperties":false}
            """);
        using var previousArguments = JsonDocument.Parse("""{"operation":"add"}""");

        var result = await client.CompleteChatAsync(
            "openai/gpt-oss-20b",
            [
                new LmChatMessage("system", "Systemregeln"),
                new LmChatMessage("user", "Bitte rechnen"),
                new LmChatMessage(
                    "assistant",
                    ToolCalls: [new LmToolCall("call_previous", "math.evaluate", previousArguments.RootElement.Clone())]),
                new LmChatMessage("tool", "{\"result\":[2]}", ToolCallId: "call_previous"),
            ],
            [new LmToolDefinition("math.evaluate", "Rechnet", schemaDocument.RootElement.Clone())]);

        Assert.Equal("Kompatible Antwort", result.Content);
        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("call_next", call.Id);
        Assert.Equal("math.evaluate", call.Name);
        Assert.Equal(9, result.InputTokens);
        Assert.Equal(3, result.OutputTokens);
        Assert.Equal(["/v1/responses", "/v1/chat/completions"], handler.RequestPaths);

        using var request = JsonDocument.Parse(handler.RequestBodies[1]);
        var root = request.RootElement;
        Assert.Equal("openai/gpt-oss-20b", root.GetProperty("model").GetString());
        Assert.Equal("math.evaluate", root.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.False(root.GetProperty("parallel_tool_calls").GetBoolean());
        var messages = root.GetProperty("messages");
        Assert.Contains(messages.EnumerateArray(), static item =>
            item.GetProperty("role").GetString() == "assistant" && item.TryGetProperty("tool_calls", out _));
        Assert.Contains(messages.EnumerateArray(), static item =>
            item.GetProperty("role").GetString() == "tool" && item.GetProperty("tool_call_id").GetString() == "call_previous");
    }

    private sealed class RecordingHandler(string responseJson, int failuresBeforeSuccess = 0) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        public string? RequestBody { get; private set; }

        public string? RequestPath { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            RequestPath = request.RequestUri?.AbsolutePath;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            if (RequestCount <= failuresBeforeSuccess)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class ResponsesCompatibilityHandler : HttpMessageHandler
    {
        public List<string> RequestPaths { get; } = [];

        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestPaths.Add(request.RequestUri?.AbsolutePath ?? string.Empty);
            RequestBodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            if (request.RequestUri?.AbsolutePath == "/v1/responses")
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent(
                        "{\"error\":{\"message\":\"The model produced output that does not match the expected peg-native format\"}}",
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            if (request.RequestUri?.AbsolutePath == "/v1/chat/completions")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        {
                          "choices": [{
                            "message": {
                              "role": "assistant",
                              "content": "Kompatible Antwort",
                              "tool_calls": [{
                                "id": "call_next",
                                "type": "function",
                                "function": {
                                  "name": "math.evaluate",
                                  "arguments": "{\"operation\":\"add\"}"
                                }
                              }]
                            }
                          }],
                          "usage": { "prompt_tokens": 9, "completion_tokens": 3 }
                        }
                        """, Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class ModelStatusHandler : HttpMessageHandler
    {
        private int _modelRequests;

        public int ModelRequests => Volatile.Read(ref _modelRequests);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get || request.RequestUri?.AbsolutePath != "/api/v1/models")
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            Interlocked.Increment(ref _modelRequests);
            await Task.Delay(25, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"models\":[]}",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private sealed class EmbeddingLoadHandler(int failuresBeforeSuccess = 0) : HttpMessageHandler
    {
        public int LoadRequests { get; private set; }

        public string? LoadRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/api/v1/models")
            {
                return JsonResponse("""
                    {
                      "models": [{
                        "type": "embedding",
                        "key": "text-embedding-bge-m3",
                        "display_name": "BGE M3",
                        "loaded_instances": [],
                        "max_context_length": 8192
                      }]
                    }
                    """);
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/api/v1/models/load")
            {
                LoadRequests++;
                LoadRequestBody = request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                if (LoadRequests <= failuresBeforeSuccess)
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }
                return JsonResponse("""
                    {
                      "instance_id": "embedding-test",
                      "status": "loaded",
                      "load_time_seconds": 0.5,
                      "load_config": { "context_length": 8192 }
                    }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage JsonResponse(string value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class ResidentCoreLoadHandler : HttpMessageHandler
    {
        public int LoadRequests { get; private set; }

        public int UnloadRequests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/api/v1/models")
            {
                return JsonResponse("""
                    {
                      "models": [
                        {
                          "type": "llm",
                          "key": "openai/gpt-oss-20b",
                          "display_name": "GPT-OSS 20B",
                          "loaded_instances": [{
                            "id": "general-resident",
                            "model_instance_id": "general-resident",
                            "config": {
                              "context_length": 131072,
                              "parallel": 1,
                              "flash_attention": true,
                              "offload_kv_cache_to_gpu": true
                            }
                          }],
                          "max_context_length": 131072
                        },
                        {
                          "type": "llm",
                          "key": "qwen3-coder-next",
                          "display_name": "Qwen3 Coder Next Q6_K",
                          "loaded_instances": [],
                          "max_context_length": 262144
                        },
                        {
                          "type": "llm",
                          "key": "qwen3-vl-30b-a3b-instruct",
                          "display_name": "Qwen3 VL 30B",
                          "loaded_instances": [],
                          "max_context_length": 65536
                        }
                      ]
                    }
                    """);
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/api/v1/models/unload")
            {
                UnloadRequests++;
                return JsonResponse("{}");
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/api/v1/models/load")
            {
                LoadRequests++;
                var requestBody = request.Content is null
                    ? "{}"
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                using var requestDocument = JsonDocument.Parse(requestBody);
                var modelId = requestDocument.RootElement.GetProperty("model").GetString();
                var contextLength = requestDocument.RootElement.GetProperty("context_length").GetInt32();
                var instanceId = string.Equals(modelId, "qwen3-coder-next", StringComparison.OrdinalIgnoreCase)
                    ? "qwen-coder-test"
                    : "vision-test";
                return JsonResponse($$"""
                    {
                      "instance_id": "{{instanceId}}",
                      "status": "loaded",
                      "load_time_seconds": 1.0,
                      "load_config": {
                        "context_length": {{contextLength}},
                        "parallel": 1,
                        "flash_attention": true,
                        "offload_kv_cache_to_gpu": true
                      }
                    }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage JsonResponse(string value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json"),
        };
    }
}
