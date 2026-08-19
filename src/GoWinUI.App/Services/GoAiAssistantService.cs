using GoAi.Client;
using GoAi.Contracts;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Chat;
using GoWinUI.Core.Models;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GoWinUI.App.Services;

public enum GoAiAssistantUpdateKind
{
    Started,
    Delta,
    Status,
    ArtifactsChanged,
    DocumentsChanged,
    Completed,
    Cancelled,
    Failed,
}

public sealed record GoAiAssistantUpdate(
    GoAiAssistantUpdateKind Kind,
    ChatMessage Message,
    IReadOnlyList<ChatArtifact>? Artifacts = null,
    ChatSession? Session = null,
    string? Status = null,
    string? Detail = null,
    string? Model = null,
    string? Error = null,
    int? ContextUsed = null,
    int? ContextLimit = null,
    int? LoadedFiles = null,
    bool ContextWasCompacted = false);

public sealed record GoAiSpeechUpdate(
    bool IsActive,
    string Status,
    string? Detail = null,
    string? Model = null,
    string? Error = null,
    bool CacheHit = false,
    string? DirectionModel = null);

public sealed class GoAiStreamDetachedException : OperationCanceledException
{
    public GoAiStreamDetachedException(CancellationToken cancellationToken)
        : base("Die lokale SSE-Anzeige wurde getrennt; der Serverlauf bleibt für die Wiederaufnahme gespeichert.", cancellationToken)
    {
    }
}

internal sealed class GoAiStreamDisconnectedException(string message, Exception innerException)
    : IOException(message, innerException);

internal sealed class GoAiRunTerminalException(string message)
    : InvalidOperationException(message);

