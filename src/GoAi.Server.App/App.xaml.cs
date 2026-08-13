using GoAi.Server.App.ViewModels;
using GoAi.Server.Core.Gateway;
using GoAi.Server.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace GoAi.Server.App;

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "WinUI owns the application lifetime and disposes the host during window shutdown.")]
public partial class App : Application
{
    private readonly IHost _host;
    private AppInstance? _appInstance;
    private MainWindow? _window;
    private int _shutdownStarted;

    public App()
    {
        InitializeComponent();
        var options = CreateServerOptions();
        var builder = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddDebug();
                logging.AddFilter("System.Net.Http.HttpClient", LogLevel.None);
            });
        if (IsDashboardOnly())
        {
            builder.ConfigureServices(services =>
            {
                services.AddGoAiServerServices(options, includeHostedServices: false);
                services.AddSingleton<ServerDashboardViewModel>();
                services.AddSingleton<MainWindow>();
            });
        }
        else
        {
            builder.ConfigureGoAiServer(configure => CopyOptions(options, configure));
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<ServerDashboardViewModel>();
                services.AddSingleton<MainWindow>();
            });
        }

        _host = builder.Build();
    }

    public static new App Current => (App)Application.Current;

    public T GetService<T>() where T : notnull => _host.Services.GetRequiredService<T>();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _appInstance = AppInstance.FindOrRegisterForKey(GetInstanceKey());
        if (!_appInstance.IsCurrent)
        {
            await _appInstance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
            Environment.Exit(0);
            return;
        }

        _appInstance.Activated += OnActivated;
        try
        {
            await _host.StartAsync();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var runtime = GetService<ServerRuntimeState>();
            runtime.SetGatewayState("Fehler", "Gateway konnte nicht gestartet werden.");
            runtime.WriteLog("Critical", "gateway.start_failed", $"Gateway-Start fehlgeschlagen ({exception.GetType().Name}).");
        }

        _window = GetService<MainWindow>();
        _window.PrepareShutdownAsync = ShutdownAsync;
        _window.Activate();
    }

    private void OnActivated(object? sender, AppActivationArguments args) => _window?.BringToForeground();

    private async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
        {
            return;
        }

        try
        {
            await _host.StopAsync(TimeSpan.FromSeconds(8));
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

    private static string ResolveDataDirectory()
    {
        var requested = Environment.GetEnvironmentVariable("GO_AI_DATA_DIRECTORY");
        return string.IsNullOrWhiteSpace(requested)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GO-AI-Server")
            : Path.GetFullPath(requested);
    }

    private static string GetInstanceKey()
    {
        var smokeKey = Environment.GetEnvironmentVariable("GO_AI_SMOKE_INSTANCE_KEY");
        return string.IsNullOrWhiteSpace(smokeKey)
            ? "GO.AI.Server"
            : $"GO.AI.Server.Smoke.{smokeKey}";
    }

    private static int ReadGatewayPort()
    {
        var value = Environment.GetEnvironmentVariable("GO_AI_GATEWAY_PORT");
        return int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port)
            && port is >= 1024 and <= 65535
            ? port
            : 7080;
    }

    private static GoAi.Server.Core.Configuration.GoAiServerOptions CreateServerOptions() => new()
    {
        DataDirectory = ResolveDataDirectory(),
        ExpectedLanIp = Environment.GetEnvironmentVariable("GO_AI_EXPECTED_LAN_IP") ?? "192.168.0.67",
        GatewayPort = ReadGatewayPort(),
        PublicUrl = Environment.GetEnvironmentVariable("GO_AI_PUBLIC_URL") ?? "https://192.168.0.67:8443",
        YouTubeApiKey = Environment.GetEnvironmentVariable("GO_AI_YOUTUBE_API_KEY"),
        ProviderDataDirectory = Environment.GetEnvironmentVariable("GO_AI_PROVIDER_DATA_DIRECTORY"),
        LmStudioTokenFile = Environment.GetEnvironmentVariable("GO_AI_LM_STUDIO_TOKEN_FILE"),
        WorkerKeyDirectory = Environment.GetEnvironmentVariable("GO_AI_WORKER_KEY_DIRECTORY"),
        WorkerDataDirectory = Environment.GetEnvironmentVariable("GO_AI_WORKER_DATA_DIRECTORY"),
        RequireLmStudioAuthentication = !string.Equals(
            Environment.GetEnvironmentVariable("GO_AI_ALLOW_UNAUTHENTICATED_LM_STUDIO"),
            "1",
            StringComparison.Ordinal),
    };

    private static bool IsDashboardOnly()
    {
        if (Environment.GetCommandLineArgs()
            .Any(static argument => string.Equals(argument, "--dashboard-only", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        using var client = new System.Net.Sockets.TcpClient();
        try
        {
            var connection = client.ConnectAsync(System.Net.IPAddress.Loopback, ReadGatewayPort());
            return connection.Wait(TimeSpan.FromMilliseconds(350)) && client.Connected;
        }
        catch (Exception exception) when (exception is System.Net.Sockets.SocketException or AggregateException)
        {
            return false;
        }
    }

    private static void CopyOptions(
        GoAi.Server.Core.Configuration.GoAiServerOptions source,
        GoAi.Server.Core.Configuration.GoAiServerOptions destination)
    {
        destination.DataDirectory = source.DataDirectory;
        destination.ExpectedLanIp = source.ExpectedLanIp;
        destination.GatewayPort = source.GatewayPort;
        destination.PublicUrl = source.PublicUrl;
        destination.YouTubeApiKey = source.YouTubeApiKey;
        destination.ProviderDataDirectory = source.ProviderDataDirectory;
        destination.LmStudioTokenFile = source.LmStudioTokenFile;
        destination.WorkerKeyDirectory = source.WorkerKeyDirectory;
        destination.WorkerDataDirectory = source.WorkerDataDirectory;
        destination.RequireLmStudioAuthentication = source.RequireLmStudioAuthentication;
    }
}
