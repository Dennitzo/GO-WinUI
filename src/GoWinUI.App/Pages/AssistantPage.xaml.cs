using GoWinUI.App.Services;
using GoWinUI.Core.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace GoWinUI.App.Pages;

public sealed partial class AssistantPage : Page, IDisposable
{
    private readonly AssistantCoordinator _coordinator;
    private readonly SettingsCoordinator _settings;
    private readonly ILogger<AssistantPage> _logger;
    private readonly SemaphoreSlim _exportGate = new(1, 1);
    private readonly UISettings _uiSettings = new();
    private readonly AccessibilitySettings _accessibilitySettings = new();
    private WebView2? _assistantWebView;
    private CancellationTokenSource? _lifetime;
    private AssistantWebBridge? _bridge;
    private bool _initialized;
    private bool _disposed;
    private bool _colorEventsSubscribed;
    private bool _contrastEventsSubscribed;
    private bool _themeEventsSubscribed;

    public AssistantPage()
    {
        InitializeComponent();
        _coordinator = App.Current.GetService<AssistantCoordinator>();
        _settings = App.Current.GetService<SettingsCoordinator>();
        _logger = App.Current.GetService<ILogger<AssistantPage>>();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _lifetime = new CancellationTokenSource();
        try
        {
            if (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture
                != System.Runtime.InteropServices.Architecture.X64)
            {
                throw new PlatformNotSupportedException(
                    "GO v1 unterstützt WebView2 ausschließlich auf Windows x64.");
            }

            _uiSettings.ColorValuesChanged += OnSystemColorsChanged;
            _colorEventsSubscribed = true;
            _accessibilitySettings.HighContrastChanged += OnHighContrastChanged;
            _contrastEventsSubscribed = true;
            App.Current.ThemeChanged += OnAppThemeChanged;
            _themeEventsSubscribed = true;
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
            var webView = new WebView2
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            _assistantWebView = webView;
            WebViewHost.Children.Add(webView);
            _bridge = new AssistantWebBridge(
                webView,
                App.Current.GetService<ILogger<AssistantWebBridge>>());
            _bridge.MessageReceived += OnBridgeMessageReceived;
            var webRoot = ApplicationAssets.ResolvePath("Assets", "Web");
            var userDataFolder = Path.Combine(App.Current.DataDirectory, "WebView2");
            await _bridge.InitializeAsync(webRoot, userDataFolder);
            webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
        }
        catch (Exception exception)
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            ErrorBar.Message = "Prüfe, ob die Microsoft Edge WebView2 Evergreen Runtime installiert ist.";
            ErrorBar.IsOpen = true;
            AppLog.WebViewInitializationFailed(_logger, exception);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Dispose();
    }

    public async Task FlushDraftAsync()
    {
        var webView = _assistantWebView;
        if (_disposed || webView?.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            var result = await webView.ExecuteScriptAsync("globalThis.goCaptureDraft?.() ?? null");
            using var document = JsonDocument.Parse(result);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("sessionId", out var sessionElement)
                || !Guid.TryParse(sessionElement.GetString(), out var sessionId)
                || !document.RootElement.TryGetProperty("draft", out var draftElement))
            {
                return;
            }

            await _coordinator.SaveDraftAsync(sessionId, draftElement.GetString() ?? string.Empty, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLog.AssistantDraftFlushFailed(_logger, exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime?.Cancel();
        _lifetime?.Dispose();
        _lifetime = null;
        if (_colorEventsSubscribed)
        {
            _uiSettings.ColorValuesChanged -= OnSystemColorsChanged;
            _colorEventsSubscribed = false;
        }
        if (_contrastEventsSubscribed)
        {
            _accessibilitySettings.HighContrastChanged -= OnHighContrastChanged;
            _contrastEventsSubscribed = false;
        }
        if (_themeEventsSubscribed)
        {
            App.Current.ThemeChanged -= OnAppThemeChanged;
            _themeEventsSubscribed = false;
        }
        if (_assistantWebView?.CoreWebView2 is { } core)
        {
            core.NavigationCompleted -= OnNavigationCompleted;
        }

        if (_bridge is not null)
        {
            _bridge.MessageReceived -= OnBridgeMessageReceived;
            _bridge.Dispose();
            _bridge = null;
        }

        if (_assistantWebView is { } webView)
        {
            WebViewHost.Children.Remove(webView);
            _assistantWebView = null;
        }

        _initialized = false;
        _exportGate.Dispose();
    }

    private async void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        LoadingOverlay.Visibility = Visibility.Collapsed;
        if (!args.IsSuccess)
        {
            ErrorBar.Message = $"WebView2-Navigationsfehler: {args.WebErrorStatus}";
            ErrorBar.IsOpen = true;
            return;
        }

        await SendThemeAsync();
    }

    private async void OnBridgeMessageReceived(object? sender, WebBridgeMessageEventArgs args)
    {
        var bridge = _bridge;
        if (bridge is null)
        {
            return;
        }

        try
        {
            switch (args.Envelope.Type)
            {
                case "document.pick":
                    await PickDocumentAsync(args.Envelope, bridge);
                    break;
                case "chat.exportPdf":
                    await ExportPdfAsync(args.Envelope.RequestId, bridge);
                    break;
                case "message.copy":
                    CopyToClipboard(args.Envelope.Payload);
                    break;
                case "external.open":
                    await OpenExternalLinkAsync(args.Envelope.Payload);
                    break;
                default:
                    await _coordinator.HandleAsync(
                        args.Envelope,
                        (type, payload, requestId) => bridge.PostAsync(type, payload, requestId),
                        _lifetime?.Token ?? CancellationToken.None);
                    break;
            }
        }
        catch (OperationCanceledException) when (_lifetime?.IsCancellationRequested == true)
        {
            // Navigation away from the assistant intentionally cancels page work.
        }
        catch (ObjectDisposedException) when (_disposed || _lifetime?.IsCancellationRequested == true)
        {
            // Persisted chat state remains authoritative after the page has been released.
        }
        catch (Exception exception)
        {
            AppLog.AssistantRequestFailed(_logger, exception, args.Envelope.Type);
            await bridge.PostErrorAsync(exception.Message, args.Envelope.RequestId);
        }
    }

    private async Task PickDocumentAsync(WebBridgeEnvelope envelope, AssistantWebBridge bridge)
    {
        if (!TryReadGuid(envelope.Payload, "sessionId", out var sessionId))
        {
            throw new InvalidOperationException("Öffne oder erstelle zuerst eine Sitzung.");
        }

        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List,
        };
        foreach (var extension in _coordinator.SupportedDocumentExtensions.Order(StringComparer.OrdinalIgnoreCase))
        {
            picker.FileTypeFilter.Add(extension.StartsWith('.') ? extension : $".{extension}");
        }
        picker.FileTypeFilter.Add(".doc");

        InitializePicker(picker);
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        await using var stream = await file.OpenStreamForReadAsync();
        await _coordinator.ImportDocumentAsync(
            sessionId,
            file.Name,
            stream,
            _lifetime?.Token ?? CancellationToken.None);
        await bridge.PostAsync(
            "document.changed",
            await _coordinator.BuildSnapshotAsync(_lifetime?.Token ?? CancellationToken.None),
            envelope.RequestId);
    }

