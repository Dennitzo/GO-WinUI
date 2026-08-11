using System.Reflection;
using GoWinUI.BricsCad.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GoWinUI.App.Services;

/// <summary>
/// Owns the optional BricsCAD transport lifecycle. No chat or assistant service depends on it.
/// </summary>
internal sealed class BricsCadBridgeLifecycle(
    IBricsCadBridgeHost bridge,
    ILogger<BricsCadBridgeLifecycle> logger) : IHostedService
{
    private bool _subscribed;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Subscribe();
        try
        {
            await bridge.StartAsync(GetBuildId(), cancellationToken);
            AppLog.BricsCadBridgeStarted(logger, bridge.Rendezvous?.Port ?? 0);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AppLog.BricsCadBridgeStartFailed(logger, exception);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        Unsubscribe();
        await bridge.DisposeAsync();
    }

    private void Subscribe()
    {
        if (_subscribed)
        {
            return;
        }

        bridge.ConnectionChanged += OnConnectionChanged;
        bridge.CapabilitiesChanged += OnCapabilitiesChanged;
        bridge.EventReceived += OnEventReceived;
        bridge.Diagnostic += OnDiagnostic;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
        {
            return;
        }

        bridge.ConnectionChanged -= OnConnectionChanged;
        bridge.CapabilitiesChanged -= OnCapabilitiesChanged;
        bridge.EventReceived -= OnEventReceived;
        bridge.Diagnostic -= OnDiagnostic;
        _subscribed = false;
    }

    private void OnConnectionChanged(object? sender, BridgeConnectionChangedEventArgs args)
    {
        if (args.Connected && args.Hello is { } hello)
        {
            AppLog.BricsCadPluginConnected(logger, hello.PluginVersion, hello.PluginBuildId);
            return;
        }

        AppLog.BricsCadPluginDisconnected(logger, args.Reason ?? "Verbindung beendet");
    }

    private void OnCapabilitiesChanged(object? sender, BridgeCapabilitiesChangedEventArgs args)
    {
        AppLog.BricsCadCapabilitiesReceived(logger, args.Capabilities.Count);
    }

    private void OnEventReceived(object? sender, BridgeEventReceivedEventArgs args)
    {
        AppLog.BricsCadEventReceived(logger, args.Name);
    }

    private void OnDiagnostic(object? sender, BridgeDiagnosticEventArgs args)
    {
        AppLog.BricsCadBridgeDiagnostic(logger, args.Exception, args.Message);
    }

    private static string GetBuildId()
    {
        Assembly assembly = typeof(App).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "development";
    }
}
