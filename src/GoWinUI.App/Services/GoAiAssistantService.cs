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
using System.Text.RegularExpressions;

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

internal sealed class GoAiRunTerminalException(
    string errorCode,
    string message,
    bool retryable)
    : InvalidOperationException(message)
{
    public string ErrorCode { get; } = errorCode;

    public bool Retryable { get; } = retryable;
}

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
    SystemAudioCaptionService liveCaptions,
    MicrophoneTranscriptionService microphone,
    SettingsCoordinator settings,
    RecentActivityService recentActivity,
    ILogger<GoAiAssistantService> logger) : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = GoAiProtocol.CreateJsonOptions();
    private static readonly Action<ILogger, string, string, Exception?> RunDiagnostic = LoggerMessage.Define<string, string>(
        LogLevel.Information,
        new EventId(5300, nameof(RunDiagnostic)),
        "GO AI Client run {RunId}: {State}.");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _speechGate = new(1, 1);
    private CancellationTokenSource? _activeCancellation;
    private CancellationTokenSource? _activeSpeechCancellation;
    private string? _activeServerRunId;
    private string? _activeSessionId;
    private int _explicitCancellation;
    private int _startupRunsStopped;
    private int _speechActive;
    private int _disposed;

    public bool IsRunning => _gate.CurrentCount == 0;

    public bool IsSpeaking => Volatile.Read(ref _speechActive) != 0;

    public Guid? ActiveSessionId =>
        Guid.TryParse(Volatile.Read(ref _activeSessionId), out var sessionId)
            ? sessionId
            : null;

    public Task<ChatMessage> SendAsync(
        Guid sessionId,
        string prompt,
        PromptTriggerMatch? trigger,
        Func<GoAiAssistantUpdate, Task> update,
        CancellationToken cancellationToken = default) =>
        SendCoreAsync(
            sessionId, prompt, trigger, update, cancellationToken);

    private async Task<ChatMessage> SendCoreAsync(
        Guid sessionId,
        string prompt,
        PromptTriggerMatch? trigger,
        Func<GoAiAssistantUpdate, Task> update,
        CancellationToken cancellationToken)
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
        Volatile.Write(ref _activeSessionId, sessionId.ToString("D"));
        _activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var session = await chats.GetSessionAsync(sessionId, _activeCancellation.Token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Die AI-Sitzung wurde nicht gefunden.");
            var historyBeforePrompt = await chats.ListMessagesAsync(sessionId, _activeCancellation.Token).ConfigureAwait(false);
            var sessionAttachments = await attachments.ListAsync(sessionId, _activeCancellation.Token).ConfigureAwait(false);
            var action = trigger?.Trigger.Action;
            var contentProfile = action == PromptTriggerAction.Audiobook
                ? MessageContentProfile.Audiobook
                : MessageContentProfile.General;
            var turn = await chats.AddTurnAsync(
                sessionId,
                prompt.Trim(),
                contentProfile,
                _activeCancellation.Token).ConfigureAwait(false);
            sessionAttachments = await BindCapturedMediaToMessageAsync(
                turn.UserMessage,
                sessionAttachments,
                _activeCancellation.Token).ConfigureAwait(false);
            var assistant = turn.AssistantMessage;
            var contextLimit = ModelContextProfiles.ResolveMaximum(settings.Current.SelectedModel, "general");
            var initialModel = settings.Current.SelectedModel;
            await update(new(
                GoAiAssistantUpdateKind.Started,
                assistant,
                Status: "Denkt nach",
                Detail: "0 Token",
                Model: initialModel,
                ContextLimit: contextLimit)).ConfigureAwait(false);

            try
            {
                return action switch
                {
                    PromptTriggerAction.Transcription => await CompleteTranscriptionAsync(assistant, trigger!, update, _activeCancellation.Token).ConfigureAwait(false),
                    PromptTriggerAction.VoiceInput => await CompleteVoiceInputAsync(assistant, update, _activeCancellation.Token).ConfigureAwait(false),
                    PromptTriggerAction.LiveCaptions or PromptTriggerAction.LiveTranslation =>
                        await CompleteLiveCaptionsAsync(assistant, trigger!, update, _activeCancellation.Token).ConfigureAwait(false),
                    _ => await CompleteRunWithRetryAsync(
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
                var current = await chats.GetMessageAsync(assistant.Id, CancellationToken.None).ConfigureAwait(false)
                    ?? assistant;
                await chats.UpdateMessageAsync(current.Id, current.Content, MessageStatus.Cancelled, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                var cancelled = current with { Status = MessageStatus.Cancelled, UpdatedAt = DateTimeOffset.UtcNow };
                await update(new(GoAiAssistantUpdateKind.Cancelled, cancelled, Status: "Abgebrochen")).ConfigureAwait(false);
                return cancelled;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                var current = await chats.GetMessageAsync(assistant.Id, CancellationToken.None).ConfigureAwait(false)
                    ?? assistant;
                var visible = string.IsNullOrWhiteSpace(current.Content)
                    ? VisibleFailure(exception)
                    : current.Content;
                await chats.UpdateMessageAsync(assistant.Id, visible, MessageStatus.Failed, exception.Message, CancellationToken.None).ConfigureAwait(false);
                var failed = await chats.GetMessageAsync(assistant.Id, CancellationToken.None).ConfigureAwait(false)
                    ?? current with { Content = visible, Status = MessageStatus.Failed, Error = exception.Message };
                await update(new(GoAiAssistantUpdateKind.Failed, failed, Error: exception.Message, Status: "Fehlgeschlagen")).ConfigureAwait(false);
                return failed;
            }
        }
        finally
        {
            _activeServerRunId = null;
            Volatile.Write(ref _activeSessionId, null);
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
            Volatile.Write(ref _activeSessionId, run.SessionId.ToString("D"));
            _activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                var message = await chats.GetMessageAsync(
                    run.AssistantMessageId,
                    cancellationToken: _activeCancellation.Token).ConfigureAwait(false);
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
                Volatile.Write(ref _activeSessionId, null);
                _activeCancellation?.Dispose();
                _activeCancellation = null;
                _gate.Release();
            }
        }
    }

    public async Task StopPersistedRunsAtStartupAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _startupRunsStopped, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var serverRunIds = await StopPersistedRunsLocallyAsync(
                runs,
                chats,
                cancellationToken).ConfigureAwait(false);
            await CancelPersistedServerRunsAsync(serverRunIds, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Exchange(ref _startupRunsStopped, 0);
            throw;
        }
    }

    internal static async Task<IReadOnlyList<string>> StopPersistedRunsLocallyAsync(
        IGoAiRunRepository runRepository,
        IChatRepository chatRepository,
        CancellationToken cancellationToken = default)
    {
        var staleRuns = await runRepository.ListResumableAsync(cancellationToken).ConfigureAwait(false);
        var serverRunIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var run in staleRuns)
        {
            await runRepository.UpdateAsync(
                run.Id,
                run.ServerRunId,
                run.LastEventId,
                "cancelled",
                run.SelectedModel,
                "client.run_stopped_on_start",
                CancellationToken.None).ConfigureAwait(false);

            var message = await chatRepository.GetMessageAsync(
                run.AssistantMessageId,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            if (message?.Status is MessageStatus.Pending or MessageStatus.Streaming or MessageStatus.Interrupted)
            {
                var content = string.IsNullOrWhiteSpace(message.Content)
                    ? "Der vorherige AI-Lauf wurde beim Clientstart gestoppt."
                    : message.Content;
                await chatRepository.UpdateMessageAsync(
                    message.Id,
                    content,
                    MessageStatus.Cancelled,
                    "Der AI-Lauf wurde beim Clientstart gestoppt.",
                    CancellationToken.None).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(run.ServerRunId))
            {
                _ = serverRunIds.Add(run.ServerRunId);
            }
        }

        return [.. serverRunIds];
    }

    private async Task CancelPersistedServerRunsAsync(
        IReadOnlyList<string> serverRunIds,
        CancellationToken cancellationToken)
    {
        if (serverRunIds.Count == 0)
        {
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var client = await connection.CreateClientAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(serverRunIds.Select(async serverRunId =>
            {
                try
                {
                    await client.CancelRunAsync(serverRunId, timeout.Token).ConfigureAwait(false);
                    RunDiagnostic(logger, serverRunId, "cancelled during client startup", null);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    RunDiagnostic(logger, serverRunId, $"startup cancel request failed ({exception.GetType().Name})", exception);
                }
            })).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            foreach (var serverRunId in serverRunIds)
            {
                RunDiagnostic(logger, serverRunId, $"startup cancel connection failed ({exception.GetType().Name})", exception);
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

    public async Task CancelSpeechAsync(CancellationToken cancellationToken = default)
    {
        var speechCancellation = Volatile.Read(ref _activeSpeechCancellation);
        try
        {
            speechCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The playback completed between reading and cancelling its token.
        }
        await microphone.StopSpeechAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ValidateSpeechStartAsync(
        Guid sessionId,
        Guid sourceMessageId,
        DateTimeOffset expectedUpdatedAt,
        SpeechStartAnchor anchor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        var message = (await chats.ListMessagesAsync(sessionId, cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(item => item.Id == sourceMessageId)
            ?? throw new InvalidOperationException("Die ausgewählte AI-Nachricht wurde nicht gefunden.");
        ValidateAnchoredSpeechMessage(message, sessionId, expectedUpdatedAt, anchor);
    }

    public async Task SpeakAsync(
        Guid sessionId,
        string? explicitText,
        Guid? sourceMessageId,
        Func<GoAiSpeechUpdate, Task> update,
        Func<SpeechPlaybackProgress, Task>? progress = null,
        SpeechStartAnchor? startAnchor = null,
        DateTimeOffset? expectedMessageUpdatedAt = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(update);
        var playbackId = Guid.NewGuid();
        long playbackEventSequence = 0;

        // Read-aloud is an independent media operation and must not acquire the
        // chat/run gate while a response is still being generated.
        await _speechGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _speechActive, 1);
        var speechCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeSpeechCancellation = speechCancellation;
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
                startAnchor,
                expectedMessageUpdatedAt,
                speechCancellation.Token).ConfigureAwait(false);
            var speechStatusDetail = VisibleSpeechSourceDetail(source.Detail);
            var cleanedSource = MicrophoneTranscriptionService.PrepareSpeechText(source.Text);
            if (string.IsNullOrWhiteSpace(cleanedSource))
            {
                throw new InvalidOperationException("Es ist kein vorlesbarer Text vorhanden.");
            }

            var allSourceUnits = SpeechSourceSegmentation.CreateUnits(source.Text);
            if (allSourceUnits.Count == 0)
            {
                allSourceUnits = SpeechSourceSegmentation.CreateUnits(cleanedSource);
            }
            var sourceUnits = SelectSpeechUnitsFromAnchor(allSourceUnits, startAnchor);
            if (startAnchor is not null)
            {
                cleanedSource = string.Join(
                    Environment.NewLine,
                    sourceUnits.Select(static unit => unit.SpeechText));
            }
            // Every source uses the same deterministic SpeechPlan. No LLM is
            // involved in read-aloud preparation, regardless of content type.
            var speechSegments = SpeechSourceSegmentation.CreateDirectSegments(
                sourceUnits,
                cleanedSource);
            await update(new(
                true,
                "Sprachausgabe wird erzeugt",
                CombineSpeechDetail(speechStatusDetail, "Sprechtext wird deterministisch vorbereitet."),
                DisplaySpeechProvider(null))).ConfigureAwait(false);

            if (speechSegments.Count == 0)
            {
                throw new InvalidOperationException("Es konnten keine vorlesbaren Sprachsegmente erstellt werden.");
            }
            var speechPlanHash = HashSpeechPlan(speechSegments);

            await update(new(
                true,
                "Sprachausgabe wird erzeugt",
                speechStatusDetail,
                DisplaySpeechProvider(null),
                CacheHit: false)).ConfigureAwait(false);
            if (progress is not null)
            {
                await progress(new(
                    sessionId,
                    source.MessageId,
                    source.Kind,
                    playbackId,
                    Interlocked.Increment(ref playbackEventSequence),
                    0,
                    speechSegments.Count,
                    [],
                    SpeechPlaybackState.Buffering,
                    source.MessageId is null ? null : sourceUnits)).ConfigureAwait(false);
            }
            string? speechProvider;
            var playbackStarted = 0;
            speechProvider = await microphone.PlaySegmentsAsync(
                    speechSegments,
                    progress: async playback =>
                    {
                        if (playback.State == SpeechPlaybackState.Playing
                            && Interlocked.CompareExchange(ref playbackStarted, 1, 0) == 0)
                        {
                            await update(new(
                                true,
                                "Sprachausgabe wird wiedergegeben",
                                speechStatusDetail,
                                DisplaySpeechProvider(playback.Provider),
                                CacheHit: false)).ConfigureAwait(false);
                        }
                        if (progress is null) return;
                        if (!string.Equals(
                            speechPlanHash,
                            HashSpeechPlan(speechSegments),
                            StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException(
                                "Die sichtbare Vorlesemarkierung hat den vorbereiteten Sprachplan verändert.");
                        }
                        var activeIndex = Math.Clamp(playback.SegmentIndex, 0, speechSegments.Count - 1);
                        // The WebView highlights exactly one visible sentence. A
                        // synthesized technical split may continue to reference
                        // that same sentence, but it must never activate multiple
                        // source ranges at once.
                        var sourceUnitId = speechSegments[activeIndex].SourceUnitIds
                            .FirstOrDefault(static id => !string.IsNullOrWhiteSpace(id));
                        IReadOnlyList<string> sourceUnitIds = sourceUnitId is null
                            ? []
                            : [sourceUnitId];
                        await progress(new(
                            sessionId,
                            source.MessageId,
                            source.Kind,
                            playbackId,
                            Interlocked.Increment(ref playbackEventSequence),
                            activeIndex,
                            speechSegments.Count,
                            sourceUnitIds,
                            playback.State)).ConfigureAwait(false);
                    },
                    profile: SpeechContentProfile.Prepared,
                    cancellationToken: speechCancellation.Token).ConfigureAwait(false);
            if (progress is not null)
            {
                await progress(new(
                    sessionId,
                    source.MessageId,
                    source.Kind,
                    playbackId,
                    Interlocked.Increment(ref playbackEventSequence),
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
                CacheHit: false)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (progress is not null)
            {
                await progress(new(
                    sessionId,
                    sourceMessageId,
                    "Vorlesen",
                    playbackId,
                    Interlocked.Increment(ref playbackEventSequence),
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
                    playbackId,
                    Interlocked.Increment(ref playbackEventSequence),
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
            Interlocked.Exchange(ref _speechActive, 0);
            if (ReferenceEquals(_activeSpeechCancellation, speechCancellation))
            {
                _activeSpeechCancellation = null;
            }
            speechCancellation.Dispose();
            _speechGate.Release();
        }
    }

    private async Task<SpeechSource> ResolveSpeechSourceAsync(
        Guid sessionId,
        string? explicitText,
        Guid? sourceMessageId,
        SpeechStartAnchor? startAnchor,
        DateTimeOffset? expectedMessageUpdatedAt,
        CancellationToken cancellationToken)
    {
        var history = await chats.ListMessagesAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (sourceMessageId is { } requestedMessageId)
        {
            var selected = history.FirstOrDefault(message => message.Id == requestedMessageId)
                ?? throw new InvalidOperationException("Die ausgewählte AI-Nachricht wurde nicht gefunden.");
            if (startAnchor is null)
            {
                if (!IsReadableSpeechMessage(selected))
                {
                    throw new InvalidOperationException("Die ausgewählte gespeicherte AI-Nachricht kann nicht vorgelesen werden.");
                }
            }
            else
            {
                ValidateAnchoredSpeechMessage(
                    selected,
                    sessionId,
                    expectedMessageUpdatedAt
                        ?? throw new InvalidOperationException("Der Nachrichtenstand für den Vorlesestart fehlt."),
                    startAnchor);
            }
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

    internal static string HashSpeechPlan(IReadOnlyList<PreparedSpeechSegment> segments) =>
        HashSpeechValue(JsonSerializer.Serialize(segments, JsonOptions));


    internal static void ValidateAnchoredSpeechMessage(
        ChatMessage message,
        Guid sessionId,
        DateTimeOffset expectedUpdatedAt,
        SpeechStartAnchor anchor)
    {
        if (message.SessionId != sessionId
            || !IsReadableSpeechMessage(message))
        {
            throw new InvalidOperationException("Diese AI-Nachricht kann nicht ab der gewählten Stelle vorgelesen werden.");
        }
        if (message.UpdatedAt.ToUniversalTime() != expectedUpdatedAt.ToUniversalTime())
        {
            throw new InvalidOperationException("Die AI-Nachricht wurde inzwischen geändert. Wähle die Vorlesestelle erneut aus.");
        }

        _ = SelectSpeechUnitsFromAnchor(
            SpeechSourceSegmentation.CreateUnits(message.Content),
            anchor);
    }

    internal static bool IsReadableSpeechMessage(ChatMessage message) =>
        message.Role == ChatRole.Assistant
        && message.Status is (MessageStatus.Completed
            or MessageStatus.Cancelled
            or MessageStatus.Interrupted
            or MessageStatus.Failed)
        && !string.IsNullOrWhiteSpace(message.Content);

    internal static IReadOnlyList<SpeechSourceUnit> SelectSpeechUnitsFromAnchor(
        IReadOnlyList<SpeechSourceUnit> sourceUnits,
        SpeechStartAnchor? anchor)
    {
        if (anchor is null)
        {
            return sourceUnits;
        }

        var firstIndex = -1;
        for (var index = 0; index < sourceUnits.Count; index++)
        {
            if (sourceUnits[index].BlockIndex == anchor.BlockIndex
                && string.Equals(sourceUnits[index].Kind, anchor.Kind, StringComparison.Ordinal))
            {
                firstIndex = index;
                break;
            }
        }
        if (firstIndex < 0)
        {
            throw new InvalidOperationException("Der ausgewählte Textblock ist nicht mehr eindeutig vorhanden.");
        }
        return sourceUnits.Skip(firstIndex).ToArray();
    }


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

    private async Task<ChatMessage> CompleteRunWithRetryAsync(
        ChatMessage assistant,
        string originalPrompt,
        PromptTriggerMatch? trigger,
        IReadOnlyList<AssistantAttachment> sessionAttachments,
        IReadOnlyList<ChatMessage> historyBeforePrompt,
        Func<GoAiAssistantUpdate, Task> update,
        CancellationToken cancellationToken)
    {
        var retryCount = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await CompleteRunAsync(
                    assistant,
                    originalPrompt,
                    trigger,
                    sessionAttachments,
                    historyBeforePrompt,
                    update,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (ShouldRetryCurrentPrompt(
                trigger?.Trigger.Action,
                exception,
                cancellationToken))
            {
                retryCount++;
                var delay = PromptRetryDelay(retryCount);
                RunDiagnostic(
                    logger,
                    assistant.Id.ToString("D"),
                    $"prompt retry {retryCount} scheduled after {exception.GetType().Name}",
                    exception);

                await chats.ResetMessageForRetryAsync(
                    assistant.Id,
                    CancellationToken.None).ConfigureAwait(false);
                assistant = await chats.GetMessageAsync(
                    assistant.Id,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false)
                    ?? assistant with
                    {
                        Content = string.Empty,
                        Status = MessageStatus.Streaming,
                        Error = null,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    };

                await update(new(
                    GoAiAssistantUpdateKind.Delta,
                    assistant)).ConfigureAwait(false);
                await update(new(
                    GoAiAssistantUpdateKind.Status,
                    assistant,
                    Status: "Wird erneut versucht",
                    Detail: $"Derselbe Prompt wird nach einem technischen Abbruch erneut ausgeführt · Versuch {retryCount} in {delay.TotalSeconds:0} Sekunden",
                    Model: settings.Current.SelectedModel)).ConfigureAwait(false);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

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
            var attempt = new GoAiRunRecord(
                Guid.NewGuid(), assistant.SessionId, assistant.Id, action, idempotencyKey, null, 0, "queued",
                null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            localRun = await runs.BeginAttemptAsync(attempt, cancellationToken).ConfigureAwait(false);
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
            var result = await StreamRunWithReconnectAsync(
                localRun,
                assistant,
                update,
                cancellationToken,
                client).ConfigureAwait(false);
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
        var current = localRun;
        var consecutiveReconnectAttempts = 0;
        var lastObservedEventId = current.LastEventId;
        while (true)
        {
            try
            {
                return await StreamRunAsync(
                    current,
                    assistant,
                    update,
                    cancellationToken,
                    suppliedClient).ConfigureAwait(false);
            }
            catch (GoAiStreamDisconnectedException exception) when (!cancellationToken.IsCancellationRequested)
            {
                current = await runs.GetAsync(localRun.Id, CancellationToken.None).ConfigureAwait(false) ?? current;
                consecutiveReconnectAttempts = ReconnectAttemptsAfterProgress(
                    consecutiveReconnectAttempts,
                    lastObservedEventId,
                    current.LastEventId);
                assistant = await chats.GetMessageAsync(
                    current.AssistantMessageId,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false) ?? assistant;
                var attemptNumber = (long)consecutiveReconnectAttempts + 1;
                var delay = StreamReconnectDelay(consecutiveReconnectAttempts);
                await update(new(
                    GoAiAssistantUpdateKind.Status,
                    assistant,
                    Status: "Verbindung wird wiederhergestellt",
                    Detail: $"SSE ab Ereignis {current.LastEventId} · Versuch {attemptNumber}"))
                    .ConfigureAwait(false);
                RunDiagnostic(logger, current.ServerRunId ?? current.Id.ToString("D"), "stream reconnect", exception);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                lastObservedEventId = current.LastEventId;
                consecutiveReconnectAttempts = Math.Min(consecutiveReconnectAttempts + 1, 1_000_000);
            }
        }
    }

    internal static int ReconnectAttemptsAfterProgress(
        int consecutiveReconnectAttempts,
        long previousEventId,
        long currentEventId) =>
        currentEventId > previousEventId ? 0 : consecutiveReconnectAttempts;

    internal static bool ShouldRetryCurrentPrompt(
        PromptTriggerAction? action,
        Exception exception,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested || exception is OperationCanceledException)
        {
            return false;
        }

        return exception switch
        {
            GoAiRunTerminalException terminal => terminal.Retryable,
            GoAiStreamDisconnectedException => true,
            TimeoutException => true,
            HttpRequestException http => http.StatusCode is null
                || http.StatusCode == System.Net.HttpStatusCode.RequestTimeout
                || http.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                || (int)http.StatusCode >= 500,
            _ => false,
        };
    }

    internal static TimeSpan PromptRetryDelay(int retryCount) =>
        TimeSpan.FromSeconds(Math.Min(30, Math.Max(2, retryCount * 2)));

    internal static TimeSpan StreamReconnectDelay(int consecutiveReconnectAttempts) =>
        TimeSpan.FromSeconds(Math.Min(30, 0.5 * Math.Pow(2, Math.Min(6, Math.Max(0, consecutiveReconnectAttempts)))));

    internal static bool IsRetryableServerErrorCode(string? errorCode) => errorCode is
        "document.context_preparation_failed"
        or "session.context_preparation_failed"
        or "general.context_budget"
        or "provider.generation_terminated"
        or "provider.http_failed"
        or "run.timeout"
        or "run.gateway_stopped";

    internal static string? FormatModelTokenProgress(
        ModelGenerationEvent progress,
        ref int activeRunTokens,
        ref bool hasRunStarted)
    {
        if (string.Equals(progress.State, "generationStarted", StringComparison.Ordinal))
        {
            activeRunTokens = 0;
            hasRunStarted = true;
        }
        else if (string.Equals(progress.State, "tokenProgress", StringComparison.Ordinal)
                 && (progress.CurrentTokens is not null
                     || progress.ProcessedPromptTokens is not null
                     || progress.GeneratedTokens is not null))
        {
            hasRunStarted = true;
            var currentTokens = progress.CurrentTokens
                ?? (progress.ProcessedPromptTokens is { } processedPromptTokens
                    ? Math.Max(0, processedPromptTokens) + Math.Max(0, progress.GeneratedTokens ?? 0)
                    : progress.GeneratedTokens!.Value);
            activeRunTokens = Math.Max(activeRunTokens, Math.Max(0, currentTokens));
        }
        else if (!hasRunStarted)
        {
            return null;
        }

        return $"{activeRunTokens:N0} Token";
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
        var collectedArtifacts = (await artifacts.ListForMessageAsync(
            assistant.Id,
            cancellationToken).ConfigureAwait(false)).ToList();
        var activeModelRunTokens = 0;
        var hasModelRunStarted = false;

        async Task<ChatMessage> CompleteAsync(
            string runId,
            long eventId,
            string? selectedModel,
            string? serverSessionTitle)
        {
            model = selectedModel ?? model;
            if (string.IsNullOrWhiteSpace(content))
            {
                content = collectedArtifacts.Count > 0
                    ? "Der Auftrag wurde abgeschlossen. Das Ergebnis ist unten lokal gespeichert."
                    : "Der GO-AI-Auftrag wurde abgeschlossen.";
            }

            var parsed = GeneralAgentResponseParser.Parse(content, serverSessionTitle ?? string.Empty);
            content = RemoveDocumentEvidenceFooter(parsed.Message);
            await chats.UpdateMessageAsync(
                assistant.Id,
                content,
                MessageStatus.Completed,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            await chats.SetMessageContextSummaryAsync(
                assistant.Id,
                parsed.ContextSummary,
                CancellationToken.None).ConfigureAwait(false);

            var requestedTitle = GeneralAgentResponseParser.NormalizeTitle(serverSessionTitle)
                ?? parsed.SessionTitle;
            if (!string.IsNullOrWhiteSpace(requestedTitle))
            {
                await chats.RenameSessionAsync(
                    assistant.SessionId,
                    requestedTitle,
                    CancellationToken.None).ConfigureAwait(false);
            }

            await runs.UpdateAsync(
                localRun.Id,
                runId,
                eventId,
                "completed",
                model,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            var final = await chats.GetMessageAsync(
                assistant.Id,
                cancellationToken: CancellationToken.None).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Die AI-Nachricht des abgeschlossenen Laufs fehlt.");
            var session = await chats.GetSessionAsync(
                assistant.SessionId,
                CancellationToken.None).ConfigureAwait(false);
            await recentActivity.RecordAsync(
                $"AI-Sitzung „{session?.Title ?? "Neue Sitzung"}“ bearbeitet",
                CancellationToken.None).ConfigureAwait(false);
            await update(new(
                GoAiAssistantUpdateKind.Completed,
                final,
                collectedArtifacts.ToArray(),
                session,
                "Fertig",
                Model: model)).ConfigureAwait(false);
            return final;
        }

        var acknowledgedEventId = localRun.LastEventId;
        try
        {
            var pendingSubmissions = await toolExecutions
                .ListPendingSubmissionsAsync(localRun.Id, localRun.ServerRunId, cancellationToken)
                .ConfigureAwait(false);
            foreach (var pending in pendingSubmissions)
            {
                var pendingResult = JsonSerializer.Deserialize<ClientToolResult>(pending.ResultJson!, JsonOptions)
                    ?? throw new InvalidDataException("Ein gespeichertes Client-Toolergebnis ist ungültig.");
                await client.SubmitClientToolResultAsync(
                    pending.ServerRunId,
                    pendingResult,
                    cancellationToken).ConfigureAwait(false);
                await toolExecutions.MarkSubmittedAsync(
                    pending.ProposalId,
                    CancellationToken.None).ConfigureAwait(false);
                acknowledgedEventId = Math.Max(acknowledgedEventId, pending.EventId);
                localRun = localRun with
                {
                    LastEventId = acknowledgedEventId,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                await runs.UpdateAsync(
                    localRun.Id,
                    localRun.ServerRunId,
                    acknowledgedEventId,
                    "running",
                    model,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }

            await foreach (var item in client.StreamRunEventsAsync(
                localRun.ServerRunId!,
                acknowledgedEventId,
                cancellationToken).ConfigureAwait(false))
            {
                switch (item.Type)
                {
                    case RunEventTypes.QueueChanged:
                        var queue = item.Data.Deserialize<QueueChangedEvent>(JsonOptions);
                        await update(new(
                            GoAiAssistantUpdateKind.Status,
                            assistant,
                            Status: "In Warteschlange",
                            Detail: queue is null ? null : $"Position {queue.Position}"))
                            .ConfigureAwait(false);
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
                            Detail: loading?.State == "loaded" ? null : "Ausgewähltes Modell wird geladen.",
                            Model: model,
                            ContextLimit: loading?.EffectiveContextLength)).ConfigureAwait(false);
                        break;
                    case RunEventTypes.ModelGeneration:
                        var generation = item.Data.Deserialize<ModelGenerationEvent>(JsonOptions);
                        if (generation is not null)
                        {
                            await update(new(
                                GoAiAssistantUpdateKind.Status,
                                assistant,
                                Status: "Denkt nach",
                                Detail: FormatModelTokenProgress(
                                    generation,
                                    ref activeModelRunTokens,
                                    ref hasModelRunStarted),
                                Model: model)).ConfigureAwait(false);
                        }
                        break;
                    case RunEventTypes.ContextChanged:
                        var context = item.Data.Deserialize<ContextChangedEvent>(JsonOptions);
                        if (context is not null)
                        {
                            var contextDetail = string.IsNullOrWhiteSpace(context.Detail)
                                ? context.DocumentPages > 0
                                    ? $"{context.DocumentPages:N0} Dokumentseiten im Kontext"
                                    : "Sitzungskontext ist bereit."
                                : context.Detail.Trim();
                            await update(new(
                                GoAiAssistantUpdateKind.Status,
                                assistant,
                                Status: context.WasCompacted ? "Kontext verdichtet" : "Kontext bereit",
                                Detail: contextDetail,
                                Model: model,
                                ContextUsed: context.EstimatedInputTokens,
                                ContextLimit: context.ContextLimit,
                                LoadedFiles: context.LoadedFiles,
                                ContextWasCompacted: context.WasCompacted)).ConfigureAwait(false);
                        }
                        break;
                    case RunEventTypes.ServerToolStarted:
                        await update(new(
                            GoAiAssistantUpdateKind.Status,
                            assistant,
                            Status: "Serverwerkzeug",
                            Detail: StringProperty(item.Data, "target")
                                ?? StringProperty(item.Data, "tool"),
                            Model: model)).ConfigureAwait(false);
                        break;
                    case RunEventTypes.ServerToolCompleted:
                        var extracted = ExtractToolResultText(item.Data);
                        if (!string.IsNullOrWhiteSpace(extracted))
                        {
                            content = AppendContent(content, extracted);
                            await chats.UpdateMessageAsync(
                                assistant.Id,
                                content,
                                MessageStatus.Streaming,
                                cancellationToken: cancellationToken).ConfigureAwait(false);
                            assistant = assistant with
                            {
                                Content = content,
                                Status = MessageStatus.Streaming,
                                UpdatedAt = DateTimeOffset.UtcNow,
                            };
                            await update(new(GoAiAssistantUpdateKind.Delta, assistant)).ConfigureAwait(false);
                        }
                        break;
                    case RunEventTypes.TextDelta:
                        content += item.Data.Deserialize<TextDeltaEvent>(JsonOptions)?.Delta ?? string.Empty;
                        await chats.UpdateMessageAsync(
                            assistant.Id,
                            content,
                            MessageStatus.Streaming,
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                        assistant = assistant with
                        {
                            Content = content,
                            Status = MessageStatus.Streaming,
                            UpdatedAt = DateTimeOffset.UtcNow,
                        };
                        await update(new(GoAiAssistantUpdateKind.Delta, assistant)).ConfigureAwait(false);
                        break;
                    case RunEventTypes.ClientToolProposed:
                        var proposal = item.Data.Deserialize<ToolProposal>(JsonOptions)
                            ?? throw new InvalidDataException(
                                "Der Server hat einen ungültigen Client-Toolvorschlag gesendet.");
                        if (!string.Equals(proposal.RunId, item.RunId, StringComparison.Ordinal))
                        {
                            throw new InvalidDataException(
                                "Der Client-Toolvorschlag gehört nicht zum aktiven Serverlauf.");
                        }
                        await update(new(
                            GoAiAssistantUpdateKind.Status,
                            assistant,
                            Status: "Lokale Aktion",
                            Detail: proposal.Summary,
                            Model: model)).ConfigureAwait(false);
                        var result = await ExecuteClientToolOnceAsync(
                            localRun,
                            item,
                            proposal,
                            cancellationToken).ConfigureAwait(false);
                        if (proposal.Name == ClientToolNames.DocumentCreate
                            && string.Equals(result.Status, "completed", StringComparison.OrdinalIgnoreCase))
                        {
                            collectedArtifacts.Clear();
                            collectedArtifacts.AddRange(await artifacts.ListForMessageAsync(
                                assistant.Id,
                                cancellationToken).ConfigureAwait(false));
                            await update(new(
                                GoAiAssistantUpdateKind.ArtifactsChanged,
                                assistant,
                                collectedArtifacts.ToArray())).ConfigureAwait(false);
                        }
                        await client.SubmitClientToolResultAsync(
                            item.RunId,
                            result,
                            cancellationToken).ConfigureAwait(false);
                        await toolExecutions.MarkSubmittedAsync(
                            proposal.ProposalId,
                            CancellationToken.None).ConfigureAwait(false);
                        break;
                    case RunEventTypes.RunWaitingForClient:
                        await update(new(
                            GoAiAssistantUpdateKind.Status,
                            assistant,
                            Status: "Lokale Aktion wird erwartet",
                            Model: model)).ConfigureAwait(false);
                        break;
                    case RunEventTypes.ArtifactCreated:
                        var descriptor = item.Data.Deserialize<ArtifactDescriptor>(JsonOptions)
                            ?? throw new InvalidDataException("Der Server hat ein ungültiges Artefakt beschrieben.");
                        var imported = await DownloadArtifactAsync(
                            client,
                            assistant.Id,
                            descriptor,
                            model ?? "GO AI Server",
                            cancellationToken).ConfigureAwait(false);
                        if (collectedArtifacts.All(value => value.Id != imported.Id))
                        {
                            collectedArtifacts.Add(imported);
                        }
                        await update(new(
                            GoAiAssistantUpdateKind.ArtifactsChanged,
                            assistant,
                            collectedArtifacts.ToArray(),
                            Status: "Artefakt gespeichert",
                            Detail: imported.FileName)).ConfigureAwait(false);
                        break;
                    case RunEventTypes.RunCompleted:
                        var completed = item.Data.Deserialize<RunCompletedEvent>(JsonOptions);
                        return await CompleteAsync(
                            item.RunId,
                            item.Id,
                            completed?.ModelId,
                            completed?.SessionTitle).ConfigureAwait(false);
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
                        throw new GoAiRunTerminalException(
                            failed?.ErrorCode ?? "server.run_failed",
                            failed?.Message ?? "Der Serverlauf ist fehlgeschlagen.",
                            failed?.Retryable ?? false);
                    case RunEventTypes.RunCancelled:
                        await runs.UpdateAsync(
                            localRun.Id,
                            item.RunId,
                            item.Id,
                            "cancelled",
                            model,
                            cancellationToken: CancellationToken.None).ConfigureAwait(false);
                        throw new OperationCanceledException(cancellationToken);
                }

                acknowledgedEventId = item.Id;
                localRun = localRun with
                {
                    LastEventId = acknowledgedEventId,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                await runs.UpdateAsync(
                    localRun.Id,
                    item.RunId,
                    acknowledgedEventId,
                    item.Type == RunEventTypes.RunWaitingForClient ? "waitingForClient" : "running",
                    model,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }

            var snapshot = await client.GetRunAsync(
                localRun.ServerRunId!,
                cancellationToken).ConfigureAwait(false);
            if (snapshot.State == RunState.Completed)
            {
                return await CompleteAsync(
                    snapshot.RunId,
                    snapshot.LastEventId,
                    snapshot.SelectedModel,
                    snapshot.SessionTitle).ConfigureAwait(false);
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
                var interrupted = snapshot.State == RunState.Interrupted;
                throw new GoAiRunTerminalException(
                    snapshot.ErrorCode ?? (interrupted ? "run.gateway_stopped" : "server.run_failed"),
                    interrupted
                        ? "Der Serverlauf wurde durch einen Serverneustart unterbrochen."
                        : "Der Serverlauf ist fehlgeschlagen.",
                    interrupted || IsRetryableServerErrorCode(snapshot.ErrorCode));
            }
            if (snapshot.State == RunState.Cancelled)
            {
                await runs.UpdateAsync(
                    localRun.Id,
                    snapshot.RunId,
                    snapshot.LastEventId,
                    "cancelled",
                    snapshot.SelectedModel,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
                throw new OperationCanceledException(cancellationToken);
            }
            throw new IOException(
                "Der SSE-Stream wurde beendet, bevor der Serverlauf einen Endzustand erreicht hat.");
        }
        catch (GoAiRunTerminalException)
        {
            throw;
        }
        catch (GoAiStreamDisconnectedException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            await runs.UpdateAsync(
                localRun.Id,
                localRun.ServerRunId,
                localRun.LastEventId,
                "running",
                model,
                "client.stream_detached",
                CancellationToken.None).ConfigureAwait(false);
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
            var result = await toolBroker.ExecuteAsync(
                proposal,
                localRun.SessionId,
                localRun.AssistantMessageId,
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

    internal static string DisplaySpeechProvider(string? provider)
    {
        return "Supertonic F5 Ultra";
    }

    private static string CombineSpeechDetail(string? sourceDetail, string detail) =>
        string.IsNullOrWhiteSpace(sourceDetail) ? detail : $"{sourceDetail} · {detail}";

    private static string? VisibleSpeechSourceDetail(string? sourceDetail) =>
        string.IsNullOrWhiteSpace(sourceDetail)
        || sourceDetail.Contains("AI-Nachricht", StringComparison.OrdinalIgnoreCase)
            ? null
            : sourceDetail;




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
        var message = "Die Live-Untertitel für das Windows-Systemaudio wurden gestartet. Sicher erkanntes Deutsch bleibt unverändert; alle anderen Sprachen werden über das aktuell ausgewählte General-AI-Modell ins Deutsche übersetzt. Verschiedene Stimmen werden als Dialog gegliedert. Die Untertitel laufen parallel zum allgemeinen Chat und können in der Untertitelanzeige beendet werden.";
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
        var final = await chats.GetMessageAsync(
            assistant.Id,
            cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die AI-Nachricht des abgeschlossenen Laufs fehlt.");
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
        _ = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die AI-Sitzung wurde nicht gefunden.");
        var action = trigger?.Trigger.Action;
        var audiobook = action == PromptTriggerAction.Audiobook;
        var contextProfile = audiobook
            ? SessionContextProfile.Audiobook
            : SessionContextProfile.General;
        var selectedModel = settings.Current.SelectedModel?.Trim();
        if (string.IsNullOrWhiteSpace(selectedModel))
        {
            throw new InvalidOperationException("In den Einstellungen ist kein General-AI-Modell ausgewählt.");
        }

        var minimumHistoryReserveTokens = CalculateDocumentHistoryReserveTokens(
            historyBeforePrompt,
            contextProfile);
        var documentContext = await documentContexts.PrepareAsync(
            client,
            sessionId,
            assistant.Id,
            originalPrompt,
            selectedModel,
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

        var sessionContext = await sessionContexts.PrepareAsync(
            client,
            sessionId,
            historyBeforePrompt,
            originalPrompt,
            selectedModel,
            contextProfile,
            knownContextLength: documentContext?.ContextLength,
            knownHistoryBudgetCharacters: documentContext?.HistoryBudgetCharacters,
            async progress => await update(new(
                GoAiAssistantUpdateKind.Status,
                assistant,
                Status: progress.Status,
                Detail: progress.Detail,
                Model: selectedModel,
                ContextLimit: documentContext?.ContextLength,
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
            hasDocumentContext: documentContext is not null,
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
        messages.Add(new RunMessage("user", latestParts));

        if (action == PromptTriggerAction.BricsCad && !toolBroker.IsBricsCadAvailable)
        {
            throw new InvalidOperationException(
                "Das GO-BricsCAD-Plugin ist nicht verbunden. Öffne BricsCAD und stelle die GO-Bridge-Verbindung her.");
        }
        var capabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "documentIo",
        };
        if (action == PromptTriggerAction.BricsCad && toolBroker.IsBricsCadAvailable)
        {
            capabilities.Add("bricscad");
        }
        if (documentContext?.Descriptor.DocumentCount > 0)
        {
            capabilities.Add("documents");
        }

        var mode = action is PromptTriggerAction.Translation
            or PromptTriggerAction.BricsCad
            or PromptTriggerAction.WebSearch
            or PromptTriggerAction.YouTubeSearch
            or PromptTriggerAction.Audiobook
                ? RunMode.General
                : RunMode.Auto;
        return new RunRequest(
            GoAiProtocol.Version,
            mode,
            messages,
            uploaded.Select(item => item.Upload.UploadId).ToArray(),
            ClientCapabilities: capabilities
                .OrderBy(static capability => capability, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Limits: new RunLimits(
                MaximumOutputTokens: 8_192,
                MaximumContextTokens: Math.Clamp(sessionContext.ContextLength, 1_024, 262_144),
                TimeoutSeconds: 3_600),
            SessionId: sessionId.ToString("D"),
            AllowedServerTools: GetAllowedServerTools(action, originalPrompt),
            PreferredGeneralModelId: selectedModel,
            DocumentContext: documentContext?.Descriptor,
            SessionContext: sessionContext.Descriptor,
            ConversationProfile: audiobook ? ConversationProfile.Audiobook : ConversationProfile.General,
            ReasoningEffort: null);
    }

    internal static string? ResolvePreferredModel(AppSettings current) =>
        current.SelectedModel?.Trim();

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

    internal static IReadOnlyList<string> GetAllowedServerTools(
        PromptTriggerAction? action,
        string? prompt = null) => action switch
    {
        PromptTriggerAction.WebSearch => ["web.search", "web.fetch"],
        PromptTriggerAction.YouTubeSearch => ["youtube.search", "web.fetch"],
        PromptTriggerAction.Audiobook => [],
        _ => ["math.evaluate", "context.embed", "context.retrieve"],
    };

    internal static bool ContainsWebResearchDirective(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return false;
        }

        return Regex.IsMatch(
            prompt,
            @"(?<![\p{L}\p{N}])(?:websuche|web-search|websearch|suche\s+im\s+web|recherchiere\s+(?:im\s+web|im\s+internet|online)|internetrecherche)(?![\p{L}\p{N}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    internal static string BuildWebResearchPrompt(string prompt)
    {
        var task = prompt.Trim();
        return "[GO_WEB_RESEARCH_REQUEST]\n"
            + "GO bereitet die SearXNG-Recherche vor der Antwort in isolierten SDK-Schritten auf. Nutze das danach "
            + "bereitgestellte Evidenzdossier, nenne die verwendeten Seiten mit Titel und URL und erfinde keine "
            + "nicht abgerufenen Inhalte.\n\nRechercheauftrag:\n"
            + task;
    }

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
                BuildWebResearchPrompt(
                    RequireRemaining(trigger, "Gib nach der Triggerphrase einen Such- und Antwortauftrag an.")),
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

    private static string VisibleFailure(Exception exception) => exception switch
    {
        GoAiRunTerminalException => exception.Message,
        DirectoryNotFoundException => exception.Message,
        FileNotFoundException => exception.Message,
        InvalidDataException => exception.Message,
        InvalidOperationException => exception.Message,
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
        _activeSpeechCancellation?.Cancel();
        _activeSpeechCancellation?.Dispose();
        _gate.Dispose();
        _speechGate.Dispose();
    }


    private sealed record SpeechSource(
        string Text,
        string Kind,
        string? Detail,
        Guid? MessageId,
        MessageContentProfile ContentProfile);

    private sealed record UploadedAttachment(AssistantAttachment Attachment, UploadCompleted Upload);
}
