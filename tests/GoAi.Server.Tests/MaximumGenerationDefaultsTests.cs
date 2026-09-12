using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class MaximumGenerationDefaultsTests
{
    [Theory]
    [InlineData("coding/gpt-oss-120b~test", "general", "high", 131_072)]
    [InlineData("coding/gpt-oss-20b~test", "coding", "high", 131_072)]
    [InlineData("coding/Qwen3.8-27B~test", "coding", "xhigh", 262_144)]
    [InlineData("coding/Qwen3.8-Flash-Next~test", "general", "xhigh", 262_144)]
    [InlineData("vision/Qwen3.8-27B~test", "vision", "xhigh", 262_144)]
    [InlineData("vision/Qwen3VL-30B-Instruct~test", "vision", "none", 262_144)]
    public async Task DefaultsUseRealHighestReasoningAndAllRemainingNativeContext(
        string modelId, string role, string expectedEffort, int context)
    {
        using var handler = new BudgetHandler(modelId, context, 7_123);
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        var tool = new LmToolDefinition("web.search", "Find primary sources", JsonSerializer.SerializeToElement(new
        {
            type = "object", properties = new { query = new { type = "string" } },
        }));
        _ = await client.CompleteChatAsync(modelId, [new("user", "Find the answer")], [tool], modelRole: role);

        var body = Assert.Single(handler.ChatBodies);
        var countBody = Assert.Single(handler.CountBodies);
        Assert.Equal(context - 7_123 - 1, body.GetProperty("max_tokens").GetInt32());
        Assert.Equal(countBody.GetProperty("messages").GetRawText(), body.GetProperty("messages").GetRawText());
        Assert.Equal(countBody.GetProperty("tools").GetRawText(), body.GetProperty("tools").GetRawText());
        Assert.False(countBody.TryGetProperty("max_tokens", out _));
        if (expectedEffort == "high") Assert.Equal("high", body.GetProperty("reasoning_effort").GetString());
        else if (expectedEffort == "xhigh")
        {
            Assert.Equal("xhigh", body.GetProperty("chat_template_kwargs").GetProperty("reasoning_effort").GetString());
            Assert.True(body.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
            Assert.Equal(countBody.GetProperty("chat_template_kwargs").GetRawText(), body.GetProperty("chat_template_kwargs").GetRawText());
        }
        else
        {
            Assert.False(body.TryGetProperty("reasoning_effort", out _));
            Assert.False(body.TryGetProperty("chat_template_kwargs", out _));
        }
        if (expectedEffort != "none") Assert.Equal(context - 7_124, body.GetProperty("reasoning_budget_tokens").GetInt32());
        var status = await client.GetStatusAsync();
        Assert.All(status.Models, model => Assert.Equal(expectedEffort, model.DefaultReasoningEffort));
    }

    [Theory]
    [InlineData(null, 100_000)]
    [InlineData("auto", 100_000)]
    [InlineData("low", 4_096)]
    [InlineData("none", 100_000)]
    public async Task CodingHonorsExplicitLimitsAndNeverOverridesReasoningWithThinkingOff(string? effort, int outputLimit)
    {
        const string modelId = "coding/Qwen3.8-27B~test";
        using var handler = new BudgetHandler(modelId, 65_536, 10_000);
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        _ = await client.CompleteChatAsync(modelId, [new("user", "Work")], [], maximumOutputTokens: outputLimit,
            modelRole: "coding", reasoningEffort: effort, requiredContextLength: 262_144);
        var body = Assert.Single(handler.ChatBodies);
        Assert.Equal(Math.Min(outputLimit, 55_535), body.GetProperty("max_tokens").GetInt32());
        var kwargs = body.GetProperty("chat_template_kwargs");
        Assert.Equal(effort != "none", kwargs.GetProperty("enable_thinking").GetBoolean());
        if (effort != "none") Assert.Equal(effort == "low" ? "low" : "xhigh", kwargs.GetProperty("reasoning_effort").GetString());
        Assert.All((await client.GetStatusAsync()).Models, model => Assert.Equal(65_536, model.ContextTokens));
    }

    [Fact]
    public async Task ExactFullPromptFailsBeforeAnyGenerationWithoutInventingAnOutputAllowance()
    {
        const string modelId = "coding/gpt-oss-120b~test";
        using var handler = new BudgetHandler(modelId, 32_768, 32_767);
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CompleteChatAsync(modelId, [new("user", "Full")], []));
        Assert.Contains("kein Platz", error.Message);
        Assert.Empty(handler.ChatBodies);
        Assert.Single(handler.CountBodies);
    }

    [Fact]
    public async Task CancellationDuringTokenCountingPreventsGeneration()
    {
        const string modelId = "coding/gpt-oss-120b~test";
        using var handler = new BudgetHandler(modelId, 131_072, 100) { HoldCount = true };
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        using var cancellation = new CancellationTokenSource();
        var operation = client.CompleteChatAsync(modelId, [new("user", "Cancel")], [], cancellationToken: cancellation.Token);
        await handler.CountEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Empty(handler.ChatBodies);
    }

    [Fact]
    public void AutomaticAndLargeExplicitOutputCeilingsDoNotEvictTheInputContext()
    {
        LmChatMessage[] messages = [new("system", "Policy"), new("user", new string('a', 60_000))];
        var automatic = ContextPlanner.Prepare(messages, 32_768, null);
        var explicitMaximum = ContextPlanner.Prepare(messages, 32_768, 262_144);
        Assert.False(automatic.WasCompacted);
        Assert.False(explicitMaximum.WasCompacted);
        Assert.Equal(messages, automatic.Messages);
        Assert.Equal(messages, explicitMaximum.Messages);
    }

    [Fact]
    public void ContextPlanningUsesTheFittedWindowAndPreservesASmallerExplicitRequest()
    {
        LmChatMessage[] history = [new("assistant", new string('a', 60_000)), new("user", "Continue")];
        Assert.False(ContextPlanner.Prepare(history, 262_144, null).WasCompacted);
        var actual = RunProcessor.ResolveLoadedContextLength(262_144, new("native", true, 16_384));
        var plan = ContextPlanner.Prepare(history, actual, null);
        Assert.Equal(16_384, actual);
        Assert.True(plan.WasCompacted);
        Assert.True(plan.EstimatedInputTokens <= plan.InputTokenBudget);
        Assert.Equal(8_192, RunProcessor.ResolveLoadedContextLength(8_192, new("native", true, 16_384)));
    }

    [Fact]
    public async Task VisionWithSmallerModelMaximumUsesItsActualWindowAndCountsImagePayload()
    {
        const string modelId = "vision/small-instruct~test";
        using var handler = new BudgetHandler(modelId, 8_192, 700);
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        var imagePath = Path.Combine(Path.GetTempPath(), "go-vision-budget-" + Guid.NewGuid().ToString("N") + ".png");
        await File.WriteAllBytesAsync(imagePath, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII="));
        try
        {
            Assert.Equal("OK", await client.AnalyzeImagesAsync(modelId, "Describe the image", [imagePath]));
            var body = Assert.Single(handler.ChatBodies);
            Assert.Equal(7_491, body.GetProperty("max_tokens").GetInt32());
            Assert.Equal(Assert.Single(handler.CountBodies).GetProperty("messages").GetRawText(), body.GetProperty("messages").GetRawText());
            var content = body.GetProperty("messages")[1].GetProperty("content");
            Assert.StartsWith("data:image/png;base64,", content[1].GetProperty("image_url").GetProperty("url").GetString(), StringComparison.Ordinal);
        }
        finally { File.Delete(imagePath); }
    }

    [Theory]
    [InlineData(262_144, 1_000, null, 261_143)]
    [InlineData(131_072, 100, 512, 512)]
    [InlineData(32_768, 100, int.MaxValue, 32_667)]
    public void OutputBudgetHasNoGlobalSixtyFourKCeiling(int context, int input, int? limit, int expected) =>
        Assert.Equal(expected, ModelRuntimeClient.ResolveMaximumOutputTokens(context, input, limit));

    private static ModelRuntimeClient CreateClient(HttpClient http) => new(http,
        Options.Create(new GoAiServerOptions { ModelRuntimeUri = new Uri("http://native.test:8081") }), NullLogger<ModelRuntimeClient>.Instance);

    private sealed class BudgetHandler(string modelId, int loadedContext, int inputTokens) : HttpMessageHandler
    {
        public List<JsonElement> CountBodies { get; } = [];
        public List<JsonElement> ChatBodies { get; } = [];
        public bool HoldCount { get; init; }
        public TaskCompletionSource CountEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/models") return Json(new { data = new[] { new
            {
                id = modelId, tags = new[] { "go-context-train:" + ModelContextProfiles.ResolveMaximum(modelId, null), "go-context-policy:max-fit" },
                status = new { value = "loaded" },
            } } });
            if (path == "/props") return Json(new { default_generation_settings = new { n_ctx = loadedContext } });
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (path == "/v1/chat/completions/input_tokens")
            {
                CountBodies.Add(document.RootElement.Clone());
                CountEntered.TrySetResult();
                if (HoldCount) await Task.Delay(Timeout.Infinite, cancellationToken);
                return Json(new { input_tokens = inputTokens });
            }
            Assert.Equal("/v1/chat/completions", path);
            ChatBodies.Add(document.RootElement.Clone());
            return Json(new { choices = new[] { new { message = new { role = "assistant", content = "OK" } } }, usage = new { prompt_tokens = inputTokens, completion_tokens = 1 } });
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
        };
    }
}
