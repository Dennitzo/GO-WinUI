using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;

namespace GoWinUI.App.Services;

public sealed class AssistantWebBridge : IDisposable
{
    private static readonly Action<ILogger, Exception?> LogLanHostUnavailable = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(9410, "AssistantLanHostUnavailable"),
        "Die LAN-Browseroberfläche konnte nicht gestartet werden; die Desktop-WebView bleibt verfügbar.");
    public const int ProtocolVersion = 1;
    public const string VirtualHost = "assistant.local";
    private const int MaximumIncomingMessageLength = 100_663_296;
    private const int MaximumOutgoingMessageLength = 16_777_216;
    private static readonly HashSet<string> AllowedIncomingTypes = new(StringComparer.Ordinal)
    {
        "app.ready", "conversation.refresh", "chat.send", "chat.steer", "chat.cancel", "session.create", "session.open",
        "reasoning.get", "reasoning.set",
        "session.rename", "session.delete", "session.clear", "session.draft", "session.groupCollapse", "session.projectCreate", "session.workspaceCreate", "mode.switch", "action.invoke", "document.pick", "document.paste", "document.upload",
        "document.remove", "attachment.remove", "workflow.list", "workflow.insert", "workflow.create",
        "workflow.update", "workflow.delete",
        "workflow.createFromMessage", "message.exportPdf", "message.copy",
        "memory.list", "memory.create", "memory.update", "memory.pin", "memory.confirm", "memory.delete", "memory.autoCapture",
        "research.list", "research.open", "research.export",
        "artifact.save", "artifact.preview", "artifact.open", "screen.capture", "screenClip.start", "screenClip.stop", "screenClip.cancel",
        "audioCapture.start", "audioCapture.stop", "audioCapture.cancel",
        "microphone.start", "microphone.audio", "microphone.speak", "microphone.stopSpeech", "microphone.toggleSpeechPause", "microphone.stop", "microphone.cancel",
        "liveCaption.start", "liveCaption.stop", "ui.sessionPane", "external.open",
    };
    private static readonly HashSet<string> AllowedOutgoingTypes = new(StringComparer.Ordinal)
    {
        "state.snapshot", "chat.queued", "queue.changed", "chat.started", "chat.delta", "chat.completed",
        "chat.cancelled", "chat.failed", "session.changed", "session.grouped", "workflow.snapshot",
        "workflow.changed", "workflow.draft", "document.changed", "document.import.started", "document.import.progress", "document.import.completed", "status.changed", "speech.status", "speech.progress", "theme.changed",
        "draft.saved", "caption.changed", "screenClip.changed", "audioCapture.changed", "capture.required", "capture.cancelled",
        "microphone.changed", "microphone.transcript", "artifact.previewReady", "host.error",
        "conversation.snapshot", "conversation.messageCommitted", "coding.changes",
        "reasoning.snapshot",
        "chat.steer.accepted",
        "action.completed", "memory.snapshot", "memory.changed", "research.snapshot", "research.exported",
    };
    private static readonly HashSet<string> ClientScopedOutgoingTypes = new(StringComparer.Ordinal)
    {
        "state.snapshot", "conversation.snapshot",
        "session.changed", "session.grouped", "workflow.snapshot", "workflow.draft",
        "document.changed", "document.import.started", "document.import.progress", "document.import.completed",
        "draft.saved", "speech.status", "speech.progress", "caption.changed", "screenClip.changed",
        "audioCapture.changed", "capture.required", "capture.cancelled", "microphone.changed",
        "microphone.transcript", "artifact.previewReady", "host.error",
        "reasoning.snapshot", "chat.steer.accepted", "action.completed", "memory.snapshot", "research.snapshot", "research.exported",
    };
    private static readonly HashSet<string> ReadableBlockKinds = new(StringComparer.Ordinal)
    {
        "heading", "paragraph", "listItem", "tableRow", "quote", "math", "code",
    };
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly WebView2 _webView;
    private readonly DispatcherQueue _dispatcher;
    private readonly ILogger<AssistantWebBridge> _logger;
    private CoreWebView2Environment? _environment;
    private CoreWebView2ContextMenuItem? _readFromHereMenuItem;
    private CoreWebView2ContextMenuItem? _readFromHereSeparator;
    private ReadFromContextTarget? _activeReadFromContextTarget;
    private bool _initialized;
    private bool _disposed;
    private AssistantLanWebHost? _lanHost;
    private readonly ConcurrentDictionary<string, string> _requestClients = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _requestClientOrder = new();

    public AssistantWebBridge(WebView2 webView, ILogger<AssistantWebBridge> logger)
    {
        _webView = webView;
        _dispatcher = webView.DispatcherQueue;
        _logger = logger;
    }

    public event EventHandler<WebBridgeMessageEventArgs>? MessageReceived;

    public event EventHandler<ReadFromContextRequestedEventArgs>? ReadFromContextRequested;

    public Func<ReadFromContextTarget, CancellationToken, Task<bool>>? ReadFromContextValidator { get; set; }

    internal Func<Guid, CancellationToken, Task<LanArtifactResource?>>? LanArtifactResolver { get; set; }

    internal Func<Guid, string, CancellationToken, Task<string?>>? LanCodingPreviewResolver { get; set; }

    internal static bool IsIncomingTypeAllowed(string type) => AllowedIncomingTypes.Contains(type);

    internal static bool IsOutgoingTypeAllowed(string type) => AllowedOutgoingTypes.Contains(type);

    internal static bool IsClientScopedOutgoingType(string type) => ClientScopedOutgoingTypes.Contains(type);

    internal static object BuildProtocolContract() => new
    {
        version = ProtocolVersion,
        clientActions = AllowedIncomingTypes.Order(StringComparer.Ordinal).ToArray(),
        hostEvents = AllowedOutgoingTypes.Order(StringComparer.Ordinal).ToArray(),
    };

    public async Task InitializeAsync(string webRoot, string userDataFolder, string? previewRoot = null)
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
            var fallbackFolder = Path.Combine(Path.GetTempPath(), "LocalAssistant", "WebView2", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fallbackFolder);
            AppLog.WebViewProfileFallback(_logger, exception);
            environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, fallbackFolder, null);
        }
        _environment = environment;
        await _webView.EnsureCoreWebView2Async(environment);
        var core = _webView.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping(
            VirtualHost,
            webRoot,
            CoreWebView2HostResourceAccessKind.DenyCors);
        if (!string.IsNullOrWhiteSpace(previewRoot))
        {
            Directory.CreateDirectory(previewRoot);
            core.SetVirtualHostNameToFolderMapping(
                AssistantArtifactPreviewService.VirtualHost,
                previewRoot,
                CoreWebView2HostResourceAccessKind.Allow);
        }
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.AreDevToolsEnabled = IsDebugBuild;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsWebMessageEnabled = true;
        core.WebMessageReceived += OnWebMessageReceived;
        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += OnNewWindowRequested;
        core.PermissionRequested += OnPermissionRequested;
        core.ProcessFailed += OnProcessFailed;
        core.ContextMenuRequested += OnContextMenuRequested;
        _initialized = true;
        var dataDirectory = Directory.GetParent(userDataFolder)?.FullName ?? userDataFolder;
        _lanHost = new AssistantLanWebHost(dataDirectory, _logger);
        _lanHost.MessageReceived += OnLanMessageReceived;
        _lanHost.ArtifactResolver = LanArtifactResolver;
        _lanHost.CodingPreviewResolver = LanCodingPreviewResolver;
        try
        {
            await _lanHost.StartAsync(webRoot, previewRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var lanHost = _lanHost;
            _lanHost = null;
            lanHost.MessageReceived -= OnLanMessageReceived;
            await lanHost.DisposeAsync();
            LogLanHostUnavailable(_logger, exception);
        }
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

        var effectiveRequestId = string.IsNullOrWhiteSpace(requestId) ? Guid.NewGuid().ToString("D") : requestId;
        var envelope = new
        {
            version = ProtocolVersion,
            type,
            requestId = effectiveRequestId,
            payload,
        };
        var json = JsonSerializer.Serialize(envelope, SerializerOptions);
        if (json.Length > MaximumOutgoingMessageLength)
        {
            throw new InvalidOperationException("Die ausgehende WebView-Nachricht überschreitet das Größenlimit.");
        }

        var clientScoped = false;
        string? targetClientId = null;
        if (IsClientScopedOutgoingType(type)
            && _requestClients.TryGetValue(effectiveRequestId, out var resolvedClientId))
        {
            clientScoped = true;
            targetClientId = resolvedClientId;
        }
        if (clientScoped && targetClientId != "desktop")
        {
            return _lanHost?.SendToClientAsync(targetClientId!, json) ?? Task.CompletedTask;
        }

        var broadcastToLan = !clientScoped;
        if (_dispatcher.HasThreadAccess)
        {
            PostOnUiThread(json);
            return broadcastToLan ? BroadcastToLanAsync(json) : Task.CompletedTask;
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

        return broadcastToLan ? CompleteAndBroadcastAsync(completion.Task, json) : completion.Task;
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
            core.PermissionRequested -= OnPermissionRequested;
            core.ProcessFailed -= OnProcessFailed;
            core.ContextMenuRequested -= OnContextMenuRequested;
        }
        if (_readFromHereMenuItem is not null)
        {
            _readFromHereMenuItem.CustomItemSelected -= OnReadFromContextSelected;
        }
        _activeReadFromContextTarget = null;
        ReadFromContextValidator = null;
        if (_lanHost is not null)
        {
            var lanHost = _lanHost;
            lanHost.MessageReceived -= OnLanMessageReceived;
            _ = DisposeLanHostAsync(lanHost);
            _lanHost = null;
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

    private async Task CompleteAndBroadcastAsync(Task nativePost, string json)
    {
        await nativePost.ConfigureAwait(false);
        await BroadcastToLanAsync(json).ConfigureAwait(false);
    }

    private Task BroadcastToLanAsync(string json) =>
        _lanHost?.BroadcastAsync(json) ?? Task.CompletedTask;

    private static async Task DisposeLanHostAsync(AssistantLanWebHost host) =>
        await host.DisposeAsync().ConfigureAwait(false);

    private void OnLanMessageReceived(object? sender, WebBridgeMessageEventArgs args)
    {
        if (_disposed) return;
        if (!string.IsNullOrWhiteSpace(args.Envelope.ClientId))
            RegisterRequestClient(args.Envelope.RequestId, args.Envelope.ClientId);
        _dispatcher.TryEnqueue(() => MessageReceived?.Invoke(this, args));
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

            if (!TryParseIncomingEnvelope(json, out var envelope))
            {
                throw new JsonException("Ungültiger Bridge-Vertrag.");
            }

            var desktopEnvelope = envelope! with { ClientId = "desktop" };
            RegisterRequestClient(desktopEnvelope.RequestId, desktopEnvelope.ClientId);
            MessageReceived?.Invoke(this, new WebBridgeMessageEventArgs(desktopEnvelope));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            AppLog.WebBridgeMessageRejected(_logger, exception.GetType().Name);
            await PostErrorAsync("Ungültige Nachricht aus der Chat-Oberfläche.");
        }
    }

    internal static bool TryParseIncomingEnvelope(string json, out WebBridgeEnvelope? envelope)
    {
        envelope = null;
        if (json.Length > MaximumIncomingMessageLength) return false;
        try
        {
            envelope = JsonSerializer.Deserialize<WebBridgeEnvelope>(json, SerializerOptions);
            return envelope is not null
                && envelope.Version == ProtocolVersion
                && IsIncomingTypeAllowed(envelope.Type)
                && !string.IsNullOrWhiteSpace(envelope.RequestId)
                && envelope.RequestId.Length <= 128
                && IsValidClientId(envelope.ClientId)
                && envelope.Payload.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void RegisterRequestClient(string requestId, string clientId)
    {
        if (!_requestClients.TryAdd(requestId, clientId))
        {
            _requestClients[requestId] = clientId;
            return;
        }
        _requestClientOrder.Enqueue(requestId);
        while (_requestClients.Count > 4096 && _requestClientOrder.TryDequeue(out var expired))
            _requestClients.TryRemove(expired, out _);
    }

    private static bool IsValidClientId(string? clientId) =>
        clientId is null
        || clientId is { Length: > 0 and <= 128 }
        && clientId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

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

    private static void OnPermissionRequested(
        CoreWebView2 sender,
        CoreWebView2PermissionRequestedEventArgs args)
    {
        if (args.PermissionKind == CoreWebView2PermissionKind.Microphone
            && IsTrustedOrigin(args.Uri))
        {
            // Default deliberately keeps WebView2's browser-style permission prompt.
            // A user's decision is stored in the dedicated GO WebView2 profile.
            args.SavesInProfile = true;
            args.State = CoreWebView2PermissionState.Default;
            return;
        }

        args.State = CoreWebView2PermissionState.Deny;
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

    private async void OnContextMenuRequested(
        CoreWebView2 sender,
        CoreWebView2ContextMenuRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        args.Handled = true;
        try
        {
            var json = await sender.ExecuteScriptAsync(
                "globalThis.goGetReadFromContextTarget?.() ?? null");
            var target = JsonSerializer.Deserialize<ReadFromContextTarget?>(json, SerializerOptions);
            if (!IsValidReadFromContextTarget(target))
            {
                _activeReadFromContextTarget = null;
                return;
            }
            if (ReadFromContextValidator is not { } validator
                || !await validator(target!, CancellationToken.None))
            {
                _activeReadFromContextTarget = null;
                return;
            }

            EnsureReadFromContextMenuItems();
            _activeReadFromContextTarget = target;
            args.MenuItems.Insert(0, _readFromHereSeparator!);
            args.MenuItems.Insert(0, _readFromHereMenuItem!);
            args.Handled = false;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _activeReadFromContextTarget = null;
            AppLog.ReadFromContextMenuFailed(_logger, exception);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void EnsureReadFromContextMenuItems()
    {
        if (_readFromHereMenuItem is not null || _environment is null)
        {
            return;
        }

        _readFromHereMenuItem = _environment.CreateContextMenuItem(
            "Ab hier vorlesen",
            null,
            CoreWebView2ContextMenuItemKind.Command);
        _readFromHereMenuItem.CustomItemSelected += OnReadFromContextSelected;
        _readFromHereSeparator = _environment.CreateContextMenuItem(
            string.Empty,
            null,
            CoreWebView2ContextMenuItemKind.Separator);
    }

    private void OnReadFromContextSelected(object? sender, object args)
    {
        if (_activeReadFromContextTarget is not { } target)
        {
            return;
        }

        _activeReadFromContextTarget = null;
        ReadFromContextRequested?.Invoke(this, new(target));
    }

    internal static bool IsValidReadFromContextTarget(ReadFromContextTarget? target) =>
        target is not null
        && target.SessionId != Guid.Empty
        && target.MessageId != Guid.Empty
        && target.MessageUpdatedAt != default
        && ReadableBlockKinds.Contains(target.Kind)
        && target.BlockIndex >= 0;

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
