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
    public async Task ProductionModelOperationsUseNativeSdkWithoutHttpRequests()
    {
        using var context = new TestServerContext();
        var native = new RecordingNativeAgentClient
        {
            Models = new LmStudioModelList(
            [
                new LmStudioModel(
                    "llm",
                    "qwen3-coder-next",
                    "Qwen3 Coder Next",
                    [],
                    262_144,
                    new LmStudioCapabilities(false, true)),
            ]),
            LoadResult = new LmStudioModelPreparation("qwen-sdk", WasAlreadyLoaded: false),
        };
        using var client = new LmStudioClient(
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance,
            native);

        var status = await client.GetStatusAsync();
        var instance = await client.EnsureModelLoadedAsync("qwen3-coder-next", 32_768);

        Assert.True(status.ProviderReachable);
        Assert.Equal("qwen-sdk", instance);
        Assert.Equal(2, native.ModelCatalogRequests);
        Assert.Equal(1, native.LoadRequests);
    }

    [Fact]
    public async Task ProductionEmbeddingAndVisionOperationsUseNativeSdkWithoutHttpRequests()
    {
        using var context = new TestServerContext();
        var native = new RecordingNativeAgentClient
        {
            EmbeddingResult = [[0.25, 0.75]],
            VisionResult = "SDK-Bildanalyse",
        };
        using var client = new LmStudioClient(
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance,
            native);
        Directory.CreateDirectory(context.Root);
        var imagePath = Path.Combine(context.Root, "sdk-vision.png");
        await File.WriteAllBytesAsync(
            imagePath,
            Convert.FromHexString("89504E470D0A1A0A00000000"));

        var embeddings = await client.CreateEmbeddingsAsync("text-embedding-bge-m3", ["Test"]);
        var vision = await client.AnalyzeImagesAsync("vision-model", "Analysiere.", [imagePath]);

        Assert.Equal(0.25, embeddings[0][0]);
        Assert.Equal("SDK-Bildanalyse", vision);
        Assert.Equal(1, native.EmbeddingRequests);
        Assert.Equal(1, native.VisionRequests);
    }

    [Fact]
    public async Task ProductionGeneralPathUsesNativeSdkWithoutAnyHttpCompletionRequest()
    {
        using var context = new TestServerContext();
        var native = new RecordingNativeAgentClient(static _ =>
            new LmChatResult("Native SDK Antwort", [], 17, 4));
        using var client = new LmStudioClient(
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance,
            native);

        var result = await client.CompleteChatAsync(
            "gpt-oss-120b",
            [new LmChatMessage("user", "Erkläre Volumenstrom.")],
            [],
            modelRole: "general");

        Assert.Equal("Native SDK Antwort", result.Content);
        var request = Assert.Single(native.Requests);
        Assert.Equal("gpt-oss-120b", request.ModelId);
        Assert.False(request.RequireToolCall);
        Assert.Empty(request.Tools);
    }

    [Fact]
    public async Task GeneralResearchToolUsesOneNamedSchemaAndOneNativeCorrectionAttempt()
    {
        using var context = new TestServerContext();
        var native = new RecordingNativeAgentClient(
            static _ => new LmChatResult("kein Tool", [], 10, 3),
            static _ =>
            {
                using var arguments = JsonDocument.Parse(
                    """{"query":"offizielle WebView2 API","maximumResults":8,"language":"de-DE"}""");
                return new LmChatResult(
                    null,
                    [new LmToolCall("call_search", "web.search", arguments.RootElement.Clone())],
                    12,
                    5);
            });
        using var client = new LmStudioClient(
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance,
            native);
        using var schema = JsonDocument.Parse(
            """{"type":"object","properties":{"query":{"type":"string"},"maximumResults":{"type":"integer"},"language":{"type":"string"}},"required":["query"],"additionalProperties":false}""");

        var result = await client.CompleteChatAsync(
            "gpt-oss-120b",
            [new LmChatMessage("user", "Suche die offizielle API.")],
            [new LmToolDefinition("web.search", "SearXNG", schema.RootElement.Clone())],
            modelRole: "general",
            requireToolCall: true,
            requiredToolName: "web.search");

        Assert.Equal(2, native.Requests.Count);
        Assert.All(native.Requests, static request =>
        {
            Assert.True(request.RequireToolCall);
            Assert.Equal("web.search", request.RequiredToolName);
            Assert.Equal("web.search", Assert.Single(request.Tools).Name);
        });
        Assert.Contains(
            native.Requests[1].Messages,
            static message => message.Content?.Contains("[GO_NATIVE_TOOL_RETRY]", StringComparison.Ordinal) == true);
        Assert.Equal(22, result.InputTokens);
        Assert.Equal(8, result.OutputTokens);
        Assert.Equal("web.search", Assert.Single(result.ToolCalls).Name);
    }

    [Fact]
    public async Task ProductionCodingPathUsesNativeSdkWithoutAnyHttpCompletionRequest()
    {
        using var context = new TestServerContext();
        var native = new RecordingNativeAgentClient(static request =>
        {
            using var arguments = JsonDocument.Parse("{}");
            return new LmChatResult(
                null,
                [new LmToolCall("call_map", "workspace.map", arguments.RootElement.Clone())],
                31,
                7);
        });
        using var client = new LmStudioClient(
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance,
            native);
        using var schema = JsonDocument.Parse(
            """{"type":"object","properties":{},"additionalProperties":false}""");
        var progress = new List<LmStudioNativeAgentProgress>();

        var result = await client.CompleteChatAsync(
            "qwen3-coder-next",
            [new LmChatMessage("user", "Kartiere den Workspace.")],
            [new LmToolDefinition("workspace.map", "Workspacekarte", schema.RootElement.Clone())],
            modelRole: "code",
            requireToolCall: true,
            requiredToolName: "workspace.map",
            nativeProgress: (item, _) =>
            {
                progress.Add(item);
                return ValueTask.CompletedTask;
            });

        Assert.Equal("workspace.map", Assert.Single(result.ToolCalls).Name);
        var request = Assert.Single(native.Requests);
        Assert.True(request.RequireToolCall);
        Assert.Equal("workspace.map", request.RequiredToolName);
        Assert.Contains(progress, static item => item.State == "toolCallGenerationNameReceived");
    }

    [Fact]
    public async Task InvalidNativeToolCallIsRetriedOnlyThroughTheNativeSdk()
    {
        using var context = new TestServerContext();
        var native = new RecordingNativeAgentClient(
            static request =>
            {
                using var arguments = JsonDocument.Parse("{}");
                return new LmChatResult(
                    null,
                    [new LmToolCall("call_bad", "unknown.tool", arguments.RootElement.Clone())],
                    20,
                    5);
            },
            static request =>
            {
                using var arguments = JsonDocument.Parse("{}");
                return new LmChatResult(
                    null,
                    [new LmToolCall("call_map", "workspace.map", arguments.RootElement.Clone())],
                    22,
                    6);
            });
        using var client = new LmStudioClient(
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance,
            native);
        using var schema = JsonDocument.Parse(
            """{"type":"object","properties":{},"additionalProperties":false}""");

        var result = await client.CompleteChatAsync(
            "qwen3-coder-next",
            [new LmChatMessage("user", "Kartiere den Workspace.")],
            [new LmToolDefinition("workspace.map", "Workspacekarte", schema.RootElement.Clone())],
            modelRole: "code",
            requireToolCall: true,
            requiredToolName: "workspace.map");

        Assert.Equal(2, native.Requests.Count);
        Assert.Equal(42, result.InputTokens);
        Assert.Equal(11, result.OutputTokens);
        Assert.Contains(
            native.Requests[1].Messages,
            static message => message.Content?.Contains("[GO_NATIVE_TOOL_RETRY]", StringComparison.Ordinal) == true);
        Assert.Equal("workspace.map", Assert.Single(result.ToolCalls).Name);
    }

    [Fact]
    public async Task InterruptedNativeArgumentsRetryOnlyTheAlreadySelectedToolSchema()
    {
        using var context = new TestServerContext();
        var native = new RecordingNativeAgentClient(
            static _ => throw new LmStudioNativeAgentException(
                "tool_generation_timeout",
                "Argument generation stopped.",
                toolName: "fs.readText"),
            static request =>
            {
                Assert.Equal("fs.readText", request.RequiredToolName);
                using var arguments = JsonDocument.Parse("""{"path":"README.md"}""");
                return new LmChatResult(
                    null,
                    [new LmToolCall("call_read", "fs.readText", arguments.RootElement.Clone())],
                    24,
                    8);
            });
        using var client = new LmStudioClient(
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance,
            native);
        using var mapSchema = JsonDocument.Parse(
            """{"type":"object","properties":{},"additionalProperties":false}""");
        using var readSchema = JsonDocument.Parse(
            """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}""");

        var result = await client.CompleteChatAsync(
            "qwen3-coder-next",
            [new LmChatMessage("user", "Lies die Projektbeschreibung.")],
            [
                new LmToolDefinition(
                    "workspace.map",
                    "Workspacekarte",
                    mapSchema.RootElement.Clone()),
                new LmToolDefinition(
                    "fs.readText",
                    "Textdatei lesen",
                    readSchema.RootElement.Clone()),
            ],
            modelRole: "code",
            requireToolCall: true);

        Assert.Equal(2, native.Requests.Count);
        Assert.Null(native.Requests[0].RequiredToolName);
        Assert.Equal("fs.readText", native.Requests[1].RequiredToolName);
        Assert.Equal("README.md", Assert.Single(result.ToolCalls).Arguments.GetProperty("path").GetString());
    }

    [Fact]
    public void ClientUsesTheRunCancellationInsteadOfAShortHttpTimeout()
    {
        using var context = new TestServerContext();
        using var http = new HttpClient(new ModelStatusHandler());
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);

        Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);
    }

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

    [Fact]
    public async Task GptOssCanBeAdvertisedAsGeneralAndCodingModelAtTheSameTime()
    {
        using var context = new TestServerContext();
        context.Options.GeneralModelId = "openai/gpt-oss-120b";
        context.Options.GeneralContextLength = 131_072;
        var handler = new ModelStatusHandler("""
            {
              "models": [{
                "type": "llm",
                "key": "openai/gpt-oss-120b",
                "display_name": "gpt-oss-120b",
                "loaded_instances": [],
                "max_context_length": 131072
              }]
            }
            """);
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);

        var status = await client.GetStatusAsync();
        var matches = status.Models
            .Where(model => string.Equals(model.Id, "openai/gpt-oss-120b", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Equal(2, matches.Length);
        Assert.Contains(matches, static model => model.Role == "general" && model.ContextTokens == 131_072);
        Assert.Contains(matches, static model => model.Role == "code" && model.ContextTokens == 131_072);
    }

    [Fact]
    public async Task UnconfiguredLlmWithoutToolUseIsNotAdvertisedInGoModelSelection()
    {
        using var context = new TestServerContext();
        var handler = new ModelStatusHandler("""
            {
              "models": [{
                "type": "llm",
                "key": "legacy-non-tool-llm",
                "display_name": "Legacy Non Tool LLM",
                "loaded_instances": [],
                "max_context_length": 1048576,
                "capabilities": {
                  "vision": false,
                  "trained_for_tool_use": false
                }
              }, {
                "type": "llm",
                "key": "tool-trained-llm",
                "display_name": "Tool Trained LLM",
                "loaded_instances": [],
                "max_context_length": 131072,
                "capabilities": {
                  "vision": false,
                  "trained_for_tool_use": true
                }
              }]
            }
            """);
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);

        var status = await client.GetStatusAsync();

        Assert.DoesNotContain(status.Models, static model => model.Id == "legacy-non-tool-llm");
        Assert.Contains(status.Models, static model => model.Id == "tool-trained-llm" && model.Role == "general");
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
              "usage": {
                "input_tokens": 120,
                "output_tokens": 24,
                "output_tokens_details": { "reasoning_tokens": 18 }
              }
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
        Assert.True(result.HadReasoning);
        Assert.Equal(18, result.ReasoningTokens);

        using var request = JsonDocument.Parse(Assert.IsType<string>(handler.RequestBody));
        var root = request.RootElement;
        Assert.False(root.GetProperty("stream").GetBoolean());
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

    [Fact(Skip = "Coding streaming is intentionally disabled; the atomic Chat Completions path is covered below.")]
    public async Task CodingCompletionStopsAfterTheFirstCompleteStreamingToolCall()
    {
        using var context = new TestServerContext();
        var handler = new StreamingResponsesHandler("""
            data: {"type":"response.output_item.added","output_index":0,"item":{"id":"fc_search","type":"function_call","call_id":"call_search","name":"web.search","arguments":""}}

            data: {"type":"response.function_call_arguments.delta","output_index":0,"item_id":"fc_search","delta":"{\"query\":\"Mechanik\""}

            data: {"type":"response.function_call_arguments.delta","output_index":0,"item_id":"fc_search","delta":",\"maximumResults\":10}"}

            data: {"type":"response.function_call_arguments.done","output_index":0,"item_id":"fc_search","name":"web.search","arguments":"{\"query\":\"Mechanik\",\"maximumResults\":10}"}

            data: {"type":"response.output_item.added","output_index":1,"item":{"id":"fc_read","type":"function_call","call_id":"call_read","name":"fs.readText","arguments":""}}

            data: {"type":"response.function_call_arguments.delta","output_index":1,"item_id":"fc_read","delta":"{\"path\":\"never-read.md\"}"}

            data: {"type":"response.completed","response":{"usage":{"input_tokens":40,"output_tokens":20}}}

            """);
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);
        using var schemaDocument = JsonDocument.Parse("""
            {"type":"object","properties":{"query":{"type":"string"}},"required":["query"],"additionalProperties":false}
            """);

        var result = await client.CompleteChatAsync(
            "qwen3-coder-next",
            [new LmChatMessage("system", "Nutze genau ein Werkzeug."), new LmChatMessage("user", "Suche im Web")],
            [new LmToolDefinition("web.search", "Suche", schemaDocument.RootElement.Clone())],
            modelRole: "code");

        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("call_search", call.Id);
        Assert.Equal("web.search", call.Name);
        Assert.Equal("Mechanik", call.Arguments.GetProperty("query").GetString());
        Assert.Equal(10, call.Arguments.GetProperty("maximumResults").GetInt32());
        using var request = JsonDocument.Parse(Assert.IsType<string>(handler.RequestBody));
        Assert.True(request.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(request.RootElement.GetProperty("parallel_tool_calls").GetBoolean());
    }

    [Fact(Skip = "Coding streaming is intentionally disabled; the atomic Chat Completions path is covered below.")]
    public async Task CodingToolStreamStillReturnsATextOnlyFinalAnswer()
    {
        using var context = new TestServerContext();
        var handler = new StreamingResponsesHandler("""
            data: {"type":"response.output_text.delta","delta":"### Prozessbericht\n\nFertig"}

            data: {"type":"response.completed","response":{"usage":{"input_tokens":25,"output_tokens":5,"output_tokens_details":{"reasoning_tokens":0}}}}

            """);
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);
        using var schemaDocument = JsonDocument.Parse("""
            {"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}
            """);

        var result = await client.CompleteChatAsync(
            "qwen3-coder-next",
            [new LmChatMessage("user", "Fasse die abgeschlossene Arbeit zusammen.")],
            [new LmToolDefinition("fs.readText", "Lese", schemaDocument.RootElement.Clone())],
            modelRole: "code");

        Assert.Equal("### Prozessbericht\n\nFertig", result.Content);
        Assert.Empty(result.ToolCalls);
        Assert.Equal(25, result.InputTokens);
        Assert.Equal(5, result.OutputTokens);
    }

    [Fact(Skip = "Coding streaming is intentionally disabled; the atomic Chat Completions path is covered below.")]
    public async Task CodingToolRequirementIsForwardedToLmStudio()
    {
        using var context = new TestServerContext();
        var handler = new StreamingResponsesHandler("""
            data: {"type":"response.output_item.added","output_index":0,"item":{"id":"fc_read","type":"function_call","call_id":"call_read","name":"fs.readText","arguments":""}}

            data: {"type":"response.function_call_arguments.done","output_index":0,"item_id":"fc_read","arguments":"{\"path\":\"README.md\"}"}

            """);
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);
        using var schemaDocument = JsonDocument.Parse("""
            {"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}
            """);

        _ = await client.CompleteChatAsync(
            "qwen3-coder-next",
            [new LmChatMessage("user", "Lies die Datei.")],
            [new LmToolDefinition("fs.readText", "Lese", schemaDocument.RootElement.Clone())],
            modelRole: "code",
            requireToolCall: true);

        using var request = JsonDocument.Parse(Assert.IsType<string>(handler.RequestBody));
        Assert.Equal("required", request.RootElement.GetProperty("tool_choice").GetString());
    }

    [Fact(Skip = "Coding streaming is intentionally disabled; the atomic Chat Completions path is covered below.")]
    public async Task TerminatedCodingStreamIsRetriedWithTheSameLoadedModel()
    {
        using var context = new TestServerContext();
        var handler = new QueuedStreamingResponsesHandler(
            """
            data: {"type":"response.failed","response":{"error":{"message":"terminated"}}}

            """,
            """
            data: {"type":"response.output_item.added","output_index":0,"item":{"id":"fc_search","type":"function_call","call_id":"call_search","name":"web.search","arguments":""}}

            data: {"type":"response.function_call_arguments.done","output_index":0,"item_id":"fc_search","arguments":"{\"query\":\"Mechanik\"}"}

            """);
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);
        using var schemaDocument = JsonDocument.Parse("""
            {"type":"object","properties":{"query":{"type":"string"}},"required":["query"],"additionalProperties":false}
            """);

        var result = await client.CompleteChatAsync(
            "qwen3-coder-next",
            [
                new LmChatMessage("system", "PRIMARY_POLICY_SENTINEL mit nativer Tool-Anweisung"),
                new LmChatMessage("user", "Suche im Web."),
            ],
            [new LmToolDefinition("web.search", "Suche", schemaDocument.RootElement.Clone())],
            modelRole: "code",
            requireToolCall: true);

        Assert.Equal(2, handler.RequestCount);
        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("web.search", call.Name);
        Assert.Equal("Mechanik", call.Arguments.GetProperty("query").GetString());
    }

    [Fact(Skip = "Coding streaming is intentionally disabled; the atomic Chat Completions path is covered below.")]
    public async Task TerminatedCodingStreamFallsBackToChatCompletionsForTheCompleteToolCall()
    {
        using var context = new TestServerContext();
        var handler = new TerminatedResponsesThenChatCompletionsHandler();
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);
        using var schemaDocument = JsonDocument.Parse("""
            {"type":"object","properties":{"query":{"type":"string"}},"required":["query"],"additionalProperties":false}
            """);

        var result = await client.CompleteChatAsync(
            "qwen3-coder-next",
            [new LmChatMessage("system", "Nutze genau ein Werkzeug."), new LmChatMessage("user", "Suche im Web")],
            [new LmToolDefinition("web.search", "Suche", schemaDocument.RootElement.Clone())],
            modelRole: "code",
            requireToolCall: true);

        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("web.search", call.Name);
        Assert.Equal("Mechanik", call.Arguments.GetProperty("query").GetString());
        Assert.Equal(2, handler.ResponsesRequestCount);
        Assert.Equal(1, handler.ChatCompletionsRequestCount);
    }

    [Fact]
    public async Task RequiredCodingTurnPreservesPrimaryPolicyAndUsesOneAtomicNativeRequest()
    {
        using var context = new TestServerContext();
        var handler = new RecordingHandler("""
            {"choices":[{"message":{"role":"assistant","content":"{\"query\":\"Mechanik\"}"},"finish_reason":"stop"}],"usage":{"prompt_tokens":40,"completion_tokens":12}}
            """);
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);
        using var schemaDocument = JsonDocument.Parse("""
            {"type":"object","properties":{"query":{"type":"string"}},"required":["query"],"additionalProperties":false}
            """);

        var result = await client.CompleteChatAsync(
            "qwen3-coder-next",
            [
                new LmChatMessage("system", "PRIMARY_POLICY_SENTINEL mit nativer Tool-Anweisung"),
                new LmChatMessage("user", "Suche im Web."),
            ],
            [new LmToolDefinition("web.search", "Suche", schemaDocument.RootElement.Clone())],
            modelRole: "code",
            requireToolCall: true,
            requiredToolName: "web.search");

        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("web.search", call.Name);
        Assert.Equal("Mechanik", call.Arguments.GetProperty("query").GetString());
        Assert.Equal("/v1/chat/completions", handler.RequestPath);
        using var request = JsonDocument.Parse(Assert.IsType<string>(handler.RequestBody));
        Assert.False(request.RootElement.GetProperty("stream").GetBoolean());
        var tool = Assert.Single(request.RootElement.GetProperty("tools").EnumerateArray());
        Assert.Equal("web.search", tool.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal(
            "string",
            tool.GetProperty("function").GetProperty("parameters")
                .GetProperty("properties").GetProperty("query").GetProperty("type").GetString());
        Assert.Equal("required", request.RootElement.GetProperty("tool_choice").GetString());
        Assert.False(request.RootElement.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.False(request.RootElement.TryGetProperty("response_format", out _));
        Assert.Contains("PRIMARY_POLICY_SENTINEL", handler.RequestBody, StringComparison.Ordinal);
        Assert.Equal(2, request.RootElement.GetProperty("messages").GetArrayLength());
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task MatchingParsedToolCallFromPlainArgumentResponseIsAcceptedWithoutRetry()
    {
        using var context = new TestServerContext();
        var handler = new RecordingHandler("""
            {"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_native","type":"function","function":{"name":"web.search","arguments":"{\"query\":\"Mechanik\"}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":40,"completion_tokens":12}}
            """);
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);
        using var schemaDocument = JsonDocument.Parse("""
            {"type":"object","properties":{"query":{"type":"string"}},"required":["query"],"additionalProperties":false}
            """);

        var result = await client.CompleteChatAsync(
            "qwen3-coder-next",
            [new LmChatMessage("user", "Suche im Web.")],
            [new LmToolDefinition("web.search", "Suche", schemaDocument.RootElement.Clone())],
            modelRole: "code",
            requireToolCall: true,
            requiredToolName: "web.search");

        Assert.Equal(1, handler.RequestCount);
        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("call_native", call.Id);
        Assert.Equal("web.search", call.Name);
        Assert.Equal("Mechanik", call.Arguments.GetProperty("query").GetString());
    }

    [Fact]
    public async Task InvalidNativeCodingArgumentsUseOneSchemaFreeRepairRequest()
    {
        using var context = new TestServerContext();
        var handler = new SequenceRecordingHandler(
            """
            {"choices":[{"message":{"role":"assistant","content":"{\"unexpected\":true}"},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":2}}
            """,
            """
            {"choices":[{"message":{"role":"assistant","content":"```json\n{\"query\":\"Mechanik\"}\n```"},"finish_reason":"stop"}],"usage":{"prompt_tokens":6,"completion_tokens":3}}
            """);
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);
        using var schemaDocument = JsonDocument.Parse("""
            {"type":"object","properties":{"query":{"type":"string"}},"required":["query"],"additionalProperties":false}
            """);

        var result = await client.CompleteChatAsync(
            "qwen3-coder-next",
            [new LmChatMessage("user", "Suche im Web.")],
            [new LmToolDefinition("web.search", "Suche", schemaDocument.RootElement.Clone())],
            modelRole: "code",
            requireToolCall: true,
            requiredToolName: "web.search");

        Assert.Equal(2, handler.RequestBodies.Count);
        using var correctionRequest = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.Contains(
            correctionRequest.RootElement.GetProperty("messages").EnumerateArray(),
            static message => message.GetProperty("content").GetString()?.Contains(
                "vorherigen Argumente waren ungültig",
                StringComparison.OrdinalIgnoreCase) == true);
        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("Mechanik", call.Arguments.GetProperty("query").GetString());
        Assert.Equal(11, result.InputTokens);
        Assert.Equal(5, result.OutputTokens);
    }

    [Fact(Skip = "Replaced by the atomic multi-tool coding round tests below.")]
    public async Task RequiredCodingTurnSelectsToolBeforeGeneratingItsRealArguments()
    {
        using var context = new TestServerContext();
        var handler = new SequenceRecordingHandler(
            """
            {"choices":[{"message":{"role":"assistant","content":"2"},"finish_reason":"stop"}],"usage":{"prompt_tokens":11,"completion_tokens":2}}
            """,
            """
            {"choices":[{"message":{"role":"assistant","content":"{\"path\":\"README.md\"}"},"finish_reason":"stop"}],"usage":{"prompt_tokens":13,"completion_tokens":4}}
            """);
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);
        using var emptySchema = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}");
        using var readSchema = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"],\"additionalProperties\":false}");

        var result = await client.CompleteChatAsync(
            "qwen3-coder-next",
            [new LmChatMessage("user", "Untersuche das Repository.")],
            [
                new LmToolDefinition("workspace.map", "Karte", emptySchema.RootElement.Clone()),
                new LmToolDefinition("fs.readText", "Datei lesen", readSchema.RootElement.Clone()),
            ],
            modelRole: "code",
            requireToolCall: true);

        Assert.Equal(2, handler.RequestBodies.Count);
        Assert.Equal(["/v1/chat/completions", "/v1/chat/completions"], handler.RequestPaths);
        using var selectionRequest = JsonDocument.Parse(handler.RequestBodies[0]);
        Assert.False(selectionRequest.RootElement.TryGetProperty("tools", out _));
        Assert.False(selectionRequest.RootElement.TryGetProperty("response_format", out _));
        Assert.False(selectionRequest.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal(8, selectionRequest.RootElement.GetProperty("max_tokens").GetInt32());
        var routingSystemText = selectionRequest.RootElement
            .GetProperty("messages")
            .EnumerateArray()
            .Single(static message => string.Equals(
                message.GetProperty("role").GetString(),
                "system",
                StringComparison.Ordinal))
            .GetProperty("content")
            .GetString();
        Assert.Contains("workspace.map", routingSystemText, StringComparison.Ordinal);
        Assert.Contains("fs.readText", routingSystemText, StringComparison.Ordinal);
        using var argumentRequest = JsonDocument.Parse(handler.RequestBodies[1]);
        var argumentTool = Assert.Single(argumentRequest.RootElement.GetProperty("tools").EnumerateArray());
        Assert.Equal("fs.readText", argumentTool.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("required", argumentRequest.RootElement.GetProperty("tool_choice").GetString());
        Assert.False(argumentRequest.RootElement.TryGetProperty("response_format", out _));
        Assert.Equal(
            "string",
            argumentTool.GetProperty("function").GetProperty("parameters")
                .GetProperty("properties").GetProperty("path").GetProperty("type").GetString());
        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("README.md", call.Arguments.GetProperty("path").GetString());
        Assert.Equal(24, result.InputTokens);
        Assert.Equal(6, result.OutputTokens);
    }

    [Fact(Skip = "Replaced by structured-history preservation in the atomic coding round tests below.")]
    public async Task CodingToolNameSelectionFlattensPreviousToolHistoryWithoutNativeToolSchema()
    {
        using var context = new TestServerContext();
        var handler = new SequenceRecordingHandler(
            """
            {"choices":[{"message":{"role":"assistant","content":"{\"name\":\"fs.readText\"}"},"finish_reason":"stop"}],"usage":{"prompt_tokens":17,"completion_tokens":2}}
            """,
            """
            {"choices":[{"message":{"role":"assistant","content":"{\"path\":\"README.md\"}"},"finish_reason":"stop"}],"usage":{"prompt_tokens":19,"completion_tokens":4}}
            """);
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);
        using var emptySchema = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}");
        using var readSchema = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"],\"additionalProperties\":false}");
        using var searchArguments = JsonDocument.Parse("{\"query\":\"Mechanik\"}");
        using var fetchArguments = JsonDocument.Parse("{\"url\":\"https://example.org/mechanik\"}");
        var fetchedPage = JsonSerializer.Serialize(new
        {
            success = true,
            url = "https://example.org/mechanik",
            mediaType = "text/html",
            content = "ROUTER_SENTINEL_" + new string('x', 40_000),
        });

        var result = await client.CompleteChatAsync(
            "qwen3-coder-next",
            [
                new LmChatMessage("system", "Arbeite im Workspace."),
                new LmChatMessage("user", "Suche nach Mechanik."),
                new LmChatMessage(
                    "assistant",
                    ToolCalls:
                    [
                        new LmToolCall(
                            "call_search",
                            "web.search",
                            searchArguments.RootElement.Clone()),
                    ]),
                new LmChatMessage(
                    "tool",
                    "{\"results\":[{\"url\":\"https://example.org/mechanik\"}]}",
                    ToolCallId: "call_search"),
                new LmChatMessage(
                    "assistant",
                    ToolCalls:
                    [
                        new LmToolCall(
                            "call_fetch",
                            "web.fetch",
                            fetchArguments.RootElement.Clone()),
                    ]),
                new LmChatMessage("tool", fetchedPage, ToolCallId: "call_fetch"),
                new LmChatMessage("user", "Untersuche nun die lokale Dokumentation."),
            ],
            [
                new LmToolDefinition("workspace.map", "Karte", emptySchema.RootElement.Clone()),
                new LmToolDefinition("fs.readText", "Datei lesen", readSchema.RootElement.Clone()),
            ],
            modelRole: "code",
            requireToolCall: true);

        using var routingRequest = JsonDocument.Parse(handler.RequestBodies[0]);
        Assert.False(routingRequest.RootElement.TryGetProperty("tools", out _));
        var routingInput = routingRequest.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.All(routingInput, static item => Assert.False(item.TryGetProperty("tool_calls", out _)));
        Assert.DoesNotContain(
            routingInput,
            static item => string.Equals(item.GetProperty("role").GetString(), "tool", StringComparison.Ordinal));
        Assert.Contains(
            routingInput,
            static item => item.GetProperty("content").GetString()?.Contains(
                "[GO_PREVIOUS_TOOL_CALLS] web.search",
                StringComparison.Ordinal) == true);
        Assert.Contains(
            routingInput,
            static item => item.GetProperty("content").GetString()?.Contains(
                "[GO_PREVIOUS_TOOL_RESULT tool=web.search]",
                StringComparison.Ordinal) == true);
        Assert.DoesNotContain("ROUTER_SENTINEL", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.Contains("https://example.org/mechanik", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.Contains("contentCharacters=", handler.RequestBodies[0], StringComparison.Ordinal);

        using var argumentRequest = JsonDocument.Parse(handler.RequestBodies[1]);
        var argumentTool = Assert.Single(argumentRequest.RootElement.GetProperty("tools").EnumerateArray());
        Assert.Equal("fs.readText", argumentTool.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("required", argumentRequest.RootElement.GetProperty("tool_choice").GetString());
        var argumentInput = argumentRequest.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.All(argumentInput, static item => Assert.False(item.TryGetProperty("tool_calls", out _)));
        Assert.DoesNotContain(
            argumentInput,
            static item => string.Equals(item.GetProperty("role").GetString(), "tool", StringComparison.Ordinal));
        Assert.Contains(
            argumentInput,
            static item => item.GetProperty("content").GetString()?.Contains(
                "[GO_PREVIOUS_TOOL_RESULT tool=web.search]",
                StringComparison.Ordinal) == true);
        Assert.Contains("ROUTER_SENTINEL", handler.RequestBodies[1], StringComparison.Ordinal);
        Assert.Equal("fs.readText", Assert.Single(result.ToolCalls).Name);
    }

    [Fact(Skip = "Replaced by schema-free coding action repair tests below.")]
    public async Task AmbiguousCodingToolNameSelectionGetsOneTextOnlyCorrectionAttempt()
    {
        using var context = new TestServerContext();
        var handler = new SequenceRecordingHandler(
            """
            {"choices":[{"message":{"role":"assistant","content":"Ich muss zuerst den nächsten Schritt prüfen."},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":8}}
            """,
            """
            {"choices":[{"message":{"role":"assistant","content":"{\"name\":\"fs.readText\"}"},"finish_reason":"stop"}],"usage":{"prompt_tokens":6,"completion_tokens":2}}
            """,
            """
            {"choices":[{"message":{"role":"assistant","content":"{\"path\":\"README.md\"}"},"finish_reason":"stop"}],"usage":{"prompt_tokens":7,"completion_tokens":3}}
            """);
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);
        using var emptySchema = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}");
        using var readSchema = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"],\"additionalProperties\":false}");

        var result = await client.CompleteChatAsync(
            "qwen3-coder-next",
            [new LmChatMessage("user", "Untersuche die lokale Dokumentation.")],
            [
                new LmToolDefinition("workspace.map", "Karte", emptySchema.RootElement.Clone()),
                new LmToolDefinition("fs.readText", "Datei lesen", readSchema.RootElement.Clone()),
            ],
            modelRole: "code",
            requireToolCall: true);

        Assert.Equal(3, handler.RequestBodies.Count);
        using var correctionRequest = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.False(correctionRequest.RootElement.TryGetProperty("tools", out _));
        Assert.Contains(
            correctionRequest.RootElement.GetProperty("messages").EnumerateArray(),
            static item => item.GetProperty("content").GetString()?.Contains(
                "vorherige Werkzeugauswahl war nicht eindeutig",
                StringComparison.OrdinalIgnoreCase) == true);
        Assert.Equal("fs.readText", Assert.Single(result.ToolCalls).Name);
        Assert.Equal(18, result.InputTokens);
        Assert.Equal(13, result.OutputTokens);
    }

    [Fact(Skip = "Replaced by native channel termination recovery tests below.")]
    public async Task TerminatedCodingToolRouterRetriesWithOnlyTheCurrentRequest()
    {
        using var context = new TestServerContext();
        var handler = new ResponseSequenceHandler(
            (HttpStatusCode.InternalServerError, "{\"error\":{\"message\":\"terminated\"}}"),
            (HttpStatusCode.OK, """
                {"choices":[{"message":{"role":"assistant","content":"2"},"finish_reason":"stop"}],"usage":{"prompt_tokens":6,"completion_tokens":2}}
                """),
            (HttpStatusCode.OK, """
                {"choices":[{"message":{"role":"assistant","content":"{\"path\":\"README.md\"}"},"finish_reason":"stop"}],"usage":{"prompt_tokens":7,"completion_tokens":3}}
                """));
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);
        using var emptySchema = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}");
        using var readSchema = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"],\"additionalProperties\":false}");

        var result = await client.CompleteChatAsync(
            "qwen3-coder-next",
            [
                new LmChatMessage("user", "Untersuche das Repository."),
                new LmChatMessage("assistant", ToolCalls:
                [
                    new LmToolCall("call_old", "web.fetch", JsonDocument.Parse("{\"url\":\"https://example.org\"}").RootElement.Clone()),
                ]),
                new LmChatMessage("tool", new string('x', 20_000), ToolCallId: "call_old"),
            ],
            [
                new LmToolDefinition("workspace.map", "Karte", emptySchema.RootElement.Clone()),
                new LmToolDefinition("fs.readText", "Datei lesen", readSchema.RootElement.Clone()),
            ],
            modelRole: "code",
            requireToolCall: true);

        Assert.Equal(3, handler.RequestBodies.Count);
        using var recoveryRequest = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.Contains(
            recoveryRequest.RootElement.GetProperty("messages").EnumerateArray(),
            static item => item.GetProperty("content").GetString()?.Contains(
                "vom Provider technisch beendet",
                StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(new string('x', 1_201), handler.RequestBodies[1], StringComparison.Ordinal);
        Assert.Equal("fs.readText", Assert.Single(result.ToolCalls).Name);
        Assert.Equal(13, result.InputTokens);
        Assert.Equal(5, result.OutputTokens);
    }

    [Fact(Skip = "The professional loop never guesses a workspace action after repeated provider failures.")]
    public async Task RepeatedCodingToolRouterFailureFallsBackToSafeWorkspaceRead()
    {
        using var context = new TestServerContext();
        var handler = new ResponseSequenceHandler(
            (HttpStatusCode.InternalServerError, "{\"error\":{\"message\":\"terminated\"}}"),
            (HttpStatusCode.InternalServerError, "{\"error\":{\"message\":\"Channel Error\"}}"),
            (HttpStatusCode.OK, """
                {"choices":[{"message":{"role":"assistant","content":"{}"},"finish_reason":"stop"}],"usage":{"prompt_tokens":7,"completion_tokens":2}}
                """));
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);
        using var emptySchema = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}");

        var result = await client.CompleteChatAsync(
            "qwen3-coder-next",
            [new LmChatMessage("user", "Analysiere den Workspace.")],
            [
                new LmToolDefinition("workspace.map", "Karte", emptySchema.RootElement.Clone()),
                new LmToolDefinition("fs.list", "Ordner lesen", emptySchema.RootElement.Clone()),
            ],
            modelRole: "code",
            requireToolCall: true);

        Assert.Equal(3, handler.RequestBodies.Count);
        using var argumentRequest = JsonDocument.Parse(handler.RequestBodies[2]);
        var selectedSchema = Assert.Single(argumentRequest.RootElement.GetProperty("tools").EnumerateArray());
        Assert.Equal(
            "workspace.map",
            selectedSchema.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("workspace.map", Assert.Single(result.ToolCalls).Name);
        Assert.Equal(7, result.InputTokens);
        Assert.Equal(2, result.OutputTokens);
    }

    [Fact(Skip = "Replaced by the atomic optional-final coding round test below.")]
    public async Task OptionalCodingTurnUsesSchemaFreeFinalSynthesisAfterNoToolWasSelected()
    {
        using var context = new TestServerContext();
        var handler = new SequenceRecordingHandler(
            """
            {"choices":[{"message":{"role":"assistant","content":"{\"name\":\"__final__\"}"},"finish_reason":"stop"}],"usage":{"prompt_tokens":7,"completion_tokens":1}}
            """,
            """
            {"id":"resp_final","status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"Die Aufgabe ist vollständig abgeschlossen."}]}],"usage":{"input_tokens":9,"output_tokens":5}}
            """);
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);
        using var schema = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}");

        var result = await client.CompleteChatAsync(
            "qwen3-coder-next",
            [new LmChatMessage("user", "Schließe den Auftrag ab.")],
            [
                new LmToolDefinition("workspace.map", "Karte", schema.RootElement.Clone()),
                new LmToolDefinition("fs.list", "Ordner lesen", schema.RootElement.Clone()),
            ],
            modelRole: "code",
            requireToolCall: false);

        Assert.Equal("Die Aufgabe ist vollständig abgeschlossen.", result.Content);
        Assert.Equal(2, handler.RequestBodies.Count);
        Assert.Equal(["/v1/chat/completions", "/v1/responses"], handler.RequestPaths);
        using var selectionRequest = JsonDocument.Parse(handler.RequestBodies[0]);
        Assert.False(selectionRequest.RootElement.TryGetProperty("tools", out _));
        using var finalRequest = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.False(finalRequest.RootElement.TryGetProperty("tools", out _));
        Assert.Equal(16, result.InputTokens);
        Assert.Equal(6, result.OutputTokens);
    }

    [Fact]
    public async Task RequiredCodingRoundSelectsToolAndArgumentsInOneAtomicRequest()
    {
        using var context = new TestServerContext();
        var handler = new RecordingHandler("""
            {
              "choices": [{
                "message": {
                  "role": "assistant",
                  "content": null,
                  "tool_calls": [{
                    "id": "call_read",
                    "type": "function",
                    "function": { "name": "fs.readText", "arguments": "{\"path\":\"README.md\"}" }
                  }]
                },
                "finish_reason": "tool_calls"
              }],
              "usage": { "prompt_tokens": 31, "completion_tokens": 7 }
            }
            """);
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);
        using var emptySchema = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}");
        using var readSchema = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"],\"additionalProperties\":false}");
        using var previousArguments = JsonDocument.Parse("{\"path\":\"src\"}");

        var result = await client.CompleteChatAsync(
            "qwen3-coder-next",
            [
                new LmChatMessage("system", "Arbeite professionell im Workspace."),
                new LmChatMessage("user", "Untersuche das Repository."),
                new LmChatMessage("assistant", ToolCalls:
                [
                    new LmToolCall("call_previous", "fs.list", previousArguments.RootElement.Clone()),
                ]),
                new LmChatMessage("tool", "{\"entries\":[\"README.md\"]}", ToolCallId: "call_previous"),
            ],
            [
                new LmToolDefinition("workspace.map", "Karte", emptySchema.RootElement.Clone()),
                new LmToolDefinition("fs.readText", "Datei lesen", readSchema.RootElement.Clone()),
            ],
            modelRole: "code",
            requireToolCall: true);

        Assert.Equal(1, handler.RequestCount);
        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("fs.readText", call.Name);
        Assert.Equal("README.md", call.Arguments.GetProperty("path").GetString());
        using var request = JsonDocument.Parse(Assert.IsType<string>(handler.RequestBody));
        var root = request.RootElement;
        Assert.Equal("/v1/chat/completions", handler.RequestPath);
        Assert.Equal(2, root.GetProperty("tools").GetArrayLength());
        Assert.Equal("required", root.GetProperty("tool_choice").GetString());
        Assert.False(root.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.Contains(root.GetProperty("messages").EnumerateArray(), static message =>
            message.GetProperty("role").GetString() == "assistant" && message.TryGetProperty("tool_calls", out _));
        Assert.Contains(root.GetProperty("messages").EnumerateArray(), static message =>
            message.GetProperty("role").GetString() == "tool" && message.GetProperty("tool_call_id").GetString() == "call_previous");
    }

    [Fact]
    public async Task NativeChannelTerminationUsesSchemaFreeValidatedRepair()
    {
        using var context = new TestServerContext();
        var handler = new ResponseSequenceHandler(
            (HttpStatusCode.InternalServerError, "{\"error\":{\"message\":\"terminated\"}}"),
            (HttpStatusCode.OK, """
                {
                  "choices": [{
                    "message": {
                      "role": "assistant",
                      "content": "{\"kind\":\"tool\",\"name\":\"fs.readText\",\"arguments\":{\"path\":\"README.md\"}}"
                    },
                    "finish_reason": "stop"
                  }],
                  "usage": { "prompt_tokens": 19, "completion_tokens": 6 }
                }
                """));
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);
        using var readSchema = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"],\"additionalProperties\":false}");

        var result = await client.CompleteChatAsync(
            "qwen3-coder-next",
            [new LmChatMessage("user", "Lies die Dokumentation.")],
            [new LmToolDefinition("fs.readText", "Datei lesen", readSchema.RootElement.Clone())],
            modelRole: "code",
            requireToolCall: true,
            requiredToolName: "fs.readText");

        Assert.Equal(2, handler.RequestBodies.Count);
        using var nativeRequest = JsonDocument.Parse(handler.RequestBodies[0]);
        using var repairRequest = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.True(nativeRequest.RootElement.TryGetProperty("tools", out _));
        Assert.False(repairRequest.RootElement.TryGetProperty("tools", out _));
        Assert.Equal("fs.readText", Assert.Single(result.ToolCalls).Name);
    }

    [Fact]
    public async Task InvalidSchemaFreeRepairGetsOneBoundedCorrection()
    {
        using var context = new TestServerContext();
        var handler = new ResponseSequenceHandler(
            (HttpStatusCode.InternalServerError, "{\"error\":{\"message\":\"Channel Error\"}}"),
            (HttpStatusCode.OK, """
                {"choices":[{"message":{"role":"assistant","content":"{\"kind\":\"tool\",\"name\":\"fs.readText\",\"arguments\":{\"unknown\":true}}"}}]}
                """),
            (HttpStatusCode.OK, """
                {"choices":[{"message":{"role":"assistant","content":"{\"kind\":\"tool\",\"name\":\"fs.readText\",\"arguments\":{\"path\":\"README.md\"}}"}}]}
                """));
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);
        using var readSchema = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"],\"additionalProperties\":false}");

        var result = await client.CompleteChatAsync(
            "qwen3-coder-next",
            [new LmChatMessage("user", "Lies die Dokumentation.")],
            [new LmToolDefinition("fs.readText", "Datei lesen", readSchema.RootElement.Clone())],
            modelRole: "code",
            requireToolCall: true,
            requiredToolName: "fs.readText");

        Assert.Equal(3, handler.RequestBodies.Count);
        Assert.Contains("vorherige Aktion war", handler.RequestBodies[2], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("README.md", Assert.Single(result.ToolCalls).Arguments.GetProperty("path").GetString());
    }

    [Fact]
    public async Task MissingProviderToolCallIdGetsAHostIdentity()
    {
        using var context = new TestServerContext();
        var handler = new RecordingHandler("""
            {"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{"type":"function","function":{"name":"workspace.map","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}
            """);
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);
        using var schema = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}");

        var result = await client.CompleteChatAsync(
            "qwen3-coder-next",
            [new LmChatMessage("user", "Erfasse den Workspace.")],
            [new LmToolDefinition("workspace.map", "Karte", schema.RootElement.Clone())],
            modelRole: "code",
            requireToolCall: true,
            requiredToolName: "workspace.map");

        Assert.StartsWith("call_", Assert.Single(result.ToolCalls).Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OptionalCodingRoundCanFinishInTheSameAtomicRequest()
    {
        using var context = new TestServerContext();
        var handler = new RecordingHandler("""
            {"choices":[{"message":{"role":"assistant","content":"Die Aufgabe ist vollständig abgeschlossen."},"finish_reason":"stop"}],"usage":{"prompt_tokens":9,"completion_tokens":5}}
            """);
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);
        using var schema = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}");

        var result = await client.CompleteChatAsync(
            "qwen3-coder-next",
            [new LmChatMessage("user", "Schließe den Auftrag ab.")],
            [new LmToolDefinition("workspace.map", "Karte", schema.RootElement.Clone())],
            modelRole: "code");

        Assert.Equal("Die Aufgabe ist vollständig abgeschlossen.", result.Content);
        Assert.Empty(result.ToolCalls);
        Assert.Equal(1, handler.RequestCount);
        using var request = JsonDocument.Parse(Assert.IsType<string>(handler.RequestBody));
        Assert.Equal("auto", request.RootElement.GetProperty("tool_choice").GetString());
    }

    [Fact]
    public async Task AtomicCodingToolCallPinsTheSingleRequiredFunction()
    {
        using var context = new TestServerContext();
        var handler = new RecordingHandler("""
            {"choices":[{"message":{"role":"assistant","content":"{\"query\":\"Mechanik\"}"},"finish_reason":"stop"}]}
            """);
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);
        using var schemaDocument = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}},\"required\":[\"query\"]}");

        _ = await client.CompleteChatAsync(
            "qwen3-coder-next",
            [new LmChatMessage("user", "Suche im Web.")],
            [new LmToolDefinition("web.search", "Suche", schemaDocument.RootElement.Clone())],
            modelRole: "code",
            requireToolCall: true,
            requiredToolName: "web.search");

        using var request = JsonDocument.Parse(Assert.IsType<string>(handler.RequestBody));
        Assert.Equal("/v1/chat/completions", handler.RequestPath);
        var tool = Assert.Single(request.RootElement.GetProperty("tools").EnumerateArray());
        Assert.Equal("web.search", tool.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("required", request.RootElement.GetProperty("tool_choice").GetString());
        Assert.False(request.RootElement.TryGetProperty("response_format", out _));
    }

    [Fact]
    public async Task NamedToolChoiceRejectsAToolOutsideTheSuppliedCatalog()
    {
        using var context = new TestServerContext();
        using var http = new HttpClient(new RecordingHandler("{}"));
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);
        using var schemaDocument = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{},\"required\":[]}");

        var error = await Assert.ThrowsAsync<ArgumentException>(() => client.CompleteChatAsync(
            "qwen3-coder-next",
            [new LmChatMessage("user", "Suche im Web.")],
            [new LmToolDefinition("workspace.map", "Karte", schemaDocument.RootElement.Clone())],
            modelRole: "code",
            requireToolCall: true,
            requiredToolName: "web.search"));

        Assert.Contains("web.search", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AtomicCodingToolCallRetriesTransientLmStudioChannelFailure()
    {
        using var context = new TestServerContext();
        var handler = new RecordingHandler("""
            {"choices":[{"message":{"role":"assistant","content":"{\"query\":\"Mechanik\"}"},"finish_reason":"stop"}]}
            """, failuresBeforeSuccess: 1);
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);
        using var schemaDocument = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}},\"required\":[\"query\"]}");

        var result = await client.CompleteChatAsync(
            "qwen3-coder-next",
            [new LmChatMessage("user", "Suche im Web.")],
            [new LmToolDefinition("web.search", "Suche", schemaDocument.RootElement.Clone())],
            modelRole: "code",
            requireToolCall: true,
            requiredToolName: "web.search");

        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("web.search", call.Name);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal("/v1/chat/completions", handler.RequestPath);
    }

    [Fact]
    public async Task DynamicSystemGuidanceStaysInChronologicalInputForPromptCacheReuse()
    {
        using var context = new TestServerContext();
        var handler = new RecordingHandler("""
            {
              "id": "resp_cache_test",
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
            "qwen3.8-27b",
            [
                new LmChatMessage("system", "Unveränderliche Grundrichtlinie"),
                new LmChatMessage("user", "Ändere und prüfe das Projekt."),
                new LmChatMessage("system", "Dynamischer Reparaturhinweis"),
            ],
            []);

        using var request = JsonDocument.Parse(Assert.IsType<string>(handler.RequestBody));
        var root = request.RootElement;
        Assert.Equal("Unveränderliche Grundrichtlinie", root.GetProperty("instructions").GetString());
        var input = root.GetProperty("input").EnumerateArray().ToArray();
        Assert.Equal(2, input.Length);
        Assert.Equal("user", input[0].GetProperty("role").GetString());
        Assert.Equal("Ändere und prüfe das Projekt.", input[0].GetProperty("content").GetString());
        Assert.Equal("user", input[1].GetProperty("role").GetString());
        Assert.Equal(
            "[GO_RUNTIME_GUIDANCE]\nDynamischer Reparaturhinweis",
            input[1].GetProperty("content").GetString());
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
    public async Task EmbeddingInferenceDoesNotScheduleAnIdleUnload()
    {
        using var context = new TestServerContext();
        var handler = new EmbeddingLoadHandler();
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);

        _ = await client.EnsureModelLoadedAsync("text-embedding-bge-m3", 8192);
        _ = await client.CreateEmbeddingsAsync("text-embedding-bge-m3", ["Lüftungsanlage"]);

        using var request = JsonDocument.Parse(Assert.IsType<string>(handler.EmbeddingRequestBody));
        Assert.False(request.RootElement.TryGetProperty("ttl", out _));
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

    [Theory]
    [InlineData("qwen3.8-27b", "qwen38-resident")]
    [InlineData("qwen3-coder-next", "qwen-resident")]
    public async Task CompatibleResidentCodingModelIsReusedWithoutAProcessMarker(
        string modelId,
        string expectedInstanceId)
    {
        using var context = new TestServerContext();
        var handler = new ResidentCodingModelHandler(modelId, expectedInstanceId);
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);
        var loadingNotifications = 0;

        var first = await client.EnsureModelPreparedAsync(
            modelId,
            262_144,
            _ =>
            {
                Interlocked.Increment(ref loadingNotifications);
                return Task.CompletedTask;
            });
        var second = await client.EnsureModelPreparedAsync(
            modelId,
            262_144,
            _ =>
            {
                Interlocked.Increment(ref loadingNotifications);
                return Task.CompletedTask;
            });

        Assert.True(first.WasAlreadyLoaded);
        Assert.True(second.WasAlreadyLoaded);
        Assert.Equal(expectedInstanceId, first.InstanceId);
        Assert.Equal(0, loadingNotifications);
        Assert.Equal(0, handler.LoadRequests);
        Assert.Equal(0, handler.UnloadRequests);
    }

    [Fact]
    public async Task QwenCoderUsesPublishedNonThinkingSamplingProfile()
    {
        using var context = new TestServerContext();
        var handler = new RecordingHandler("""
            {
              "choices": [{
                "message": { "role": "assistant", "content": "Erledigt" },
                "finish_reason": "stop"
              }],
              "usage": { "prompt_tokens": 20, "completion_tokens": 4 }
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
            [],
            modelRole: "code");

        using var request = JsonDocument.Parse(Assert.IsType<string>(handler.RequestBody));
        var root = request.RootElement;
        Assert.Equal(1.0, root.GetProperty("temperature").GetDouble(), 3);
        Assert.Equal(0.95, root.GetProperty("top_p").GetDouble(), 3);
        Assert.Equal(40, root.GetProperty("top_k").GetInt32());
        Assert.False(root.TryGetProperty("reasoning", out _));
    }

    [Fact]
    public async Task Qwen38CoderUsesLmStudioOnSwitchWithBoundedReasoning()
    {
        using var context = new TestServerContext();
        var handler = new RecordingHandler("""
            {
              "choices": [{
                "message": { "role": "assistant", "content": "Erledigt" },
                "finish_reason": "stop"
              }],
              "usage": { "prompt_tokens": 20, "completion_tokens": 4 }
            }
            """);
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);

        _ = await client.CompleteChatAsync(
            "qwen3.8-27b",
            [new LmChatMessage("user", "Analysiere und ändere das Projekt.")],
            [],
            modelRole: "code");

        using var request = JsonDocument.Parse(Assert.IsType<string>(handler.RequestBody));
        var root = request.RootElement;
        Assert.Equal(1.0, root.GetProperty("temperature").GetDouble(), 3);
        Assert.Equal(0.95, root.GetProperty("top_p").GetDouble(), 3);
        Assert.Equal(20, root.GetProperty("top_k").GetInt32());
        Assert.Equal(0.0, root.GetProperty("min_p").GetDouble(), 3);
        Assert.Equal(0.0, root.GetProperty("presence_penalty").GetDouble(), 3);
        Assert.Equal(1.0, root.GetProperty("repetition_penalty").GetDouble(), 3);
        Assert.False(root.TryGetProperty("reasoning", out _));
        Assert.Equal(4_096, root.GetProperty("thinking_budget_tokens").GetInt32());
    }

    [Fact]
    public async Task Qwen38CoderMapsLmStudioOffToResponsesNone()
    {
        using var context = new TestServerContext();
        var handler = new RecordingHandler("""
            {
              "choices": [{
                "message": { "role": "assistant", "content": "Erledigt" },
                "finish_reason": "stop"
              }],
              "usage": { "prompt_tokens": 20, "completion_tokens": 4 }
            }
            """);
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);

        _ = await client.CompleteChatAsync(
            "qwen3.8-27b",
            [new LmChatMessage("user", "Analysiere das Projekt gründlich.")],
            [],
            modelRole: "code",
            reasoningEffort: "off");

        using var request = JsonDocument.Parse(Assert.IsType<string>(handler.RequestBody));
        var root = request.RootElement;
        Assert.Equal("off", root.GetProperty("reasoning_effort").GetString());
        Assert.False(root.TryGetProperty("thinking_budget_tokens", out _));
    }

    [Fact]
    public async Task GptOssCoderUsesOfficialSamplingAndHighReasoningProfile()
    {
        using var context = new TestServerContext();
        var handler = new RecordingHandler("""
            {
              "choices": [{
                "message": { "role": "assistant", "content": "Erledigt" },
                "finish_reason": "stop"
              }],
              "usage": { "prompt_tokens": 20, "completion_tokens": 4 }
            }
            """);
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);

        _ = await client.CompleteChatAsync(
            "openai/gpt-oss-120b",
            [new LmChatMessage("user", "Behebe den Fehler und prüfe die Änderung.")],
            [],
            modelRole: "code");

        using var request = JsonDocument.Parse(Assert.IsType<string>(handler.RequestBody));
        var root = request.RootElement;
        Assert.Equal(1.0, root.GetProperty("temperature").GetDouble(), 3);
        Assert.Equal(1.0, root.GetProperty("top_p").GetDouble(), 3);
        Assert.Equal("high", root.GetProperty("reasoning_effort").GetString());
        Assert.False(root.TryGetProperty("top_k", out _));
    }

    [Fact]
    public async Task GptOssGeneralRoleKeepsTheExistingLowReasoningProfile()
    {
        using var context = new TestServerContext();
        var handler = new RecordingHandler("""
            {
              "status": "completed",
              "output": [{
                "type": "message",
                "content": [{ "type": "output_text", "text": "Antwort" }]
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
            "openai/gpt-oss-120b",
            [new LmChatMessage("user", "Erkläre die Gleichung.")],
            [],
            modelRole: "general");

        using var request = JsonDocument.Parse(Assert.IsType<string>(handler.RequestBody));
        var root = request.RootElement;
        Assert.Equal(0.2, root.GetProperty("temperature").GetDouble(), 3);
        Assert.Equal("low", root.GetProperty("reasoning").GetProperty("effort").GetString());
    }

    [Fact]
    public async Task GptOssGeneralRoleForwardsTheSelectedMediumReasoningEffort()
    {
        using var context = new TestServerContext();
        var handler = new RecordingHandler("""
            {
              "status": "completed",
              "output": [{
                "type": "message",
                "content": [{ "type": "output_text", "text": "Antwort" }]
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
            "openai/gpt-oss-120b",
            [new LmChatMessage("user", "Erkläre die Gleichung.")],
            [],
            modelRole: "general",
            reasoningEffort: "medium");

        using var request = JsonDocument.Parse(Assert.IsType<string>(handler.RequestBody));
        Assert.Equal("medium", request.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
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
    public async Task VisionInferenceDoesNotScheduleAnIdleUnload()
    {
        using var context = new TestServerContext();
        var handler = new ResidentCoreLoadHandler();
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);
        Directory.CreateDirectory(context.Root);
        var imagePath = Path.Combine(context.Root, "vision.png");
        await File.WriteAllBytesAsync(imagePath, [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

        _ = await client.EnsureModelLoadedAsync("qwen3-vl-30b-a3b-instruct", 65_536);
        _ = await client.AnalyzeImagesAsync(
            "qwen3-vl-30b-a3b-instruct",
            "Was ist zu sehen?",
            [imagePath]);

        using var request = JsonDocument.Parse(Assert.IsType<string>(handler.ChatRequestBody));
        Assert.False(request.RootElement.TryGetProperty("ttl", out _));
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

    [Fact(Skip = "Coding now uses Chat Completions directly; no Responses compatibility probe is performed.")]
    public async Task CodingResponsesCompatibilityFailureFallsBackToToolFreeChatCompletion()
    {
        using var context = new TestServerContext();
        var handler = new ResponsesCompatibilityHandler();
        using var http = new HttpClient(handler);
        using var client = new LmStudioClient(
            http,
            context.WrappedOptions,
            new DpapiSecretStore(context.WrappedOptions),
            NullLogger<LmStudioClient>.Instance);

        var result = await client.CompleteChatAsync(
            "qwen3.8-27b",
            [new LmChatMessage("user", "Arbeite am Repository weiter.")],
            [],
            modelRole: "code");

        Assert.Equal("Kompatible Antwort", result.Content);
        Assert.Empty(result.ToolCalls);
        Assert.Equal(["/v1/responses", "/v1/chat/completions"], handler.RequestPaths);
        using var request = JsonDocument.Parse(handler.RequestBodies[0]);
        var root = request.RootElement;
        Assert.Equal("qwen3.8-27b", root.GetProperty("model").GetString());
        Assert.Equal(20, root.GetProperty("top_k").GetInt32());
        using var fallbackRequest = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.False(fallbackRequest.RootElement.TryGetProperty("tools", out _));
    }

    private sealed class StreamingResponsesHandler(string responseEvents) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseEvents, Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    private sealed class QueuedStreamingResponsesHandler(params string[] responseEvents) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responseEvents);

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No queued streaming response remains.");
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "text/event-stream"),
            });
        }
    }

    private sealed class TerminatedResponsesThenChatCompletionsHandler : HttpMessageHandler
    {
        public int ResponsesRequestCount { get; private set; }

        public int ChatCompletionsRequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/v1/responses")
            {
                ResponsesRequestCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "data: {\"type\":\"response.failed\",\"response\":{\"error\":{\"message\":\"terminated before complete tool call\"}}}\n\n",
                        Encoding.UTF8,
                        "text/event-stream"),
                });
            }

            if (request.RequestUri?.AbsolutePath == "/v1/chat/completions")
            {
                ChatCompletionsRequestCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_search","function":{"name":"web.search","arguments":""}}]}}]}

                        data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"query\":\"Mechanik\"}"}}]}}]}

                        data: {"choices":[{"finish_reason":"tool_calls","delta":{}}],"usage":{"prompt_tokens":40,"completion_tokens":12}}

                        data: [DONE]

                        """, Encoding.UTF8, "text/event-stream"),
                });
            }

            throw new InvalidOperationException($"Unexpected LM Studio path: {request.RequestUri?.AbsolutePath}");
        }
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

    private sealed class SequenceRecordingHandler(params string[] responseJson) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responseJson);

        public List<string> RequestBodies { get; } = [];

        public List<string> RequestPaths { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestPaths.Add(request.RequestUri?.AbsolutePath ?? string.Empty);
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (!_responses.TryDequeue(out var response))
            {
                throw new InvalidOperationException("The test received more requests than configured responses.");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class ResponseSequenceHandler(
        params (HttpStatusCode StatusCode, string Body)[] configuredResponses) : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode StatusCode, string Body)> _responses = new(configuredResponses);

        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (!_responses.TryDequeue(out var configuredResponse))
            {
                throw new InvalidOperationException("The test received more requests than configured responses.");
            }

            return new HttpResponseMessage(configuredResponse.StatusCode)
            {
                Content = new StringContent(configuredResponse.Body, Encoding.UTF8, "application/json"),
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
                using var requestDocument = JsonDocument.Parse(RequestBodies[^1]);
                if (!requestDocument.RootElement.TryGetProperty("tools", out _))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""
                            {
                              "choices": [{
                                "message": {
                                  "role": "assistant",
                                  "content": "Kompatible Antwort"
                                }
                              }],
                              "usage": { "prompt_tokens": 9, "completion_tokens": 3 }
                            }
                            """, Encoding.UTF8, "application/json"),
                    };
                }
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

    private sealed class ModelStatusHandler(string responseJson = "{\"models\":[]}") : HttpMessageHandler
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
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class EmbeddingLoadHandler(int failuresBeforeSuccess = 0) : HttpMessageHandler
    {
        public int LoadRequests { get; private set; }

        public string? LoadRequestBody { get; private set; }

        public string? EmbeddingRequestBody { get; private set; }

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

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/embeddings")
            {
                EmbeddingRequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return JsonResponse("{\"data\":[{\"index\":0,\"embedding\":[0.1,0.2]}]}");
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

        public string? ChatRequestBody { get; private set; }

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

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/chat/completions")
            {
                ChatRequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return JsonResponse("{\"choices\":[{\"message\":{\"content\":\"Bildanalyse\"}}]}");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage JsonResponse(string value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class ResidentCodingModelHandler(string modelId, string instanceId) : HttpMessageHandler
    {
        public int LoadRequests { get; private set; }

        public int UnloadRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/api/v1/models")
            {
                return Task.FromResult(JsonResponse($$"""
                    {
                      "models": [{
                        "type": "llm",
                        "key": "{{modelId}}",
                        "display_name": "Resident coding model",
                        "loaded_instances": [{
                          "id": "{{instanceId}}",
                          "model_instance_id": "{{instanceId}}",
                          "config": {
                            "context_length": 262144,
                            "parallel": 1,
                            "flash_attention": true,
                            "offload_kv_cache_to_gpu": true
                          }
                        }],
                        "max_context_length": 262144
                      }]
                    }
                    """));
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/api/v1/models/load")
            {
                LoadRequests++;
                return Task.FromResult(JsonResponse("{}"));
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/api/v1/models/unload")
            {
                UnloadRequests++;
                return Task.FromResult(JsonResponse("{}"));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResponse(string value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class RecordingNativeAgentClient : ILmStudioNativeAgentClient
    {
        private readonly Queue<Func<LmStudioNativeAgentRequest, LmChatResult>> _responses;

        public RecordingNativeAgentClient(
            params Func<LmStudioNativeAgentRequest, LmChatResult>[] responses)
        {
            _responses = new Queue<Func<LmStudioNativeAgentRequest, LmChatResult>>(responses);
        }

        public List<LmStudioNativeAgentRequest> Requests { get; } = [];

        public LmStudioModelList Models { get; set; } = new([]);

        public LmStudioModelPreparation LoadResult { get; set; } =
            new("native-test", WasAlreadyLoaded: false);

        public IReadOnlyList<IReadOnlyList<double>> EmbeddingResult { get; set; } = [];

        public string VisionResult { get; set; } = "Vision";

        public int ModelCatalogRequests { get; private set; }

        public int LoadRequests { get; private set; }

        public int UnloadRequests { get; private set; }

        public int EmbeddingRequests { get; private set; }

        public int VisionRequests { get; private set; }

        public Task<LmStudioModelList> GetModelsAsync(CancellationToken cancellationToken = default)
        {
            ModelCatalogRequests++;
            return Task.FromResult(Models);
        }

        public Task<LmStudioModelPreparation> LoadModelAsync(
            string modelId,
            int contextLength,
            bool isEmbedding,
            CancellationToken cancellationToken = default)
        {
            LoadRequests++;
            return Task.FromResult(LoadResult);
        }

        public Task<int> UnloadModelsAsync(
            IReadOnlyCollection<string> identifiers,
            bool unloadAll,
            IReadOnlyCollection<string>? preserveModelIds = null,
            CancellationToken cancellationToken = default)
        {
            UnloadRequests++;
            return Task.FromResult(identifiers.Count);
        }

        public Task<IReadOnlyList<IReadOnlyList<double>>> CreateEmbeddingsAsync(
            string modelId,
            IReadOnlyList<string> inputs,
            CancellationToken cancellationToken = default)
        {
            EmbeddingRequests++;
            return Task.FromResult(EmbeddingResult);
        }

        public Task<string> AnalyzeImagesAsync(
            string modelId,
            string prompt,
            IReadOnlyList<string> imagePaths,
            CancellationToken cancellationToken = default)
        {
            VisionRequests++;
            return Task.FromResult(VisionResult);
        }

        public async Task<LmChatResult> CompleteAsync(
            LmStudioNativeAgentRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (request.Progress is not null)
            {
                await request.Progress(
                    new LmStudioNativeAgentProgress(
                        "toolCallGenerationNameReceived",
                        request.RequiredToolName),
                    cancellationToken).ConfigureAwait(false);
            }
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No native SDK response remains.");
            }
            return _responses.Dequeue()(request);
        }
    }
}
