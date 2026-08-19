using GoAi.Client;
using GoAi.Contracts;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Collections.Concurrent;
using System.Net;
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
    private Func<SpeechSegmentPlaybackUpdate, Task>? _playbackProgress;
    private int _activePlaybackSegmentIndex = -1;
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
        Func<int, PreparedSpeechSegment, CancellationToken, ValueTask<PreparedSpeechSegment>>? segmentResolver = null,
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
        Task producer = Task.CompletedTask;
        CancellationTokenSource? playbackCancellation = null;
        var temporaryFiles = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (requireActiveVoiceMode && !_active)
            {
                return null;
            }
            using var standaloneClient = _client is null
                ? await connection.CreateClientAsync(cancellationToken).ConfigureAwait(false)
                : null;
            var client = _client ?? standaloneClient!;
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

            var playbackBatches = SpeechSourceSegmentation.CreatePlaybackBatches(playable);
            var channel = Channel.CreateBounded<PreparedAudioBatch>(new BoundedChannelOptions(2)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });
            producer = ProduceSpeechAudioBatchesAsync(
                client,
                playable,
                playbackBatches,
                channel.Writer,
                temporaryFiles,
                segmentResolver,
                linked.Token);

            await NotifyPlaybackProgressAsync(progress, new(0, SpeechPlaybackState.Buffering)).ConfigureAwait(false);
            for (var batchIndex = 0; batchIndex < playbackBatches.Count; batchIndex++)
            {
                linked.Token.ThrowIfCancellationRequested();
                PreparedAudioBatch preparedBatch;
                try
                {
                    preparedBatch = await channel.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
                }
                catch (ChannelClosedException)
                {
                    await producer.ConfigureAwait(false);
                    throw;
                }
                var expectedBatch = playbackBatches[batchIndex];
                if (preparedBatch.Index != batchIndex
                    || !preparedBatch.Parts.Select(static part => part.Index).SequenceEqual(expectedBatch.SegmentIndexes))
                {
                    throw new InvalidDataException("Die vorbereiteten Sprachabschnitte sind nicht in der erwarteten Reihenfolge eingetroffen.");
                }

                speechProvider = await PlayPreparedAudioBatchAsync(
                    preparedBatch,
                    progress,
                    linked.Token).ConfigureAwait(false) ?? speechProvider;
                foreach (var prepared in preparedBatch.Parts)
                {
                    temporaryFiles.TryRemove(prepared.Path, out _);
                    TryDelete(prepared.Path);
                }
            }
            await producer.ConfigureAwait(false);
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
            try
            {
                await producer.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // The producer failure was already observed by the consumer or cancellation path.
            }
            foreach (var path in temporaryFiles.Keys)
            {
                TryDelete(path);
            }
            lock (_playbackGate)
            {
                _playbackProgress = null;
                _activePlaybackSegmentIndex = -1;
            }
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
                    playbackUpdate = new(_activePlaybackSegmentIndex, SpeechPlaybackState.Playing, _provider);
                }
                else
                {
                    _activeOutput.Pause();
                    _speechPaused = true;
                    _status = "Vorlesen pausiert";
                    playbackUpdate = new(_activePlaybackSegmentIndex, SpeechPlaybackState.Paused, _provider);
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
            RaiseChanged();
            if (progress is not null && playbackUpdate is not null)
            {
                await progress(playbackUpdate).ConfigureAwait(false);
            }
        }
        return Current;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
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
            StopPlayback();
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
            await _speechGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            _speechGate.Release();
            ReleaseSession();
            _turnId = null;
            _turnTranscript = string.Empty;
            _nextTurnChunk = 0;
            _recognizing = false;
            _speaking = false;
            _stopping = false;
            _startedAt = null;
            _status = "Inaktiv";
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

    private static async Task ProduceSpeechAudioBatchesAsync(
        GoAiClient client,
        IReadOnlyList<PreparedSpeechSegment> segments,
        IReadOnlyList<SpeechPlaybackBatchPlan> batches,
        ChannelWriter<PreparedAudioBatch> writer,
        ConcurrentDictionary<string, byte> temporaryFiles,
        Func<int, PreparedSpeechSegment, CancellationToken, ValueTask<PreparedSpeechSegment>>? segmentResolver,
        CancellationToken cancellationToken)
    {
        Exception? error = null;
        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "GO", "Voice");
            Directory.CreateDirectory(directory);
            for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
            {
                var batch = batches[batchIndex];
                var preparedParts = new List<PreparedAudioPart>(batch.SegmentIndexes.Count);
                foreach (var index in batch.SegmentIndexes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var segment = segmentResolver is null
                        ? segments[index]
                        : await segmentResolver(index, segments[index], cancellationToken).ConfigureAwait(false);
                    var synthesisText = SpeechSourceSegmentation.PrepareForSynthesis(segment);
                    if (string.IsNullOrWhiteSpace(synthesisText))
                    {
                        throw new InvalidDataException("Ein vorbereitetes Sprachsegment enthält keinen vorlesbaren Text.");
                    }
                    var speech = await client.SynthesizeSpeechAsync(
                        new SpeechRequest(
                            synthesisText,
                            Speed: segment.Speed),
                        cancellationToken).ConfigureAwait(false);
                    var path = Path.Combine(directory, $"{Guid.NewGuid():N}.wav");
                    temporaryFiles.TryAdd(path, 0);
                    try
                    {
                        await client.DownloadArtifactAsync(
                            speech.Artifact.ArtifactId,
                            path,
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                        preparedParts.Add(new(index, path, speech.Provider));
                    }
                    catch
                    {
                        temporaryFiles.TryRemove(path, out _);
                        TryDelete(path);
                        throw;
                    }
                }
                await writer.WriteAsync(
                    new PreparedAudioBatch(batchIndex, batch.Id, preparedParts),
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

    private async Task<string?> PlayPreparedAudioBatchAsync(
        PreparedAudioBatch batch,
        Func<SpeechSegmentPlaybackUpdate, Task>? progress,
        CancellationToken cancellationToken)
    {
        if (batch.Parts.Count == 0)
        {
            return null;
        }

        var readers = new List<WaveFileReader>(batch.Parts.Count);
        try
        {
            var providers = new List<ISampleProvider>(batch.Parts.Count);
            var cumulativeDurations = new List<TimeSpan>(batch.Parts.Count);
            var elapsed = TimeSpan.Zero;
            foreach (var part in batch.Parts)
            {
                var reader = new WaveFileReader(part.Path);
                readers.Add(reader);
                ValidateAudibleWave(reader);
                reader.Position = 0;
                providers.Add(CreatePlaybackSampleProvider(reader));
                elapsed += reader.TotalTime;
                cumulativeDurations.Add(elapsed);
            }

            var concatenated = new ConcatenatingSampleProvider(providers);
            using var output = new WaveOutEvent();
            var completion = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            output.PlaybackStopped += (_, args) => completion.TrySetResult(args.Exception);
            output.Init(concatenated.ToWaveProvider());

            var activePart = -1;
            async Task ActivatePartAsync(int partIndex)
            {
                if (partIndex == activePart || partIndex < 0 || partIndex >= batch.Parts.Count)
                {
                    return;
                }

                activePart = partIndex;
                var part = batch.Parts[partIndex];
                lock (_playbackGate)
                {
                    _activePlaybackSegmentIndex = part.Index;
                    _provider = part.Provider;
                    _status = "AI-Antwort wird vorgelesen";
                }
                RaiseChanged();
                await NotifyPlaybackProgressAsync(
                    progress,
                    new(part.Index, SpeechPlaybackState.Playing, part.Provider)).ConfigureAwait(false);
            }

            lock (_playbackGate)
            {
                _activeOutput = output;
                _canPauseSpeech = true;
                _speechPaused = false;
                _status = "AI-Antwort wird vorgelesen";
            }
            await ActivatePartAsync(0).ConfigureAwait(false);
            using var registration = cancellationToken.Register(static value =>
            {
                try { ((WaveOutEvent)value!).Stop(); }
                catch (ObjectDisposedException) { }
            }, output);

            Exception? error = null;
            try
            {
                output.Play();
                while (!completion.Task.IsCompleted)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var position = TimeSpan.FromSeconds(
                        output.GetPosition() / (double)output.OutputWaveFormat.AverageBytesPerSecond);
                    var nextPart = 0;
                    while (nextPart < cumulativeDurations.Count - 1
                        && position >= cumulativeDurations[nextPart])
                    {
                        nextPart++;
                    }
                    await ActivatePartAsync(nextPart).ConfigureAwait(false);
                    await Task.WhenAny(
                        completion.Task,
                        Task.Delay(TimeSpan.FromMilliseconds(35), cancellationToken)).ConfigureAwait(false);
                }
                error = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
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
                    }
                }
                RaiseChanged();
            }
            if (error is not null)
            {
                throw error;
            }
            return batch.Parts[^1].Provider;
        }
        finally
        {
            foreach (var reader in readers)
            {
                reader.Dispose();
            }
        }
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

    private sealed record PreparedAudioPart(
        int Index,
        string Path,
        string Provider);

    private sealed record PreparedAudioBatch(
        int Index,
        string Id,
        IReadOnlyList<PreparedAudioPart> Parts);

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
}
