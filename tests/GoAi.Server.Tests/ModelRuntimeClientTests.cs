using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class ModelRuntimeClientTests
{
    private const string NativeGeneralId = "coding/gpt-oss-120b~test";
    private const string NativeQwenId = "coding/qwen3.8-27b~test";
    private const string NativeCoderId = "coding/qwen3-coder-next~test";
    private static readonly string[] RequiredPath = ["path"];
    private static readonly string[] RequiredName = ["name"];
    private static readonly string[] RequiredOperation = ["operation"];

    [Fact]
    public async Task InstalledNativeRuntimeCatalogIsMappedToGoRoles()
    {
        var handler = new NativeRuntimeHandler();
        using var client = CreateClient(new HttpClient(handler));

        var status = await client.GetStatusAsync();

        Assert.True(status.ProviderReachable);
        Assert.Contains(status.Models, model =>
            model.Id == NativeQwenId
            && model.Role == "general"
            && model.Downloaded
            && model.ContextTokens == 262_144);
        Assert.Contains(status.Models, model =>
            model.Id == NativeGeneralId
            && model.Role == "general"
            && model.Loaded);
    }

    [Fact]
    public async Task AlreadyLoadedModelUsesInstanceIdWithoutReload()
    {
        var handler = new NativeRuntimeHandler(returnToolCall: true);
        using var client = CreateClient(new HttpClient(handler));
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
            modelRole: "general",
            reasoningEffort: "high",
            requireToolCall: true,
            requiredToolName: "fs.readText");

        Assert.Equal("fs.readText", Assert.Single(result.ToolCalls).Name);
        Assert.Empty(handler.ModelOperations);
        using var body = JsonDocument.Parse(Assert.Single(handler.ChatBodies));
        Assert.Equal(NativeGeneralId, body.RootElement.GetProperty("model").GetString());
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.True(body.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.False(body.RootElement.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.Equal("high", body.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Equal("required", body.RootElement.GetProperty("tool_choice").GetString());
        var transportName = body.RootElement.GetProperty("tools")[0]
            .GetProperty("function").GetProperty("name").GetString();
        Assert.NotNull(transportName);
        Assert.DoesNotContain('.', transportName);
        Assert.StartsWith("go_fs_readtext_", transportName, StringComparison.Ordinal);
        Assert.Equal(transportName, ModelRuntimeClient.ToTransportToolName("fs.readText"));
    }

    [Fact]
    public void TransportToolNamesStayReadableBoundedAndCollisionSafe()
    {
        var dotted = ModelRuntimeClient.ToTransportToolName("workspace.inspect");
        var underscored = ModelRuntimeClient.ToTransportToolName("workspace_inspect");
        var longName = ModelRuntimeClient.ToTransportToolName(new string('a', 100));

        Assert.StartsWith("go_workspace_inspect_", dotted, StringComparison.Ordinal);
        Assert.NotEqual(dotted, underscored);
        Assert.True(longName.Length <= 63);
        Assert.Matches("^[a-zA-Z0-9_-]+$", longName);
    }

    [Fact]
    public void HashlessNativeRuntimeToolAliasResolvesOnlyWhenUnambiguous()
    {
        var webFetch = ModelRuntimeClient.ToTransportToolName("web.fetch");
        var tools = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [webFetch] = "web.fetch",
        };

        Assert.True(ModelRuntimeClient.TryResolveTransportToolName(
            "go_web_fetch",
            tools,
            out var logicalName));
        Assert.Equal("web.fetch", logicalName);

        tools[ModelRuntimeClient.ToTransportToolName("web_fetch")] = "web_fetch";
        Assert.False(ModelRuntimeClient.TryResolveTransportToolName(
            "go_web_fetch",
            tools,
            out _));
    }

    [Fact]
    public void HashlessNativeRuntimeReasoningEnvelopeResolvesToLogicalToolName()
    {
        var tools = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ModelRuntimeClient.ToTransportToolName("web.fetch")] = "web.fetch",
        };

        var parsed = ModelRuntimeClient.TryParseReasoningToolCall(
            "<tool_call><function=go_web_fetch><parameter=url>https://example.test</parameter></function></tool_call>",
            tools,
            out var call);

        Assert.True(parsed);
        Assert.Equal("web.fetch", call.Name);
        Assert.Equal("https://example.test", call.Arguments.GetProperty("url").GetString());
    }

    [Fact]
    public void FragmentedQwen38ReasoningEnvelopeResolvesLikeNativeRuntimeOutput()
    {
        var tools = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ModelRuntimeClient.ToTransportToolName("web.fetch")] = "web.fetch",
        };
        var reasoning = """
            <tool_call>
            <function=go_web_fetch>
            <parameter=url>
            https://de.wikibooks.org/wiki/Formelsammlung_Physik:_Klassische_Mechanik
            </parameter>
            </function>
            </tool_call>
            """;

        var parsed = ModelRuntimeClient.TryParseReasoningToolCall(
            reasoning,
            tools,
            out var call);

        Assert.True(parsed);
        Assert.Equal("web.fetch", call.Name);
        Assert.Equal(
            "https://de.wikibooks.org/wiki/Formelsammlung_Physik:_Klassische_Mechanik",
            call.Arguments.GetProperty("url").GetString());
    }

    [Fact]
    public void InvalidToolJsonRetryAddsOneCompactProtocolRepairInstruction()
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = "coder-instance",
            ["messages"] = new object[]
            {
                new { role = "system", content = "Kurz." },
                new { role = "user", content = "Erstelle Physik.py." },
            },
            ["stream"] = true,
        };

        var repaired = ModelRuntimeClient.CreateToolProtocolRepairBody(body, "fs.proposeCreate");
        var messages = Assert.IsType<object[]>(repaired["messages"]);
        Assert.Equal(3, messages.Length);
        var repair = JsonSerializer.Serialize(messages[^1]);

        Assert.Contains("fs.proposeCreate", repair, StringComparison.Ordinal);
        Assert.Contains("12.000", repair, StringComparison.Ordinal);
        Assert.Equal("coder-instance", repaired["model"]);
        Assert.Equal(2, Assert.IsType<object[]>(body["messages"]).Length);
    }

    [Fact]
    public void NativeCatalogParsesInstalledRolesWithoutKnownModelAliases()
    {
        using var document = JsonDocument.Parse("""
            {"data":[{"id":"vision/future-model-Q8_0~123","status":{"value":"loaded"}},{"id":"hf/network-model","status":{"value":"unloaded"}}]}
            """);
        var model = Assert.Single(ModelRuntimeClient.ReadRuntimeModels(document.RootElement));
        Assert.Equal("vision/future-model-Q8_0~123", model.Id);
        Assert.Equal(32_768, model.MaximumContextLength);
        Assert.Equal(model.Id, model.InstanceId);
        Assert.True(model.SupportsTools);
        Assert.True(model.SupportsVision);
        Assert.Equal(["none"], model.ReasoningEfforts);
        Assert.Equal("none", model.DefaultReasoningEffort);
    }

    [Fact]
    public async Task Qwen38SwitchUsesDynamicNativeRuntimeKeyWithoutInventingReasoningSupport()
    {
        var handler = new NativeRuntimeHandler(returnToolCall: false);
        using var client = CreateClient(new HttpClient(handler));

        _ = await client.CompleteChatAsync(
            NativeModelCatalog.Qwen38Id,
            [new LmChatMessage("user", "Prüfe das Projekt.")],
            [],
            modelRole: "general",
            reasoningEffort: "none");

        Assert.Equal(2, handler.ModelOperations.Count);
        Assert.Equal("unload:" + NativeGeneralId, handler.ModelOperations[0]);
        Assert.Equal("load:" + NativeQwenId, handler.ModelOperations[1]);
        using var body = JsonDocument.Parse(Assert.Single(handler.ChatBodies));
        Assert.Equal(NativeQwenId, body.RootElement.GetProperty("model").GetString());
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
        Assert.False(body.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
    }

    [Fact]
    public async Task CompletedModelLoadIsRecoveredAfterTransientLoadChannelFailure()
    {
        var handler = new NativeRuntimeHandler(transientLoadChannelFailure: true);
        using var client = CreateClient(new HttpClient(handler));

        var instance = await client.EnsureModelLoadedAsync(
            NativeModelCatalog.Qwen3CoderNextQ8Id,
            32_768);

        Assert.Equal(NativeCoderId, instance);
        Assert.Equal(1, handler.ModelOperations.Count(operation =>
            operation == "load:" + NativeCoderId));
    }

    [Fact]
    public async Task Qwen38RequiredToolTurnUsesConservativeCatalogFallback()
    {
        var handler = new NativeRuntimeHandler(returnToolCall: true);
        using var client = CreateClient(new HttpClient(handler));
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { name = new { type = "string" } },
            required = RequiredName,
        });

        _ = await client.CompleteChatAsync(
            NativeModelCatalog.Qwen38Id,
            [new LmChatMessage("user", "Wähle das Werkzeug.")],
            [new LmToolDefinition("go.selectTool", "Werkzeug wählen", schema)],
            modelRole: "general",
            reasoningEffort: "none",
            requireToolCall: true,
            requiredToolName: "go.selectTool");

        using var body = JsonDocument.Parse(Assert.Single(handler.ChatBodies));
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
        Assert.False(body.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
    }

    [Fact]
    public async Task CompleteReasoningToolEnvelopeIsConvertedToValidatedNativeCall()
    {
        var handler = new NativeRuntimeHandler(reasoningToolCall: true);
        using var client = CreateClient(new HttpClient(handler));
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { operation = new { type = "string" } },
            required = RequiredOperation,
        });

        var result = await client.CompleteChatAsync(
            NativeModelCatalog.Qwen38Id,
            [new LmChatMessage("user", "Untersuche den Workspace.")],
            [new LmToolDefinition("workspace.inspect", "Workspace untersuchen", schema)],
            modelRole: "general",
            reasoningEffort: "none",
            requireToolCall: true);

        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("workspace.inspect", call.Name);
        Assert.Equal("map", call.Arguments.GetProperty("operation").GetString());
        Assert.StartsWith("call-reasoning-", call.Id, StringComparison.Ordinal);
        Assert.True(result.HadReasoning);
    }

    [Fact]
    public void ReasoningToolEnvelopeRejectsUnknownOrMultipleTools()
    {
        var tools = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ModelRuntimeClient.ToTransportToolName("workspace.inspect")] = "workspace.inspect",
        };

        Assert.False(ModelRuntimeClient.TryParseReasoningToolCall(
            "<tool_call><function=workspace.change></function></tool_call>",
            tools,
            out _));
        Assert.False(ModelRuntimeClient.TryParseReasoningToolCall(
            "<tool_call><function=workspace.inspect></function></tool_call>" +
            "<tool_call><function=workspace.inspect></function></tool_call>",
            tools,
            out _));
    }

    [Fact]
    public async Task NativeRuntimeMessageOrderCoalescesInitialSystemAndConvertsLateSystemGuidance()
    {
        var handler = new NativeRuntimeHandler(returnToolCall: false);
        using var client = CreateClient(new HttpClient(handler));

        _ = await client.CompleteChatAsync(
            "gpt-oss-120b",
            [
                new LmChatMessage("system", "Policy"),
                new LmChatMessage("system", "Repositorykarte"),
                new LmChatMessage("user", "Bearbeite den Auftrag."),
                new LmChatMessage("system", "Erzeuge jetzt den erforderlichen Tool-Call."),
            ],
            []);

        using var body = JsonDocument.Parse(Assert.Single(handler.ChatBodies));
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("Policy\n\nRepositorykarte", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("Bearbeite den Auftrag.", messages[1].GetProperty("content").GetString());
        Assert.Equal("user", messages[2].GetProperty("role").GetString());
        Assert.StartsWith("GO-Laufanweisung:\n", messages[2].GetProperty("content").GetString());
        Assert.DoesNotContain(
            messages.EnumerateArray().Skip(1),
            message => string.Equals(
                message.GetProperty("role").GetString(),
                "system",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task QwenCoderNextRejectsUnsupportedReasoningEffort()
    {
        var handler = new NativeRuntimeHandler(returnToolCall: false);
        using var client = CreateClient(new HttpClient(handler));

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CompleteChatAsync(
            NativeModelCatalog.Qwen3CoderNextQ8Id,
            [new LmChatMessage("user", "Prüfe das Projekt.")],
            [],
            modelRole: "general",
            reasoningEffort: "high"));

        Assert.Empty(handler.ChatBodies);
    }

    [Fact]
    public async Task Qwen38CanDisableThinkingWithoutSendingAnInvalidEffort()
    {
        var handler = new NativeRuntimeHandler(returnToolCall: false);
        using var client = CreateClient(new HttpClient(handler));

        _ = await client.CompleteChatAsync(
            NativeModelCatalog.Qwen38Id,
            [new LmChatMessage("user", "Antworte direkt.")],
            [],
            modelRole: "general",
            reasoningEffort: "none");

        using var body = JsonDocument.Parse(Assert.Single(handler.ChatBodies));
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
        Assert.False(body.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
    }

    [Fact]
    public async Task StreamingTurnReportsFinalUsageWithoutPrivateSlotsEndpoint()
    {
        var handler = new NativeRuntimeHandler(returnToolCall: false);
        using var client = CreateClient(new HttpClient(handler));
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

        Assert.Contains(progress, item => item.State == "generationStarted");
        var tokens = Assert.Single(progress, item => item.State == "tokenProgress");
        Assert.Equal(100, tokens.PromptTokens);
        Assert.Equal(20, tokens.GeneratedTokens);
        Assert.Equal(120, tokens.CurrentTokens);
        Assert.DoesNotContain(handler.RequestPaths, path => path.StartsWith("/slots", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VisibleTextFragmentsAreForwardedBeforeTheBufferedResultCompletes()
    {
        var handler = new NativeRuntimeHandler(streamingText: true);
        using var client = CreateClient(new HttpClient(handler));
        var progress = new List<ModelRuntimeProgress>();

        var result = await client.CompleteChatAsync(
            "gpt-oss-120b",
            [new LmChatMessage("user", "BegrÃ¼ÃŸe mich.")],
            [],
            nativeProgress: (value, _) =>
            {
                progress.Add(value);
                return ValueTask.CompletedTask;
            });

        Assert.Equal("Hallo Welt", result.Content);
        Assert.Equal(
            ["Hallo", " Welt"],
            progress.Where(item => item.State == "contentDelta")
                .Select(item => item.ContentDelta!)
                .ToArray());
    }

    [Fact]
    public void VisibleTextGateStreamsPlainTextButSuppressesStructuredEnvelopes()
    {
        var visible = new IncrementalVisibleTextGate(enabled: true);
        Assert.Null(visible.Push("Hallo"));
        var continuation = new string('x', 92);
        Assert.Equal("Hallo" + continuation, visible.Push(continuation));
        Assert.True(visible.HasStreamed);

        var structured = new IncrementalVisibleTextGate(enabled: true);
        Assert.Null(structured.Push("  {\"schema\":"));
        Assert.Null(structured.Push("\"go.ai.agent.response.v1\"}"));
        Assert.Null(structured.Flush());
        Assert.False(structured.HasStreamed);
    }

    [Fact]
    public async Task StreamingToolFragmentsAreBufferedAndValidatedBeforeReturning()
    {
        var handler = new NativeRuntimeHandler(streamingToolCall: true);
        using var client = CreateClient(new HttpClient(handler));
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { operation = new { type = "string" } },
            required = RequiredOperation,
        });
        var progress = new List<ModelRuntimeProgress>();

        var result = await client.CompleteChatAsync(
            "gpt-oss-120b",
            [new LmChatMessage("user", "Untersuche den Workspace.")],
            [new LmToolDefinition("workspace.inspect", "Workspace untersuchen", schema)],
            requireToolCall: true,
            nativeProgress: (value, _) =>
            {
                progress.Add(value);
                return ValueTask.CompletedTask;
            });

        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("workspace.inspect", call.Name);
        Assert.Equal("map", call.Arguments.GetProperty("operation").GetString());
        Assert.Equal(100, result.InputTokens);
        Assert.Equal(20, result.OutputTokens);
        Assert.Equal(7, result.ReasoningTokens);
        Assert.True(result.HadReasoning);
        Assert.Contains(progress, item => item.State == "toolSelected" && item.ToolName == "workspace.inspect");
    }

    [Fact]
    public async Task CompleteStreamingToolJsonSurvivesMissingDoneFrameWithoutRetry()
    {
        var handler = new NativeRuntimeHandler(
            streamingToolCall: true,
            completeToolWithoutDone: true);
        using var client = CreateClient(new HttpClient(handler));
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { operation = new { type = "string" } },
            required = RequiredOperation,
        });
        var progress = new List<ModelRuntimeProgress>();

        var result = await client.CompleteChatAsync(
            "gpt-oss-120b",
            [new LmChatMessage("user", "Untersuche den Workspace.")],
            [new LmToolDefinition("workspace.inspect", "Workspace untersuchen", schema)],
            requireToolCall: true,
            nativeProgress: (value, _) =>
            {
                progress.Add(value);
                return ValueTask.CompletedTask;
            });

        Assert.Equal(1, handler.ChatAttempts);
        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("workspace.inspect", call.Name);
        Assert.Equal("map", call.Arguments.GetProperty("operation").GetString());
        Assert.DoesNotContain(progress, item => item.State == "generationRetry");
    }

    [Fact]
    public async Task StructuredToolOnlyKeepsACompleteToolCallAndDiscardsIncidentalFreeText()
    {
        var handler = new NativeRuntimeHandler(
            streamingToolCall: true,
            completeToolWithoutDone: true,
            streamingToolWithFreeText: true);
        using var client = CreateClient(new HttpClient(handler));
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { operation = new { type = "string" } },
            required = RequiredOperation,
        });
        var progress = new List<ModelRuntimeProgress>();

        var result = await client.CompleteChatAsync(
            "gpt-oss-120b",
            [new LmChatMessage("user", "Untersuche den Workspace.")],
            [new LmToolDefinition("workspace.inspect", "Workspace untersuchen", schema)],
            requireToolCall: true,
            nativeProgress: (value, _) =>
            {
                progress.Add(value);
                return ValueTask.CompletedTask;
            },
            structuredToolOnly: true);

        Assert.Equal(1, handler.ChatAttempts);
        Assert.Null(result.Content);
        Assert.Equal("workspace.inspect", Assert.Single(result.ToolCalls).Name);
        Assert.DoesNotContain(progress, item => item.State == "contentDelta");
        Assert.DoesNotContain(progress, item => item.State == "generationRetry");
    }

    [Fact]
    public async Task TransientInferenceFailureIsRetriedAtMostTwiceBeforeToolExecution()
    {
        var handler = new NativeRuntimeHandler(returnToolCall: false, transientChatFailures: 2);
        using var client = CreateClient(new HttpClient(handler));

        var result = await client.CompleteChatAsync(
            "gpt-oss-120b",
            [new LmChatMessage("user", "Test")],
            []);

        Assert.Equal("Fertig", result.Content);
        Assert.Equal(3, handler.ChatAttempts);
    }

    [Fact]
    public async Task PrematureStreamingToolJsonIsDiagnosedAndRetriedBeforeExecution()
    {
        var handler = new NativeRuntimeHandler(
            streamingToolCall: true,
            prematureStreamingFailures: 2);
        using var client = CreateClient(new HttpClient(handler));
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { operation = new { type = "string" } },
            required = RequiredOperation,
        });
        var progress = new List<ModelRuntimeProgress>();

        var result = await client.CompleteChatAsync(
            "gpt-oss-120b",
            [new LmChatMessage("user", "Untersuche den Workspace.")],
            [new LmToolDefinition("workspace.inspect", "Workspace untersuchen", schema)],
            requireToolCall: true,
            nativeProgress: (value, _) =>
            {
                progress.Add(value);
                return ValueTask.CompletedTask;
            });

        Assert.Equal(3, handler.ChatAttempts);
        Assert.Equal("workspace.inspect", Assert.Single(result.ToolCalls).Name);
        var retries = progress.Where(item => item.State == "generationRetry").ToArray();
        Assert.Equal(2, retries.Length);
        Assert.Collection(
            retries,
            first => AssertRetry(first, 1),
            second => AssertRetry(second, 2));

        static void AssertRetry(ModelRuntimeProgress progress, int attempt)
        {
            Assert.Equal(attempt, progress.Attempt);
            Assert.Equal("premature_eof", progress.FailureKind);
            Assert.Equal("workspace.inspect", progress.ToolName);
            Assert.True(progress.ArgumentCharacters > 0);
            Assert.False(progress.ToolArgumentsJsonComplete);
            Assert.False(progress.FinishObserved);
        }
    }

    [Fact]
    public async Task PrematureStreamingToolJsonExhaustionHasStableProviderCode()
    {
        var handler = new NativeRuntimeHandler(
            streamingToolCall: true,
            prematureStreamingFailures: 3);
        using var client = CreateClient(new HttpClient(handler));
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { operation = new { type = "string" } },
            required = RequiredOperation,
        });
        var progress = new List<ModelRuntimeProgress>();

        var exception = await Assert.ThrowsAsync<ModelGenerationTerminatedException>(() =>
            client.CompleteChatAsync(
                "gpt-oss-120b",
                [new LmChatMessage("user", "Untersuche den Workspace.")],
                [new LmToolDefinition("workspace.inspect", "Workspace untersuchen", schema)],
                requireToolCall: true,
                nativeProgress: (value, _) =>
                {
                    progress.Add(value);
                    return ValueTask.CompletedTask;
                }));

        Assert.Equal("transport_retry_exhausted", exception.ProviderCode);
        Assert.Equal(3, handler.ChatAttempts);
        Assert.Equal([1, 2, 3], progress
            .Where(item => item.State == "generationRetry")
            .Select(item => item.Attempt)
            .ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TransportFailureBeforeResponseReportsItsRealPhaseInsteadOfIncompleteToolCall(bool tokenCounting)
    {
        var handler = new NativeRuntimeHandler(transientChatFailures: tokenCounting ? 0 : 3, tokenCountingFailures: tokenCounting ? 3 : 0);
        using var client = CreateClient(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<ModelProviderRequestException>(() =>
            client.CompleteChatAsync("gpt-oss-120b", [new LmChatMessage("user", "Prüfe das Projekt.")], []));

        Assert.Equal(tokenCounting ? "token_counting" : "generation", exception.Phase);
        Assert.Contains(tokenCounting ? "Prompt-Tokenzählung" : "Modellanfrage", exception.Message);
        Assert.Contains("nach 3 Versuchen", exception.Message);
        Assert.DoesNotContain("Tool-Call", exception.Message);
        Assert.Equal(3, handler.TokenCountingAttempts);
        Assert.Equal(tokenCounting ? 0 : 3, handler.ChatAttempts);
        if (tokenCounting)
        {
            Assert.Equal(HttpStatusCode.InternalServerError, exception.StatusCode);
            Assert.Contains("proxy error: Failed to read connection", exception.Message);
        }
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.RequestTimeout, true)]
    [InlineData(System.Net.HttpStatusCode.TooManyRequests, true)]
    [InlineData(System.Net.HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(System.Net.HttpStatusCode.BadRequest, false)]
    [InlineData(System.Net.HttpStatusCode.Unauthorized, false)]
    public void OnlyTransientProviderFailuresAreRetried(
        System.Net.HttpStatusCode statusCode,
        bool expected)
    {
        var exception = new HttpRequestException("provider failure", null, statusCode);

        Assert.Equal(expected, ModelRuntimeClient.IsTransientInferenceFailure(exception));
    }

    private static ModelRuntimeClient CreateClient(HttpClient http) => new(
        http,
        Options.Create(new GoAiServerOptions
        {
            ModelRuntimeUri = new Uri("http://native.test:8081", UriKind.Absolute),
            GeneralModelId = "gpt-oss-120b",
        }),
        NullLogger<ModelRuntimeClient>.Instance);

    private sealed class NativeRuntimeHandler(
        bool returnToolCall = false,
        int transientChatFailures = 0,
        bool streamingToolCall = false,
        int prematureStreamingFailures = 0,
        bool completeToolWithoutDone = false,
        bool reasoningToolCall = false,
        bool streamingText = false,
        bool transientLoadChannelFailure = false,
        bool streamingToolWithFreeText = false,
        int tokenCountingFailures = 0) : HttpMessageHandler
    {
        private string? _loadedKey = NativeGeneralId;

        public List<string> ChatBodies { get; } = [];
        public List<string> ModelOperations { get; } = [];
        public List<string> RequestPaths { get; } = [];
        public int ChatAttempts { get; private set; }
        public int TokenCountingAttempts { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            RequestPaths.Add(path);
            if (path == "/props") return Json("{\"default_generation_settings\":{\"n_ctx\":32768}}");
            if (path == "/v1/chat/completions/input_tokens")
            {
                TokenCountingAttempts++;
                if (TokenCountingAttempts <= tokenCountingFailures)
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("proxy error: Failed to read connection") };
                return Json("{\"input_tokens\":100}");
            }
            if (request.Method == HttpMethod.Get && path == "/v1/models")
            {
                return Json(JsonSerializer.Serialize(new
                {
                    data = new object[]
                    {
                        Model("gpt-oss-120b"),
                        Model("qwen3.8-27b"),
                        Model("qwen3-coder-next"),
                        Model("qwen3-vl-30b-a3b-instruct"),
                        Model("text-embedding-bge-m3"),
                    },
                }));
            }

            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            if (request.Method == HttpMethod.Post && path == "/models/unload")
            {
                using var operation = JsonDocument.Parse(body);
                var instance = operation.RootElement.GetProperty("model").GetString();
                ModelOperations.Add("unload:" + instance);
                _loadedKey = null;
                return Json("{\"success\":true}");
            }
            if (request.Method == HttpMethod.Post && path == "/models/load")
            {
                using var operation = JsonDocument.Parse(body);
                var requestedModel = operation.RootElement.GetProperty("model").GetString();
                _loadedKey = requestedModel;
                ModelOperations.Add($"load:{requestedModel}");
                if (transientLoadChannelFailure)
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    {
                        Content = new StringContent("runtime channel restarted", Encoding.UTF8, "text/plain"),
                    };
                }
                return Json("{\"success\":true}");
            }
            if (request.Method == HttpMethod.Post && path == "/v1/chat/completions")
            {
                ChatAttempts++;
                if (ChatAttempts <= transientChatFailures)
                {
                    throw new HttpRequestException("transient");
                }
                ChatBodies.Add(body);
                if (ChatAttempts <= prematureStreamingFailures)
                {
                    using var requestBody = JsonDocument.Parse(body);
                    var selectedToolName = requestBody.RootElement.GetProperty("tools")[0]
                        .GetProperty("function").GetProperty("name").GetString()!;
                    return PrematureEventStream(selectedToolName);
                }
                if (streamingToolCall)
                {
                    using var requestBody = JsonDocument.Parse(body);
                    var selectedToolName = requestBody.RootElement.GetProperty("tools")[0]
                        .GetProperty("function").GetProperty("name").GetString()!;
                    return EventStream(selectedToolName, completeToolWithoutDone, streamingToolWithFreeText);
                }
                if (reasoningToolCall)
                {
                    return ReasoningToolEventStream();
                }
                if (streamingText)
                {
                    return TextEventStream();
                }
                if (returnToolCall)
                {
                    using var requestBody = JsonDocument.Parse(body);
                    var selectedToolName = requestBody.RootElement.GetProperty("tools")[0]
                        .GetProperty("function").GetProperty("name").GetString();
                    return Json(JsonSerializer.Serialize(new
                    {
                        choices = new[]
                        {
                            new
                            {
                                message = new
                                {
                                    role = "assistant",
                                    content = (string?)null,
                                    tool_calls = new[]
                                    {
                                        new
                                        {
                                            id = "call-1",
                                            type = "function",
                                            function = new
                                            {
                                                name = selectedToolName,
                                                arguments = "{\"path\":\"README.md\"}",
                                            },
                                        },
                                    },
                                },
                            },
                        },
                        usage = new { prompt_tokens = 100, completion_tokens = 20 },
                    }));
                }
                return Json("""
                    {"choices":[{"message":{"role":"assistant","content":"Fertig"}}],"usage":{"prompt_tokens":100,"completion_tokens":20}}
                    """);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private object Model(string key)
        {
            var id = key switch
            {
                "text-embedding-bge-m3" => "embedding/bge-m3~test",
                "qwen3-vl-30b-a3b-instruct" => "vision/qwen3-vl-30b-a3b-instruct~test",
                _ => "coding/" + key + "~test",
            };
            return new { id, status = new { value = _loadedKey == id ? "loaded" : "unloaded" } };
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        private static HttpResponseMessage EventStream(
            string toolName,
            bool omitCompletion = false,
            bool includeFreeText = false)
        {
            var chunks = new List<object>
            {
                new { choices = new[] { new { index = 0, delta = (object)new { role = "assistant", reasoning_content = "prüfen" }, finish_reason = (string?)null } } },
                new { choices = new[] { new { index = 0, delta = (object)new { tool_calls = new[] { new { index = 0, id = "call-stream", type = "function", function = new { name = toolName, arguments = "" } } } }, finish_reason = (string?)null } } },
                new { choices = new[] { new { index = 0, delta = (object)new { tool_calls = new[] { new { index = 0, type = "function", function = new { arguments = "{\"operation\":" } } } }, finish_reason = (string?)null } } },
                new { choices = new[] { new { index = 0, delta = (object)new { tool_calls = new[] { new { index = 0, type = "function", function = new { arguments = "\"map\"}" } } } }, finish_reason = (string?)null } } },
            };
            if (includeFreeText)
            {
                chunks.Insert(1, new { choices = new[] { new { index = 0, delta = (object)new { content = "Interner Begleittext" }, finish_reason = (string?)null } } });
            }
            if (!omitCompletion)
            {
                chunks.Add(new { choices = new[] { new { index = 0, delta = (object)new { }, finish_reason = (string?)"tool_calls" } } });
            }
            var builder = new StringBuilder();
            foreach (var chunk in chunks)
            {
                builder.Append("data: ").Append(JsonSerializer.Serialize(chunk)).Append("\n\n");
            }
            if (!omitCompletion)
            {
                builder.Append("data: ")
                    .Append(JsonSerializer.Serialize(new
                    {
                        choices = Array.Empty<object>(),
                        usage = new
                        {
                            prompt_tokens = 100,
                            completion_tokens = 20,
                            completion_tokens_details = new { reasoning_tokens = 7 },
                        },
                    }))
                    .Append("\n\ndata: [DONE]\n\n");
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(builder.ToString(), Encoding.UTF8, "text/event-stream"),
            };
        }

        private static HttpResponseMessage PrematureEventStream(string toolName)
        {
            var chunks = new[]
            {
                new { choices = new[] { new { index = 0, delta = (object)new { role = "assistant", reasoning_content = "prüfen" }, finish_reason = (string?)null } } },
                new { choices = new[] { new { index = 0, delta = (object)new { tool_calls = new[] { new { index = 0, id = "call-stream", type = "function", function = new { name = toolName, arguments = "" } } } }, finish_reason = (string?)null } } },
                new { choices = new[] { new { index = 0, delta = (object)new { tool_calls = new[] { new { index = 0, type = "function", function = new { arguments = "{\"operation\":" } } } }, finish_reason = (string?)null } } },
            };
            var builder = new StringBuilder();
            foreach (var chunk in chunks)
            {
                builder.Append("data: ").Append(JsonSerializer.Serialize(chunk)).Append("\n\n");
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(builder.ToString(), Encoding.UTF8, "text/event-stream"),
            };
        }

        private static HttpResponseMessage ReasoningToolEventStream()
        {
            var chunks = new object[]
            {
                new { choices = new[] { new { index = 0, delta = (object)new { role = "assistant", reasoning_content = "Ich prÃ¼fe den Workspace.\n<tool_call>\n<function=workspace.inspect>\n" }, finish_reason = (string?)null } } },
                new { choices = new[] { new { index = 0, delta = (object)new { reasoning_content = "<parameter=operation>\nmap\n</parameter>\n" }, finish_reason = (string?)null } } },
                new { choices = new[] { new { index = 0, delta = (object)new { reasoning_content = "</function>\n</tool_call>" }, finish_reason = (string?)null } } },
                new { choices = new[] { new { index = 0, delta = (object)new { }, finish_reason = (string?)"stop" } } },
            };
            var builder = new StringBuilder();
            foreach (var chunk in chunks)
            {
                builder.Append("data: ").Append(JsonSerializer.Serialize(chunk)).Append("\n\n");
            }
            builder.Append("data: ")
                .Append(JsonSerializer.Serialize(new
                {
                    choices = Array.Empty<object>(),
                    usage = new
                    {
                        prompt_tokens = 120,
                        completion_tokens = 32,
                        completion_tokens_details = new { reasoning_tokens = 32 },
                    },
                }))
                .Append("\n\ndata: [DONE]\n\n");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(builder.ToString(), Encoding.UTF8, "text/event-stream"),
            };
        }

        private static HttpResponseMessage TextEventStream()
        {
            var chunks = new object[]
            {
                new { choices = new[] { new { index = 0, delta = (object)new { role = "assistant", content = "Hallo" }, finish_reason = (string?)null } } },
                new { choices = new[] { new { index = 0, delta = (object)new { content = " Welt" }, finish_reason = (string?)null } } },
                new { choices = new[] { new { index = 0, delta = (object)new { }, finish_reason = (string?)"stop" } } },
            };
            var builder = new StringBuilder();
            foreach (var chunk in chunks)
            {
                builder.Append("data: ").Append(JsonSerializer.Serialize(chunk)).Append("\n\n");
            }
            builder.Append("data: ")
                .Append(JsonSerializer.Serialize(new
                {
                    choices = Array.Empty<object>(),
                    usage = new { prompt_tokens = 15, completion_tokens = 2 },
                }))
                .Append("\n\ndata: [DONE]\n\n");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(builder.ToString(), Encoding.UTF8, "text/event-stream"),
            };
        }
    }
}
