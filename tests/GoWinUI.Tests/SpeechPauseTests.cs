using System.Collections.Concurrent;
using GoWinUI.App.Services;
using GoWinUI.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace GoWinUI.Tests;

public sealed class SpeechPauseTests
{
    [Fact]
    public async Task HostCanPausePreparationAndQueueGapsWithoutAnAudioDeviceAndNestedChunksKeepThePause()
    {
        using var microphone = new MicrophoneTranscriptionService(null!, null!, NullLogger<MicrophoneTranscriptionService>.Instance);
        var snapshots = new List<MicrophoneSnapshot>();
        microphone.Changed += (_, snapshot) => snapshots.Add(snapshot);
        Assert.False(microphone.Current.CanPauseSpeech);
        using (microphone.BeginSpeechSession(resetPause: true))
        {
            Assert.True(microphone.Current.IsSpeaking);
            Assert.True(microphone.Current.CanPauseSpeech);
            var paused = await microphone.ToggleSpeechPauseAsync();
            Assert.True(paused.IsSpeechPaused);
            Assert.Contains("pausiert", paused.Status, StringComparison.Ordinal);
            var waiting = microphone.WaitForSpeechResumeAsync(CancellationToken.None);
            Assert.False(waiting.IsCompleted);
            using (microphone.BeginSpeechSession())
            {
                Assert.True(microphone.Current.IsSpeechPaused);
                Assert.False(waiting.IsCompleted);
            }
            Assert.True(microphone.Current.IsSpeechPaused);
            Assert.True(microphone.Current.CanPauseSpeech);
            Assert.False(waiting.IsCompleted);
            var resumed = await microphone.ToggleSpeechPauseAsync();
            await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(resumed.IsSpeechPaused);
            Assert.True(resumed.CanPauseSpeech);
        }
        Assert.False(microphone.Current.IsSpeaking);
        Assert.False(microphone.Current.CanPauseSpeech);
        Assert.False(microphone.Current.IsSpeechPaused);
        Assert.Contains(snapshots, static snapshot => snapshot.IsSpeaking && snapshot.CanPauseSpeech && snapshot.IsSpeechPaused);
        Assert.False((await microphone.ToggleSpeechPauseAsync()).IsSpeechPaused);
    }

    [Fact]
    public async Task PausingBeforeTheFirstChunkRetainsAllQueuedAndFinalTextAndResumesEachOnce()
    {
        using var microphone = new MicrophoneTranscriptionService(null!, null!, NullLogger<MicrophoneTranscriptionService>.Instance);
        var initial = Message();
        var spoken = new ConcurrentQueue<string>();
        var enteredWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = new SpeechStreamingSession(initial,
            (text, _) => { spoken.Enqueue(text); return Task.CompletedTask; },
            waitForResume: token =>
            {
                var waiting = microphone.WaitForSpeechResumeAsync(token);
                enteredWait.TrySetResult();
                return waiting;
            }, playbackLifetime: microphone.BeginSpeechSession(resetPause: true));
        Assert.True((await microphone.ToggleSpeechPauseAsync()).IsSpeechPaused);
        const string first = "Der erste Abschnitt bleibt erhalten. ";
        const string second = "Der zweite Abschnitt bleibt ebenfalls erhalten. ";
        const string tail = "Der letzte Rest";
        session.Observe(new(GoAiAssistantUpdateKind.Delta, initial with { Content = first }));
        await enteredWait.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Observe(new(GoAiAssistantUpdateKind.Delta, initial with { Content = first + second }));
        session.Observe(new(GoAiAssistantUpdateKind.Completed, initial with { Content = first + second + tail }));
        session.Observe(new(GoAiAssistantUpdateKind.Completed, initial with { Content = first + second + tail }));
        Assert.Empty(spoken);
        Assert.False(session.Completion.IsCompleted);
        Assert.True(microphone.Current.IsSpeechPaused);
        await microphone.ToggleSpeechPauseAsync();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([first.Trim(), second.Trim(), tail], spoken.ToArray());
        Assert.Null(session.Failure);
        Assert.False(microphone.Current.IsSpeaking);
    }

    [Fact]
    public async Task PausingBetweenChunksDoesNotRestartTheAlreadyPlayedText()
    {
        var initial = Message();
        var pause = new SpeechPauseCoordinator();
        var spoken = new ConcurrentQueue<string>();
        var firstPlayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = new SpeechStreamingSession(initial, (text, _) =>
        {
            spoken.Enqueue(text);
            if (spoken.Count == 1)
            {
                pause.Toggle(); // Exactly at the first completed segment, before the next dequeue.
                firstPlayed.TrySetResult();
            }
            return Task.CompletedTask;
        }, waitForResume: token =>
        {
            var waiting = pause.WaitAsync(token);
            if (pause.IsPaused) nextWaiting.TrySetResult();
            return waiting;
        }, playbackLifetime: pause.Begin());
        const string first = "Bereits gelesener erster Satz. ";
        const string second = "Der nächste Satz bleibt in der Warteschlange.";
        session.Observe(new(GoAiAssistantUpdateKind.Delta, initial with { Content = first }));
        await firstPlayed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Observe(new(GoAiAssistantUpdateKind.Completed, initial with { Content = first + second }));
        await nextWaiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(first.Trim(), Assert.Single(spoken));
        Assert.False(session.Completion.IsCompleted);
        pause.Toggle();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([first.Trim(), second], spoken.ToArray());
        Assert.False(pause.IsActive);
        Assert.False(pause.IsPaused);
    }

    [Fact]
    public async Task CancellingWhilePausedUnblocksTheQueueAndNeverPlaysPendingText()
    {
        var initial = Message();
        var pause = new SpeechPauseCoordinator();
        var enteredWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var played = 0;
        using var session = new SpeechStreamingSession(initial,
            (_, _) => { Interlocked.Increment(ref played); return Task.CompletedTask; },
            waitForResume: token =>
            {
                var waiting = pause.WaitAsync(token);
                enteredWait.TrySetResult();
                return waiting;
            }, playbackLifetime: pause.Begin());
        pause.Toggle();
        session.Observe(new(GoAiAssistantUpdateKind.Completed, initial with { Content = "Dieser Satz bleibt ungesprochen." }));
        await enteredWait.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Cancel();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, played);
        Assert.True(session.WasCancelled);
        Assert.Null(session.Failure);
        Assert.False(pause.IsActive);
        Assert.False(pause.IsPaused);
    }

    [Fact]
    public async Task ReleasingTheLastLifetimeWakesWaitersAndANewSessionStartsUnpaused()
    {
        var pause = new SpeechPauseCoordinator();
        var lifetime = pause.Begin();
        pause.Toggle();
        var waiting = pause.WaitAsync(CancellationToken.None);
        Assert.False(waiting.IsCompleted);
        lifetime.Dispose();
        lifetime.Dispose();
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pause.IsActive);
        using var next = pause.Begin();
        Assert.True(pause.IsActive);
        Assert.False(pause.IsPaused);
    }

    private static ChatMessage Message() => new(Guid.NewGuid(), Guid.NewGuid(), ChatRole.Assistant,
        "", MessageStatus.Streaming, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
