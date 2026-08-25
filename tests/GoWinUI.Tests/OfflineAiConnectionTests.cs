using GoWinUI.App.Services;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Sockets;

namespace GoWinUI.Tests;

public sealed class OfflineAiConnectionTests
{
    [Fact]
    public async Task RequestDrivenModelStateIsReportedAsReadyForClientUse()
    {
        var probe = new ProbeHttpHandler(new Dictionary<string, (HttpStatusCode StatusCode, string Body)>
        {
            ["/v1/health/live"] = (HttpStatusCode.OK,
                """{"status":"live","protocolVersion":"1.0","timestamp":"2026-08-24T07:29:18Z"}"""),
            ["/v1/capabilities"] = (HttpStatusCode.OK,
                """{"protocolVersion":"1.0","serverVersion":"1.0","models":[],"serverTools":[],"clientTools":[],"uploadLimits":{},"mediaTypes":[],"supportsSseResume":true,"uploadChunkSize":8388608}"""),
            ["/v1/health/ready"] = (HttpStatusCode.OK,
                """{"status":"modelNotLoaded","protocolVersion":"1.0","timestamp":"2026-08-24T07:29:18Z","reason":"Das Modell wird beim ersten AI-Lauf geladen."}"""),
        });
        var store = new RecordingSettingsStore(new AppSettings
        {
            IsAiConnectionEnabled = true,
            GoAiServerUrl = "http://127.0.0.1:65000",
        });
        using var settings = new SettingsCoordinator(store);
        await settings.InitializeAsync();
        using var connection = new GoAiConnectionService(
            settings,
            NullLogger<GoAiConnectionService>.Instance,
            () => probe);

        var status = await connection.TestAsync();

        Assert.True(status.IsReachable);
        Assert.True(status.IsReady);
        Assert.Contains("ersten AI-Lauf", status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingRequiredModelIsReportedAsReachableButDegraded()
    {
        var probe = new ProbeHttpHandler(new Dictionary<string, (HttpStatusCode StatusCode, string Body)>
        {
            ["/v1/health/live"] = (HttpStatusCode.OK,
                """{"status":"live","protocolVersion":"1.0","timestamp":"2026-08-20T07:29:18Z"}"""),
            ["/v1/capabilities"] = (HttpStatusCode.OK,
                """{"protocolVersion":"1.0","serverVersion":"1.0","models":[],"serverTools":[],"clientTools":[],"uploadLimits":{},"mediaTypes":[],"supportsSseResume":true,"uploadChunkSize":8388608}"""),
            ["/v1/health/ready"] = (HttpStatusCode.ServiceUnavailable,
                """{"status":"notReady","protocolVersion":"1.0","timestamp":"2026-08-20T07:29:18Z","reason":"Erforderliche Modelle fehlen: gpt-oss-120b","repair":"Das gepinnte Docker-Modell vollständig installieren."}"""),
        });
        var store = new RecordingSettingsStore(new AppSettings
        {
            IsAiConnectionEnabled = true,
            GoAiServerUrl = "http://127.0.0.1:65000",
        });
        using var settings = new SettingsCoordinator(store);
        await settings.InitializeAsync();
        var logger = new RecordingLogger<GoAiConnectionService>();
        using var connection = new GoAiConnectionService(settings, logger, () => probe);

        var status = await connection.TestAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(status.IsReachable, $"{status.Message} {logger.LastException}");
        Assert.False(status.IsReady);
        Assert.Contains("Eingeschränkt", status.Message, StringComparison.Ordinal);
        Assert.Contains("gpt-oss-120b", status.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("nicht erreichbar", status.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["/v1/health/live", "/v1/capabilities", "/v1/health/ready"], probe.Paths);
    }

    [Fact]
    public async Task OfflineOperationsReturnWithoutCreatingANetworkClient()
    {
        var store = new RecordingSettingsStore(new AppSettings { IsAiConnectionEnabled = false });
        using var settings = new SettingsCoordinator(store);
        await settings.InitializeAsync();
        using var connection = new GoAiConnectionService(settings, NullLogger<GoAiConnectionService>.Instance);

        var exception = await Assert.ThrowsAsync<GoAiConnectionDisabledException>(() => connection.CreateClientAsync());
        var status = await connection.TestAsync();

        Assert.Contains("Offline", exception.Message, StringComparison.Ordinal);
        Assert.False(status.IsReady);
        Assert.Contains("Offline", status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisablingConnectionModeCancelsAnAlreadyRunningRequest()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var store = new RecordingSettingsStore(new AppSettings
        {
            IsAiConnectionEnabled = true,
            GoAiServerUrl = $"http://127.0.0.1:{port}",
        });
        using var settings = new SettingsCoordinator(store);
        await settings.InitializeAsync();
        using var connection = new GoAiConnectionService(settings, NullLogger<GoAiConnectionService>.Instance);
        using var client = await connection.CreateClientAsync();

        var request = client.GetReadyHealthAsync();
        using var accepted = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
        using var reader = new StreamReader(accepted.GetStream(), leaveOpen: true);
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5))))
        {
        }

        connection.ApplyConnectionMode(false);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ExplicitOnlineModeSurvivesASettingsRoundTrip()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var store = environment.Get<ISettingsStore>();

        await store.SaveAsync(new AppSettings { IsAiConnectionEnabled = true });

        var restored = await store.LoadAsync();
        Assert.True(restored.IsAiConnectionEnabled);
        Assert.Equal(AppSettings.CurrentVersion, restored.Version);
    }

    [Fact]
    public async Task UnresponsiveGatewayProbeEndsAtTheDedicatedTimeout()
    {
        var store = new RecordingSettingsStore(new AppSettings
        {
            IsAiConnectionEnabled = true,
            GoAiServerUrl = "http://127.0.0.1:65000",
        });
        using var settings = new SettingsCoordinator(store);
        await settings.InitializeAsync();
        using var connection = new GoAiConnectionService(
            settings,
            NullLogger<GoAiConnectionService>.Instance,
            static () => new HangingHttpHandler(),
            TimeSpan.FromMilliseconds(50));

        var status = await connection.TestAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(status.IsReachable);
        Assert.False(status.IsReady);
        Assert.Contains("Zeitlimit", status.Message, StringComparison.Ordinal);
    }

    private sealed class ProbeHttpHandler(
        IReadOnlyDictionary<string, (HttpStatusCode StatusCode, string Body)> responses) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            Paths.Add(path);
            return Task.FromResult(responses.TryGetValue(path, out var response)
                ? new HttpResponseMessage(response.StatusCode)
                {
                    Content = new StringContent(response.Body, System.Text.Encoding.UTF8, "application/json"),
                }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class HangingHttpHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = request;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancellation token must end the synthetic request.");
        }
    }

    private sealed class RecordingSettingsStore(AppSettings current) : ISettingsStore
    {
        private AppSettings _current = current;
        public string SettingsPath { get; } = Path.Combine(Path.GetTempPath(), "GO-tests-unused-settings.json");

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_current);
        }

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _current = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public Exception? LastException { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => LastException = exception;
    }
}
