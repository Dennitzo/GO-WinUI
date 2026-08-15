using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace GoWinUI.App.Services;

public enum DesktopCaptureTargetKind
{
    VirtualDesktop,
    Monitor,
    Window,
}

public sealed record DesktopCaptureTarget(
    string Id,
    DesktopCaptureTargetKind Kind,
    string DisplayName,
    int X,
    int Y,
    int Width,
    int Height,
    nint Handle = 0);

public sealed record DesktopScreenshot(
    string FileName,
    string ContentType,
    byte[] Content,
    int Width,
    int Height,
    string SourceLabel);

public sealed class DesktopScreenshotService(ILogger<DesktopScreenshotService> logger)
{
    private const int MaximumDimension = 32_768;
    private const long MaximumPixels = 150_000_000;
    private const int SrcCopy = 0x00CC0020;
    private const int CaptureBlt = 0x40000000;
    private const uint DibRgbColors = 0;
    private const int BiRgb = 0;
    private const int DwmExtendedFrameBounds = 9;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;

    public IReadOnlyList<DesktopCaptureTarget> ListTargets()
    {
        var targets = new List<DesktopCaptureTarget>();
        var virtualX = GetSystemMetrics(76);
        var virtualY = GetSystemMetrics(77);
        var virtualWidth = GetSystemMetrics(78);
        var virtualHeight = GetSystemMetrics(79);
        if (virtualWidth > 0 && virtualHeight > 0)
        {
            targets.Add(new(
                "desktop",
                DesktopCaptureTargetKind.VirtualDesktop,
                $"Gesamter Desktop · {virtualWidth} × {virtualHeight}",
                virtualX,
                virtualY,
                virtualWidth,
                virtualHeight));
        }

        var monitorIndex = 0;
        _ = EnumDisplayMonitors(0, 0, (handle, _, _, _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(handle, ref info))
            {
                monitorIndex++;
                var width = info.Monitor.Right - info.Monitor.Left;
                var height = info.Monitor.Bottom - info.Monitor.Top;
                var primary = (info.Flags & 1) != 0 ? " · Hauptmonitor" : string.Empty;
                targets.Add(new(
                    $"monitor-{handle:X}",
                    DesktopCaptureTargetKind.Monitor,
                    $"Monitor {monitorIndex}{primary} · {width} × {height}",
                    info.Monitor.Left,
                    info.Monitor.Top,
                    width,
                    height,
                    handle));
            }
            return true;
        }, 0);

