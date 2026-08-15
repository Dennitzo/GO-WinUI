using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Buffers.Binary;

namespace GoWinUI.App.Services;

public sealed record SystemAudioCaptureSnapshot(
    bool IsRecording,
    bool IsBusy,
    string Status,
    int ElapsedSeconds,
    int MaximumSeconds,
    string? SourceLabel,
    string? Error);

public sealed record SystemAudioCaptureResult(
    Guid SessionId,
    byte[] Content,
    string FileName,
    string ContentType,
    double DurationSeconds,
    string SourceLabel);

public sealed class SystemAudioAnalysisCaptureService(
    ILogger<SystemAudioAnalysisCaptureService> logger) : IDisposable
{
    internal const int SampleRate = 16_000;
    internal const int MaximumSeconds = 600;
    private const int BytesPerSample = sizeof(short);
    private const int MinimumPcmBytes = SampleRate * BytesPerSample / 5;
    private const int MaximumPcmBytes = SampleRate * BytesPerSample * MaximumSeconds;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _audioGate = new();
    private readonly float[] _sampleBuffer = new float[SampleRate];
    private WasapiLoopbackCapture? _capture;
    private BufferedWaveProvider? _audioBuffer;
    private ISampleProvider? _resampledAudio;
    private MemoryStream? _pcm;
    private TaskCompletionSource<bool>? _recordingStopped;
    private CancellationTokenSource? _progressCancellation;
    private Task? _progressTask;
    private Guid? _sessionId;
    private DateTimeOffset? _startedAt;
    private int _elapsedSeconds;
    private string _status = "Inaktiv";
    private string? _error;
    private bool _busy;
    private bool _stopRequested;
    private bool _recordingEnded;
    private bool _disposed;

    public event EventHandler<SystemAudioCaptureSnapshot>? Changed;

    public SystemAudioCaptureSnapshot Current => new(
        _capture is not null && !_busy && !_recordingEnded,
        _busy,
        _status,
        _elapsedSeconds,
        MaximumSeconds,
        "Windows-Systemaudio",
        _error);

    public async Task StartAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_capture is not null)
            {
                throw new InvalidOperationException("Eine Systemaudio-Aufnahme läuft bereits.");
            }

            _sessionId = sessionId;
            _startedAt = DateTimeOffset.UtcNow;
            _elapsedSeconds = 0;
            _status = "Windows-Systemaudio wird verbunden";
            _error = null;
            _busy = true;
            _stopRequested = false;
            _recordingEnded = false;
            _pcm = new MemoryStream(capacity: SampleRate * BytesPerSample * 30);
            RaiseChanged();

            try
            {
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
                if (sampleProvider.WaveFormat.SampleRate != SampleRate)
                {
                    sampleProvider = new WdlResamplingSampleProvider(sampleProvider, SampleRate);
                }

                _capture = capture;
                _audioBuffer = buffer;
                _resampledAudio = sampleProvider;
                _recordingStopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                capture.DataAvailable += OnDataAvailable;
                capture.RecordingStopped += OnRecordingStopped;
                capture.StartRecording();

                _progressCancellation = new CancellationTokenSource();
                _progressTask = Task.Run(
                    () => TrackProgressAsync(_progressCancellation.Token),
                    CancellationToken.None);
                _busy = false;
                _status = "Windows-Systemaudio wird aufgenommen";
                RaiseChanged();
                CaptureStarted(
                    logger,
                    capture.WaveFormat.SampleRate,
                    capture.WaveFormat.Channels,
                    null);
            }
            catch
            {
                await ReleaseCaptureAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            _busy = false;
            _status = "Systemaudio-Aufnahme fehlgeschlagen";
            _error = FriendlyError(exception);
            RaiseChanged();
            CaptureFailed(logger, exception.GetType().Name, exception);
            throw new InvalidOperationException(_error, exception);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<SystemAudioCaptureResult> StopAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var capture = _capture ?? throw new InvalidOperationException("Es läuft keine Systemaudio-Aufnahme.");
            var sessionId = _sessionId ?? throw new InvalidOperationException("Die Zielsitzung der Systemaudio-Aufnahme fehlt.");
            _busy = true;
            _stopRequested = true;
            _status = "Systemaudio-Aufnahme wird vorbereitet";
            RaiseChanged();
            _progressCancellation?.Cancel();

            try
            {
                capture.StopRecording();
            }
            catch (InvalidOperationException)
            {
                // Das Wiedergabegerät kann die Aufnahme bereits beendet haben.
            }
            if (_recordingStopped is not null)
            {
                try
                {
                    await _recordingStopped.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // Bereits empfangene Systemaudiodaten bleiben verwendbar.
                }
            }

            byte[] data;
            lock (_audioGate)
            {
                DrainResamplerLocked();
                data = _pcm?.ToArray() ?? [];
            }
            var duration = data.Length / (double)(SampleRate * BytesPerSample);
            await ReleaseCaptureAsync().ConfigureAwait(false);

            if (data.Length < MinimumPcmBytes)
            {
                _busy = false;
                _status = "Keine Systemaudio-Wiedergabe erkannt";
                _error = "Während der Aufnahme wurde kein verwertbares Windows-Systemaudio erkannt.";
                RaiseChanged();
                throw new InvalidOperationException(_error);
            }

            _busy = false;
            _elapsedSeconds = Math.Min(MaximumSeconds, (int)Math.Ceiling(duration));
            _status = "Systemaudio-Aufnahme abgeschlossen";
            _error = null;
            RaiseChanged();
            CaptureStopped(logger, duration, null);
            return new SystemAudioCaptureResult(
                sessionId,
                CreateWave(data),
                $"GO-Systemaudio-{DateTime.Now:yyyy-MM-dd-HHmmss}.wav",
                "audio/wav",
                duration,
                "Windows-Systemaudio");
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            if (_capture is not null)
            {
                await ReleaseCaptureAsync().ConfigureAwait(false);
            }
            _busy = false;
            _error ??= FriendlyError(exception);
            _status = "Systemaudio-Aufnahme fehlgeschlagen";
            RaiseChanged();
            CaptureFailed(logger, exception.GetType().Name, exception);
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_capture is null)
            {
                return;
            }
            _stopRequested = true;
            _progressCancellation?.Cancel();
            try
            {
                _capture.StopRecording();
            }
            catch (InvalidOperationException)
            {
                // Bereits beendet.
            }
            await ReleaseCaptureAsync().ConfigureAwait(false);
            _busy = false;
            _status = "Systemaudio-Aufnahme verworfen";
            _error = null;
            _elapsedSeconds = 0;
            RaiseChanged();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        if (_busy || _recordingEnded || args.BytesRecorded <= 0)
        {
            return;
        }
        try
        {
            lock (_audioGate)
            {
                _audioBuffer?.AddSamples(args.Buffer, 0, args.BytesRecorded);
                DrainResamplerLocked();
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _error = FriendlyError(exception);
            _status = "Systemaudio-Aufnahme unterbrochen";
            CaptureFailed(logger, exception.GetType().Name, exception);
            RaiseChanged();
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        _recordingEnded = true;
        if (args.Exception is not null && !_stopRequested)
        {
            _error = FriendlyError(args.Exception);
            _status = "Systemaudio-Aufnahme unterbrochen";
            CaptureFailed(logger, args.Exception.GetType().Name, args.Exception);
            RaiseChanged();
        }
        _recordingStopped?.TrySetResult(true);
    }

    private void DrainResamplerLocked()
    {
        if (_resampledAudio is null || _pcm is null)
        {
            return;
        }
        int read;
        while (_pcm.Length < MaximumPcmBytes
            && (read = _resampledAudio.Read(_sampleBuffer, 0, _sampleBuffer.Length)) > 0)
        {
            var remainingSamples = (MaximumPcmBytes - checked((int)_pcm.Length)) / BytesPerSample;
            var count = Math.Min(read, remainingSamples);
            var bytes = new byte[count * BytesPerSample];
            for (var index = 0; index < count; index++)
            {
                var value = Math.Clamp(_sampleBuffer[index], -1f, 1f);
                var pcm = value <= -1f ? short.MinValue : (short)Math.Round(value * short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(index * BytesPerSample, BytesPerSample), pcm);
            }
            _pcm.Write(bytes, 0, bytes.Length);
        }
    }

    private async Task TrackProgressAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var startedAt = _startedAt;
                if (startedAt is null)
                {
                    return;
                }
                _elapsedSeconds = Math.Min(
                    MaximumSeconds,
                    Math.Max(0, (int)(DateTimeOffset.UtcNow - startedAt.Value).TotalSeconds));
                _status = _elapsedSeconds >= MaximumSeconds
                    ? "10-Minuten-Limit erreicht · Aufnahme wird abgeschlossen"
                    : "Windows-Systemaudio wird aufgenommen";
                RaiseChanged();
                if (_elapsedSeconds >= MaximumSeconds)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Reguläres Ende der Fortschrittsanzeige.
        }
    }

    private async Task ReleaseCaptureAsync()
    {
        var progressTask = _progressTask;
        _progressCancellation?.Cancel();
        if (progressTask is not null)
        {
            try
            {
                await progressTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Reguläres Ende.
            }
        }

        var capture = _capture;
        if (capture is not null)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            capture.Dispose();
        }
        lock (_audioGate)
        {
            _pcm?.Dispose();
            _pcm = null;
            _audioBuffer = null;
            _resampledAudio = null;
        }
        _capture = null;
        _sessionId = null;
        _startedAt = null;
        _recordingStopped = null;
        _progressTask = null;
        _progressCancellation?.Dispose();
        _progressCancellation = null;
        _stopRequested = false;
        _recordingEnded = false;
    }

    internal static byte[] CreateWave(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length < MinimumPcmBytes || pcm.Length > MaximumPcmBytes || (pcm.Length & 1) != 0)
        {
            throw new ArgumentException("Die PCM16-Systemaudiodaten liegen außerhalb der erlaubten Dauer.", nameof(pcm));
        }
        var wave = new byte[44 + pcm.Length];
        "RIFF"u8.CopyTo(wave);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(4, 4), 36 + pcm.Length);
        "WAVEfmt "u8.CopyTo(wave.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(24, 4), SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(28, 4), SampleRate * BytesPerSample);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(32, 2), BytesPerSample);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(34, 2), 16);
        "data"u8.CopyTo(wave.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(40, 4), pcm.Length);
        pcm.CopyTo(wave.AsSpan(44));
        return wave;
    }

    private static string FriendlyError(Exception exception) => exception switch
    {
        NAudio.MmException => "Windows-Systemaudio konnte nicht aufgenommen werden. Prüfe das Standard-Wiedergabegerät.",
        _ => exception.Message,
    };

    private void RaiseChanged() => Changed?.Invoke(this, Current);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        try
        {
            CancelAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            CaptureFailed(logger, exception.GetType().Name, exception);
        }
        _disposed = true;
        _lifecycleGate.Dispose();
    }

    private sealed class DownmixSampleProvider(ISampleProvider source) : ISampleProvider
    {
        private float[] _sourceBuffer = [];

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            var channels = source.WaveFormat.Channels;
            var required = checked(count * channels);
            if (_sourceBuffer.Length < required)
            {
                _sourceBuffer = new float[required];
            }
            var sourceRead = source.Read(_sourceBuffer, 0, required);
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

    private static readonly Action<ILogger, int, int, Exception?> CaptureStarted =
        LoggerMessage.Define<int, int>(LogLevel.Information, new EventId(5350, nameof(CaptureStarted)),
            "System-audio analysis capture started ({SampleRate} Hz, {Channels} channels).");
    private static readonly Action<ILogger, double, Exception?> CaptureStopped =
        LoggerMessage.Define<double>(LogLevel.Information, new EventId(5351, nameof(CaptureStopped)),
            "System-audio analysis capture stopped after {DurationSeconds:F1} seconds.");
    private static readonly Action<ILogger, string, Exception?> CaptureFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(5352, nameof(CaptureFailed)),
            "System-audio analysis capture failed ({FailureKind}).");
}
