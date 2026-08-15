using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace GoWinUI.App.Services;

public sealed record ScreenClipSnapshot(
    bool IsRecording,
    bool IsBusy,
    string Status,
    int ElapsedSeconds,
    int MaximumSeconds,
    string? SourceLabel,
    string? Error);

public sealed record ScreenClipResult(
    Guid SessionId,
    string Path,
    string FileName,
    string ContentType,
    int Width,
    int Height,
    int Frames,
    double DurationSeconds,
    string SourceLabel);

public sealed class ScreenClipCaptureService(ILogger<ScreenClipCaptureService> logger) : IDisposable
{
    private const int FramesPerSecond = 2;
    private const int MaximumSeconds = 30;
    private const int MaximumWidth = 1_280;
    private const int MaximumHeight = 720;
    private const int MinimumEncoderWidth = 320;
    private const int MinimumEncoderHeight = 240;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _captureCancellation;
    private Task? _captureTask;
    private UncompressedAviWriter? _writer;
    private DesktopCaptureTarget? _target;
    private Guid? _sessionId;
    private string? _temporaryPath;
    private int _elapsedSeconds;
    private string _status = "Inaktiv";
    private string? _error;
    private bool _stopping;
    private bool _disposed;

    public event EventHandler<ScreenClipSnapshot>? Changed;

    public ScreenClipSnapshot Current => new(
        _writer is not null && !_stopping,
        _stopping,
        _status,
        _elapsedSeconds,
        MaximumSeconds,
        _target?.DisplayName,
        _error);

