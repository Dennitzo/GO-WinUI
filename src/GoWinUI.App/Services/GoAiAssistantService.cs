using GoAi.Client;
using GoAi.Contracts;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Chat;
using GoWinUI.Core.Models;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GoWinUI.App.Services;

public enum GoAiAssistantUpdateKind
{
    Started,
    Delta,
    Status,
    ArtifactsChanged,
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
    LocalToolBroker toolBroker,
    WorkspaceRepositoryIndex repositoryIndex,
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
            var sessionDocuments = await documents.ListAsync(sessionId, _activeCancellation.Token).ConfigureAwait(false);
            var user = await chats.AddMessageAsync(
                sessionId, ChatRole.User, prompt.Trim(), MessageStatus.Completed, _activeCancellation.Token).ConfigureAwait(false);
            sessionAttachments = await BindCapturedMediaToMessageAsync(
                user,
                sessionAttachments,
                _activeCancellation.Token).ConfigureAwait(false);
            var assistant = await chats.AddMessageAsync(
                sessionId, ChatRole.Assistant, string.Empty, MessageStatus.Streaming, _activeCancellation.Token).ConfigureAwait(false);
            var contextLimit = trigger?.Trigger.Action == PromptTriggerAction.Code ? 262_144 : 131_072;
            await update(new(
                GoAiAssistantUpdateKind.Started,
                assistant,
                Status: "Denkt nach",
                Detail: trigger?.Trigger.Action == PromptTriggerAction.Code
                    ? "Laguna bereitet den Workspace vor."
                    : "GO AI Server verarbeitet die Anfrage.",
                ContextLimit: contextLimit)).ConfigureAwait(false);

