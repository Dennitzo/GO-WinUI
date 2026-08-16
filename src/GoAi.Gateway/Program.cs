using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Gateway;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var expectedLanIp = Environment.GetEnvironmentVariable("GO_AI_EXPECTED_LAN_IP") ?? "192.168.0.67";
var options = new GoAiServerOptions
{
    DataDirectory = ResolveDataDirectory(),
    ExpectedLanIp = expectedLanIp,
    GatewayPort = ReadGatewayPort(),
    PublicUrl = Environment.GetEnvironmentVariable("GO_AI_PUBLIC_URL") ?? "https://192.168.0.67:8443",
    LmStudioUri = ResolveLmStudioUri(expectedLanIp),
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

var builder = Host.CreateDefaultBuilder(args)
    .UseWindowsService(service => service.ServiceName = "GO AI Server Gateway")
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        if (Environment.UserInteractive)
        {
            logging.AddSimpleConsole(console => console.SingleLine = true);
        }
        logging.AddFilter("System.Net.Http.HttpClient", LogLevel.None);
    })
    .ConfigureGoAiServer(destination =>
    {
        destination.DataDirectory = options.DataDirectory;
        destination.ExpectedLanIp = options.ExpectedLanIp;
        destination.GatewayPort = options.GatewayPort;
        destination.PublicUrl = options.PublicUrl;
        destination.YouTubeApiKey = options.YouTubeApiKey;
        destination.ProviderDataDirectory = options.ProviderDataDirectory;
        destination.LmStudioTokenFile = options.LmStudioTokenFile;
        destination.WorkerKeyDirectory = options.WorkerKeyDirectory;
        destination.WorkerDataDirectory = options.WorkerDataDirectory;
        destination.RequireLmStudioAuthentication = options.RequireLmStudioAuthentication;
    });

await builder.Build().RunAsync().ConfigureAwait(false);

static string ResolveDataDirectory()
{
    var requested = Environment.GetEnvironmentVariable("GO_AI_DATA_DIRECTORY");
    return string.IsNullOrWhiteSpace(requested)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GO-AI-Server")
        : Path.GetFullPath(requested);
}

static int ReadGatewayPort()
{
    var value = Environment.GetEnvironmentVariable("GO_AI_GATEWAY_PORT");
    return int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port)
        && port is >= 1024 and <= 65535
        ? port
        : 7080;
}

static Uri ResolveLmStudioUri(string expectedLanIp)
{
    var configured = Environment.GetEnvironmentVariable("GO_AI_LM_STUDIO_URL");
    return Uri.TryCreate(configured, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        ? uri
        : new Uri($"http://{expectedLanIp}:1234", UriKind.Absolute);
}
