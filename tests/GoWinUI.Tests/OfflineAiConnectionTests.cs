using GoWinUI.App.Services;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Sockets;

namespace GoWinUI.Tests;

public sealed class OfflineAiConnectionTests
{
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
        Assert.Equal(6, restored.Version);
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
}
