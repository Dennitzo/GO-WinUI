using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using Microsoft.Extensions.Options;

namespace GoAi.Server.Core.Status;

public sealed class ServiceProbeService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GoAiServerOptions _options;

    public ServiceProbeService(IHttpClientFactory httpClientFactory, IOptions<GoAiServerOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
    }

    public async Task<IReadOnlyList<ServiceStatusSnapshot>> GetStatusesAsync(CancellationToken cancellationToken = default)
    {
        var definitions = new[]
        {
            ("SearXNG", _options.SearxngUri, "/healthz"),
            ("Speech / Live-Untertitel", _options.SpeechWorkerUri, "/health"),
            ("Media Worker", _options.MediaWorkerUri, "/health"),
            ("Image Worker", _options.ImageWorkerUri, "/health"),
        };
        var tasks = definitions.Select(definition => ProbeAsync(
            definition.Item1,
            definition.Item2,
            definition.Item3,
            cancellationToken));
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<ServiceStatusSnapshot> ProbeAsync(
        string name,
        Uri endpoint,
        string healthPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(nameof(ServiceProbeService));
            client.Timeout = TimeSpan.FromSeconds(2);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, healthPath));
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return new ServiceStatusSnapshot(
                name,
                response.IsSuccessStatusCode ? "Bereit" : "Fehler",
                endpoint.ToString(),
                response.IsSuccessStatusCode,
                DateTimeOffset.UtcNow,
                $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return new ServiceStatusSnapshot(
                name,
                "Nicht gestartet",
                endpoint.ToString(),
                false,
                DateTimeOffset.UtcNow,
                exception is TaskCanceledException ? "Zeitüberschreitung" : "Nicht erreichbar");
        }
    }
}
