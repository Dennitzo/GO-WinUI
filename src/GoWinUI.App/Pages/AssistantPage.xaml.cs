using GoWinUI.App.Services;
using GoWinUI.App.ViewModels;
using GoAi.Contracts;
using GoWinUI.Core.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System.Text.Json;
using System.Text;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace GoWinUI.App.Pages;

internal enum VoicePlaybackControl
{
    None,
    Pause,
    Resume,
    Cancel,
}

public sealed partial class AssistantPage : Page, IDisposable
{
    internal const double PdfA4WidthInches = 210d / 25.4d;
    internal const double PdfA4HeightInches = 297d / 25.4d;
    internal const double PdfBookMarginTopInches = 20d / 25.4d;
    internal const double PdfBookMarginRightInches = 20d / 25.4d;
    internal const double PdfBookMarginBottomInches = 24d / 25.4d;
    internal const double PdfBookMarginLeftInches = 24d / 25.4d;

    private readonly AssistantCoordinator _coordinator;
    private readonly GoAiAssistantService _goAi;
    private readonly SettingsCoordinator _settings;
    private readonly ShellViewModel _shell;
    private readonly IChatArtifactRepository _artifacts;
    private readonly IBinaryObjectStore _blobs;
    private readonly SystemAudioCaptionService _liveCaptions;
    private readonly MicrophoneTranscriptionService _microphone;
    private readonly SystemAudioAnalysisCaptureService _audioCapture;
    private readonly DesktopScreenshotService _screenshots;
    private readonly ScreenClipCaptureService _screenClips;
    private readonly AssistantArtifactPreviewService _previews;
    private readonly ILogger<AssistantPage> _logger;
    private readonly SemaphoreSlim _exportGate = new(1, 1);
    private readonly SemaphoreSlim _captionResultGate = new(1, 1);
    private readonly SemaphoreSlim _microphoneBridgeGate = new(1, 1);
    private readonly SemaphoreSlim _audioCaptureBridgeGate = new(1, 1);
    private readonly SemaphoreSlim _chatBridgeGate = new(1, 1);
    private readonly SemaphoreSlim _speechStartGate = new(1, 1);
    private readonly UISettings _uiSettings = new();
    private readonly AccessibilitySettings _accessibilitySettings = new();
    private WebView2? _assistantWebView;
    private CancellationTokenSource? _lifetime;
    private AssistantWebBridge? _bridge;
    private Task? _activeSpeechTask;
    private CancellationTokenSource? _activeSpeechRequestCancellation;
    private bool _initialized;
    private bool _disposed;
    private bool _colorEventsSubscribed;
    private bool _contrastEventsSubscribed;
    private bool _themeEventsSubscribed;
    private DateTimeOffset? _lastPersistedCaptionStartedAt;

    public AssistantPage()
    {
        InitializeComponent();
        _coordinator = App.Current.GetService<AssistantCoordinator>();
        _goAi = App.Current.GetService<GoAiAssistantService>();
        _settings = App.Current.GetService<SettingsCoordinator>();
        _shell = App.Current.GetService<ShellViewModel>();
        _artifacts = App.Current.GetService<IChatArtifactRepository>();
        _blobs = App.Current.GetService<IBinaryObjectStore>();
        _liveCaptions = App.Current.GetService<SystemAudioCaptionService>();
        _microphone = App.Current.GetService<MicrophoneTranscriptionService>();
        _audioCapture = App.Current.GetService<SystemAudioAnalysisCaptureService>();
        _screenshots = App.Current.GetService<DesktopScreenshotService>();
        _screenClips = App.Current.GetService<ScreenClipCaptureService>();
        _previews = App.Current.GetService<AssistantArtifactPreviewService>();
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
        _liveCaptions.Changed += OnLiveCaptionChanged;
        _microphone.Changed += OnMicrophoneChanged;
        _microphone.TurnChanged += OnMicrophoneTurnChanged;
        _audioCapture.Changed += OnAudioCaptureChanged;
        _screenClips.Changed += OnScreenClipChanged;
        var initializationStage = "Plattformprüfung";
        try
        {
            if (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture
                != System.Runtime.InteropServices.Architecture.X64)
            {
                throw new PlatformNotSupportedException(
                    "GO v1 unterstützt WebView2 ausschließlich auf Windows x64.");
            }

            initializationStage = "Systemereignisse";
            SubscribeOptionalSystemThemeEvents();
            App.Current.ThemeChanged += OnAppThemeChanged;
            _themeEventsSubscribed = true;
            initializationStage = "WebView2-Runtimeprüfung";
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
            initializationStage = "WebView2-Steuerelement";
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
            _bridge.ReadFromContextRequested += OnReadFromContextRequested;
            _bridge.ReadFromContextValidator = ValidateReadFromContextTargetAsync;
            initializationStage = "Webassets";
            var webRoot = ApplicationAssets.ResolvePath("Assets", "Web");
            var userDataFolder = Path.Combine(App.Current.DataDirectory, "WebView2");
            initializationStage = "WebView2-Umgebung";
            await _bridge.InitializeAsync(webRoot, userDataFolder, _previews.CacheRoot);
            initializationStage = "Navigation";
            webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            _bridge.NavigateToApp();
        }
        catch (Exception exception)
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            ErrorBar.Message = $"{initializationStage}: {exception.GetType().Name} "
                + $"(0x{exception.HResult:X8}) – {exception.Message}";
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
        _liveCaptions.Changed -= OnLiveCaptionChanged;
        _microphone.Changed -= OnMicrophoneChanged;
        _microphone.TurnChanged -= OnMicrophoneTurnChanged;
        _audioCapture.Changed -= OnAudioCaptureChanged;
        _screenClips.Changed -= OnScreenClipChanged;
        _activeSpeechRequestCancellation?.Cancel();
        _activeSpeechRequestCancellation?.Dispose();
        _activeSpeechRequestCancellation = null;
        ClearClientSpeechRuns();
        _ = StopMicrophoneAfterUnloadAsync();
        _ = _audioCapture.CancelAsync(CancellationToken.None);
        if (_assistantWebView?.CoreWebView2 is { } core)
        {
            core.NavigationCompleted -= OnNavigationCompleted;
        }

        if (_bridge is not null)
        {
            _bridge.MessageReceived -= OnBridgeMessageReceived;
            _bridge.ReadFromContextRequested -= OnReadFromContextRequested;
            _bridge.ReadFromContextValidator = null;
            _bridge.Dispose();
            _bridge = null;
        }

        if (_assistantWebView is { } webView)
        {
            try
            {
                webView.Close();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                AppLog.WebViewCloseFailed(_logger, exception);
            }

            WebViewHost.Children.Remove(webView);
            _assistantWebView = null;
        }

        _initialized = false;
        _microphoneBridgeGate.Dispose();
        _audioCaptureBridgeGate.Dispose();
        _chatBridgeGate.Dispose();
        _speechStartGate.Dispose();
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
        if (_bridge is { } bridge)
        {
            await HandleLiveCaptionSnapshotAsync(_liveCaptions.Current, bridge);
            await bridge.PostAsync("microphone.changed", _microphone.Current);
            await bridge.PostAsync("audioCapture.changed", _audioCapture.Current);
            await bridge.PostAsync("screenClip.changed", _screenClips.Current);
        }
    }

