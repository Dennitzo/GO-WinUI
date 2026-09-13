using System.Collections.Concurrent;
using GoWinUI.App.Services;
using GoWinUI.Core.Models;

namespace GoWinUI.Tests;

public sealed class SpeechStreamingTests
{
    [Theory]
    [InlineData("Ich prüfe jetzt die Datei.", "Ich prüfe jetzt die Datei.")]
    [InlineData("**Zuerst** prüfe ich die Datei.", "Zuerst prüfe ich die Datei.")]
    [InlineData("\n### Zunächst prüfe ich die Datei.", "Zunächst prüfe ich die Datei.")]
    public async Task CodingTokenStreamPreservesTheFirstWordThroughTheActualSpeechPlan(string narration, string expected)
    {
        var initial = Message();
        var synthesisText = new ConcurrentQueue<string>();
        using var spoken = new SemaphoreSlim(0);
        using var session = new SpeechStreamingSession(initial, (text, _) =>
        {
            // Follow the same normalization and segmentation as SpeakCoreAsync.
            var units = SpeechSourceSegmentation.CreateUnits(text);
            var segments = SpeechSourceSegmentation.CreateDirectSegments(units,
                MicrophoneTranscriptionService.PrepareSpeechText(text));
            foreach (var segment in segments) synthesisText.Enqueue(segment.Text);
            spoken.Release();
            return Task.CompletedTask;
        });
        var reasoning = new AssistantToolStep("thinking", "assistant.reasoning", "running", "INTERNAL_REASONING");
        session.Observe(new(GoAiAssistantUpdateKind.Status, initial, ToolStep: reasoning));
        for (var length = 1; length <= narration.Length; length++)
        {
            var partial = initial with { Content = narration[..length] };
            session.Observe(new(GoAiAssistantUpdateKind.Delta, partial));
            session.Observe(new(GoAiAssistantUpdateKind.Status, partial, ToolStep: reasoning));
        }
        Assert.Empty(synthesisText);
        var final = initial with { Content = narration };
        var tool = new AssistantToolStep("read", "coding.read", "running", "TOOL_DETAIL");
        session.Observe(new(GoAiAssistantUpdateKind.Status, final, ToolStep: tool));
        Assert.True(await spoken.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(expected, Assert.Single(synthesisText));
        session.Observe(new(GoAiAssistantUpdateKind.Completed, final));
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(expected, Assert.Single(synthesisText));
        Assert.Null(session.Failure);
    }

    [Fact]
    public async Task AssistantTextPlaysDuringTwoDeltasWithoutAMicrophoneSessionAndFinishesOnlyTheRemainingText()
    {
        var initial = Message();
        var spoken = new ConcurrentQueue<string>();
        using var signal = new SemaphoreSlim(0);
        using var session = new SpeechStreamingSession(initial, (text, _) =>
        {
            spoken.Enqueue(text);
            signal.Release();
            return Task.CompletedTask;
        });
        const string first = "Der erste Satz wird bereits vorgelesen. ";
        const string second = "Der zweite Satz folgt während des Laufs. ";
        const string remainder = "Der letzte Abschnitt ohne Satzzeichen";
        session.Observe(new(GoAiAssistantUpdateKind.Delta, initial with { Content = first }));
        Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(session.Completion.IsCompleted);
        session.Observe(new(GoAiAssistantUpdateKind.Delta, initial with { Content = first + second + remainder }));
        Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, spoken.Count);
        session.Observe(new(GoAiAssistantUpdateKind.Delta, initial with { Content = first }));
        var final = new GoAiAssistantUpdate(GoAiAssistantUpdateKind.Completed,
            initial with { Content = first + second + remainder, Status = MessageStatus.Completed });
        session.Observe(final);
        session.Observe(final);
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([first.Trim(), second.Trim(), remainder], spoken.ToArray());
        Assert.Null(session.Failure);
    }