    public async Task StartAsync(
        Guid sessionId,
        DesktopCaptureTarget target,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_writer is not null)
            {
                throw new InvalidOperationException("Eine Bildschirmaufnahme läuft bereits.");
            }
            var (width, height) = Scale(target.Width, target.Height);
            var directory = Path.Combine(Path.GetTempPath(), "GO", "ScreenClips");
            Directory.CreateDirectory(directory);
            CleanupStaleFiles(directory);
            var path = Path.Combine(directory, $"{Guid.NewGuid():N}.avi");
            var writer = new UncompressedAviWriter(path, width, height, FramesPerSecond);
            _sessionId = sessionId;
            _target = target;
            _temporaryPath = path;
            _writer = writer;
            _elapsedSeconds = 0;
            _status = "Bildschirmclip wird aufgenommen · ohne Ton";
            _error = null;
            _stopping = false;
            _captureCancellation = new CancellationTokenSource();
            _captureTask = Task.Run(
                () => CaptureLoopAsync(target, writer, _captureCancellation.Token),
                CancellationToken.None);
            ClipStarted(logger, target.Kind.ToString(), width, height, null);
            RaiseChanged();
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            Release(deleteFile: true);
            _status = "Bildschirmaufnahme fehlgeschlagen";
            _error = exception.Message;
            ClipFailed(logger, exception.GetType().Name, exception);
            RaiseChanged();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ScreenClipResult> StopAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var writer = _writer ?? throw new InvalidOperationException("Es läuft keine Bildschirmaufnahme.");
            var target = _target ?? throw new InvalidOperationException("Die Aufnahmequelle fehlt.");
            var sessionId = _sessionId ?? throw new InvalidOperationException("Die Zielsitzung fehlt.");
            var path = _temporaryPath ?? throw new InvalidOperationException("Die temporäre Clipdatei fehlt.");
            _stopping = true;
            _status = "Bildschirmclip wird abgeschlossen";
            RaiseChanged();
            _captureCancellation?.Cancel();
            if (_captureTask is not null)
            {
                try
                {
                    await _captureTask.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_captureCancellation?.IsCancellationRequested == true)
                {
                    // Normal recording stop.
                }
            }
            writer.Dispose();
            var frames = writer.FrameCount;
            var duration = frames / (double)FramesPerSecond;
            if (frames == 0 || !File.Exists(path) || new FileInfo(path).Length <= 256)
            {
                Release(deleteFile: true, writerAlreadyDisposed: true);
                throw new InvalidOperationException("Der Bildschirmclip enthält kein Bild.");
            }
            _status = "Bildschirmclip wird für die Vorschau vorbereitet";
            RaiseChanged();
            var previewPath = await TranscodeToMp4Async(
                path,
                writer.Width,
                writer.Height,
                cancellationToken).ConfigureAwait(false);
            TryDelete(path);
            var result = new ScreenClipResult(
                sessionId,
                previewPath,
                $"GO-Bildschirmclip-{DateTime.Now:yyyy-MM-dd-HHmmss}.mp4",
                "video/mp4",
                writer.Width,
                writer.Height,
                frames,
                duration,
                target.DisplayName);
            Release(deleteFile: false, writerAlreadyDisposed: true);
            _status = "Bildschirmclip aufgenommen";
            _elapsedSeconds = (int)Math.Ceiling(duration);
            _stopping = false;
            ClipStopped(logger, frames, duration, null);
            RaiseChanged();
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            Release(deleteFile: true);
            _status = "Bildschirmaufnahme fehlgeschlagen";
            _error = exception.Message;
            _stopping = false;
            ClipFailed(logger, exception.GetType().Name, exception);
            RaiseChanged();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_writer is null)
            {
                return;
            }
            _stopping = true;
            _captureCancellation?.Cancel();
            if (_captureTask is not null)
            {
                try { await _captureTask.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_captureCancellation?.IsCancellationRequested == true) { }
                catch (TimeoutException) { }
            }
            Release(deleteFile: true);
            _status = "Bildschirmaufnahme verworfen";
            _error = null;
            _elapsedSeconds = 0;
            _stopping = false;
            RaiseChanged();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CaptureLoopAsync(
        DesktopCaptureTarget target,
        UncompressedAviWriter writer,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var interval = TimeSpan.FromSeconds(1d / FramesPerSecond);
        try
        {
            for (var frame = 0; frame < FramesPerSecond * MaximumSeconds; frame++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var scheduled = TimeSpan.FromTicks(interval.Ticks * frame);
                var wait = scheduled - stopwatch.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                }
                var pixels = DesktopScreenshotService.CaptureVideoFrame(target, writer.Width, writer.Height);
                writer.WriteFrame(pixels);
                var elapsed = Math.Min(MaximumSeconds, (int)Math.Floor(stopwatch.Elapsed.TotalSeconds));
                if (elapsed != _elapsedSeconds)
                {
                    _elapsedSeconds = elapsed;
                    RaiseChanged();
                }
            }
            _elapsedSeconds = MaximumSeconds;
            _status = "30-Sekunden-Limit erreicht · Clip übernehmen";
            RaiseChanged();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal explicit stop.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _error = exception.Message;
            _status = "Aufnahme unterbrochen · vorhandenen Clip übernehmen";
            ClipFailed(logger, exception.GetType().Name, exception);
            RaiseChanged();
        }
    }

    private void Release(bool deleteFile, bool writerAlreadyDisposed = false)
    {
        _captureCancellation?.Cancel();
        _captureCancellation?.Dispose();
        _captureCancellation = null;
        _captureTask = null;
        if (!writerAlreadyDisposed)
        {
            _writer?.Dispose();
        }
        _writer = null;
        _target = null;
        _sessionId = null;
        if (deleteFile && _temporaryPath is { } path)
        {
            TryDelete(path);
        }
        _temporaryPath = null;
    }

    private static (int Width, int Height) Scale(int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            throw new InvalidOperationException("Die Aufnahmequelle besitzt keine gültige Größe.");
        }
        var scale = Math.Min(1d, Math.Min(MaximumWidth / (double)sourceWidth, MaximumHeight / (double)sourceHeight));
        var width = Math.Clamp((int)Math.Floor(sourceWidth * scale), MinimumEncoderWidth, MaximumWidth);
        var height = Math.Clamp((int)Math.Floor(sourceHeight * scale), MinimumEncoderHeight, MaximumHeight);
        width -= width % 2;
        height -= height % 2;
        return (width, height);
    }

    internal static async Task<string> TranscodeToMp4Async(
        string sourcePath,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        var destinationPath = Path.ChangeExtension(sourcePath, ".mp4");
        TryDelete(destinationPath);
        try
        {
            var source = await StorageFile.GetFileFromPathAsync(sourcePath).AsTask(cancellationToken).ConfigureAwait(false);
            var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(destinationPath)!)
                .AsTask(cancellationToken).ConfigureAwait(false);
            var destination = await folder.CreateFileAsync(
                    Path.GetFileName(destinationPath),
                    CreationCollisionOption.ReplaceExisting)
                .AsTask(cancellationToken).ConfigureAwait(false);
            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD720p);
            profile.Video.Width = checked((uint)width);
            profile.Video.Height = checked((uint)height);
            profile.Video.FrameRate.Numerator = FramesPerSecond;
            profile.Video.FrameRate.Denominator = 1;
            profile.Video.Bitrate = 2_500_000;
            var transcoder = new MediaTranscoder
            {
                HardwareAccelerationEnabled = true,
            };
            var prepared = await transcoder.PrepareFileTranscodeAsync(source, destination, profile)
                .AsTask(cancellationToken).ConfigureAwait(false);
            if (!prepared.CanTranscode)
            {
                throw new InvalidOperationException($"Windows konnte den Bildschirmclip nicht als MP4 vorbereiten ({prepared.FailureReason}).");
            }
            await prepared.TranscodeAsync().AsTask(cancellationToken).ConfigureAwait(false);
            if (!File.Exists(destinationPath) || new FileInfo(destinationPath).Length <= 256)
            {
                throw new InvalidOperationException("Die erzeugte MP4-Vorschau ist leer.");
            }
            return destinationPath;
        }
        catch
        {
            TryDelete(destinationPath);
            throw;
        }
    }

    private static void CleanupStaleFiles(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.avi"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-1))
                {
                    File.Delete(file);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void RaiseChanged() => Changed?.Invoke(this, Current);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        try { CancelAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ClipFailed(logger, exception.GetType().Name, exception);
        }
        _disposed = true;
        _gate.Dispose();
    }

    internal sealed class UncompressedAviWriter : IDisposable
    {
        private const uint AviHasIndex = 0x10;
        private const uint AviKeyFrame = 0x10;
        private readonly FileStream _stream;
        private readonly BinaryWriter _writer;
        private readonly List<IndexEntry> _index = [];
        private readonly int _rowStride;
        private readonly int _frameBytes;
        private readonly long _riffSizePosition;
        private readonly long _headerListSizePosition;
        private readonly long _totalFramesPosition;
        private readonly long _streamLengthPosition;
        private readonly long _moviListSizePosition;
        private readonly long _moviFourCcPosition;
        private bool _disposed;

        public UncompressedAviWriter(string path, int width, int height, int framesPerSecond)
        {
            Width = width;
            Height = height;
            FramesPerSecond = framesPerSecond;
            _rowStride = checked((width * 3 + 3) & ~3);
            _frameBytes = checked(_rowStride * height);
            _stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 1_048_576, FileOptions.SequentialScan);
            _writer = new BinaryWriter(_stream, Encoding.ASCII, leaveOpen: true);

            WriteFourCc("RIFF");
            _riffSizePosition = Position;
            WriteUInt32(0);
            WriteFourCc("AVI ");

            WriteFourCc("LIST");
            _headerListSizePosition = Position;
            WriteUInt32(0);
            var headerContentStart = Position;
            WriteFourCc("hdrl");

            WriteFourCc("avih");
            WriteUInt32(56);
            WriteUInt32((uint)(1_000_000 / framesPerSecond));
            WriteUInt32((uint)(_frameBytes * framesPerSecond));
            WriteUInt32(0);
            WriteUInt32(AviHasIndex);
            _totalFramesPosition = Position;
            WriteUInt32(0);
            WriteUInt32(0);
            WriteUInt32(1);
            WriteUInt32((uint)_frameBytes);
            WriteUInt32((uint)width);
            WriteUInt32((uint)height);
            for (var index = 0; index < 4; index++) WriteUInt32(0);

            WriteFourCc("LIST");
            var streamListSizePosition = Position;
            WriteUInt32(0);
            var streamListContentStart = Position;
            WriteFourCc("strl");

            WriteFourCc("strh");
            WriteUInt32(56);
            WriteFourCc("vids");
            WriteFourCc("DIB ");
            WriteUInt32(0);
            WriteUInt16(0);
            WriteUInt16(0);
            WriteUInt32(0);
            WriteUInt32(1);
            WriteUInt32((uint)framesPerSecond);
            WriteUInt32(0);
            _streamLengthPosition = Position;
            WriteUInt32(0);
            WriteUInt32((uint)_frameBytes);
            WriteUInt32(uint.MaxValue);
            WriteUInt32(0);
            WriteInt16(0);
            WriteInt16(0);
            WriteInt16((short)Math.Min(short.MaxValue, width));
            WriteInt16((short)Math.Min(short.MaxValue, height));

            WriteFourCc("strf");
            WriteUInt32(40);
            WriteUInt32(40);
            WriteInt32(width);
            WriteInt32(height);
            WriteUInt16(1);
            WriteUInt16(24);
            WriteUInt32(0);
            WriteUInt32((uint)_frameBytes);
            WriteInt32(0);
            WriteInt32(0);
            WriteUInt32(0);
            WriteUInt32(0);

            PatchUInt32(streamListSizePosition, checked((uint)(Position - streamListContentStart)));
            PatchUInt32(_headerListSizePosition, checked((uint)(Position - headerContentStart)));

            WriteFourCc("LIST");
            _moviListSizePosition = Position;
            WriteUInt32(0);
            _moviFourCcPosition = Position;
            WriteFourCc("movi");
        }

        public int Width { get; }
        public int Height { get; }
        public int FramesPerSecond { get; }
        public int FrameCount { get; private set; }
        private long Position => _stream.Position;

        public void WriteFrame(ReadOnlySpan<byte> topDownBgra)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (topDownBgra.Length != checked(Width * Height * 4))
            {
                throw new ArgumentException("Das Bildschirmframe besitzt eine unerwartete Größe.", nameof(topDownBgra));
            }
            var chunkPosition = Position;
            WriteFourCc("00db");
            WriteUInt32((uint)_frameBytes);
            var row = new byte[_rowStride];
            for (var sourceY = Height - 1; sourceY >= 0; sourceY--)
            {
                row.AsSpan().Clear();
                var source = topDownBgra.Slice(sourceY * Width * 4, Width * 4);
                for (var x = 0; x < Width; x++)
                {
                    row[x * 3] = source[x * 4];
                    row[x * 3 + 1] = source[x * 4 + 1];
                    row[x * 3 + 2] = source[x * 4 + 2];
                }
                _writer.Write(row);
            }
            if ((_frameBytes & 1) != 0) _writer.Write((byte)0);
            _index.Add(new IndexEntry(
                checked((uint)(chunkPosition - _moviFourCcPosition)),
                checked((uint)_frameBytes)));
            FrameCount++;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            try
            {
                var indexStart = Position;
                WriteFourCc("idx1");
                WriteUInt32(checked((uint)(_index.Count * 16)));
                foreach (var entry in _index)
                {
                    WriteFourCc("00db");
                    WriteUInt32(AviKeyFrame);
                    WriteUInt32(entry.Offset);
                    WriteUInt32(entry.Size);
                }
                PatchUInt32(_moviListSizePosition, checked((uint)(indexStart - _moviFourCcPosition)));
                PatchUInt32(_totalFramesPosition, checked((uint)FrameCount));
                PatchUInt32(_streamLengthPosition, checked((uint)FrameCount));
                PatchUInt32(_riffSizePosition, checked((uint)(Position - 8)));
                _writer.Flush();
                _stream.Flush(flushToDisk: true);
            }
            finally
            {
                _writer.Dispose();
                _stream.Dispose();
            }
        }

        private void PatchUInt32(long position, uint value)
        {
            var current = Position;
            _stream.Position = position;
            WriteUInt32(value);
            _stream.Position = current;
        }

        private void WriteFourCc(string value)
        {
            if (value.Length != 4) throw new ArgumentException("FourCC muss vier Zeichen enthalten.", nameof(value));
            _writer.Write(Encoding.ASCII.GetBytes(value));
        }
        private void WriteUInt16(ushort value) => _writer.Write(value);
        private void WriteInt16(short value) => _writer.Write(value);
        private void WriteUInt32(uint value) => _writer.Write(value);
        private void WriteInt32(int value) => _writer.Write(value);
        private sealed record IndexEntry(uint Offset, uint Size);
    }

    private static readonly Action<ILogger, string, int, int, Exception?> ClipStarted =
        LoggerMessage.Define<string, int, int>(LogLevel.Information, new EventId(5330, nameof(ClipStarted)),
            "Screen clip recording started ({Source}, {Width}x{Height}).");
    private static readonly Action<ILogger, int, double, Exception?> ClipStopped =
        LoggerMessage.Define<int, double>(LogLevel.Information, new EventId(5331, nameof(ClipStopped)),
            "Screen clip recording stopped ({Frames} frames, {DurationSeconds:F1} seconds).");
    private static readonly Action<ILogger, string, Exception?> ClipFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(5332, nameof(ClipFailed)),
            "Screen clip recording failed ({FailureKind}).");
}
