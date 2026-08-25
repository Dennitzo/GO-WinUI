using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Gateway;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var options = new GoAiServerOptions
{
    DataDirectory = ResolveDataDirectory(),
    ExpectedLanIp = Environment.GetEnvironmentVariable("GO_AI_EXPECTED_LAN_IP") ?? "192.168.0.67",
    GatewayPort = ReadGatewayPort(),
    PublicUrl = Environment.GetEnvironmentVariable("GO_AI_PUBLIC_URL") ?? "http://192.168.0.67:8080",
    ModelRuntimeUri = ResolveUri("GO_AI_MODEL_RUNTIME_URL", "http://llm:8080"),
    SearxngUri = ResolveUri("GO_AI_SEARXNG_URL", "http://searxng:8080"),
    SpeechWorkerUri = ResolveUri("GO_AI_SPEECH_WORKER_URL", "http://speech:8080"),
    MediaWorkerUri = ResolveUri("GO_AI_MEDIA_WORKER_URL", "http://media:8080"),
    ImageWorkerUri = ResolveUri("GO_AI_IMAGE_WORKER_URL", "http://image:8080"),
    YouTubeApiKey = Environment.GetEnvironmentVariable("GO_AI_YOUTUBE_API_KEY"),
    WorkerDataDirectory = Environment.GetEnvironmentVariable("GO_AI_WORKER_DATA_DIRECTORY"),
};

var builder = Host.CreateDefaultBuilder(args)
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
        destination.ModelRuntimeUri = options.ModelRuntimeUri;
        destination.SearxngUri = options.SearxngUri;
        destination.SpeechWorkerUri = options.SpeechWorkerUri;
        destination.MediaWorkerUri = options.MediaWorkerUri;
        destination.ImageWorkerUri = options.ImageWorkerUri;
        destination.YouTubeApiKey = options.YouTubeApiKey;
        destination.WorkerDataDirectory = options.WorkerDataDirectory;
    });

await builder.Build().RunAsync().ConfigureAwait(false);

static string ResolveDataDirectory()
{
    var requested = Environment.GetEnvironmentVariable("GO_AI_DATA_DIRECTORY");
    return string.IsNullOrWhiteSpace(requested)
        ? GoAiServerOptions.ResolveDefaultDataDirectory()
        : Path.GetFullPath(requested);
}

static int ReadGatewayPort()
{
    var value = Environment.GetEnvironmentVariable("GO_AI_GATEWAY_PORT");
    return int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port)
        && port is >= 1024 and <= 65535
        ? port
        : 8080;
}

static Uri ResolveUri(string variableName, string fallback)
{
    var configured = Environment.GetEnvironmentVariable(variableName);
    return Uri.TryCreate(configured, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        ? uri
        : new Uri(fallback, UriKind.Absolute);
}