    private async Task ExportPdfAsync(string requestId, AssistantWebBridge bridge)
    {
        if (!await _exportGate.WaitAsync(0))
        {
            throw new InvalidOperationException("Ein PDF-Export läuft bereits.");
        }

        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"GO-Chat-{DateTime.Now:yyyy-MM-dd-HHmm}",
                DefaultFileExtension = ".pdf",
            };
            picker.FileTypeChoices.Add("PDF-Dokument", new List<string> { ".pdf" });
            InitializePicker(picker);
            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            var core = _assistantWebView?.CoreWebView2
                ?? throw new InvalidOperationException("Die Chat-Oberfläche ist nicht bereit.");
            var printSettings = core.Environment.CreatePrintSettings();
            printSettings.ShouldPrintBackgrounds = true;
            printSettings.ShouldPrintHeaderAndFooter = false;
            var success = await core.PrintToPdfAsync(file.Path, printSettings);
            if (!success)
            {
                throw new InvalidOperationException("WebView2 konnte das PDF nicht erstellen.");
            }

            await bridge.PostAsync("status.changed", new { exportCompleted = true }, requestId);
        }
        finally
        {
            _exportGate.Release();
        }
    }

    private static void CopyToClipboard(JsonElement payload)
    {
        if (!payload.TryGetProperty("text", out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Der zu kopierende Text fehlt.");
        }

        var text = property.GetString() ?? string.Empty;
        if (text.Length > 1_000_000)
        {
            throw new InvalidOperationException("Der Text ist zu groß für diese Aktion.");
        }

        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    private static async Task OpenExternalLinkAsync(JsonElement payload)
    {
        if (!payload.TryGetProperty("url", out var property)
            || property.ValueKind != JsonValueKind.String
            || !Uri.TryCreate(property.GetString(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("Der externe Link ist ungültig.");
        }

        _ = await Launcher.LaunchUriAsync(uri);
    }

    private async Task SendThemeAsync()
    {
        var bridge = _bridge;
        if (bridge is null)
        {
            return;
        }

        var accent = _uiSettings.GetColorValue(UIColorType.Accent);
        var accentHex = $"#{accent.R:X2}{accent.G:X2}{accent.B:X2}";
        await bridge.PostAsync("theme.changed", new
        {
            theme = _settings.Current.Theme.ToString().ToLowerInvariant(),
            accent = accentHex,
            highContrast = _accessibilitySettings.HighContrast,
        });
    }

    private void OnSystemColorsChanged(UISettings sender, object args) => QueueThemeRefresh();

    private void OnHighContrastChanged(AccessibilitySettings sender, object args) => QueueThemeRefresh();

    private void OnAppThemeChanged(object? sender, EventArgs args) => QueueThemeRefresh();

    private void QueueThemeRefresh()
    {
        _ = DispatcherQueue.TryEnqueue(() => _ = SendThemeIgnoringDisposalAsync());
    }

    private async Task SendThemeIgnoringDisposalAsync()
    {
        try
        {
            await SendThemeAsync();
        }
        catch (InvalidOperationException) when (_disposed)
        {
        }
        catch (ObjectDisposedException) when (_disposed)
        {
        }
    }

    private static void InitializePicker(object picker)
    {
        var window = App.Current.MainWindow
            ?? throw new InvalidOperationException("Das Hauptfenster ist nicht verfügbar.");
        var handle = WindowNative.GetWindowHandle(window);
        InitializeWithWindow.Initialize(picker, handle);
    }

    private static bool TryReadGuid(JsonElement payload, string name, out Guid value)
    {
        value = default;
        return payload.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            && Guid.TryParse(property.GetString(), out value);
    }
}
