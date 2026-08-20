using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Workers;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace GoAi.Server.Core.Status;

public sealed class ServiceProbeService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WorkerApiClient _workers;
    private readonly GoAiServerOptions _options;

    public ServiceProbeService(
        IHttpClientFactory httpClientFactory,
        WorkerApiClient workers,
        IOptions<GoAiServerOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _workers = workers;
        _options = options.Value;
    }

    public async Task<IReadOnlyList<ServiceStatusSnapshot>> GetStatusesAsync(
        CancellationToken cancellationToken = default)
    {
        Task<ServiceStatusSnapshot>[] tasks =
        [
            ProbeHttpServiceAsync("SearXNG", _options.SearxngUri, "/healthz", cancellationToken),
            ProbeWorkerAsync("Speech / Live-Untertitel", "speech", _options.SpeechWorkerUri, cancellationToken),
            ProbeWorkerAsync("Media Worker", "media", _options.MediaWorkerUri, cancellationToken),
            ProbeWorkerAsync("Image Worker", "image", _options.ImageWorkerUri, cancellationToken),
        ];
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<ServiceStatusSnapshot> ProbeHttpServiceAsync(
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
            var ready = response.IsSuccessStatusCode;
            return new ServiceStatusSnapshot(
                name,
                ready ? "Bereit" : "Fehler",
                endpoint.ToString(),
                ready,
                DateTimeOffset.UtcNow,
                $"HTTP {(int)response.StatusCode}",
                ready,
                ready ? [name] : []);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return Unavailable(name, endpoint, exception);
        }
    }

    private async Task<ServiceStatusSnapshot> ProbeWorkerAsync(
        string displayName,
        string workerName,
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        try
        {
            var status = await _workers.GetStatusAsync(workerName, cancellationToken).ConfigureAwait(false);
            var state = ReadString(status, "status") ?? "ready";
            var components = GetLoadedComponents(workerName, status);
            var loaded = workerName == "media" || components.Count > 0;
            return new ServiceStatusSnapshot(
                displayName,
                string.Equals(state, "ready", StringComparison.OrdinalIgnoreCase) ? "Bereit" : state,
                endpoint.ToString(),
                true,
                DateTimeOffset.UtcNow,
                BuildWorkerDetail(workerName, components),
                loaded,
                components);
        }
        catch (Exception exception) when (
            exception is HttpRequestException
            or TaskCanceledException
            or IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            return Unavailable(displayName, endpoint, exception);
        }
    }

    private static ServiceStatusSnapshot Unavailable(string name, Uri endpoint, Exception exception) => new(
        name,
        "Nicht gestartet",
        endpoint.ToString(),
        false,
        DateTimeOffset.UtcNow,
        exception is TaskCanceledException ? "Zeitüberschreitung" : "Nicht erreichbar",
        false,
        []);

    private static List<string> GetLoadedComponents(string workerName, JsonElement status)
    {
        var result = new List<string>();
        if (workerName == "speech")
        {
            if (ReadBoolean(status, "sttLoaded")) result.Add("Whisper large-v3");
            if (ReadBoolean(status, "ttsLoaded")) result.Add("Supertonic F5 Ultra · GPU 1");
            if (ReadBoolean(status, "speakerLoaded")) result.Add("ECAPA Sprecherkennung");
        }
        else if (workerName == "image" && ReadBoolean(status, "modelLoaded"))
        {
            result.Add(ReadString(status, "model") ?? "Z-Image-Turbo");
        }
        else if (workerName == "media")
        {
            result.Add("Media Worker");
        }
        return result;
    }

    private static string BuildWorkerDetail(string workerName, List<string> components) =>
        components.Count > 0
            ? string.Join(" · ", components)
            : workerName switch
            {
                "speech" => "Sprachmodelle entladen",
                "image" => "Bildmodell entladen",
                _ => "Dienst aktiv",
            };

    private static bool ReadBoolean(JsonElement status, string name) =>
        status.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True;

    private static string? ReadString(JsonElement status, string name) =>
        status.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
