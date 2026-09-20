using GoWinUI.Core.Models;

namespace GoWinUI.App.Services;

public sealed partial class GoAiAssistantService
{
    private readonly object _automaticSpeechGate = new();
    private SpeechStreamingSession? _automaticSpeechSession;

    internal void ObserveAutomaticSpeech(GoAiAssistantUpdate update,
        Func<GoAiSpeechUpdate, Task> status,
        Func<SpeechPlaybackProgress, Task> progress)
    {
        if (Volatile.Read(ref _disposed) != 0 || update.Message.Role != ChatRole.Assistant) return;
        lock (_automaticSpeechGate)
        {
            if (update.Kind == GoAiAssistantUpdateKind.Started)
            {
                _automaticSpeechSession?.Cancel();
                SpeechStreamingSession? session = null;
                session = new(update.Message, async (text, token) =>
                {
                    if (!IsCurrentAutomaticSpeech(session)) return;
                    await SpeakCoreAsync(update.Message.SessionId, text, null,
                        speech =>
                        {
                            if (!IsCurrentAutomaticSpeech(session)) return Task.CompletedTask;
                            if (!speech.IsActive)
                            {
                                if (speech.Status == "Abgebrochen") session!.Cancel();
                                // The queue owns completion and stop state across all chunks.
                                return speech.Error is null && !session!.WasCancelled
                                    ? status(new(true, "Antwort wird fortlaufend vorgelesen", "Warte auf weiteren Antworttext."))
                                    : Task.CompletedTask;
                            }
                            return status(speech);
                        },
                        playback => IsCurrentAutomaticSpeech(session) ? progress(playback) : Task.CompletedTask,
                        null, null,
                        // Never resolve an attachment or the full saved message for a delta.
                        // Live source ranges are deliberately omitted; a chunk is not the
                        // complete message used by the existing read-from-here highlighter.
                        new(text, "AI-Antwort (live)", null, null, update.Message.ContentProfile), token).ConfigureAwait(false);
                }, microphone.WaitForSpeechResumeAsync, microphone.BeginSpeechSession(resetPause: true),
                    _activeCancellation?.Token ?? CancellationToken.None) { WaitForActionBoundary = true };
                _automaticSpeechSession = session;
                _ = ObserveAutomaticSpeechCompletionAsync(session, status);
            }
            _automaticSpeechSession?.Observe(update);
        }
    }

    private bool IsCurrentAutomaticSpeech(SpeechStreamingSession? session)
    {
        lock (_automaticSpeechGate) return session is not null && ReferenceEquals(_automaticSpeechSession, session);
    }

    private async Task ObserveAutomaticSpeechCompletionAsync(SpeechStreamingSession session, Func<GoAiSpeechUpdate, Task> status)
    {
        await session.Completion.ConfigureAwait(false);
        if (!session.HasPlayed || !IsCurrentAutomaticSpeech(session)) return;
        try
        {
            await status(new(false, session.Failure is not null ? "Vorlesen fehlgeschlagen"
                : session.WasCancelled ? "Abgebrochen" : "Abgeschlossen", Error: session.Failure?.Message)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The WebView may have been detached while audio was finishing.
            RunDiagnostic(logger, session.MessageId.ToString("D"), "automatic-speech-status-detached", exception);
        }
    }

    private void CancelAutomaticSpeech()
    {
        lock (_automaticSpeechGate) _automaticSpeechSession?.Cancel();
    }
}
