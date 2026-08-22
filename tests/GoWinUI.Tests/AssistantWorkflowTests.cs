using GoWinUI.App.Services;
using GoWinUI.App.Pages;
using GoWinUI.App.ViewModels;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using GoWinUI.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace GoWinUI.Tests;

public sealed class AssistantWorkflowTests
{
    [Fact]
    public void WorkspacePickerIsAcceptedByNativeAndWebBridgeContracts()
    {
        Assert.True(AssistantWebBridge.IsIncomingTypeAllowed("workspace.pick"));
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var bridge = File.ReadAllText(Path.Combine(webRoot, "bridge.js"));
        Assert.Contains("\"workspace.pick\"", bridge, StringComparison.Ordinal);
    }

    [Fact]
    public void PersistentSessionToolIsAcceptedAndLegacySessionModeRemainsCompatible()
    {
        Assert.True(AssistantWebBridge.IsIncomingTypeAllowed("session.mode"));
        Assert.True(AssistantWebBridge.IsIncomingTypeAllowed("session.tool"));
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var bridge = File.ReadAllText(Path.Combine(webRoot, "bridge.js"));
        Assert.Contains("\"session.mode\"", bridge, StringComparison.Ordinal);
        Assert.Contains("\"session.tool\"", bridge, StringComparison.Ordinal);

        var html = File.ReadAllText(Path.Combine(webRoot, "index.html"));
        Assert.Contains("bridge.js?v=20260821-2", html, StringComparison.Ordinal);

        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));
        Assert.Contains("post(\"session.tool\"", app, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionPinUsesDescriptiveGermanLabelsAcrossTheWebViewContract()
    {
        Assert.True(AssistantWebBridge.IsIncomingTypeAllowed("session.pin"));
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var bridge = File.ReadAllText(Path.Combine(webRoot, "bridge.js"));
        Assert.Contains("\"session.pin\"", bridge, StringComparison.Ordinal);
        var html = File.ReadAllText(Path.Combine(webRoot, "index.html"));
        Assert.Contains("title=\"Sitzung anpinnen\"", html, StringComparison.Ordinal);
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));
        Assert.Contains("session?.isPinned ? \"Sitzung loslösen\" : \"Sitzung anpinnen\"", app, StringComparison.Ordinal);
        Assert.Contains("post(\"session.pin\"", app, StringComparison.Ordinal);
        Assert.DoesNotContain("className = `session-pin", app, StringComparison.Ordinal);
        Assert.Contains("item.append(open, remove)", app, StringComparison.Ordinal);
        Assert.Contains("session.isPinned ? \" pinned\"", app, StringComparison.Ordinal);
        Assert.Contains("class=\"pdf-chip\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatAndMessagePdfExportsUseTheSharedDinA4BookLayout()
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var html = File.ReadAllText(Path.Combine(webRoot, "index.html"));
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));
        var styles = File.ReadAllText(Path.Combine(webRoot, "styles.css"));

        Assert.InRange(AssistantPage.PdfA4WidthInches, 8.267, 8.268);
        Assert.InRange(AssistantPage.PdfA4HeightInches, 11.692, 11.693);
        Assert.InRange(AssistantPage.PdfBookMarginLeftInches, .944, .946);
        Assert.InRange(AssistantPage.PdfBookMarginBottomInches, .944, .946);
        Assert.Contains("styles.css?v=20260821-2", html, StringComparison.Ordinal);
        Assert.Contains("markdown.js?v=20260821-1", html, StringComparison.Ordinal);
        Assert.Contains("voice.js?v=20260822-2", html, StringComparison.Ordinal);
        Assert.Contains("app.js?v=20260822-6", html, StringComparison.Ordinal);
        Assert.Contains("globalThis.goPrepareBookPdf = messageId =>", app, StringComparison.Ordinal);
        Assert.Contains("globalThis.goPdfBookReady = () =>", app, StringComparison.Ordinal);
        Assert.Contains("globalThis.goPrepareMessagePdf = globalThis.goPrepareBookPdf", app, StringComparison.Ordinal);
        Assert.Contains("pdf-book--message", app, StringComparison.Ordinal);
        Assert.Contains("pdf-book--chat", app, StringComparison.Ordinal);
        Assert.Contains("size: A4 portrait", styles, StringComparison.Ordinal);
        Assert.Contains("background: #fff", styles, StringComparison.Ordinal);
        Assert.Contains("color-scheme: only light !important", styles, StringComparison.Ordinal);
        Assert.Contains("font: 10.75pt/1.58 Georgia", styles, StringComparison.Ordinal);
        Assert.Contains("hyphens: auto", styles, StringComparison.Ordinal);
        Assert.Contains("orphans: 3", styles, StringComparison.Ordinal);
        Assert.Contains("widows: 3", styles, StringComparison.Ordinal);
        Assert.Contains(".pdf-book thead { display: table-header-group; }", styles, StringComparison.Ordinal);
        Assert.Contains("white-space: pre-wrap !important", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("pdf-exporting-message", styles, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("microphone.start")]
    [InlineData("microphone.audio")]
    [InlineData("microphone.speak")]
    [InlineData("microphone.stopSpeech")]
    [InlineData("microphone.toggleSpeechPause")]
    [InlineData("microphone.stop")]
    [InlineData("microphone.cancel")]
    public void MicrophoneMessagesAreAcceptedByTheWebBridge(string messageType)
    {
        Assert.True(AssistantWebBridge.IsIncomingTypeAllowed(messageType));
    }

    [Fact]
    public void WebAssetsExposeTheSameMicrophoneContractAndPlaceItAboveSend()
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var bridge = File.ReadAllText(Path.Combine(webRoot, "bridge.js"));
        Assert.Contains("\"microphone.start\"", bridge, StringComparison.Ordinal);
        Assert.Contains("\"microphone.audio\"", bridge, StringComparison.Ordinal);
        Assert.Contains("\"microphone.speak\"", bridge, StringComparison.Ordinal);
        Assert.Contains("\"microphone.stopSpeech\"", bridge, StringComparison.Ordinal);
        Assert.Contains("\"microphone.toggleSpeechPause\"", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("microphone.previousSpeechParagraph", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("microphone.skipSpeechParagraph", bridge, StringComparison.Ordinal);
        Assert.Contains("\"microphone.stop\"", bridge, StringComparison.Ordinal);
        Assert.Contains("\"microphone.cancel\"", bridge, StringComparison.Ordinal);
        Assert.Contains("\"microphone.changed\"", bridge, StringComparison.Ordinal);
        Assert.Contains("\"microphone.transcript\"", bridge, StringComparison.Ordinal);
        Assert.Contains("\"attachment.remove\"", bridge, StringComparison.Ordinal);

        var html = File.ReadAllText(Path.Combine(webRoot, "index.html"));
        var submit = html.IndexOf("class=\"toolbar-group composer-submit\"", StringComparison.Ordinal);
        var microphone = html.IndexOf("id=\"microphone\"", submit, StringComparison.Ordinal);
        var send = html.IndexOf("id=\"send\"", submit, StringComparison.Ordinal);
        Assert.True(submit >= 0 && microphone > submit && send > microphone);

        var styles = File.ReadAllText(Path.Combine(webRoot, "styles.css"));
        Assert.Contains(".composer-submit {", styles, StringComparison.Ordinal);
        Assert.Contains(".send-button, .stop-button, .microphone-button", styles, StringComparison.Ordinal);

        var voice = File.ReadAllText(Path.Combine(webRoot, "voice.js"));
        Assert.Contains("navigator.mediaDevices.getUserMedia", voice, StringComparison.Ordinal);
        Assert.Contains("microphone.audio", voice, StringComparison.Ordinal);
        Assert.Contains("go:voice-level", voice, StringComparison.Ordinal);
        Assert.Contains("createAnalyser", voice, StringComparison.Ordinal);
        Assert.Contains("createMediaStreamDestination", voice, StringComparison.Ordinal);
        Assert.Contains("getUserMedia({ audio: true, video: false })", voice, StringComparison.Ordinal);
        Assert.Contains("beginTurn(values)", voice, StringComparison.Ordinal);
        Assert.Contains("const bridgeFrameSamples = 1600;", voice, StringComparison.Ordinal);
        Assert.Contains("if (turn.speechSamples >= minimumTurnSamples) emitAvailableFrames(turn);", voice, StringComparison.Ordinal);
        Assert.Contains("const preRollSamples = 4000;", voice, StringComparison.Ordinal);
        Assert.Contains("const silenceToFinishSamples = 8000;", voice, StringComparison.Ordinal);
        Assert.Contains("sendFrame(turn, turn.frameBuffer.splice(0), true);", voice, StringComparison.Ordinal);
        Assert.DoesNotContain("windowSamples", voice, StringComparison.Ordinal);
        Assert.DoesNotContain("for (const value of values) samples.push(value);", voice, StringComparison.Ordinal);
        Assert.DoesNotContain("MediaRecorder", voice, StringComparison.Ordinal);

        Assert.Contains("microphone-frequency", html, StringComparison.Ordinal);
        Assert.DoesNotContain("voice-feedback", html, StringComparison.Ordinal);
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));
        Assert.Contains("voice-context-chip", app, StringComparison.Ordinal);
        Assert.DoesNotContain("voice-listening-preview", app, StringComparison.Ordinal);
        Assert.Contains("Ich höre zu", app, StringComparison.Ordinal);
        Assert.Contains("const hasContent = Boolean(caption.isActive);", app, StringComparison.Ordinal);
        Assert.DoesNotContain("caption.isActive || caption.error", app, StringComparison.Ordinal);
        Assert.DoesNotContain("start-live-translation", html, StringComparison.Ordinal);
        Assert.Contains("artifact.provider === \"screen-capture\"", app, StringComparison.Ordinal);
        Assert.Contains("message-artifacts--captures", app, StringComparison.Ordinal);
        Assert.DoesNotContain("renderVoiceFeedback()", app, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"capture-screen\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"capture-clip\"", html, StringComparison.Ordinal);
        Assert.Contains("data-tool-action=\"webSearch\"", html, StringComparison.Ordinal);
        Assert.Contains("data-tool-action=\"imageGeneration\"", html, StringComparison.Ordinal);
        Assert.Contains("data-tool-action=\"code\"", html, StringComparison.Ordinal);
        Assert.Contains("Vorlesen", app, StringComparison.Ordinal);
        Assert.Contains("post(\"microphone.speak\", {", app, StringComparison.Ordinal);
        Assert.Contains("messageId: String(message.id)", app, StringComparison.Ordinal);
        Assert.DoesNotContain("speechMessageId: String(message.id)", app, StringComparison.Ordinal);
    }

    [Fact]
    public void ScreenClipEventsRenderOnlyInTheComposerContext()
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));
        var styles = File.ReadAllText(Path.Combine(webRoot, "styles.css"));

        Assert.DoesNotContain("createScreenClipProgressMessage", app, StringComparison.Ordinal);
        Assert.DoesNotContain("data-screen-clip-progress", app, StringComparison.Ordinal);
        Assert.Contains("Video aufnehmen · ${formatClipTime(elapsed)}", app, StringComparison.Ordinal);
        Assert.Contains("post(\"screenClip.stop\"", app, StringComparison.Ordinal);
        Assert.Contains("post(\"screenClip.cancel\"", app, StringComparison.Ordinal);
        Assert.DoesNotContain(".screen-clip-progress", styles, StringComparison.Ordinal);
        Assert.Contains(".screen-clip-chip", styles, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("webSearch", PromptTriggerAction.WebSearch)]
    [InlineData("imageGeneration", PromptTriggerAction.ImageGeneration)]
    [InlineData("youTubeSearch", PromptTriggerAction.YouTubeSearch)]
    [InlineData("bricsCad", PromptTriggerAction.BricsCad)]
    [InlineData("code", PromptTriggerAction.Code)]
    [InlineData("audiobook", PromptTriggerAction.Audiobook)]
    [InlineData("textToSpeech", PromptTriggerAction.TextToSpeech)]
    public void ExplicitComposerToolCreatesOneShotTrigger(string tool, PromptTriggerAction expected)
    {
        var match = AssistantCoordinator.CreateToolMatch(tool, "Aufgabe ohne Präfix");
        Assert.Equal(expected, match.Trigger.Action);
        Assert.Equal("Aufgabe ohne Präfix", match.RemainingPrompt);
    }

    [Fact]
    public async Task ReadAloudPromptIsClassifiedWithoutBecomingAChatRun()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        using var coordinator = CreateCoordinator(environment, settings, CreateRecentActivity(settings));
        var session = await environment.Get<IChatRepository>().CreateSessionAsync("Parallel vorlesen");

        using var speechPayload = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            sessionId = session.Id,
            prompt = "Vorlesen",
            toolAction = "textToSpeech",
        }));
        using var chatPayload = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            sessionId = session.Id,
            prompt = "Analysiere den nÃ¤chsten Fall.",
        }));

        Assert.True(await coordinator.IsSpeechRequestAsync(speechPayload.RootElement));
        Assert.False(await coordinator.IsSpeechRequestAsync(chatPayload.RootElement));
    }

    [Fact]
    public void PersistedCodingProcessReportCanAlwaysBeReadAloud()
    {
        var now = DateTimeOffset.UtcNow;
        var message = new ChatMessage(
            Guid.NewGuid(),
            Guid.NewGuid(),
            ChatRole.Assistant,
            "### Prozessbericht\n\nDie Schwarzschild-Metrik wird symbolisch geprÃ¼ft.",
            MessageStatus.Completed,
            now,
            now);

        Assert.True(GoAiAssistantService.IsReadableSpeechMessage(message));
        Assert.NotEmpty(SpeechSourceSegmentation.CreateUnits(message.Content));
    }

    [Fact]
    public void BrowserMicrophonePcmIsWrappedAsA16KhzMonoWave()
    {
        var pcm = new byte[16_000 * 2];

        var wave = MicrophoneTranscriptionService.CreatePcm16Wave(pcm);

        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wave, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wave, 8, 4));
        Assert.Equal(1, BitConverter.ToInt16(wave, 22));
        Assert.Equal(16_000, BitConverter.ToInt32(wave, 24));
        Assert.Equal(16, BitConverter.ToInt16(wave, 34));
        Assert.Equal(pcm.Length + 44, wave.Length);
    }

    [Fact]
    public void DictationCaptureSchedulesAtFiveHundredMillisecondsAndKeepsSixSeconds()
    {
        var capture = new MicrophoneTranscriptionService.DictationCaptureState("turn-1", "session-1");
        var frame = new byte[16_000 * sizeof(short) / 10];

        for (var index = 0; index < 4; index++)
        {
            capture.Append(frame);
        }
        Assert.False(capture.ShouldSchedule(isFinal: false));

        capture.Append(frame);
        Assert.True(capture.ShouldSchedule(isFinal: false));
        var first = capture.CreateWindow(isFinal: false);
        capture.MarkScheduled();
        Assert.Equal(0, first.Revision);
        Assert.Equal(16_000, first.Pcm.Length);

        capture.Append(frame);
        capture.Append(frame);
        Assert.False(capture.ShouldSchedule(isFinal: false));
        capture.Append(frame);
        Assert.True(capture.ShouldSchedule(isFinal: false));
        _ = capture.CreateWindow(isFinal: false);
        capture.MarkScheduled();

        for (var index = 0; index < 60; index++)
        {
            capture.Append(frame);
        }
        var rolling = capture.CreateWindow(isFinal: true);
        Assert.Equal(6_000 * 32, rolling.Pcm.Length);
        Assert.Equal(800, rolling.WindowStartMilliseconds);
        Assert.True(capture.ShouldSchedule(isFinal: true));
    }

    [Fact]
    public void SystemAudioAnalysisCaptureProducesAValidTenMinuteBoundedWave()
    {
        var pcm = new byte[SystemAudioAnalysisCaptureService.SampleRate * sizeof(short)];

        var wave = SystemAudioAnalysisCaptureService.CreateWave(pcm);

        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wave, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wave, 8, 4));
        Assert.Equal(SystemAudioAnalysisCaptureService.SampleRate, BitConverter.ToInt32(wave, 24));
        Assert.Equal(1, BitConverter.ToInt16(wave, 22));
        Assert.Equal(16, BitConverter.ToInt16(wave, 34));
        Assert.Equal(pcm.Length + 44, wave.Length);
    }

    [Fact]
    public void MediaAnalysisPrefersDocumentsAndOtherwiseRequiresMatchingMedia()
    {
        var now = DateTimeOffset.UtcNow;
        var image = new AssistantAttachment(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "foto.png", "image/png", "blob", 10, now);

        Assert.True(AssistantCoordinator.HasMediaAnalysisContext(
            PromptTriggerAction.AudioAnalysis,
            hasDocuments: true,
            []));
        Assert.True(AssistantCoordinator.HasMediaAnalysisContext(
            PromptTriggerAction.ImageAnalysis,
            hasDocuments: false,
            [image]));
        Assert.False(AssistantCoordinator.HasMediaAnalysisContext(
            PromptTriggerAction.VideoAnalysis,
            hasDocuments: false,
            [image]));
    }

    [Fact]
    public void AnalysisToolsOwnCaptureAndSidebarDeleteRemainsInteractiveDuringRuns()
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var html = File.ReadAllText(Path.Combine(webRoot, "index.html"));
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));
        var styles = File.ReadAllText(Path.Combine(webRoot, "styles.css"));
        var voice = File.ReadAllText(Path.Combine(webRoot, "voice.js"));
        var bridge = File.ReadAllText(Path.Combine(webRoot, "bridge.js"));

        Assert.Contains("data-tool-action=\"audioAnalysis\"", html, StringComparison.Ordinal);
        Assert.Contains("data-tool-action=\"videoAnalysis\"", html, StringComparison.Ordinal);
        Assert.Contains("data-tool-action=\"imageAnalysis\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-tool-immediate=\"screen.capture\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-tool-immediate=\"screenClip.toggle\"", html, StringComparison.Ordinal);
        Assert.Contains("beginMediaCapture(action)", app, StringComparison.Ordinal);
        Assert.Contains("if (state.documents.length > 0) return true", app, StringComparison.Ordinal);
        Assert.DoesNotContain("remove.disabled = state.isRunning", app, StringComparison.Ordinal);
        Assert.DoesNotContain(".session-delete:disabled", styles, StringComparison.Ordinal);
        Assert.Contains(".session-item:hover .session-delete", styles, StringComparison.Ordinal);
        Assert.Contains("elements.workspaceButton.classList.remove(\"active\")", app, StringComparison.Ordinal);
        Assert.Contains("post(\"audioCapture.start\", { sessionId: state.activeSessionId })", app, StringComparison.Ordinal);
        Assert.Contains("Systemaudio aufnehmen", app, StringComparison.Ordinal);
        Assert.DoesNotContain("goAnalysisAudioCapture", voice, StringComparison.Ordinal);
        Assert.DoesNotContain("audioCapture.audio", bridge, StringComparison.Ordinal);
        Assert.True(AssistantWebBridge.IsIncomingTypeAllowed("audioCapture.start"));
        Assert.True(AssistantWebBridge.IsIncomingTypeAllowed("audioCapture.stop"));
        Assert.False(AssistantWebBridge.IsIncomingTypeAllowed("audioCapture.audio"));
    }

    [Theory]
    [InlineData("Beenden")]
    [InlineData("Aufnahme beenden.")]
    [InlineData("Abschließen!")]
    [InlineData("Aufnahme abschließen")]
    public void VoiceCanFinishAnActiveMediaCaptureWithoutAnIntentModelRun(string command)
    {
        Assert.True(AssistantPage.IsMediaCaptureFinishCommand(command));
    }

    [Theory]
    [InlineData("Senden")]
    [InlineData("Prompt senden.")]
    [InlineData("  PROMPT   SENDEN!  ")]
    public void ExplicitVoiceCommandSendsTheCurrentComposerDraft(string command)
    {
        Assert.True(AssistantPage.IsVoicePromptSendCommand(command));
    }

    [Theory]
    [InlineData("Bitte diesen Text senden")]
    [InlineData("Hörbuch erstellen")]
    [InlineData("Suche im Web")]
    public void OrdinaryDictationNeverSendsItself(string dictation)
    {
        Assert.False(AssistantPage.IsVoicePromptSendCommand(dictation));
    }

    [Fact]
    public void VoiceRecognitionUsesEditableComposerDictationWithoutWhisperHotwordsOrIntentDispatch()
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));
        var voice = File.ReadAllText(Path.Combine(webRoot, "voice.js"));
        var repositoryRoot = FindRepositoryRoot();
        var worker = File.ReadAllText(Path.Combine(repositoryRoot, "workers", "speech", "app.py"));
        var page = File.ReadAllText(Path.Combine(repositoryRoot, "src", "GoWinUI.App", "Pages", "AssistantPage.xaml.cs"));
        var microphone = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "GoWinUI.App",
            "Services",
            "MicrophoneTranscriptionService.cs"));

        Assert.Contains("function updateVoiceDictation(", app, StringComparison.Ordinal);
        Assert.Contains("elements.prompt.value = turn.renderedValue", app, StringComparison.Ordinal);
        Assert.Contains("nextRevision <= turn.lastRevision", app, StringComparison.Ordinal);
        Assert.Contains("turn.manuallyConfirmed = true", app, StringComparison.Ordinal);
        Assert.Contains("payload?.isFinal && payload?.sendPrompt", app, StringComparison.Ordinal);
        Assert.Contains("void submitPrompt();", app, StringComparison.Ordinal);
        Assert.DoesNotContain("submitVoicePrompt", app, StringComparison.Ordinal);
        Assert.Contains("const bridgeFrameSamples = 1600;", voice, StringComparison.Ordinal);
        Assert.Contains("const silenceToFinishSamples = 8000;", voice, StringComparison.Ordinal);
        Assert.DoesNotContain("windowSamples", voice, StringComparison.Ordinal);
        Assert.Contains("FirstDecodeMilliseconds = 480", microphone, StringComparison.Ordinal);
        Assert.Contains("DecodeCadenceMilliseconds = 300", microphone, StringComparison.Ordinal);
        Assert.Contains("WindowMilliseconds = 6_000", microphone, StringComparison.Ordinal);
        Assert.Contains("LiveCaptionProfile.Dictation", microphone, StringComparison.Ordinal);
        Assert.Contains("_pendingDictationPartial = pending", microphone, StringComparison.Ordinal);
        Assert.Contains("_pendingDictationFinals.Enqueue(pending)", microphone, StringComparison.Ordinal);
        Assert.DoesNotContain("WHISPER_HOTWORDS", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("hotwords=", worker, StringComparison.Ordinal);
        Assert.Contains("condition_on_previous_text=not dictation", worker, StringComparison.Ordinal);
        Assert.Contains("word_timestamps=dictation", worker, StringComparison.Ordinal);
        Assert.Contains("if not dictation and temporary is not None", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("ClassifyIntentAsync", page, StringComparison.Ordinal);
        Assert.DoesNotContain("UtteranceIntent.", page, StringComparison.Ordinal);
    }

    [Fact]
    public void VoiceDictationRemainsVisibleWhileSpeechPlaybackIsActive()
    {
        var repositoryRoot = FindRepositoryRoot();
        var page = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "GoWinUI.App",
            "Pages",
            "AssistantPage.xaml.cs"));
        var app = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Web",
            "app.js"));

        Assert.Contains(
            "Whisper dictation and speech playback are independent",
            page,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "if (!_microphone.Current.IsSpeaking)",
            page,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "if (microphoneState.IsSpeaking)\r\n            {\r\n                await bridge.PostAsync(\"microphone.transcript\"",
            page,
            StringComparison.Ordinal);
        Assert.Contains(
            "Speech playback and dictation have independent lifetimes",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "setSuspended(!state.microphone?.isRecording)",
            app,
            StringComparison.Ordinal);
        var microphone = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "GoWinUI.App",
            "Services",
            "MicrophoneTranscriptionService.cs"));
        Assert.Contains("private string? _transcriptionProvider;", microphone, StringComparison.Ordinal);
        Assert.Contains("private string? _speechProvider;", microphone, StringComparison.Ordinal);
        Assert.Contains("_speaking ? _speechProvider : _transcriptionProvider", microphone, StringComparison.Ordinal);
        Assert.Contains("Sprache wird erkannt · Vorlesen läuft", microphone, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Pausieren", false, "Pause")]
    [InlineData("Vorlesen pausieren.", false, "Pause")]
    [InlineData("Fortsetzen", true, "Resume")]
    [InlineData("Weiterlesen!", true, "Resume")]
    [InlineData("Abbrechen", false, "Cancel")]
    public void VoicePlaybackCommandsAreHandledLocallyWhileSpeechIsActive(
        string command,
        bool paused,
        string expected)
    {
        var state = new MicrophoneSnapshot(
            IsRecording: true,
            IsBusy: true,
            IsSpeaking: true,
            CanPauseSpeech: true,
            IsSpeechPaused: paused,
            Status: "AI-Antwort wird vorgelesen",
            StartedAt: DateTimeOffset.UtcNow,
            Error: null,
            PartialTranscript: string.Empty,
            Provider: "supertonic-3-F5-cuda",
            DeviceLabel: "Test");

        Assert.Equal(
            expected,
            AssistantPage.ResolveVoicePlaybackControl(command, state, speechOperationActive: true).ToString());
    }

    [Theory]
    [InlineData("Sprachsteuerung abbrechen")]
    [InlineData("Sprachsteuerung beenden.")]
    [InlineData("  SPRACHSTEUERUNG   BEENDEN!  ")]
    public void ExplicitVoiceCommandsStopPersistentVoiceControl(string command)
    {
        Assert.True(AssistantPage.IsVoiceControlStopCommand(command));
    }

    [Fact]
    public void NewPromptInterruptsOnlyAnExistingChatRunAndNeverIndependentSpeech()
    {
        Assert.False(AssistantPage.ShouldCancelActiveChatBeforePrompt(
            isSpeechRequest: false,
            chatRequestInFlight: false,
            aiRunActive: false,
            speechPlaybackActive: true));
        Assert.True(AssistantPage.ShouldCancelActiveChatBeforePrompt(
            isSpeechRequest: false,
            chatRequestInFlight: true,
            aiRunActive: false,
            speechPlaybackActive: true));
        Assert.True(AssistantPage.ShouldCancelActiveChatBeforePrompt(
            isSpeechRequest: false,
            chatRequestInFlight: false,
            aiRunActive: true,
            speechPlaybackActive: true));
        Assert.False(AssistantPage.ShouldCancelActiveChatBeforePrompt(
            isSpeechRequest: true,
            chatRequestInFlight: true,
            aiRunActive: true,
            speechPlaybackActive: true));
    }

    [Theory]
    [InlineData("Abbrechen")]
    [InlineData("Vorlesen abbrechen")]
    [InlineData("Sprachsteuerung starten")]
    [InlineData("Beenden")]
    public void GenericCommandsDoNotDisablePersistentVoiceControl(string command)
    {
        Assert.False(AssistantPage.IsVoiceControlStopCommand(command));
    }

    [Fact]
    public void SpeechStatusHidesHardwareAndSelectedMessageDetails()
    {
        var app = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "Web", "app.js"));
        var styles = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "Web", "styles.css"));

        Assert.Equal("Supertonic F5 Ultra", GoAiAssistantService.DisplaySpeechProvider(null));
        Assert.Equal("Supertonic F5 Ultra", GoAiAssistantService.DisplaySpeechProvider("supertonic-3-F5-cuda"));
        Assert.Contains("setSuspended(!state.microphone?.isRecording)", app, StringComparison.Ordinal);
        Assert.DoesNotContain("Boolean(state.microphone?.isSpeaking)\n      || state.voicePlaybackPending", app, StringComparison.Ordinal);
        Assert.Contains("elements.microphone.disabled = Boolean(state.voiceStarting)", app, StringComparison.Ordinal);
        Assert.Contains("payload?.isFinal && payload?.stopVoice", app, StringComparison.Ordinal);
        Assert.DoesNotContain("microphone.isBusy && !active", app, StringComparison.Ordinal);
        Assert.Contains("const active = Boolean(browserActive || state.voiceStarting)", app, StringComparison.Ordinal);
        Assert.Contains("function resetTransientVoiceStateForSessionChange()", app, StringComparison.Ordinal);
        Assert.Contains("resetTransientVoiceStateForSessionChange();", app, StringComparison.Ordinal);
        Assert.Contains("isBusy: browserCaptureActive && Boolean(state.microphone?.isBusy)", app, StringComparison.Ordinal);
        Assert.DoesNotContain("browserVoiceActive || state.microphone?.isBusy", app, StringComparison.Ordinal);
        Assert.Contains("elements.microphone.classList.remove(\"speaking\")", app, StringComparison.Ordinal);
        Assert.DoesNotContain("classList.toggle(\"speaking\"", app, StringComparison.Ordinal);
        Assert.DoesNotContain(".microphone-button.speaking", styles, StringComparison.Ordinal);
        Assert.Contains("&& isVoiceControlActive()", app, StringComparison.Ordinal);
        Assert.Contains("const captureStop = globalThis.goVoiceCapture?.stop(false)", app, StringComparison.Ordinal);
        Assert.Contains("if (notifyHost) post(\"microphone.stop\", {})", app, StringComparison.Ordinal);
        Assert.True(
            app.IndexOf("if (notifyHost) post(\"microphone.stop\", {})", StringComparison.Ordinal)
            < app.IndexOf("await captureStop", StringComparison.Ordinal));
    }

    [Fact]
    public void AutomaticVoiceOutputRemovesMarkdownNoise()
    {
        var value = MicrophoneTranscriptionService.PrepareSpeechText(
            "## Ergebnis\n\n**Volumenstrom:** [siehe Quelle](https://example.test) | 450 m³/h");

        Assert.Equal("Ergebnis Volumenstrom: siehe Quelle , vierhundertfünfzig Kubikmeter pro Stunde", value);
    }

    [Theory]
    [InlineData(
        @"Die Druckdifferenz ist \(\Delta p = \frac{\rho}{2} \cdot v^2\).",
        "Delta p gleich Rho geteilt durch zwei mal v hoch zwei")]
    [InlineData(
        @"Volumenstrom: $$\dot{V} = A \cdot v$$",
        "V Punkt gleich A mal v")]
    [InlineData(
        @"Die Kantenlänge lautet \(\sqrt[3]{x_1}\).",
        "dritte Wurzel aus x Index eins")]
    [InlineData(
        @"Einheit: \frac{\mathrm{m}^3}{\mathrm{h}}",
        "Kubikmeter pro Stunde")]
    [InlineData(
        "Für den Druck gilt Δp = ρ · v².",
        "Delta p gleich Rho mal v hoch zwei")]
    public void AutomaticVoiceOutputSpeaksLatexAsGermanMathematics(
        string markdown,
        string expectedPhrase)
    {
        var value = MicrophoneTranscriptionService.PrepareSpeechText(markdown);

        Assert.Contains(expectedPhrase, value, StringComparison.Ordinal);
        Assert.DoesNotContain('\\', value);
        Assert.DoesNotContain('$', value);
        Assert.DoesNotContain('{', value);
        Assert.DoesNotContain('}', value);
    }

    [Theory]
    [InlineData("Der Anteil beträgt 2 %.", "Der Anteil beträgt zwei Prozent.")]
    [InlineData("Der Wert ist 3,14.", "Der Wert ist drei Komma eins vier.")]
    [InlineData("Beginn: 09:23 Uhr.", "Beginn: neun Uhr dreiundzwanzig.")]
    [InlineData("Stand 19.08.2026.", "Stand neunzehnter August zweitausendsechsundzwanzig.")]
    [InlineData("Nach DIN 1946-6.", "Nach DIN eintausendneunhundertsechsundvierzig Strich sechs.")]
    [InlineData("Leistung 12 kW bei -5 °C.", "Leistung zwölf Kilowatt bei minus fünf Grad Celsius.")]
    [InlineData("Bereich 10-12 m.", "Bereich zehn bis zwölf Meter.")]
    public void DeterministicSpeechPlanSpeaksGermanValues(
        string source,
        string expected)
    {
        Assert.Equal(expected, MicrophoneTranscriptionService.PrepareSpeechText(source));
    }

    [Fact]
    public void HiddenSpeechTextRemovesQuotationFormsAndPreservesWordApostrophes()
    {
        var value = SpeechSourceSegmentation.NormalizeSpeechPunctuation(
            "„Hallo“, «Welt» und O’Connor's Anlage.");

        Assert.Equal("Hallo, Welt und O'Connor's Anlage.", value);
        Assert.False(SpeechSourceSegmentation.ContainsForbiddenSpeechQuotation(value));
    }

    [Theory]
    [InlineData(@"\(f(x)=x\)")]
    [InlineData(@"\(x^2 + \sqrt{x}=4\)")]
    [InlineData(@"$$\int_0^1 \frac{x^2 + 1}{\sqrt{x}}\,dx = 2$$")]
    public void MathematicsUsesTheSameDeterministicPipelineAsProse(string source)
    {
        var units = SpeechSourceSegmentation.CreateUnits(source);

        var segments = SpeechSourceSegmentation.CreateDirectSegments(units);

        Assert.NotEmpty(segments);
        Assert.All(segments, static segment =>
        {
            Assert.Single(segment.SourceUnitIds);
            Assert.DoesNotContain("GOMATH", segment.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void DeterministicSpeechPlanNormalizesInlineMathematicsAndRemovesQuotes()
    {
        var units = SpeechSourceSegmentation.CreateUnits(
            @"„Die Gleichung lautet \(\Delta p = \frac{\rho}{2}\)“, erklärte Lea.");
        var prepared = SpeechSourceSegmentation.CreateDirectSegments(units);

        Assert.NotEmpty(prepared);
        Assert.Contains("Delta p gleich Rho geteilt durch zwei", string.Join(' ', prepared.Select(static segment => segment.Text)), StringComparison.Ordinal);
        Assert.All(prepared, static segment =>
        {
            Assert.False(SpeechSourceSegmentation.ContainsForbiddenSpeechQuotation(
                SpeechSourceSegmentation.PrepareForSynthesis(segment)));
        });
    }


    [Theory]
    [InlineData("Vorlesen Seite 3", 3, 3, "Seite 3")]
    [InlineData("Vorlesen Seiten 3-5", 3, 5, "Seite 3 bis 5")]
    [InlineData("Vorlesen Seiten 2 bis 4", 2, 4, "Seite 2 bis 4")]
    [InlineData("Vorlesen ab Seite 3 bis Seite 5", 3, 5, "Seite 3 bis 5")]
    [InlineData("Vorlesen ab Seite 7", 7, null, "ab Seite 7")]
    public void DocumentSpeechRecognizesGermanPageSelections(string prompt, int start, int? end, string description)
    {
        var selection = GoAiAssistantService.ParseSpeechPageSelection(prompt);

        Assert.True(selection.HasValue);
        Assert.Equal(start, selection.Value.Start);
        Assert.Equal(end, selection.Value.End);
        Assert.Equal(description, selection.Value.Description);
    }

    [Theory]
    [InlineData("Vorlesen")]
    [InlineData("Lies vor")]
    [InlineData("Lies die letzte Nachricht vor")]
    public void PlainSpeechCommandsDoNotAccidentallySelectDocumentPages(string prompt)
    {
        Assert.Null(GoAiAssistantService.ParseSpeechPageSelection(prompt));
    }


    [Fact]
    public void SpeechSourceUnitsPreserveMarkdownStructureAndStableOrder()
    {
        const string markdown = """
            # Heading

            First sentence. Second sentence!

            - List item.

            | Name | Value |
            | --- | --- |
            | Air | 42 |

            > Quoted sentence.

            $$x^2$$

            ```csharp
            Console.WriteLine(42);
            ```
            """;

        var units = SpeechSourceSegmentation.CreateUnits(markdown);

        Assert.NotEmpty(units);
        Assert.Equal(
            Enumerable.Range(1, units.Count).Select(index => $"u{index:0000}"),
            units.Select(static unit => unit.Id));
        Assert.Contains(units, static unit => unit.Kind == "heading");
        Assert.Equal(2, units.Count(static unit => unit.Kind == "paragraph"));
        Assert.Contains(units, static unit => unit.Kind == "listItem");
        Assert.Equal(2, units.Count(static unit => unit.Kind == "tableRow"));
        Assert.Contains(units, static unit => unit.Kind == "quote");
        Assert.Contains(units, static unit => unit.Kind == "math");
        Assert.Contains(units, static unit => unit.Kind == "code");
    }

    [Fact]
    public void DirectSpeechSegmentsKeepSourceMappingAndStayBelowPlaybackLimit()
    {
        var source = string.Join(' ', Enumerable.Repeat("A deliberately long clause,", 160)) + " complete.";
        var units = SpeechSourceSegmentation.CreateUnits(source);

        var segments = SpeechSourceSegmentation.CreateDirectSegments(units);

        Assert.True(segments.Count > 1);
        Assert.All(segments, segment =>
        {
            Assert.InRange(segment.Text.Length, 1, SpeechSourceSegmentation.MaximumSegmentCharacters);
            Assert.Single(segment.SourceUnitIds);
            Assert.Contains(segment.SourceUnitIds[0], units.Select(static unit => unit.Id));
        });
    }

    [Fact]
    public void LongSentenceBelowThreeThousandCharactersRemainsOneSpeechRequest()
    {
        var source = string.Join(
            ' ',
            Enumerable.Repeat("Dieser ausführliche Satzteil bleibt für eine flüssige Aussprache zusammen,", 35))
            + " und endet erst hier.";

        var units = SpeechSourceSegmentation.CreateUnits(source);
        var segment = Assert.Single(SpeechSourceSegmentation.CreateDirectSegments(units));
        var batch = Assert.Single(SpeechSourceSegmentation.CreatePlaybackBatches([segment]));

        Assert.InRange(segment.Text.Length, 301, SpeechSourceSegmentation.MaximumSegmentCharacters);
        Assert.Equal([0], batch.SegmentIndexes);
    }

    [Fact]
    public void PlaybackBatchesContainExactlyOneSentenceAndPreserveSourceOrder()
    {
        const string source = "Narration before dialogue. \"We continue together.\" Narration after dialogue.\n\nSecond paragraph starts here. \"Ready.\"";
        var units = SpeechSourceSegmentation.CreateUnits(source);
        var segments = SpeechSourceSegmentation.CreateDirectSegments(units);

        var batches = SpeechSourceSegmentation.CreatePlaybackBatches(segments);

        Assert.Equal(segments.Count, batches.Count);
        Assert.All(batches, static batch => Assert.Single(batch.SegmentIndexes));
        Assert.Equal(Enumerable.Range(0, segments.Count), batches.SelectMany(static batch => batch.SegmentIndexes));
        Assert.Contains(segments, static segment => segment.Text.Contains("We continue together", StringComparison.Ordinal));
        Assert.Equal(batches.Count, batches.Select(static batch => batch.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void PlaybackNormalizationPreservesBatchAcrossLongSegmentSplits()
    {
        var longText = string.Join(' ', Enumerable.Repeat("A long sentence fragment,", 160)) + ".";
        var normalized = SpeechSourceSegmentation.NormalizePreparedSegments([
            new PreparedSpeechSegment(
                "source",
                longText,
                ["u0001"],
                PlaybackBatchId: "paragraph-1"),
        ]);

        Assert.True(normalized.Count > 1);
        Assert.All(normalized, static segment => Assert.Equal("paragraph-1", segment.PlaybackBatchId));
        var batches = SpeechSourceSegmentation.CreatePlaybackBatches(normalized);
        Assert.Equal(normalized.Count, batches.Count);
        Assert.All(batches, static batch => Assert.Single(batch.SegmentIndexes));
        Assert.Equal(Enumerable.Range(0, normalized.Count), batches.SelectMany(static batch => batch.SegmentIndexes));
        Assert.All(batches.Take(batches.Count - 1), static batch => Assert.Equal(40, batch.PauseAfterMilliseconds));
        Assert.Equal(180, batches[^1].PauseAfterMilliseconds);
    }

    [Theory]
    [InlineData("Natascha ließ das Steuer sanft nach vorne gleiten und spürte, wie")]
    [InlineData("Natascha ließ das Steuer sanft nach vorne gleiten und spürte, wie, “")]
    [InlineData("„Alles bleibt ruhig“, sagte Natascha, „während wir weiterfliegen.“")]
    public void PlaybackNormalizationNeverCreatesEmptySegmentsAtCommasOrQuotes(string source)
    {
        var units = SpeechSourceSegmentation.CreateUnits(source);
        var direct = SpeechSourceSegmentation.CreateDirectSegments(units, source);
        var playable = SpeechSourceSegmentation.NormalizePreparedSegments(direct);

        Assert.NotEmpty(playable);
        Assert.All(playable, segment =>
            Assert.False(string.IsNullOrWhiteSpace(
                SpeechSourceSegmentation.PrepareForSynthesis(segment))));
        Assert.Contains(
            playable,
            segment => segment.Text.Contains("Natascha", StringComparison.Ordinal));
    }

    [Fact]
    public void PlaybackNormalizationDropsPunctuationOnlyTechnicalFragments()
    {
        var playable = SpeechSourceSegmentation.NormalizePreparedSegments([
            new PreparedSpeechSegment("text", "Ein hörbarer Satz,", ["u0001"]),
            new PreparedSpeechSegment("punctuation", "“,", ["u0001"]),
        ]);

        var segment = Assert.Single(playable);
        Assert.Equal("Ein hörbarer Satz", segment.Text);
        Assert.Equal("Ein hörbarer Satz", SpeechSourceSegmentation.PrepareForSynthesis(segment));
    }

    [Fact]
    public void QuotedSentenceEndingMapsToOneVisibleSentenceAtATime()
    {
        const string source = "„Wir stabilisieren das System.“ Meine Stimme blieb ruhig. „Dann fliegen wir weiter.“";

        var units = SpeechSourceSegmentation.CreateUnits(source);
        var segments = SpeechSourceSegmentation.CreateDirectSegments(units, source);

        Assert.Equal(3, units.Count);
        Assert.Equal(3, segments.Count);
        Assert.Equal([0, 1, 2], units.Select(static unit => unit.OrdinalInBlock));
        Assert.All(segments, static segment => Assert.Single(segment.SourceUnitIds));
        Assert.Equal(
            units.Select(static unit => unit.Id),
            segments.Select(static segment => segment.SourceUnitIds.Single()));
    }

    [Fact]
    public void SpeechProgressBridgeCarriesVisibleSourceMappingWithoutSpokenRewrite()
    {
        var sessionId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var unit = Assert.Single(SpeechSourceSegmentation.CreateUnits("Visible original sentence."));
        var playbackId = Guid.NewGuid();
        var payload = SpeechPlaybackProgressBridge.ToPayload(new(
            sessionId,
            messageId,
            "AI-Nachricht",
            playbackId,
            7,
            0,
            1,
            [unit.Id],
            SpeechPlaybackState.Playing,
            [unit]));

        var json = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);

        Assert.Contains($"\"sessionId\":\"{sessionId:D}\"", json, StringComparison.Ordinal);
        Assert.Contains($"\"sourceMessageId\":\"{messageId:D}\"", json, StringComparison.Ordinal);
        Assert.Contains($"\"playbackId\":\"{playbackId:D}\"", json, StringComparison.Ordinal);
        Assert.Contains("\"eventSequence\":7", json, StringComparison.Ordinal);
        Assert.Contains("\"state\":\"playing\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sourceUnitIds\":[\"u0001\"]", json, StringComparison.Ordinal);
        Assert.Contains("Visible original sentence.", json, StringComparison.Ordinal);
        Assert.DoesNotContain("speechText", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WebSpeechProgressHighlightsSourcesWithoutAutomaticScrolling()
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));
        var bridge = File.ReadAllText(Path.Combine(webRoot, "bridge.js"));
        var styles = File.ReadAllText(Path.Combine(webRoot, "styles.css"));

        Assert.Contains("case \"speech.progress\":", app, StringComparison.Ordinal);
        Assert.Contains("updateSpeechProgress(payload)", app, StringComparison.Ordinal);
        Assert.Contains("CSS.highlights.set", app, StringComparison.Ordinal);
        Assert.Contains("speechSourceRangeMap(content, sourceUnits)", app, StringComparison.Ordinal);
        Assert.Contains("activeSourceUnitIds.slice(0, 1)", app, StringComparison.Ordinal);
        Assert.Contains("incomingPlaybackId !== currentPlaybackId", app, StringComparison.Ordinal);
        Assert.Contains("if (!payload?.isSpeaking || !wasSpeaking || payload?.error)", app, StringComparison.Ordinal);
        Assert.Contains("previousScrollTop", app, StringComparison.Ordinal);
        Assert.DoesNotContain("scrollIntoView", app, StringComparison.Ordinal);
        Assert.Contains("::highlight(go-speech-current)", styles, StringComparison.Ordinal);
        Assert.Contains("background-color: Highlight", styles, StringComparison.Ordinal);
        Assert.Contains("\"speech.progress\"", bridge, StringComparison.Ordinal);
        Assert.True(AssistantWebBridge.IsOutgoingTypeAllowed("speech.progress"));
        Assert.True(AssistantWebBridge.IsOutgoingTypeAllowed("chat.removed"));
    }

    [Fact]
    public void ArtifactImagePreviewUsesAnEmbeddedPngDataUrl()
    {
        byte[] png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

        var url = AssistantArtifactPreviewService.BuildImageDataUrl(png);

        Assert.StartsWith("data:image/png;base64,", url, StringComparison.Ordinal);
        Assert.Equal(png, Convert.FromBase64String(url[(url.IndexOf(',', StringComparison.Ordinal) + 1)..]));
    }

    [Fact]
    public async Task ArtifactImagesCanBeMaterializedAndOpenedThroughTheBridge()
    {
        Assert.True(AssistantWebBridge.IsIncomingTypeAllowed("artifact.open"));
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var bridge = File.ReadAllText(Path.Combine(webRoot, "bridge.js"));
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));
        Assert.Contains("\"artifact.open\"", bridge, StringComparison.Ordinal);
        Assert.Contains("post(\"artifact.open\", { artifactId: artifact.id })", app, StringComparison.Ordinal);

        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Bild öffnen");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Bild", MessageStatus.Completed);
        byte[] png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
        var sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(png)).ToLowerInvariant();
        await using var source = new MemoryStream(png, writable: false);
        var artifact = await environment.Get<IChatArtifactRepository>().ImportAsync(
            message.Id,
            "test-image",
            "plot.png",
            "image/png",
            sha256,
            png.Length,
            "coding-campaign",
            null,
            source);
        var cacheRoot = Path.Combine(environment.Directory, "preview-cache");
        using var previews = new AssistantArtifactPreviewService(
            environment.Get<IChatArtifactRepository>(),
            environment.Get<IBinaryObjectStore>(),
            cacheRoot);

        var path = await previews.MaterializeOriginalAsync(artifact.Id, CancellationToken.None);

        Assert.Equal(Path.Combine(cacheRoot, artifact.Id.ToString("N"), "original.png"), path);
        Assert.Equal(png, await File.ReadAllBytesAsync(path));
    }


    [Fact]
    public async Task ArtifactAudioPreviewIsMaterializedForTheStaticWebViewHost()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Audio-Vorschau");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.User, "Audio", MessageStatus.Completed);
        var wave = SystemAudioAnalysisCaptureService.CreateWave(new byte[SystemAudioAnalysisCaptureService.SampleRate * sizeof(short)]);
        var sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(wave)).ToLowerInvariant();
        await using var source = new MemoryStream(wave, writable: false);
        var artifact = await environment.Get<IChatArtifactRepository>().ImportAsync(
            message.Id,
            "test-audio",
            "GO-Systemaudio-Test.wav",
            "audio/wav",
            sha256,
            wave.Length,
            "screen-capture",
            null,
            source);
        var cacheRoot = Path.Combine(environment.Directory, "preview-cache");
        using var previews = new AssistantArtifactPreviewService(
            environment.Get<IChatArtifactRepository>(),
            environment.Get<IBinaryObjectStore>(),
            cacheRoot);

        var preview = await previews.PrepareAsync(artifact.Id, CancellationToken.None);

        Assert.Equal($"https://{AssistantArtifactPreviewService.VirtualHost}/{artifact.Id:N}/media.wav", preview.Url);
        var cached = Path.Combine(cacheRoot, artifact.Id.ToString("N"), "media.wav");
        Assert.True(File.Exists(cached));
        Assert.Equal(wave, await File.ReadAllBytesAsync(cached));
    }

    [Fact]
    public void WebViewAllowsStaticAudioAndVideoPreviewResources()
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var html = File.ReadAllText(Path.Combine(webRoot, "index.html"));
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));

        Assert.Contains("media-src 'self' https://go-preview.local", html, StringComparison.Ordinal);
        Assert.Contains("mediaType.startsWith(\"audio/\") || mediaType.startsWith(\"video/\")", app, StringComparison.Ordinal);
        Assert.Contains("audio.src = payload.url", app, StringComparison.Ordinal);
        Assert.Contains("video.src = payload.url", app, StringComparison.Ordinal);
        Assert.Contains("Audio speichern", app, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalChatReceivesNoImageOrMediaTool()
    {
        var tools = GoAiAssistantService.GetAllowedServerTools(null);
        Assert.DoesNotContain("image.generate", tools);
        Assert.DoesNotContain("media.analyze", tools);
        Assert.DoesNotContain("web.search", tools);
    }

    [Fact]
    public void DocumentAnswersKeepInlineCitationsButRemoveTheEvidenceFooter()
    {
        const string response = "Die XREF-Vorlage wird im Projekt geladen [Anleitung_C.A.T.S.pdf, S. 12].\n\n"
            + "**Verwendete Dokumentbelege:** [Anleitung_C.A.T.S.pdf, S. 1]; [Anleitung_C.A.T.S.pdf, S. 12]";

        var cleaned = GoAiAssistantService.RemoveDocumentEvidenceFooter(response);

        Assert.Equal("Die XREF-Vorlage wird im Projekt geladen [Anleitung_C.A.T.S.pdf, S. 12].", cleaned);
    }

    [Fact]
    public void ToolOnlyAssistantCardsAreExcludedFromTheNextServerRunHistory()
    {
        var sessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        ChatMessage[] history =
        [
            new(Guid.NewGuid(), sessionId, ChatRole.User, "Warum wirken manche Möbel altmodisch?", MessageStatus.Completed, now, now),
            new(Guid.NewGuid(), sessionId, ChatRole.Assistant, "Das hat mehrere kulturelle und wirtschaftliche Ursachen.", MessageStatus.Completed, now, now),
            new(Guid.NewGuid(), sessionId, ChatRole.User, "Lies die ausgewählte Nachricht vor", MessageStatus.Completed, now, now),
            new(
                Guid.NewGuid(),
                sessionId,
                ChatRole.Assistant,
                string.Empty,
                MessageStatus.Completed,
                now,
                now,
                ToolExecution: new ToolExecutionInfo("Vorlesen", "AI-Nachricht", "Abgeschlossen")),
            new(Guid.NewGuid(), sessionId, ChatRole.User, "Erkläre den genannten Punkt genauer.", MessageStatus.Completed, now, now),
        ];

        var messages = GoAiAssistantService.BuildHistoryMessages(history, 400_000);

        Assert.Equal(4, messages.Count);
        Assert.Equal("Erkläre den genannten Punkt genauer.", Assert.Single(messages[^1].Content).Text);
        Assert.DoesNotContain(
            messages.SelectMany(static message => message.Content),
            static part => string.IsNullOrWhiteSpace(part.Text)
                && string.IsNullOrWhiteSpace(part.UploadId)
                && string.IsNullOrWhiteSpace(part.ArtifactId));
    }

    [Fact]
    public void ComposerClearsOneShotToolsButKeepsEveryPersistentSessionToolAfterTerminalRunState()
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));

        Assert.Contains("new Set([\"code\", \"bricsCad\", \"audiobook\"])", app, StringComparison.Ordinal);
        Assert.Contains("!persistentToolActions.has(state.selectedToolAction)", app, StringComparison.Ordinal);
        Assert.Contains("clearCompletedOneShotToolAction();", app, StringComparison.Ordinal);
        Assert.Contains("case \"chat.completed\":", app, StringComparison.Ordinal);
        Assert.Contains("case \"chat.cancelled\":", app, StringComparison.Ordinal);
        Assert.Contains("case \"chat.failed\":", app, StringComparison.Ordinal);
    }

    [Fact]
    public void ComposerCollapsesTwoOrMoreAttachedFilesIntoAnUpwardOverlayMenu()
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));
        var css = File.ReadAllText(Path.Combine(webRoot, "styles.css"));

        Assert.Contains("if (attachedFiles.length < 2)", app, StringComparison.Ordinal);
        Assert.Contains("summary.className = \"active-tool-chip attachment-summary\"", app, StringComparison.Ordinal);
        Assert.Contains("menu.className = \"attachment-menu\"", app, StringComparison.Ordinal);
        Assert.Contains("removeAll.className = \"attachment-summary__remove active-tool-chip__remove\"", app, StringComparison.Ordinal);
        Assert.Contains("Alle Dateianhänge entfernen", app, StringComparison.Ordinal);
        Assert.Contains("document-preparation-status", app, StringComparison.Ordinal);
        Assert.Contains("document.import.started", app, StringComparison.Ordinal);
        Assert.Contains("wird verarbeitet", app, StringComparison.Ordinal);
        Assert.Contains("@keyframes document-status-spin", css, StringComparison.Ordinal);
        Assert.Contains("bottom: calc(100% + 8px)", css, StringComparison.Ordinal);
        Assert.Contains("position: absolute", css, StringComparison.Ordinal);
    }

    [Fact]
    public void RunningMessageShowsItsModelAndPreservesContextWhenUpdatesOmitTokenCounts()
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));

        Assert.Contains("runStatusText(liveStatus)", app, StringComparison.Ordinal);
        Assert.Contains("uniqueStatusParts(status, model ? `Modell: ${model}` : null, detail)", app, StringComparison.Ordinal);
        Assert.Contains("cleanStatusMetadata", app, StringComparison.Ordinal);
        Assert.DoesNotContain("Kontexttoken", app, StringComparison.Ordinal);
        Assert.Contains("if (Number.isFinite(payload.contextUsed))", app, StringComparison.Ordinal);
        Assert.Contains("function visibleModelLabel(value)", app, StringComparison.Ordinal);
        Assert.Contains("model.replace(/\\s*·\\s*MXFP4", app, StringComparison.Ordinal);
        Assert.Contains("state.messageRunStatus.clear();", app, StringComparison.Ordinal);
        Assert.Contains("const acceptsRunStatus =", app, StringComparison.Ordinal);
        Assert.Contains("!isTerminalMessageStatus(statusMessage?.status)", app, StringComparison.Ordinal);
    }

    [Fact]
    public void SpeechUsesComposerStatusWithoutRetryOrChatRendering()
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var html = File.ReadAllText(Path.Combine(webRoot, "index.html"));
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));
        var bridge = File.ReadAllText(Path.Combine(webRoot, "bridge.js"));

        Assert.Contains("id=\"composer-speech-status\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"composer-speech-pause\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"composer-speech-stop\"", html, StringComparison.Ordinal);
        Assert.Contains("title=\"Vorlesen stoppen\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("composer-speech-previous", html, StringComparison.Ordinal);
        Assert.DoesNotContain("composer-speech-skip", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Vorheriger Absatz", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Absatz überspringen", html, StringComparison.Ordinal);
        Assert.DoesNotContain("composer-speech-pause-label", html, StringComparison.Ordinal);
        Assert.True(
            html.IndexOf("id=\"composer-speech-status\"", StringComparison.Ordinal)
            < html.IndexOf("<div class=\"composer\">", StringComparison.Ordinal));
        Assert.Contains("case \"speech.status\":", app, StringComparison.Ordinal);
        Assert.Contains("renderSpeechStatus();", app, StringComparison.Ordinal);
        Assert.Contains("post(\"microphone.toggleSpeechPause\"", app, StringComparison.Ordinal);
        Assert.Contains("elements.composerSpeechStop.addEventListener", app, StringComparison.Ordinal);
        Assert.Contains("post(\"microphone.stopSpeech\", {});", app, StringComparison.Ordinal);
        Assert.Contains("const canStop = state.isRunning || campaignRunning;", app, StringComparison.Ordinal);
        var promptStop = app.IndexOf("elements.stop.addEventListener", StringComparison.Ordinal);
        var speechStop = app.IndexOf("elements.composerSpeechStop.addEventListener", StringComparison.Ordinal);
        Assert.True(promptStop >= 0 && speechStop > promptStop);
        Assert.DoesNotContain(
            "microphone.stopSpeech",
            app[promptStop..speechStop],
            StringComparison.Ordinal);
        Assert.Contains("\"chat.removed\"", app, StringComparison.Ordinal);
        Assert.DoesNotContain("microphone.previousSpeechParagraph", app, StringComparison.Ordinal);
        Assert.DoesNotContain("microphone.skipSpeechParagraph", app, StringComparison.Ordinal);
        Assert.Contains("isPaused ? \"Fortsetzen\" : \"Pausieren\"", app, StringComparison.Ordinal);
        Assert.Contains("messageId: payload.message.id", app, StringComparison.Ordinal);
        Assert.Contains("sessionId: payload.message.sessionId", app, StringComparison.Ordinal);
        Assert.Contains("\"speech.status\"", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("Erneut senden", app, StringComparison.Ordinal);
        Assert.DoesNotContain("retryMessage", app, StringComparison.Ordinal);
        Assert.DoesNotContain("createMessageFooterLink(\"Fortsetzen\"", app, StringComparison.Ordinal);
        Assert.DoesNotContain("function continueMessage()", app, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadAloudRemainsIndependentAndScrollIsPersistedOnlyAcrossSessionChanges()
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));

        Assert.Contains("const canStop = state.isRunning || campaignRunning;", app, StringComparison.Ordinal);
        Assert.Contains("if (sessionChanged && previousSessionId) persistSessionScrollPosition(previousSessionId);", app, StringComparison.Ordinal);
        Assert.Contains("if (sessionChanged) restoreSessionScrollPosition(state.activeSessionId);", app, StringComparison.Ordinal);
        Assert.Contains("sessionScrollStoragePrefix", app, StringComparison.Ordinal);
        Assert.Contains("anchorMessageId", app, StringComparison.Ordinal);
        Assert.Contains("renderMessages(currentSessionMessagesChanged);", app, StringComparison.Ordinal);
        Assert.Contains("renderMessages(messagesChanged);", app, StringComparison.Ordinal);
        Assert.Contains("renderMessages(true);", app, StringComparison.Ordinal);
        Assert.DoesNotContain("scheduleSessionScrollSave", app, StringComparison.Ordinal);
        Assert.DoesNotContain("elements.messageScroll.addEventListener(\"scroll\"", app, StringComparison.Ordinal);
        Assert.DoesNotContain("addEventListener(\"pagehide\", () => persistSessionScrollPosition", app, StringComparison.Ordinal);
        Assert.DoesNotContain("preserveForSpeech", app, StringComparison.Ordinal);
        Assert.Contains("&& isVoiceControlActive()", app, StringComparison.Ordinal);
        Assert.DoesNotContain("payload.message?.contentProfile !== \"audiobook\"", app, StringComparison.Ordinal);
    }

    [Fact]
    public void SpeechAnchorSelectsTheRequestedBlockAndEverythingAfterIt()
    {
        var units = SpeechSourceSegmentation.CreateUnits("Erster Absatz.\n\nZweiter Absatz.\n\nDritter Absatz.");
        var selected = GoAiAssistantService.SelectSpeechUnitsFromAnchor(
            units,
            new SpeechStartAnchor("paragraph", 1));

        Assert.DoesNotContain(selected, unit => unit.Text.Contains("Erster", StringComparison.Ordinal));
        Assert.Contains(selected, unit => unit.Text.Contains("Zweiter", StringComparison.Ordinal));
        Assert.Contains(selected, unit => unit.Text.Contains("Dritter", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadFromHereOnlyAcceptsStableAuthoritativeAssistantBlocks()
    {
        var sessionId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var updatedAt = DateTimeOffset.UtcNow;
        var anchor = new SpeechStartAnchor("paragraph", 0);
        foreach (var status in new[]
                 {
                     MessageStatus.Completed,
                     MessageStatus.Cancelled,
                     MessageStatus.Interrupted,
                     MessageStatus.Failed,
                 })
        {
            var message = new ChatMessage(
                messageId,
                sessionId,
                ChatRole.Assistant,
                "Ein stabil gespeicherter Absatz.",
                status,
                updatedAt.AddMinutes(-1),
                updatedAt);
            GoAiAssistantService.ValidateAnchoredSpeechMessage(
                message,
                sessionId,
                updatedAt,
                anchor);
        }

        var streaming = new ChatMessage(
            messageId,
            sessionId,
            ChatRole.Assistant,
            "Noch nicht stabil.",
            MessageStatus.Streaming,
            updatedAt.AddMinutes(-1),
            updatedAt);
        Assert.Throws<InvalidOperationException>(() =>
            GoAiAssistantService.ValidateAnchoredSpeechMessage(
                streaming,
                sessionId,
                updatedAt,
                anchor));
        Assert.Throws<InvalidOperationException>(() =>
            GoAiAssistantService.ValidateAnchoredSpeechMessage(
                streaming with { Status = MessageStatus.Completed },
                sessionId,
                updatedAt.AddSeconds(1),
                anchor));
    }

    [Fact]
    public void FooterSpeechAcceptsEveryStableStoredAssistantMessage()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var status in new[]
                 {
                     MessageStatus.Completed,
                     MessageStatus.Cancelled,
                     MessageStatus.Interrupted,
                     MessageStatus.Failed,
                 })
        {
            var message = new ChatMessage(
                Guid.NewGuid(),
                Guid.NewGuid(),
                ChatRole.Assistant,
                "Der Coding-Workflow ist geladen und startet gestoppt. Senden startet ihn erneut.",
                status,
                now,
                now);
            Assert.True(GoAiAssistantService.IsReadableSpeechMessage(message));
        }

        var invalid = new ChatMessage(
            Guid.NewGuid(),
            Guid.NewGuid(),
            ChatRole.Assistant,
            "Noch nicht stabil.",
            MessageStatus.Streaming,
            now,
            now);
        Assert.False(GoAiAssistantService.IsReadableSpeechMessage(invalid));
        Assert.False(GoAiAssistantService.IsReadableSpeechMessage(
            invalid with { Role = ChatRole.User, Status = MessageStatus.Completed }));
        Assert.False(GoAiAssistantService.IsReadableSpeechMessage(
            invalid with { Content = string.Empty, Status = MessageStatus.Completed }));
    }

    [Fact]
    public void WebViewAnnotatesReadableBlocksForTheNativeReadFromHereMenu()
    {
        var target = new ReadFromContextTarget(
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            "tableRow",
            2);
        Assert.True(AssistantWebBridge.IsValidReadFromContextTarget(target));
        Assert.False(AssistantWebBridge.IsValidReadFromContextTarget(target with { Kind = "button" }));
        Assert.False(AssistantWebBridge.IsIncomingTypeAllowed("microphone.previousSpeechParagraph"));
        Assert.False(AssistantWebBridge.IsIncomingTypeAllowed("microphone.skipSpeechParagraph"));

        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));
        Assert.Contains("annotateReadableSpeechBlocks(message, article, content)", app, StringComparison.Ordinal);
        Assert.Contains("globalThis.goGetReadFromContextTarget", app, StringComparison.Ordinal);
        Assert.Contains("data-speech-block-kind", app, StringComparison.Ordinal);
        Assert.Contains("messageUpdatedAt", app, StringComparison.Ordinal);
    }

    [Fact]
    public void WebViewRendersAuthoritativeCodingDiffsWithoutHtmlInjection()
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));
        var styles = File.ReadAllText(Path.Combine(webRoot, "styles.css"));

        Assert.Contains("case \"chat.codeDiff\"", app, StringComparison.Ordinal);
        Assert.Contains("function createCodeDiff(message, force = false)", app, StringComparison.Ordinal);
        Assert.Contains("row.textContent", app, StringComparison.Ordinal);
        Assert.Contains("Git-Diff kopieren", app, StringComparison.Ordinal);
        Assert.Contains(".message-code-diff", styles, StringComparison.Ordinal);
        Assert.Contains(".diff-line--added", styles, StringComparison.Ordinal);
        Assert.Contains(".diff-line--deleted", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void MediaTransportButtonsMapToSpeechControls()
    {
        Assert.Equal(
            SpeechMediaTransportCommand.Play,
            SpeechMediaTransportController.ResolveCommand(Windows.Media.SystemMediaTransportControlsButton.Play));
        Assert.Equal(
            SpeechMediaTransportCommand.Pause,
            SpeechMediaTransportController.ResolveCommand(Windows.Media.SystemMediaTransportControlsButton.Pause));
        Assert.Equal(
            SpeechMediaTransportCommand.None,
            SpeechMediaTransportController.ResolveCommand(Windows.Media.SystemMediaTransportControlsButton.Next));
        Assert.Equal(
            SpeechMediaTransportCommand.None,
            SpeechMediaTransportController.ResolveCommand(Windows.Media.SystemMediaTransportControlsButton.Previous));
    }

    [Fact]
    public void MediaTransportControllerRegistersAndReleasesNativeControls()
    {
        using var controller = new SpeechMediaTransportController(
            _ => Task.CompletedTask,
            exception => throw new InvalidOperationException("Die Windows-Mediensteuerung ist fehlgeschlagen.", exception));

        controller.Activate();
        controller.SetPlaying(paused: false);
        controller.SetPlaying(paused: true);
        controller.Deactivate();
    }

    [Fact]
    public void AudiobookToolRemainsPersistentInTheComposer()
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var html = File.ReadAllText(Path.Combine(webRoot, "index.html"));
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));

        Assert.Contains("data-tool-action=\"audiobook\"", html, StringComparison.Ordinal);
        Assert.Contains("Hörbuch erstellen", html, StringComparison.Ordinal);
        Assert.Contains("audiobook: [\"Hörbuch erstellen\"", app, StringComparison.Ordinal);
        Assert.DoesNotContain("payload.message?.contentProfile !== \"audiobook\"", app, StringComparison.Ordinal);
    }

    [Fact]
    public void AudiobookContextIncludesInterruptedStoryTextButExcludesEarlierGeneralConversation()
    {
        var sessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var generalUser = new ChatMessage(Guid.NewGuid(), sessionId, ChatRole.User, "Allgemeine Frage", MessageStatus.Completed, now, now);
        var generalAnswer = new ChatMessage(Guid.NewGuid(), sessionId, ChatRole.Assistant, "Allgemeine Antwort", MessageStatus.Completed, now, now);
        var storyPrompt = new ChatMessage(Guid.NewGuid(), sessionId, ChatRole.User, "Eine Geschichte im Maschinenraum", MessageStatus.Completed, now.AddSeconds(1), now.AddSeconds(1));
        var firstChapter = new ChatMessage(
            Guid.NewGuid(), sessionId, ChatRole.Assistant, "Im Maschinenraum begann die Reise.", MessageStatus.Completed,
            now.AddSeconds(2), now.AddSeconds(2), ContentProfile: MessageContentProfile.Audiobook);
        var direction = new ChatMessage(Guid.NewGuid(), sessionId, ChatRole.User, "Fortsetzen: Ein Alarm ertönt.", MessageStatus.Completed, now.AddSeconds(3), now.AddSeconds(3));
        var interrupted = new ChatMessage(
            Guid.NewGuid(), sessionId, ChatRole.Assistant, "Plötzlich heulte die Sirene", MessageStatus.Interrupted,
            now.AddSeconds(4), now.AddSeconds(4), ContentProfile: MessageContentProfile.Audiobook);

        var eligible = SessionContextPreparationService.SelectEligibleHistory(
            [generalUser, generalAnswer, storyPrompt, firstChapter, direction, interrupted],
            SessionContextProfile.Audiobook);

        Assert.Equal([storyPrompt.Id, firstChapter.Id, direction.Id, interrupted.Id], eligible.Select(static item => item.Id));
    }

    [Fact]
    public void AudiobookPromptCreatesOneChapterAndContinuationStartsAtTheLatestScene()
    {
        var first = AssistantCoordinator.CreateToolMatch(
            "audiobook",
            "Eine Ingenieurin entdeckt unter Berlin einen verlassenen Maschinenraum.");
        var continuation = AssistantCoordinator.CreateToolMatch(
            "audiobook",
            "Fortsetzen: Der Generator springt unerwartet an.");

        var firstPrompt = GoAiAssistantService.BuildAudiobookPrompt(first, first.OriginalPrompt, hasAudiobookHistory: false);
        var nextPrompt = GoAiAssistantService.BuildAudiobookPrompt(continuation, continuation.OriginalPrompt, hasAudiobookHistory: true);

        Assert.Contains("erste Kapitel", firstPrompt, StringComparison.Ordinal);
        Assert.Contains("# Kapitel eins – Titel", firstPrompt, StringComparison.Ordinal);
        Assert.Contains("eintausendfünfhundert bis zweitausendfünfhundert Wörter", firstPrompt, StringComparison.Ordinal);
        Assert.Contains("zwei Prozent", firstPrompt, StringComparison.Ordinal);
        Assert.Contains("Hauptfigur", firstPrompt, StringComparison.Ordinal);
        Assert.Contains("langfristigen Leitfaden", firstPrompt, StringComparison.Ordinal);
        Assert.Contains("zukünftige Handlungsfäden", firstPrompt, StringComparison.Ordinal);
        Assert.Contains("unmittelbar letzte Szene", nextPrompt, StringComparison.Ordinal);
        Assert.Contains("langfristigen Serienleitfaden", nextPrompt, StringComparison.Ordinal);
        Assert.Contains("Perspektive der Hauptfigur", nextPrompt, StringComparison.Ordinal);
        Assert.Contains("neuer AI-Lauf ist ausdrücklich keine Kapitelgrenze", nextPrompt, StringComparison.Ordinal);
        Assert.Contains("ohne neue Kapitelüberschrift", nextPrompt, StringComparison.Ordinal);
        Assert.Contains("tatsächlich ein neues Kapitel beginnt", nextPrompt, StringComparison.Ordinal);
        Assert.Contains("zwei Prozent", nextPrompt, StringComparison.Ordinal);
        Assert.Contains("Der Generator springt unerwartet an.", nextPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Fortsetzen:", nextPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacySpeechToolMessagesAreExcludedFromPersistentSessionContext()
    {
        var sessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var speechCommand = new ChatMessage(
            Guid.NewGuid(), sessionId, ChatRole.User, "Lies die ausgewählte Nachricht vor",
            MessageStatus.Completed, now, now);
        var legacySpeechCard = new ChatMessage(
            Guid.NewGuid(), sessionId, ChatRole.Assistant, string.Empty,
            MessageStatus.Completed, now.AddSeconds(1), now.AddSeconds(1),
            ToolExecution: new ToolExecutionInfo("Vorlesen", "AI-Nachricht", "Abgeschlossen"));
        var normalUser = new ChatMessage(
            Guid.NewGuid(), sessionId, ChatRole.User, "Wie hoch ist der Volumenstrom?",
            MessageStatus.Completed, now.AddSeconds(2), now.AddSeconds(2));
        var normalAssistant = new ChatMessage(
            Guid.NewGuid(), sessionId, ChatRole.Assistant, "4.200 m³/h.",
            MessageStatus.Completed, now.AddSeconds(3), now.AddSeconds(3));

        var eligible = SessionContextPreparationService.SelectEligibleHistory(
            [speechCommand, legacySpeechCard, normalUser, normalAssistant]);

        Assert.Equal([normalUser.Id, normalAssistant.Id], eligible.Select(static item => item.Id));
    }

    [Fact]
    public void DocumentHistoryReserveAndBlockTargetsScaleWithAvailableHistory()
    {
        var sessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var longMessage = new ChatMessage(
            Guid.NewGuid(), sessionId, ChatRole.Assistant, new string('x', 60_000),
            MessageStatus.Completed, now, now);

        Assert.Equal(1_024, GoAiAssistantService.CalculateDocumentHistoryReserveTokens([]));
        Assert.InRange(
            GoAiAssistantService.CalculateDocumentHistoryReserveTokens([longMessage]),
            4_096,
            16_384);
        Assert.True(SessionContextPreparationService.CalculateBlockSummaryTarget(4_000, 4) < 4_000);
    }

    [Fact]
    public void SseReconnectBudgetResetsAfterPersistedEventProgress()
    {
        Assert.Equal(4, GoAiAssistantService.ReconnectAttemptsAfterProgress(4, 120, 120));
        Assert.Equal(0, GoAiAssistantService.ReconnectAttemptsAfterProgress(4, 120, 121));
    }

    private static ChatMessage Message(Guid sessionId, string content) => new(
        Guid.NewGuid(), sessionId, ChatRole.Assistant, content, MessageStatus.Completed,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Theory]
    [InlineData("Person 1: Die Lüftungsanlage läuft.", null, "**Live-Untertitel**", "Die Lüftungsanlage läuft")]
    [InlineData("Person 1: Die Lüftungsanlage läuft.\nPerson 2: Ich prüfe den Volumenstrom.", null, "**Live-Untertitel**", "Person 1: Die Lüftungsanlage läuft.\n\nPerson 2: Ich prüfe den Volumenstrom.")]
    [InlineData("", "GO AI Server ist nicht erreichbar.", "**Live-Untertitel fehlgeschlagen**", "GO AI Server ist nicht erreichbar")]
    [InlineData("", null, "**Live-Untertitel**", "Es wurde kein Sprachinhalt erkannt")]
    public async Task CompletedCaptionStatesBecomePersistentChatMessages(
        string transcript,
        string? error,
        string expectedTitle,
        string expectedDetail)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        using var coordinator = CreateCoordinator(environment, settings, CreateRecentActivity(settings));

        await coordinator.AddLiveCaptionResultAsync(transcript, error);

        var sessionId = Assert.IsType<Guid>(settings.Current.ActiveSessionId);
        var message = Assert.Single(await environment.Get<IChatRepository>().ListMessagesAsync(sessionId));
        Assert.Equal(ChatRole.Assistant, message.Role);
        Assert.Equal(MessageStatus.Completed, message.Status);
        Assert.Contains(expectedTitle, message.Content, StringComparison.Ordinal);
        Assert.Contains(expectedDetail, message.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildingSessionSnapshotDoesNotContactLocalAi()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var localAi = new UnexpectedLmStudioClient();
        using var coordinator = CreateCoordinator(environment, settings, CreateRecentActivity(settings), localAi);

        _ = await coordinator.BuildSnapshotAsync();

        Assert.Equal(0, localAi.ListModelsCallCount);
    }

    [Fact]
    public async Task SelectingWorkflowInsertsVisibleMessageWithoutSessionAttachment()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var recentActivity = CreateRecentActivity(settings);
        using var coordinator = CreateCoordinator(environment, settings, recentActivity);
        var workflows = await environment.Get<IWorkflowRepository>().ListAsync();
        var workflow = workflows[0];
        using var payloadDocument = JsonDocument.Parse(JsonSerializer.Serialize(new { workflowId = workflow.Id }));
        var envelope = new WebBridgeEnvelope(
            AssistantWebBridge.ProtocolVersion,
            "workflow.insert",
            Guid.NewGuid().ToString("D"),
            payloadDocument.RootElement.Clone());
        var emittedTypes = new List<string>();

        await coordinator.HandleAsync(
            envelope,
            (type, _, _) =>
            {
                emittedTypes.Add(type);
                return Task.CompletedTask;
            });

        var sessionId = Assert.IsType<Guid>(settings.Current.ActiveSessionId);
        var session = await environment.Get<IChatRepository>().GetSessionAsync(sessionId);
        var messages = await environment.Get<IChatRepository>().ListMessagesAsync(sessionId);
        Assert.NotNull(session);
        Assert.Null(session.SelectedWorkflowId);
        var message = Assert.Single(messages);
        Assert.Equal(ChatRole.Assistant, message.Role);
        Assert.Equal(MessageStatus.Completed, message.Status);
        Assert.Contains($"Workflow: {workflow.Title}", message.Content, StringComparison.Ordinal);
        Assert.Contains("Nutze diesen Workflow als Kontext", message.Content, StringComparison.Ordinal);
        Assert.Contains("session.changed", emittedTypes);
    }

    [Fact]
    public async Task OpeningSessionsAlwaysEmitsTheExactVisibleDatabaseRowsForThatSession()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var chats = environment.Get<IChatRepository>();
        var first = await chats.CreateSessionAsync("Erste Sitzung");
        var firstTurn = await chats.AddTurnAsync(first.Id, "Erste Frage");
        await chats.UpdateMessageAsync(firstTurn.AssistantMessage.Id, "Erste Antwort", MessageStatus.Completed);
        var second = await chats.CreateSessionAsync("Zweite Sitzung");
        var secondTurn = await chats.AddTurnAsync(second.Id, "Zweite Frage");
        await chats.UpdateMessageAsync(secondTurn.AssistantMessage.Id, "Zweite Antwort", MessageStatus.Completed);
        await settings.UpdateAsync(current => current with { ActiveSessionId = first.Id });
        using var coordinator = CreateCoordinator(environment, settings, CreateRecentActivity(settings));

        var secondSnapshot = await OpenAndCaptureSnapshotAsync(coordinator, second.Id);
        var firstSnapshot = await OpenAndCaptureSnapshotAsync(coordinator, first.Id);

        Assert.Equal(
            new[] { secondTurn.UserMessage.Id, secondTurn.AssistantMessage.Id },
            secondSnapshot.GetProperty("messages").EnumerateArray()
                .Select(static message => message.GetProperty("id").GetGuid()).ToArray());
        Assert.Equal(
            new[] { firstTurn.UserMessage.Id, firstTurn.AssistantMessage.Id },
            firstSnapshot.GetProperty("messages").EnumerateArray()
                .Select(static message => message.GetProperty("id").GetGuid()).ToArray());
        Assert.Equal("Erste Antwort", firstSnapshot.GetProperty("messages")[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task SessionActionsUseTheNewDefaultTitleAndUpdateRecentActivity()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var recentActivity = CreateRecentActivity(settings);
        using var coordinator = CreateCoordinator(environment, settings, recentActivity);

        await HandleAsync(coordinator, "session.create", new { });

        var sessionId = Assert.IsType<Guid>(settings.Current.ActiveSessionId);
        var session = await environment.Get<IChatRepository>().GetSessionAsync(sessionId);
        Assert.NotNull(session);
        Assert.Equal("Neue Sitzung", session.Title);
        Assert.Equal("AI-Sitzung „Neue Sitzung“ erstellt", settings.Current.LastActivityText);

        await HandleAsync(coordinator, "session.rename", new { sessionId, title = "Planung" });
        Assert.Equal("AI-Sitzung in „Planung“ umbenannt", settings.Current.LastActivityText);

        await HandleAsync(coordinator, "session.open", new { sessionId });
        Assert.Equal("AI-Sitzung „Planung“ geöffnet", settings.Current.LastActivityText);

        await HandleAsync(coordinator, "session.delete", new { sessionId });
        Assert.Equal("AI-Sitzung „Planung“ gelöscht", settings.Current.LastActivityText);
        var replacementId = Assert.IsType<Guid>(settings.Current.ActiveSessionId);
        var replacement = await environment.Get<IChatRepository>().GetSessionAsync(replacementId);
        Assert.NotNull(replacement);
        Assert.Equal("Neue Sitzung", replacement.Title);
    }

    private static RecentActivityService CreateRecentActivity(SettingsCoordinator settings)
    {
        var service = new RecentActivityService(
            settings,
            new ShellViewModel(),
            NullLogger<RecentActivityService>.Instance);
        service.Restore();
        return service;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GO.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Das GO-Repository wurde aus dem Testausgabeverzeichnis nicht gefunden.");
    }

    private static AssistantCoordinator CreateCoordinator(
        TestEnvironment environment,
        SettingsCoordinator settings,
        RecentActivityService recentActivity,
        ILmStudioClient? lmStudio = null) => new(
            environment.Get<IChatRepository>(),
            environment.Get<IWorkflowRepository>(),
            environment.Get<IDocumentIngestor>(),
            lmStudio ?? environment.Get<ILmStudioClient>(),
            environment.Get<IContextAssembler>(),
            environment.Get<IChatOrchestrator>(),
            environment.Get<IPromptTriggerRepository>(),
            environment.Get<IAssistantAttachmentRepository>(),
            environment.Get<IChatArtifactRepository>(),
            environment.Get<IConversationSnapshotRepository>(),
            null,
            settings,
            recentActivity);

    private static async Task HandleAsync(
        AssistantCoordinator coordinator,
        string type,
        object payload)
    {
        using var payloadDocument = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var envelope = new WebBridgeEnvelope(
            AssistantWebBridge.ProtocolVersion,
            type,
            Guid.NewGuid().ToString("D"),
            payloadDocument.RootElement.Clone());
        await coordinator.HandleAsync(envelope, static (_, _, _) => Task.CompletedTask);
    }

    private static async Task<JsonElement> OpenAndCaptureSnapshotAsync(
        AssistantCoordinator coordinator,
        Guid sessionId)
    {
        using var payloadDocument = JsonDocument.Parse(JsonSerializer.Serialize(new { sessionId }));
        var envelope = new WebBridgeEnvelope(
            AssistantWebBridge.ProtocolVersion,
            "session.open",
            Guid.NewGuid().ToString("D"),
            payloadDocument.RootElement.Clone());
        JsonElement? snapshot = null;
        await coordinator.HandleAsync(
            envelope,
            (type, payload, _) =>
            {
                if (type is "state.snapshot" or "session.changed")
                {
                    snapshot = JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web);
                }
                return Task.CompletedTask;
            });
        return snapshot ?? throw new InvalidOperationException("Der Sitzungs-Snapshot wurde nicht emittiert.");
    }

    private sealed class UnexpectedLmStudioClient : ILmStudioClient
    {
        public int ListModelsCallCount { get; private set; }

        public Task<IReadOnlyList<LmModel>> ListModelsAsync(CancellationToken cancellationToken = default)
        {
            ListModelsCallCount++;
            throw new InvalidOperationException("A local UI snapshot must not query LM Studio.");
        }

        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public async IAsyncEnumerable<LmDelta> StreamAsync(
            LmChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
