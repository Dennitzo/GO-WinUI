using GoAi.Contracts;
using GoAi.Server.Core.Gateway;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class EmptyInterruptedNativeTailTests
{
    private const string ModelId = "coding/DeepSeek-V4-Flash-Vision-Fixture~empty-tail";

    [Theory]
    [InlineData("")]
    [InlineData(" \n\t")]
    public async Task StopAfterPrefillPersistsAnExactEmptyOrWhitespaceAssistantTurn(string generatedTail)
    {
        using var context = new TestServerContext();
        context.Options.ModelRuntimeUri = new("http://native.test:19090");
        context.Options.GeneralModelId = ModelId;
        using var handler = new PrefillOnlyHandler(generatedTail);
        using var http = new HttpClient(handler);
        using var runtime = new ModelRuntimeClient(http, context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGoAiServerServices(context.Options, includeHostedServices: false);
        services.AddSingleton(context.Database);
        services.AddSingleton(runtime);
        using var provider = services.BuildServiceProvider();
        var repository = provider.GetRequiredService<RunRepository>();
        var request = Request();
        var stopped = (await repository.CreateAsync(request, null)).Snapshot.RunId;
        using var cancellation = new CancellationTokenSource();
        var processing = provider.GetRequiredService<RunProcessor>().ProcessAsync(stopped, cancellation.Token);
        await handler.PrefillRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await repository.CancelAsync(stopped));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);

        var receipt = Assert.Single(await repository.GetEventsAfterAsync(stopped, 0),
            item => item.Type == RunProcessor.InterruptedTurnEventType);
        Assert.True(receipt.Data.GetProperty("exactNativeTailRecovered").GetBoolean());
        Assert.Equal("", receipt.Data.GetProperty("content").GetString());
        Assert.Equal(generatedTail, receipt.Data.GetProperty("reasoningContent").GetString());
        var next = FollowUp(request);
        var nextId = (await repository.CreateAsync(next, null)).Snapshot.RunId;
        // Read from a fresh repository, so the assertion exercises the durable
        // event receipt rather than the interrupted in-memory callback buffers.
        var restored = await new RunRepository(context.Database, new RunEventNotifier())
            .GetGeneralSessionContextAsync(nextId, next);
        Assert.NotNull(restored);
        Assert.Empty(restored.VisibleResponse);
        Assert.True(GeneralSessionContext.TryContinue(restored, next,
            RunProcessor.CreateInitialMessages(next, "general", []), out var messages));
        var assistant = Assert.Single(messages, message => message.Role == "assistant");
        Assert.Equal("", assistant.Content);
        Assert.Equal(generatedTail, assistant.ReasoningContent);
        Assert.Null(assistant.ToolCalls);
        Assert.Single(messages, message => message.Content?.StartsWith("GO_INTERRUPTED_MODEL_TURN", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task EmptyUnverifiedReceiptCannotInventAnAssistantTurn(bool? exactNativeTailRecovered)
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var request = Request();
        var stopped = (await repository.CreateAsync(request, null)).Snapshot.RunId;
        var native = ModelRuntimeClient.PrepareLanguageBoundMessages(RunProcessor.CreateInitialMessages(request, "general", []));
        await repository.SaveCheckpointAsync(stopped, new(native, 0, 0, 0, 0, VisibleTextLength: 0,
            StreamingTurnStartEventId: 0, PreserveSessionPromptPrefix: true));
        await repository.AppendEventAsync(stopped, RunProcessor.InterruptedTurnEventType,
            new { turnStartEventId = 0, content = "", reasoningContent = "", exactNativeTailRecovered });
        await repository.CancelAsync(stopped);
        var next = FollowUp(request);
        var nextId = (await repository.CreateAsync(next, null)).Snapshot.RunId;
        var restored = await repository.GetGeneralSessionContextAsync(nextId, next);
        Assert.NotNull(restored);
        Assert.Equal(native, restored.Checkpoint.Messages);
        Assert.DoesNotContain(restored.Checkpoint.Messages, message => message.Role == "assistant");
    }

    private static RunRequest Request() => new(GoAiProtocol.Version, RunMode.General,
        [new("user", [new("text", "Merke EICHE-42.")])], SessionId: "empty-tail-session",
        PreferredGeneralModelId: ModelId, ReasoningEffort: "high", ClientCapabilities: [], AllowedServerTools: []);

    private static RunRequest FollowUp(RunRequest request) => request with
    {
        Messages = [.. request.Messages, new("user", [new("text", "Nenne die Kennung.")])],
    };

    private sealed class PrefillOnlyHandler(string generatedTail) : HttpMessageHandler
    {
        private static readonly string[] Tags = ["go-context-train:32768", "go-reasoning-mode:llama-native",
            "go-reasoning-levels:none|high", "go-reasoning-default:high"];
        internal TaskCompletionSource PrefillRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var result = request.RequestUri!.AbsolutePath switch
            {
                "/v1/models" => Json(new { data = new[] { new { id = ModelId, tags = Tags, status = new { value = "loaded" } } } }),
                "/props" => Json(new { default_generation_settings = new { n_ctx = 32768 } }),
                "/v1/chat/completions/input_tokens" => Json(new { input_tokens = 1536 }),
                "/sessions/prepare" => Json(new { status = "prepared" }),
                "/sessions/save" => Json(new { status = "saved", generatedTail }),
                "/v1/chat/completions" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new PrefillStream(PrefillRead))
                    {
                        Headers = { ContentType = new("text/event-stream") },
                    },
                },
                _ => throw new InvalidOperationException("Unexpected endpoint: " + request.RequestUri.AbsolutePath),
            };
            return Task.FromResult(result);
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
        };
    }

    private sealed class PrefillStream(TaskCompletionSource read) : MemoryStream(Encoding.UTF8.GetBytes(
        "data: {\"prompt_progress\":{\"total\":1536,\"processed\":1536,\"cache\":0}}\n\n"))
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < Length) return await base.ReadAsync(buffer, cancellationToken);
            read.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