public sealed class GoAiAssistantService(
    GoAiConnectionService connection,
    IChatRepository chats,
    IAssistantAttachmentRepository attachments,
    IChatArtifactRepository artifacts,
    IGoAiRunRepository runs,
    IClientToolExecutionRepository toolExecutions,
    IBinaryObjectStore blobs,
    IDocumentIngestor documents,
    DocumentContextPreparationService documentContexts,
    SessionContextPreparationService sessionContexts,
    LocalToolBroker toolBroker,
    WorkspaceRepositoryIndex repositoryIndex,
    SystemAudioCaptionService liveCaptions,
    MicrophoneTranscriptionService microphone,
    SettingsCoordinator settings,
    RecentActivityService recentActivity,
    ILogger<GoAiAssistantService> logger) : IDisposable
{
    private const int SpeechPreparationSchemaVersion = 5;
    private static readonly JsonSerializerOptions JsonOptions = GoAiProtocol.CreateJsonOptions();
    private static readonly Action<ILogger, string, string, Exception?> RunDiagnostic = LoggerMessage.Define<string, string>(
        LogLevel.Information,
        new EventId(5300, nameof(RunDiagnostic)),
        "GO AI Client run {RunId}: {State}.");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _activeCancellation;
    private string? _activeServerRunId;
    private int _explicitCancellation;
    private int _disposed;

    public bool IsRunning => _gate.CurrentCount == 0;

    public async Task<ChatMessage> SendAsync(
        Guid sessionId,
        string prompt,
        PromptTriggerMatch? trigger,
        Func<GoAiAssistantUpdate, Task> update,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (trigger?.Trigger.Action == PromptTriggerAction.TextToSpeech)
        {
            throw new InvalidOperationException("Vorlesen muss als nachrichtenlose Sprachausgabe gestartet werden.");
        }
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Es läuft bereits ein GO-AI-Auftrag.");
        }
        Interlocked.Exchange(ref _explicitCancellation, 0);
        _activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var session = await chats.GetSessionAsync(sessionId, _activeCancellation.Token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Die AI-Sitzung wurde nicht gefunden.");
            if (trigger?.Trigger.Action == PromptTriggerAction.Code
                && (string.IsNullOrWhiteSpace(session.WorkspacePath) || !Directory.Exists(session.WorkspacePath)))
            {
                throw new DirectoryNotFoundException(
                    "Der Coding-Workspace dieser Sitzung ist nicht verfügbar. Wähle den Workspace im Promptfenster erneut aus.");
            }
            var historyBeforePrompt = await chats.ListMessagesAsync(sessionId, _activeCancellation.Token).ConfigureAwait(false);
            var sessionAttachments = await attachments.ListAsync(sessionId, _activeCancellation.Token).ConfigureAwait(false);
            var action = trigger?.Trigger.Action;
            var contentProfile = action == PromptTriggerAction.Audiobook
                ? MessageContentProfile.Audiobook
                : MessageContentProfile.General;
            var user = await chats.AddMessageAsync(
                sessionId, ChatRole.User, prompt.Trim(), MessageStatus.Completed,
                cancellationToken: _activeCancellation.Token).ConfigureAwait(false);
            sessionAttachments = await BindCapturedMediaToMessageAsync(
                user,
                sessionAttachments,
                _activeCancellation.Token).ConfigureAwait(false);
            var assistant = await chats.AddMessageAsync(
                sessionId, ChatRole.Assistant, string.Empty, MessageStatus.Streaming,
                contentProfile,
                cancellationToken: _activeCancellation.Token).ConfigureAwait(false);
            var contextLimit = action == PromptTriggerAction.Code ? 262_144 : 131_072;
            await update(new(
                GoAiAssistantUpdateKind.Started,
                assistant,
                Status: "Denkt nach",
                Detail: action switch
                {
                    PromptTriggerAction.Code => "Laguna bereitet den Workspace vor.",
                    PromptTriggerAction.Audiobook => "Der Buchautor bereitet das nächste Kapitel vor.",
                    _ => "GO AI Server verarbeitet die Anfrage.",
                },
                ContextLimit: contextLimit)).ConfigureAwait(false);

            try
            {
                return action switch
                {
                    PromptTriggerAction.Transcription => await CompleteTranscriptionAsync(assistant, trigger!, update, _activeCancellation.Token).ConfigureAwait(false),
                    PromptTriggerAction.VoiceInput => await CompleteVoiceInputAsync(assistant, update, _activeCancellation.Token).ConfigureAwait(false),
                    PromptTriggerAction.LiveCaptions or PromptTriggerAction.LiveTranslation =>
                        await CompleteLiveCaptionsAsync(assistant, trigger!, update, _activeCancellation.Token).ConfigureAwait(false),
                    _ => await CompleteRunAsync(
                        assistant,
                        prompt,
                        trigger,
                        sessionAttachments,
                        historyBeforePrompt,
                        update,
                        _activeCancellation.Token).ConfigureAwait(false),
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && Volatile.Read(ref _explicitCancellation) == 0)
            {
                throw new GoAiStreamDetachedException(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                var current = (await chats.ListMessagesAsync(sessionId, CancellationToken.None).ConfigureAwait(false))
                    .Single(item => item.Id == assistant.Id);
                await chats.UpdateMessageAsync(current.Id, current.Content, MessageStatus.Cancelled, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                var cancelled = current with { Status = MessageStatus.Cancelled, UpdatedAt = DateTimeOffset.UtcNow };
                await update(new(GoAiAssistantUpdateKind.Cancelled, cancelled, Status: "Abgebrochen")).ConfigureAwait(false);
                return cancelled;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                var current = (await chats.ListMessagesAsync(sessionId, CancellationToken.None).ConfigureAwait(false))
                    .Single(item => item.Id == assistant.Id);
                var visible = string.IsNullOrWhiteSpace(current.Content)
                    ? VisibleFailure(exception)
                    : current.Content;
                await chats.UpdateMessageAsync(assistant.Id, visible, MessageStatus.Failed, exception.Message, CancellationToken.None).ConfigureAwait(false);
                var failed = (await chats.ListMessagesAsync(sessionId, CancellationToken.None).ConfigureAwait(false))
                    .Single(item => item.Id == assistant.Id);
                await update(new(GoAiAssistantUpdateKind.Failed, failed, Error: exception.Message, Status: "Fehlgeschlagen")).ConfigureAwait(false);
                return failed;
            }
        }
        finally
        {
            _activeServerRunId = null;
            _activeCancellation?.Dispose();
            _activeCancellation = null;
            _gate.Release();
        }
    }

    public async Task ResumePendingAsync(
        Func<GoAiAssistantUpdate, Task> update,
        CancellationToken cancellationToken = default)
    {
        foreach (var run in await runs.ListResumableAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
            Interlocked.Exchange(ref _explicitCancellation, 0);
            _activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                var message = (await chats.ListMessagesAsync(run.SessionId, _activeCancellation.Token).ConfigureAwait(false))
                    .SingleOrDefault(item => item.Id == run.AssistantMessageId);
                if (message is null || string.IsNullOrWhiteSpace(run.ServerRunId))
                {
                    continue;
                }
                _activeServerRunId = run.ServerRunId;
                await update(new(GoAiAssistantUpdateKind.Started, message, Status: "Wird fortgesetzt", Detail: "SSE-Ereignisse werden ab dem letzten bestätigten Ereignis geladen.")).ConfigureAwait(false);
                try
                {
                    _ = await StreamRunWithReconnectAsync(run, message, update, _activeCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && Volatile.Read(ref _explicitCancellation) == 0)
                {
                    throw new GoAiStreamDetachedException(cancellationToken);
                }
            }
            finally
            {
                _activeServerRunId = null;
                _activeCancellation?.Dispose();
                _activeCancellation = null;
                _gate.Release();
            }
        }
    }

    public async Task CancelCurrentAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref _explicitCancellation, 1);
        var serverRunId = _activeServerRunId;
        if (!string.IsNullOrWhiteSpace(serverRunId))
        {
            try
            {
                using var client = await connection.CreateClientAsync(cancellationToken).ConfigureAwait(false);
                await client.CancelRunAsync(serverRunId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                RunDiagnostic(logger, serverRunId, $"cancel request failed ({exception.GetType().Name})", exception);
            }
        }
        _activeCancellation?.Cancel();
    }

    public async Task CancelCurrentAndWaitAsync(CancellationToken cancellationToken = default)
    {
        await CancelCurrentAsync(CancellationToken.None).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _gate.Release();
    }

    public async Task SpeakAsync(
        Guid sessionId,
        string? explicitText,
        Guid? sourceMessageId,
        Func<GoAiSpeechUpdate, Task> update,
        Func<SpeechPlaybackProgress, Task>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(update);

        // Automatic voice playback can arrive while the just-completed chat callback still
        // owns the gate. Waiting here preserves ordering without creating a prompt queue.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _explicitCancellation, 0);
        _activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await update(new(
                true,
                "Vorlesen wird vorbereitet",
                "Der Vorlesekontext wird ermittelt.")).ConfigureAwait(false);

            var source = await ResolveSpeechSourceAsync(
                sessionId,
                explicitText,
                sourceMessageId,
                _activeCancellation.Token).ConfigureAwait(false);
            var speechStatusDetail = VisibleSpeechSourceDetail(source.Detail);
            var cleanedSource = MicrophoneTranscriptionService.PrepareSpeechText(source.Text);
            if (string.IsNullOrWhiteSpace(cleanedSource))
            {
                throw new InvalidOperationException("Es ist kein vorlesbarer Text vorhanden.");
            }

            var sourceUnits = SpeechSourceSegmentation.CreateUnits(source.Text);
            if (sourceUnits.Count == 0)
            {
                sourceUnits = SpeechSourceSegmentation.CreateUnits(cleanedSource);
            }
            var selectedGeneral = settings.Current.SelectedModel?.Trim() ?? string.Empty;
            SpeechPreparation? cached = null;
            var rewriteText = RequiresSpeechPreparation(source.ContentProfile);
            var sourceHash = HashSpeechValue(source.Text.ReplaceLineEndings("\n"));
            var cacheKey = HashSpeechValue(string.Join('|',
                SpeechPreparationSchemaVersion,
                sessionId.ToString("D"),
                source.MessageId?.ToString("D") ?? "none",
                source.Kind,
                source.ContentProfile,
                sourceHash,
                selectedGeneral.ToLowerInvariant()));
            cached = await chats.GetSpeechPreparationAsync(
                cacheKey,
                _activeCancellation.Token).ConfigureAwait(false);
            var speechSegments = ReadCachedSpeechSegments(cached, sourceUnits);
            DialogueDirectionSession? dialogueDirection = null;
            if (speechSegments.Count > 0)
            {
                await update(new(
                    true,
                    rewriteText ? "Vorlesefassung geladen" : "Sprechregie geladen",
                    CombineSpeechDetail(
                        speechStatusDetail,
                        rewriteText
                            ? "Bereits in dieser Sitzung aufbereitet."
                            : "Originaltext und Sprechregie wurden aus dieser Sitzung geladen."),
                    rewriteText ? selectedGeneral : "Supertonic F5 Ultra",
                    CacheHit: true)).ConfigureAwait(false);
            }
            else if (rewriteText)
            {
                cached = null;
                speechSegments = await PrepareSpeechWithGeneralAiAsync(
                    sourceUnits,
                    selectedGeneral,
                    update,
                    preserveOriginalText: false,
                    cancellationToken: _activeCancellation.Token).ConfigureAwait(false) ?? [];
                if (speechSegments.Count > 0)
                {
                    await SaveSpeechPreparationAsync(
                        cacheKey,
                        sessionId,
                        source,
                        sourceHash,
                        selectedGeneral,
                        sourceUnits,
                        speechSegments,
                        _activeCancellation.Token).ConfigureAwait(false);
                }
                else
                {
                    speechSegments = SpeechSourceSegmentation.CreateDirectSegments(sourceUnits, cleanedSource);
                }
            }
            else
            {
                cached = null;
                speechSegments = SpeechSourceSegmentation.CreateDirectSegments(sourceUnits, cleanedSource);
                await update(new(
                    true,
                    "Sprachausgabe wird erzeugt",
                    CombineSpeechDetail(speechStatusDetail, "Erzählertext wird direkt vorbereitet."),
                    "Supertonic F5 Ultra")).ConfigureAwait(false);
                dialogueDirection = StartDialogueDirection(
                    sourceUnits,
                    speechSegments,
                    selectedGeneral,
                    speechStatusDetail,
                    update,
                    _activeCancellation.Token);
                if (dialogueDirection is null)
                {
                    await SaveSpeechPreparationAsync(
                        cacheKey,
                        sessionId,
                        source,
                        sourceHash,
                        selectedGeneral,
                        sourceUnits,
                        speechSegments,
                        _activeCancellation.Token).ConfigureAwait(false);
                }
            }

            if (speechSegments.Count == 0)
            {
                throw new InvalidOperationException("Es konnten keine vorlesbaren Sprachsegmente erstellt werden.");
            }

            await update(new(
                true,
                "Sprachausgabe wird erzeugt",
                speechStatusDetail,
                "Supertonic F5 Ultra",
                CacheHit: cached is not null,
                DirectionModel: dialogueDirection is null ? null : selectedGeneral)).ConfigureAwait(false);
            if (progress is not null)
            {
                await progress(new(
                    sessionId,
                    source.MessageId,
                    source.Kind,
                    0,
                    speechSegments.Count,
                    [],
                    SpeechPlaybackState.Buffering,
                    source.MessageId is null ? null : sourceUnits)).ConfigureAwait(false);
            }
            string? speechProvider;
            var playbackStarted = 0;
            try
            {
                speechProvider = await microphone.PlaySegmentsAsync(
                    speechSegments,
                    progress: async playback =>
                    {
                        if (playback.State == SpeechPlaybackState.Playing
                            && Interlocked.CompareExchange(ref playbackStarted, 1, 0) == 0)
                        {
                            dialogueDirection?.MarkPlaybackStarted();
                            await update(new(
                                true,
                                "Sprachausgabe wird wiedergegeben",
                                speechStatusDetail,
                                DisplaySpeechProvider(playback.Provider),
                                CacheHit: cached is not null,
                                DirectionModel: dialogueDirection is { IsCompleted: false }
                                    ? selectedGeneral
                                    : null)).ConfigureAwait(false);
                        }
                        if (progress is null) return;
                        var segmentIndex = Math.Clamp(playback.SegmentIndex, 0, speechSegments.Count - 1);
                        var segment = speechSegments[segmentIndex];
                        await progress(new(
                            sessionId,
                            source.MessageId,
                            source.Kind,
                            segmentIndex,
                            speechSegments.Count,
                            segment.SourceUnitIds,
                            playback.State)).ConfigureAwait(false);
                    },
                    segmentResolver: dialogueDirection is null ? null : dialogueDirection.ResolveAsync,
                    cancellationToken: _activeCancellation.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (dialogueDirection is not null && exception is not OutOfMemoryException)
            {
                _activeCancellation.Cancel();
                try
                {
                    await dialogueDirection.Completion.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Playback and its parallel dialogue analysis share the same cancellation.
                }
                throw;
            }
            if (dialogueDirection is not null)
            {
                var directionResult = await dialogueDirection.Completion.ConfigureAwait(false);
                if (directionResult.Cacheable)
                {
                    await SaveSpeechPreparationAsync(
                        cacheKey,
                        sessionId,
                        source,
                        sourceHash,
                        selectedGeneral,
                        sourceUnits,
                        directionResult.Segments,
                        _activeCancellation.Token).ConfigureAwait(false);
                }
            }
            if (progress is not null)
            {
                await progress(new(
                    sessionId,
                    source.MessageId,
                    source.Kind,
                    speechSegments.Count,
                    speechSegments.Count,
                    [],
                    SpeechPlaybackState.Completed)).ConfigureAwait(false);
            }
            await update(new(
                false,
                "Abgeschlossen",
                speechStatusDetail,
                DisplaySpeechProvider(speechProvider),
                CacheHit: cached is not null)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (progress is not null)
            {
                await progress(new(
                    sessionId,
                    sourceMessageId,
                    "Vorlesen",
                    0,
                    0,
                    [],
                    SpeechPlaybackState.Cancelled)).ConfigureAwait(false);
            }
            await update(new(false, "Abgebrochen")).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (progress is not null)
            {
                await progress(new(
                    sessionId,
                    sourceMessageId,
                    "Vorlesen",
                    0,
                    0,
                    [],
                    SpeechPlaybackState.Cancelled)).ConfigureAwait(false);
            }
            await update(new(false, "Fehlgeschlagen", Error: exception.Message)).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _activeServerRunId = null;
            _activeCancellation?.Dispose();
            _activeCancellation = null;
            _gate.Release();
        }
    }

    private async Task<SpeechSource> ResolveSpeechSourceAsync(
        Guid sessionId,
        string? explicitText,
        Guid? sourceMessageId,
        CancellationToken cancellationToken)
    {
        var history = await chats.ListMessagesAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (sourceMessageId is { } requestedMessageId)
        {
            var selected = history.FirstOrDefault(message => message.Id == requestedMessageId
                && message.Role == ChatRole.Assistant
                && message.Status == MessageStatus.Completed)
                ?? throw new InvalidOperationException("Die ausgewählte abgeschlossene AI-Nachricht wurde nicht gefunden.");
            return new(
                selected.Content,
                "AI-Nachricht",
                null,
                selected.Id,
                selected.ContentProfile);
        }

        var sessionDocuments = await documents.ListAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var documentSpeech = await ResolveDocumentSpeechTextAsync(
            explicitText,
            sessionDocuments,
            cancellationToken).ConfigureAwait(false);
        if (documentSpeech is not null)
        {
            if (string.IsNullOrWhiteSpace(documentSpeech.Text))
            {
                throw new InvalidOperationException(documentSpeech.Error
                    ?? "Die angehängten Dokumente enthalten keinen vorlesbaren Text.");
            }
            return new(
                documentSpeech.Text,
                "Dokument aus Anhang",
                documentSpeech.Detail ?? "Dokument aus Anhang",
                null,
                MessageContentProfile.General);
        }

        var requested = explicitText?.Trim().TrimStart(':').Trim();
        if (!string.IsNullOrWhiteSpace(requested)
            && !requested.Equals("die letzte Nachricht vor", StringComparison.OrdinalIgnoreCase))
        {
            return new(requested, "Vorgegebener Text", "Vorgegebener Text", null, MessageContentProfile.General);
        }

        var lastAssistant = history
            .Reverse()
            .FirstOrDefault(static message => message.Role == ChatRole.Assistant
                && message.Status == MessageStatus.Completed
                && !string.IsNullOrWhiteSpace(message.Content)
                && !string.Equals(message.Content.Trim(), "Der Text wurde vorgelesen.", StringComparison.OrdinalIgnoreCase)
                && !message.Content.StartsWith("Die Sprachausgabe wurde", StringComparison.OrdinalIgnoreCase)
                && !message.Content.StartsWith("Der Auftrag wurde abgeschlossen", StringComparison.OrdinalIgnoreCase));
        if (lastAssistant is null)
        {
            throw new InvalidOperationException("Es ist keine geeignete abgeschlossene AI-Antwort zum Vorlesen vorhanden.");
        }
        return new(
            lastAssistant.Content,
            "AI-Nachricht",
            "Letzte AI-Nachricht",
            lastAssistant.Id,
            lastAssistant.ContentProfile);
    }

    private static string HashSpeechValue(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private Task SaveSpeechPreparationAsync(
        string cacheKey,
        Guid sessionId,
        SpeechSource source,
        string sourceHash,
        string selectedGeneral,
        IReadOnlyList<SpeechSourceUnit> sourceUnits,
        IReadOnlyList<PreparedSpeechSegment> speechSegments,
        CancellationToken cancellationToken)
    {
        var preparedText = string.Join(' ', speechSegments.Select(static segment => segment.Text));
        return chats.SaveSpeechPreparationAsync(
            new SpeechPreparation(
                cacheKey,
                sessionId,
                source.MessageId,
                source.Kind,
                sourceHash,
                selectedGeneral,
                preparedText,
                DateTimeOffset.UtcNow,
                JsonSerializer.Serialize(sourceUnits, JsonOptions),
                JsonSerializer.Serialize(speechSegments, JsonOptions)),
            cancellationToken);
    }

    private static IReadOnlyList<PreparedSpeechSegment> ReadCachedSpeechSegments(
        SpeechPreparation? preparation,
        IReadOnlyList<SpeechSourceUnit> currentUnits)
    {
        if (preparation is null || string.IsNullOrWhiteSpace(preparation.SegmentsJson))
        {
            return [];
        }
        try
        {
            var segments = JsonSerializer.Deserialize<PreparedSpeechSegment[]>(
                preparation.SegmentsJson,
                JsonOptions) ?? [];
            var validIds = currentUnits.Select(static unit => unit.Id).ToHashSet(StringComparer.Ordinal);
            if (segments.Length == 0
                || segments.Any(segment => string.IsNullOrWhiteSpace(segment.Text)
                    || segment.SourceUnitIds.Any(id => !validIds.Contains(id))))
            {
                return [];
            }
            return SpeechSourceSegmentation.NormalizePreparedSegments(segments);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    internal static bool RequiresSpeechPreparation(MessageContentProfile contentProfile) =>
        contentProfile != MessageContentProfile.Audiobook;

    private async Task<IReadOnlyList<AssistantAttachment>> BindCapturedMediaToMessageAsync(
        ChatMessage userMessage,
        IReadOnlyList<AssistantAttachment> sessionAttachments,
        CancellationToken cancellationToken)
    {
        if (!sessionAttachments.Any(IsCapturedMedia))
        {
            return sessionAttachments;
        }

        var runAttachments = new List<AssistantAttachment>(sessionAttachments.Count);
        foreach (var attachment in sessionAttachments)
        {
            if (!IsCapturedMedia(attachment))
            {
                runAttachments.Add(attachment);
                continue;
            }

            var isVideo = attachment.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
            var isAudio = attachment.ContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);
            await using var source = await blobs.OpenReadAsync(attachment.BlobId, cancellationToken).ConfigureAwait(false);
            var artifact = await artifacts.ImportAsync(
                userMessage.Id,
                $"client-capture-{attachment.Id:N}",
                attachment.FileName,
                attachment.ContentType,
                attachment.Sha256,
                attachment.Length,
                "screen-capture",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["source"] = isVideo
                        ? "screenClip.capture"
                        : isAudio
                            ? "audioCapture.capture"
                            : "screen.capture",
                },
                source,
                cancellationToken).ConfigureAwait(false);

            // The artifact now owns the same content in the local blob store. Removing
            // the pending attachment keeps the one-shot capture out of later context runs,
            // while this run still reads it through the artifact's retained blob.
            await attachments.RemoveAsync(attachment.Id, cancellationToken).ConfigureAwait(false);
            runAttachments.Add(attachment with { BlobId = artifact.BlobId });
        }

        return runAttachments;
    }

    internal static bool IsCapturedMedia(AssistantAttachment attachment) =>
        attachment.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            && attachment.FileName.StartsWith("GO-Screenshot-", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetExtension(attachment.FileName), ".png", StringComparison.OrdinalIgnoreCase)
        || attachment.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
            && attachment.FileName.StartsWith("GO-Bildschirmclip-", StringComparison.OrdinalIgnoreCase)
        || attachment.ContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            && (attachment.FileName.StartsWith("GO-Audioaufnahme-", StringComparison.OrdinalIgnoreCase)
                || attachment.FileName.StartsWith("GO-Systemaudio-", StringComparison.OrdinalIgnoreCase))
            && string.Equals(Path.GetExtension(attachment.FileName), ".wav", StringComparison.OrdinalIgnoreCase);

    private async Task<ChatMessage> CompleteRunAsync(
        ChatMessage assistant,
        string originalPrompt,
        PromptTriggerMatch? trigger,
        IReadOnlyList<AssistantAttachment> sessionAttachments,
        IReadOnlyList<ChatMessage> historyBeforePrompt,
        Func<GoAiAssistantUpdate, Task> update,
        CancellationToken cancellationToken)
    {
        using var client = await connection.CreateClientAsync(cancellationToken).ConfigureAwait(false);
        // Sonderdienste gelten ausschliesslich fuer den aktuell erkannten Datenbank-Trigger.
        // Medien aus einer vorherigen Nachricht duerfen keinen Folgelauf umdeuten.
        var action = trigger?.Trigger.Action;
        if (action == PromptTriggerAction.BricsCad && !toolBroker.IsBricsCadAvailable)
        {
            throw new InvalidOperationException("Das GO-BricsCAD-Plugin ist nicht verbunden. Öffne BricsCAD und stelle die GO-Bridge-Verbindung her.");
        }
        var isMediaAnalysis = action is PromptTriggerAction.AudioAnalysis
            or PromptTriggerAction.VideoAnalysis
            or PromptTriggerAction.ImageAnalysis;
        var hasDocumentContext = isMediaAnalysis
            && (await documents.ListAsync(assistant.SessionId, cancellationToken).ConfigureAwait(false)).Count > 0;
        var selectedMedia = isMediaAnalysis && !hasDocumentContext
            ? FindMediaAttachment(action!.Value, sessionAttachments)
            : null;
        if (isMediaAnalysis && !hasDocumentContext && selectedMedia is null)
        {
            throw new InvalidOperationException(MissingMediaContextMessage(action!.Value));
        }
        IReadOnlyList<AssistantAttachment> uploadSource = action switch
        {
            PromptTriggerAction.ImageGeneration => [],
            PromptTriggerAction.AudioAnalysis or PromptTriggerAction.VideoAnalysis or PromptTriggerAction.ImageAnalysis =>
                selectedMedia is null ? [] : [selectedMedia],
            _ => sessionAttachments,
        };
        var uploaded = await UploadAttachmentsAsync(client, uploadSource, update, assistant, cancellationToken).ConfigureAwait(false);
        var retainUploadsForResume = false;
        GoAiRunRecord? localRun = null;
        try
        {
            RunAccepted accepted;
            var idempotencyKey = $"go-client-{Guid.NewGuid():N}";
            localRun = new GoAiRunRecord(
                Guid.NewGuid(), assistant.SessionId, assistant.Id, action, idempotencyKey, null, 0, "queued",
                null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            await runs.CreateAsync(localRun, cancellationToken).ConfigureAwait(false);

            if (action == PromptTriggerAction.ImageGeneration)
            {
                var imagePrompt = RequireRemaining(trigger!, "Beschreibe nach der Triggerphrase das gewünschte Bild.");
                accepted = await client.GenerateImageAsync(new ImageGenerationRequest(imagePrompt), idempotencyKey, cancellationToken).ConfigureAwait(false);
            }
            else if (isMediaAnalysis && selectedMedia is not null)
            {
                var selected = uploaded.Single(item => item.Attachment.Id == selectedMedia!.Id);
                var requestedAnalysis = trigger is null ? originalPrompt : trigger.RemainingPrompt;
                var analysisPrompt = string.IsNullOrWhiteSpace(requestedAnalysis)
                    ? action switch
                    {
                        PromptTriggerAction.ImageAnalysis => "Analysiere dieses Bild fachlich für die TGA-Planung. Nenne sichtbare Befunde, Unsicherheiten und erforderliche Prüfungen.",
                        PromptTriggerAction.VideoAnalysis => "Analysiere diesen Bildschirm- oder Videoclip fachlich für die TGA-Planung. Beschreibe zeitcodiert relevante Vorgänge, Befunde, Unsicherheiten und erforderliche Prüfungen.",
                        _ => "Analysiere diese Audioaufnahme fachlich für die TGA-Planung. Fasse Inhalte, Entscheidungen, offene Punkte und Unsicherheiten zusammen.",
                    }
                    : requestedAnalysis;
                accepted = await client.AnalyzeMediaAsync(
                    new MediaJobRequest(selected.Upload.UploadId, analysisPrompt),
                    idempotencyKey,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var request = await BuildRunRequestAsync(
                    client,
                    assistant.SessionId,
                    originalPrompt,
                    trigger,
                    sessionAttachments,
                    historyBeforePrompt,
                    uploaded,
                    assistant,
                    update,
                    cancellationToken).ConfigureAwait(false);
                accepted = await client.CreateRunAsync(request, idempotencyKey, cancellationToken).ConfigureAwait(false);
            }

            localRun = localRun with { ServerRunId = accepted.RunId, State = ToStorage(accepted.State), UpdatedAt = DateTimeOffset.UtcNow };
            await runs.UpdateAsync(localRun.Id, accepted.RunId, 0, localRun.State, cancellationToken: cancellationToken).ConfigureAwait(false);
            _activeServerRunId = accepted.RunId;
            RunDiagnostic(logger, accepted.RunId, "accepted", null);
            var result = await StreamRunWithReconnectAsync(localRun, assistant, update, cancellationToken, client).ConfigureAwait(false);
            return result;
        }
        catch (GoAiStreamDisconnectedException)
        {
            // The run remains active on the server and references these uploads. Server-side
            // retention will remove them after the run's TTL if GO cannot reconnect later.
            retainUploadsForResume = true;
            throw;
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested
            && Volatile.Read(ref _explicitCancellation) == 0
            && localRun?.ServerRunId is not null)
        {
            retainUploadsForResume = true;
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not GoAiStreamDetachedException and not OutOfMemoryException)
        {
            if (localRun is not null && string.IsNullOrWhiteSpace(localRun.ServerRunId))
            {
                await runs.UpdateAsync(
                    localRun.Id,
                    null,
                    localRun.LastEventId,
                    "failed",
                    errorCode: "client.run_create_failed",
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            // A detached SSE reader does not cancel the server run. Its temporary uploads
            // must remain available until the resumed run reaches a terminal state.
            if (!retainUploadsForResume)
            {
                foreach (var upload in uploaded)
                {
                    try { await client.DeleteUploadAsync(upload.Upload.UploadId, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        RunDiagnostic(logger, upload.Upload.UploadId, "temporary upload cleanup deferred", exception);
                    }
                }
            }
        }
    }

    private async Task<ChatMessage> StreamRunWithReconnectAsync(
        GoAiRunRecord localRun,
        ChatMessage assistant,
        Func<GoAiAssistantUpdate, Task> update,
        CancellationToken cancellationToken,
        GoAiClient? suppliedClient = null)
    {
        const int maximumReconnectAttempts = 5;
        var current = localRun;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await StreamRunAsync(current, assistant, update, cancellationToken, suppliedClient).ConfigureAwait(false);
            }
            catch (GoAiStreamDisconnectedException exception) when (
                attempt < maximumReconnectAttempts
                && !cancellationToken.IsCancellationRequested)
            {
                current = await runs.GetAsync(localRun.Id, CancellationToken.None).ConfigureAwait(false) ?? current;
                assistant = (await chats.ListMessagesAsync(current.SessionId, CancellationToken.None).ConfigureAwait(false))
                    .SingleOrDefault(item => item.Id == current.AssistantMessageId) ?? assistant;
                var delay = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt));
                await update(new(
                    GoAiAssistantUpdateKind.Status,
                    assistant,
                    Status: "Verbindung wird wiederhergestellt",
                    Detail: $"SSE ab Ereignis {current.LastEventId} · Versuch {attempt + 1}/{maximumReconnectAttempts}"))
                    .ConfigureAwait(false);
                RunDiagnostic(logger, current.ServerRunId ?? current.Id.ToString("D"), "stream reconnect", exception);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<ChatMessage> StreamRunAsync(
        GoAiRunRecord localRun,
        ChatMessage assistant,
        Func<GoAiAssistantUpdate, Task> update,
        CancellationToken cancellationToken,
        GoAiClient? suppliedClient = null)
    {
        var ownsClient = suppliedClient is null;
        var client = suppliedClient ?? await connection.CreateClientAsync(cancellationToken).ConfigureAwait(false);
        var content = assistant.Content;
        var model = localRun.SelectedModel;
        var collectedArtifacts = (await artifacts.ListForMessageAsync(assistant.Id, cancellationToken).ConfigureAwait(false)).ToList();
        try
        {
            await foreach (var item in client.StreamRunEventsAsync(localRun.ServerRunId!, localRun.LastEventId, cancellationToken).ConfigureAwait(false))
            {
                localRun = localRun with { LastEventId = item.Id, UpdatedAt = DateTimeOffset.UtcNow };
                switch (item.Type)
                {
                    case RunEventTypes.QueueChanged:
                        var queue = item.Data.Deserialize<QueueChangedEvent>(JsonOptions);
                        await update(new(GoAiAssistantUpdateKind.Status, assistant, Status: "In Warteschlange", Detail: queue is null ? null : $"Position {queue.Position}")).ConfigureAwait(false);
                        break;
                    case RunEventTypes.ModelSelected:
                    case RunEventTypes.ModelFallback:
                        var selected = item.Data.Deserialize<ModelSelectedEvent>(JsonOptions);
                        model = selected?.ModelId ?? model;
                        await update(new(
                            GoAiAssistantUpdateKind.Status,
                            assistant,
                            Status: selected?.IsFallback == true ? "Fallback-Modell" : "Modell gewählt",
                            Model: model)).ConfigureAwait(false);
                        break;
                    case RunEventTypes.ModelLoading:
                        var loading = item.Data.Deserialize<ModelLoadingEvent>(JsonOptions);
                        model = loading?.ModelId ?? model;
                        await update(new(
                            GoAiAssistantUpdateKind.Status,
                            assistant,
                            Status: loading?.State == "loaded" ? "Denkt nach" : "Modell wird geladen",
                            Detail: loading?.State == "loaded"
                                ? null
                                : "Ausgewähltes Modell wird geladen.",
                            Model: model,
                            ContextLimit: loading?.EffectiveContextLength)).ConfigureAwait(false);
                        break;
                    case RunEventTypes.ContextChanged:
                        var context = item.Data.Deserialize<ContextChangedEvent>(JsonOptions);
                        if (context is not null)
                        {
                            await update(new(
                                GoAiAssistantUpdateKind.Status,
                                assistant,
                                Status: context.WasCompacted ? "Kontext verdichtet" : "Repositorykontext bereit",
                                Detail: context.Detail
                                    ?? $"{context.LoadedFiles:N0} Quelldateien geladen",
                                Model: model,
                                ContextUsed: context.EstimatedInputTokens,
                                ContextLimit: context.ContextLimit,
                                LoadedFiles: context.LoadedFiles,
                                ContextWasCompacted: context.WasCompacted)).ConfigureAwait(false);
                        }
                        break;
                    case RunEventTypes.ServerToolStarted:
                        await update(new(GoAiAssistantUpdateKind.Status, assistant, Status: "Serverwerkzeug", Detail: StringProperty(item.Data, "tool"), Model: model)).ConfigureAwait(false);
                        break;
                    case RunEventTypes.ServerToolCompleted:
                        var extracted = ExtractToolResultText(item.Data);
                        if (!string.IsNullOrWhiteSpace(extracted))
                        {
                            content = AppendContent(content, extracted);
                            await chats.UpdateMessageAsync(assistant.Id, content, MessageStatus.Streaming, cancellationToken: cancellationToken).ConfigureAwait(false);
                            assistant = assistant with { Content = content, Status = MessageStatus.Streaming, UpdatedAt = DateTimeOffset.UtcNow };
                            await update(new(GoAiAssistantUpdateKind.Delta, assistant)).ConfigureAwait(false);
                        }
                        break;
                    case RunEventTypes.TextDelta:
                        var delta = item.Data.Deserialize<TextDeltaEvent>(JsonOptions)?.Delta ?? string.Empty;
                        content += delta;
                        await chats.UpdateMessageAsync(assistant.Id, content, MessageStatus.Streaming, cancellationToken: cancellationToken).ConfigureAwait(false);
                        assistant = assistant with { Content = content, Status = MessageStatus.Streaming, UpdatedAt = DateTimeOffset.UtcNow };
                        await update(new(GoAiAssistantUpdateKind.Delta, assistant)).ConfigureAwait(false);
                        break;
                    case RunEventTypes.ClientToolProposed:
                        var proposal = item.Data.Deserialize<ToolProposal>(JsonOptions)
                            ?? throw new InvalidDataException("Der Server hat einen ungültigen Client-Toolvorschlag gesendet.");
                        if (!string.Equals(proposal.RunId, item.RunId, StringComparison.Ordinal))
                        {
                            throw new InvalidDataException("Der Client-Toolvorschlag gehört nicht zum aktiven Serverlauf.");
                        }
                        await update(new(GoAiAssistantUpdateKind.Status, assistant, Status: "Lokale Aktion", Detail: proposal.Summary, Model: model)).ConfigureAwait(false);
                        var result = await ExecuteClientToolOnceAsync(localRun, item, proposal, cancellationToken).ConfigureAwait(false);
                        await client.SubmitClientToolResultAsync(item.RunId, result, cancellationToken).ConfigureAwait(false);
                        await toolExecutions.MarkSubmittedAsync(proposal.ProposalId, CancellationToken.None).ConfigureAwait(false);
                        break;
                    case RunEventTypes.ArtifactCreated:
                        var descriptor = item.Data.Deserialize<ArtifactDescriptor>(JsonOptions)
                            ?? throw new InvalidDataException("Der Server hat ein ungültiges Artefakt beschrieben.");
                        var imported = await DownloadArtifactAsync(client, assistant.Id, descriptor, model ?? "GO AI Server", cancellationToken).ConfigureAwait(false);
                        if (collectedArtifacts.All(value => value.Id != imported.Id))
                        {
                            collectedArtifacts.Add(imported);
                        }
                        await update(new(GoAiAssistantUpdateKind.ArtifactsChanged, assistant, collectedArtifacts.ToArray(), Status: "Artefakt gespeichert", Detail: imported.FileName)).ConfigureAwait(false);
                        break;
                    case RunEventTypes.RunCompleted:
                        var completed = item.Data.Deserialize<RunCompletedEvent>(JsonOptions);
                        model = completed?.ModelId ?? model;
                        if (string.IsNullOrWhiteSpace(content))
                        {
                            content = collectedArtifacts.Count > 0
                                ? "Der Auftrag wurde abgeschlossen. Das Ergebnis ist unten lokal gespeichert."
                                : "Der GO-AI-Auftrag wurde abgeschlossen.";
                        }
                        var parsedResponse = GeneralAgentResponseParser.Parse(content, completed?.SessionTitle ?? string.Empty);
                        content = RemoveDocumentEvidenceFooter(parsedResponse.Message);
                        await chats.UpdateMessageAsync(assistant.Id, content, MessageStatus.Completed, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                        await chats.SetMessageContextSummaryAsync(assistant.Id, parsedResponse.ContextSummary, CancellationToken.None).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(completed?.SessionTitle))
                        {
                            var sessionTitle = GeneralAgentResponseParser.NormalizeTitle(completed.SessionTitle);
                            if (sessionTitle is not null)
                            {
                                await chats.RenameSessionAsync(assistant.SessionId, sessionTitle, CancellationToken.None).ConfigureAwait(false);
                            }
                        }
                        else if (!string.IsNullOrWhiteSpace(parsedResponse.SessionTitle))
                        {
                            await chats.RenameSessionAsync(assistant.SessionId, parsedResponse.SessionTitle, CancellationToken.None).ConfigureAwait(false);
                        }
                        await runs.UpdateAsync(localRun.Id, item.RunId, item.Id, "completed", model, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                        var final = (await chats.ListMessagesAsync(assistant.SessionId, CancellationToken.None).ConfigureAwait(false)).Single(value => value.Id == assistant.Id);
                        var session = await chats.GetSessionAsync(assistant.SessionId, CancellationToken.None).ConfigureAwait(false);
                        await recentActivity.RecordAsync($"AI-Sitzung „{session?.Title ?? "Neue Sitzung"}“ bearbeitet", CancellationToken.None).ConfigureAwait(false);
                        await update(new(GoAiAssistantUpdateKind.Completed, final, collectedArtifacts.ToArray(), session, "Fertig", model)).ConfigureAwait(false);
                        return final;
                    case RunEventTypes.RunFailed:
                        var failed = item.Data.Deserialize<RunFailedEvent>(JsonOptions);
                        await runs.UpdateAsync(
                            localRun.Id,
                            item.RunId,
                            item.Id,
                            "failed",
                            model,
                            failed?.ErrorCode ?? "server.run_failed",
                            CancellationToken.None).ConfigureAwait(false);
                        throw new GoAiRunTerminalException(failed?.Message ?? "Der Serverlauf ist fehlgeschlagen.");
                    case RunEventTypes.RunCancelled:
                        await runs.UpdateAsync(localRun.Id, item.RunId, item.Id, "cancelled", model, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                        throw new OperationCanceledException(cancellationToken);
                }

                await runs.UpdateAsync(
                    localRun.Id,
                    item.RunId,
                    item.Id,
                    item.Type == RunEventTypes.RunWaitingForClient ? "waitingForClient" : "running",
                    model,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }

            var snapshot = await client.GetRunAsync(localRun.ServerRunId!, cancellationToken).ConfigureAwait(false);
            if (snapshot.State == RunState.Completed)
            {
                if (string.IsNullOrWhiteSpace(content))
                {
                    content = "Der GO-AI-Auftrag wurde abgeschlossen.";
                }
                var snapshotResponse = GeneralAgentResponseParser.Parse(content, string.Empty);
                content = RemoveDocumentEvidenceFooter(snapshotResponse.Message);
                await chats.UpdateMessageAsync(assistant.Id, content, MessageStatus.Completed, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                await chats.SetMessageContextSummaryAsync(assistant.Id, snapshotResponse.ContextSummary, CancellationToken.None).ConfigureAwait(false);
                await runs.UpdateAsync(localRun.Id, snapshot.RunId, snapshot.LastEventId, "completed", snapshot.SelectedModel, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                var final = (await chats.ListMessagesAsync(assistant.SessionId, CancellationToken.None).ConfigureAwait(false)).Single(value => value.Id == assistant.Id);
                await update(new(GoAiAssistantUpdateKind.Completed, final, collectedArtifacts.ToArray(), await chats.GetSessionAsync(assistant.SessionId, CancellationToken.None), "Fertig", snapshot.SelectedModel)).ConfigureAwait(false);
                return final;
            }
            if (snapshot.State is RunState.Failed or RunState.Interrupted)
            {
                await runs.UpdateAsync(
                    localRun.Id,
                    snapshot.RunId,
                    snapshot.LastEventId,
                    snapshot.State == RunState.Interrupted ? "interrupted" : "failed",
                    snapshot.SelectedModel,
                    snapshot.ErrorCode ?? "server.run_failed",
                    CancellationToken.None).ConfigureAwait(false);
                throw new GoAiRunTerminalException(snapshot.State == RunState.Interrupted
                    ? "Der Serverlauf wurde durch einen Serverneustart unterbrochen. Starte den Auftrag erneut."
                    : "Der Serverlauf ist fehlgeschlagen.");
            }
            if (snapshot.State == RunState.Cancelled)
            {
                await runs.UpdateAsync(localRun.Id, snapshot.RunId, snapshot.LastEventId, "cancelled", snapshot.SelectedModel, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                throw new OperationCanceledException(cancellationToken);
            }
            throw new IOException("Der SSE-Stream wurde beendet, bevor der Serverlauf einen Endzustand erreicht hat.");
        }
        catch (GoAiRunTerminalException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            await runs.UpdateAsync(localRun.Id, localRun.ServerRunId, localRun.LastEventId, "running", model, "client.stream_detached", CancellationToken.None).ConfigureAwait(false);
            throw new GoAiStreamDisconnectedException(
                "Die Verbindung zum laufenden GO-AI-Auftrag wurde unterbrochen.",
                exception);
        }
        finally
        {
            if (ownsClient)
            {
                client.Dispose();
            }
        }
    }

    private async Task<ClientToolResult> ExecuteClientToolOnceAsync(
        GoAiRunRecord localRun,
        RunEvent item,
        ToolProposal proposal,
        CancellationToken cancellationToken)
    {
        var execution = await toolExecutions.GetAsync(proposal.ProposalId, cancellationToken).ConfigureAwait(false);
        if (execution is null)
        {
            var now = DateTimeOffset.UtcNow;
            execution = await toolExecutions.BeginAsync(
                new ClientToolExecutionRecord(
                    proposal.ProposalId,
                    localRun.Id,
                    item.RunId,
                    item.Id,
                    proposal.Name,
                    "executing",
                    null,
                    now,
                    now),
                cancellationToken).ConfigureAwait(false);
            var session = await chats.GetSessionAsync(localRun.SessionId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Die Workspace-Sitzung des lokalen Werkzeugs wurde nicht gefunden.");
            var result = await toolBroker.ExecuteAsync(
                proposal,
                session.WorkspacePath,
                localRun.SessionId,
                cancellationToken).ConfigureAwait(false);
            var json = JsonSerializer.Serialize(result, JsonOptions);
            _ = await toolExecutions.CompleteAsync(proposal.ProposalId, json, CancellationToken.None).ConfigureAwait(false);
            return result;
        }

        if (execution.LocalRunId != localRun.Id
            || !string.Equals(execution.ServerRunId, item.RunId, StringComparison.Ordinal)
            || execution.EventId != item.Id
            || !string.Equals(execution.ToolName, proposal.Name, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Der wiederaufgenommene Client-Toolvorschlag stimmt nicht mit dem lokalen Journal überein.");
        }
        if (!string.IsNullOrWhiteSpace(execution.ResultJson))
        {
            return JsonSerializer.Deserialize<ClientToolResult>(execution.ResultJson, JsonOptions)
                ?? throw new InvalidDataException("Das gespeicherte Client-Toolergebnis ist ungültig.");
        }

        // GO may have terminated after starting a local mutation but before its result was
        // committed. Never repeat an operation with an unknown outcome automatically.
        var unknown = new ClientToolResult(
            proposal.ProposalId,
            "failed",
            JsonSerializer.SerializeToElement(new { outcomeUnknown = true }, JsonOptions),
            "client.tool_outcome_unknown",
            "GO wurde während der lokalen Aktion beendet. Die Aktion wird aus Sicherheitsgründen nicht automatisch wiederholt.");
        _ = await toolExecutions.CompleteAsync(
            proposal.ProposalId,
            JsonSerializer.Serialize(unknown, JsonOptions),
            CancellationToken.None).ConfigureAwait(false);
        return unknown;
    }

    private async Task<ChatMessage> CompleteWebSearchAsync(
        ChatMessage assistant,
        PromptTriggerMatch trigger,
        bool youtube,
        Func<GoAiAssistantUpdate, Task> update,
        CancellationToken cancellationToken)
    {
        var query = RequireRemaining(trigger, "Gib nach der Triggerphrase einen Suchbegriff an.");
        using var client = await connection.CreateClientAsync(cancellationToken).ConfigureAwait(false);
        await update(new(GoAiAssistantUpdateKind.Status, assistant, Status: youtube ? "YouTube-Suche" : "Websuche", Detail: query)).ConfigureAwait(false);
        var result = youtube
            ? await client.SearchYouTubeAsync(new WebSearchRequest(query, 10, settings.Current.Language), cancellationToken).ConfigureAwait(false)
            : await client.SearchWebAsync(new WebSearchRequest(query, 10, settings.Current.Language), cancellationToken).ConfigureAwait(false);
        var markdown = FormatSearchResults(result, youtube);
        return await CompleteImmediateAsync(assistant, markdown, update, result.IsFallback ? "Fallback" : result.Provider, cancellationToken).ConfigureAwait(false);
    }

    internal static string DisplaySpeechProvider(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return "Supertonic F5 Ultra";
        }

        if (provider.Contains("supertonic", StringComparison.OrdinalIgnoreCase))
        {
            return "Supertonic F5 Ultra";
        }

        if (provider.Contains("piper", StringComparison.OrdinalIgnoreCase))
        {
            return "Piper Kerstin";
        }

        if (provider.Contains("windows", StringComparison.OrdinalIgnoreCase)
            || provider.Contains("katja", StringComparison.OrdinalIgnoreCase))
        {
            return "Windows Katja";
        }

        return provider;
    }

    private static string CombineSpeechDetail(string? sourceDetail, string detail) =>
        string.IsNullOrWhiteSpace(sourceDetail) ? detail : $"{sourceDetail} · {detail}";

    private static string? VisibleSpeechSourceDetail(string? sourceDetail) =>
        string.IsNullOrWhiteSpace(sourceDetail)
        || sourceDetail.Contains("AI-Nachricht", StringComparison.OrdinalIgnoreCase)
            ? null
            : sourceDetail;

    private DialogueDirectionSession? StartDialogueDirection(
        IReadOnlyList<SpeechSourceUnit> sourceUnits,
        IReadOnlyList<PreparedSpeechSegment> segments,
        string selectedGeneral,
        string? sourceDetail,
        Func<GoAiSpeechUpdate, Task> update,
        CancellationToken cancellationToken)
    {
        if (!segments.Any(static segment => segment.Delivery == SpeechDelivery.Dialogue))
        {
            return null;
        }

        var session = new DialogueDirectionSession(segments);
        session.Completion = RunDialogueDirectionSessionAsync(
            session,
            sourceUnits,
            selectedGeneral,
            sourceDetail,
            update,
            cancellationToken);
        return session;
    }

    private async Task<DialogueDirectionResult> RunDialogueDirectionSessionAsync(
        DialogueDirectionSession session,
        IReadOnlyList<SpeechSourceUnit> sourceUnits,
        string selectedGeneral,
        string? sourceDetail,
        Func<GoAiSpeechUpdate, Task> update,
        CancellationToken cancellationToken)
    {
        try
        {
            await update(new(
                true,
                "Sprachausgabe wird erzeugt",
                sourceDetail,
                "Supertonic F5 Ultra",
                DirectionModel: selectedGeneral)).ConfigureAwait(false);

            using var client = await connection.CreateClientAsync(cancellationToken).ConfigureAwait(false);
            var modelStatus = await client.GetModelStatusAsync(cancellationToken).ConfigureAwait(false);
            var selectedModel = modelStatus.Models.FirstOrDefault(model => model.Loaded
                && string.Equals(model.Id, selectedGeneral, StringComparison.OrdinalIgnoreCase));
            if (selectedModel is null)
            {
                session.ResolveAllNeutral(cacheable: false);
                return session.CreateResult();
            }

            foreach (var batch in BuildDialogueDirectionBatches(sourceUnits, session.SourceSegments))
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyDictionary<int, PreparedSpeechSegment>? prepared = null;
                for (var attempt = 0; attempt < 2 && prepared is null; attempt++)
                {
                    var prompt = CreateDialogueDirectionPrompt(
                        session.SourceSegments,
                        batch,
                        correction: attempt > 0);
                    var raw = await RunDialogueDirectionBatchAsync(
                        client,
                        prompt,
                        selectedGeneral,
                        cancellationToken).ConfigureAwait(false);
                    prepared = ParseDialogueDirectionBatch(raw, session.SourceSegments, batch.SegmentIndexes);
                }

                if (prepared is null)
                {
                    session.ResolveNeutral(batch.SegmentIndexes, cacheable: false);
                    continue;
                }
                foreach (var pair in prepared)
                {
                    session.Resolve(pair.Key, pair.Value, cacheable: true);
                }
            }
            session.ResolveAllNeutral(cacheable: false);
            return session.CreateResult();
        }
        catch (OperationCanceledException)
        {
            session.Cancel(cancellationToken);
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _activeServerRunId = null;
            RunDiagnostic(logger, "dialogue-direction", "dialogue direction fallback", exception);
            session.ResolveAllNeutral(cacheable: false);
            return session.CreateResult();
        }
        finally
        {
            await update(new(
                true,
                session.PlaybackStarted
                    ? "Sprachausgabe wird wiedergegeben"
                    : "Sprachausgabe wird erzeugt",
                sourceDetail,
                "Supertonic F5 Ultra")).ConfigureAwait(false);
        }
    }

    internal static IReadOnlyList<DialogueDirectionBatch> BuildDialogueDirectionBatches(
        IReadOnlyList<SpeechSourceUnit> sourceUnits,
        IReadOnlyList<PreparedSpeechSegment> segments)
    {
        var dialogueIndexes = segments
            .Select((segment, index) => (segment, index))
            .Where(static item => item.segment.Delivery == SpeechDelivery.Dialogue)
            .Select(static item => item.index)
            .ToArray();
        if (dialogueIndexes.Length == 0)
        {
            return [];
        }

        var batches = new List<DialogueDirectionBatch>();
        var current = new List<int>();
        var characters = 0;
        foreach (var index in dialogueIndexes)
        {
            var next = segments[index].Text.Length;
            if (current.Count > 0 && (current.Count >= 6 || characters + next > 1_500))
            {
                batches.Add(CreateDialogueDirectionBatch(sourceUnits, segments, current));
                current.Clear();
                characters = 0;
            }
            current.Add(index);
            characters += next;
        }
        if (current.Count > 0)
        {
            batches.Add(CreateDialogueDirectionBatch(sourceUnits, segments, current));
        }
        return batches;
    }

    private static DialogueDirectionBatch CreateDialogueDirectionBatch(
        IReadOnlyList<SpeechSourceUnit> sourceUnits,
        IReadOnlyList<PreparedSpeechSegment> segments,
        IReadOnlyList<int> indexes)
    {
        var sourceOrder = sourceUnits
            .Select((unit, index) => (unit.Id, index))
            .ToDictionary(static item => item.Id, static item => item.index, StringComparer.Ordinal);
        var positions = indexes
            .SelectMany(index => segments[index].SourceUnitIds)
            .Where(sourceOrder.ContainsKey)
            .Select(id => sourceOrder[id])
            .Distinct()
            .Order()
            .ToArray();
        var context = positions.Length == 0
            ? Array.Empty<SpeechSourceUnit>()
            : sourceUnits
                .Skip(Math.Max(0, positions[0] - 2))
                .Take(Math.Min(sourceUnits.Count - Math.Max(0, positions[0] - 2), positions[^1] - positions[0] + 5))
                .ToArray();
        return new(indexes.ToArray(), context);
    }

    private static string CreateDialogueDirectionPrompt(
        PreparedSpeechSegment[] segments,
        DialogueDirectionBatch batch,
        bool correction)
    {
        var targets = batch.SegmentIndexes.Select(index => new
        {
            id = segments[index].Id,
            text = segments[index].Text,
        }).ToArray();
        var targetIds = batch.SegmentIndexes
            .SelectMany(index => segments[index].SourceUnitIds)
            .ToHashSet(StringComparer.Ordinal);
        var context = batch.ContextUnits.Select(unit => new
        {
            role = targetIds.Contains(unit.Id) ? "target" : "context",
            text = unit.SpeechText.Length <= 350 ? unit.SpeechText : unit.SpeechText[..350],
        }).ToList();
        string BuildPrompt() => $$"""
            {{(correction ? "Die vorige Antwort war ungültig. " : string.Empty)}}Bestimme eine deutlich hörbare, aber inhaltlich passende Sprechregie ausschließlich für die direkten Redezeilen in targets. context dient nur zur Einordnung und darf nicht ausgegeben werden.
            Antworte nur als JSON: {"segments":[{"id":"s0001","synthesisText":"unveränderter Wortlaut mit anderer Interpunktion","mood":"warm","intensity":0.8,"expressionBefore":null,"expressionAfter":null}]}
            Regeln: Jede target-ID exakt einmal und in Reihenfolge. synthesisText muss exakt dieselben Unicode-Wörter in derselben Reihenfolge enthalten; erlaubt sind nur andere Satzzeichen, Leerzeichen und Sprechpausen. Keine Wörter ergänzen, entfernen oder ersetzen. mood ist neutral, warm, joyful, tense, sad, relieved, angry, mysterious, fearful oder tender. intensity liegt zwischen 0 und 1. Nutze starke, klar wahrnehmbare Intensitäten. expressionBefore/After ist null, laugh, breath oder sigh und nur bei semantischer Eindeutigkeit. Kein Markdown.
            targets={{JsonSerializer.Serialize(targets, JsonOptions)}}
            context={{JsonSerializer.Serialize(context, JsonOptions)}}
            """;

        var prompt = BuildPrompt();
        while (prompt.Length > 4_000 && context.Count > 0)
        {
            var removeIndex = context.FindIndex(static item => item.role == "context");
            if (removeIndex < 0) removeIndex = 0;
            context.RemoveAt(removeIndex);
            prompt = BuildPrompt();
        }
        if (prompt.Length <= 4_000)
        {
            return prompt;
        }
        var compact = $"Nur JSON mit segments ausgeben. Jede ID exakt einmal. synthesisText behält alle Unicode-Wörter und Symbole exakt in Reihenfolge; nur Satzzeichen und Leerzeichen ändern. mood: neutral|warm|joyful|tense|sad|relieved|angry|mysterious|fearful|tender. intensity: 0..1. expressionBefore/After: null|laugh|breath|sigh. targets={JsonSerializer.Serialize(targets, JsonOptions)}";
        return compact.Length <= 4_000
            ? compact
            : throw new InvalidDataException("Das Dialogpaket überschreitet das sichere Regie-Kontextlimit.");
    }

    private async Task<string> RunDialogueDirectionBatchAsync(
        GoAiClient client,
        string prompt,
        string selectedGeneral,
        CancellationToken cancellationToken)
    {
        var accepted = await client.CreateRunAsync(new RunRequest(
            GoAiProtocol.Version,
            RunMode.General,
            [new RunMessage("user", [new ContentPart("text", prompt)])],
            Limits: new RunLimits(
                MaximumOutputTokens: 1_024,
                MaximumContextTokens: 8_192,
                TimeoutSeconds: 180),
            AllowedServerTools: [],
            PreferredGeneralModelId: selectedGeneral),
            $"go-dialogue-direction-{Guid.NewGuid():N}", cancellationToken).ConfigureAwait(false);
        _activeServerRunId = accepted.RunId;
        var output = new StringBuilder();
        try
        {
            await foreach (var item in client.StreamRunEventsAsync(
                accepted.RunId,
                cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                switch (item.Type)
                {
                    case RunEventTypes.ModelSelected:
                    case RunEventTypes.ModelFallback:
                        var selected = item.Data.Deserialize<ModelSelectedEvent>(JsonOptions)?.ModelId;
                        if (!string.IsNullOrWhiteSpace(selected)
                            && !string.Equals(selected, selectedGeneral, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException("Die Dialogregie hat ein unerwartetes Modell ausgewählt.");
                        }
                        break;
                    case RunEventTypes.TextDelta:
                        var delta = item.Data.Deserialize<TextDeltaEvent>(JsonOptions);
                        if (!string.IsNullOrEmpty(delta?.Delta)) output.Append(delta.Delta);
                        break;
                    case RunEventTypes.RunFailed:
                        var failure = item.Data.Deserialize<RunFailedEvent>(JsonOptions);
                        throw new InvalidOperationException(failure?.Message ?? "Die Dialogregie ist fehlgeschlagen.");
                    case RunEventTypes.RunCancelled:
                        throw new OperationCanceledException("Die Dialogregie wurde abgebrochen.", cancellationToken);
                }
            }
            return output.ToString().Trim();
        }
        finally
        {
            _activeServerRunId = null;
        }
    }

    internal static IReadOnlyDictionary<int, PreparedSpeechSegment>? ParseDialogueDirectionBatch(
        string raw,
        IReadOnlyList<PreparedSpeechSegment> segments,
        IReadOnlyList<int> targetIndexes)
    {
        try
        {
            var json = StripOuterJsonFence(raw);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                json = StripOuterJsonFence(message.GetString() ?? string.Empty);
            }
            var response = JsonSerializer.Deserialize<DialogueDirectionResponse>(json, JsonOptions);
            if (response?.Segments is null || response.Segments.Count != targetIndexes.Count)
            {
                return null;
            }

            var output = new Dictionary<int, PreparedSpeechSegment>();
            for (var itemIndex = 0; itemIndex < targetIndexes.Count; itemIndex++)
            {
                var segmentIndex = targetIndexes[itemIndex];
                var source = segments[segmentIndex];
                var candidate = response.Segments[itemIndex];
                if (!string.Equals(candidate.Id, source.Id, StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(candidate.SynthesisText)
                    || !SpeechSourceSegmentation.HasSameSpokenWords(source.Text, candidate.SynthesisText)
                    || !TryParseSpeechMood(candidate.Mood, out var mood)
                    || !TryParseSpeechExpression(candidate.ExpressionBefore, out var before)
                    || !TryParseSpeechExpression(candidate.ExpressionAfter, out var after))
                {
                    return null;
                }
                var intensity = candidate.Intensity ?? 0;
                if (!double.IsFinite(intensity) || intensity is < 0 or > 1)
                {
                    return null;
                }
                output[segmentIndex] = source with
                {
                    Mood = mood,
                    Intensity = intensity,
                    Speed = SpeechSourceSegmentation.ResolveDialogueSpeed(mood, intensity),
                    PauseAfterMilliseconds = SpeechSourceSegmentation.ResolveDialoguePause(intensity),
                    ExpressionBefore = before,
                    ExpressionAfter = after,
                    SynthesisText = candidate.SynthesisText.Trim(),
                    DirectionResolved = true,
                };
            }
            return output;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<PreparedSpeechSegment>?> PrepareSpeechWithGeneralAiAsync(
        IReadOnlyList<SpeechSourceUnit> sourceUnits,
        string selectedGeneral,
        Func<GoAiSpeechUpdate, Task> update,
        bool preserveOriginalText,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = await connection.CreateClientAsync(cancellationToken).ConfigureAwait(false);
            var modelStatus = await client.GetModelStatusAsync(cancellationToken).ConfigureAwait(false);
            var selectedModel = modelStatus.Models.FirstOrDefault(model => model.Loaded
                && string.Equals(model.Id, selectedGeneral, StringComparison.OrdinalIgnoreCase));
            if (selectedModel is null)
            {
                return null;
            }
            await update(new(
                true,
                preserveOriginalText
                    ? "Sprechregie wird analysiert"
                    : "Text wird für Sprachausgabe aufbereitet",
                preserveOriginalText
                    ? "Direkte Rede, Erzählertext, Stimmung, Tempo und Pausen werden einmalig bestimmt."
                    : "Vorlesefassung und Sprechregie werden einmalig bestimmt.",
                selectedGeneral)).ConfigureAwait(false);
            var output = new List<PreparedSpeechSegment>();
            foreach (var chunk in ChunkSpeechUnits(sourceUnits, 9_000))
            {
                IReadOnlyList<PreparedSpeechSegment>? prepared = null;
                for (var attempt = 0; attempt < 2 && prepared is null; attempt++)
                {
                    var prompt = CreateMappedSpeechPreparationPrompt(
                        chunk,
                        correction: attempt > 0,
                        preserveOriginalText);
                    var raw = await RunSpeechPreparationAsync(
                        client,
                        prompt,
                        selectedGeneral,
                        selectedModel.ContextTokens,
                        update,
                        preserveOriginalText,
                        cancellationToken).ConfigureAwait(false);
                    prepared = ParseMappedSpeechSegments(raw, chunk, preserveOriginalText);
                }
                if (prepared is null)
                {
                    throw new InvalidDataException("Die AI-Vorlesefassung enthielt keine gültige Zuordnung zum Originaltext.");
                }
                output.AddRange(prepared);
            }
            return SpeechSourceSegmentation.NormalizePreparedSegments(output);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _activeServerRunId = null;
            RunDiagnostic(logger, "speech-preparation", "speech preparation fallback", exception);
            await update(new(true, "Sprachausgabe", "Lokale Textbereinigung wird verwendet.")).ConfigureAwait(false);
            return null;
        }
    }

    private async Task<string> RunSpeechPreparationAsync(
        GoAiClient client,
        string prompt,
        string selectedGeneral,
        int contextTokens,
        Func<GoAiSpeechUpdate, Task> update,
        bool preserveOriginalText,
        CancellationToken cancellationToken)
    {
        var status = preserveOriginalText
            ? "Sprechregie wird analysiert"
            : "Text wird für Sprachausgabe aufbereitet";
        var detail = preserveOriginalText
            ? "Direkte Rede erhält eine deutlich ausgeprägte Sprechregie."
            : "Das Sprachmodell ordnet die Vorlesefassung dem sichtbaren Originaltext zu.";
        var accepted = await client.CreateRunAsync(new RunRequest(
            GoAiProtocol.Version,
            RunMode.General,
            [new RunMessage("user", [new ContentPart("text", prompt)])],
            Limits: new RunLimits(
                MaximumOutputTokens: 8_192,
                MaximumContextTokens: Math.Clamp(contextTokens, 8_192, 262_144),
                TimeoutSeconds: 600),
            AllowedServerTools: [],
            PreferredGeneralModelId: selectedGeneral),
            $"go-speech-prep-{Guid.NewGuid():N}", cancellationToken).ConfigureAwait(false);
        _activeServerRunId = accepted.RunId;
        var output = new StringBuilder();
        string? activeModel = selectedGeneral;
        await foreach (var item in client.StreamRunEventsAsync(accepted.RunId, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            switch (item.Type)
            {
                case RunEventTypes.ModelSelected:
                case RunEventTypes.ModelFallback:
                    activeModel = item.Data.Deserialize<ModelSelectedEvent>(JsonOptions)?.ModelId ?? activeModel;
                    await update(new(
                        true,
                        Status: status,
                        Detail: detail,
                        Model: activeModel)).ConfigureAwait(false);
                    break;
                case RunEventTypes.ModelLoading:
                    var loading = item.Data.Deserialize<ModelLoadingEvent>(JsonOptions);
                    activeModel = loading?.ModelId ?? activeModel;
                    await update(new(
                        true,
                        Status: loading?.State == "loaded"
                            ? status
                            : "Modell wird geladen",
                        Detail: loading?.State == "loaded"
                            ? detail
                            : "Ausgewähltes Modell wird geladen.",
                        Model: activeModel)).ConfigureAwait(false);
                    break;
                case RunEventTypes.ContextChanged:
                    await update(new(
                        true,
                        Status: status,
                        Detail: detail,
                        Model: activeModel)).ConfigureAwait(false);
                    break;
                case RunEventTypes.TextDelta:
                    var delta = item.Data.Deserialize<TextDeltaEvent>(JsonOptions);
                    if (!string.IsNullOrEmpty(delta?.Delta)) output.Append(delta.Delta);
                    break;
                case RunEventTypes.RunFailed:
                    var failure = item.Data.Deserialize<RunFailedEvent>(JsonOptions);
                    throw new InvalidOperationException(
                        failure?.Message ?? "Die AI-Aufbereitung für die Sprachausgabe ist fehlgeschlagen.");
                case RunEventTypes.RunCancelled:
                    throw new OperationCanceledException(
                        "Die AI-Aufbereitung für die Sprachausgabe wurde abgebrochen.",
                        cancellationToken);
            }
        }
        _activeServerRunId = null;
        return output.ToString().Trim();
    }

    private static string CreateMappedSpeechPreparationPrompt(
        IReadOnlyList<SpeechSourceUnit> units,
        bool correction,
        bool preserveOriginalText)
    {
        var source = JsonSerializer.Serialize(
            units.Select(static unit => new
            {
                id = unit.Id,
                text = unit.SpeechText,
                delivery = unit.Delivery.ToString().ToLowerInvariant(),
            }),
            JsonOptions);
        var correctionRule = correction
            ? "Deine vorherige Ausgabe war ungültig. Halte dieses Mal das JSON-Schema und alle Quell-IDs ausnahmslos ein. "
            : string.Empty;
        var textRule = preserveOriginalText
            ? "Der Text jeder Quelleneinheit muss zeichengetreu und unverändert ausgegeben werden. Erlaubt ist ausschließlich das Ergänzen der Sprechregie in den separaten JSON-Feldern."
            : "Schreibe die Quelleneinheiten stark in flüssige, natürliche deutsche Vorlesesprache um, als hätte ein Buchautor sie für eine professionelle Lesung verfasst. Schaffe angenehme Übergänge und vollständige, gut sprechbare Sätze. Entferne Layoutreste, Tabellenmarker, technische Artefakte sowie unnötige Meta- und Bedienhinweise. Bewahre Bedeutung, fachlich relevante Aussagen, Zahlen, Einheiten und Reihenfolge. Erfinde keine Fakten.";
        var preservationRules = preserveOriginalText
            ? "- Gib exakt ein Segment je Quelleneinheit aus, verwende genau deren eine sourceUnitId und kopiere text unverändert.\n- Fasse keine Einheiten zusammen und teile keine Einheit auf."
            : "- Ein Segment darf mehrere direkt zusammengehörige Quell-IDs desselben delivery-Typs verbinden.\n- Eine Quell-ID darf für mehrere aufeinanderfolgende Sätze wiederholt werden.";
        return $$"""
            {{correctionRule}}{{textRule}}

            Direkte Rede und Erzählertext wurden bereits getrennt. Vermische beide Typen nicht. Bestimme eine
            deutlich hörbare, inhaltlich passende Sprechregie ausschließlich für delivery=dialogue. Erzählertext
            bleibt ausnahmslos neutral, ohne Ausdruckstags, Tempoänderung oder künstliche Pause.

            Antworte ausschließlich mit genau diesem JSON-Format, ohne Markdown oder zusätzlichen Text:
            {"segments":[{"text":"Vorlesesatz","sourceUnitIds":["u0001"],"delivery":"dialogue","mood":"warm","intensity":0.45,"speed":0.96,"pauseAfterMilliseconds":220,"expressionBefore":null,"expressionAfter":null}]}

            Regeln:
            - Jede ausgegebene Einheit enthält nicht leeren Vorlesetext und mindestens eine vorhandene sourceUnitId.
            - Verwende jede unten gelieferte Quell-ID mindestens einmal.
            - IDs und Segmente bleiben in der Reihenfolge der Quelle; unbekannte IDs sind verboten.
            {{preservationRules}}
            - delivery ist exakt narration oder dialogue und muss dem gelieferten Typ entsprechen.
            - mood ist exakt neutral, warm, joyful, tense, sad, relieved, angry, mysterious, fearful oder tender.
            - intensity liegt zwischen 0.0 und 1.0. Verwende bei emotional eindeutiger direkter Rede kräftige Werte.
            - speed liegt zwischen 0.82 und 1.32; für narration ist speed exakt 1.0.
            - pauseAfterMilliseconds ist eine ganze Zahl zwischen 0 und 1500.
            - expressionBefore und expressionAfter sind null oder exakt laugh, breath oder sigh.
            - Ausdruckstags sparsam und nur bei semantisch eindeutigem Lachen, Atemholen oder Seufzen einsetzen.
            - Redeankündigungen wie „sagte Natascha leise“ bleiben narration; nur der Inhalt in Anführungszeichen ist dialogue.
            - Gib keine Überschrift, Quellenhinweise oder Erklärung der Bearbeitung aus.

            Quelleneinheiten:
            {{source}}
            """;
    }

    internal static IReadOnlyList<PreparedSpeechSegment>? ParseMappedSpeechSegments(
        string raw,
        IReadOnlyList<SpeechSourceUnit> units,
        bool preserveOriginalText = false)
    {
        var json = StripOuterJsonFence(raw);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                json = StripOuterJsonFence(message.GetString() ?? string.Empty);
            }
            var response = JsonSerializer.Deserialize<MappedSpeechPreparationResponse>(json, JsonOptions);
            if (response?.Segments is null || response.Segments.Count == 0)
            {
                return null;
            }

            var order = units.Select((unit, index) => (unit.Id, index))
                .ToDictionary(static item => item.Id, static item => item.index, StringComparer.Ordinal);
            var covered = new HashSet<string>(StringComparer.Ordinal);
            var output = new List<PreparedSpeechSegment>();
            var previousMaximum = -1;
            foreach (var candidate in response.Segments)
            {
                if (string.IsNullOrWhiteSpace(candidate.Text)
                    || candidate.SourceUnitIds is null
                    || candidate.SourceUnitIds.Count == 0)
                {
                    return null;
                }
                var ids = candidate.SourceUnitIds
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (ids.Any(id => !order.ContainsKey(id)))
                {
                    return null;
                }
                var sourceDeliveries = ids
                    .Select(id => units[order[id]].Delivery)
                    .Distinct()
                    .ToArray();
                if (sourceDeliveries.Length != 1
                    || !TryParseSpeechDelivery(candidate.Delivery, sourceDeliveries[0], out var delivery)
                    || delivery != sourceDeliveries[0]
                    || !TryParseSpeechMood(candidate.Mood, out var mood)
                    || !TryParseSpeechExpression(candidate.ExpressionBefore, out var expressionBefore)
                    || !TryParseSpeechExpression(candidate.ExpressionAfter, out var expressionAfter))
                {
                    return null;
                }
                var intensity = candidate.Intensity ?? 0;
                var speed = candidate.Speed ?? 1.0;
                var pause = candidate.PauseAfterMilliseconds ?? 0;
                if (!double.IsFinite(intensity) || intensity is < 0 or > 1
                    || !double.IsFinite(speed) || speed is < 0.82 or > 1.32
                    || pause is < 0 or > 1_500)
                {
                    return null;
                }
                if (preserveOriginalText)
                {
                    if (ids.Length != 1
                        || covered.Contains(ids[0])
                        || !string.Equals(
                            candidate.Text.Trim(),
                            units[order[ids[0]]].SpeechText.Trim(),
                            StringComparison.Ordinal))
                    {
                        return null;
                    }
                }
                var positions = ids.Select(id => order[id]).ToArray();
                if (!positions.SequenceEqual(positions.Order()))
                {
                    return null;
                }
                var minimum = positions[0];
                if (minimum < previousMaximum && positions.Any(position => position != previousMaximum))
                {
                    return null;
                }
                previousMaximum = Math.Max(previousMaximum, positions[^1]);
                covered.UnionWith(ids);
                if (delivery == SpeechDelivery.Narration)
                {
                    mood = SpeechMood.Neutral;
                    intensity = 0;
                    speed = 1.0;
                    pause = 0;
                    expressionBefore = null;
                    expressionAfter = null;
                }
                else
                {
                    speed = SpeechSourceSegmentation.ResolveDialogueSpeed(mood, intensity);
                    pause = SpeechSourceSegmentation.ResolveDialoguePause(intensity);
                }
                output.Add(new(
                    $"s{output.Count + 1:0000}",
                    candidate.Text.Trim(),
                    ids,
                    delivery,
                    mood,
                    intensity,
                    speed,
                    pause,
                    expressionBefore,
                    expressionAfter,
                    PlaybackBatchId: units[minimum].BlockId));
            }
            if (covered.Count != units.Count || units.Any(unit => !covered.Contains(unit.Id)))
            {
                return null;
            }
            return SpeechSourceSegmentation.NormalizePreparedSegments(output);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryParseSpeechDelivery(
        string? value,
        SpeechDelivery fallback,
        out SpeechDelivery delivery)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            delivery = fallback;
            return true;
        }
        return Enum.TryParse(value, ignoreCase: true, out delivery)
            && Enum.IsDefined(delivery);
    }

    private static bool TryParseSpeechMood(string? value, out SpeechMood mood)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            mood = SpeechMood.Neutral;
            return true;
        }
        return Enum.TryParse(value, ignoreCase: true, out mood)
            && Enum.IsDefined(mood);
    }

    private static bool TryParseSpeechExpression(
        string? value,
        out SpeechExpression? expression)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            expression = null;
            return true;
        }
        if (Enum.TryParse<SpeechExpression>(value, ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed))
        {
            expression = parsed;
            return true;
        }
        expression = null;
        return false;
    }

    private static string StripOuterJsonFence(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;
        var firstLine = trimmed.IndexOf('\n');
        if (firstLine < 0) return trimmed;
        trimmed = trimmed[(firstLine + 1)..];
        var closing = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return closing >= 0 ? trimmed[..closing].Trim() : trimmed.Trim();
    }

    private static List<IReadOnlyList<SpeechSourceUnit>> ChunkSpeechUnits(
        IReadOnlyList<SpeechSourceUnit> units,
        int maximumCharacters)
    {
        var output = new List<IReadOnlyList<SpeechSourceUnit>>();
        var current = new List<SpeechSourceUnit>();
        var characters = 0;
        foreach (var unit in units)
        {
            var next = unit.SpeechText.Length + unit.Id.Length + 32;
            if (current.Count > 0 && characters + next > maximumCharacters)
            {
                output.Add(current.ToArray());
                current.Clear();
                characters = 0;
            }
            current.Add(unit);
            characters += next;
        }
        if (current.Count > 0) output.Add(current.ToArray());
        return output;
    }

    private sealed record MappedSpeechPreparationResponse(
        IReadOnlyList<MappedSpeechPreparationSegment>? Segments);

    private sealed record MappedSpeechPreparationSegment(
        string Text,
        IReadOnlyList<string>? SourceUnitIds,
        string? Delivery = null,
        string? Mood = null,
        double? Intensity = null,
        double? Speed = null,
        int? PauseAfterMilliseconds = null,
        string? ExpressionBefore = null,
        string? ExpressionAfter = null);

    internal static IReadOnlyList<string> SplitSpeechPreparationChunks(string source, int maximumCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCharacters, 1_000);
        var remaining = source.Trim();
        if (remaining.Length == 0) return [];
        var chunks = new List<string>();
        while (remaining.Length > maximumCharacters)
        {
            var boundary = remaining.LastIndexOfAny(['\n', '.', '!', '?'], maximumCharacters - 1, maximumCharacters);
            if (boundary < maximumCharacters / 2) boundary = maximumCharacters;
            else boundary++;
            chunks.Add(remaining[..boundary].Trim());
            remaining = remaining[boundary..].TrimStart();
        }
        if (remaining.Length > 0) chunks.Add(remaining);
        return chunks;
    }

    private sealed record DocumentSpeechResolution(string? Text, string? Detail, string? Error);

    private async Task<DocumentSpeechResolution?> ResolveDocumentSpeechTextAsync(
        string? explicitText,
        IReadOnlyList<StoredDocument> sessionDocuments,
        CancellationToken cancellationToken)
    {
        if (sessionDocuments.Count == 0)
        {
            return null;
        }

        var pageSelection = ParseSpeechPageSelection(explicitText);
        var selectedPages = new List<(StoredDocument Document, DocumentPage Page)>();
        foreach (var document in sessionDocuments.OrderBy(item => item.CreatedAt))
        {
            var pages = await documents.ReadPagesAsync(document.Id, cancellationToken).ConfigureAwait(false);
            var filtered = pages
                .Where(page => pageSelection is null
                    || (page.PageNumber >= pageSelection.Value.Start
                        && (!pageSelection.Value.End.HasValue || page.PageNumber <= pageSelection.Value.End.Value)))
                .OrderBy(page => page.PageNumber)
                .Where(page => !string.IsNullOrWhiteSpace(page.Text));
            selectedPages.AddRange(filtered.Select(page => (document, page)));
        }

        if (selectedPages.Count == 0)
        {
            var requested = pageSelection is null ? "" : $" für {pageSelection.Value.Description}";
            return new DocumentSpeechResolution(null, null,
                $"Die angehängten Dokumente enthalten keinen vorlesbaren Text{requested}.");
        }

        var builder = new StringBuilder();
        foreach (var group in selectedPages.GroupBy(item => item.Document.Id))
        {
            var document = group.First().Document;
            builder.AppendLine(CultureInfo.InvariantCulture, $"Dokument: {document.FileName}");
            foreach (var item in group)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"Seite {item.Page.PageNumber}.");
                builder.AppendLine(MicrophoneTranscriptionService.PrepareSpeechText(item.Page.Text));
                builder.AppendLine();
            }
        }

        var text = builder.ToString().Trim();
        var detail = pageSelection is null
            ? $"Dokumente werden vorgelesen ({selectedPages.Count} Seiten)."
            : $"Dokumente werden vorgelesen ({pageSelection.Value.Description}).";
        return new DocumentSpeechResolution(text, detail, null);
    }

    internal static (int Start, int? End, string Description)? ParseSpeechPageSelection(string? prompt)
    {
        var value = prompt?.Trim() ?? string.Empty;
        if (value.Contains(" bis ", StringComparison.OrdinalIgnoreCase))
        {
            value = System.Text.RegularExpressions.Regex.Replace(value, @"\bab\s+seite\s+", "Seite ", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        }
        var match = System.Text.RegularExpressions.Regex.Match(
            value,
            @"\b(?:seiten?|page|pages)\s+(\d+)(?:\s*(?:-|bis)\s*(?:seiten?\s*)?(\d+))?\b|\bab\s+seite\s+(\d+)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }

        var startText = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[3].Value;
        if (!int.TryParse(startText, NumberStyles.None, CultureInfo.InvariantCulture, out var start) || start < 1)
        {
            return null;
        }

        int? end = match.Groups[3].Success ? null : start;
        if (match.Groups[2].Success && int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedEnd))
        {
            end = parsedEnd >= start ? parsedEnd : start;
        }

        var description = end == start ? $"Seite {start}" : end.HasValue ? $"Seite {start} bis {end.Value}" : $"ab Seite {start}";
        return (start, end, description);
    }

    internal static string? ResolveSpeechText(string? explicitText, IReadOnlyList<ChatMessage> history)
    {
        var requested = explicitText?.Trim().TrimStart(':').Trim();
        if (!string.IsNullOrWhiteSpace(requested)
            && !requested.Equals("die letzte Nachricht vor", StringComparison.OrdinalIgnoreCase))
        {
            return MicrophoneTranscriptionService.PrepareSpeechText(requested);
        }

        foreach (var message in history.Reverse())
        {
            if (message.Role != ChatRole.Assistant || message.Status != MessageStatus.Completed)
            {
                continue;
            }
            var text = MicrophoneTranscriptionService.PrepareSpeechText(message.Content);
            if (string.IsNullOrWhiteSpace(text)
                || text.Equals("Der Text wurde vorgelesen.", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("Die Sprachausgabe wurde", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("Der Auftrag wurde abgeschlossen", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            return text;
        }
        return null;
    }

    private async Task<ChatMessage> CompleteTranscriptionAsync(
        ChatMessage assistant,
        PromptTriggerMatch trigger,
        Func<GoAiAssistantUpdate, Task> update,
        CancellationToken cancellationToken)
    {
        var sessionAttachments = await attachments.ListAsync(assistant.SessionId, cancellationToken).ConfigureAwait(false);
        var audio = sessionAttachments.LastOrDefault(item => item.ContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Hänge zuerst eine Audiodatei an.");
        using var client = await connection.CreateClientAsync(cancellationToken).ConfigureAwait(false);
        var uploaded = await UploadAttachmentsAsync(client, [audio], update, assistant, cancellationToken).ConfigureAwait(false);
        try
        {
            await update(new(GoAiAssistantUpdateKind.Status, assistant, Status: "Transkribiert", Detail: audio.FileName)).ConfigureAwait(false);
            var response = await client.TranscribeAsync(
                new TranscriptionRequest(
                    uploaded[0].Upload.UploadId,
                    string.Equals(settings.Current.LiveCaptionLanguage, "auto", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : settings.Current.LiveCaptionLanguage),
                cancellationToken).ConfigureAwait(false);
            var markdown = FormatTranscription(response, trigger.RemainingPrompt);
            return await CompleteImmediateAsync(assistant, markdown, update, response.Provider, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { await client.DeleteUploadAsync(uploaded[0].Upload.UploadId, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { RunDiagnostic(logger, uploaded[0].Upload.UploadId, "cleanup deferred", exception); }
        }
    }

    private async Task<ChatMessage> CompleteLiveCaptionsAsync(
        ChatMessage assistant,
        PromptTriggerMatch trigger,
        Func<GoAiAssistantUpdate, Task> update,
        CancellationToken cancellationToken)
    {
        var mode = LiveCaptionMode.Transcribe;
        await update(new(
            GoAiAssistantUpdateKind.Status,
            assistant,
            Status: "Live-Untertitel",
            Detail: "Windows-Systemaudio wird verbunden.")).ConfigureAwait(false);
        await liveCaptions.StartAsync(mode, cancellationToken).ConfigureAwait(false);
        var message = "Die Live-Untertitel für das Windows-Systemaudio wurden gestartet. Englisch erkannte Abschnitte werden automatisch ins Deutsche übersetzt; verschiedene Stimmen werden als Dialog gegliedert. Die Untertitel laufen parallel zum allgemeinen Chat und können in der Untertitelanzeige beendet werden.";
        return await CompleteImmediateAsync(
            assistant,
            message,
            update,
            "Whisper live",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ChatMessage> CompleteVoiceInputAsync(
        ChatMessage assistant,
        Func<GoAiAssistantUpdate, Task> update,
        CancellationToken cancellationToken)
    {
        await update(new(
            GoAiAssistantUpdateKind.Status,
            assistant,
            Status: "Sprachsteuerung",
            Detail: "Browser-Mikrofon steht für den Gesprächsmodus bereit.")).ConfigureAwait(false);
        return await CompleteImmediateAsync(
            assistant,
            "Starte den fortlaufenden Gesprächsmodus über das Mikrofonsymbol rechts im Promptfenster. GO fragt die Mikrofonfreigabe über WebView2 ab, zeigt den erkannten Text während des Sprechens direkt im Chat, sendet ihn nach einer kurzen Pause und liest die AI-Antwort automatisch vor. Ein erneuter Klick beendet den Gesprächsmodus.",
            update,
            "Whisper live",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ChatMessage> CompleteImmediateAsync(
        ChatMessage assistant,
        string content,
        Func<GoAiAssistantUpdate, Task> update,
        string provider,
        CancellationToken cancellationToken,
        IReadOnlyList<ChatArtifact>? resultArtifacts = null)
    {
        var contextSummary = GeneralAgentResponseParser.CreateContextSummary(null, content);
        await chats.UpdateMessageAsync(assistant.Id, content, MessageStatus.Completed, cancellationToken: cancellationToken).ConfigureAwait(false);
        await chats.SetMessageContextSummaryAsync(assistant.Id, contextSummary, cancellationToken).ConfigureAwait(false);
        var final = (await chats.ListMessagesAsync(assistant.SessionId, cancellationToken).ConfigureAwait(false)).Single(item => item.Id == assistant.Id);
        var session = await chats.GetSessionAsync(assistant.SessionId, cancellationToken).ConfigureAwait(false);
        await recentActivity.RecordAsync($"AI-Sitzung „{session?.Title ?? "Neue Sitzung"}“ bearbeitet", CancellationToken.None).ConfigureAwait(false);
        await update(new(GoAiAssistantUpdateKind.Completed, final, resultArtifacts, session, "Fertig", provider)).ConfigureAwait(false);
        return final;
    }

    private async Task<RunRequest> BuildRunRequestAsync(
        GoAiClient client,
        Guid sessionId,
        string originalPrompt,
        PromptTriggerMatch? trigger,
        IReadOnlyList<AssistantAttachment> sessionAttachments,
        IReadOnlyList<ChatMessage> historyBeforePrompt,
        IReadOnlyList<UploadedAttachment> uploaded,
        ChatMessage assistant,
        Func<GoAiAssistantUpdate, Task> update,
        CancellationToken cancellationToken)
    {
        _ = sessionAttachments;
        var session = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die AI-Sitzung wurde nicht gefunden.");
        var action = trigger?.Trigger.Action;
        var coding = action == PromptTriggerAction.Code;
        var audiobook = action == PromptTriggerAction.Audiobook;
        var contextProfile = coding
            ? SessionContextProfile.Code
            : audiobook
                ? SessionContextProfile.Audiobook
                : SessionContextProfile.General;
        var preferredGeneralModel = settings.Current.SelectedModel;
        if (string.IsNullOrWhiteSpace(preferredGeneralModel))
        {
            throw new InvalidOperationException("In den Einstellungen ist kein General-AI-Modell ausgewählt.");
        }

        DocumentRunContext? documentContext = null;
        if (!coding)
        {
            var minimumHistoryReserveTokens = CalculateDocumentHistoryReserveTokens(historyBeforePrompt, contextProfile);
            documentContext = await documentContexts.PrepareAsync(
                client,
                sessionId,
                assistant.Id,
                originalPrompt,
                preferredGeneralModel,
                minimumHistoryReserveTokens,
                async progress =>
                {
                    await update(new(
                        GoAiAssistantUpdateKind.Status,
                        assistant,
                        Status: progress.Status,
                        Detail: progress.Detail,
                        Model: progress.Model)).ConfigureAwait(false);
                    await update(new(GoAiAssistantUpdateKind.DocumentsChanged, assistant)).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }

        var sessionContext = await sessionContexts.PrepareAsync(
            client,
            sessionId,
            historyBeforePrompt,
            originalPrompt,
            preferredGeneralModel,
            coding,
            contextProfile,
            knownContextLength: coding ? 262_144 : documentContext?.ContextLength,
            knownHistoryBudgetCharacters: coding ? 120_000 : documentContext?.HistoryBudgetCharacters,
            async progress => await update(new(
                GoAiAssistantUpdateKind.Status,
                assistant,
                Status: progress.Status,
                Detail: progress.Detail,
                Model: coding ? "Laguna-S-2.1" : preferredGeneralModel,
                ContextLimit: coding ? 262_144 : documentContext?.ContextLength,
                ContextWasCompacted: true)).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        var messages = sessionContext.Messages.ToList();

        var hasAudiobookHistory = historyBeforePrompt.Any(static message =>
            message.Role == ChatRole.Assistant
            && message.ContentProfile == MessageContentProfile.Audiobook
            && !string.IsNullOrWhiteSpace(message.Content)
            && message.Status is MessageStatus.Completed or MessageStatus.Cancelled or MessageStatus.Interrupted);
        var transformed = TransformPrompt(
            originalPrompt,
            trigger,
            documentContext is not null,
            hasAudiobookHistory);
        var latestParts = new List<ContentPart> { new("text", Text: transformed) };
        foreach (var item in uploaded)
        {
            latestParts.Add(new ContentPart(
                "upload",
                UploadId: item.Upload.UploadId,
                MediaType: item.Attachment.ContentType,
                FileName: item.Attachment.FileName));
        }
        if (documentContext is not null)
        {
            latestParts.AddRange(documentContext.ContentParts);
        }
        WorkspaceDescriptor? workspaceDescriptor = null;
        if (coding)
        {
            var workspacePath = session.WorkspacePath;
            if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
            {
                throw new DirectoryNotFoundException("Der Coding-Workspace dieser Sitzung ist nicht verfügbar.");
            }
            await update(new(
                GoAiAssistantUpdateKind.Status,
                assistant,
                Status: "Repository wird indiziert",
                Detail: Path.GetFileName(workspacePath),
                ContextLimit: 262_144)).ConfigureAwait(false);
            var snapshot = await repositoryIndex.GetSnapshotAsync(workspacePath, cancellationToken).ConfigureAwait(false);
            var map = WorkspaceRepositoryIndex.BuildRepositoryMap(snapshot);
            workspaceDescriptor = new WorkspaceDescriptor(
                Path.GetFileName(snapshot.Root),
                snapshot.WorkspaceFingerprint,
                snapshot.RevisionFingerprint,
                map,
                snapshot.Entries.Count,
                snapshot.TextFileCount,
                snapshot.TextBytes,
                snapshot.IndexedAt,
                snapshot.IsTruncated);
            latestParts.Add(new ContentPart(
                "text",
                Text: $"[GO_WORKSPACE]\nDer dauerhaft an diese Sitzung gebundene Workspace '{Path.GetFileName(workspacePath)}' ist aktiv. Verwende relative Pfade ab '.'. Analysiere zuerst die Repositorykarte und lade anschließend relevante Dateien gebündelt mit fs.readMany."));
            await update(new(
                GoAiAssistantUpdateKind.Status,
                assistant,
                Status: "Repository bereit",
                Detail: $"{snapshot.Entries.Count:N0} Dateien indiziert · Laguna 262K",
                ContextUsed: EstimateRequestTokens(messages, latestParts, map),
                ContextLimit: 262_144,
                LoadedFiles: 0)).ConfigureAwait(false);
        }
        messages.Add(new RunMessage("user", latestParts));

        var mode = coding ? RunMode.Code
            : audiobook ? RunMode.General
            : action is PromptTriggerAction.Translation
                or PromptTriggerAction.BricsCad
                or PromptTriggerAction.WebSearch
                or PromptTriggerAction.YouTubeSearch
                ? RunMode.General
            : RunMode.Auto;
        if (action == PromptTriggerAction.BricsCad && !toolBroker.IsBricsCadAvailable)
        {
            throw new InvalidOperationException("Das GO-BricsCAD-Plugin ist nicht verbunden. Öffne BricsCAD und stelle die GO-Bridge-Verbindung her.");
        }
        var capabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (coding)
        {
            capabilities.UnionWith(toolBroker.GetAvailableCapabilities(session.WorkspacePath));
        }
        if (action == PromptTriggerAction.BricsCad && toolBroker.IsBricsCadAvailable)
        {
            capabilities.Add("bricscad");
        }
        if (documentContext is not null)
        {
            capabilities.Add("documents");
        }
        var clientCapabilities = capabilities
            .OrderBy(static capability => capability, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new RunRequest(
            GoAiProtocol.Version,
            mode,
            messages,
            uploaded.Select(item => item.Upload.UploadId).ToArray(),
            ClientCapabilities: clientCapabilities,
            Limits: new RunLimits(
                MaximumOutputTokens: 8_192,
                MaximumContextTokens: Math.Clamp(sessionContext.ContextLength, 1_024, 262_144),
                TimeoutSeconds: coding ? 14_400 : 3_600),
            SessionId: sessionId.ToString("D"),
            AllowedServerTools: GetAllowedServerTools(action),
            Workspace: workspaceDescriptor,
            PreferredGeneralModelId: coding ? null : preferredGeneralModel,
            DocumentContext: documentContext?.Descriptor,
            SessionContext: sessionContext.Descriptor,
            ConversationProfile: audiobook ? ConversationProfile.Audiobook : ConversationProfile.General);
    }

    internal static int CalculateDocumentHistoryReserveTokens(
        IReadOnlyList<ChatMessage> history,
        SessionContextProfile profile = SessionContextProfile.General)
    {
        var eligible = SessionContextPreparationService.SelectEligibleHistory(history, profile);
        if (eligible.Length == 0)
        {
            return 1_024;
        }

        var characters = eligible.Sum(static message => message.Content.Length + 96L);
        var estimatedTokens = (characters + 2L) / 3L;
        return (int)Math.Clamp(estimatedTokens, 4_096L, 16_384L);
    }

    internal static IReadOnlyList<RunMessage> BuildHistoryMessages(
        IReadOnlyList<ChatMessage> history,
        int historyBudget)
    {
        var messages = new List<RunMessage>();
        var eligibleHistory = history
            .Where(item => item.Status == MessageStatus.Completed
                && item.Role is ChatRole.User or ChatRole.Assistant
                && !string.IsNullOrWhiteSpace(item.Content))
            .ToArray();
        var selectedHistory = new Stack<ChatMessage>();
        var selectedCharacters = 0;
        for (var index = eligibleHistory.Length - 1; index >= 0; index--)
        {
            var remaining = historyBudget - selectedCharacters;
            var candidate = eligibleHistory[index];
            if (remaining <= 0 || candidate.Content.Length > remaining)
            {
                break;
            }
            selectedHistory.Push(candidate);
            selectedCharacters += candidate.Content.Length;
        }
        foreach (var message in selectedHistory)
        {
            if (!string.IsNullOrWhiteSpace(message.Content))
            {
                var parts = new List<ContentPart>();
                for (var offset = 0; offset < message.Content.Length;)
                {
                    var length = Math.Min(240_000, message.Content.Length - offset);
                    if (offset + length < message.Content.Length && char.IsHighSurrogate(message.Content[offset + length - 1]))
                    {
                        length--;
                    }
                    parts.Add(new ContentPart("text", Text: message.Content.Substring(offset, length)));
                    offset += length;
                }
                messages.Add(new RunMessage(
                    message.Role == ChatRole.Assistant ? "assistant" : "user",
                    parts));
            }
        }
        return messages;
    }

    internal static IReadOnlyList<string> GetAllowedServerTools(PromptTriggerAction? action) => action switch
    {
        PromptTriggerAction.WebSearch => ["web.search", "web.fetch"],
        PromptTriggerAction.YouTubeSearch => ["youtube.search", "web.fetch"],
        PromptTriggerAction.Code => ["math.evaluate"],
        PromptTriggerAction.Audiobook => [],
        _ => ["math.evaluate", "context.embed", "context.retrieve"],
    };

    internal static string RemoveDocumentEvidenceFooter(string content)
    {
        const string marker = "Verwendete Dokumentbelege:";
        var markerIndex = content.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return content;
        }

        var footerStart = markerIndex >= 2 && content.AsSpan(markerIndex - 2, 2).SequenceEqual("**")
            ? markerIndex - 2
            : markerIndex;
        var prefix = content[..footerStart];
        if (prefix.Length > 0
            && !prefix.EndsWith("\n\n", StringComparison.Ordinal)
            && !prefix.EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            return content;
        }

        return prefix.TrimEnd();
    }

    private async Task<IReadOnlyList<UploadedAttachment>> UploadAttachmentsAsync(
        GoAiClient client,
        IReadOnlyList<AssistantAttachment> source,
        Func<GoAiAssistantUpdate, Task> update,
        ChatMessage assistant,
        CancellationToken cancellationToken)
    {
        var result = new List<UploadedAttachment>();
        try
        {
            foreach (var attachment in source)
            {
                await update(new(GoAiAssistantUpdateKind.Status, assistant, Status: "Datei wird übertragen", Detail: attachment.FileName)).ConfigureAwait(false);
                var temporaryDirectory = Path.Combine(Path.GetTempPath(), "GO", "AI-Uploads");
                Directory.CreateDirectory(temporaryDirectory);
                var temporaryPath = Path.Combine(temporaryDirectory, $"{Guid.NewGuid():N}{Path.GetExtension(attachment.FileName)}");
                try
                {
                    await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, true))
                    {
                        await blobs.ExportAsync(attachment.BlobId, output, cancellationToken).ConfigureAwait(false);
                    }
                    var uploaded = await client.UploadFileAsync(temporaryPath, attachment.ContentType, cancellationToken: cancellationToken).ConfigureAwait(false);
                    result.Add(new UploadedAttachment(attachment, uploaded));
                }
                finally
                {
                    try { File.Delete(temporaryPath); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            return result;
        }
        catch
        {
            foreach (var uploaded in result)
            {
                try { await client.DeleteUploadAsync(uploaded.Upload.UploadId, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    RunDiagnostic(logger, uploaded.Upload.UploadId, "partial upload cleanup deferred", exception);
                }
            }
            throw;
        }
    }

    private async Task<ChatArtifact> DownloadArtifactAsync(
        GoAiClient client,
        Guid messageId,
        ArtifactDescriptor descriptor,
        string provider,
        CancellationToken cancellationToken)
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "GO", "AI-Artifacts");
        Directory.CreateDirectory(temporaryDirectory);
        var temporaryPath = Path.Combine(temporaryDirectory, $"{Guid.NewGuid():N}.download");
        try
        {
            await client.DownloadArtifactAsync(descriptor.ArtifactId, temporaryPath, cancellationToken: cancellationToken).ConfigureAwait(false);
            await using var input = new FileStream(temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, true);
            return await artifacts.ImportAsync(
                messageId,
                descriptor.ArtifactId,
                descriptor.FileName,
                descriptor.MediaType,
                descriptor.Sha256,
                descriptor.Length,
                provider,
                descriptor.Metadata,
                input,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch (IOException) { }
        }
    }

    private static AssistantAttachment? FindMediaAttachment(
        PromptTriggerAction action,
        IReadOnlyList<AssistantAttachment> source)
    {
        return source.LastOrDefault(item => action switch
        {
            PromptTriggerAction.ImageAnalysis => item.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase),
            PromptTriggerAction.VideoAnalysis => item.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase),
            _ => item.ContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase),
        });
    }

    private static string MissingMediaContextMessage(PromptTriggerAction action) => action switch
    {
        PromptTriggerAction.ImageAnalysis => "Hänge ein Bild oder Dokument an oder nimm zuerst ein Bild auf.",
        PromptTriggerAction.VideoAnalysis => "Hänge ein Video oder Dokument an oder nimm zuerst ein Video auf.",
        _ => "Hänge eine Audiodatei oder ein Dokument an oder nimm zuerst Audio auf.",
    };

    internal static PromptTriggerAction? InferMediaAnalysisAction(
        string prompt,
        IReadOnlyList<AssistantAttachment> source)
    {
        if (source.Count == 0 || string.IsNullOrWhiteSpace(prompt))
        {
            return null;
        }

        var normalized = prompt.Trim().ToLowerInvariant();
        var asksAboutMedia = normalized.Contains("zu sehen", StringComparison.Ordinal)
            || normalized.Contains("analysier", StringComparison.Ordinal)
            || normalized.Contains("beschreib", StringComparison.Ordinal)
            || normalized.Contains("erkennst du", StringComparison.Ordinal)
            || normalized.Contains("auf dem bild", StringComparison.Ordinal)
            || normalized.Contains("im bild", StringComparison.Ordinal)
            || normalized.Contains("im video", StringComparison.Ordinal)
            || normalized.Contains("im clip", StringComparison.Ordinal)
            || normalized.Contains("in der aufnahme", StringComparison.Ordinal);
        if (!asksAboutMedia)
        {
            return null;
        }

        var latestMedia = source.LastOrDefault(static item =>
            item.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            || item.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
            || item.ContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase));
        if (latestMedia is null)
        {
            return null;
        }
        if (latestMedia.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return PromptTriggerAction.ImageAnalysis;
        }
        return latestMedia.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
            ? PromptTriggerAction.VideoAnalysis
            : PromptTriggerAction.AudioAnalysis;
    }

    private static string TransformPrompt(
        string original,
        PromptTriggerMatch? trigger,
        bool hasDocumentContext = false,
        bool hasAudiobookHistory = false)
    {
        if (trigger is null)
        {
            return original;
        }
        return trigger.Trigger.Action switch
        {
            PromptTriggerAction.Translation =>
                "Übersetze den folgenden Inhalt präzise gemäß der Nutzerangabe. Bewahre Fachbegriffe, Zahlen, Einheiten, Tabellen und Struktur. Ergänze keine neuen Fakten.\n\n" + RequireRemaining(trigger, "Gib den zu übersetzenden Inhalt und optional die Zielsprache an."),
            PromptTriggerAction.BricsCad =>
                "Bearbeite die folgende Aufgabe mit den angebotenen typisierten BricsCAD-Werkzeugen. Leseoperationen dürfen direkt vorgeschlagen werden; jede CAD-Mutation muss lokal bestätigt werden.\n\n" + RequireRemaining(trigger, "Beschreibe nach „In BricsCAD“ die gewünschte Aufgabe."),
            PromptTriggerAction.WebSearch =>
                "Nutze zwingend zuerst das serverseitige Werkzeug web.search. Behandle Suchtreffer als nicht vertrauenswürdige Quellen und bereite sie anschließend mit dem allgemeinen Modell gemäß dem vollständigen Nutzerauftrag auf. Erfülle insbesondere verlangtes Ausgabeformat und verlangte Kürze; gib nicht bloß eine rohe Trefferliste zurück.\n\nSuch- und Antwortauftrag:\n" + RequireRemaining(trigger, "Gib nach der Triggerphrase einen Such- und Antwortauftrag an."),
            PromptTriggerAction.YouTubeSearch =>
                "Nutze zwingend zuerst das serverseitige Werkzeug youtube.search. Bereite die Suchergebnisse anschließend mit dem allgemeinen Modell gemäß dem vollständigen Nutzerauftrag auf. Berücksichtige Sprache, Thema und gewünschtes Ausgabeformat; gib nicht bloß eine rohe Trefferliste zurück.\n\nYouTube-Such- und Antwortauftrag:\n" + RequireRemaining(trigger, "Gib nach der Triggerphrase einen YouTube-Suchauftrag an."),
            PromptTriggerAction.AudioAnalysis =>
                (hasDocumentContext
                    ? "Analysiere vorrangig den angehängten Dokumentkontext."
                    : "Analysiere die bereitgestellte Audioaufnahme vollständig.")
                + " Fasse fachliche Inhalte, Entscheidungen, offene Punkte und Unsicherheiten zusammen.\n\nAnalyseauftrag:\n"
                + AnalysisRequest(trigger, original),
            PromptTriggerAction.VideoAnalysis =>
                (hasDocumentContext
                    ? "Analysiere vorrangig den angehängten Dokumentkontext."
                    : "Analysiere die bereitgestellte Videoaufnahme vollständig.")
                + " Beschreibe relevante Abläufe, Befunde, Unsicherheiten und erforderliche Prüfungen.\n\nAnalyseauftrag:\n"
                + AnalysisRequest(trigger, original),
            PromptTriggerAction.ImageAnalysis =>
                (hasDocumentContext
                    ? "Analysiere vorrangig den angehängten Dokumentkontext."
                    : "Analysiere das bereitgestellte Bild vollständig.")
                + " Nenne relevante Befunde, Unsicherheiten und erforderliche fachliche Prüfungen.\n\nAnalyseauftrag:\n"
                + AnalysisRequest(trigger, original),
            PromptTriggerAction.Code => RequireRemaining(trigger, "Beschreibe nach der Triggerphrase die Codeaufgabe."),
            PromptTriggerAction.Audiobook => BuildAudiobookPrompt(trigger, original, hasAudiobookHistory),
            _ => original,
        };
    }

    internal static string BuildAudiobookPrompt(
        PromptTriggerMatch trigger,
        string original,
        bool hasAudiobookHistory)
    {
        var direction = trigger.RemainingPrompt.Trim();
        if (string.Equals(direction, original.Trim(), StringComparison.Ordinal))
        {
            direction = StripAudiobookCommand(direction);
        }

        if (!hasAudiobookHistory)
        {
            if (IsContinuationCommand(original))
            {
                throw new InvalidOperationException(
                    "In dieser Sitzung ist noch keine Hörbuchgeschichte vorhanden. Starte zuerst mit „Hörbuch erstellen“.");
            }
            if (string.IsNullOrWhiteSpace(direction))
            {
                throw new InvalidOperationException(
                    "Beschreibe nach „Hörbuch erstellen“ das Szenario, die Handlung oder die gewünschten Figuren.");
            }
            return "Verfasse das erste Kapitel einer neuen, fortlaufenden Hörbuchgeschichte. "
                + "Das Kapitel soll etwa eintausendfünfhundert bis zweitausendfünfhundert Wörter umfassen und unmittelbar "
                + "mit einer prägnanten, inhaltlich passenden Kapitelüberschrift im Format „# Kapitel eins – Titel“ beginnen. "
                + "Schreibe danach fließende Prosa. Schreibe sämtliche Zahlenwerte natürlich als deutsche Wörter aus; "
                + "verwende im Kapitel keine Ziffern oder Prozentzeichen, sondern beispielsweise „zwei Prozent“. "
                + "Erschaffe mindestens eine Hauptfigur und erzähle konsequent aus ihrer Wahrnehmung. "
                + "Behandle alle genannten Handlungen als langfristigen Leitfaden einer potenziell unbegrenzten Serie: "
                + "Verwende jetzt nur den organisch passenden Anfang und bewahre spätere Ereignisse als zukünftige Handlungsfäden.\n\n"
                + "Langfristige Vorgabe für die Geschichte:\n" + direction;
        }

        var steering = string.IsNullOrWhiteSpace(direction)
            ? "Setze die unmittelbar letzte Szene schlüssig fort, ohne den bisherigen Verlauf zusammenzufassen."
            : "Setze die unmittelbar letzte Szene schlüssig fort. Behandle die folgende Richtungsangabe als langfristigen "
                + "Serienleitfaden und verwende in diesem Kapitel nur den Teil, der organisch an die aktuelle Szene anschließt:\n"
                + direction;
        return steering
            + "\n\nSchreibe den nächsten zusammenhängenden Hörbuchabschnitt mit etwa eintausendfünfhundert bis "
            + "zweitausendfünfhundert Wörtern. Ein neuer AI-Lauf ist ausdrücklich keine Kapitelgrenze. Solange Szene und "
            + "Kapitelbogen offen sind, setze ohne neue Kapitelüberschrift fort. Nur wenn das bisherige Kapitel narrativ "
            + "abgeschlossen ist und jetzt tatsächlich ein neues Kapitel beginnt, setze direkt vor dessen ersten Absatz "
            + "eine prägnante passende Überschrift im Format „# Kapitel ausgeschriebene Nummer – Titel“. Setze niemals eine "
            + "Kapitelüberschrift ans Antwortende, ohne das neue Kapitel danach zu beginnen. Schreibe sämtliche Zahlenwerte "
            + "natürlich als deutsche Wörter aus; "
            + "verwende im Kapitel keine Ziffern oder Prozentzeichen, sondern beispielsweise „zwei Prozent“. "
            + "Beginne direkt nach dem letzten Szenenanker, bleibe in der Perspektive der Hauptfigur und wiederhole bereits "
            + "erzählte Passagen nicht. Bewahre noch nicht umgesetzte Vorgaben ausdrücklich für spätere Kapitel.";
    }

    private static string StripAudiobookCommand(string value)
    {
        string[] commands = ["Hörbuch erstellen", "Hoerbuch erstellen", "Hörbuch fortsetzen", "Hoerbuch fortsetzen", "Fortsetzen"];
        foreach (var command in commands)
        {
            if (value.StartsWith(command, StringComparison.OrdinalIgnoreCase))
            {
                return value[command.Length..].TrimStart(' ', ':', '-', '–', '—').Trim();
            }
        }
        return value.Trim();
    }

    private static bool IsContinuationCommand(string value)
    {
        var normalized = value.Trim();
        return normalized.StartsWith("Hörbuch fortsetzen", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("Hoerbuch fortsetzen", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Fortsetzen", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("Fortsetzen:", StringComparison.OrdinalIgnoreCase);
    }

    private static string AnalysisRequest(PromptTriggerMatch trigger, string original) =>
        string.IsNullOrWhiteSpace(trigger.RemainingPrompt)
            ? original
            : trigger.RemainingPrompt;

    private static string ExtractToolResultText(JsonElement data)
    {
        if (!data.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }
        if (result.TryGetProperty("analysis", out var analysis) && analysis.ValueKind == JsonValueKind.String)
        {
            var text = analysis.GetString() ?? string.Empty;
            if (result.TryGetProperty("transcription", out var transcription)
                && transcription.ValueKind == JsonValueKind.Object
                && transcription.TryGetProperty("text", out var transcriptText)
                && transcriptText.ValueKind == JsonValueKind.String)
            {
                text += "\n\n### Transkript\n\n" + transcriptText.GetString();
            }
            return text;
        }
        return string.Empty;
    }

    private static string FormatSearchResults(WebSearchResponse response, bool youtube)
    {
        var builder = new StringBuilder();
        builder.AppendLine(youtube ? "## YouTube-Suchergebnisse" : "## Websuchergebnisse");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"Suche: **{EscapeMarkdown(response.Query)}** · Anbieter: {EscapeMarkdown(response.Provider)}{(response.IsFallback ? " (Fallback)" : string.Empty)}");
        builder.AppendLine();
        if (response.Results.Count == 0)
        {
            builder.AppendLine("Keine Treffer gefunden.");
        }
        foreach (var (item, index) in response.Results.Select((value, index) => (value, index)))
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"{index + 1}. [{EscapeMarkdownLinkLabel(item.Title)}]({item.Url})");
            if (!string.IsNullOrWhiteSpace(item.Snippet))
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"   {EscapeMarkdown(item.Snippet)}");
            }
            var metadata = new[] { item.Source, item.PublishedAt?.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.CurrentCulture), item.Duration }
                .Where(value => !string.IsNullOrWhiteSpace(value));
            var metadataText = string.Join(" · ", metadata);
            if (metadataText.Length > 0)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"   *{EscapeMarkdown(metadataText)}*");
            }
        }
        return builder.ToString().Trim();
    }

    private static string FormatTranscription(TranscriptionResponse response, string? instruction)
    {
        var builder = new StringBuilder("## Transkript\n\n");
        builder.AppendLine(response.Text.Trim());
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"Sprache: **{EscapeMarkdown(response.Language)}** · Anbieter: **{EscapeMarkdown(response.Provider)}**");
        if (!string.IsNullOrWhiteSpace(instruction))
        {
            builder.AppendLine();
            builder.AppendLine(CultureInfo.InvariantCulture, $"Auftrag: {EscapeMarkdown(instruction)}");
        }
        if (response.Segments.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("### Zeitsegmente");
            builder.AppendLine();
            foreach (var segment in response.Segments)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- `{TimeSpan.FromSeconds(segment.Start):mm\\:ss}–{TimeSpan.FromSeconds(segment.End):mm\\:ss}` {EscapeMarkdown(segment.Text)}");
            }
        }
        return builder.ToString().Trim();
    }

    private static string EscapeMarkdown(string? value) => (value ?? string.Empty)
        .Replace('\r', ' ')
        .Replace('\n', ' ')
        .Replace('|', '¦')
        .Trim();

    private static string EscapeMarkdownLinkLabel(string? value) => EscapeMarkdown(value)
        .Replace('[', '(')
        .Replace(']', ')');

    private static string AppendContent(string current, string next) => string.IsNullOrWhiteSpace(current)
        ? next.Trim()
        : current.TrimEnd() + "\n\n" + next.Trim();

    private static string StringProperty(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string RequireRemaining(PromptTriggerMatch trigger, string error)
    {
        if (string.IsNullOrWhiteSpace(trigger.RemainingPrompt))
        {
            throw new InvalidOperationException(error);
        }
        return trigger.RemainingPrompt;
    }

    private static string BoundText(string value, int maximum) => value.Length <= maximum ? value : value[..maximum] + "\n[gekürzt]";

    private static int EstimateRequestTokens(
        IReadOnlyList<RunMessage> messages,
        IReadOnlyList<ContentPart> latestParts,
        string repositoryMap)
    {
        var characters = repositoryMap.Length
            + messages.SelectMany(static message => message.Content).Sum(static part => part.Text?.Length ?? 0)
            + latestParts.Sum(static part => part.Text?.Length ?? 0);
        return Math.Max(1, (characters + 2) / 3);
    }

    private static string VisibleFailure(Exception exception) => exception switch
    {
        GoAiRunTerminalException => exception.Message,
        DirectoryNotFoundException => exception.Message,
        FileNotFoundException => exception.Message,
        InvalidDataException => exception.Message,
        InvalidOperationException => exception.Message,
        TimeoutException => "Der Coding-Lauf hat sein Zeitlimit erreicht. Bereits gewonnene Ergebnisse wurden beibehalten.",
        _ => "Der GO-AI-Auftrag konnte nicht abgeschlossen werden.",
    };

    private static string ToStorage(RunState state)
    {
        var value = state.ToString();
        return $"{char.ToLowerInvariant(value[0])}{value[1..]}";
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _activeCancellation?.Cancel();
        _activeCancellation?.Dispose();
        _gate.Dispose();
    }

    internal sealed record DialogueDirectionBatch(
        IReadOnlyList<int> SegmentIndexes,
        IReadOnlyList<SpeechSourceUnit> ContextUnits);

    private sealed record DialogueDirectionResponse(
        IReadOnlyList<DialogueDirectionItem>? Segments);

    private sealed record DialogueDirectionItem(
        string Id,
        string SynthesisText,
        string? Mood = null,
        double? Intensity = null,
        string? ExpressionBefore = null,
        string? ExpressionAfter = null);

    private sealed record DialogueDirectionResult(
        IReadOnlyList<PreparedSpeechSegment> Segments,
        bool Cacheable);

    private sealed class DialogueDirectionSession
    {
        private readonly object _gate = new();
        private readonly Dictionary<int, TaskCompletionSource<PreparedSpeechSegment>> _pending = [];
        private readonly PreparedSpeechSegment[] _resolved;
        private bool _cacheable = true;
        private int _playbackStarted;

        public DialogueDirectionSession(IReadOnlyList<PreparedSpeechSegment> segments)
        {
            SourceSegments = segments.ToArray();
            _resolved = SourceSegments.ToArray();
            for (var index = 0; index < SourceSegments.Length; index++)
            {
                if (SourceSegments[index].Delivery == SpeechDelivery.Dialogue)
                {
                    _pending[index] = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
        }

        public PreparedSpeechSegment[] SourceSegments { get; }

        public Task<DialogueDirectionResult> Completion { get; set; } =
            Task.FromResult(new DialogueDirectionResult([], false));

        public bool IsCompleted => Completion.IsCompleted;

        public bool PlaybackStarted => Volatile.Read(ref _playbackStarted) != 0;

        public void MarkPlaybackStarted() => Interlocked.Exchange(ref _playbackStarted, 1);

        public ValueTask<PreparedSpeechSegment> ResolveAsync(
            int index,
            PreparedSpeechSegment source,
            CancellationToken cancellationToken)
        {
            if (!_pending.TryGetValue(index, out var completion))
            {
                return ValueTask.FromResult(source);
            }
            return new(completion.Task.WaitAsync(cancellationToken));
        }

        public void Resolve(int index, PreparedSpeechSegment segment, bool cacheable)
        {
            TaskCompletionSource<PreparedSpeechSegment>? completion;
            lock (_gate)
            {
                if (!_pending.TryGetValue(index, out completion) || completion.Task.IsCompleted)
                {
                    return;
                }
                _resolved[index] = segment;
                if (!cacheable) _cacheable = false;
            }
            completion.TrySetResult(segment);
        }

        public void ResolveNeutral(IEnumerable<int> indexes, bool cacheable)
        {
            foreach (var index in indexes)
            {
                if (index < 0 || index >= SourceSegments.Length) continue;
                var source = SourceSegments[index];
                Resolve(index, source with
                {
                    Mood = SpeechMood.Neutral,
                    Intensity = 0,
                    Speed = 1.0,
                    PauseAfterMilliseconds = 0,
                    ExpressionBefore = null,
                    ExpressionAfter = null,
                    SynthesisText = source.Text,
                    DirectionResolved = false,
                }, cacheable);
            }
        }

        public void ResolveAllNeutral(bool cacheable)
        {
            int[] unresolved;
            lock (_gate)
            {
                unresolved = _pending
                    .Where(static pair => !pair.Value.Task.IsCompleted)
                    .Select(static pair => pair.Key)
                    .ToArray();
            }
            if (unresolved.Length > 0)
            {
                ResolveNeutral(unresolved, cacheable);
            }
        }

        public void Cancel(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _cacheable = false;
                foreach (var completion in _pending.Values)
                {
                    completion.TrySetCanceled(cancellationToken);
                }
            }
        }

        public DialogueDirectionResult CreateResult()
        {
            lock (_gate)
            {
                return new(_resolved.ToArray(), _cacheable);
            }
        }
    }

    private sealed record SpeechSource(
        string Text,
        string Kind,
        string? Detail,
        Guid? MessageId,
        MessageContentProfile ContentProfile);

    private sealed record UploadedAttachment(AssistantAttachment Attachment, UploadCompleted Upload);
}
