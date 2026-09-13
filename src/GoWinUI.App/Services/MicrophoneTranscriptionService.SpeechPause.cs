namespace GoWinUI.App.Services;

public sealed partial class MicrophoneTranscriptionService
{
    private readonly SpeechPauseCoordinator _speechPause = new();

    internal IDisposable BeginSpeechSession(bool resetPause = false)
    {
        IDisposable session;
        lock (_playbackGate)
        {
            session = _speechPause.Begin(resetPause);
            UpdateSpeechPauseStatus();
        }
        RaiseChanged();
        return new SpeechSessionLease(this, session);
    }

    internal Task WaitForSpeechResumeAsync(CancellationToken cancellationToken) => _speechPause.WaitAsync(cancellationToken);

    // Call while holding _playbackGate, so attaching/refilling audio and the user's
    // pause command cannot race each other into starting a paused output.
    private void UpdateSpeechPauseStatus()
    {
        var status = _speechPause.IsPaused ? "Vorlesen pausiert"
            : _activeOutput is not null ? "AI-Antwort wird vorgelesen"
            : _speaking ? "Sprachausgabe wird erzeugt"
            : _speechPause.IsActive ? "Warte auf weiteren Vorlesetext"
            : _active ? "Ich höre zu" : "Inaktiv";
        _status = _recognizing ? status + " · Sprache wird erkannt" : status;
    }

    private sealed class SpeechSessionLease(MicrophoneTranscriptionService owner, IDisposable session) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner._playbackGate)
            {
                session.Dispose();
                owner.UpdateSpeechPauseStatus();
            }
            if (!owner._disposed) owner.RaiseChanged();
        }
    }
}
