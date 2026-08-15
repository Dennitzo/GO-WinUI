using GoWinUI.App.Services;
using GoWinUI.App.ViewModels;
using GoAi.Contracts;
using GoWinUI.BricsCad.Protocol;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using GoWinUI.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.AppLifecycle;
using System.Globalization;
using Windows.UI.ViewManagement;

namespace GoWinUI.App;

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "WinUI owns the Application lifetime; PrepareShutdownAsync cancels and disposes the monitor token.")]
public partial class App : Application
{
    private static readonly string[] AccentBrushKeys =
    [
        "GoAccentBrush",
        "GoAccentSubtleBrush",
        "AccentFillColorDefaultBrush",
        "AccentFillColorSecondaryBrush",
        "AccentFillColorTertiaryBrush",
        "AccentFillColorDisabledBrush",
        "AccentTextFillColorPrimaryBrush",
        "ToggleSwitchFillOn",
        "ToggleSwitchFillOnPointerOver",
        "ToggleSwitchFillOnPressed",
        "ToggleSwitchFillOnDisabled",
        "ToggleSwitchStrokeOn",
        "ToggleSwitchStrokeOnPointerOver",
        "ToggleSwitchStrokeOnPressed",
        "ToggleSwitchStrokeOnDisabled",
        "NavigationViewSelectionIndicatorForeground",
    ];
    private readonly IHost _host;
    private AppInstance? _appInstance;
    private MainWindow? _window;
    private FrameworkElement? _themeRoot;
    private AppTheme _appliedTheme = AppTheme.System;
    private CancellationTokenSource? _aiAvailabilityCancellation;
    private Task? _aiAvailabilityMonitor;
    private int _shutdownStarted;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        DataDirectory = ResolveDataDirectory();
        _host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddDebug();
            })
            .ConfigureServices(services =>
            {
                services.AddGoInfrastructure(options => options.DataDirectory = DataDirectory);
                services.AddSingleton<BricsCadBridgeHost>();
                services.AddSingleton<IBricsCadBridgeHost>(static provider => provider.GetRequiredService<BricsCadBridgeHost>());
                services.AddSingleton<BricsCadBridgeLifecycle>();
                services.AddHostedService(static provider => provider.GetRequiredService<BricsCadBridgeLifecycle>());
                services.AddSingleton<SettingsCoordinator>();
                services.AddSingleton<IAiSecretStore, WindowsCredentialSecretStore>();
                services.AddSingleton<GoAiConnectionService>();
                services.AddSingleton<SystemAudioCaptionService>();
                services.AddSingleton<MicrophoneTranscriptionService>();
                services.AddSingleton<SystemAudioAnalysisCaptureService>();
                services.AddSingleton<DesktopScreenshotService>();
                services.AddSingleton<ScreenClipCaptureService>();
                services.AddSingleton<ProjectAssetThumbnailService>();
                services.AddSingleton<AssistantArtifactPreviewService>();
                services.AddSingleton<ShellViewModel>();
                services.AddSingleton<RecentActivityService>();
                services.AddSingleton<ProjectAssetActivityService>();
                services.AddSingleton<AssistantCoordinator>();
                services.AddSingleton<ProjectsViewModel>();
                services.AddSingleton<LogsViewModel>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<MainWindow>();
                services.AddSingleton<ToolConfirmationService>();
                services.AddSingleton<WorkspaceRepositoryIndex>();
                services.AddSingleton<LocalToolBroker>();
                services.AddSingleton<GoAiAssistantService>();
            })
            .Build();
    }

    public static new App Current => (App)Application.Current;

    public MainWindow? MainWindow => _window;

    public string DataDirectory { get; }

    public string AccentColor { get; private set; } = AppSettings.DefaultAccentColor;

    public string BackgroundColor { get; private set; } = AppSettings.DefaultBackgroundColor;

    public event EventHandler? ThemeChanged;

    public T GetService<T>() where T : notnull => _host.Services.GetRequiredService<T>();

    public void ApplyTheme(AppTheme theme)
    {
        _appliedTheme = theme;
        if ((_themeRoot ?? _window?.Content as FrameworkElement) is { } root)
        {
            root.RequestedTheme = theme switch
            {
                AppTheme.Light => ElementTheme.Light,
                AppTheme.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }

        ApplyPaletteColors();
    }

    public void ApplyAccentColor(string accentColor)
    {
        if (!TryParsePaletteColor(accentColor, out var color))
        {
            accentColor = AppSettings.DefaultAccentColor;
            _ = TryParsePaletteColor(accentColor, out color);
        }

        AccentColor = accentColor.ToUpperInvariant();
        if (!IsHighContrastEnabled())
        {
            SetBrushColors(AccentBrushKeys, color);
        }

        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ApplyBackgroundColor(string backgroundColor)
    {
        if (!TryParsePaletteColor(backgroundColor, out var color))
        {
            backgroundColor = AppSettings.DefaultBackgroundColor;
            _ = TryParsePaletteColor(backgroundColor, out color);
        }

        BackgroundColor = backgroundColor.ToUpperInvariant();
        if (!IsHighContrastEnabled())
        {
            ApplyBackgroundSurfaceColors(color);
        }

        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _appInstance = AppInstance.FindOrRegisterForKey(GetInstanceKey());
            if (!_appInstance.IsCurrent)
            {
                await _appInstance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
                Environment.Exit(0);
                return;
            }

            _appInstance.Activated += OnAppInstanceActivated;
            await _host.StartAsync();
            var database = GetService<IGoDatabase>();
            await database.InitializeAsync();
            _ = await GetService<IChatRepository>().MarkStreamingMessagesInterruptedAsync();
            var settings = GetService<SettingsCoordinator>();
            await settings.InitializeAsync();
            await settings.UpdateAsync(static current => current);
            _ = await GetService<GoAiConnectionService>().TryProvisionLocalHostAsync();

            var shell = GetService<ShellViewModel>();
            shell.DatabaseStatus = "Datenbank bereit";
            GetService<RecentActivityService>().Restore();
            GetService<ProjectAssetActivityService>().Start();
            _window = GetService<MainWindow>();
            if (_window.Content is FrameworkElement themeRoot)
            {
                _themeRoot = themeRoot;
                _themeRoot.ActualThemeChanged += OnActualThemeChanged;
            }

            ApplyTheme(settings.Current.Theme);
            ApplyAccentColor(settings.Current.AccentColor);
            ApplyBackgroundColor(settings.Current.BackgroundColor);
            _window.Closed += OnWindowClosed;
            _window.BeforeCloseAsync = PrepareShutdownAsync;
            _window.Activate();
            _aiAvailabilityCancellation = new CancellationTokenSource();
            _aiAvailabilityMonitor = MonitorLocalAiAvailabilityAsync(_aiAvailabilityCancellation.Token);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"GO startup failed: {exception}");
            throw;
        }
    }

    private void OnAppInstanceActivated(object? sender, AppActivationArguments args)
    {
        _window?.DispatcherQueue.TryEnqueue(() => _window.BringToForeground());
    }

    private async Task MonitorLocalAiAvailabilityAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var hasActiveRuns = await RefreshLocalAiAvailabilityAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await Task.Delay(
                    hasActiveRuns ? TimeSpan.FromMilliseconds(750) : TimeSpan.FromSeconds(2),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<bool> RefreshLocalAiAvailabilityAsync(CancellationToken cancellationToken)
    {
        var connected = false;
        GpuStatusSnapshot? gpuStatus = null;
        ModelStatusSnapshot? modelStatus = null;
        IReadOnlyList<ServiceStatusSnapshot>? serviceStatus = null;
        try
        {
            var settings = GetService<SettingsCoordinator>().Current;
            if (settings.AiProvider == AiProviderKind.GoAiServer)
            {
                using var client = await GetService<GoAiConnectionService>()
                    .CreateClientAsync(cancellationToken)
                    .ConfigureAwait(false);
                var healthTask = client.GetReadyHealthAsync(cancellationToken);
                var gpuTask = client.GetGpuStatusAsync(cancellationToken);
                var modelTask = client.GetModelStatusAsync(cancellationToken);
                var serviceTask = client.GetServiceStatusAsync(cancellationToken);
                await Task.WhenAll(healthTask, gpuTask, modelTask, serviceTask).ConfigureAwait(false);
                var health = await healthTask.ConfigureAwait(false);
                gpuStatus = await gpuTask.ConfigureAwait(false);
                modelStatus = await modelTask.ConfigureAwait(false);
                serviceStatus = await serviceTask.ConfigureAwait(false);
                connected = string.Equals(health.Status, "ready", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(health.ProtocolVersion, settings.GoAiProtocolVersion, StringComparison.Ordinal);
            }
            else
            {
                connected = await GetService<ILmStudioClient>()
                    .TestConnectionAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            AppLog.LocalAiAvailabilityCheckFailed(GetService<ILogger<App>>(), exception);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        SetLocalAiStatus(connected, gpuStatus, modelStatus, serviceStatus);
        return gpuStatus?.ActiveWorkloads is { Count: > 0 }
            || !string.IsNullOrWhiteSpace(gpuStatus?.ActiveLease);
    }

    private void SetLocalAiStatus(
        bool connected,
        GpuStatusSnapshot? gpuStatus,
        ModelStatusSnapshot? modelStatus,
        IReadOnlyList<ServiceStatusSnapshot>? serviceStatus)
    {
        var shell = GetService<ShellViewModel>();
        var dispatcher = _window?.DispatcherQueue;
        if (dispatcher is null || dispatcher.HasThreadAccess)
        {
            shell.IsAiAvailable = connected;
            shell.SetAiServiceAvailability(connected, modelStatus, serviceStatus);
            shell.SetActiveAiRuns(gpuStatus);
            return;
        }

        _ = dispatcher.TryEnqueue(() =>
        {
            shell.IsAiAvailable = connected;
            shell.SetAiServiceAvailability(connected, modelStatus, serviceStatus);
            shell.SetActiveAiRuns(gpuStatus);
        });
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (_themeRoot is not null)
        {
            _themeRoot.ActualThemeChanged -= OnActualThemeChanged;
            _themeRoot = null;
        }

        if (_window is not null)
        {
            _window.Closed -= OnWindowClosed;
            _window.BeforeCloseAsync = null;
        }
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        if (_appliedTheme == AppTheme.System)
        {
            ApplyPaletteColors();
        }
    }

    private async Task PrepareShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
        {
            return;
        }

        try
        {
            var availabilityCancellation = Interlocked.Exchange(ref _aiAvailabilityCancellation, null);
            var availabilityMonitor = Interlocked.Exchange(ref _aiAvailabilityMonitor, null);
            availabilityCancellation?.Cancel();
            if (availabilityMonitor is not null)
            {
                try
                {
                    await availabilityMonitor;
                }
                catch (OperationCanceledException)
                {
                    // The availability monitor is expected to stop during shutdown.
                }
            }

            availabilityCancellation?.Dispose();
            await _host.StopAsync(TimeSpan.FromSeconds(4));
        }
        finally
        {
            _appInstance?.UnregisterKey();
            if (_host is IAsyncDisposable asyncHost)
            {
                await asyncHost.DisposeAsync();
            }
            else
            {
                _host.Dispose();
            }
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        TryLogCritical(args.Exception, $"Unhandled XAML exception: {args.Message}");
    }

    private void OnAppDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs args)
    {
        TryLogCritical(args.ExceptionObject as Exception, $"Unhandled AppDomain exception; terminating={args.IsTerminating}");
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        TryLogCritical(args.Exception, "Unobserved task exception");
        args.SetObserved();
    }

    private void TryLogCritical(Exception? exception, string details)
    {
        try
        {
            if (exception is not null)
            {
                details = $"{details} | {exception.GetType().FullName} "
                    + $"(0x{exception.HResult:X8}) | {exception.StackTrace}";
            }

            var logger = GetService<ILogger<App>>();
            if (logger.IsEnabled(LogLevel.Critical))
            {
                AppLog.UnhandledApplicationException(logger, exception, details);
            }
        }
        catch
        {
            System.Diagnostics.Debug.WriteLine($"{details}: {exception}");
        }
    }

    private static string ResolveDataDirectory()
    {
        var requested = Environment.GetEnvironmentVariable("GO_DATA_DIRECTORY");
        return string.IsNullOrWhiteSpace(requested)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GO")
            : Path.GetFullPath(requested);
    }

    private static string GetInstanceKey()
    {
        var smokeKey = Environment.GetEnvironmentVariable("GO_SMOKE_INSTANCE_KEY");
        return string.IsNullOrWhiteSpace(smokeKey)
            ? "GO.Main"
            : $"GO.Smoke.{smokeKey}";
    }

    private static IEnumerable<ResourceDictionary> EnumerateResourceDictionaries(ResourceDictionary root)
    {
        yield return root;
        foreach (var merged in root.MergedDictionaries)
        {
            foreach (var nested in EnumerateResourceDictionaries(merged))
            {
                yield return nested;
            }
        }
    }

    private void ApplyPaletteColors()
    {
        if (!IsHighContrastEnabled())
        {
            _ = TryParsePaletteColor(AccentColor, out var accent);
            _ = TryParsePaletteColor(BackgroundColor, out var background);
            SetBrushColors(AccentBrushKeys, accent);
            ApplyBackgroundSurfaceColors(background);
        }

        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyBackgroundSurfaceColors(Windows.UI.Color background)
    {
        var isLight = _appliedTheme == AppTheme.Light
            || (_appliedTheme == AppTheme.System && _themeRoot?.ActualTheme == ElementTheme.Light);
        if (isLight)
        {
            SetBrushColor("GoWindowBrush", MixBackground(0xF3F0F5, background, 0.07, 0xFF));
            SetBrushColor("GoLayerBrush", MixBackground(0xFFFFFF, background, 0.04, 0xF8));
            SetBrushColor("GoLayerStrongBrush", MixBackground(0xFAF8FC, background, 0.08, 0xFF));
            SetBrushColor("GoInputBrush", MixBackground(0xFFFFFF, background, 0.09, 0xFF));
            SetBrushColor("GoHoverBrush", MixBackground(0xF5F1F7, background, 0.13, 0xFF));
            SetBrushColor("GoPressedBrush", MixBackground(0xEEE9F1, background, 0.18, 0xFF));
            SetBrushColor("GoStrokeBrush", MixBackground(0x302A38, background, 0.22, 0x52));
            return;
        }

        SetBrushColor("GoWindowBrush", MixBackground(0x121016, background, 0.08, 0xFF));
        SetBrushColor("GoLayerBrush", MixBackground(0x1B1820, background, 0.12, 0xE6));
        SetBrushColor("GoLayerStrongBrush", MixBackground(0x211D27, background, 0.18, 0xF2));
        SetBrushColor("GoInputBrush", MixBackground(0x28232F, background, 0.20, 0xFF));
        SetBrushColor("GoHoverBrush", MixBackground(0x2E2836, background, 0.23, 0xFF));
        SetBrushColor("GoPressedBrush", MixBackground(0x332C3C, background, 0.28, 0xFF));
        SetBrushColor("GoStrokeBrush", MixBackground(0xFFFFFF, background, 0.28, 0x42));
    }

    private void SetBrushColors(IEnumerable<string> keys, Windows.UI.Color color)
    {
        foreach (var key in keys)
        {
            SetBrushColor(key, color);
        }
    }

    private void SetBrushColor(string key, Windows.UI.Color color)
    {
        foreach (var dictionary in EnumerateResourceDictionaries(Resources))
        {
            if (dictionary.ContainsKey(key)
                && dictionary[key] is SolidColorBrush brush)
            {
                brush.Color = color;
            }
        }
    }

    private static Windows.UI.Color MixBackground(
        uint baseRgb,
        Windows.UI.Color background,
        double backgroundWeight,
        byte alpha)
    {
        var baseColor = Windows.UI.Color.FromArgb(
            alpha,
            (byte)(baseRgb >> 16),
            (byte)(baseRgb >> 8),
            (byte)baseRgb);
        var baseWeight = 1d - backgroundWeight;
        return Windows.UI.Color.FromArgb(
            alpha,
            (byte)Math.Round((baseColor.R * baseWeight) + (background.R * backgroundWeight)),
            (byte)Math.Round((baseColor.G * baseWeight) + (background.G * backgroundWeight)),
            (byte)Math.Round((baseColor.B * baseWeight) + (background.B * backgroundWeight)));
    }

    private static bool IsHighContrastEnabled()
    {
        try
        {
            return new AccessibilitySettings().HighContrast;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParsePaletteColor(string? value, out Windows.UI.Color color)
    {
        color = default;
        return value is { Length: 7 }
            && value[0] == '#'
            && uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)
            && SetColor(rgb, out color);
    }

    private static bool SetColor(uint rgb, out Windows.UI.Color color)
    {
        color = Windows.UI.Color.FromArgb(
            255,
            (byte)(rgb >> 16),
            (byte)(rgb >> 8),
            (byte)rgb);
        return true;
    }
}
