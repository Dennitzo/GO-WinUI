using System.Globalization;
using GoAi.Server.Core.Models;

namespace GoAi.Server.Tests;

public sealed class ReasoningLoopGuardTests
{
    // Original repeated passage from the overnight Coding incident, round 133.
    internal const string NightPassage = "Eigentlich: Ich sollte den Bericht erstellen. Die Kernarbeit ist abgeschlossen. "
        + "Ich erstelle den Abschlussbericht. Die Kernarbeit ist abgeschlossen. Die verbleibenden Subagent-Erwähnungen "
        + "werden im Bericht angeben. Aber zuerst: Ich sollte die verbleibenden Subagent-Erwähnungen prüfen. ";

    [Fact]
    public void SustainedRepetitionStopsWithBoundedDiagnosticContext()
    {
        var guard = new ReasoningLoopGuard();
        var failure = Assert.Throws<ReasoningLoopDetectedException>(() =>
        {
            for (var index = 0; index < 300; index++) guard.ObserveReasoning(NightPassage, TimeSpan.FromSeconds(index));
        });
        Assert.Equal("reasoning_repetition", failure.FailureKind);
        Assert.InRange(failure.ReasoningWords, 1280, 1800);
        Assert.InRange(failure.ReasoningTail.Length, 1, 16_384);
        Assert.Contains("Bericht erstellen", failure.ReasoningTail);
        Assert.False(ModelRuntimeClient.IsTransientInferenceFailure(failure));
    }

    [Fact]
    public void FragmentBoundariesDoNotHideRepeatedSequences()
    {
        var guard = new ReasoningLoopGuard();
        var text = string.Concat(Enumerable.Repeat(NightPassage, 100));
        var failure = Assert.Throws<ReasoningLoopDetectedException>(() =>
        {
            foreach (var character in text) guard.ObserveReasoning(character.ToString(), TimeSpan.Zero);
        });
        Assert.Equal("reasoning_repetition", failure.FailureKind);
    }

    [Fact]
    public void ShortReconsiderationAndLongNovelReasoningAreAllowed()
    {
        var guard = new ReasoningLoopGuard();
        for (var index = 0; index < 10; index++) guard.ObserveReasoning(NightPassage, TimeSpan.Zero);
        for (var index = 0; index < 20_000; index++)
            guard.ObserveReasoning("Schritt" + index.ToString(CultureInfo.InvariantCulture) + " ", TimeSpan.Zero);
        Assert.True(guard.ReasoningWords > 20_000);
        Assert.InRange(guard.StoredSequenceCount, 1, 8192);
    }

    [Fact]
    public void OnlyNovelVisibleContentRestartsReasoningPhase()
    {
        var guard = new ReasoningLoopGuard(TimeSpan.FromMinutes(30));
        guard.ObserveReasoning("Erster Gedankengang. ", TimeSpan.Zero);
        Assert.False(guard.ObserveResponseProgress(" \n"));
        Assert.True(guard.ObserveResponseProgress("Ein konkretes neues Zwischenergebnis."));
        Assert.Null(guard.Remaining(TimeSpan.FromMinutes(29)));
        guard.ObserveReasoning("Nächste unabhängige Überlegung. ", TimeSpan.FromMinutes(29));
        // Complete one boundary-spanning sequence, then familiar content cannot
        // keep restarting the timer even if streamed in many small fragments.
        guard.ObserveResponseProgress("Ein konkretes neues Zwischenergebnis.");
        guard.ObserveReasoning("Noch eine Überlegung. ", TimeSpan.FromMinutes(29));
        for (var index = 0; index < 100; index++)
            guard.ObserveResponseProgress("Ein konkretes neues Zwischenergebnis.");
        var failure = Assert.Throws<ReasoningLoopDetectedException>(() => guard.ThrowIfWatchdogExpired(TimeSpan.FromMinutes(60)));
        Assert.Equal("reasoning_watchdog", failure.FailureKind);
        Assert.Contains("30 Minuten", failure.Message);
    }

    [Fact]
    public void AdvancingReasoningDoesNotDisableAbsoluteReasoningOnlyWatchdog()
    {
        var guard = new ReasoningLoopGuard(TimeSpan.FromMinutes(2));
        guard.ObserveReasoning("Ein neuer Anfang. ", TimeSpan.Zero);
        guard.ObserveReasoning("Eine andere Erkenntnis. ", TimeSpan.FromMinutes(1));
        Assert.Equal(TimeSpan.FromMinutes(1), guard.Remaining(TimeSpan.FromMinutes(1)));
        Assert.Throws<ReasoningLoopDetectedException>(() => guard.ObserveReasoning("Noch mehr Fortschritt. ", TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void UnspacedCjkReasoningCanAlsoBeDetected()
    {
        var guard = new ReasoningLoopGuard();
        Assert.Throws<ReasoningLoopDetectedException>(() => guard.ObserveReasoning(
            string.Concat(Enumerable.Repeat("我应该完成报告然后再次检查所有内容", 200)), TimeSpan.Zero));
    }
}