            var action = trigger?.Trigger.Action;
            try
            {
                return action switch
                {
                    PromptTriggerAction.TextToSpeech => await CompleteSpeechAsync(assistant, trigger!, historyBeforePrompt, sessionDocuments, update, _activeCancellation.Token).ConfigureAwait(false),
                    PromptTriggerAction.Transcription => await CompleteTranscriptionAsync(assistant, trigger!, update, _activeCancellation.Token).ConfigureAwait(false),
                    PromptTriggerAction.VoiceInput => await CompleteVoiceInputAsync(assistant, update, _activeCancellation.Token).ConfigureAwait(false),
                    PromptTriggerAction.LiveCaptions or PromptTriggerAction.LiveTranslation =>
                        await CompleteLiveCaptionsAsync(assistant, trigger!, update, _activeCancellation.Token).ConfigureAwait(false),
                    _ => await CompleteRunAsync(
                        assistant,
                        prompt,
                        trigger,
                        sessionAttachments,
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
                if (trigger?.Trigger.Action == PromptTriggerAction.TextToSpeech)
                {
                    await chats.SetToolExecutionAsync(assistant.Id, new ToolExecutionInfo("Vorlesen", "Dokument aus Anhang", "Abgebrochen"), CancellationToken.None).ConfigureAwait(false);
                }
                var current = (await chats.ListMessagesAsync(sessionId, CancellationToken.None).ConfigureAwait(false))
                    .Single(item => item.Id == assistant.Id);
                await chats.UpdateMessageAsync(current.Id, current.Content, MessageStatus.Cancelled, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                var cancelled = current with { Status = MessageStatus.Cancelled, UpdatedAt = DateTimeOffset.UtcNow };
                await update(new(GoAiAssistantUpdateKind.Cancelled, cancelled, Status: "Abgebrochen")).ConfigureAwait(false);
                return cancelled;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                if (trigger?.Trigger.Action == PromptTriggerAction.TextToSpeech)
                {
                    await chats.SetToolExecutionAsync(assistant.Id, new ToolExecutionInfo("Vorlesen", "Dokument aus Anhang", "Fehlgeschlagen", exception.Message), CancellationToken.None).ConfigureAwait(false);
                }
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
                    assistant.SessionId,
                    originalPrompt,
                    trigger,
                    sessionAttachments,
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
                            Detail: selected?.ModelId,
                            Model: model)).ConfigureAwait(false);
                        break;
                    case RunEventTypes.ModelLoading:
                        var loading = item.Data.Deserialize<ModelLoadingEvent>(JsonOptions);
                        model = loading?.ModelId ?? model;
                        await update(new(
                            GoAiAssistantUpdateKind.Status,
                            assistant,
                            Status: loading?.State == "loaded" ? "Denkt nach" : "Modell wird geladen",
                            Detail: loading is null
                                ? model
                                : $"{loading.ModelId} · {loading.EffectiveContextLength:N0} Kontexttoken",
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
                        content = parsedResponse.Message;
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
                content = snapshotResponse.Message;
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

    private async Task<ChatMessage> CompleteSpeechAsync(
        ChatMessage assistant,
        PromptTriggerMatch trigger,
        IReadOnlyList<ChatMessage> historyBeforePrompt,
        IReadOnlyList<StoredDocument> sessionDocuments,
        Func<GoAiAssistantUpdate, Task> update,
        CancellationToken cancellationToken)
    {
        var selectedMessageSpeech = trigger.OriginalPrompt.Equals("Lies die ausgewählte Nachricht vor", StringComparison.OrdinalIgnoreCase);
        var documentSpeech = selectedMessageSpeech
            ? null
            : await ResolveDocumentSpeechTextAsync(trigger.RemainingPrompt, sessionDocuments, cancellationToken).ConfigureAwait(false);
        var text = documentSpeech?.Text ?? ResolveSpeechText(trigger.RemainingPrompt, historyBeforePrompt);
        if (documentSpeech is not null)
        {
            text = MicrophoneTranscriptionService.PrepareSpeechText(text!);
        }
        var execution = new ToolExecutionInfo(
            "Vorlesen",
            documentSpeech is not null
                ? "Dokument aus Anhang"
                : selectedMessageSpeech
                    ? "AI-Nachricht"
                    : (!string.IsNullOrWhiteSpace(trigger.RemainingPrompt) ? "Vorgegebener Text" : "Letzte AI-Nachricht"),
            "Läuft",
            documentSpeech?.Detail ?? "Audio wird erzeugt.",
            "Piper Kerstin");
        await chats.SetToolExecutionAsync(assistant.Id, execution, cancellationToken).ConfigureAwait(false);
        assistant = assistant with { ToolExecution = execution };
        if (string.IsNullOrWhiteSpace(text))
        {
            var failedExecution = execution with { Status = "Fehlgeschlagen", Detail = documentSpeech?.Error ?? "Kein vorlesbarer Text gefunden." };
            await chats.SetToolExecutionAsync(assistant.Id, failedExecution, CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException(documentSpeech?.Error
                ?? "Es ist keine geeignete abgeschlossene AI-Antwort zum Vorlesen vorhanden.");
        }
        var preparation = await PrepareSpeechWithGeneralAiAsync(text!, update, assistant, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(preparation))
        {
            text = preparation;
        }
        execution = execution with
        {
            Detail = string.IsNullOrWhiteSpace(preparation)
                ? "Lokale Textbereinigung · Audio wird erzeugt."
                : "General AI aufbereitet · Audio wird erzeugt."
        };
        await chats.SetToolExecutionAsync(assistant.Id, execution, cancellationToken).ConfigureAwait(false);
        assistant = assistant with { ToolExecution = execution };
        await update(new(
            GoAiAssistantUpdateKind.Status,
            assistant,
            Status: "Sprachausgabe",
            Detail: documentSpeech?.Detail ?? "Audio wird erzeugt.")).ConfigureAwait(false);
        await microphone.PlayTextAsync(text, cancellationToken: cancellationToken).ConfigureAwait(false);
        var completedExecution = execution with { Status = "Abgeschlossen" };
        await chats.SetToolExecutionAsync(assistant.Id, completedExecution, CancellationToken.None).ConfigureAwait(false);
        assistant = assistant with { ToolExecution = completedExecution };
        return await CompleteImmediateAsync(assistant, string.Empty, update, "Piper Kerstin", cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> PrepareSpeechWithGeneralAiAsync(
        string source,
        Func<GoAiAssistantUpdate, Task> update,
        ChatMessage assistant,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = await connection.CreateClientAsync(cancellationToken).ConfigureAwait(false);
            var chunks = SplitSpeechPreparationChunks(source, 12_000);
            var output = new StringBuilder();
            foreach (var chunk in chunks)
            {
                var prompt = "Schreibe den folgenden Text stark in flüssige, natürliche deutsche Vorlesesprache um, als hätte ein Buchautor ihn für eine professionelle Lesung verfasst. Schaffe angenehme Übergänge und vollständige, gut sprechbare Sätze. Entferne Layoutreste, Tabellenmarker, Kopf- und Fußzeilen, technische Artefakte sowie unnötige Meta- und Bedienhinweise. Bewahre die Bedeutung, alle fachlich relevanten Aussagen, Zahlen, Einheiten und die logische Reihenfolge. Erfinde keine neuen Fakten. Gib ausschließlich den fertigen Vorlesetext ohne Überschrift, Markdown, Quellenhinweise oder Erklärung deiner Bearbeitung aus.\n\n" + chunk;
                var accepted = await client.CreateRunAsync(new RunRequest(
                    GoAiProtocol.Version,
                    RunMode.General,
                    [new RunMessage("user", [new ContentPart("text", prompt)])],
                    Limits: new RunLimits(MaximumOutputTokens: 4_096, MaximumContextTokens: 131_072, TimeoutSeconds: 600),
                    AllowedServerTools: []),
                    $"go-speech-prep-{Guid.NewGuid():N}", cancellationToken).ConfigureAwait(false);
                string? activeModel = null;
                await foreach (var item in client.StreamRunEventsAsync(accepted.RunId, cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    switch (item.Type)
                    {
                        case RunEventTypes.ModelSelected:
                        case RunEventTypes.ModelFallback:
                            activeModel = item.Data.Deserialize<ModelSelectedEvent>(JsonOptions)?.ModelId ?? activeModel;
                            await update(new(
                                GoAiAssistantUpdateKind.Status,
                                assistant,
                                Status: "Text wird für Sprachausgabe aufbereitet",
                                Detail: "Das Sprachmodell formuliert den Text für natürliches Vorlesen um.",
                                Model: activeModel)).ConfigureAwait(false);
                            break;
                        case RunEventTypes.ModelLoading:
                            var loading = item.Data.Deserialize<ModelLoadingEvent>(JsonOptions);
                            activeModel = loading?.ModelId ?? activeModel;
                            await update(new(
                                GoAiAssistantUpdateKind.Status,
                                assistant,
                                Status: loading?.State == "loaded"
                                    ? "Text wird für Sprachausgabe aufbereitet"
                                    : "Modell wird geladen",
                                Detail: loading is null
                                    ? "Sprachausgabe wird vorbereitet."
                                    : $"{loading.ModelId} · {loading.EffectiveContextLength:N0} Kontexttoken",
                                Model: activeModel,
                                ContextLimit: loading?.EffectiveContextLength)).ConfigureAwait(false);
                            break;
                        case RunEventTypes.ContextChanged:
                            var context = item.Data.Deserialize<ContextChangedEvent>(JsonOptions);
                            if (context is not null)
                            {
                                await update(new(
                                    GoAiAssistantUpdateKind.Status,
                                    assistant,
                                    Status: "Text wird für Sprachausgabe aufbereitet",
                                    Detail: "Das Sprachmodell formuliert den Text für natürliches Vorlesen um.",
                                    Model: activeModel,
                                    ContextUsed: context.EstimatedInputTokens,
                                    ContextLimit: context.ContextLimit,
                                    ContextWasCompacted: context.WasCompacted)).ConfigureAwait(false);
                            }
                            break;
                        case RunEventTypes.TextDelta:
                            var delta = item.Data.Deserialize<TextDeltaEvent>(JsonOptions);
                            if (!string.IsNullOrEmpty(delta?.Delta)) output.Append(delta.Delta);
                            break;
                    }
                }
                if (output.Length > 0 && !char.IsWhiteSpace(output[^1])) output.AppendLine();
            }
            return MicrophoneTranscriptionService.PrepareSpeechText(output.ToString());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RunDiagnostic(logger, assistant.Id.ToString("D"), "speech preparation fallback", exception);
            await update(new(GoAiAssistantUpdateKind.Status, assistant, Status: "Sprachausgabe", Detail: "Lokale Textbereinigung verwendet.")).ConfigureAwait(false);
            return null;
        }
    }

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
        Guid sessionId,
        string originalPrompt,
        PromptTriggerMatch? trigger,
        IReadOnlyList<AssistantAttachment> sessionAttachments,
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
        var history = await chats.ListMessagesAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var historyBudget = coding ? 120_000 : 400_000;
        var messages = BuildHistoryMessages(history, historyBudget).ToList();
        if (messages.Count == 0 || messages[^1].Role != "user")
        {
            messages.Add(new RunMessage("user", [new ContentPart("text", Text: originalPrompt)]));
        }

        var documentText = await BuildDocumentContextAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var transformed = TransformPrompt(originalPrompt, trigger, !string.IsNullOrWhiteSpace(documentText));
        var latestParts = new List<ContentPart> { new("text", Text: transformed) };
        foreach (var item in uploaded)
        {
            latestParts.Add(new ContentPart(
                "upload",
                UploadId: item.Upload.UploadId,
                MediaType: item.Attachment.ContentType,
                FileName: item.Attachment.FileName));
        }
        if (!string.IsNullOrWhiteSpace(documentText))
        {
            latestParts.Add(new ContentPart("text", Text: documentText));
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
        messages[^1] = new RunMessage("user", latestParts);

        var mode = coding ? RunMode.Code
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
        IReadOnlyList<string> clientCapabilities = coding
            ? toolBroker.GetAvailableCapabilities(session.WorkspacePath)
            : action == PromptTriggerAction.BricsCad && toolBroker.IsBricsCadAvailable
                ? ["bricscad"]
                : [];
        return new RunRequest(
            GoAiProtocol.Version,
            mode,
            messages,
            uploaded.Select(item => item.Upload.UploadId).ToArray(),
            ClientCapabilities: clientCapabilities,
            Limits: new RunLimits(
                MaximumOutputTokens: 8_192,
                MaximumContextTokens: coding ? 262_144 : null,
                TimeoutSeconds: coding ? 14_400 : 3_600),
            SessionId: sessionId.ToString("D"),
            AllowedServerTools: GetAllowedServerTools(action),
            Workspace: workspaceDescriptor);
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
            if (remaining <= 0)
            {
                break;
            }
            var candidate = eligibleHistory[index];
            var bounded = candidate with { Content = BoundText(candidate.Content, remaining) };
            selectedHistory.Push(bounded);
            selectedCharacters += bounded.Content.Length;
        }
        foreach (var message in selectedHistory)
        {
            var text = BoundText(message.Content, 240_000);
            if (!string.IsNullOrWhiteSpace(text))
            {
                messages.Add(new RunMessage(
                    message.Role == ChatRole.Assistant ? "assistant" : "user",
                    [new ContentPart("text", Text: text)]));
            }
        }
        return messages;
    }

    internal static IReadOnlyList<string> GetAllowedServerTools(PromptTriggerAction? action) => action switch
    {
        PromptTriggerAction.WebSearch => ["web.search", "web.fetch"],
        PromptTriggerAction.YouTubeSearch => ["youtube.search", "web.fetch"],
        PromptTriggerAction.Code => ["math.evaluate"],
        _ => ["math.evaluate", "context.embed", "context.retrieve"],
    };

    private async Task<string> BuildDocumentContextAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        foreach (var document in await documents.ListAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            foreach (var page in await documents.ReadPagesAsync(document.Id, cancellationToken).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(page.Text))
                {
                    continue;
                }
                builder.AppendLine(CultureInfo.InvariantCulture, $"[Lokales Dokument: {document.FileName}, Seite {page.PageNumber}]");
                builder.AppendLine(page.Text);
                if (builder.Length >= 220_000)
                {
                    builder.AppendLine("[Dokumentkontext lokal gekürzt]");
                    return builder.ToString()[..Math.Min(builder.Length, 240_000)];
                }
            }
        }
        return builder.ToString();
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
        bool hasDocumentContext = false)
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
            _ => original,
        };
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

    private sealed record UploadedAttachment(AssistantAttachment Attachment, UploadCompleted Upload);
}
