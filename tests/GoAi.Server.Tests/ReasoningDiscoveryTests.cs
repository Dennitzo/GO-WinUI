using System.Net;
using System.Text;
using System.Text.Json;
using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GoAi.Server.Tests;

public sealed class ReasoningDiscoveryTests
{
    [Theory]
    [InlineData("{% if resolved_reasoning_effort not in ('xhigh', 'medium', 'low') %}{% endif %} enable_thinking", "llama-native", "xhigh")]
    [InlineData("{% if reasoning_strength in ['low', 'medium', 'high'] %}{% endif %}", "llama-native", "high")]
    [InlineData("{% if enable_thinking %}<think>{% endif %}", "llama-toggle", "on")]
    public void NativeTemplateDeterminesControlWithoutModelName(string template, string family, string highest)
    {
        var profile = ModelRuntimeClient.ReadReasoningMetadata(JsonSerializer.SerializeToElement(new { chat_template = template }));
        Assert.NotNull(profile);
        Assert.Equal(family, profile.Family);
        Assert.Equal(highest, profile.DefaultEffort);
    }

    [Theory]
    [InlineData("custom_level")]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("xhigh")]
    [InlineData("none")]
    public async Task UnknownModelUsesDiscoveredLevelsInTheRealNativeRequest(string requested)
    {
        using var handler = new DiscoveryHandler();
        using var http = new HttpClient(handler);
        using var runtime = new ModelRuntimeClient(http, Options.Create(new GoAiServerOptions { ModelRuntimeUri = new("http://native.test") }), NullLogger<ModelRuntimeClient>.Instance);
        var snapshot = await runtime.GetStatusAsync();
        var coding = Assert.Single(snapshot.Models, model => model.Role == "coding");
        Assert.Contains("custom_level", coding.ReasoningEfforts!);
        var result = await runtime.CompleteChatAsync(DiscoveryHandler.Model, [new("user", "Test")], [],
            modelRole: "coding", reasoningEffort: requested);
        Assert.Equal("Done", result.Content);
        Assert.Equal(requested, handler.Body!.Value.GetProperty("reasoning_effort").GetString());
        var system = handler.Body.Value.GetProperty("messages")[0];
        Assert.Equal("system", system.GetProperty("role").GetString());
        Assert.Contains("durchgehend auf Deutsch", system.GetProperty("content").GetString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.CompleteChatAsync(DiscoveryHandler.Model,
            [new("user", "Test")], [], modelRole: "coding", reasoningEffort: "not_supported"));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public void LanguageRuleIsFirstAndStableWithoutMutatingHistoryOrToolResults()
    {
        LmChatMessage[] original = [new("system", "Existing policy"), new("user", "Please inspect English source."),
            new("tool", "English diagnostic output", ToolCallId: "call1")];
        var prepared = ModelRuntimeClient.PrepareLanguageBoundMessages(original);
        Assert.StartsWith(GoAi.Server.Core.Coding.CodingAgentPolicy.ReasoningLanguagePrompt, prepared[0].Content);
        Assert.Equal("Existing policy", original[0].Content);
        Assert.Equal(original[2], prepared[2]);
        Assert.Equal(prepared, ModelRuntimeClient.PrepareLanguageBoundMessages(prepared));
    }

    private sealed class DiscoveryHandler : HttpMessageHandler
    {
        internal const string Model = "coding/never-before-seen-model~metadata";
        internal JsonElement? Body;
        internal int Requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            var response = path switch
            {
                "/v1/models" => """{"data":[{"id":"coding/never-before-seen-model~metadata","status":{"value":"loaded"},"tags":["go-reasoning-mode:llama-native","go-reasoning-levels:none|low|medium|xhigh|custom_level","go-reasoning-default:low"]}]}""",
                "/props" => """{"default_generation_settings":{"n_ctx":32768}}""",
                "/v1/chat/completions/input_tokens" => """{"input_tokens":20}""",
                "/v1/chat/completions" => """{"choices":[{"message":{"content":"Done"},"finish_reason":"stop"}]}""",
                _ => throw new InvalidOperationException(path),
            };
            if (path == "/v1/chat/completions")
            {
                Requests++;
                Body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(token));
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
