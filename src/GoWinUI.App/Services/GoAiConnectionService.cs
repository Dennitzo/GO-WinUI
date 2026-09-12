using GoAi.Client;
using GoAi.Contracts;
using Microsoft.Extensions.Logging;

namespace GoWinUI.App.Services;

public sealed record GoAiConnectionStatus(
    bool IsReachable,
    bool IsReady,
    string Message,
    CapabilitySnapshot? Capabilities = null,
    HealthSnapshot? Health = null);

public sealed class GoAiConnectionService(
    SettingsCoordinator settings,
    ILogger<GoAiConnectionService> logger,
    NativeModelRuntimeService? nativeRuntime = null) : IDisposable
{
    public const string DefaultServerUrl = "http://192.168.0.67:8080";
    private static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(12);
    private static readonly Action<ILogger, string, Exception?> ConnectionFailed = LoggerMessage.Define<string>(
        LogLevel.Warning,
        new EventId(5200, nameof(ConnectionFailed)),
        "GO Docker gateway connection check failed ({FailureKind}).");
    private readonly object _connectionModeSync = new();
    private readonly string _clientId = $"go-winui-{Guid.NewGuid():N}";
    private readonly List<CancellationTokenSource> _retiredModeCancellations = [];
    private Func<HttpMessageHandler>? _httpHandlerFactory;
    private TimeSpan _probeTimeout = DefaultProbeTimeout;
    private CancellationTokenSource? _connectionModeCancellation =
        settings.Current.IsAiConnectionEnabled ? new CancellationTokenSource() : null;
    private bool _disposed;

    public string? NativeRuntimeError { get; private set; }

    internal GoAiConnectionService(
        SettingsCoordinator settings,
        ILogger<GoAiConnectionService> logger,
        Func<HttpMessageHandler> httpHandlerFactory,
        TimeSpan? probeTimeout = null,
        NativeModelRuntimeService? nativeRuntime = null)
        : this(settings, logger, nativeRuntime)
    {
        _httpHandlerFactory = httpHandlerFactory ?? throw new ArgumentNullException(nameof(httpHandlerFactory));
        if (probeTimeout is { } timeout)
        {
            if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(probeTimeout));
            _probeTimeout = timeout;
        }
    }

    public async Task<GoAiClient> CreateClientAsync(CancellationToken cancellationToken = default)
    {
        var connectionModeToken = GetConnectionModeToken();
        if (!Uri.TryCreate(settings.Current.GoAiServerUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseAddress)
            || baseAddress.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("Die Docker-Gatewayadresse ist ungültig.");
        }

        if (nativeRuntime is not null)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connectionModeToken);
            try
            {
                await nativeRuntime.EnsureStartedAsync(baseAddress, linked.Token).ConfigureAwait(false);
                NativeRuntimeError = null;
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Other gateway services remain usable even if a local language-model dependency is missing.
                NativeRuntimeError = exception.Message;
            }
        }

        HttpMessageHandler handler = _httpHandlerFactory?.Invoke()
            ?? new HttpClientHandler { UseProxy = false };
        var modeHandler = new ConnectionModeHandler(handler, connectionModeToken);
        var httpClient = new HttpClient(modeHandler, disposeHandler: true)
        {
            BaseAddress = baseAddress,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("GO-WinUI/1.0");
        return new GoAiClient(httpClient, _clientId, ownsHttpClient: true);
    }

    public async Task<GoAiConnectionStatus> TestAsync(CancellationToken cancellationToken = default)
    {
        if (!settings.Current.IsAiConnectionEnabled)
        {
            return new(false, false, "Offline · AI-Verbindungen sind deaktiviert.");
        }

        try
        {
            using var client = await CreateClientAsync(cancellationToken).ConfigureAwait(false);
            using var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeCancellation.CancelAfter(_probeTimeout);
            var probeToken = probeCancellation.Token;
            var live = await client.GetLiveHealthAsync(probeToken).ConfigureAwait(false);
            if (!string.Equals(live.ProtocolVersion, settings.Current.GoAiProtocolVersion, StringComparison.Ordinal))
            {
                return new(false, false,
                    $"Protokollabweichung: Gateway {live.ProtocolVersion}, GO {settings.Current.GoAiProtocolVersion}.",
                    Health: live);
            }

            var capabilities = await client.GetCapabilitiesAsync(probeToken).ConfigureAwait(false);
            var health = await client.GetReadyHealthAsync(probeToken).ConfigureAwait(false);
            return health.Status switch
            {
                "ready" => new(true, true,
                    $"Verbunden · Modell bereit · {capabilities.ServerTools.Count} Servertools",
                    capabilities, health),
                "modelLoading" => new(true, true,
                    $"Verbunden · Modell wird geladen · {health.Reason}", capabilities, health),
                "modelNotLoaded" => new(true, true,
                    "Verbunden · Modell wird beim ersten AI-Lauf geladen", capabilities, health),
                _ => new(true, false,
                    $"Verbunden · Eingeschränkt · {NativeRuntimeError ?? health.Reason ?? "Dienstfehler"}", capabilities, health),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ConnectionFailed(logger, exception.GetType().Name, exception);
            return new(false, false, FriendlyConnectionError(exception));
        }
    }

    public void ApplyConnectionMode(bool enabled)
    {
        CancellationTokenSource? cancellation = null;
        lock (_connectionModeSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (enabled)
            {
                _connectionModeCancellation ??= new CancellationTokenSource();
            }
            else if (_connectionModeCancellation is { } active)
            {
                _connectionModeCancellation = null;
                _retiredModeCancellations.Add(active);
                cancellation = active;
            }
        }
        cancellation?.Cancel();
    }

    public void Dispose()
    {
        CancellationTokenSource[] sources;
        lock (_connectionModeSync)
        {
            if (_disposed) return;
            _disposed = true;
            if (_connectionModeCancellation is { } active)
            {
                _retiredModeCancellations.Add(active);
                _connectionModeCancellation = null;
            }
            sources = [.. _retiredModeCancellations];
            _retiredModeCancellations.Clear();
        }
        foreach (var source in sources)
        {
            source.Cancel();
            source.Dispose();
        }
    }

    private CancellationToken GetConnectionModeToken()
    {
        lock (_connectionModeSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!settings.Current.IsAiConnectionEnabled || _connectionModeCancellation is null)
                throw new GoAiConnectionDisabledException();
            return _connectionModeCancellation.Token;
        }
    }

    private static string FriendlyConnectionError(Exception exception) => exception switch
    {
        GoAiConnectionDisabledException => exception.Message,
        GoAiApiException apiException => apiException.Problem?.Detail ?? apiException.Message,
        HttpRequestException => "Der GO Docker-Gatewaystack ist nicht erreichbar.",
        TaskCanceledException => "Die Verbindung zum Docker-Gateway hat das Zeitlimit überschritten.",
        OperationCanceledException => "Die Verbindung zum Docker-Gateway hat das Zeitlimit überschritten.",
        _ => exception.Message,
    };

    private sealed class ConnectionModeHandler(
        HttpMessageHandler innerHandler,
        CancellationToken connectionModeToken) : DelegatingHandler(innerHandler)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (connectionModeToken.IsCancellationRequested)
                throw new GoAiConnectionDisabledException();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                connectionModeToken);
            return await base.SendAsync(request, linked.Token).ConfigureAwait(false);
        }
    }
}

public sealed class GoAiConnectionDisabledException()
    : InvalidOperationException("Offline · AI-Verbindungen sind deaktiviert.");
