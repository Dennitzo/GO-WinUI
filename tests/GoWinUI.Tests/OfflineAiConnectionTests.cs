using GoWinUI.App.Services;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;

namespace GoWinUI.Tests;

public sealed class OfflineAiConnectionTests
{
    [Fact]
    public async Task MissingOptionalCodingModelIsReportedAsReachableButDegraded()
    {
        var probe = new ProbeHttpHandler(new Dictionary<string, (HttpStatusCode StatusCode, string Body)>
        {
            ["/v1/health/live"] = (HttpStatusCode.OK, """
                {"status":"live","protocolVersion":"1.0","timestamp":"2026-08-20T07:29:18Z"}
                """),
            ["/v1/capabilities"] = (HttpStatusCode.OK, """
                {"protocolVersion":"1.0","serverVersion":"1.0","models":[],"serverTools":[],"clientTools":[],"uploadLimits":{},"mediaTypes":[],"supportsSseResume":true,"uploadChunkSize":8388608}
                """),
            ["/v1/health/ready"] = (HttpStatusCode.ServiceUnavailable, """
                {"status":"notReady","protocolVersion":"1.0","timestamp":"2026-08-20T07:29:18Z","reason":"Erforderliche Modelle fehlen: qwen3-coder-next","repair":"Das Coding-Modell vollständig herunterladen."}
                """),
        });
        var store = new RecordingSettingsStore(new AppSettings
        {
            IsAiConnectionEnabled = true,
            GoAiServerUrl = "http://127.0.0.1:65000",
        });
        using var settings = new SettingsCoordinator(store);
        await settings.InitializeAsync();
        var secrets = new RecordingSecretStore("test-api-key");
        var logger = new RecordingLogger<GoAiConnectionService>();
        using var connection = new GoAiConnectionService(
            settings,
            secrets,
            logger,
            () => probe);

        // The complete test suite deliberately runs CPU-heavy Lean, PDF and repository
        // integration tests in parallel. Give the loopback probe enough scheduling headroom
        // without weakening the production timeout/cancellation behavior under test.
        var status = await connection.TestAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(status.IsReachable, $"{status.Message} {logger.LastException}");
        Assert.False(status.IsReady);
        Assert.Contains("Eingeschränkt", status.Message, StringComparison.Ordinal);
        Assert.Contains("qwen3-coder-next", status.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("nicht erreichbar", status.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            ["/v1/health/live", "/v1/capabilities", "/v1/health/ready"],
            probe.Paths);
    }

    [Fact]
    public async Task OfflineOperationsReturnBeforeReadingTheApiKey()
    {
        var store = new RecordingSettingsStore(new AppSettings());
        using var settings = new SettingsCoordinator(store);
        await settings.InitializeAsync();
        var secrets = new RecordingSecretStore("must-not-be-read");
        using var connection = new GoAiConnectionService(
            settings,
            secrets,
            NullLogger<GoAiConnectionService>.Instance);

        var exception = await Assert.ThrowsAsync<GoAiConnectionDisabledException>(
            () => connection.CreateClientAsync());
        var status = await connection.TestAsync();
        var provisioned = await connection.TryProvisionLocalHostAsync();

        Assert.Contains("Offline", exception.Message, StringComparison.Ordinal);
        Assert.False(status.IsReady);
        Assert.Contains("Offline", status.Message, StringComparison.Ordinal);
        Assert.False(provisioned);
        Assert.Equal(0, secrets.GetApiKeyCallCount);
        Assert.Equal(0, secrets.SetApiKeyCallCount);
        Assert.Equal(0, secrets.DeleteApiKeyCallCount);
    }

    [Fact]
    public async Task DisablingConnectionModePreventsAnExistingClientFromSendingARequest()
    {
        var store = new RecordingSettingsStore(new AppSettings { IsAiConnectionEnabled = true });
        using var settings = new SettingsCoordinator(store);
        await settings.InitializeAsync();
        var secrets = new RecordingSecretStore("test-api-key");
        using var connection = new GoAiConnectionService(
            settings,
            secrets,
            NullLogger<GoAiConnectionService>.Instance);
        using var client = await connection.CreateClientAsync();

        connection.ApplyConnectionMode(false);

        await Assert.ThrowsAsync<GoAiConnectionDisabledException>(
            () => client.GetReadyHealthAsync());
        Assert.Equal(1, secrets.GetApiKeyCallCount);
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
        var secrets = new RecordingSecretStore("test-api-key");
        using var connection = new GoAiConnectionService(
            settings,
            secrets,
            NullLogger<GoAiConnectionService>.Instance);
        using var client = await connection.CreateClientAsync();

        var request = client.GetReadyHealthAsync();
        using var accepted = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
        using var reader = new StreamReader(accepted.GetStream(), leaveOpen: true);
        string? line;
        do
        {
            line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        while (!string.IsNullOrEmpty(line));

        connection.ApplyConnectionMode(false);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => request.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ExplicitOnlineModeSurvivesASettingsRoundTrip()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var store = environment.Get<ISettingsStore>();

        await store.SaveAsync(new AppSettings { IsAiConnectionEnabled = true });

        var restored = await store.LoadAsync();
        Assert.True(restored.IsAiConnectionEnabled);
        Assert.Equal(11, restored.Version);
    }

    private sealed class ProbeHttpHandler(
        IReadOnlyDictionary<string, (HttpStatusCode StatusCode, string Body)> responses)
        : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            Paths.Add(path);
            if (!responses.TryGetValue(path, out var response))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(response.StatusCode)
            {
                Content = new StringContent(response.Body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class RecordingSettingsStore(AppSettings current) : ISettingsStore
    {
        private AppSettings _current = current;

        public string SettingsPath { get; } = Path.Combine(
            Path.GetTempPath(),
            "GO-tests-unused-settings.json");

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

    private sealed class RecordingSecretStore(string? apiKey) : IAiSecretStore
    {
        public int GetApiKeyCallCount { get; private set; }
        public int SetApiKeyCallCount { get; private set; }
        public int DeleteApiKeyCallCount { get; private set; }

        public Task<string?> GetApiKeyAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetApiKeyCallCount++;
            return Task.FromResult(apiKey);
        }

        public Task SetApiKeyAsync(string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetApiKeyCallCount++;
            return Task.CompletedTask;
        }

        public Task DeleteApiKeyAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteApiKeyCallCount++;
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
            Func<TState, Exception?, string> formatter)
        {
            _ = logLevel;
            _ = eventId;
            _ = state;
            _ = formatter;
            LastException = exception;
        }
    }
}
