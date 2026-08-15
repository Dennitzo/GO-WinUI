using GoAi.Client;
using GoAi.Contracts;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Buffers.Binary;

namespace GoWinUI.App.Services;

public sealed record SystemAudioCaptionSnapshot(
    bool IsActive,
    string Mode,
    string Status,
    string Transcript,
    string? Provider,
    string? Error,
    DateTimeOffset? StartedAt);

public sealed class SystemAudioCaptionService(
    GoAiConnectionService connection,
    ILogger<SystemAudioCaptionService> logger) : IDisposable
{
    private const int TargetSampleRate = GoAiProtocol.LiveCaptionSampleRate;
    private const int WindowMilliseconds = 4_000;
    private const int OverlapMilliseconds = 500;
    private const int WindowSamples = TargetSampleRate * WindowMilliseconds / 1_000;
    private const int OverlapSamples = TargetSampleRate * OverlapMilliseconds / 1_000;
    private const int MinimumFinalSamples = TargetSampleRate / 5;
    private const int MaximumVisibleTranscriptLength = 100_000;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _audioAvailable = new(0, 1);
    private readonly List<float> _samples = new(WindowSamples * 2);
    private CancellationTokenSource? _sessionCancellation;
    private WasapiLoopbackCapture? _capture;
    private BufferedWaveProvider? _audioBuffer;
    private ISampleProvider? _resampledAudio;
    private GoAi.Client.GoAiClient? _client;
    private Task? _processor;
    private string? _serverSessionId;
    private long _sequence;
    private bool _finishing;
    private bool _starting;
    private bool _sentWindow;
    private bool _disposed;
    private string _mode = "transcribe";
    private string _status = "Inaktiv";
    private string _transcript = string.Empty;
    private string? _provider;
    private string? _error;
    private DateTimeOffset? _startedAt;

    public event EventHandler<SystemAudioCaptionSnapshot>? Changed;

    public bool IsRunning => _starting || _capture is not null;

    public SystemAudioCaptionSnapshot Current => new(
        IsRunning,
        _mode,
        _status,
        VisibleTranscript(_transcript),
        _provider,
        _error,
        _startedAt);

    public async Task StartAsync(LiveCaptionMode mode, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_capture is not null)
            {
                throw new InvalidOperationException("Systemaudio-Untertitel laufen bereits. Beende sie vor dem Moduswechsel.");
            }

            _mode = mode == LiveCaptionMode.TranslateToEnglish ? "translateToEnglish" : "transcribe";
            _starting = true;
            _status = "Wird gestartet";
            _transcript = string.Empty;
            _provider = null;
            _error = null;
            _startedAt = DateTimeOffset.Now;
            _sequence = 0;
            _finishing = false;
            _sentWindow = false;
            _samples.Clear();
            RaiseChanged();

            var client = await connection.CreateClientAsync(cancellationToken).ConfigureAwait(false);
            _client = client;
            try
            {
                var session = await client.CreateLiveCaptionSessionAsync(
                    new LiveCaptionSessionRequest(
                        // Systemaudio muss die Quellsprache erkennen, damit Englisch,
                        // Koreanisch und weitere Sprachen zuverlässig nach Deutsch gelangen.
                        Language: null,
                        mode,
                        TargetSampleRate,
                        1,
                        WindowMilliseconds,
                        OverlapMilliseconds),
                    cancellationToken).ConfigureAwait(false);
                _serverSessionId = session.SessionId;

                var capture = new WasapiLoopbackCapture();
                var buffer = new BufferedWaveProvider(capture.WaveFormat)
                {
                    BufferDuration = TimeSpan.FromSeconds(20),
                    DiscardOnBufferOverflow = true,
                    ReadFully = false,
                };
                ISampleProvider sampleProvider = buffer.ToSampleProvider();
                if (sampleProvider.WaveFormat.Channels != 1)
                {
                    sampleProvider = new DownmixSampleProvider(sampleProvider);
                }
                if (sampleProvider.WaveFormat.SampleRate != TargetSampleRate)
                {
                    sampleProvider = new WdlResamplingSampleProvider(sampleProvider, TargetSampleRate);
                }

                _sessionCancellation = new CancellationTokenSource();
                _capture = capture;
                _audioBuffer = buffer;
                _resampledAudio = sampleProvider;
                capture.DataAvailable += OnDataAvailable;
                capture.RecordingStopped += OnRecordingStopped;
                _processor = Task.Run(() => ProcessAudioAsync(_sessionCancellation.Token), CancellationToken.None);
                capture.StartRecording();
                _starting = false;
                _status = mode == LiveCaptionMode.TranslateToEnglish
                    ? "Live-Übersetzung ins Englische"
                    : "Live-Untertitel aktiv";
                RaiseChanged();
                CaptionStarted(logger, _mode, capture.WaveFormat.SampleRate, capture.WaveFormat.Channels, null);
            }
            catch
            {
                await CleanupFailedStartAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            _starting = false;
            _status = "Fehler";
            _error = FriendlyError(exception);
            RaiseChanged();
            CaptionFailed(logger, exception.GetType().Name, exception);
            throw new InvalidOperationException(_error, exception);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<SystemAudioCaptionSnapshot> StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
            return Current;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void ClearCompleted()
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("Aktive Live-Untertitel können nicht geleert werden.");
        }
        _mode = "transcribe";
        _status = "Inaktiv";
        _transcript = string.Empty;
        _provider = null;
        _error = null;
        _startedAt = null;
        RaiseChanged();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        if (_finishing || args.BytesRecorded <= 0)
        {
            return;
        }

        try
        {
            _audioBuffer?.AddSamples(args.Buffer, 0, args.BytesRecorded);
            SignalAudioAvailable();
        }
        catch (InvalidOperationException exception)
        {
            CaptionBufferFailed(logger, exception);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is not null && !_finishing)
        {
            _error = FriendlyError(args.Exception);
            _status = "Audioaufnahme unterbrochen";
            CaptionFailed(logger, args.Exception.GetType().Name, args.Exception);
            RaiseChanged();
        }
        SignalAudioAvailable();
    }

    private async Task ProcessAudioAsync(CancellationToken cancellationToken)
    {
        var output = new float[TargetSampleRate];
        try
        {
            while (true)
            {
                await _audioAvailable.WaitAsync(cancellationToken).ConfigureAwait(false);
                DrainResampler(output);
                while (_samples.Count >= WindowSamples)
                {
                    await SendWindowAsync(_samples.GetRange(0, WindowSamples), cancellationToken).ConfigureAwait(false);
                    _samples.RemoveRange(0, WindowSamples - OverlapSamples);
                    _sentWindow = true;
                }

                if (_finishing)
                {
                    DrainResampler(output);
                    var minimum = _sentWindow ? OverlapSamples + MinimumFinalSamples : MinimumFinalSamples;
                    if (_samples.Count >= minimum)
                    {
                        await SendWindowAsync(_samples.ToArray(), cancellationToken).ConfigureAwait(false);
                    }
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Explicit shutdown abandons only the currently incomplete audio window.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _error = FriendlyError(exception);
            _status = "Untertitel fehlgeschlagen";
            CaptionFailed(logger, exception.GetType().Name, exception);
            _ = Task.Run(StopAfterProcessingFailureAsync, CancellationToken.None);
        }
    }

    private async Task StopAfterProcessingFailureAsync()
    {
        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            CaptionFailed(logger, $"cleanup_{exception.GetType().Name}", exception);
        }
    }

    private void DrainResampler(float[] output)
    {
        if (_resampledAudio is null)
        {
            return;
        }
        int read;
        while ((read = _resampledAudio.Read(output, 0, output.Length)) > 0)
        {
            _samples.AddRange(output.AsSpan(0, read).ToArray());
        }
    }

    private async Task SendWindowAsync(IReadOnlyList<float> samples, CancellationToken cancellationToken)
    {
        var client = _client ?? throw new InvalidOperationException("Der Untertitel-Client ist nicht verfügbar.");
        var sessionId = _serverSessionId ?? throw new InvalidOperationException("Die Untertitel-Sitzung ist nicht verfügbar.");
        _status = "Sprache wird erkannt";
        RaiseChanged();
        var wave = CreatePcm16Wave(samples);
        var response = await client.SendLiveCaptionChunkAsync(sessionId, _sequence, wave, cancellationToken).ConfigureAwait(false);
        _sequence++;
        _transcript = response.Transcript;
        _provider = response.Provider;
        _status = _mode == "translateToEnglish" ? "Live-Übersetzung aktiv" : "Live-Untertitel aktiv";
        _error = null;
        RaiseChanged();
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        if (_capture is null)
        {
            return;
        }

        _status = "Wird beendet";
        _finishing = true;
        RaiseChanged();
        try
        {
            _capture.StopRecording();
        }
        catch (InvalidOperationException)
        {
            // Capture has already stopped after a device change.
        }
        SignalAudioAvailable();

        if (_processor is not null)
        {
            try
            {
                await _processor.WaitAsync(TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _sessionCancellation?.Cancel();
                _error = "Das letzte Audiofenster konnte nicht rechtzeitig verarbeitet werden.";
            }
        }

        if (_client is not null && _serverSessionId is not null)
        {
            try
            {
                var stopped = await _client.StopLiveCaptionSessionAsync(_serverSessionId, cancellationToken).ConfigureAwait(false);
                _transcript = stopped.Transcript;
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                _error ??= FriendlyError(exception);
                CaptionFailed(logger, exception.GetType().Name, exception);
            }
        }

        ReleaseSessionResources();
        _status = _error is null ? "Beendet" : "Mit Fehler beendet";
        _finishing = false;
        CaptionStopped(logger, _sequence, null);
        RaiseChanged();
    }

    private async Task CleanupFailedStartAsync()
    {
        if (_client is not null && _serverSessionId is not null)
        {
            try
            {
                _ = await _client.StopLiveCaptionSessionAsync(_serverSessionId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                CaptionFailed(logger, exception.GetType().Name, exception);
            }
        }
        ReleaseSessionResources();
    }

    private void ReleaseSessionResources()
    {
        var capture = _capture;
        if (capture is not null)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            capture.Dispose();
        }
        _capture = null;
        _starting = false;
        _audioBuffer = null;
        _resampledAudio = null;
        _processor = null;
        _serverSessionId = null;
        _sessionCancellation?.Cancel();
        _sessionCancellation?.Dispose();
        _sessionCancellation = null;
        _client?.Dispose();
        _client = null;
        _samples.Clear();
        while (_audioAvailable.CurrentCount > 0)
        {
            _ = _audioAvailable.Wait(0);
        }
    }

    private void SignalAudioAvailable()
    {
        if (_audioAvailable.CurrentCount == 0)
        {
            try
            {
                _audioAvailable.Release();
            }
            catch (SemaphoreFullException)
            {
                // Another capture callback has already signalled the processor.
            }
        }
    }

    private void RaiseChanged() => Changed?.Invoke(this, Current);

    private static string VisibleTranscript(string transcript) => transcript.Length <= MaximumVisibleTranscriptLength
        ? transcript
        : transcript[^MaximumVisibleTranscriptLength..];

    internal static byte[] CreatePcm16Wave(IReadOnlyList<float> samples)
    {
        var dataLength = checked(samples.Count * sizeof(short));
        var result = new byte[44 + dataLength];
        "RIFF"u8.CopyTo(result);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4, 4), 36 + dataLength);
        "WAVEfmt "u8.CopyTo(result.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(24, 4), TargetSampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(28, 4), TargetSampleRate * sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(32, 2), sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(34, 2), 16);
        "data"u8.CopyTo(result.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(40, 4), dataLength);
        for (var index = 0; index < samples.Count; index++)
        {
            var value = Math.Clamp(samples[index], -1f, 1f);
            var pcm = value <= -1f ? short.MinValue : (short)Math.Round(value * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(44 + index * 2, 2), pcm);
        }
        return result;
    }

    private static string FriendlyError(Exception exception) => exception switch
    {
        NAudio.MmException => "Windows-Systemaudio konnte nicht aufgenommen werden. Prüfe das Standard-Wiedergabegerät.",
        GoAiApiException apiException => apiException.Problem?.Detail ?? apiException.Message,
        HttpRequestException => "GO AI Server ist während der Live-Untertitel nicht erreichbar.",
        _ => exception.Message,
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        try
        {
            StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            CaptionFailed(logger, exception.GetType().Name, exception);
        }
        _disposed = true;
        _lifecycleGate.Dispose();
        _audioAvailable.Dispose();
    }

    private sealed class DownmixSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private float[] _sourceBuffer = [];

        public DownmixSampleProvider(ISampleProvider source)
        {
            _source = source;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var channels = _source.WaveFormat.Channels;
            var required = checked(count * channels);
            if (_sourceBuffer.Length < required)
            {
                _sourceBuffer = new float[required];
            }
            var sourceRead = _source.Read(_sourceBuffer, 0, required);
            var frames = sourceRead / channels;
            for (var frame = 0; frame < frames; frame++)
            {
                var sum = 0f;
                for (var channel = 0; channel < channels; channel++)
                {
                    sum += _sourceBuffer[frame * channels + channel];
                }
                buffer[offset + frame] = sum / channels;
            }
            return frames;
        }
    }

    private static readonly Action<ILogger, string, int, int, Exception?> CaptionStarted =
        LoggerMessage.Define<string, int, int>(LogLevel.Information, new EventId(5300, nameof(CaptionStarted)),
            "System-audio captions started ({Mode}, {SampleRate} Hz, {Channels} channels).");
    private static readonly Action<ILogger, long, Exception?> CaptionStopped =
        LoggerMessage.Define<long>(LogLevel.Information, new EventId(5301, nameof(CaptionStopped)),
            "System-audio captions stopped after {ChunkCount} chunks.");
    private static readonly Action<ILogger, string, Exception?> CaptionFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(5302, nameof(CaptionFailed)),
            "System-audio captions failed ({FailureKind}).");
    private static readonly Action<ILogger, Exception?> CaptionBufferFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(5303, nameof(CaptionBufferFailed)),
            "A system-audio capture buffer could not be accepted.");
}
