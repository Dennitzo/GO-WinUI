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
    public async Task LagunaLoadsExclusivelyAndUnloadsTheGeneralModel()
    {
        using var context = new TestServerContext();
        var handler = new ResidentCoreLoadHandler();
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);

        var instance = await client.EnsureModelLoadedAsync("poolside/laguna-s-2.1", 131_072);

        Assert.Equal("laguna-test", instance);
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

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/api/v1/models")
            {
                return Task.FromResult(JsonResponse("""
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
                          "key": "poolside/laguna-s-2.1",
                          "display_name": "Laguna S 2.1",
                          "loaded_instances": [],
                          "max_context_length": 262144
                        }
                      ]
                    }
                    """));
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/api/v1/models/unload")
            {
                UnloadRequests++;
                return Task.FromResult(JsonResponse("{}"));
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/api/v1/models/load")
            {
                LoadRequests++;
                return Task.FromResult(JsonResponse("""
                    {
                      "instance_id": "laguna-test",
                      "status": "loaded",
                      "load_time_seconds": 1.0,
                      "load_config": {
                        "context_length": 131072,
                        "parallel": 1,
                        "flash_attention": true,
                        "offload_kv_cache_to_gpu": true
                      }
                    }
                    """));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResponse(string value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json"),
        };
    }
}