    private async void OnBridgeMessageReceived(object? sender, WebBridgeMessageEventArgs args)
    {
        var bridge = _bridge;
        if (bridge is null)
        {
            return;
        }

        var updatesAiState = false;
        var isSpeechRequest = args.Envelope.Type == "microphone.speak";
        var microphoneMessageLocked = false;
        var audioCaptureMessageLocked = false;
        var chatMessageLocked = false;
        try
        {
            if (args.Envelope.Type == "chat.send")
            {
                isSpeechRequest = await _coordinator.IsSpeechRequestAsync(
                    args.Envelope.Payload,
                    _lifetime?.Token ?? CancellationToken.None);
            }
            updatesAiState = args.Envelope.Type == "chat.send" && !isSpeechRequest;

            if (args.Envelope.Type.StartsWith("microphone.", StringComparison.Ordinal))
            {
                await _microphoneBridgeGate.WaitAsync(_lifetime?.Token ?? CancellationToken.None);
                microphoneMessageLocked = true;
            }

            if (args.Envelope.Type.StartsWith("audioCapture.", StringComparison.Ordinal)
                || (args.Envelope.Type == "chat.send" && !isSpeechRequest))
            {
                await _audioCaptureBridgeGate.WaitAsync(_lifetime?.Token ?? CancellationToken.None);
                audioCaptureMessageLocked = true;
            }

            if (args.Envelope.Type == "chat.send")
            {
                if (ShouldCancelActiveChatBeforePrompt(
                    isSpeechRequest,
                    _chatBridgeGate.CurrentCount == 0,
                    _goAi.IsRunning,
                    _microphone.Current.IsSpeaking || _goAi.IsSpeaking))
                {
                    await _coordinator.CancelCurrentAsync().ConfigureAwait(false);
                }
                if (!isSpeechRequest)
                {
                    await _chatBridgeGate.WaitAsync(_lifetime?.Token ?? CancellationToken.None);
                    chatMessageLocked = true;
                    if (_screenClips.Current.IsRecording)
                    {
                        var promptSessionId = TryReadGuid(args.Envelope.Payload, "sessionId", out var requestedSessionId)
                            ? requestedSessionId
                            : (Guid?)null;
                        await StopScreenClipAsync(args.Envelope, bridge, promptSessionId, suppressSnapshot: true);
                    }

                    var requiredCapture = await _coordinator.GetRequiredMediaCaptureAsync(
                        args.Envelope.Payload,
                        _lifetime?.Token ?? CancellationToken.None);
                    if (requiredCapture is { } captureAction)
                    {
                        await bridge.PostAsync("capture.required", new
                        {
                            action = AssistantCoordinator.MediaActionName(captureAction),
                        }, args.Envelope.RequestId);
                        return;
                    }
                }
            }

            if (updatesAiState)
            {
                await SetShellAiStateAsync(true);
            }

            switch (args.Envelope.Type)
            {
                case "document.pick":
                    await PickDocumentAsync(args.Envelope, bridge);
                    break;
                case "workspace.pick":
                    await PickWorkspaceAsync(args.Envelope, bridge);
                    break;
                case "chat.exportPdf":
                    await ExportPdfAsync(args.Envelope, bridge, selectedMessageOnly: false);
                    break;
                case "message.exportPdf":
                    await ExportPdfAsync(args.Envelope, bridge, selectedMessageOnly: true);
                    break;
                case "message.copy":
                    CopyToClipboard(args.Envelope.Payload);
                    break;
                case "artifact.save":
                    await SaveArtifactAsync(args.Envelope.Payload, bridge);
                    break;
                case "artifact.preview":
                    if (!TryReadGuid(args.Envelope.Payload, "artifactId", out var previewId))
                    {
                        throw new InvalidOperationException("Die Artefakt-ID ist ungültig.");
                    }
                    var preview = await _previews.PrepareAsync(previewId, _lifetime?.Token ?? CancellationToken.None);
                    await bridge.PostAsync("artifact.previewReady", new
                    {
                        artifactId = previewId,
                        url = preview.Url,
                        posterUrl = preview.PosterUrl,
                    }, args.Envelope.RequestId);
                    break;
                case "artifact.open":
                    await OpenArtifactAsync(args.Envelope.Payload);
                    break;
                case "screen.capture":
                    await CaptureScreenshotAsync(args.Envelope, bridge);
                    break;
                case "screenClip.start":
                    await StartScreenClipAsync(args.Envelope, bridge);
                    break;
                case "screenClip.stop":
                    await StopScreenClipAsync(args.Envelope, bridge);
                    break;
                case "screenClip.cancel":
                    await _screenClips.CancelAsync(CancellationToken.None);
                    await bridge.PostAsync("screenClip.changed", _screenClips.Current, args.Envelope.RequestId);
                    await bridge.PostAsync("capture.cancelled", new { action = "videoAnalysis" }, args.Envelope.RequestId);
                    break;
                case "audioCapture.start":
                    if (!TryReadGuid(args.Envelope.Payload, "sessionId", out var audioSessionId))
                    {
                        throw new InvalidOperationException("Die Systemaudio-Aufnahme enthält keine gültige Sitzung.");
                    }
                    await _audioCapture.StartAsync(
                        audioSessionId,
                        _lifetime?.Token ?? CancellationToken.None);
                    await bridge.PostAsync("audioCapture.changed", _audioCapture.Current, args.Envelope.RequestId);
                    break;
                case "audioCapture.stop":
                    await StopAudioCaptureAsync(args.Envelope, bridge);
                    break;
                case "audioCapture.cancel":
                    await _audioCapture.CancelAsync(CancellationToken.None);
                    await bridge.PostAsync("audioCapture.changed", _audioCapture.Current, args.Envelope.RequestId);
                    await bridge.PostAsync("capture.cancelled", new { action = "audioAnalysis" }, args.Envelope.RequestId);
                    break;
                case "microphone.start":
                    await _microphone.StartAsync(
                        ReadOptionalText(args.Envelope.Payload, "deviceLabel", 256),
                        _lifetime?.Token ?? CancellationToken.None);
                    await bridge.PostAsync("microphone.changed", _microphone.Current, args.Envelope.RequestId);
                    break;
                case "microphone.audio":
                    if (!args.Envelope.Payload.TryGetProperty("chunkIndex", out var chunkElement)
                        || !chunkElement.TryGetInt32(out var chunkIndex)
                        || !args.Envelope.Payload.TryGetProperty("isFinal", out var finalElement)
                        || finalElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        throw new InvalidOperationException("Das Mikrofon-Audiofenster ist ungültig.");
                    }
                    await _microphone.SubmitChunkAsync(
                        ReadRequiredText(args.Envelope.Payload, "turnId", 128),
                        ReadOptionalText(args.Envelope.Payload, "sessionId", 64),
                        chunkIndex,
                        ReadRequiredText(args.Envelope.Payload, "pcm", 150_000),
                        finalElement.GetBoolean(),
                        _lifetime?.Token ?? CancellationToken.None);
                    break;
                case "microphone.stop":
                    await _microphone.StopVoiceControlAsync(CancellationToken.None);
                    await bridge.PostAsync("microphone.changed", _microphone.Current, args.Envelope.RequestId);
                    break;
                case "microphone.speak":
                    var speechText = ReadRequiredText(args.Envelope.Payload, "text", 100_000);
                    if (TryReadGuid(args.Envelope.Payload, "sessionId", out var speechSessionId)
                        && TryReadGuid(args.Envelope.Payload, "messageId", out var speechMessageId))
                    {
                        _ = StartSpeechRequestAsync(
                            speechSessionId,
                            speechMessageId,
                            speechText,
                            bridge,
                            args.Envelope.RequestId,
                            null,
                            null);
                    }
                    else
                    {
                        _ = SpeakFeedbackWithoutBlockingBridgeAsync(speechText);
                    }
                    break;
                case "microphone.stopSpeech":
                    await _goAi.CancelSpeechAsync(CancellationToken.None);
                    break;
                case "microphone.toggleSpeechPause":
                    var playback = await _microphone.ToggleSpeechPauseAsync(
                        _lifetime?.Token ?? CancellationToken.None);
                    await bridge.PostAsync("microphone.changed", playback, args.Envelope.RequestId);
                    break;
                case "microphone.cancel":
                    await _microphone.CancelAsync(CancellationToken.None);
                    await bridge.PostAsync("microphone.changed", _microphone.Current, args.Envelope.RequestId);
                    break;
                case "liveCaption.start":
                    await _liveCaptions.StartAsync(
                        LiveCaptionMode.Transcribe,
                        _lifetime?.Token ?? CancellationToken.None);
                    await bridge.PostAsync("caption.changed", _liveCaptions.Current, args.Envelope.RequestId);
                    break;
                case "liveCaption.stop":
                    _ = await _liveCaptions.StopAsync(_lifetime?.Token ?? CancellationToken.None);
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
        finally
        {
            if (chatMessageLocked)
            {
                _chatBridgeGate.Release();
            }
            if (microphoneMessageLocked)
            {
                _microphoneBridgeGate.Release();
            }
            if (audioCaptureMessageLocked)
            {
                _audioCaptureBridgeGate.Release();
            }
            if (updatesAiState)
            {
                await SetShellAiStateAsync(false);
            }
        }
    }

    internal static bool ShouldCancelActiveChatBeforePrompt(
        bool isSpeechRequest,
        bool chatRequestInFlight,
        bool aiRunActive,
        bool speechPlaybackActive)
    {
        // Vorlesen has its own lifecycle and controls. Merely playing audio is
        // never a reason to cancel anything when a new prompt is submitted.
        _ = speechPlaybackActive;
        return !isSpeechRequest && (chatRequestInFlight || aiRunActive);
    }

    private void OnReadFromContextRequested(
        object? sender,
        ReadFromContextRequestedEventArgs args)
    {
        var bridge = _bridge;
        if (bridge is null || _disposed)
        {
            return;
        }

        var target = args.Target;
        _ = StartSpeechRequestAsync(
            target.SessionId,
            target.MessageId,
            null,
            bridge,
            Guid.NewGuid().ToString("D"),
            new SpeechStartAnchor(target.Kind, target.BlockIndex),
            target.MessageUpdatedAt);
    }

    private async Task<bool> ValidateReadFromContextTargetAsync(
        ReadFromContextTarget target,
        CancellationToken cancellationToken)
    {
        if (_disposed || _settings.Current.ActiveSessionId != target.SessionId)
        {
            return false;
        }
        try
        {
            await _goAi.ValidateSpeechStartAsync(
                target.SessionId,
                target.MessageId,
                target.MessageUpdatedAt,
                new SpeechStartAnchor(target.Kind, target.BlockIndex),
                _lifetime?.Token ?? cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLog.ReadFromContextMenuFailed(_logger, exception);
            return false;
        }
    }

    private async Task StartSpeechRequestAsync(
        Guid sessionId,
        Guid messageId,
        string? text,
        AssistantWebBridge bridge,
        string requestId,
        SpeechStartAnchor? startAnchor,
        DateTimeOffset? expectedMessageUpdatedAt)
    {
        var gateHeld = false;
        try
        {
            await _speechStartGate.WaitAsync(_lifetime?.Token ?? CancellationToken.None);
            gateHeld = true;
            if (startAnchor is not null)
            {
                if (_settings.Current.ActiveSessionId != sessionId)
                {
                    throw new InvalidOperationException("Die ausgewählte AI-Sitzung ist nicht mehr geöffnet.");
                }
                await _goAi.ValidateSpeechStartAsync(
                    sessionId,
                    messageId,
                    expectedMessageUpdatedAt
                        ?? throw new InvalidOperationException("Der Nachrichtenstand für den Vorlesestart fehlt."),
                    startAnchor,
                    _lifetime?.Token ?? CancellationToken.None).ConfigureAwait(false);
            }

            var previousTask = _activeSpeechTask;
            var previousCancellation = _activeSpeechRequestCancellation;
            previousCancellation?.Cancel();
            if (_goAi.IsSpeaking || _microphone.Current.IsSpeaking)
            {
                await _goAi.CancelSpeechAsync(CancellationToken.None).ConfigureAwait(false);
            }
            if (previousTask is { IsCompleted: false })
            {
                await previousTask.ConfigureAwait(false);
            }
            previousCancellation?.Dispose();

            var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetime?.Token ?? CancellationToken.None);
            _activeSpeechRequestCancellation = requestCancellation;
            _activeSpeechTask = SpeakWithoutBlockingBridgeAsync(
                sessionId,
                messageId,
                text,
                bridge,
                requestId,
                startAnchor,
                expectedMessageUpdatedAt,
                requestCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetime?.IsCancellationRequested == true)
        {
            // The page was closed while the native context action was being validated.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLog.AssistantRequestFailed(_logger, exception, "speech.readFromHere");
            await bridge.PostErrorAsync(exception.Message, requestId).ConfigureAwait(false);
        }
        finally
        {
            if (gateHeld)
            {
                _speechStartGate.Release();
            }
        }
    }

    private async Task SpeakWithoutBlockingBridgeAsync(
        Guid sessionId,
        Guid messageId,
        string? text,
        AssistantWebBridge bridge,
        string requestId,
        SpeechStartAnchor? startAnchor,
        DateTimeOffset? expectedMessageUpdatedAt,
        CancellationToken cancellationToken)
    {
        try
        {
            await _goAi.SpeakAsync(
                sessionId,
                text,
                messageId,
                update => bridge.PostAsync("speech.status", new
                {
                    active = update.IsActive,
                    status = update.Status,
                    detail = update.Detail,
                    model = update.Model,
                    directionModel = update.DirectionModel,
                    error = update.Error,
                    cacheHit = update.CacheHit,
                }, requestId),
                playback => bridge.PostAsync(
                    "speech.progress",
                    SpeechPlaybackProgressBridge.ToPayload(playback),
                    requestId),
                startAnchor,
                expectedMessageUpdatedAt,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A voice command or replacement prompt intentionally stopped playback.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLog.AssistantRequestFailed(_logger, exception, "microphone.speak");
        }
    }

    private async Task SpeakFeedbackWithoutBlockingBridgeAsync(string text)
    {
        try
        {
            await _microphone.PlayTextAsync(text, cancellationToken: _lifetime?.Token ?? CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A voice command or replacement prompt intentionally stopped playback.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLog.AssistantRequestFailed(_logger, exception, "microphone.speak");
        }
    }

    private async void OnLiveCaptionChanged(object? sender, SystemAudioCaptionSnapshot snapshot)
    {
        if (_disposed)
        {
            return;
        }
        UpdateClientAiRun(
            "system-audio-stt",
            snapshot.IsActive,
            string.Equals(snapshot.Status, "Sprache wird erkannt", StringComparison.Ordinal)
                ? "Systemaudio wird transkribiert"
                : "Live-Untertitel aktiv",
            "Docker · Whisper large-v3");
        try
        {
            await HandleLiveCaptionSnapshotAsync(snapshot, _bridge);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLog.AssistantRequestFailed(_logger, exception, "liveCaption.update");
        }
    }

    private async Task HandleLiveCaptionSnapshotAsync(
        SystemAudioCaptionSnapshot snapshot,
        AssistantWebBridge? bridge)
    {
        if (snapshot.IsActive && !string.IsNullOrWhiteSpace(snapshot.Error))
        {
            _ = await _liveCaptions.StopAsync(CancellationToken.None);
            return;
        }

        if (snapshot.IsActive || snapshot.StartedAt is null)
        {
            if (bridge is not null)
            {
                await bridge.PostAsync("caption.changed", snapshot);
            }
            return;
        }

        await _captionResultGate.WaitAsync(CancellationToken.None);
        try
        {
            if (_lastPersistedCaptionStartedAt == snapshot.StartedAt)
            {
                return;
            }

            await _coordinator.AddLiveCaptionResultAsync(
                snapshot.Transcript,
                snapshot.Error,
                CancellationToken.None);
            _lastPersistedCaptionStartedAt = snapshot.StartedAt;

            var current = _liveCaptions.Current;
            if (!current.IsActive && current.StartedAt == snapshot.StartedAt)
            {
                _liveCaptions.ClearCompleted();
            }

            if (bridge is not null)
            {
                await bridge.PostAsync(
                    "session.changed",
                    await _coordinator.BuildSnapshotAsync(CancellationToken.None));
                await bridge.PostAsync("caption.changed", _liveCaptions.Current);
            }
        }
        finally
        {
            _captionResultGate.Release();
        }
    }

    private async void OnMicrophoneChanged(object? sender, MicrophoneSnapshot snapshot)
    {
        if (_disposed)
        {
            return;
        }
        UpdateClientAiRun(
            "microphone-stt-warmup",
            string.Equals(snapshot.Status, "Sprachmodell wird geladen", StringComparison.Ordinal),
            "Sprachmodell wird vorbereitet",
            "Docker · Whisper STT");
        UpdateClientAiRun(
            "microphone-stt",
            snapshot.Status.Contains("Sprache wird erkannt", StringComparison.Ordinal),
            "Sprache wird live transkribiert",
            "Docker · Whisper STT");
        UpdateClientAiRun(
            "microphone-tts",
            snapshot.IsSpeaking,
            "Antwort wird vorgelesen",
            $"Docker · {GoAiAssistantService.DisplaySpeechProvider(snapshot.Provider)}");
        var bridge = _bridge;
        if (bridge is null)
        {
            return;
        }
        try
        {
            await bridge.PostAsync("microphone.changed", snapshot);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLog.AssistantRequestFailed(_logger, exception, "microphone.update");
        }
    }

    private async void OnMicrophoneTurnChanged(object? sender, MicrophoneTurnSnapshot snapshot)
    {
        var bridge = _bridge;
        if (_disposed || bridge is null)
        {
            return;
        }
        try
        {
            if (!snapshot.IsFinal)
            {
                // Whisper dictation and speech playback are independent. Keep
                // publishing revisions while Supertonic is synthesizing or
                // playing so the user can continue editing the composer.
                await bridge.PostAsync("microphone.transcript", snapshot);
                return;
            }

            var voiceCancellation = _lifetime?.Token ?? CancellationToken.None;

            if (IsVoiceControlStopCommand(snapshot.Text))
            {
                // This command controls the persistent microphone session itself,
                // not the current TTS or AI operation. Chromium releases its
                // track through the transcript event while this session stops.
                await bridge.PostAsync("microphone.transcript", new
                {
                    snapshot.TurnId,
                    snapshot.Text,
                    snapshot.IsFinal,
                    snapshot.Provider,
                    snapshot.Revision,
                    snapshot.StableText,
                    snapshot.ProvisionalText,
                    snapshot.ClientSessionId,
                    Execute = false,
                    Cancel = false,
                    Noise = true,
                    StopVoice = true,
                });
                await _microphone.StopVoiceControlAsync(CancellationToken.None).ConfigureAwait(false);
                return;
            }

            var microphoneState = _microphone.Current;
            var playbackControl = ResolveVoicePlaybackControl(
                snapshot.Text,
                microphoneState,
                _goAi.IsRunning);
            if (playbackControl != VoicePlaybackControl.None)
            {
                if (playbackControl is VoicePlaybackControl.Pause or VoicePlaybackControl.Resume)
                {
                    _ = await _microphone.ToggleSpeechPauseAsync(voiceCancellation).ConfigureAwait(false);
                }
                else
                {
                    await _microphone.StopSpeechAsync(voiceCancellation).ConfigureAwait(false);
                    await _coordinator.CancelCurrentAsync().ConfigureAwait(false);
                }

                await bridge.PostAsync("microphone.transcript", new
                {
                    snapshot.TurnId,
                    snapshot.Text,
                    snapshot.IsFinal,
                    snapshot.Provider,
                    snapshot.Revision,
                    snapshot.StableText,
                    snapshot.ProvisionalText,
                    snapshot.ClientSessionId,
                    Execute = false,
                    Cancel = playbackControl == VoicePlaybackControl.Cancel,
                    Noise = true,
                    Control = playbackControl.ToString().ToLowerInvariant(),
                });
                return;
            }

            if (IsVoicePromptSendCommand(snapshot.Text))
            {
                await bridge.PostAsync("microphone.transcript", new
                {
                    snapshot.TurnId,
                    snapshot.Text,
                    snapshot.IsFinal,
                    snapshot.Provider,
                    snapshot.Revision,
                    snapshot.StableText,
                    snapshot.ProvisionalText,
                    snapshot.ClientSessionId,
                    SendPrompt = true,
                    Dictation = false,
                });
                return;
            }

            if ((_audioCapture.Current.IsRecording || _screenClips.Current.IsRecording)
                && IsMediaCaptureFinishCommand(snapshot.Text))
            {
                // Aufnahmebefehle dürfen nicht erst einen Intent-Modelllauf abwarten.
                // Der Web-Composer beendet damit die aktive Aufnahme und behält den
                // ursprünglichen Analyseprompt für den anschließenden Lauf bei.
                await bridge.PostAsync("microphone.transcript", new
                {
                    snapshot.TurnId,
                    snapshot.Text,
                    snapshot.IsFinal,
                    snapshot.Provider,
                    snapshot.Revision,
                    snapshot.StableText,
                    snapshot.ProvisionalText,
                    snapshot.ClientSessionId,
                    Execute = false,
                    Cancel = true,
                    Noise = false,
                });
                return;
            }

            await bridge.PostAsync("microphone.transcript", new
            {
                snapshot.TurnId,
                snapshot.Text,
                snapshot.IsFinal,
                snapshot.Provider,
                snapshot.Revision,
                snapshot.StableText,
                snapshot.ProvisionalText,
                snapshot.ClientSessionId,
                SendPrompt = false,
                Dictation = true,
            });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLog.AssistantRequestFailed(_logger, exception, "microphone.transcript");
        }
    }

    internal static bool IsMediaCaptureFinishCommand(string? text)
    {
        var normalized = (text ?? string.Empty)
            .Trim()
            .TrimEnd('.', '!', '?', ',', ';', ':')
            .Trim()
            .ToLowerInvariant();
        return normalized is "beenden" or "aufnahme beenden" or "abschließen" or "aufnahme abschließen";
    }

    internal static bool IsVoiceControlStopCommand(string? text)
    {
        var command = NormalizeSpokenCommand(text);
        return command is "sprachsteuerung abbrechen" or "sprachsteuerung beenden";
    }

    internal static bool IsVoicePromptSendCommand(string? text)
    {
        var command = NormalizeSpokenCommand(text);
        return command is "senden" or "prompt senden";
    }

    internal static VoicePlaybackControl ResolveVoicePlaybackControl(
        string? text,
        MicrophoneSnapshot state,
        bool speechOperationActive)
    {
        var command = NormalizeSpokenCommand(text);
        if ((state.IsSpeaking || speechOperationActive)
            && command is "abbrechen" or "abbruch" or "stopp" or "stop"
                or "vorlesen abbrechen" or "vorlesen stoppen")
        {
            return VoicePlaybackControl.Cancel;
        }
        if (state.IsSpeaking && state.CanPauseSpeech && !state.IsSpeechPaused
            && command is "pausieren" or "pause" or "vorlesen pausieren" or "wiedergabe pausieren")
        {
            return VoicePlaybackControl.Pause;
        }
        if (state.IsSpeaking && state.CanPauseSpeech && state.IsSpeechPaused
            && command is "fortsetzen" or "weiter" or "weiterlesen"
                or "vorlesen fortsetzen" or "wiedergabe fortsetzen")
        {
            return VoicePlaybackControl.Resume;
        }
        return VoicePlaybackControl.None;
    }

    private static string NormalizeSpokenCommand(string? text)
    {
        var value = (text ?? string.Empty)
            .Trim()
            .TrimEnd('.', '!', '?', ',', ';', ':')
            .ToLowerInvariant()
            .Replace('-', ' ')
            .Replace('–', ' ')
            .Replace('—', ' ');
        return string.Join(' ', value.Split(
            [' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private async Task StopMicrophoneAfterUnloadAsync()
    {
        try
        {
            await _microphone.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLog.AssistantRequestFailed(_logger, exception, "microphone.unload");
        }
    }

    private void ClearClientSpeechRuns()
    {
        var captions = _liveCaptions.Current;
        _shell.SetClientAiRun(
            "system-audio-stt",
            captions.IsActive,
            "Live-Untertitel aktiv",
            "Docker · Whisper large-v3");
        _shell.SetClientAiRun("microphone-stt-warmup", false, string.Empty, string.Empty);
        _shell.SetClientAiRun("microphone-stt", false, string.Empty, string.Empty);
        _shell.SetClientAiRun("microphone-tts", false, string.Empty, string.Empty);
    }

    private void UpdateClientAiRun(
        string key,
        bool isActive,
        string displayName,
        string runtime)
    {
        void Update() => _shell.SetClientAiRun(key, isActive, displayName, runtime);

        if (DispatcherQueue.HasThreadAccess)
        {
            Update();
        }
        else
        {
            _ = DispatcherQueue.TryEnqueue(Update);
        }
    }

    private async void OnScreenClipChanged(object? sender, ScreenClipSnapshot snapshot)
    {
        var bridge = _bridge;
        if (_disposed || bridge is null)
        {
            return;
        }
        try
        {
            await bridge.PostAsync("screenClip.changed", snapshot);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLog.AssistantRequestFailed(_logger, exception, "screenClip.update");
        }
    }

    private async void OnAudioCaptureChanged(object? sender, SystemAudioCaptureSnapshot snapshot)
    {
        var bridge = _bridge;
        if (_disposed || bridge is null)
        {
            return;
        }
        try
        {
            await bridge.PostAsync("audioCapture.changed", snapshot);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLog.AssistantRequestFailed(_logger, exception, "audioCapture.update");
        }
    }

    private Task SetShellAiStateAsync(bool isRunning)
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            SetShellAiState(isRunning);
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (!_disposed)
                    {
                        SetShellAiState(isRunning);
                    }

                    completion.SetResult();
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
            }))
        {
            completion.SetResult();
        }

        return completion.Task;
    }

    private void SetShellAiState(bool isRunning)
    {
        _shell.IsAiRunning = isRunning;
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
        picker.FileTypeFilter.Add("*");

        InitializePicker(picker);
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0)
        {
            return;
        }

        var pendingNames = files.Select(static file => file.Name).ToList();
        await bridge.PostAsync("document.import.started", new { files = pendingNames }, envelope.RequestId);
        try
        {
            foreach (var file in files)
            {
                var extension = Path.GetExtension(file.Name);
                await using var stream = await file.OpenStreamForReadAsync();
                if (_coordinator.SupportedDocumentExtensions.Contains(extension))
                {
                    await _coordinator.ImportDocumentAsync(
                        sessionId,
                        file.Name,
                        stream,
                        _lifetime?.Token ?? CancellationToken.None);
                }
                else
                {
                    await _coordinator.ImportAttachmentAsync(
                        sessionId,
                        file.Name,
                        ResolveAttachmentContentType(file.Name, file.ContentType),
                        stream,
                        _lifetime?.Token ?? CancellationToken.None);
                }
                pendingNames.Remove(file.Name);
                await bridge.PostAsync("document.import.progress", new { remaining = pendingNames }, envelope.RequestId);
            }
        }
        finally
        {
            await bridge.PostAsync("document.import.completed", new { }, envelope.RequestId);
        }
        await bridge.PostAsync(
            "document.changed",
            await _coordinator.BuildSnapshotAsync(_lifetime?.Token ?? CancellationToken.None),
            envelope.RequestId);
    }

    private async Task CaptureScreenshotAsync(WebBridgeEnvelope envelope, AssistantWebBridge bridge)
    {
        if (!TryReadGuid(envelope.Payload, "sessionId", out var sessionId))
        {
            throw new InvalidOperationException("Öffne oder erstelle zuerst eine Sitzung.");
        }
        var selected = await SelectCaptureTargetAsync(
            "Screenshot aufnehmen",
            "GO erstellt genau einen Screenshot der gewählten Quelle und fügt ihn als lokalen Sitzungsanhang hinzu.");
        if (selected is null)
        {
            await bridge.PostAsync("capture.cancelled", new { action = "imageAnalysis" }, envelope.RequestId);
            return;
        }

        var screenshot = await _screenshots.CaptureAsync(selected, _lifetime?.Token ?? CancellationToken.None);
        await using var stream = new MemoryStream(screenshot.Content, writable: false);
        await _coordinator.ImportAttachmentAsync(
            sessionId,
            screenshot.FileName,
            screenshot.ContentType,
            stream,
            _lifetime?.Token ?? CancellationToken.None);
        await bridge.PostAsync(
            "document.changed",
            await _coordinator.BuildSnapshotAsync(_lifetime?.Token ?? CancellationToken.None),
            envelope.RequestId);
    }

    internal static string ResolveAttachmentContentType(string fileName, string? reportedContentType)
    {
        if (!string.IsNullOrWhiteSpace(reportedContentType)
            && !string.Equals(reportedContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            return reportedContentType;
        }
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".tif" or ".tiff" => "image/tiff",
            ".wav" => "audio/wav",
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".flac" => "audio/flac",
            ".ogg" or ".oga" => "audio/ogg",
            ".aac" => "audio/aac",
            ".mp4" or ".m4v" => "video/mp4",
            ".mov" => "video/quicktime",
            ".avi" => "video/x-msvideo",
            ".mkv" => "video/x-matroska",
            ".webm" => "video/webm",
            _ => "application/octet-stream",
        };
    }

    private async Task StartScreenClipAsync(WebBridgeEnvelope envelope, AssistantWebBridge bridge)
    {
        if (!TryReadGuid(envelope.Payload, "sessionId", out var sessionId))
        {
            throw new InvalidOperationException("Öffne oder erstelle zuerst eine Sitzung.");
        }
        var selected = await SelectCaptureTargetAsync(
            "Bildschirmclip aufnehmen",
            "GO nimmt die gewählte Quelle bewusst für maximal 30 Sekunden mit zwei Bildern pro Sekunde und ohne Ton auf. Der Clip bleibt lokal, bis du ihn mit einem AI-Auftrag temporär hochlädst.");
        if (selected is null)
        {
            await bridge.PostAsync("capture.cancelled", new { action = "videoAnalysis" }, envelope.RequestId);
            return;
        }
        await _screenClips.StartAsync(sessionId, selected, CancellationToken.None);
        await bridge.PostAsync("screenClip.changed", _screenClips.Current, envelope.RequestId);
    }

    private async Task StopScreenClipAsync(
        WebBridgeEnvelope envelope,
        AssistantWebBridge bridge,
        Guid? targetSessionId = null,
        bool suppressSnapshot = false)
    {
        var result = await _screenClips.StopAsync(CancellationToken.None);
        try
        {
            await using var stream = await OpenCapturedMediaAsync(
                result.Path,
                _lifetime?.Token ?? CancellationToken.None);
            await _coordinator.ImportAttachmentAsync(
                targetSessionId ?? result.SessionId,
                result.FileName,
                result.ContentType,
                stream,
                _lifetime?.Token ?? CancellationToken.None);
        }
        finally
        {
            try { File.Delete(result.Path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        await bridge.PostAsync("screenClip.changed", _screenClips.Current, envelope.RequestId);
        if (!suppressSnapshot)
        {
            await bridge.PostAsync(
                "document.changed",
                await _coordinator.BuildSnapshotAsync(_lifetime?.Token ?? CancellationToken.None),
                envelope.RequestId);
        }
    }

    private async Task StopAudioCaptureAsync(
        WebBridgeEnvelope envelope,
        AssistantWebBridge bridge)
    {
        var result = await _audioCapture.StopAsync(CancellationToken.None);
        await using var stream = new MemoryStream(result.Content, writable: false);
        await _coordinator.ImportAttachmentAsync(
            result.SessionId,
            result.FileName,
            result.ContentType,
            stream,
            _lifetime?.Token ?? CancellationToken.None);
        await bridge.PostAsync("audioCapture.changed", _audioCapture.Current, envelope.RequestId);
        await bridge.PostAsync(
            "document.changed",
            await _coordinator.BuildSnapshotAsync(_lifetime?.Token ?? CancellationToken.None),
            envelope.RequestId);
    }

    private static async Task<FileStream> OpenCapturedMediaAsync(string path, CancellationToken cancellationToken)
    {
        IOException? lastError = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    1_048_576,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (IOException exception)
            {
                lastError = exception;
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
        throw new IOException("Der fertiggestellte Bildschirmclip konnte nicht für die lokale Speicherung geöffnet werden.", lastError);
    }

    private async Task<DesktopCaptureTarget?> SelectCaptureTargetAsync(string title, string description)
    {
        var targets = _screenshots.ListTargets();
        if (targets.Count == 0)
        {
            throw new InvalidOperationException("Windows meldet keinen aufnehmbaren Bildschirm oder kein Fenster.");
        }
        var selector = new ComboBox
        {
            Header = "Aufnahmequelle",
            ItemsSource = targets,
            DisplayMemberPath = nameof(DesktopCaptureTarget.DisplayName),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(selector);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = "Aufnehmen",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary
            ? selector.SelectedItem as DesktopCaptureTarget
            : null;
    }

    private async void OnArtifactResourceRequested(
        CoreWebView2 sender,
        CoreWebView2WebResourceRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            if (!Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri)
                || !string.Equals(uri.Host, AssistantWebBridge.VirtualHost, StringComparison.OrdinalIgnoreCase)
                || !uri.AbsolutePath.StartsWith("/artifacts/", StringComparison.Ordinal)
                || !Guid.TryParse(uri.AbsolutePath["/artifacts/".Length..], out var artifactId)
                || await _artifacts.GetAsync(artifactId, _lifetime?.Token ?? CancellationToken.None) is not { } artifact)
            {
                args.Response = sender.Environment.CreateWebResourceResponse(
                    new MemoryStream().AsRandomAccessStream(), 404, "Not Found", "Cache-Control: no-store\r\n");
                return;
            }

            Stream source;
            if (artifact.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                var memory = new MemoryStream(artifact.Length <= int.MaxValue ? checked((int)artifact.Length) : 0);
                await _blobs.ExportAsync(artifact.BlobId, memory, _lifetime?.Token ?? CancellationToken.None);
                var randomAccess = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(randomAccess.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(memory.ToArray());
                    _ = await writer.StoreAsync();
                    _ = await writer.FlushAsync();
                    writer.DetachStream();
                }
                randomAccess.Seek(0);
                var imageHeaders = new StringBuilder()
                    .Append("Content-Type: ").Append(artifact.ContentType).Append("\r\n")
                    .Append("Content-Length: ").Append(artifact.Length).Append("\r\n")
                    .Append("Cache-Control: private, no-store\r\n")
                    .Append("Cross-Origin-Resource-Policy: same-origin\r\n")
                    .Append("X-Content-Type-Options: nosniff\r\n")
                    .Append("Content-Disposition: inline\r\n");
                args.Response = sender.Environment.CreateWebResourceResponse(
                    randomAccess,
                    200,
                    "OK",
                    imageHeaders.ToString());
                return;
            }
            else
            {
                source = await _blobs.OpenReadAsync(artifact.BlobId, _lifetime?.Token ?? CancellationToken.None);
            }
            var rangeHeader = args.Request.Headers.Contains("Range")
                ? args.Request.Headers.GetHeader("Range")
                : null;
            var range = ParseRange(rangeHeader, artifact.Length);
            Stream content = source;
            var status = 200;
            var reason = "OK";
            var length = artifact.Length;
            var headers = new StringBuilder()
                .Append("Content-Type: ").Append(artifact.ContentType).Append("\r\n")
                .Append("Accept-Ranges: bytes\r\n")
                .Append("Cache-Control: private, no-store\r\n")
                .Append("Cross-Origin-Resource-Policy: same-origin\r\n")
                .Append("X-Content-Type-Options: nosniff\r\n");
            if (range is { } requested)
            {
                content = new RangeReadStream(source, requested.Start, requested.Length);
                status = 206;
                reason = "Partial Content";
                length = requested.Length;
                headers.Append("Content-Range: bytes ")
                    .Append(requested.Start).Append('-').Append(requested.End)
                    .Append('/').Append(artifact.Length).Append("\r\n");
            }
            var disposition = uri.Query.Contains("download=1", StringComparison.OrdinalIgnoreCase) ? "attachment" : "inline";
            var safeName = new string(artifact.FileName
                .Select(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '_')
                .ToArray());
            headers.Append("Content-Length: ").Append(length).Append("\r\n")
                .Append("Content-Disposition: ").Append(disposition).Append("; filename=\"").Append(safeName)
                .Append("\"; filename*=UTF-8''").Append(Uri.EscapeDataString(artifact.FileName)).Append("\r\n");
            args.Response = sender.Environment.CreateWebResourceResponse(
                content.AsRandomAccessStream(), status, reason, headers.ToString());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLog.AssistantRequestFailed(_logger, exception, "artifact.read");
            args.Response = sender.Environment.CreateWebResourceResponse(
                new MemoryStream().AsRandomAccessStream(), 500, "Internal Server Error", "Cache-Control: no-store\r\n");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static ByteRange? ParseRange(string? value, long totalLength)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)
            || value.Contains(',', StringComparison.Ordinal))
        {
            return null;
        }
        var parts = value[6..].Split('-', 2);
        if (parts.Length != 2 || !long.TryParse(parts[0], out var start) || start < 0 || start >= totalLength)
        {
            return null;
        }
        var end = string.IsNullOrWhiteSpace(parts[1]) || !long.TryParse(parts[1], out var parsedEnd)
            ? totalLength - 1
            : Math.Min(parsedEnd, totalLength - 1);
        return end < start ? null : new ByteRange(start, end);
    }

    private sealed record ByteRange(long Start, long End)
    {
        public long Length => End - Start + 1;
    }

    private sealed class RangeReadStream : Stream
    {
        private readonly Stream _source;
        private readonly long _start;
        private readonly long _length;
        private long _position;
        private long _skip;

        public RangeReadStream(Stream source, long start, long length)
        {
            _source = source;
            _start = start;
            _skip = start;
            _length = length;
            if (_source.CanSeek)
            {
                _ = _source.Seek(start, SeekOrigin.Begin);
                _skip = 0;
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => _source.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position
        {
            get => _position;
            set => _ = Seek(value, SeekOrigin.Begin);
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_skip > 0)
            {
                var discarded = new byte[(int)Math.Min(81_920, _skip)];
                var read = await _source.ReadAsync(discarded, cancellationToken).ConfigureAwait(false);
                if (read == 0) return 0;
                _skip -= read;
            }
            if (_position >= _length) return 0;
            var count = (int)Math.Min(buffer.Length, _length - _position);
            var actual = await _source.ReadAsync(buffer[..count], cancellationToken).ConfigureAwait(false);
            _position += actual;
            return actual;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _source.Dispose();
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await _source.DisposeAsync();
            await base.DisposeAsync();
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            if (!CanSeek)
            {
                throw new NotSupportedException();
            }
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(_length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            if (target < 0 || target > _length)
            {
                throw new IOException("Die angeforderte Artefaktposition liegt außerhalb des HTTP-Bereichs.");
            }
            _ = _source.Seek(checked(_start + target), SeekOrigin.Begin);
            _position = target;
            _skip = 0;
            return _position;
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private async Task ExportPdfAsync(
        WebBridgeEnvelope envelope,
        AssistantWebBridge bridge,
        bool selectedMessageOnly)
    {
        if (!await _exportGate.WaitAsync(0))
        {
            throw new InvalidOperationException("Ein PDF-Export läuft bereits.");
        }

        CoreWebView2? core = null;
        var bookPrepared = false;
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = selectedMessageOnly
                    ? $"GO-Nachricht-{DateTime.Now:yyyy-MM-dd-HHmm}"
                    : $"GO-Chat-{DateTime.Now:yyyy-MM-dd-HHmm}",
                DefaultFileExtension = ".pdf",
            };
            picker.FileTypeChoices.Add("PDF-Dokument", new List<string> { ".pdf" });
            InitializePicker(picker);
            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            core = _assistantWebView?.CoreWebView2
                ?? throw new InvalidOperationException("Die Chat-Oberfläche ist nicht bereit.");
            string? messageId = null;
            if (selectedMessageOnly)
            {
                messageId = ReadRequiredText(envelope.Payload, "messageId", 128);
            }

            var scriptArgument = JsonSerializer.Serialize(messageId);
            var result = await core.ExecuteScriptAsync(
                $"globalThis.goPrepareBookPdf?.({scriptArgument}) ?? false");
            if (!string.Equals(result, "true", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(selectedMessageOnly
                    ? "Die ausgewählte Nachricht wurde nicht gefunden."
                    : "Der Chat enthält keine exportierbaren Nachrichten.");
            }

            bookPrepared = true;
            await WaitForBookPdfAssetsAsync(core);

            var printSettings = core.Environment.CreatePrintSettings();
            ConfigureBookPdfPrintSettings(printSettings);
            var success = await core.PrintToPdfAsync(file.Path, printSettings);
            if (!success)
            {
                throw new InvalidOperationException("WebView2 konnte das PDF nicht erstellen.");
            }

            await bridge.PostAsync("status.changed", new { exportCompleted = true }, envelope.RequestId);
        }
        finally
        {
            if (bookPrepared && core is not null)
            {
                try
                {
                    await core.ExecuteScriptAsync("globalThis.goFinishBookPdf?.()");
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    AppLog.MessagePdfViewResetFailed(_logger, exception);
                }
            }

            _exportGate.Release();
        }
    }

    private static void ConfigureBookPdfPrintSettings(CoreWebView2PrintSettings printSettings)
    {
        printSettings.Orientation = CoreWebView2PrintOrientation.Portrait;
        printSettings.PageWidth = PdfA4WidthInches;
        printSettings.PageHeight = PdfA4HeightInches;
        printSettings.MarginTop = PdfBookMarginTopInches;
        printSettings.MarginRight = PdfBookMarginRightInches;
        printSettings.MarginBottom = PdfBookMarginBottomInches;
        printSettings.MarginLeft = PdfBookMarginLeftInches;
        printSettings.ScaleFactor = 1d;
        printSettings.ShouldPrintBackgrounds = true;
        printSettings.ShouldPrintHeaderAndFooter = false;
        printSettings.ShouldPrintSelectionOnly = false;
    }

    private static async Task WaitForBookPdfAssetsAsync(CoreWebView2 core)
    {
        const int maximumAttempts = 40;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            var ready = await core.ExecuteScriptAsync("globalThis.goPdfBookReady?.() ?? true");
            if (string.Equals(ready, "true", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await Task.Delay(50);
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

    private async Task SaveArtifactAsync(JsonElement payload, AssistantWebBridge bridge)
    {
        if (!TryReadGuid(payload, "artifactId", out var artifactId)
            || await _artifacts.GetAsync(artifactId, _lifetime?.Token ?? CancellationToken.None) is not { } artifact)
        {
            throw new InvalidOperationException("Das lokale AI-Artefakt wurde nicht gefunden.");
        }
        var extension = Path.GetExtension(artifact.FileName);
        if (string.IsNullOrWhiteSpace(extension)) extension = ".bin";
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(artifact.FileName),
            DefaultFileExtension = extension,
        };
        picker.FileTypeChoices.Add("AI-Artefakt", new List<string> { extension });
        InitializePicker(picker);
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        await using var output = await file.OpenStreamForWriteAsync();
        output.SetLength(0);
        await _blobs.ExportAsync(artifact.BlobId, output, _lifetime?.Token ?? CancellationToken.None);
        await bridge.PostAsync("status.changed", new { artifactSaved = artifact.FileName });
    }

    private async Task OpenArtifactAsync(JsonElement payload)
    {
        if (!TryReadGuid(payload, "artifactId", out var artifactId)
            || await _artifacts.GetAsync(artifactId, _lifetime?.Token ?? CancellationToken.None) is not { } artifact
            || !artifact.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Das zu öffnende Bild wurde nicht gefunden.");
        }

        var path = await _previews.MaterializeOriginalAsync(
            artifactId,
            _lifetime?.Token ?? CancellationToken.None);
        var file = await StorageFile.GetFileFromPathAsync(path);
        if (!await Launcher.LaunchFileAsync(file))
        {
            throw new InvalidOperationException("Das Bild konnte nicht im Standardprogramm geöffnet werden.");
        }
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

        var isLight = ActualTheme == ElementTheme.Light;
        var resolvedTheme = isLight ? "light" : "dark";
        var accentHex = App.Current.AccentColor;
        var backgroundHex = App.Current.BackgroundColor;
        var highContrast = false;
        try
        {
            highContrast = _accessibilitySettings.HighContrast;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLog.SystemThemeEventUnavailable(_logger, exception, "AccessibilitySettings.HighContrast");
        }

        await bridge.PostAsync("theme.changed", new
        {
            theme = resolvedTheme,
            accent = accentHex,
            backgroundAccent = backgroundHex,
            highContrast,
        });
    }

    private void SubscribeOptionalSystemThemeEvents()
    {
        try
        {
            _uiSettings.ColorValuesChanged += OnSystemColorsChanged;
            _colorEventsSubscribed = true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLog.SystemThemeEventUnavailable(_logger, exception, "UISettings.ColorValuesChanged");
        }

        try
        {
            _accessibilitySettings.HighContrastChanged += OnHighContrastChanged;
            _contrastEventsSubscribed = true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLog.SystemThemeEventUnavailable(_logger, exception, "AccessibilitySettings.HighContrastChanged");
        }
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

    private async Task PickWorkspaceAsync(WebBridgeEnvelope envelope, AssistantWebBridge bridge)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add("*");
        InitializePicker(picker);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        var workspace = Path.GetFullPath(folder.Path);
        await _coordinator.SetActiveWorkspaceAsync(
            workspace,
            _lifetime?.Token ?? CancellationToken.None);
        await bridge.PostAsync(
            "state.snapshot",
            await _coordinator.BuildSnapshotAsync(_lifetime?.Token ?? CancellationToken.None),
            envelope.RequestId);
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

    private static string ReadRequiredText(JsonElement payload, string name, int maximumLength)
    {
        if (!payload.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"'{name}' fehlt oder ist ungültig.");
        }

        var value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength
            ? value
            : throw new InvalidOperationException($"'{name}' fehlt oder ist ungültig.");
    }

    private static string? ReadOptionalText(JsonElement payload, string name, int maximumLength)
    {
        if (!payload.TryGetProperty(name, out var property)
            || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"'{name}' ist ungültig.");
        }
        var value = property.GetString()?.Trim();
        return string.IsNullOrEmpty(value)
            ? null
            : value.Length <= maximumLength
                ? value
                : throw new InvalidOperationException($"'{name}' ist zu lang.");
    }
}
