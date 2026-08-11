using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;

namespace GoWinUI.App.Services;

public sealed class AssistantWebBridge : IDisposable
{
    public const int ProtocolVersion = 1;
    public const string VirtualHost = "go.local";
    private const int MaximumIncomingMessageLength = 1_048_576;
    private const int MaximumOutgoingMessageLength = 16_777_216;
    private static readonly HashSet<string> AllowedIncomingTypes = new(StringComparer.Ordinal)
    {
        "app.ready", "chat.send", "chat.cancel", "session.create", "session.open",
        "session.rename", "session.delete", "session.clear", "session.draft", "document.pick",
        "document.remove", "workflow.list", "workflow.insert", "workflow.create",
        "workflow.update", "workflow.delete",
        "workflow.createFromMessage", "chat.exportPdf", "message.exportPdf", "message.copy",
        "ui.sessionPane", "external.open",
    };
    private static readonly HashSet<string> AllowedOutgoingTypes = new(StringComparer.Ordinal)
    {
        "state.snapshot", "chat.started", "chat.delta", "chat.completed",
        "chat.cancelled", "chat.failed", "session.changed", "workflow.snapshot",
        "workflow.changed", "workflow.draft", "document.changed", "status.changed", "theme.changed",
        "draft.saved", "host.error",
    };
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly WebView2 _webView;
    private readonly DispatcherQueue _dispatcher;
    private readonly ILogger<AssistantWebBridge> _logger;
    private bool _initialized;
    private bool _disposed;

    public AssistantWebBridge(WebView2 webView, ILogger<AssistantWebBridge> logger)
    {
        _webView = webView;
        _dispatcher = webView.DispatcherQueue;
        _logger = logger;
    }

    public event EventHandler<WebBridgeMessageEventArgs>? MessageReceived;

    public async Task InitializeAsync(string webRoot, string userDataFolder)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
        {
            return;
        }

        if (!Directory.Exists(webRoot))
        {
            throw new DirectoryNotFoundException($"Webassets wurden nicht gefunden: {webRoot}");
        }

        Directory.CreateDirectory(userDataFolder);
        CoreWebView2Environment environment;
        try
        {
            environment = await CoreWebView2Environment.CreateWithOptionsAsync(
                browserExecutableFolder: null,
                userDataFolder,
                options: null);
        }
        catch (Exception exception) when (exception is COMException or UnauthorizedAccessException or IOException)
        {
            // A stale/locked profile must not prevent the rest of the desktop app from opening.
            // Keep the configured directory as the first choice so the profile remains persistent.
            var fallbackFolder = Path.Combine(Path.GetTempPath(), "GO", "WebView2", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fallbackFolder);
            AppLog.WebViewProfileFallback(_logger, exception);
            environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, fallbackFolder, null);
        }
        await _webView.EnsureCoreWebView2Async(environment);
        var core = _webView.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping(
            VirtualHost,
            webRoot,
            CoreWebView2HostResourceAccessKind.DenyCors);
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = IsDebugBuild;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsWebMessageEnabled = true;
        core.WebMessageReceived += OnWebMessageReceived;
        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += OnNewWindowRequested;
        core.ProcessFailed += OnProcessFailed;
        _initialized = true;
    }

    public void NavigateToApp()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _webView.CoreWebView2 is not { } core)
        {
            throw new InvalidOperationException("Die WebView2-Umgebung ist noch nicht initialisiert.");
        }

        core.Navigate($"https://{VirtualHost}/index.html");
    }

    public Task PostAsync(string type, object payload, string? requestId = null)
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        if (!AllowedOutgoingTypes.Contains(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Unbekannter ausgehender Bridge-Typ.");
        }

        var envelope = new
        {
            version = ProtocolVersion,
            type,
            requestId = string.IsNullOrWhiteSpace(requestId) ? Guid.NewGuid().ToString("D") : requestId,
            payload,
        };
        var json = JsonSerializer.Serialize(envelope, SerializerOptions);
        if (json.Length > MaximumOutgoingMessageLength)
        {
            throw new InvalidOperationException("Die ausgehende WebView-Nachricht überschreitet das Größenlimit.");
        }

        if (_dispatcher.HasThreadAccess)
        {
            PostOnUiThread(json);
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(() =>
            {
                try
                {
                    PostOnUiThread(json);
                    completion.SetResult();
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
            }))
        {
            if (_disposed)
            {
                completion.SetResult();
            }
            else
            {
                completion.SetException(new InvalidOperationException("Die WebView ist nicht mehr verfügbar."));
            }
        }

        return completion.Task;
    }

    public async Task PostErrorAsync(string message, string? requestId = null)
    {
        await PostAsync("host.error", new { message }, requestId);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_initialized && _webView.CoreWebView2 is { } core)
        {
            core.WebMessageReceived -= OnWebMessageReceived;
            core.NavigationStarting -= OnNavigationStarting;
            core.NewWindowRequested -= OnNewWindowRequested;
            core.ProcessFailed -= OnProcessFailed;
        }
    }

    private void PostOnUiThread(string json)
    {
        if (_disposed)
        {
            return;
        }

        _webView.CoreWebView2?.PostWebMessageAsJson(json);
    }

    private async void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!IsTrustedOrigin(args.Source))
        {
            AppLog.WebBridgeOriginRejected(_logger, GetOriginLabel(args.Source));
            return;
        }

        string json;
        try
        {
            json = args.WebMessageAsJson;
            if (json.Length > MaximumIncomingMessageLength)
            {
                throw new JsonException("Die Bridge-Nachricht überschreitet das Größenlimit.");
            }

            var envelope = JsonSerializer.Deserialize<WebBridgeEnvelope>(json, SerializerOptions)
                ?? throw new JsonException("Leere Bridge-Nachricht.");
            if (envelope.Version != ProtocolVersion
                || !AllowedIncomingTypes.Contains(envelope.Type)
                || string.IsNullOrWhiteSpace(envelope.RequestId)
                || envelope.RequestId.Length > 128
                || envelope.Payload.ValueKind is not JsonValueKind.Object)
            {
                throw new JsonException("Ungültiger Bridge-Vertrag.");
            }

            MessageReceived?.Invoke(this, new WebBridgeMessageEventArgs(envelope));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            AppLog.WebBridgeMessageRejected(_logger, exception.GetType().Name);
            await PostErrorAsync("Ungültige Nachricht aus der Chat-Oberfläche.");
        }
    }

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!IsTrustedOrigin(args.Uri))
        {
            args.Cancel = true;
            AppLog.WebBridgeOriginRejected(_logger, GetOriginLabel(args.Uri));
        }
    }

    private static void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
    }

    private async void OnProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args)
    {
        if (args.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited
            or CoreWebView2ProcessFailedKind.RenderProcessExited
            or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
        {
            try
            {
                await Task.Yield();
                sender.Reload();
            }
            catch (InvalidOperationException)
            {
                // The page-level error UI remains visible if WebView2 cannot recover.
            }
        }
    }

    private static bool IsTrustedOrigin(string source)
    {
        return Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.Equals(uri.Host, VirtualHost, StringComparison.OrdinalIgnoreCase)
            && uri.IsDefaultPort;
    }

    private static string GetOriginLabel(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority)
            : "invalid-origin";
    }

    private static bool IsDebugBuild
    {
        get
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }
    }
}
