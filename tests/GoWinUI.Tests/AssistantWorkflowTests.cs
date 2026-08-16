using GoWinUI.App.Services;
using GoWinUI.App.Pages;
using GoWinUI.App.ViewModels;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
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
    public void SessionModeIsAcceptedByNativeAndWebBridgeContracts()
    {
        Assert.True(AssistantWebBridge.IsIncomingTypeAllowed("session.mode"));
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var bridge = File.ReadAllText(Path.Combine(webRoot, "bridge.js"));
        Assert.Contains("\"session.mode\"", bridge, StringComparison.Ordinal);

        var html = File.ReadAllText(Path.Combine(webRoot, "index.html"));
        Assert.Contains("bridge.js?v=20260814-7", html, StringComparison.Ordinal);

        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));
        Assert.Contains("post(\"session.mode\"", app, StringComparison.Ordinal);
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

    [Theory]
    [InlineData("microphone.start")]
    [InlineData("microphone.audio")]
    [InlineData("microphone.speak")]
    [InlineData("microphone.stopSpeech")]
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
        Assert.DoesNotContain("MediaRecorder", voice, StringComparison.Ordinal);

        Assert.Contains("microphone-frequency", html, StringComparison.Ordinal);
        Assert.DoesNotContain("voice-feedback", html, StringComparison.Ordinal);
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));
        Assert.Contains("voice-listening-preview", app, StringComparison.Ordinal);
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
        Assert.Contains("speechMessageId: String(message.id)", app, StringComparison.Ordinal);
        Assert.Contains("toolAction: \"textToSpeech\"", app, StringComparison.Ordinal);
    }

    [Fact]
    public void ScreenClipEventsRenderProgressInChatAndComposerContext()
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));
        var styles = File.ReadAllText(Path.Combine(webRoot, "styles.css"));

        Assert.Contains("createScreenClipProgressMessage", app, StringComparison.Ordinal);
        Assert.Contains("data-screen-clip-progress", app, StringComparison.Ordinal);
        Assert.Contains("Video aufnehmen · ${formatClipTime(elapsed)}", app, StringComparison.Ordinal);
        Assert.Contains("post(\"screenClip.stop\"", app, StringComparison.Ordinal);
        Assert.Contains("post(\"screenClip.cancel\"", app, StringComparison.Ordinal);
        Assert.Contains(".screen-clip-progress", styles, StringComparison.Ordinal);
        Assert.Contains(".screen-clip-chip", styles, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("webSearch", PromptTriggerAction.WebSearch)]
    [InlineData("imageGeneration", PromptTriggerAction.ImageGeneration)]
    [InlineData("youTubeSearch", PromptTriggerAction.YouTubeSearch)]
    [InlineData("bricsCad", PromptTriggerAction.BricsCad)]
    [InlineData("code", PromptTriggerAction.Code)]
    public void ExplicitComposerToolCreatesOneShotTrigger(string tool, PromptTriggerAction expected)
    {
        var match = AssistantCoordinator.CreateToolMatch(tool, "Aufgabe ohne Präfix");
        Assert.Equal(expected, match.Trigger.Action);
        Assert.Equal("Aufgabe ohne Präfix", match.RemainingPrompt);
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
    public void AnalysisToolsOwnCaptureAndSidebarDeleteRemainsHoverOnlyDuringRuns()
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
        Assert.Contains(".session-delete:disabled { opacity: 0; }", styles, StringComparison.Ordinal);
        Assert.Contains(".session-item:hover .session-delete:not(:disabled)", styles, StringComparison.Ordinal);
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

    [Fact]
    public void AutomaticVoiceOutputRemovesMarkdownNoise()
    {
        var value = MicrophoneTranscriptionService.PrepareSpeechText(
            "## Ergebnis\n\n**Volumenstrom:** [siehe Quelle](https://example.test) | 450 m³/h");

        Assert.Equal("Ergebnis Volumenstrom: siehe Quelle , 450 m hoch 3 geteilt durch h", value);
    }

    [Theory]
    [InlineData(
        @"Die Druckdifferenz ist \(\Delta p = \frac{\rho}{2} \cdot v^2\).",
        "Delta p gleich Rho geteilt durch 2 mal v hoch 2")]
    [InlineData(
        @"Volumenstrom: $$\dot{V} = A \cdot v$$",
        "V Punkt gleich A mal v")]
    [InlineData(
        @"Die Kantenlänge lautet \(\sqrt[3]{x_1}\).",
        "dritte Wurzel aus x Index 1")]
    [InlineData(
        @"Einheit: \frac{\mathrm{m}^3}{\mathrm{h}}",
        "m hoch 3 geteilt durch h")]
    [InlineData(
        "Für den Druck gilt Δp = ρ · v².",
        "Delta p gleich Rho mal v hoch 2")]
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

    [Fact]
    public void SpeechCommandsResolveExplicitTextAndLastSuitableAnswer()
    {
        var sessionId = Guid.NewGuid();
        var history = new[]
        {
            Message(sessionId, "Die fachliche Antwort mit **Markdown**."),
            Message(sessionId, "Die Sprachausgabe wurde erzeugt."),
        };

        Assert.Equal("Nur dieser Text", GoAiAssistantService.ResolveSpeechText(": Nur dieser Text", history));
        Assert.Equal("Die fachliche Antwort mit Markdown.", GoAiAssistantService.ResolveSpeechText("die letzte Nachricht vor", history));
        Assert.Equal("Die fachliche Antwort mit Markdown.", GoAiAssistantService.ResolveSpeechText(null, history));
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
    public void SpeechPreparationSplitsLongTextAtNaturalBoundariesWithoutDataLoss()
    {
        var source = string.Join(' ', Enumerable.Repeat("Ein vollständiger Satz.", 900));

        var chunks = GoAiAssistantService.SplitSpeechPreparationChunks(source, 4_000);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.InRange(chunk.Length, 1, 4_000));
        Assert.Equal(
            string.Concat(source.Where(character => !char.IsWhiteSpace(character))),
            string.Concat(string.Join(' ', chunks).Where(character => !char.IsWhiteSpace(character))));
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
    public void ComposerClearsOneShotToolsButKeepsCodingAndBricsCadAfterEveryTerminalRunState()
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));

        Assert.Contains("new Set([\"code\", \"bricsCad\"])", app, StringComparison.Ordinal);
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
        Assert.Contains("bottom: calc(100% + 8px)", css, StringComparison.Ordinal);
        Assert.Contains("position: absolute", css, StringComparison.Ordinal);
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