    [Fact]
    public async Task StoppingCancelsCurrentAudioAndDiscardsQueuedAndFutureTextUntilANewSession()
    {
        var initial = Message();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var session = new SpeechStreamingSession(initial, async (_, token) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        const string text = "Erster Satz. Zweiter Satz. Dritter Satz. ";
        session.Observe(new(GoAiAssistantUpdateKind.Delta, initial with { Content = text }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Cancel();
        session.Observe(new(GoAiAssistantUpdateKind.Delta, initial with { Content = text + "Vierter Satz. " }));
        session.Observe(new(GoAiAssistantUpdateKind.Completed, initial with { Content = text + "Vierter Satz. Rest" }));
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, calls);
        Assert.True(session.WasCancelled);
        Assert.Null(session.Failure);
    }

    [Theory]
    [InlineData(GoAiAssistantUpdateKind.Cancelled)]
    [InlineData(GoAiAssistantUpdateKind.Failed)]
    public async Task AStoppedOrFailedRunNeverReadsItsFallbackError(GoAiAssistantUpdateKind kind)
    {
        var initial = Message();
        var spoken = new ConcurrentQueue<string>();
        using var session = new SpeechStreamingSession(initial, (text, _) => { spoken.Enqueue(text); return Task.CompletedTask; });
        session.Observe(new(kind, initial with { Content = "Keine Antwort erzeugt", Error = "Keine Antwort erzeugt" }));
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(spoken);
    }

    [Fact]
    public async Task ARealToolBoundaryFlushesFinishedNarrationButNeverSpeaksToolDetailsOrInputs()
    {
        var initial = Message();
        var spoken = new ConcurrentQueue<string>();
        using var signal = new SemaphoreSlim(0);
        using var session = new SpeechStreamingSession(initial, (text, _) => { spoken.Enqueue(text); signal.Release(); return Task.CompletedTask; });
        var current = initial with { Content = "Ich prüfe jetzt die Datei." };
        session.Observe(new(GoAiAssistantUpdateKind.Delta, current));
        Assert.Empty(spoken);
        var tool = new AssistantToolStep("read", "coding.read", "running", "DO_NOT_SPEAK_TOOL_DETAIL",
            InputJson: "{\"path\":\"DO_NOT_SPEAK_INPUT\"}", OutputJson: "{\"stdout\":\"DO_NOT_SPEAK_STDOUT\"}");
        session.Observe(new(GoAiAssistantUpdateKind.Status, current, ToolStep: tool));
        Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("Ich prüfe jetzt die Datei.", Assert.Single(spoken));
        session.Observe(new(GoAiAssistantUpdateKind.Completed, current));
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(spoken);
    }

    [Fact]
    public async Task ResumeSkipsStoredTextAndPreservesAnOpenLongFenceUntilItsActualClosingMarker()
    {
        const string baseline = "Bereits gespeicherte Erklärung.\n````python\nprint('bereits";
        var initial = Message(baseline);
        var spoken = new ConcurrentQueue<string>();
        using var session = new SpeechStreamingSession(initial, (text, _) => { spoken.Enqueue(text); return Task.CompletedTask; });
        var content = baseline + " gespeichert')\n```\nDO_NOT_READ_THIS_CODE. \n";
        session.Observe(new(GoAiAssistantUpdateKind.Delta, initial with { Content = content }));
        Assert.Empty(spoken);
        content += "````\nNeue Erklärung wird vorgelesen. ";
        session.Observe(new(GoAiAssistantUpdateKind.Delta, initial with { Content = content }));
        session.Observe(new(GoAiAssistantUpdateKind.Completed, initial with { Content = content }));
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Neue Erklärung wird vorgelesen.", Assert.Single(spoken));
    }

    [Fact]
    public void StreamingBoundariesPreserveDecimalAndInlineCodeAndLeaveUnfinishedTextForCompletion()
    {
        var buffer = new SpeechStreamingTextBuffer();
        Assert.Empty(buffer.Take("Version 3."));
        var content = "Version 3.14 nutzt `a.b()` zuverlässig. Noch offen";
        Assert.Equal("Version drei Komma eins vier nutzt a.b() zuverlässig.", Assert.Single(buffer.Take(content)));
        Assert.Empty(buffer.Take(content));
        Assert.Equal("Noch offen", Assert.Single(buffer.Take(content, complete: true)));
        Assert.Empty(buffer.Take(content, complete: true));
    }

    [Fact]
    public void RewritingAnAlreadySpokenPrefixStopsInsteadOfDuplicatingOrMisreadingTheReplacement()
    {
        var buffer = new SpeechStreamingTextBuffer();
        Assert.Single(buffer.Take("Die erste Antwort. "));
        Assert.Empty(buffer.Take("Eine völlig andere Antwort. "));
        Assert.True(buffer.HasConflictingRevision);
        Assert.Empty(buffer.Take("Eine völlig andere Antwort. Schluss", complete: true));
    }

    private static ChatMessage Message(string content = "") => new(Guid.NewGuid(), Guid.NewGuid(), ChatRole.Assistant,
        content, MessageStatus.Streaming, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
