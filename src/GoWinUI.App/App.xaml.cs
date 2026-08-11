using GoWinUI.App.Services;
using GoWinUI.App.ViewModels;
using GoWinUI.BricsCad.Protocol;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using GoWinUI.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace GoWinUI.App;

public partial class App : Application
{
    private readonly IHost _host;
    private AppInstance? _appInstance;
    private MainWindow? _window;
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
                services.AddSingleton<ProjectAssetThumbnailService>();
                services.AddSingleton<ShellViewModel>();
                services.AddSingleton<AssistantCoordinator>();
                services.AddSingleton<ProjectsViewModel>();
                services.AddSingleton<LogsViewModel>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();
    }

    public static new App Current => (App)Application.Current;

    public MainWindow? MainWindow => _window;

    public string DataDirectory { get; }

    public event EventHandler? ThemeChanged;

    public T GetService<T>() where T : notnull => _host.Services.GetRequiredService<T>();

    public void ApplyTheme(AppTheme theme)
    {
        if (_window?.Content is FrameworkElement root)
        {
            root.RequestedTheme = theme switch
            {
                AppTheme.Light => ElementTheme.Light,
                AppTheme.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
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

            var shell = GetService<ShellViewModel>();
            shell.DatabaseStatus = "SQLite bereit";
            _window = GetService<MainWindow>();
            ApplyTheme(settings.Current.Theme);
            _window.Closed += OnWindowClosed;
            _window.BeforeCloseAsync = PrepareShutdownAsync;
            _window.Activate();
            _ = RefreshLmStudioStatusAsync();
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

    private async Task RefreshLmStudioStatusAsync()
    {
        var shell = GetService<ShellViewModel>();
        try
        {
            var connected = await GetService<ILmStudioClient>().TestConnectionAsync();
            shell.LmStudioStatus = connected
                ? GetService<SettingsCoordinator>().Current.SelectedModel ?? "LM Studio bereit"
                : "LM Studio nicht verbunden";
        }
        catch (Exception exception)
        {
            shell.LmStudioStatus = "LM Studio nicht verbunden";
            AppLog.LmStudioStatusFailed(GetService<ILogger<App>>(), exception);
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (_window is not null)
        {
            _window.Closed -= OnWindowClosed;
            _window.BeforeCloseAsync = null;
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
}
