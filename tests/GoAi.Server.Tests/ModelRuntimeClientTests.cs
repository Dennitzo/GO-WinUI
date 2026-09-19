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
    public async Task SessionCacheIsPreparedBeforeInferenceAndSavedBeforeReleasingTurn()
    {
        var handler = new NativeRuntimeHandler();
        using var client = CreateClient(new HttpClient(handler));
        await client.CompleteChatAsync("gpt-oss-120b", [new LmChatMessage("user", "Hallo")], [], sessionCacheKey: "session/workspace");
        Assert.True(handler.RequestPaths.IndexOf("/sessions/prepare") < handler.RequestPaths.IndexOf("/v1/chat/completions"));
        Assert.True(handler.RequestPaths.IndexOf("/sessions/save") > handler.RequestPaths.IndexOf("/v1/chat/completions"));
        Assert.Equal("session/workspace", handler.CacheBodies[0].GetProperty("sessionKey").GetString());
        Assert.Equal("session/workspace", handler.CacheBodies[1].GetProperty("sessionKey").GetString());
        await client.CompleteChatAsync("gpt-oss-120b", [new LmChatMessage("user", "Nebenaufgabe")], []);
        Assert.Equal(JsonValueKind.Null, handler.CacheBodies[^1].GetProperty("sessionKey").ValueKind);
    }

    [Fact]
    public async Task BareLegacyRouterDoesNotRequireSessionPersistenceForStatelessTurns()
    {
        var handler = new NativeRuntimeHandler();
        using var client = CreateClient(new HttpClient(handler));
        await client.CompleteChatAsync("gpt-oss-120b", [new LmChatMessage("user", "Nebenaufgabe")], []);
        Assert.Empty(handler.CacheBodies);
    }

    [Fact]
    public async Task RestartedClientPreservesTheWorkersResidentSessionBeforeItsFirstStatelessPrompt()
    {
        // The HTTP handler represents one surviving supervisor/router. Its
        // resident slot outlives both independent ModelRuntimeClient instances.
        string? resident = null;
        string? slot = null;
        var snapshots = new Dictionary<string, string>();
        var beforeGeneration = new List<string?>();
        NativeRuntimeHandler? handler = null;
        handler = new NativeRuntimeHandler
        {
            ManagedGpuRuntime = true,
            OnCacheRequest = (path, _) =>
            {
                var key = handler!.CacheBodies[^1].GetProperty("sessionKey").GetString();
                if (path == "/sessions/prepare" && key != resident)
                {
                    if (resident is not null && slot is not null) snapshots[resident] = slot;
                    resident = key;
                    if (key is not null && snapshots.TryGetValue(key, out var restored)) slot = restored;
                }
                else if (path == "/sessions/save" && resident is not null && slot is not null)
                    snapshots[resident] = slot;
                return Task.CompletedTask;
            },
            OnChatRequest = body => { beforeGeneration.Add(slot); slot = body; },
        };
        using (var first = CreateClient(new HttpClient(handler, disposeHandler: false)))
            await first.CompleteChatAsync("gpt-oss-120b", [new("user", "Sitzung A: unveränderter Kontext")], [], sessionCacheKey: "session-A");
        var original = Assert.Single(handler.ChatBodies);
        using (var restarted = CreateClient(new HttpClient(handler, disposeHandler: false)))
        {
            await restarted.CompleteChatAsync("gpt-oss-120b", [new("user", "Unabhängige Hilfszusammenfassung")], []);
            Assert.Null(resident);
            Assert.Equal(original, snapshots["session-A"]);
            await restarted.CompleteChatAsync("gpt-oss-120b", [new("user", "Sitzung A: Fortsetzung")], [], sessionCacheKey: "session-A");
        }
        Assert.Equal(original, beforeGeneration[2]);
        Assert.Equal(JsonValueKind.Null, handler.CacheBodies[2].GetProperty("sessionKey").ValueKind);
        handler.Dispose();
    }

    [Fact]
    public async Task ManagedLegacySupervisorWithoutSessionEndpointStillAllowsFirstStatelessTurn()
    {
        var handler = new NativeRuntimeHandler
        {
            ManagedGpuRuntime = true,
            OnCacheRequest = (_, _) => throw new HttpRequestException("Legacy supervisor has no session API", null, HttpStatusCode.NotFound),
        };
        using var client = CreateClient(new HttpClient(handler));

        var response = await client.CompleteChatAsync("gpt-oss-120b", [new("user", "Stateless Frage")], []);

        Assert.NotNull(response.Content);
        Assert.Single(handler.CacheBodies);
        Assert.Equal(JsonValueKind.Null, handler.CacheBodies[0].GetProperty("sessionKey").ValueKind);
        Assert.Equal(1, handler.ChatAttempts);
    }

    [Fact]
    public async Task SessionCacheProgrammingErrorsAreNeverTreatedAsLegacyCompatibility()
    {
        var handler = new NativeRuntimeHandler
        {
            ManagedGpuRuntime = true,
            OnCacheRequest = (_, _) => throw new InvalidOperationException("Unexpected fake endpoint or programming error"),
        };
        using var client = CreateClient(new HttpClient(handler));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CompleteChatAsync("gpt-oss-120b", [new("user", "Frage")], []));
        Assert.Equal(0, handler.ChatAttempts);
    }

    [Fact]
    public async Task UnsupportedLegacySessionEndpointFallsBackToCompleteConversation()
    {
        var handler = new NativeRuntimeHandler
        {
            OnCacheRequest = (_, _) => throw new HttpRequestException("Legacy runtime has no session API", null, HttpStatusCode.NotFound),
        };
        using var client = CreateClient(new HttpClient(handler));
        var response = await client.CompleteChatAsync("gpt-oss-120b",
            [new LmChatMessage("user", "Erste Frage"), new LmChatMessage("assistant", "Erste Antwort"), new LmChatMessage("user", "Folgefrage")],
            [], sessionCacheKey: "persisted-session");
        Assert.NotNull(response.Content);
        Assert.Contains("Erste Antwort", Assert.Single(handler.ChatBodies));
        Assert.Equal(1, handler.ChatAttempts);
    }

    [Fact]
    public async Task CancelledTurnDoesNotStartSessionCacheOperation()
    {
        var handler = new NativeRuntimeHandler();
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CompleteChatAsync(
            "gpt-oss-120b", [new LmChatMessage("user", "Abgebrochen")], [],
            sessionCacheKey: "session/workspace", cancellationToken: cancellation.Token));
        Assert.Empty(handler.RequestPaths);
    }

    [Theory]
    [InlineData("prepare")]
    [InlineData("save")]
    public async Task CancellationWaitsForDispatchedCacheOperationBeforeReleasingModelTurn(string operation)
    {
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var intercepted = 0;
        var handler = new NativeRuntimeHandler
        {
            OnCacheRequest = async (path, token) =>
            {
                if (path == "/sessions/" + operation && Interlocked.Increment(ref intercepted) == 1)
                {
                    entered.SetResult(token);
                    await release.Task.WaitAsync(token);
                }
            },
        };
        using var client = CreateClient(new HttpClient(handler));
        using var cancellation = new CancellationTokenSource();
        var first = client.CompleteChatAsync("gpt-oss-120b", [new LmChatMessage("user", "Erster Auftrag")], [],
            sessionCacheKey: "session/workspace", cancellationToken: cancellation.Token);
        var controlToken = await entered.Task;
        await cancellation.CancelAsync();
        Assert.False(controlToken.IsCancellationRequested);
        Assert.False(first.IsCompleted);
        var pathsBeforeSecond = handler.RequestPaths.Count;
        var second = client.CompleteChatAsync("gpt-oss-120b", [new LmChatMessage("user", "Neuer Auftrag")], []);
        Assert.False(second.IsCompleted);
        Assert.Equal(pathsBeforeSecond, handler.RequestPaths.Count);
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await first);
        await second;
        Assert.Equal(operation == "save" ? 2 : 1, handler.ChatAttempts);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Prüfung")]
    public async Task ToolHistoryNeverSendsNullTextToNativeParser(string? reasoning)
    {
        var handler = new NativeRuntimeHandler();
        using var client = CreateClient(new HttpClient(handler));
        await client.CompleteChatAsync("gpt-oss-120b",
            [new LmChatMessage("user", "Prüfe die Datei"),
             new LmChatMessage("assistant", null,
                 [new LmToolCall("call-test", "fs.readText", JsonSerializer.SerializeToElement(new { path = "README.md" }))],
                 ReasoningContent: reasoning),
             new LmChatMessage("tool", "Dateiinhalt", ToolCallId: "call-test"),
             new LmChatMessage("user", "Weiter")],
            [], modelRole: "general");
        using var body = JsonDocument.Parse(Assert.Single(handler.ChatBodies));
        var assistant = body.RootElement.GetProperty("messages").EnumerateArray()
            .Single(message => message.TryGetProperty("tool_calls", out _));
        Assert.Equal("", assistant.GetProperty("content").GetString());
        Assert.Equal(reasoning ?? "", assistant.GetProperty("reasoning_content").GetString());
    }

    [Theory]
    [InlineData(NativeGeneralId, "general")]
    [InlineData(NativeGeneralId, "coding")]
    [InlineData(NativeQwenId, "general")]
    [InlineData(NativeQwenId, "coding")]
    [InlineData(NativeCoderId, "coding")]
    public async Task GermanReasoningInstructionAndCacheFlagReachEveryTextModelRole(string model, string role)
    {
        var handler = new NativeRuntimeHandler();
        using var client = CreateClient(new HttpClient(handler));
        await client.CompleteChatAsync(model, [new LmChatMessage("user", "Explain this English source code.")], [], modelRole: role);
        using var body = JsonDocument.Parse(Assert.Single(handler.ChatBodies));
        var messages = body.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Contains("Deutsch", messages[0].GetProperty("content").GetString());
        Assert.Contains("ausschließlich auf Deutsch", messages[^1].GetProperty("content").GetString());
        Assert.Contains(messages, message => message.GetProperty("content").GetString() == "Explain this English source code.");
        Assert.True(body.RootElement.GetProperty("cache_prompt").GetBoolean());
    }

    [Fact]
    public void GermanLanguagePreparationIsIdempotentAndPreservesHistoricalReasoningExactly()
    {
        var messages = new LmChatMessage[] { new("system", "Aktuelle Policy"), new("user", "Hello"),
            new("assistant", "Done", ReasoningContent: "Original historical reasoning."), new("user", "Weiter") };
        var first = ModelRuntimeClient.PrepareLanguageBoundMessages(messages);
        var repeated = ModelRuntimeClient.PrepareLanguageBoundMessages(first);
        Assert.Equal(first, repeated);
        Assert.Equal("Original historical reasoning.", repeated.Single(message => message.Role == "assistant").ReasoningContent);
        Assert.Single(repeated, ModelRuntimeClient.IsLanguageReminder);
    }

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
    public async Task ModelSwitchWaitsForActualUnloadBeforeRequestingNewGpuPlacement()
    {
        var handler = new NativeRuntimeHandler { DelayedUnloadPolls = 3 };
        using var client = CreateClient(new HttpClient(handler));
        await client.CompleteChatAsync(NativeQwenId, [new LmChatMessage("user", "Modell wechseln")], []);
        Assert.Equal(3, handler.CatalogReadsWhileUnloading);
        Assert.Equal(new[] { "unload:" + NativeGeneralId, "load:" + NativeQwenId }, handler.ModelOperations);
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
        Assert.True(ModelRuntimeClient.TryParseReasoningToolCall(
            "<tool_call><function=go_web_fetch><parameter=url>https://example.test</parameter></function></tool_call>",
            tools, out var repeated));
        Assert.NotEqual(call.Id, repeated.Id);
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
        Assert.Empty(model.ReasoningEfforts);
        Assert.Null(model.DefaultReasoningEffort);
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
        Assert.Equal(4, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.EndsWith("Policy\n\nRepositorykarte\n\n" + ModelRuntimeClient.DirectUserInstructionPolicy,
            messages[0].GetProperty("content").GetString());
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReasoningIsLosslesslyBatchedAndKeptSeparateFromAnswer(bool jsonResponse)
    {
        var handler = new NativeRuntimeHandler(streamingReasoning: !jsonResponse, jsonReasoning: jsonResponse);
        using var client = CreateClient(new HttpClient(handler));
        var progress = new List<ModelRuntimeProgress>();
        var result = await client.CompleteChatAsync("gpt-oss-120b", [new LmChatMessage("user", "Prüfe.")], [],
            nativeProgress: (value, _) => { progress.Add(value); return ValueTask.CompletedTask; });

        Assert.Equal("Fertig", result.Content);
        Assert.True(result.HadReasoning);
        Assert.Equal(NativeRuntimeHandler.ReasoningText, result.ReasoningContent);
        Assert.Equal(NativeRuntimeHandler.ReasoningText,
            string.Concat(progress.Select(item => item.ReasoningDelta)));
        Assert.All(progress.Where(item => item.ReasoningDelta is not null), item => Assert.Null(item.ContentDelta));
        if (!jsonResponse)
        {
            Assert.Equal("Fertig", string.Concat(progress.Select(item => item.ContentDelta)));
            Assert.True(progress.FindLastIndex(item => item.ReasoningDelta is not null)
                < progress.FindIndex(item => item.ContentDelta is not null));
            Assert.True(progress.Count(item => item.ReasoningDelta is not null) < NativeRuntimeHandler.ReasoningText.Length / 2);
        }
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
        Assert.Equal(7, result.Metrics?.ReasoningTokens);
        Assert.NotNull(result.Metrics?.TimeToFirstTokenMilliseconds);
        Assert.NotNull(result.Metrics?.TokenCountingMilliseconds);
        Assert.NotNull(result.Metrics?.RuntimeQueueMilliseconds);
        Assert.NotNull(result.Metrics?.TotalMilliseconds);
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
        int tokenCountingFailures = 0,
        bool streamingReasoning = false,
        bool jsonReasoning = false) : HttpMessageHandler
    {
        internal const string ReasoningText = "## Plan\n\n1. Datei prüfen.\n2. `änderung` anwenden.\n\nAbschließend testen.";
        private string? _loadedKey = NativeGeneralId;
        private int _remainingUnloadPolls;

        public List<string> ChatBodies { get; } = [];
        public List<string> ModelOperations { get; } = [];
        public List<string> RequestPaths { get; } = [];
        public List<JsonElement> CacheBodies { get; } = [];
        public int ChatAttempts { get; private set; }
        public int TokenCountingAttempts { get; private set; }
        public Func<string, CancellationToken, Task>? OnCacheRequest { get; init; }
        public Action<string>? OnChatRequest { get; init; }
        public bool ManagedGpuRuntime { get; init; }
        public int DelayedUnloadPolls { get; init; }
        public int CatalogReadsWhileUnloading { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            RequestPaths.Add(path);
            if (path.StartsWith("/sessions/", StringComparison.Ordinal))
            {
                CacheBodies.Add(JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(cancellationToken)));
                if (OnCacheRequest is not null) await OnCacheRequest(path, cancellationToken);
                return Json("{\"status\":\"resident\"}");
            }
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
                if (_remainingUnloadPolls > 0)
                {
                    CatalogReadsWhileUnloading++;
                    if (--_remainingUnloadPolls == 0) _loadedKey = null;
                }
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
                _remainingUnloadPolls = DelayedUnloadPolls;
                if (_remainingUnloadPolls == 0) _loadedKey = null;
                return Json("{\"success\":true}");
            }
            if (request.Method == HttpMethod.Post && path == "/models/load")
            {
                if (_remainingUnloadPolls > 0)
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("Unload the previous model before selecting GPU placement") };
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
                OnChatRequest?.Invoke(body);
                ChatBodies.Add(body);
                if (streamingReasoning)
                {
                    var frames = new StringBuilder();
                    frames.Append("data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"\\n \"}}]}\n\n");
                    foreach (var character in ReasoningText)
                        frames.Append("data: ").Append(JsonSerializer.Serialize(new
                        {
                            choices = new[] { new { delta = new { reasoning_content = character.ToString(), reasoning = "duplicate alias" } } },
                        })).Append("\n\n");
                    frames.Append("data: {\"choices\":[{\"delta\":{\"content\":\"Fertig\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");
                    return new(HttpStatusCode.OK) { Content = new StringContent(frames.ToString(), Encoding.UTF8, "text/event-stream") };
                }
                if (jsonReasoning)
                    return Json(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = "Fertig", reasoning = ReasoningText } } } }));
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
            return new { id, status = new { value = _loadedKey == id ? "loaded" : "unloaded" },
                tags = ManagedGpuRuntime ? new[] { "go-gpu-policy:single-preferred-v1" } : Array.Empty<string>() };
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