        var windows = new List<DesktopCaptureTarget>();
        _ = EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle) || IsIconic(handle) || GetWindowTextLength(handle) is <= 0 or > 1_000)
            {
                return true;
            }
            if ((GetWindowLongPtr(handle, GwlExStyle).ToInt64() & WsExToolWindow) != 0)
            {
                return true;
            }
            var title = new char[GetWindowTextLength(handle) + 1];
            var copied = GetWindowText(handle, title, title.Length);
            var label = copied > 0 ? new string(title, 0, copied).Trim() : string.Empty;
            if (label.Length == 0 || !TryGetWindowBounds(handle, out var bounds))
            {
                return true;
            }
            var width = bounds.Right - bounds.Left;
            var height = bounds.Bottom - bounds.Top;
            if (width >= 160 && height >= 100)
            {
                windows.Add(new(
                    $"window-{handle:X}",
                    DesktopCaptureTargetKind.Window,
                    $"Fenster · {label}",
                    bounds.Left,
                    bounds.Top,
                    width,
                    height,
                    handle));
            }
            return true;
        }, 0);
        targets.AddRange(windows.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase).Take(100));
        CaptureTargetsEnumerated(logger, targets.Count, null);
        return targets;
    }

    public async Task<DesktopScreenshot> CaptureAsync(
        DesktopCaptureTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ValidateBounds(target);
        cancellationToken.ThrowIfCancellationRequested();
        var pixels = CaptureBgra(target);
        cancellationToken.ThrowIfCancellationRequested();
        var png = await EncodePngAsync(pixels, target.Width, target.Height, cancellationToken).ConfigureAwait(false);
        var source = target.Kind switch
        {
            DesktopCaptureTargetKind.Window => "Fenster",
            DesktopCaptureTargetKind.Monitor => "Monitor",
            _ => "Desktop",
        };
        ScreenshotCaptured(logger, source, target.Width, target.Height, null);
        return new DesktopScreenshot(
            $"GO-Screenshot-{DateTime.Now:yyyy-MM-dd-HHmmss}.png",
            "image/png",
            png,
            target.Width,
            target.Height,
            target.DisplayName);
    }

    internal static byte[] CaptureVideoFrame(
        DesktopCaptureTarget target,
        int outputWidth,
        int outputHeight)
    {
        ArgumentNullException.ThrowIfNull(target);
        ValidateBounds(target);
        if (outputWidth is <= 0 or > MaximumDimension || outputHeight is <= 0 or > MaximumDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(outputWidth), "Die Videoauflösung ist ungültig.");
        }
        return CaptureBgra(target, outputWidth, outputHeight, preferWindowCapture: true);
    }

    private static byte[] CaptureBgra(DesktopCaptureTarget target) =>
        CaptureBgra(target, target.Width, target.Height, preferWindowCapture: true);

    private static byte[] CaptureBgra(
        DesktopCaptureTarget target,
        int outputWidth,
        int outputHeight,
        bool preferWindowCapture)
    {
        var sourceDc = GetDC(0);
        if (sourceDc == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Der Desktop-Kontext konnte nicht geöffnet werden.");
        }
        var memoryDc = CreateCompatibleDC(sourceDc);
        var bitmap = CreateCompatibleBitmap(sourceDc, outputWidth, outputHeight);
        if (memoryDc == 0 || bitmap == 0)
        {
            if (bitmap != 0) _ = DeleteObject(bitmap);
            if (memoryDc != 0) _ = DeleteDC(memoryDc);
            _ = ReleaseDC(0, sourceDc);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Der Screenshot-Puffer konnte nicht erzeugt werden.");
        }

        var previous = SelectObject(memoryDc, bitmap);
        try
        {
            bool captured;
            if (preferWindowCapture && target.Kind == DesktopCaptureTargetKind.Window)
            {
                if (target.Handle == 0 || !IsWindow(target.Handle))
                {
                    throw new InvalidOperationException("Das ausgewählte Fenster wurde geschlossen.");
                }
                var windowDc = CreateCompatibleDC(sourceDc);
                var windowBitmap = CreateCompatibleBitmap(sourceDc, target.Width, target.Height);
                if (windowDc == 0 || windowBitmap == 0)
                {
                    if (windowBitmap != 0) _ = DeleteObject(windowBitmap);
                    if (windowDc != 0) _ = DeleteDC(windowDc);
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Der Fensteraufnahmepuffer konnte nicht erzeugt werden.");
                }
                var oldWindowBitmap = SelectObject(windowDc, windowBitmap);
                try
                {
                    if (!PrintWindow(target.Handle, windowDc, 2))
                    {
                        throw new InvalidOperationException("Das ausgewählte Fenster unterstützt keine direkte Aufnahme.");
                    }
                    _ = SetStretchBltMode(memoryDc, 4);
                    captured = StretchBlt(memoryDc, 0, 0, outputWidth, outputHeight, windowDc, 0, 0, target.Width, target.Height, SrcCopy);
                }
                finally
                {
                    _ = SelectObject(windowDc, oldWindowBitmap);
                    _ = DeleteObject(windowBitmap);
                    _ = DeleteDC(windowDc);
                }
            }
            else
            {
                _ = SetStretchBltMode(memoryDc, 4);
                captured = StretchBlt(
                    memoryDc,
                    0,
                    0,
                    outputWidth,
                    outputHeight,
                    sourceDc,
                    target.X,
                    target.Y,
                    target.Width,
                    target.Height,
                    SrcCopy | CaptureBlt);
            }
            if (!captured)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Der ausgewählte Bildschirminhalt konnte nicht aufgenommen werden.");
            }

            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = outputWidth,
                    Height = -outputHeight,
                    Planes = 1,
                    BitCount = 32,
                    Compression = BiRgb,
                },
            };
            var result = new byte[checked(outputWidth * outputHeight * 4)];
            var lines = GetDIBits(memoryDc, bitmap, 0, (uint)outputHeight, result, ref info, DibRgbColors);
            if (lines != outputHeight)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Der Screenshot konnte nicht in Pixeldaten umgewandelt werden.");
            }
            return result;
        }
        finally
        {
            _ = SelectObject(memoryDc, previous);
            _ = DeleteObject(bitmap);
            _ = DeleteDC(memoryDc);
            _ = ReleaseDC(0, sourceDc);
        }
    }

    private static async Task<byte[]> EncodePngAsync(
        byte[] pixels,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        using var randomAccess = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, randomAccess);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            (uint)width,
            (uint)height,
            96,
            96,
            pixels);
        await encoder.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);
        randomAccess.Seek(0);
        using var input = randomAccess.AsStreamForRead();
        using var output = new MemoryStream();
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        return output.ToArray();
    }

    private static void ValidateBounds(DesktopCaptureTarget target)
    {
        if (target.Width is <= 0 or > MaximumDimension
            || target.Height is <= 0 or > MaximumDimension
            || (long)target.Width * target.Height > MaximumPixels)
        {
            throw new InvalidOperationException("Der ausgewählte Bildschirmbereich ist zu groß oder ungültig.");
        }
    }

    private static bool TryGetWindowBounds(nint handle, out NativeRect bounds)
    {
        if (DwmGetWindowAttribute(handle, DwmExtendedFrameBounds, out bounds, Marshal.SizeOf<NativeRect>()) == 0)
        {
            return bounds.Right > bounds.Left && bounds.Bottom > bounds.Top;
        }
        return GetWindowRect(handle, out bounds)
            && bounds.Right > bounds.Left && bounds.Bottom > bounds.Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public int Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    private delegate bool MonitorCallback(nint monitor, nint hdc, nint rect, nint data);
    private delegate bool WindowCallback(nint window, nint data);

    [DllImport("user32.dll", SetLastError = true)] private static extern nint GetDC(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern int ReleaseDC(nint window, nint hdc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleBitmap(nint hdc, int width, int height);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint SelectObject(nint hdc, nint value);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, int operation);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool StretchBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, int operation);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(nint hdc, int mode);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int GetDIBits(nint hdc, nint bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfo info, uint usage);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(WindowCallback callback, nint data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, [Out] char[] text, int maximum);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out NativeRect bounds);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PrintWindow(nint window, nint hdc, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out NativeRect value, int size);

    private static readonly Action<ILogger, string, int, int, Exception?> ScreenshotCaptured =
        LoggerMessage.Define<string, int, int>(LogLevel.Information, new EventId(5320, nameof(ScreenshotCaptured)),
            "Desktop screenshot captured ({Source}, {Width}x{Height}).");
    private static readonly Action<ILogger, int, Exception?> CaptureTargetsEnumerated =
        LoggerMessage.Define<int>(LogLevel.Debug, new EventId(5321, nameof(CaptureTargetsEnumerated)),
            "Enumerated {TargetCount} desktop screenshot targets.");
}
