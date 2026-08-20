using GoAi.Client;
using GoAi.Contracts;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Collections.Concurrent;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace GoWinUI.App.Services;

public sealed record MicrophoneSnapshot(
    bool IsRecording,
    bool IsBusy,
    bool IsSpeaking,
    bool CanPauseSpeech,
    bool IsSpeechPaused,
    string Status,
    DateTimeOffset? StartedAt,
    string? Error,
    string PartialTranscript,
    string? Provider,
    string? DeviceLabel);

public sealed record MicrophoneTurnSnapshot(
    string TurnId,
    string Text,
    bool IsFinal,
    string? Provider);

public sealed partial class MicrophoneTranscriptionService(
    GoAiConnectionService connection,
    SettingsCoordinator settings,
    ILogger<MicrophoneTranscriptionService> logger) : IDisposable
{
    private const int SampleRate = GoAiProtocol.LiveCaptionSampleRate;
    private const int WindowMilliseconds = 2_000;
    private const int OverlapMilliseconds = 500;
    private const int MinimumPcmBytes = SampleRate * sizeof(short) / 5;
    private const int MaximumPcmBytes = SampleRate * sizeof(short) * 3;
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _chunkGate = new(1, 1);
    private readonly SemaphoreSlim _speechGate = new(1, 1);
    private readonly object _playbackGate = new();
    private GoAiClient? _client;
    private CancellationTokenSource? _sessionCancellation;
    private CancellationTokenSource? _playbackCancellation;
    private Task? _keepAliveTask;
    private string? _serverSessionId;
    private long _serverSequence;
    private string? _turnId;
    private int _nextTurnChunk;
    private string _turnTranscript = string.Empty;
    private string _status = "Inaktiv";
    private string? _error;
    private string? _provider;
    private string? _deviceLabel;
    private DateTimeOffset? _startedAt;
    private WaveOutEvent? _activeOutput;
    private SpeechMediaTransportController? _mediaTransport;
    private Func<SpeechSegmentPlaybackUpdate, Task>? _playbackProgress;
    private int _activePlaybackSegmentIndex = -1;
    private IReadOnlyList<int> _activePlaybackSegmentIndexes = [];
    private bool _active;
    private bool _starting;
    private bool _stopping;
    private bool _speaking;
    private bool _canPauseSpeech;
    private bool _speechPaused;
    private bool _recognizing;
    private bool _disposed;

    public event EventHandler<MicrophoneSnapshot>? Changed;

    public event EventHandler<MicrophoneTurnSnapshot>? TurnChanged;

    public MicrophoneSnapshot Current => new(
        _active,
        _starting || _stopping || _speaking || _recognizing,
        _speaking,
        _canPauseSpeech,
        _speechPaused,
        _status,
        _startedAt,
        _error,
        _turnTranscript,
        _provider,
        _deviceLabel);

    public async Task<UtteranceIntentResponse> ClassifyIntentAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        using var client = await connection.CreateClientAsync(cancellationToken).ConfigureAwait(false);
        var language = settings.Current.LiveCaptionLanguage;
        return await client.ClassifyUtteranceIntentAsync(
            new UtteranceIntentRequest(
                text,
                string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase) ? null : language),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task StartAsync(
        string? deviceLabel = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_active || _starting)
            {
                return;
            }

            _starting = true;
            _status = "Sprachmodell wird geladen";
            _error = null;
            _provider = null;
            _deviceLabel = string.IsNullOrWhiteSpace(deviceLabel) ? "Standardmikrofon" : deviceLabel.Trim();
            _startedAt = DateTimeOffset.Now;
            _turnId = null;
            _turnTranscript = string.Empty;
            _nextTurnChunk = 0;
            _serverSequence = 0;
            RaiseChanged();

            var client = await connection.CreateClientAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var language = settings.Current.LiveCaptionLanguage;
                var session = await client.CreateLiveCaptionSessionAsync(
                    new LiveCaptionSessionRequest(
                        string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase) ? null : language,
                        LiveCaptionMode.Transcribe,
                        SampleRate,
                        1,
                        WindowMilliseconds,
                        OverlapMilliseconds),
                    cancellationToken).ConfigureAwait(false);

                _client = client;
                _serverSessionId = session.SessionId;
                _sessionCancellation = new CancellationTokenSource();
                _active = true;
                _starting = false;
                _status = "Ich höre zu";
                _keepAliveTask = Task.Run(
                    () => KeepAliveAsync(_sessionCancellation.Token),
                    CancellationToken.None);
                RaiseChanged();
                VoiceStarted(logger, _deviceLabel, null);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            _active = false;
            _starting = false;
            _status = "Sprachsteuerung fehlgeschlagen";
            _error = FriendlyError(exception);
            _startedAt = null;
            ReleaseSession();
            RaiseChanged();
            VoiceFailed(logger, exception.GetType().Name, exception);
            throw new InvalidOperationException(_error, exception);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task SubmitChunkAsync(
        string turnId,
        int chunkIndex,
        string pcmBase64,
        bool isFinal,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(turnId) || turnId.Length > 128)
        {
            throw new ArgumentException("Die Sprachsequenz ist ungültig.", nameof(turnId));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(chunkIndex);
        var pcm = DecodePcm(pcmBase64);

        await _chunkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_active)
            {
                return;
            }
            if (_turnId is null)
            {
                _turnId = turnId;
                _nextTurnChunk = 0;
                _turnTranscript = string.Empty;
            }
            if (!string.Equals(_turnId, turnId, StringComparison.Ordinal))
            {
                // WebView message callbacks may finish acquiring the native bridge
                // semaphore in a different order. A new turn supersedes an unfinished
                // turn; the speech window itself is still independently transcribable.
                _turnId = turnId;
                _nextTurnChunk = chunkIndex;
                _turnTranscript = string.Empty;
            }
            else if (chunkIndex < _nextTurnChunk)
            {
                // Duplicate delivery after a bridge retry is idempotent.
                return;
            }
            else if (chunkIndex > _nextTurnChunk)
            {
                // Continue with the newest available window instead of poisoning all
                // later microphone input because one browser message was dropped.
                _nextTurnChunk = chunkIndex;
            }

            var client = _client ?? throw new InvalidOperationException("Der Sprachclient ist nicht aktiv.");
            var sessionId = _serverSessionId ?? throw new InvalidOperationException("Die Sprachsitzung ist nicht aktiv.");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _sessionCancellation?.Token ?? CancellationToken.None);
            _status = "Sprache wird erkannt";
            _error = null;
            _recognizing = true;
            RaiseChanged();

            var response = await SendChunkWithRecoveryAsync(
                client,
                sessionId,
                CreatePcm16Wave(pcm),
                linked.Token).ConfigureAwait(false);
            _nextTurnChunk++;
            _provider = response.Provider;
            _turnTranscript = AppendSegment(_turnTranscript, PlainSpeechText(response));
            var turnSnapshot = new MicrophoneTurnSnapshot(
                turnId,
                _turnTranscript,
                isFinal,
                _provider);

            if (isFinal)
            {
                _turnId = null;
                _nextTurnChunk = 0;
                _turnTranscript = string.Empty;
            }
            TurnChanged?.Invoke(this, turnSnapshot);
            _recognizing = false;
            _status = _speaking ? "AI-Antwort wird vorgelesen" : "Ich höre zu";
            _error = null;
            RaiseChanged();
        }
        catch (OperationCanceledException) when (_sessionCancellation?.IsCancellationRequested == true)
        {
            // Stopping voice mode intentionally abandons the current partial window.
            _recognizing = false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            _recognizing = false;
            _turnId = null;
            _nextTurnChunk = 0;
            _turnTranscript = string.Empty;
            _status = "Spracherkennung unterbrochen";
            _error = FriendlyError(exception);
            RaiseChanged();
            VoiceFailed(logger, exception.GetType().Name, exception);
            throw new InvalidOperationException(_error, exception);
        }
        finally
        {
            _recognizing = false;
            _chunkGate.Release();
        }
    }

    public Task SpeakAsync(string markdown, CancellationToken cancellationToken = default) =>
        PlayTextAsync(markdown, requireActiveVoiceMode: true, cancellationToken);

    public Task<string?> PlayTextAsync(
        string markdown,
        bool requireActiveVoiceMode = false,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var text = PrepareSpeechText(markdown);
        if (string.IsNullOrWhiteSpace(text) || (requireActiveVoiceMode && !_active))
        {
            return Task.FromResult<string?>(null);
        }
        var units = SpeechSourceSegmentation.CreateUnits(text);
        var segments = SpeechSourceSegmentation.CreateDirectSegments(units, text);
        return PlaySegmentsAsync(segments, requireActiveVoiceMode: requireActiveVoiceMode, cancellationToken: cancellationToken);
    }

    internal async Task<string?> PlaySegmentsAsync(
        IReadOnlyList<PreparedSpeechSegment> segments,
        Func<SpeechSegmentPlaybackUpdate, Task>? progress = null,
        SpeechContentProfile profile = SpeechContentProfile.Prepared,
        bool requireActiveVoiceMode = false,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var playable = SpeechSourceSegmentation.NormalizePreparedSegments(segments);
        if (playable.Count == 0 || (requireActiveVoiceMode && !_active))
        {
            return null;
        }

        await _speechGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? speechProvider = null;
        CancellationTokenSource? playbackCancellation = null;
        try
        {
            if (requireActiveVoiceMode && !_active)
            {
                return null;
            }
            // Speech synthesis and persistent microphone transcription have
            // independent lifetimes. Never borrow the live-caption client here:
            // turning voice control off must not dispose or stop active TTS.
            using var speechClient = await connection.CreateClientAsync(cancellationToken).ConfigureAwait(false);
            var client = speechClient;
            _playbackCancellation?.Cancel();
            _playbackCancellation?.Dispose();
            playbackCancellation = new CancellationTokenSource();
            _playbackCancellation = playbackCancellation;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                requireActiveVoiceMode ? _sessionCancellation?.Token ?? CancellationToken.None : CancellationToken.None,
                playbackCancellation.Token);
            _speaking = true;
            _status = "Sprachausgabe wird erzeugt";
            _error = null;
            lock (_playbackGate)
            {
                _playbackProgress = progress;
                _activePlaybackSegmentIndex = -1;
            }
            RaiseChanged();
            ActivateMediaTransportControls();

            var playbackBatches = SpeechSourceSegmentation.CreatePlaybackBatches(playable);
            var firstBatch = playbackBatches[0];
            var firstSegment = firstBatch.SegmentIndexes.Count > 0
                ? firstBatch.SegmentIndexes[0]
                : 0;
            await NotifyPlaybackProgressAsync(
                progress,
                new(
                    firstSegment,
                    SpeechPlaybackState.Buffering,
                    speechProvider,
                    firstBatch.SegmentIndexes)).ConfigureAwait(false);

            speechProvider = await RunSpeechPlaybackAsync(
                client,
                profile,
                playable,
                playbackBatches,
                progress,
                linked.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            _error = FriendlyError(exception);
            _status = "Vorlesen fehlgeschlagen";
            VoiceFailed(logger, exception.GetType().Name, exception);
            RaiseChanged();
            throw new InvalidOperationException(_error, exception);
        }
        finally
        {
            playbackCancellation?.Cancel();
            lock (_playbackGate)
            {
                _playbackProgress = null;
                _activePlaybackSegmentIndex = -1;
                _activePlaybackSegmentIndexes = [];
            }
            DeactivateMediaTransportControls();
            if (ReferenceEquals(_playbackCancellation, playbackCancellation))
            {
                _playbackCancellation?.Dispose();
                _playbackCancellation = null;
            }
            _speaking = false;
            if (_active)
            {
                _status = "Ich höre zu";
            }
            RaiseChanged();
            _speechGate.Release();
        }

        return speechProvider;
    }

    public Task StopSpeechAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _playbackCancellation?.Cancel();
        StopPlayback();
        return Task.CompletedTask;
    }

    public async Task<MicrophoneSnapshot> ToggleSpeechPauseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var changed = false;
        Func<SpeechSegmentPlaybackUpdate, Task>? progress = null;
        SpeechSegmentPlaybackUpdate? playbackUpdate = null;
        lock (_playbackGate)
        {
            if (!_speaking || !_canPauseSpeech || _activeOutput is null)
            {
                return Current;
            }

            try
            {
                if (_speechPaused)
                {
                    _activeOutput.Play();
                    _speechPaused = false;
                    _status = "AI-Antwort wird vorgelesen";
                    playbackUpdate = new(
                        _activePlaybackSegmentIndex,
                        SpeechPlaybackState.Playing,
                        _provider,
                        _activePlaybackSegmentIndexes);
                }
                else
                {
                    _activeOutput.Pause();
                    _speechPaused = true;
                    _status = "Vorlesen pausiert";
                    playbackUpdate = new(
                        _activePlaybackSegmentIndex,
                        SpeechPlaybackState.Paused,
                        _provider,
                        _activePlaybackSegmentIndexes);
                }
                progress = _playbackProgress;
                changed = true;
            }
            catch (ObjectDisposedException)
            {
                _activeOutput = null;
                _canPauseSpeech = false;
                _speechPaused = false;
            }
        }

        if (changed)
        {
            SetMediaTransportPlaybackState(Current.IsSpeechPaused);
            RaiseChanged();
            if (progress is not null && playbackUpdate is not null)
            {
                await progress(playbackUpdate).ConfigureAwait(false);
            }
        }
        return Current;
    }

    public Task StopVoiceControlAsync(CancellationToken cancellationToken = default) =>
        StopAsync(stopSpeech: false, cancellationToken: cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        StopAsync(stopSpeech: true, cancellationToken: cancellationToken);

    private async Task StopAsync(
        bool stopSpeech,
        CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_active && !_starting)
            {
                return;
            }

            _active = false;
            _starting = false;
            _stopping = true;
            _status = "Sprachsteuerung wird beendet";
            _sessionCancellation?.Cancel();
            if (stopSpeech)
            {
                StopPlayback();
            }
            RaiseChanged();

            await _chunkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_client is not null && _serverSessionId is not null)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    try
                    {
                        _ = await _client.StopLiveCaptionSessionAsync(
                            _serverSessionId,
                            timeout.Token).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        VoiceFailed(logger, "session_stop", exception);
                    }
                }
            }
            finally
            {
                _chunkGate.Release();
            }

            var keepAlive = _keepAliveTask;
            if (keepAlive is not null)
            {
                try { await keepAlive.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            if (stopSpeech)
            {
                await _speechGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                _speechGate.Release();
            }
            ReleaseSession();
            _turnId = null;
            _turnTranscript = string.Empty;
            _nextTurnChunk = 0;
            _recognizing = false;
            if (stopSpeech)
            {
                _speaking = false;
            }
            _stopping = false;
            _startedAt = null;
            _status = _speaking ? "AI-Antwort wird vorgelesen" : "Inaktiv";
            _error = null;
            RaiseChanged();
            VoiceStopped(logger, null);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public Task CancelAsync(CancellationToken cancellationToken = default) => StopAsync(cancellationToken);

    private async Task KeepAliveAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(KeepAliveInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var client = _client;
                var sessionId = _serverSessionId;
                if (client is null || sessionId is null)
                {
                    return;
                }
                try
                {
                    _ = await client.KeepLiveCaptionSessionAliveAsync(sessionId, cancellationToken).ConfigureAwait(false);
                }
                catch (GoAiApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Protocol 1.0 servers built before the heartbeat endpoint stay
                    // compatible until the installed gateway is upgraded.
                    await _chunkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (_active && string.Equals(_serverSessionId, sessionId, StringComparison.Ordinal))
                        {
                            _ = await SendChunkWithRecoveryAsync(
                                client,
                                sessionId,
                                CreatePcm16Wave(new byte[MinimumPcmBytes]),
                                cancellationToken).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        _chunkGate.Release();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal voice-mode shutdown.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _error = "Die dauerhafte Sprachsitzung hat kurzzeitig die Serververbindung verloren.";
            RaiseChanged();
            VoiceFailed(logger, "keepalive", exception);
        }
    }

    private async Task<LiveCaptionChunkResponse> SendChunkWithRecoveryAsync(
        GoAiClient client,
        string sessionId,
        ReadOnlyMemory<byte> wave,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.SendLiveCaptionChunkAsync(
                sessionId,
                _serverSequence,
                wave,
                cancellationToken).ConfigureAwait(false);
            _serverSequence++;
            return response;
        }
        catch (GoAiApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            var session = await CreateServerSessionAsync(client, cancellationToken).ConfigureAwait(false);
            _serverSessionId = session.SessionId;
            _serverSequence = 0;
            var response = await client.SendLiveCaptionChunkAsync(
                session.SessionId,
                _serverSequence,
                wave,
                cancellationToken).ConfigureAwait(false);
            _serverSequence++;
            return response;
        }
    }

    private Task<LiveCaptionSessionSnapshot> CreateServerSessionAsync(
        GoAiClient client,
        CancellationToken cancellationToken)
    {
        var language = settings.Current.LiveCaptionLanguage;
        return client.CreateLiveCaptionSessionAsync(
            new LiveCaptionSessionRequest(
                string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase) ? null : language,
                LiveCaptionMode.Transcribe,
                SampleRate,
                1,
                WindowMilliseconds,
                OverlapMilliseconds),
            cancellationToken);
    }

    private async Task<string?> RunSpeechPlaybackAsync(
        GoAiClient client,
        SpeechContentProfile profile,
        IReadOnlyList<PreparedSpeechSegment> segments,
        IReadOnlyList<SpeechPlaybackBatchPlan> batches,
        Func<SpeechSegmentPlaybackUpdate, Task>? progress,
        CancellationToken cancellationToken)
    {
        var playbackBatches = batches.ToArray();
        var temporaryFiles = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        Task producer = Task.CompletedTask;
        string? speechSessionId = null;
        var speechSessionCompleted = false;
        try
        {
            var speechSession = await client.CreateSpeechSessionAsync(
                new SpeechSessionRequest(
                    profile,
                    "de"),
                cancellationToken).ConfigureAwait(false);
            speechSessionId = speechSession.SessionId;
            // Keep exactly one completed successor ready while the current
            // sentence is playing. Supertonic remains serial and never waits
            // for a complete paragraph before playback can begin.
            var channel = Channel.CreateBounded<PreparedAudioBatch>(new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });
            producer = ProduceSpeechAudioBatchesAsync(
                client,
                speechSession.SessionId,
                segments,
                playbackBatches,
                channel.Writer,
                temporaryFiles,
                cancellationToken);

            var provider = await PlayPreparedAudioBatchesAsync(
                channel.Reader,
                playbackBatches,
                temporaryFiles,
                progress,
                cancellationToken).ConfigureAwait(false);

            await producer.ConfigureAwait(false);
            _ = await client.EndSpeechSessionAsync(
                speechSession.SessionId,
                CancellationToken.None).ConfigureAwait(false);
            speechSessionCompleted = true;
            return provider;
        }
        finally
        {
            if (!speechSessionCompleted && speechSessionId is not null)
            {
                await CancelSpeechSessionAsync(client, speechSessionId).ConfigureAwait(false);
            }
            try
            {
                await producer.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Cancellation and synthesis errors are observed by the playback path.
            }
            foreach (var path in temporaryFiles.Keys)
            {
                TryDelete(path);
            }
        }
    }

    private async Task CancelSpeechSessionAsync(
        GoAiClient client,
        string speechSessionId)
    {
        try
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            _ = await client.CancelSpeechSessionAsync(
                speechSessionId,
                cleanupTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            VoiceFailed(logger, "speech_session_cleanup", exception);
        }
    }

    private static async Task ProduceSpeechAudioBatchesAsync(
        GoAiClient client,
        string speechSessionId,
        IReadOnlyList<PreparedSpeechSegment> segments,
        SpeechPlaybackBatchPlan[] batches,
        ChannelWriter<PreparedAudioBatch> writer,
        ConcurrentDictionary<string, byte> temporaryFiles,
        CancellationToken cancellationToken)
    {
        Exception? error = null;
        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "GO", "Voice");
            Directory.CreateDirectory(directory);
            for (var batchIndex = 0; batchIndex < batches.Length; batchIndex++)
            {
                var batch = batches[batchIndex];
                var paragraphParts = new List<SpeechParagraphPart>(batch.SegmentIndexes.Count);
                foreach (var index in batch.SegmentIndexes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var segment = segments[index];
                    var synthesisText = SpeechSourceSegmentation.PrepareForSynthesis(segment);
                    if (string.IsNullOrWhiteSpace(synthesisText))
                    {
                        throw new InvalidDataException("Ein vorbereitetes Sprachsegment enthält keinen vorlesbaren Text.");
                    }
                    paragraphParts.Add(new(
                        index,
                        synthesisText,
                        1.0,
                        0,
                        0));
                }
                var paragraphText = string.Join(' ', paragraphParts.Select(static part => part.Text));
                if (paragraphText.Length > SpeechSourceSegmentation.MaximumSegmentCharacters)
                {
                    throw new InvalidDataException("Ein Vorlesesatz überschreitet die zulässige Länge von 3000 Zeichen.");
                }
                var speech = await client.SynthesizeSpeechParagraphAsync(
                    speechSessionId,
                    new SpeechParagraphRequest(paragraphText, batchIndex, 1.0, paragraphParts),
                    cancellationToken).ConfigureAwait(false);
                var timingByIndex = speech.Timings?
                    .GroupBy(static timing => timing.SegmentIndex)
                    .ToDictionary(static group => group.Key, static group => group.Single());
                var preparedParts = paragraphParts.Select(part =>
                {
                    SpeechParagraphTiming? timing = null;
                    if (timingByIndex is not null)
                    {
                        _ = timingByIndex.TryGetValue(part.SegmentIndex, out timing);
                    }
                    return new PreparedAudioPart(
                        part.SegmentIndex,
                        timing?.StartSeconds,
                        timing?.EndSeconds,
                        Math.Max(1, part.Text.Length));
                }).ToArray();
                if (preparedParts.Length > 1
                    && preparedParts.Any(static part => part.StartSeconds is null || part.EndSeconds is null))
                {
                    throw new InvalidDataException(
                        "Die Satzmarkierung konnte nicht sicher aus dem erzeugten Audio ausgerichtet werden.");
                }
                var path = Path.Combine(directory, $"{Guid.NewGuid():N}.wav");
                temporaryFiles.TryAdd(path, 0);
                try
                {
                    await client.DownloadArtifactAsync(
                        speech.Artifact.ArtifactId,
                        path,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    temporaryFiles.TryRemove(path, out _);
                    TryDelete(path);
                    throw;
                }
                await writer.WriteAsync(
                    new PreparedAudioBatch(
                        batchIndex,
                        batch.Id,
                        preparedParts,
                        path,
                        speech.Provider,
                        batch.PauseAfterMilliseconds),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            error = exception;
            throw;
        }
        finally
        {
            writer.TryComplete(error);
        }
    }

    private static Task NotifyPlaybackProgressAsync(
        Func<SpeechSegmentPlaybackUpdate, Task>? progress,
        SpeechSegmentPlaybackUpdate update) =>
        progress is null ? Task.CompletedTask : progress(update);

    private async Task<string?> PlayPreparedAudioBatchesAsync(
        ChannelReader<PreparedAudioBatch> reader,
        SpeechPlaybackBatchPlan[] expectedBatches,
        ConcurrentDictionary<string, byte> temporaryFiles,
        Func<SpeechSegmentPlaybackUpdate, Task>? progress,
        CancellationToken cancellationToken)
    {
        if (expectedBatches.Length == 0)
        {
            return null;
        }

        PreparedAudioBatch firstBatch;
        try
        {
            firstBatch = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException exception)
        {
            RethrowSpeechChannelFailure(
                exception,
                    "Es wurde kein hörbarer Vorlesesatz erzeugt.");
            throw;
        }

        ValidatePreparedBatch(firstBatch, expectedBatches[0], 0);
        var playbackFormat = new WaveFormat(44_100, 16, 1);
        var buffer = new BufferedWaveProvider(playbackFormat)
        {
            BufferDuration = TimeSpan.FromMinutes(15),
            DiscardOnBufferOverflow = false,
            ReadFully = true,
        };
        var boundaries = new List<PreparedPlaybackBoundary>(expectedBatches.Length);
        var boundaryGate = new object();
        long writtenBytes = 0;
        var appendedCount = 0;
        var appendCompleted = false;
        string? lastProvider = null;

        void AppendBatch(PreparedAudioBatch batch)
        {
            var pcm = ReadPlaybackPcm16(batch.Path);
            var durationSeconds = pcm.Length / (double)playbackFormat.AverageBytesPerSecond;
            var timingsAreUsable = batch.Parts.Count > 0
                && batch.Parts.All(part =>
                    part.StartSeconds is >= 0
                    && part.EndSeconds > part.StartSeconds
                    && part.EndSeconds <= durationSeconds + 0.1);
            var totalWeight = Math.Max(1, batch.Parts.Sum(static part => part.Weight));
            var accumulatedWeight = 0;
            lock (boundaryGate)
            {
                var batchStart = writtenBytes;
                buffer.AddSamples(pcm, 0, pcm.Length);
                writtenBytes += pcm.Length;
                var batchEnd = writtenBytes;
                long previousEnd = batchStart;
                for (var index = 0; index < batch.Parts.Count; index++)
                {
                    var part = batch.Parts[index];
                    long start;
                    long end;
                    if (timingsAreUsable)
                    {
                        start = batchStart + PlaybackByteOffset(
                            part.StartSeconds!.Value,
                            playbackFormat,
                            pcm.Length);
                        end = batchStart + PlaybackByteOffset(
                            part.EndSeconds!.Value,
                            playbackFormat,
                            pcm.Length);
                    }
                    else
                    {
                        start = batchStart + (long)Math.Round(
                            pcm.Length * (accumulatedWeight / (double)totalWeight),
                            MidpointRounding.AwayFromZero);
                        accumulatedWeight += part.Weight;
                        end = batchStart + (long)Math.Round(
                            pcm.Length * (accumulatedWeight / (double)totalWeight),
                            MidpointRounding.AwayFromZero);
                    }
                    start = Math.Clamp(start, previousEnd, batchEnd);
                    end = index == batch.Parts.Count - 1
                        ? batchEnd
                        : Math.Clamp(
                            end,
                            Math.Min(batchEnd, start + playbackFormat.BlockAlign),
                            batchEnd);
                    previousEnd = end;
                    boundaries.Add(new(batch.Index, start, end, part, batch.Provider));
                }
                var pauseBytes = playbackFormat.AverageBytesPerSecond * batch.PauseAfterMilliseconds / 1_000;
                pauseBytes -= pauseBytes % playbackFormat.BlockAlign;
                if (pauseBytes > 0)
                {
                    buffer.AddSamples(new byte[pauseBytes], 0, pauseBytes);
                    writtenBytes += pauseBytes;
                }
                appendedCount++;
            }
            lastProvider = batch.Provider;
            temporaryFiles.TryRemove(batch.Path, out _);
            TryDelete(batch.Path);
        }

        AppendBatch(firstBatch);
        var appender = Task.Run(async () =>
        {
            try
            {
                for (var index = 1; index < expectedBatches.Length; index++)
                {
                    while (buffer.BufferedDuration > TimeSpan.FromMinutes(7))
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
                    }
                    PreparedAudioBatch batch;
                    try
                    {
                        batch = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (ChannelClosedException exception)
                    {
                        RethrowSpeechChannelFailure(
                            exception,
                            "Nicht alle Vorlesesätze wurden erzeugt.");
                        throw;
                    }
                    ValidatePreparedBatch(batch, expectedBatches[index], index);
                    AppendBatch(batch);
                }
            }
            finally
            {
                Volatile.Write(ref appendCompleted, true);
            }
        }, cancellationToken);

        using var output = new WaveOutEvent { DesiredLatency = 120, NumberOfBuffers = 3 };
        var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, args) => stopped.TrySetResult(args.Exception);
        output.Init(buffer);
        lock (_playbackGate)
        {
            _activeOutput = output;
            _canPauseSpeech = true;
            _speechPaused = false;
            _status = "AI-Antwort wird vorgelesen";
        }
        RaiseChanged();

        var activeSegment = -1;
        var automaticallyPaused = false;
        try
        {
            output.Play();
            SetMediaTransportPlaybackState(paused: false);
            while (!stopped.Task.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outputPosition = output.GetPosition();
                var position = Math.Min(outputPosition, Interlocked.Read(ref writtenBytes));
                PreparedPlaybackBoundary? current = null;
                lock (boundaryGate)
                {
                    for (var index = boundaries.Count - 1; index >= 0; index--)
                    {
                        if (position >= boundaries[index].StartByte)
                        {
                            current = boundaries[index];
                            break;
                        }
                    }
                }
                if (current is not null && current.Part.Index != activeSegment)
                {
                    activeSegment = current.Part.Index;
                    var snapshotChanged = false;
                    lock (_playbackGate)
                    {
                        _activePlaybackSegmentIndex = current.Part.Index;
                        _activePlaybackSegmentIndexes = [current.Part.Index];
                        snapshotChanged = !string.Equals(_provider, current.Provider, StringComparison.Ordinal)
                            || !string.Equals(_status, "AI-Antwort wird vorgelesen", StringComparison.Ordinal);
                        _provider = current.Provider;
                        _status = "AI-Antwort wird vorgelesen";
                    }
                    // A sentence boundary is carried by speech.progress. Re-emitting
                    // the otherwise unchanged microphone snapshot here caused the
                    // WebView to rebuild every message between the old and new
                    // highlight and made the marker visibly jump.
                    if (snapshotChanged)
                    {
                        RaiseChanged();
                    }
                    await NotifyPlaybackProgressAsync(
                        progress,
                        new(
                            current.Part.Index,
                            SpeechPlaybackState.Playing,
                            current.Provider,
                            [current.Part.Index])).ConfigureAwait(false);
                }

                bool allAppended;
                lock (boundaryGate)
                {
                    allAppended = Volatile.Read(ref appendCompleted)
                        && appendedCount == expectedBatches.Length;
                }
                if (appender.IsCompleted && !allAppended)
                {
                    await appender.ConfigureAwait(false);
                    throw new InvalidDataException("Die Vorlesepipeline wurde vor dem letzten Satz beendet.");
                }
                if (allAppended && position >= Interlocked.Read(ref writtenBytes))
                {
                    output.Stop();
                    break;
                }

                bool userPaused;
                lock (_playbackGate)
                {
                    userPaused = _speechPaused;
                }
                if (!allAppended
                    && !userPaused
                    && !automaticallyPaused
                    && buffer.BufferedDuration < TimeSpan.FromMilliseconds(160))
                {
                    output.Pause();
                    automaticallyPaused = true;
                }
                else if (automaticallyPaused
                    && !userPaused
                    && (buffer.BufferedDuration >= TimeSpan.FromMilliseconds(500) || allAppended))
                {
                    output.Play();
                    automaticallyPaused = false;
                }

                await Task.WhenAny(
                    stopped.Task,
                    Task.Delay(TimeSpan.FromMilliseconds(30), cancellationToken)).ConfigureAwait(false);
            }
            await appender.ConfigureAwait(false);
            var error = await stopped.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (error is not null)
            {
                throw error;
            }
            return lastProvider;
        }
        finally
        {
            lock (_playbackGate)
            {
                if (ReferenceEquals(_activeOutput, output))
                {
                    _activeOutput = null;
                    _canPauseSpeech = false;
                    _speechPaused = false;
                    _activePlaybackSegmentIndexes = [];
                }
            }
            RaiseChanged();
        }
    }

    private static void ValidatePreparedBatch(
        PreparedAudioBatch batch,
        SpeechPlaybackBatchPlan expected,
        int expectedIndex)
    {
        if (batch.Index != expectedIndex
            || !batch.Parts.Select(static part => part.Index).SequenceEqual(expected.SegmentIndexes))
        {
            throw new InvalidDataException("Die vorbereiteten Sprachabschnitte sind nicht in der erwarteten Reihenfolge eingetroffen.");
        }
    }

    private static byte[] ReadPlaybackPcm16(string path)
    {
        using var reader = new WaveFileReader(path);
        ValidateAudibleWave(reader);
        reader.Position = 0;
        var provider = CreatePlaybackSampleProvider(reader).ToWaveProvider16();
        using var output = new MemoryStream();
        var chunk = new byte[provider.WaveFormat.AverageBytesPerSecond];
        int read;
        while ((read = provider.Read(chunk, 0, chunk.Length)) > 0)
        {
            output.Write(chunk, 0, read);
        }
        return output.ToArray();
    }

    private static int PlaybackByteOffset(double seconds, WaveFormat format, int maximumBytes)
    {
        var value = (int)Math.Clamp(
            Math.Round(seconds * format.AverageBytesPerSecond, MidpointRounding.AwayFromZero),
            0,
            maximumBytes);
        return value - (value % format.BlockAlign);
    }

    private static void RethrowSpeechChannelFailure(
        ChannelClosedException exception,
        string fallbackMessage)
    {
        if (exception.InnerException is { } inner)
        {
            ExceptionDispatchInfo.Capture(inner).Throw();
        }
        throw new InvalidDataException(fallbackMessage, exception);
    }

    private static ISampleProvider CreatePlaybackSampleProvider(WaveFileReader reader)
    {
        ISampleProvider provider = reader.ToSampleProvider();
        provider = provider.WaveFormat.Channels switch
        {
            1 => provider,
            2 => new StereoToMonoSampleProvider(provider)
            {
                LeftVolume = 0.5f,
                RightVolume = 0.5f,
            },
            _ => throw new InvalidDataException("Die erzeugte Sprachausgabe besitzt ein nicht unterstütztes Kanalformat."),
        };
        if (provider.WaveFormat.SampleRate != 44_100)
        {
            provider = new WdlResamplingSampleProvider(provider, 44_100);
        }
        return provider;
    }

    internal static void ValidateAudibleWave(WaveFileReader reader)
    {
        if (reader.TotalTime < TimeSpan.FromMilliseconds(100))
        {
            throw new InvalidDataException("Die erzeugte Audiodatei ist zu kurz.");
        }
        var provider = reader.ToSampleProvider();
        var samples = new float[Math.Min(reader.WaveFormat.SampleRate, 48_000)];
        double energy = 0;
        var count = 0;
        int read;
        while (count < samples.Length && (read = provider.Read(samples, count, samples.Length - count)) > 0)
        {
            for (var index = count; index < count + read; index++)
            {
                energy += samples[index] * samples[index];
            }
            count += read;
        }
        if (count == 0 || Math.Sqrt(energy / count) < 0.0005)
        {
            throw new InvalidDataException("Die erzeugte Audiodatei enthält kein hörbares Signal.");
        }
    }

    private void StopPlayback()
    {
        lock (_playbackGate)
        {
            try { _activeOutput?.Stop(); }
            catch (ObjectDisposedException) { }
            _speechPaused = false;
        }
        DeactivateMediaTransportControls();
    }

    private void ActivateMediaTransportControls()
    {
        try
        {
            _mediaTransport ??= new SpeechMediaTransportController(
                HandleMediaTransportCommandAsync,
                exception => MediaTransportFailed(logger, exception.GetType().Name, exception));
            _mediaTransport.Activate();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            MediaTransportFailed(logger, exception.GetType().Name, exception);
            _mediaTransport?.Dispose();
            _mediaTransport = null;
        }
    }

    private void SetMediaTransportPlaybackState(bool paused)
    {
        try
        {
            _mediaTransport?.SetPlaying(paused);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            MediaTransportFailed(logger, exception.GetType().Name, exception);
        }
    }

    private void DeactivateMediaTransportControls()
    {
        try
        {
            _mediaTransport?.Deactivate();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            MediaTransportFailed(logger, exception.GetType().Name, exception);
        }
    }

    private async Task HandleMediaTransportCommandAsync(SpeechMediaTransportCommand command)
    {
        var current = Current;
        switch (command)
        {
            case SpeechMediaTransportCommand.Play when current.IsSpeaking && current.IsSpeechPaused:
            case SpeechMediaTransportCommand.Pause when current.IsSpeaking && !current.IsSpeechPaused:
                _ = await ToggleSpeechPauseAsync(CancellationToken.None).ConfigureAwait(false);
                break;
        }
    }

    private void ReleaseSession()
    {
        _sessionCancellation?.Cancel();
        _sessionCancellation?.Dispose();
        _sessionCancellation = null;
        _keepAliveTask = null;
        _client?.Dispose();
        _client = null;
        _serverSessionId = null;
        _serverSequence = 0;
    }

    internal static byte[] CreatePcm16Wave(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length is < MinimumPcmBytes or > MaximumPcmBytes || (pcm.Length & 1) != 0)
        {
            throw new ArgumentException("Das Mikrofon-Audiofenster muss 0,2 bis 3 Sekunden PCM16 enthalten.", nameof(pcm));
        }
        var wave = new byte[44 + pcm.Length];
        "RIFF"u8.CopyTo(wave);
        BitConverter.TryWriteBytes(wave.AsSpan(4, 4), 36 + pcm.Length);
        "WAVEfmt "u8.CopyTo(wave.AsSpan(8));
        BitConverter.TryWriteBytes(wave.AsSpan(16, 4), 16);
        BitConverter.TryWriteBytes(wave.AsSpan(20, 2), (short)1);
        BitConverter.TryWriteBytes(wave.AsSpan(22, 2), (short)1);
        BitConverter.TryWriteBytes(wave.AsSpan(24, 4), SampleRate);
        BitConverter.TryWriteBytes(wave.AsSpan(28, 4), SampleRate * sizeof(short));
        BitConverter.TryWriteBytes(wave.AsSpan(32, 2), (short)sizeof(short));
        BitConverter.TryWriteBytes(wave.AsSpan(34, 2), (short)16);
        "data"u8.CopyTo(wave.AsSpan(36));
        BitConverter.TryWriteBytes(wave.AsSpan(40, 4), pcm.Length);
        pcm.CopyTo(wave.AsSpan(44));
        return wave;
    }

    private static byte[] DecodePcm(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 150_000)
        {
            throw new ArgumentException("Das Mikrofon-Audiofenster ist leer oder zu groß.", nameof(value));
        }
        try
        {
            var pcm = Convert.FromBase64String(value);
            if (pcm.Length is < MinimumPcmBytes or > MaximumPcmBytes || (pcm.Length & 1) != 0)
            {
                throw new ArgumentException("Das Mikrofon-Audiofenster liegt außerhalb der erlaubten Dauer.", nameof(value));
            }
            return pcm;
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Das Mikrofon-Audiofenster ist nicht gültig codiert.", nameof(value), exception);
        }
    }

    private static string AppendSegment(string current, string next)
    {
        var value = next.Trim();
        if (value.Length == 0)
        {
            return current;
        }
        return current.Length == 0 ? value : $"{current} {value}";
    }

    internal static string PlainSpeechText(LiveCaptionChunkResponse response)
    {
        var plain = string.Join(' ', response.Segments
            .Select(static segment => segment.Text.Trim())
            .Where(static text => text.Length > 0));
        return plain.Length > 0
            ? plain
            : SpeakerPrefixRegex().Replace(response.Text ?? string.Empty, string.Empty).Trim();
    }

    internal static string PrepareSpeechText(string markdown)
    {
        var text = FencedCodeRegex().Replace(markdown ?? string.Empty, " Codeblock ausgelassen. ");
        text = ImageRegex().Replace(text, "$1");
        text = LinkRegex().Replace(text, "$1");
        text = TableSeparatorRegex().Replace(text, string.Empty);
        text = HeadingOrListRegex().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = GermanSpeechTextNormalizer.Normalize(text);
        text = MarkdownMarkerRegex().Replace(text, string.Empty);
        text = text.Replace('|', ',');
        return WhitespaceRegex().Replace(text, " ").Trim();
    }

    private void RaiseChanged() => Changed?.Invoke(this, Current);

    private sealed record PreparedAudioBatch(
        int Index,
        string Id,
        IReadOnlyList<PreparedAudioPart> Parts,
        string Path,
        string Provider,
        int PauseAfterMilliseconds);

    private sealed record PreparedAudioPart(
        int Index,
        double? StartSeconds,
        double? EndSeconds,
        int Weight);

    private sealed record PreparedPlaybackBoundary(
        int BatchIndex,
        long StartByte,
        long EndByte,
        PreparedAudioPart Part,
        string Provider);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string FriendlyError(Exception exception) => exception switch
    {
        GoAiApiException apiException => apiException.Problem?.Detail ?? apiException.Message,
        HttpRequestException => "GO AI Server ist für die Spracherkennung nicht erreichbar.",
        TimeoutException => "Die Spracherkennung hat nicht rechtzeitig geantwortet.",
        _ => exception.Message,
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        try { StopAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            VoiceFailed(logger, exception.GetType().Name, exception);
        }
        _disposed = true;
        StopPlayback();
        _mediaTransport?.Dispose();
        _mediaTransport = null;
        ReleaseSession();
        _lifecycleGate.Dispose();
        _chunkGate.Dispose();
        _speechGate.Dispose();
    }

    [GeneratedRegex(@"```[\s\S]*?```", RegexOptions.CultureInvariant)]
    private static partial Regex FencedCodeRegex();

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex ImageRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"(?m)^\s*\|?\s*:?-{3,}.*$", RegexOptions.CultureInvariant)]
    private static partial Regex TableSeparatorRegex();

    [GeneratedRegex(@"(?m)^\s*(?:#{1,6}|[-*+]\s+|\d+[.)]\s+|>\s*)", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingOrListRegex();

    [GeneratedRegex(@"[*_~`]", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownMarkerRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"(?:^|\r?\n)\s*Person\s+\d+\s*:\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SpeakerPrefixRegex();

    private static readonly Action<ILogger, string?, Exception?> VoiceStarted =
        LoggerMessage.Define<string?>(LogLevel.Information, new EventId(5310, nameof(VoiceStarted)),
            "Browser microphone conversation started ({DeviceLabel}).");
    private static readonly Action<ILogger, Exception?> VoiceStopped =
        LoggerMessage.Define(LogLevel.Information, new EventId(5311, nameof(VoiceStopped)),
            "Browser microphone conversation stopped.");
    private static readonly Action<ILogger, string, Exception?> VoiceFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(5312, nameof(VoiceFailed)),
            "Browser microphone conversation failed ({FailureKind}).");
    private static readonly Action<ILogger, string, Exception?> MediaTransportFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(5313, nameof(MediaTransportFailed)),
            "Windows speech media controls failed ({FailureKind}).");
}
