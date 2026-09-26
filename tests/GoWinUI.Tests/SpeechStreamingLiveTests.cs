using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using GoWinUI.App.Services;
using GoWinUI.App.ViewModels;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace GoWinUI.Tests;

/// <summary>Opt-in real gateway synthesis and physical playback, driven through the normal coordinator update path.</summary>
public sealed class SpeechStreamingLiveTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    [Trait("Category", "Live")]
    public async Task CoordinatorPlaysRealSpeechBeforeCompletionWithMicrophoneDisabledAndExcludesTools()
    {
        if (Environment.GetEnvironmentVariable("GO_AI_STREAMING_SPEECH_LIVE") != "1") return;
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        await settings.UpdateAsync(value => value with
        {
            GoAiServerUrl = Environment.GetEnvironmentVariable("GO_AI_SERVER_URL") ?? "http://127.0.0.1:8080",
            IsAiConnectionEnabled = true,
        });
        var paragraphs = new ConcurrentQueue<string>();
        var requestPaths = new ConcurrentQueue<string>();
        using var connection = new GoAiConnectionService(settings, NullLogger<GoAiConnectionService>.Instance,
            () => new RecordingHandler(paragraphs, requestPaths));
        using var microphone = new MicrophoneTranscriptionService(connection, settings, NullLogger<MicrophoneTranscriptionService>.Instance);
        var chats = environment.Get<IChatRepository>();
        var documents = environment.Get<IDocumentIngestor>();
        var recent = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
        var broker = new LocalToolBroker(connection, documents, null!, chats);
        using var service = new GoAiAssistantService(connection, chats, environment.Get<IAssistantAttachmentRepository>(),
            environment.Get<IChatArtifactRepository>(), environment.Get<IGoAiRunRepository>(), environment.Get<IClientToolExecutionRepository>(),
            environment.Get<IBinaryObjectStore>(), documents, new DocumentContextPreparationService(documents),
            new SessionContextPreparationService(chats), broker, null!, microphone, settings, recent,
            NullLogger<GoAiAssistantService>.Instance);
        var coordinator = new AssistantCoordinator(chats, environment.Get<IWorkflowRepository>(), documents,
            environment.Get<IContextAssembler>(), environment.Get<IPromptTriggerRepository>(),
            environment.Get<IAssistantAttachmentRepository>(), environment.Get<IChatArtifactRepository>(),
            environment.Get<IConversationSnapshotRepository>(), service, settings, recent, microphone);
        var session = await chats.CreateSessionAsync("Isolierte Prüfung der laufenden Sprachausgabe");
        await settings.UpdateAsync(value => value with { ActiveSessionId = session.Id });
        var turn = await chats.AddTurnAsync(session.Id, "Erkläre die Aufgabe in kurzen Abschnitten.");
        var current = turn.AssistantMessage;
        var playing = Channel.CreateUnbounded<Guid>();
        var playbackIds = new ConcurrentDictionary<Guid, byte>();
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        const string first = "Die erste Erklärung wird bereits während des Laufs vorgelesen.";
        const string second = "Die zweite Erklärung folgt, während die Antwort noch entsteht.";
        const string remainder = "Zum Abschluss folgt der verbliebene Antworttext";
        const string secret = "DO_NOT_SPEAK_TOOL_DATA";
        var tool = new AssistantToolStep("speech-live-tool", "coding.command", "completed", secret,
            InputJson: "{\"command\":\"DO_NOT_SPEAK_TOOL_DATA\"}",
            OutputJson: "{\"stdout\":\"DO_NOT_SPEAK_TOOL_DATA\",\"stderr\":\"DO_NOT_SPEAK_TOOL_DATA\"}");
        var steps = new[] { tool };
        output.WriteLine($"profile={environment.Directory}; server={settings.Current.GoAiServerUrl}; microphone=disabled; actual audio playback is enabled");
        try
        {
            // No microphone/voice-control start occurs anywhere in this test.
            Assert.False(microphone.Current.IsRecording);
            await coordinator.EmitGoAiUpdateAsync(new(GoAiAssistantUpdateKind.Started, current), Emit, "speech-live");
            await Send(GoAiAssistantUpdateKind.Delta, first + " ", MessageStatus.Streaming);
            var firstPlayback = await WaitForPlaybackAsync();
            Assert.Equal(MessageStatus.Streaming, (await chats.GetMessageAsync(current.Id))!.Status);
            Assert.False(microphone.Current.IsRecording);
            Assert.False(done.Task.IsCompleted);
            output.WriteLine("First real Playing event arrived while the persisted AI message is still streaming.");

            await Send(GoAiAssistantUpdateKind.Delta, first + " " + second + " ", MessageStatus.Streaming);
            await coordinator.EmitGoAiUpdateAsync(new(GoAiAssistantUpdateKind.Status, current, ToolStep: tool), Emit, "speech-live");
            var secondPlayback = await WaitForPlaybackAsync();
            Assert.NotEqual(firstPlayback, secondPlayback);
            Assert.Equal(MessageStatus.Streaming, (await chats.GetMessageAsync(current.Id))!.Status);
            Assert.False(done.Task.IsCompleted);

            await Send(GoAiAssistantUpdateKind.Completed, first + " " + second + " " + remainder, MessageStatus.Completed);
            await coordinator.EmitGoAiUpdateAsync(new(GoAiAssistantUpdateKind.Completed, current), Emit, "speech-live");
            Assert.Equal("Abgeschlossen", await done.Task.WaitAsync(timeout.Token));
            Assert.Equal(3, playbackIds.Count);
            var actual = paragraphs.ToArray();
            Assert.Equal(3, actual.Length);
            Assert.Contains(first, actual[0], StringComparison.Ordinal);
            Assert.Contains(second, actual[1], StringComparison.Ordinal);
            Assert.Contains(remainder, actual[2], StringComparison.Ordinal);
            Assert.DoesNotContain(secret, string.Join("\n", actual), StringComparison.Ordinal);
            Assert.DoesNotContain(requestPaths, path => path.Contains("transcription", StringComparison.OrdinalIgnoreCase)
                || path.Contains("live-caption", StringComparison.OrdinalIgnoreCase));
            Assert.False(microphone.Current.IsRecording);
            output.WriteLine("Passed: three real TTS paragraphs and playback IDs; two audible before completion; final tail once; no microphone or tool text.");
        }
        finally
        {
            await service.CancelSpeechAsync();
            if (!playbackIds.IsEmpty && !done.Task.IsCompleted)
                await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }

        async Task Send(GoAiAssistantUpdateKind kind, string content, MessageStatus status)
        {
            await chats.UpdateMessageWithToolStepsAsync(current.Id, content, status, steps, timeout.Token);
            current = (await chats.GetMessageAsync(current.Id, timeout.Token))!;
            await coordinator.EmitGoAiUpdateAsync(new(kind, current), Emit, "speech-live");
        }

        async Task<Guid> WaitForPlaybackAsync()
        {
            var next = playing.Reader.ReadAsync(timeout.Token).AsTask();
            if (await Task.WhenAny(next, done.Task) == done.Task)
                throw new InvalidOperationException("Speech ended before the expected real playback: " + await done.Task);
            return await next;
        }

        Task Emit(string kind, object payload, string? requestId)
        {
            if (kind is not ("speech.progress" or "speech.status")) return Task.CompletedTask;
            var data = JsonSerializer.SerializeToElement(payload, Json);
            Assert.False(microphone.Current.IsRecording);
            if (kind == "speech.progress" && data.GetProperty("state").GetString() == "playing")
            {
                var playbackId = data.GetProperty("playbackId").GetGuid();
                if (playbackIds.TryAdd(playbackId, 0)) playing.Writer.TryWrite(playbackId);
            }
            if (kind == "speech.status")
            {
                output.WriteLine($"{DateTimeOffset.UtcNow:O} {JsonSerializer.Serialize(data)}");
                if (!data.GetProperty("active").GetBoolean())
                {
                    if (data.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                        done.TrySetException(new InvalidOperationException(error.GetString()));
                    else done.TrySetResult(data.GetProperty("status").GetString()!);
                }
            }
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHandler(ConcurrentQueue<string> paragraphs, ConcurrentQueue<string> requestPaths) : DelegatingHandler(new HttpClientHandler { UseProxy = false })
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            requestPaths.Enqueue(path);
            if (request.Method == HttpMethod.Post && path.EndsWith("/paragraphs", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                paragraphs.Enqueue(body.RootElement.GetProperty("text").GetString()!);
            }
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
