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
    private static readonly Action<ILogger, Guid, Exception?> LocalAutomationFailed =
        LoggerMessage.Define<Guid>(
            LogLevel.Error,
            new EventId(5900, nameof(LocalAutomationFailed)),
            "Local coding campaign automation command failed for session {SessionId}.");
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
    private readonly SemaphoreSlim _aiAvailabilityLifecycle = new(1, 1);
    private string? _lastLoggedAiConnectionState;
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
                services.AddSingleton<LeanProofService>();
                services.AddSingleton<CodingProofVerifier>();
                services.AddSingleton<ICodingCampaignDefinition, EinsteinCodingCampaignDefinition>();
                services.AddSingleton<ICodingCampaignDefinition, TheoreticalPhysicsCodingCampaignDefinition>();
                services.AddSingleton<ICodingCampaignDefinition, TgaVentilationCodingCampaignDefinition>();
                services.AddSingleton<CodingCampaignCatalog>();
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
                services.AddSingleton<DocumentContextPreparationService>();
                services.AddSingleton<SessionContextPreparationService>();
                services.AddSingleton<LocalToolBroker>();
                services.AddSingleton<CodingDiffService>();
                services.AddSingleton<CodingRunTraceService>();
                services.AddSingleton<CodingSolutionPdfExporter>();
                services.AddSingleton<GoAiAssistantService>();
                services.AddSingleton<ICodingCampaignAgent>(static provider => provider.GetRequiredService<GoAiAssistantService>());
                services.AddSingleton<CodingCampaignService>();
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
            await GetService<CodingCampaignService>().PrepareForClientStartAsync();
            await GetService<GoAiAssistantService>().StopPersistedCampaignRunsAtStartupAsync();
            _ = await GetService<GoAiConnectionService>().TryProvisionLocalHostAsync();

            var shell = GetService<ShellViewModel>();
            shell.IsAiConnectionEnabled = settings.Current.IsAiConnectionEnabled;
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
            await ApplyAiConnectionModeAsync(settings.Current.IsAiConnectionEnabled);
            var initialArguments = string.Join(' ', Environment.GetCommandLineArgs().Skip(1));
            _ = await TryExecuteLocalAutomationAsync(
                string.IsNullOrWhiteSpace(initialArguments) ? args.Arguments : initialArguments);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"GO startup failed: {exception}");
            throw;
        }
    }

    public async Task ApplyAiConnectionModeAsync(bool enabled)
    {
        await _aiAvailabilityLifecycle.WaitAsync();
        try
        {
            var previousCancellation = Interlocked.Exchange(ref _aiAvailabilityCancellation, null);
            var previousMonitor = Interlocked.Exchange(ref _aiAvailabilityMonitor, null);
            previousCancellation?.Cancel();
            GetService<GoAiConnectionService>().ApplyConnectionMode(enabled);
            if (previousMonitor is not null)
            {
                try
                {
                    await previousMonitor;
                }
                catch (OperationCanceledException)
                {
                    // Changing connection mode intentionally stops an active availability probe.
                }
            }
            previousCancellation?.Dispose();

            var shell = GetService<ShellViewModel>();
            shell.IsAiConnectionEnabled = enabled;
            if (!enabled)
            {
                SetLocalAiStatus(false, false, null, null, null);
                return;
            }

            shell.BeginAiAvailabilityCheck();
            if (Volatile.Read(ref _shutdownStarted) != 0)
            {
                return;
            }

            var cancellation = new CancellationTokenSource();
            _aiAvailabilityCancellation = cancellation;
            _aiAvailabilityMonitor = MonitorLocalAiAvailabilityAsync(cancellation.Token);
        }
        finally
        {
            _aiAvailabilityLifecycle.Release();
        }
    }

    private void OnAppInstanceActivated(object? sender, AppActivationArguments args)
    {
        var arguments = args.Kind == ExtendedActivationKind.Launch
            && args.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launchArguments
                ? launchArguments.Arguments
                : null;
        _window?.DispatcherQueue.TryEnqueue(async () =>
        {
            _window.BringToForeground();
            _ = await TryExecuteLocalAutomationAsync(arguments);
        });
    }

    private async Task<bool> TryExecuteLocalAutomationAsync(string? arguments)
    {
        if (!LocalAutomationCommand.TryParse(arguments, out var command) || command is null)
        {
            return false;
        }

        try
        {
            var campaigns = GetService<CodingCampaignService>();
            if (command.Action == LocalAutomationAction.RunCodingCampaign)
            {
                _ = await campaigns.RunAsync(command.SessionId);
            }
            else
            {
                _ = await campaigns.StopAsync(command.SessionId);
            }

            return true;
        }
        catch (Exception exception)
        {
            LocalAutomationFailed(GetService<ILogger<App>>(), command.SessionId, exception);
            return false;
        }
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
        var currentSettings = GetService<SettingsCoordinator>().Current;
        if (!currentSettings.IsAiConnectionEnabled)
        {
            SetLocalAiStatus(false, false, null, null, null);
            return false;
        }

        var connected = false;
        var serverReady = false;
        GpuStatusSnapshot? gpuStatus = null;
        ModelStatusSnapshot? modelStatus = null;
        IReadOnlyList<ServiceStatusSnapshot>? serviceStatus = null;
        try
        {
            if (currentSettings.AiProvider == AiProviderKind.GoAiServer)
            {
                using var client = await GetService<GoAiConnectionService>()
                    .CreateClientAsync(cancellationToken)
                    .ConfigureAwait(false);
                var healthTask = client.GetReadyHealthAsync(cancellationToken);
                var capabilitiesTask = client.GetCapabilitiesAsync(cancellationToken);

                // Readiness may legitimately be degraded because one optional
                // model is still downloading. Capabilities is authenticated and
                // therefore proves that this client can use the gateway.
                var health = await healthTask.ConfigureAwait(false);
                var capabilities = await capabilitiesTask.ConfigureAwait(false);
                connected = string.Equals(
                    health.ProtocolVersion,
                    currentSettings.GoAiProtocolVersion,
                    StringComparison.Ordinal)
                    && string.Equals(
                        capabilities.ProtocolVersion,
                        currentSettings.GoAiProtocolVersion,
                        StringComparison.Ordinal);
                serverReady = connected
                    && string.Equals(health.Status, "ready", StringComparison.OrdinalIgnoreCase);

                // Diagnostic endpoints enrich individual service chips but must
                // never downgrade an already authenticated gateway connection.
                var gpuTask = client.GetGpuStatusAsync(cancellationToken);
                var modelTask = client.GetModelStatusAsync(cancellationToken);
                var serviceTask = client.GetServiceStatusAsync(cancellationToken);
                gpuStatus = await AwaitOptionalStatusAsync(gpuTask, cancellationToken).ConfigureAwait(false);
                modelStatus = await AwaitOptionalStatusAsync(modelTask, cancellationToken).ConfigureAwait(false);
                serviceStatus = await AwaitOptionalStatusAsync(serviceTask, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                connected = await GetService<ILmStudioClient>()
                    .TestConnectionAsync(cancellationToken)
                    .ConfigureAwait(false);
                serverReady = connected;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (GoAiConnectionDisabledException)
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

        SetLocalAiStatus(connected, serverReady, gpuStatus, modelStatus, serviceStatus);
        return gpuStatus?.ActiveWorkloads is { Count: > 0 }
            || !string.IsNullOrWhiteSpace(gpuStatus?.ActiveLease);
    }

    private static async Task<T?> AwaitOptionalStatusAsync<T>(
        Task<T> task,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    private void SetLocalAiStatus(
        bool connected,
        bool serverReady,
        GpuStatusSnapshot? gpuStatus,
        ModelStatusSnapshot? modelStatus,
        IReadOnlyList<ServiceStatusSnapshot>? serviceStatus)
    {
        var connectionState = !GetService<SettingsCoordinator>().Current.IsAiConnectionEnabled
            ? "Offline"
            : !connected
                ? "Nicht erreichbar"
                : serverReady
                    ? "Online"
                    : "Online · Eingeschränkt";
        if (!string.Equals(
                Interlocked.Exchange(ref _lastLoggedAiConnectionState, connectionState),
                connectionState,
                StringComparison.Ordinal))
        {
            var logger = GetService<ILogger<App>>();
            if (logger.IsEnabled(LogLevel.Information))
            {
                AppLog.LocalAiConnectionStateChanged(logger, connectionState);
            }
        }

        var shell = GetService<ShellViewModel>();
        var dispatcher = _window?.DispatcherQueue;
        if (dispatcher is null || dispatcher.HasThreadAccess)
        {
            shell.ApplyAiAvailabilitySnapshot(connected, serverReady, gpuStatus, modelStatus, serviceStatus);
            return;
        }

        _ = dispatcher.TryEnqueue(() =>
        {
            shell.ApplyAiAvailabilitySnapshot(connected, serverReady, gpuStatus, modelStatus, serviceStatus);
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
