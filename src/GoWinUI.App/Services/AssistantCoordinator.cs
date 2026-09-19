using GoAi.Contracts;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Chat;
using GoWinUI.Core.Coding;
using GoWinUI.Core.Models;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GoWinUI.App.Services;

public sealed class AssistantCoordinator(
    IChatRepository chats,
    IWorkflowRepository workflows,
    IDocumentIngestor documents,
    IContextAssembler contextAssembler,
    IPromptTriggerRepository promptTriggers,
    IAssistantAttachmentRepository attachments,
    IChatArtifactRepository artifacts,
    IConversationSnapshotRepository conversationSnapshots,
    GoAiAssistantService? goAi,
    SettingsCoordinator settings,
    RecentActivityService recentActivity,
    MicrophoneTranscriptionService? microphone = null)
{
    private const string DefaultSessionTitle = "Neue Sitzung";
    private const string DefaultSystemPrompt = "GO ist ein allgemeiner lokaler AI-Assistent. Unterstütze die konkrete Aufgabe des Nutzers, etwa beim Programmieren, Schreiben, Lernen, Analysieren oder Planen. Passe Sprache, Detailtiefe und Vorgehen an die Frage an. Unterscheide belegte Informationen von Annahmen, benenne relevante Unsicherheiten und erfinde keine Fakten, Quellen oder Ergebnisse.";
    private int _startupRunsHandled;
    private Task? _resumeTask;
    private readonly object _resumeLock = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, AssistantDisplayState> _displayStates = new();

    internal sealed record AssistantDisplayState(Guid MessageId, string? ModelSelection, bool IsCoding,
        bool IsRunning, string? Status = null, string? Detail = null, string? Model = null,
        int? ContextUsed = null, int? ContextLimit = null, int? LoadedFiles = null, bool ContextWasCompacted = false);

    private async Task ObserveDisplayStateAsync(GoAiAssistantUpdate update)
    {
        if (update.Kind is not (GoAiAssistantUpdateKind.Started or GoAiAssistantUpdateKind.Status
            or GoAiAssistantUpdateKind.Completed or GoAiAssistantUpdateKind.Cancelled or GoAiAssistantUpdateKind.Failed)) return;
        var isCoding = _displayStates.TryGetValue(update.Message.SessionId, out var previous)
            && previous.MessageId == update.Message.Id ? previous.IsCoding
                : (await chats.GetSessionAsync(update.Message.SessionId, CancellationToken.None).ConfigureAwait(false))?.PersistentToolAction == PersistentToolAction.Coding;
        var selection = isCoding ? settings.Current.SelectedCodingModel : settings.Current.SelectedModel;
        _displayStates.AddOrUpdate(update.Message.SessionId,
            _ => Merge(new(update.Message.Id, selection, isCoding, true)),
            (_, current) => Merge(current.MessageId == update.Message.Id ? current : new(update.Message.Id, selection, isCoding, true)));

        AssistantDisplayState Merge(AssistantDisplayState current)
        {
            var reasoningOnly = update.ToolStep?.Tool == "assistant.reasoning";
            var resumed = update.Kind == GoAiAssistantUpdateKind.Started && current.Status is not null;
            return current with
            {
                IsRunning = update.Kind is not (GoAiAssistantUpdateKind.Completed or GoAiAssistantUpdateKind.Cancelled or GoAiAssistantUpdateKind.Failed),
                Status = reasoningOnly || resumed ? current.Status : update.Status ?? current.Status,
                Detail = reasoningOnly || resumed ? current.Detail : update.Detail ?? current.Detail,
                Model = reasoningOnly ? current.Model : update.Model ?? current.Model,
                ContextUsed = update.ContextUsed ?? current.ContextUsed,
                ContextLimit = update.ContextLimit ?? current.ContextLimit,
                LoadedFiles = update.LoadedFiles ?? current.LoadedFiles,
                ContextWasCompacted = update.ContextUsed.HasValue ? update.ContextWasCompacted : current.ContextWasCompacted,
            };
        }
    }

    public Task SaveDraftAsync(Guid sessionId, string draft, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(draft.Length, 100_000);
        return chats.SaveDraftAsync(sessionId, draft, cancellationToken);
    }

    public async Task SetCodingWorkspacePathAsync(Guid sessionId, string path, CancellationToken cancellationToken = default)
    {
        EnsureContextCanChange();
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException("Der Coding-Projektordner existiert nicht.");
        var session = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die ausgewählte Coding-Sitzung wurde nicht gefunden.");
        await CodingWorkspaceGit.EnsureRepositoryAsync(fullPath, cancellationToken).ConfigureAwait(false);
        await chats.SetCodingWorkspacePathAsync(session.Id, fullPath, activateCoding: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> HasAudiobookVoiceContextAsync(CancellationToken cancellationToken = default)
    {
        var session = await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false);
        return session.PersistentToolAction == PersistentToolAction.Audiobook
            || await HasAudiobookContentAsync(session.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PromptTriggerAction?> GetRequiredMediaCaptureAsync(
        JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        var prompt = GetRequiredString(payload, "prompt", 100_000);
        var sessionId = GetOptionalGuid(payload, "sessionId")
            ?? (await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false)).Id;
        var session = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die AI-Sitzung wurde nicht gefunden.");
        var explicitTool = GetOptionalString(payload, "toolAction", 40);
        var match = await ResolvePromptMatchAsync(
            session,
            prompt,
            explicitTool,
            cancellationToken).ConfigureAwait(false);
        var action = match?.Trigger.Action;
        if (action is not (PromptTriggerAction.AudioAnalysis
            or PromptTriggerAction.VideoAnalysis
            or PromptTriggerAction.ImageAnalysis))
        {
            return null;
        }

        var hasDocuments = (await documents.ListAsync(sessionId, cancellationToken).ConfigureAwait(false)).Count > 0;
        var sessionAttachments = await attachments.ListAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return HasMediaAnalysisContext(action.Value, hasDocuments, sessionAttachments)
            ? null
            : action;
    }

    public async Task<bool> IsSpeechRequestAsync(
        JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        var prompt = GetOptionalString(payload, "prompt", 100_000) ?? string.Empty;
        var sessionId = GetOptionalGuid(payload, "sessionId")
            ?? (await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false)).Id;
        var session = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die AI-Sitzung wurde nicht gefunden.");
        var explicitTool = GetOptionalString(payload, "toolAction", 40);
        var match = await ResolvePromptMatchAsync(
            session,
            prompt,
            explicitTool,
            cancellationToken).ConfigureAwait(false);
        return match?.Trigger.Action == PromptTriggerAction.TextToSpeech;
    }

    internal static bool HasMediaAnalysisContext(
        PromptTriggerAction action,
        bool hasDocuments,
        IReadOnlyList<AssistantAttachment> sessionAttachments) =>
        hasDocuments || sessionAttachments.Any(item => action switch
        {
            PromptTriggerAction.ImageAnalysis => item.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase),
            PromptTriggerAction.VideoAnalysis => item.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase),
            PromptTriggerAction.AudioAnalysis => item.ContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase),
            _ => false,
        });

    internal static string MediaActionName(PromptTriggerAction action) => action switch
    {
        PromptTriggerAction.AudioAnalysis => "audioAnalysis",
        PromptTriggerAction.VideoAnalysis => "videoAnalysis",
        PromptTriggerAction.ImageAnalysis => "imageAnalysis",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    public async Task CancelCurrentAsync()
    {
        if (goAi is not null)
        {
            await goAi.CancelCurrentAndWaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task AddLiveCaptionResultAsync(
        string? transcript,
        string? error,
        CancellationToken cancellationToken = default)
    {
        var normalizedTranscript = FormatLiveCaptionText(transcript);
        var normalizedError = error?.Trim() ?? string.Empty;
        var title = normalizedError.Length == 0 ? "Live-Untertitel" : "Live-Untertitel fehlgeschlagen";
        var details = normalizedTranscript.Length > 0
            ? normalizedTranscript
            : normalizedError.Length > 0
                ? normalizedError
                : "Es wurde kein Sprachinhalt erkannt.";
        if (normalizedError.Length > 0 && normalizedTranscript.Length > 0)
        {
            details += $"\n\n**Fehler:** {normalizedError}";
        }
        var session = await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false);
        await chats.AddMessageAsync(
            session.Id,
            ChatRole.Assistant,
            $"**{title}**\n\n{details}",
            MessageStatus.Completed,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await recentActivity.RecordAsync(
            $"Live-Untertitel in AI-Sitzung „{session.Title}“ gespeichert",
            CancellationToken.None).ConfigureAwait(false);
    }

    private static string FormatLiveCaptionText(string? value)
    {
        var normalized = (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        // Markdown paragraphs preserve the speaker/segment boundaries in the
        // rendered chat. A single newline inside a paragraph would otherwise
        // be collapsed by HTML whitespace handling.
        var lines = normalized
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return string.Join("\n\n", lines);
    }

    public async Task<object> BuildSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var activeSession = await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false);
        var conversation = await conversationSnapshots.GetAsync(activeSession.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die aktive Chat-Sitzung wurde nicht gefunden.");
        var session = conversation.Session;
        var sessions = await chats.ListSessionsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var messages = conversation.Messages;
        var artifactItems = conversation.Artifacts;
        var workflowItems = await workflows.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var documentItems = await documents.ListAsync(session.Id, cancellationToken).ConfigureAwait(false);
        var attachmentItems = await attachments.ListAsync(session.Id, cancellationToken).ConfigureAwait(false);
        var documentGroupStatus = BuildDocumentGroupStatus(documentItems, attachmentItems.Count);
        // A snapshot is local UI state. Never make sidebar/session interaction wait for
        // the native model runtime, which may be offline or loading a model.
        var isCodingSession = session.PersistentToolAction == PersistentToolAction.Coding;
        var selectedModel = isCodingSession ? settings.Current.SelectedCodingModel : settings.Current.SelectedModel;
        var contextLimit = ModelContextProfiles.ResolveMaximum(selectedModel, isCodingSession ? "coding" : "general");
        ContextBuildResult context;
        if (isCodingSession)
        {
            // Match the history sent by the Coding client. General chat policies and
            // attached document pages are not part of that request.
            var codingHistory = GoAiAssistantService.BuildHistoryMessages(messages,
                GoAiAssistantService.CalculateCodingHistoryBudget(contextLimit, session.Draft));
            var historyCharacters = codingHistory.Sum(message => message.Content.Sum(part => part.Text?.Length ?? 0));
            var eligibleHistoryCount = messages.Count(message => message.Status == MessageStatus.Completed
                && message.Role is ChatRole.User or ChatRole.Assistant
                && !string.IsNullOrWhiteSpace(message.Content));
            context = new([], Math.Max(1, (historyCharacters + session.Draft.Length + 3) / 4),
                codingHistory.Count < eligibleHistoryCount,
                "Geschätzter Coding-Kontext aus Chatverlauf und Entwurf. Systemprompt und Werkzeugergebnisse ergänzt der Server während des Laufs.");
        }
        else
        {
            var pages = new List<DocumentPage>();
            foreach (var document in documentItems)
            {
                pages.AddRange(await documents.ReadPagesAsync(document.Id, cancellationToken).ConfigureAwait(false));
            }
            context = contextAssembler.Build(new(
                DefaultSystemPrompt,
                string.IsNullOrWhiteSpace(session.Draft) ? "Nächste Benutzereingabe" : session.Draft,
                messages,
                null,
                pages,
                contextLimit));
        }
        _displayStates.TryGetValue(session.Id, out var display);
        if (display is not null && (display.IsCoding != isCodingSession || display.ModelSelection != selectedModel)) display = null;
        var displayMessage = display is null ? null : messages.FirstOrDefault(message => message.Id == display.MessageId);
        var isSessionRunning = settings.Current.IsAiConnectionEnabled && (
            goAi?.IsRunning == true && goAi.ActiveSessionId == session.Id
            || display?.IsRunning == true && displayMessage?.Status is MessageStatus.Pending or MessageStatus.Streaming);
        return new
        {
            sessionGroups = (await chats.ListSessionGroupsAsync(cancellationToken).ConfigureAwait(false)).Select(group => new
            {
                group.Id,
                group.Name,
                group.IsCollapsed,
                group.WorkspacePath,
                sessionIds = sessions.Where(item => item.SessionGroupId == group.Id).Select(item => item.Id),
            }),
            sessions = sessions.Select(ToSessionDto),
            messages = messages.Select(message => ToMessageDto(
                message,
                artifactItems.TryGetValue(message.Id, out var messageArtifacts) ? messageArtifacts : null)),
            workflows = workflowItems.Select(ToWorkflowDto),
            conversationRevision = session.ConversationRevision,
            documents = documentItems.Select(ToDocumentDto),
            attachments = attachmentItems.Select(ToAttachmentDto),
            documentGroupStatus,
            activeSessionId = session.Id,
            draft = session.Draft,
            isRunning = isSessionRunning,
            isAiBusy = settings.Current.IsAiConnectionEnabled && (goAi?.IsRunning == true || _displayStates.Values.Any(item => item.IsRunning)),
            activeRunSessionId = goAi?.ActiveSessionId,
            activeRunId = goAi?.ActiveRunId,
            runMessageId = isSessionRunning ? display?.MessageId : null,
            runStatus = isSessionRunning ? display?.Status ?? "Denkt nach" : null,
            runDetail = isSessionRunning ? display?.Detail : null,
            loadedFiles = display?.LoadedFiles,
            model = display?.Model ?? (isCodingSession ? selectedModel ?? "GO AI Server" : "GO AI Server"),
            provider = settings.Current.AiProvider.ToString(),
            contextUsed = display?.ContextUsed ?? context.EstimatedTokens,
            contextLimit = display?.ContextLimit ?? contextLimit,
            contextWasTruncated = display?.ContextUsed is not null ? display.ContextWasCompacted : context.WasTruncated,
            contextNotice = display?.ContextUsed is not null ? null : context.TruncationNotice,
            contextSource = display?.ContextUsed is not null ? "measured" : "estimated",
            contextMessageId = display?.ContextUsed is not null ? display.MessageId : (Guid?)null,
            selectedToolAction = PersistentToolActionName(session.PersistentToolAction),
            reasoningModelId = selectedModel,
            reasoningRole = isCodingSession ? "coding" : "general",
            codingWorkspacePath = session.CodingWorkspacePath,
            codingToolStepsExpanded = settings.Current.CodingToolStepsExpanded,
            changesSummary = goAi is null ? null : await goAi.GetChangesSummaryAsync(session.Id, messages, cancellationToken).ConfigureAwait(false),
            isSessionPaneOpen = settings.Current.IsAssistantSessionPaneOpen,
        };
    }

    private async Task<object> BuildConversationSnapshotAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var conversation = await conversationSnapshots.GetAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die Chat-Sitzung wurde nicht gefunden.");
        return new
        {
            activeSessionId = sessionId,
            conversationRevision = conversation.Session.ConversationRevision,
            codingToolStepsExpanded = settings.Current.CodingToolStepsExpanded,
            changesSummary = goAi is null ? null : await goAi.GetChangesSummaryAsync(sessionId, conversation.Messages, cancellationToken).ConfigureAwait(false),
            messages = conversation.Messages.Select(message => ToMessageDto(
                message,
                conversation.Artifacts.TryGetValue(message.Id, out var messageArtifacts) ? messageArtifacts : null)),
        };
    }

    private async Task EmitCommittedMessageAsync(
        Guid messageId,
        Func<string, object, string?, Task> emit,
        string requestId)
    {
        var messageReference = await chats.GetMessageAsync(
            messageId,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        if (messageReference is null)
        {
            return;
        }
        var conversation = await conversationSnapshots.GetAsync(
            messageReference.SessionId,
            CancellationToken.None).ConfigureAwait(false);
        var message = conversation?.Messages.FirstOrDefault(candidate => candidate.Id == messageId);
        if (conversation is null || message is null)
        {
            return;
        }
        await emit("conversation.messageCommitted", new
        {
            sessionId = message.SessionId,
            conversationRevision = conversation.Session.ConversationRevision,
            message = ToMessageDto(
                message,
                conversation.Artifacts.TryGetValue(message.Id, out var messageArtifacts) ? messageArtifacts : null),
        }, requestId).ConfigureAwait(false);
    }

    public async Task HandleAsync(
        WebBridgeEnvelope envelope,
        Func<string, object, string?, Task> emit,
        CancellationToken cancellationToken = default)
    {
        switch (envelope.Type)
        {
            case "app.ready":
            {
                var isFirstReady = Interlocked.CompareExchange(ref _startupRunsHandled, 1, 0) == 0;
                if (isFirstReady && goAi is not null)
                {
                    await goAi.StopPersistedRunsAtStartupAsync(cancellationToken).ConfigureAwait(false);
                }
                await emit("state.snapshot", await BuildSnapshotAsync(cancellationToken), envelope.RequestId);
                if (settings.Current.IsAiConnectionEnabled
                    && settings.Current.AiProvider == AiProviderKind.GoAiServer
                    && goAi is not null)
                {
                    lock (_resumeLock)
                    {
                        // A new WebView can arrive while the previous cancelled reader is
                        // still releasing its run gate. Always attach this page afterwards.
                        _resumeTask = ResumePendingInBackgroundAsync(emit, envelope.RequestId, cancellationToken, _resumeTask);
                    }
                }
                break;
            }
            case "session.create":
                await CreateSessionAsync(emit, envelope.RequestId, cancellationToken);
                break;
            case "session.projectCreate":
                await CreateWorkspaceSessionAsync(GetRequiredString(envelope.Payload, "workspacePath", 32_768), emit, envelope.RequestId, cancellationToken);
                break;
            case "session.open":
                await OpenSessionAsync(GetRequiredGuid(envelope.Payload, "sessionId"), emit, envelope.RequestId, cancellationToken);
                break;
            case "conversation.refresh":
            {
                var requestedSessionId = GetOptionalGuid(envelope.Payload, "sessionId")
                    ?? (await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false)).Id;
                await emit(
                    "conversation.snapshot",
                    await BuildConversationSnapshotAsync(requestedSessionId, cancellationToken).ConfigureAwait(false),
                    envelope.RequestId).ConfigureAwait(false);
                break;
            }
            case "session.rename":
                await RenameSessionAsync(
                    GetRequiredGuid(envelope.Payload, "sessionId"),
                    GetRequiredString(envelope.Payload, "title", 160),
                    emit,
                    envelope.RequestId,
                    cancellationToken);
                break;
            case "session.delete":
                await DeleteSessionAsync(GetRequiredGuid(envelope.Payload, "sessionId"), emit, envelope.RequestId, cancellationToken);
                break;
            case "session.clear":
                await ClearSessionsAsync(emit, envelope.RequestId, cancellationToken);
                break;
            case "session.draft":
                await chats.SaveDraftAsync(
                    GetRequiredGuid(envelope.Payload, "sessionId"),
                    GetOptionalString(envelope.Payload, "draft", 100_000) ?? string.Empty,
                    cancellationToken);
                await emit("draft.saved", new { }, envelope.RequestId);
                break;
            case "session.pin":
                await SetSessionPinnedAsync(
                    GetRequiredGuid(envelope.Payload, "sessionId"),
                    envelope.Payload.TryGetProperty("pinned", out var pinnedElement) && pinnedElement.ValueKind == JsonValueKind.True,
                    emit,
                    envelope.RequestId,
                    cancellationToken).ConfigureAwait(false);
                break;
            case "session.groupCollapse":
            {
                var groupId = GetRequiredGuid(envelope.Payload, "groupId");
                var collapsed = envelope.Payload.TryGetProperty("collapsed", out var collapsedElement)
                    && collapsedElement.ValueKind == JsonValueKind.True;
                await chats.SetSessionGroupCollapsedAsync(groupId, collapsed, cancellationToken).ConfigureAwait(false);
                await emit("session.grouped", await BuildSessionSidebarSnapshotAsync(false, cancellationToken), envelope.RequestId).ConfigureAwait(false);
                break;
            }
            case "session.tool":
                await SetSessionToolAsync(
                    GetRequiredGuid(envelope.Payload, "sessionId"),
                    GetOptionalString(envelope.Payload, "action", 32),
                    emit,
                    envelope.RequestId,
                    cancellationToken).ConfigureAwait(false);
                break;
            case "reasoning.get":
            case "reasoning.set":
            {
                if (goAi is null) throw new InvalidOperationException("AI-Verbindung ist nicht verfügbar.");
                var role = GetOptionalString(envelope.Payload, "role", 16) ?? "general";
                if (role is not ("coding" or "general")) throw new ArgumentException("Unbekannte Modellrolle.");
                var modelId = GetOptionalString(envelope.Payload, "modelId", 512) ?? "";
                var selectedId = role == "coding" ? settings.Current.SelectedCodingModel : settings.Current.SelectedModel;
                if (modelId != selectedId) throw new InvalidOperationException("Die Modellauswahl hat sich geändert. Öffne Reasoning erneut.");
                var options = await goAi.GetReasoningOptionsAsync(modelId, role, cancellationToken).ConfigureAwait(false);
                if (envelope.Type == "reasoning.set")
                {
                    EnsureContextCanChange();
                    if (modelId != (role == "coding" ? settings.Current.SelectedCodingModel : settings.Current.SelectedModel))
                        throw new InvalidOperationException("Das ausgewählte Modell hat sich während der Prüfung geändert.");
                    var effort = GetRequiredString(envelope.Payload, "effort", 32).Trim().ToLowerInvariant();
                    if (!options.Available || !options.Levels.Contains(effort))
                        throw new ArgumentException("Diese Reasoning-Stufe wird vom ausgewählten Modell nicht unterstützt.");
                    await settings.UpdateAsync(current =>
                    {
                        var choices = new Dictionary<string, string>(current.ReasoningEffortsByModel, StringComparer.OrdinalIgnoreCase);
                        var key = GoAiAssistantService.ReasoningKey(modelId, role);
                        choices[key] = effort;
                        return current with { ReasoningEffortsByModel = choices };
                    }, cancellationToken).ConfigureAwait(false);
                    options = options with { Selected = effort };
                }
                await emit("reasoning.snapshot", options, envelope.RequestId);
                break;
            }
            case "chat.send":
                await SendChatAsync(envelope, emit, cancellationToken);
                break;
            case "chat.steer":
                if (goAi is null) throw new InvalidOperationException("AI-Verbindung ist nicht verfügbar.");
                var steeringSessionId = GetRequiredGuid(envelope.Payload, "sessionId");
                var steeringAccepted = await goAi.SteerAsync(steeringSessionId,
                    GetRequiredString(envelope.Payload, "prompt", 100_000),
                    GetRequiredString(envelope.Payload, "inputId", 128),
                    GetOptionalString(envelope.Payload, "expectedRunId", 128), cancellationToken).ConfigureAwait(false);
                await emit("chat.steer.accepted", new { inputId = steeringAccepted.InputId,
                    sessionId = steeringSessionId, runId = steeringAccepted.RunId, sequence = steeringAccepted.Sequence }, envelope.RequestId).ConfigureAwait(false);
                break;
            case "chat.cancel":
                await CancelCurrentAsync().ConfigureAwait(false);
                break;
            case "document.remove":
                EnsureContextCanChange();
                await documents.RemoveAsync(GetRequiredGuid(envelope.Payload, "documentId"), cancellationToken);
                await emit("document.changed", await BuildSnapshotAsync(cancellationToken), envelope.RequestId);
                break;
            case "attachment.remove":
                EnsureContextCanChange();
                await attachments.RemoveAsync(GetRequiredGuid(envelope.Payload, "attachmentId"), cancellationToken);
                await emit("document.changed", await BuildSnapshotAsync(cancellationToken), envelope.RequestId);
                break;
            case "workflow.list":
                await ListWorkflowsAsync(envelope, emit, cancellationToken);
                break;
            case "workflow.insert":
                await InsertWorkflowAsync(envelope, emit, cancellationToken);
                break;
            case "workflow.create":
                await CreateWorkflowAsync(envelope, emit, cancellationToken);
                break;
            case "workflow.update":
                await UpdateWorkflowAsync(envelope, emit, cancellationToken);
                break;
            case "workflow.delete":
                await workflows.DeleteAsync(
                    GetRequiredGuid(envelope.Payload, "workflowId"),
                    GetRequiredInt64(envelope.Payload, "revision"),
                    cancellationToken);
                await emit("workflow.changed", await BuildSnapshotAsync(cancellationToken), envelope.RequestId);
                break;
            case "workflow.createFromMessage":
                await CreateWorkflowFromMessageAsync(envelope, emit, cancellationToken);
                break;
            case "ui.sessionPane":
                await settings.UpdateAsync(current => current with
                {
                    IsAssistantSessionPaneOpen = GetRequiredBoolean(envelope.Payload, "isOpen"),
                }, CancellationToken.None).ConfigureAwait(false);
                break;
        }
    }

    public async Task ImportDocumentAsync(
        Guid sessionId,
        string fileName,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        EnsureContextCanChange();
        var result = await documents.ImportAsync(sessionId, fileName, content, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Error ?? "Das Dokument konnte nicht importiert werden.");
        }

        if (!result.HasExtractableText)
        {
            if (result.Document is { } emptyDocument)
            {
                await documents.RemoveAsync(emptyDocument.Id, CancellationToken.None).ConfigureAwait(false);
            }

            throw new InvalidOperationException("Das Dokument enthält keinen extrahierbaren Text. OCR ist in dieser Version nicht enthalten.");
        }

        var session = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die Sitzung wurde nicht gefunden.");
        await recentActivity.RecordAsync(
            $"Datei „{fileName}“ zur AI-Sitzung „{session.Title}“ hinzugefügt",
            CancellationToken.None).ConfigureAwait(false);
    }

    public IReadOnlySet<string> SupportedDocumentExtensions => documents.SupportedExtensions;

    public async Task ImportAttachmentAsync(
        Guid sessionId,
        string fileName,
        string contentType,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        EnsureContextCanChange();
        _ = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die Sitzung wurde nicht gefunden.");
        _ = await attachments.ImportAsync(sessionId, fileName, contentType, content, cancellationToken).ConfigureAwait(false);
        var session = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await recentActivity.RecordAsync(
            $"Datei „{fileName}“ zur AI-Sitzung „{session?.Title ?? DefaultSessionTitle}“ hinzugefügt",
            CancellationToken.None).ConfigureAwait(false);
    }

    private void EnsureContextCanChange()
    {
        if (goAi?.IsRunning == true)
        {
            throw new InvalidOperationException("Anhänge und Dokumente können während eines laufenden AI-Auftrags nicht geändert werden.");
        }
    }

    private async Task<ChatSession> EnsureActiveSessionAsync(CancellationToken cancellationToken)
    {
        if (settings.Current.ActiveSessionId is { } activeId
            && await chats.GetSessionAsync(activeId, cancellationToken).ConfigureAwait(false) is { } active)
        {
            return active;
        }

        var existing = await chats.ListSessionsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var session = existing.Count > 0
            ? existing[0]
            : await chats.CreateSessionAsync(DefaultSessionTitle, cancellationToken).ConfigureAwait(false);
        await settings.UpdateAsync(current => current with { ActiveSessionId = session.Id }, cancellationToken).ConfigureAwait(false);
        return session;
    }

    private async Task CreateSessionAsync(
        Func<string, object, string?, Task> emit,
        string requestId,
        CancellationToken cancellationToken)
    {
        var session = await chats.CreateSessionAsync(DefaultSessionTitle, cancellationToken).ConfigureAwait(false);
        await settings.UpdateAsync(current => current with { ActiveSessionId = session.Id }, cancellationToken).ConfigureAwait(false);
        await recentActivity.RecordAsync(
            $"AI-Sitzung „{session.Title}“ erstellt",
            CancellationToken.None).ConfigureAwait(false);
        await emit("session.changed", await BuildSnapshotAsync(cancellationToken), requestId);
    }

    public async Task CreateWorkspaceSessionAsync(
        string workspacePath,
        Func<string, object, string?, Task> emit,
        string requestId,
        CancellationToken cancellationToken)
    {
        EnsureContextCanChange();
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        var fullPath = Path.GetFullPath(workspacePath.Trim());
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException("Der Projektordner existiert nicht.");
        var group = await chats.GetOrCreateSessionGroupForWorkspaceAsync(fullPath, cancellationToken).ConfigureAwait(false);
        var session = await chats.CreateSessionAsync(DefaultSessionTitle, cancellationToken).ConfigureAwait(false);
        await chats.SetCodingWorkspacePathAsync(session.Id, fullPath, activateCoding: true, cancellationToken).ConfigureAwait(false);
        await chats.SetSessionGroupCollapsedAsync(group.Id, false, cancellationToken).ConfigureAwait(false);
        await settings.UpdateAsync(current => current with { ActiveSessionId = session.Id }, cancellationToken).ConfigureAwait(false);
        await recentActivity.RecordAsync(
            $"Neue Sitzung im Projekt \"{group.Name}\" gestartet",
            CancellationToken.None).ConfigureAwait(false);
        await emit("session.changed", await BuildSnapshotAsync(cancellationToken), requestId);
    }

    private async Task SetSessionToolAsync(
        Guid sessionId,
        string? requestedAction,
        Func<string, object, string?, Task> emit,
        string requestId,
        CancellationToken cancellationToken)
    {
        var action = requestedAction?.Trim() switch
        {
            null or "" => (PersistentToolAction?)null,
            "bricsCad" => PersistentToolAction.BricsCad,
            "audiobook" => PersistentToolAction.Audiobook,
            "coding" => PersistentToolAction.Coding,
            _ => throw new InvalidOperationException("Die angeforderte persistente Tool-Aktion ist unbekannt."),
        };
        var session = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die ausgewählte Tool-Sitzung wurde nicht gefunden.");
        await chats.SetPersistentToolActionAsync(session.Id, action, cancellationToken).ConfigureAwait(false);
        await emit("session.changed", await BuildSnapshotAsync(cancellationToken), requestId).ConfigureAwait(false);
    }

    private async Task OpenSessionAsync(
        Guid sessionId,
        Func<string, object, string?, Task> emit,
        string requestId,
        CancellationToken cancellationToken)
    {
        var session = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die Sitzung wurde nicht gefunden.");
        await settings.UpdateAsync(current => current with { ActiveSessionId = sessionId }, cancellationToken).ConfigureAwait(false);
        await recentActivity.RecordAsync(
            $"AI-Sitzung „{session.Title}“ geöffnet",
            CancellationToken.None).ConfigureAwait(false);
        await emit("session.changed", await BuildSnapshotAsync(cancellationToken), requestId);
    }

    private async Task RenameSessionAsync(
        Guid sessionId,
        string title,
        Func<string, object, string?, Task> emit,
        string requestId,
        CancellationToken cancellationToken)
    {
        _ = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die Sitzung wurde nicht gefunden.");
        await chats.RenameSessionAsync(sessionId, title, cancellationToken).ConfigureAwait(false);
        await recentActivity.RecordAsync(
            $"AI-Sitzung in „{title}“ umbenannt",
            CancellationToken.None).ConfigureAwait(false);
        await emit("session.changed", await BuildSnapshotAsync(cancellationToken), requestId);
    }

    private async Task SetSessionPinnedAsync(
        Guid sessionId,
        bool pinned,
        Func<string, object, string?, Task> emit,
        string requestId,
        CancellationToken cancellationToken)
    {
        var session = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die Sitzung wurde nicht gefunden.");
        await chats.SetPinnedAsync(sessionId, pinned, cancellationToken).ConfigureAwait(false);
        await recentActivity.RecordAsync(
            $"AI-Sitzung „{session.Title}“ { (pinned ? "angepinnt" : "losgelöst") }",
            CancellationToken.None).ConfigureAwait(false);
        await emit("session.changed", await BuildSnapshotAsync(cancellationToken), requestId).ConfigureAwait(false);
    }

    private async Task DeleteSessionAsync(
        Guid sessionId,
        Func<string, object, string?, Task> emit,
        string requestId,
        CancellationToken cancellationToken)
    {
        var session = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die Sitzung wurde nicht gefunden.");

        if (goAi?.ActiveSessionId == sessionId)
        {
            await goAi.CancelCurrentAndWaitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        await chats.DeleteSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        _displayStates.TryRemove(sessionId, out _);
        await CleanupDeletedCodingChangesAsync([sessionId]).ConfigureAwait(false);
        if (settings.Current.ActiveSessionId == sessionId)
        {
            await settings.UpdateAsync(current => current with { ActiveSessionId = null }, cancellationToken).ConfigureAwait(false);
        }

        await recentActivity.RecordAsync(
            $"AI-Sitzung „{session.Title}“ gelöscht",
            CancellationToken.None).ConfigureAwait(false);
        _ = await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false);
        await emit("session.changed", await BuildSnapshotAsync(cancellationToken), requestId);
    }

    private async Task ClearSessionsAsync(
        Func<string, object, string?, Task> emit,
        string requestId,
        CancellationToken cancellationToken)
    {
        if (goAi?.IsRunning == true)
        {
            throw new InvalidOperationException("Die Sitzungen können während einer laufenden Antwort nicht gelöscht werden.");
        }

        var activeSessionId = settings.Current.ActiveSessionId;
        var previousSessions = await chats.ListSessionsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var deletedCount = await chats.DeleteUnpinnedSessionsAsync(cancellationToken).ConfigureAwait(false);
        if (deletedCount > 0)
            await CleanupDeletedCodingChangesAsync(previousSessions.Select(static session => session.Id)).ConfigureAwait(false);

        if (activeSessionId is { } currentActiveSessionId
            && await chats.GetSessionAsync(currentActiveSessionId, cancellationToken).ConfigureAwait(false) is null)
        {
            await settings.UpdateAsync(
                current => current with { ActiveSessionId = null },
                cancellationToken).ConfigureAwait(false);
        }
        if (deletedCount > 0)
        {
            await recentActivity.RecordAsync(
                "Alle nicht angepinnten AI-Sitzungen gelöscht",
                CancellationToken.None).ConfigureAwait(false);
        }

        _ = await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false);
        await emit("session.changed", await BuildSnapshotAsync(cancellationToken), requestId);
    }

    private async Task CleanupDeletedCodingChangesAsync(IEnumerable<Guid> candidateSessionIds)
    {
        // Re-read after the committed deletion: a session pinned concurrently
        // with bulk clearing must keep both its history and its baseline cache.
        var remaining = (await chats.ListSessionsAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false))
            .Select(static session => session.Id).ToHashSet();
        foreach (var sessionId in candidateSessionIds.Where(id => !remaining.Contains(id)))
        {
            _displayStates.TryRemove(sessionId, out _);
            try { await CodingChangesMonitor.DeleteSessionStorageAsync(settings.DataDirectory, sessionId).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.TraceWarning("Die gelöschte Sitzung {0} hinterließ einen nicht löschbaren Änderungscache: {1}", sessionId, exception.Message);
            }
            try { await CodingRunEvidenceStore.DeleteSessionAsync(settings.DataDirectory, sessionId).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                System.Diagnostics.Trace.TraceWarning("Die gelöschte Sitzung {0} hinterließ nicht löschbare Werkzeugbelege: {1}", sessionId, exception.Message);
            }
        }
    }

    private async Task SendChatAsync(
        WebBridgeEnvelope envelope,
        Func<string, object, string?, Task> emit,
        CancellationToken cancellationToken)
    {
        if (!settings.Current.IsAiConnectionEnabled)
        {
            throw new GoAiConnectionDisabledException();
        }

        await SendGoAiChatAsync(envelope, emit, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendGoAiChatAsync(
        WebBridgeEnvelope envelope,
        Func<string, object, string?, Task> emit,
        CancellationToken cancellationToken)
    {
        var prompt = GetRequiredString(envelope.Payload, "prompt", 100_000);
        if (IsCancelCommand(prompt))
        {
            if (microphone is not null)
            {
                await microphone.StopSpeechAsync(cancellationToken).ConfigureAwait(false);
            }
            if (goAi is not null)
            {
                await goAi.CancelCurrentAsync(CancellationToken.None).ConfigureAwait(false);
            }
            await emit("speech.status", new
            {
                active = false,
                status = "Abgebrochen",
                detail = (string?)null,
                model = (string?)null,
                error = (string?)null,
            }, envelope.RequestId).ConfigureAwait(false);
            return;
        }
        var sessionId = GetOptionalGuid(envelope.Payload, "sessionId")
            ?? (await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false)).Id;
        var session = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die AI-Sitzung wurde nicht gefunden.");
        await chats.SaveDraftAsync(sessionId, string.Empty, cancellationToken).ConfigureAwait(false);
        var explicitTool = GetOptionalString(envelope.Payload, "toolAction", 40);
        var match = await ResolvePromptMatchAsync(
            session,
            prompt,
            explicitTool,
            cancellationToken).ConfigureAwait(false);
        await settings.UpdateAsync(current => current with
        {
            ActiveSessionId = sessionId,
        }, cancellationToken).ConfigureAwait(false);
        var speechMessageId = GetOptionalGuid(envelope.Payload, "speechMessageId");
        if (match?.Trigger.Action == PromptTriggerAction.TextToSpeech)
        {
            var serverAssistant = goAi
                ?? throw new InvalidOperationException("Der GO-AI-Clientdienst ist nicht verfügbar.");
            await serverAssistant.SpeakAsync(
                sessionId,
                match.RemainingPrompt,
                speechMessageId,
                speech => emit("speech.status", new
                {
                    active = speech.IsActive,
                    status = speech.Status,
                    detail = speech.Detail,
                    model = speech.Model,
                    directionModel = speech.DirectionModel,
                    error = speech.Error,
                    cacheHit = speech.CacheHit,
                }, envelope.RequestId),
                playback => emit(
                    "speech.progress",
                    SpeechPlaybackProgressBridge.ToPayload(playback),
                    envelope.RequestId),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }
        var requestedPersistentAction = PersistentToolActionFor(match?.Trigger.Action);
        if (requestedPersistentAction == PersistentToolAction.Audiobook
            && IsAudiobookContinuationRequest(prompt, match)
            && !await HasAudiobookContentAsync(session.Id, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "In dieser Sitzung ist noch keine Hörbuchgeschichte vorhanden. Starte zuerst mit „Hörbuch erstellen“.");
        }
        if (requestedPersistentAction is { } persistentAction
            && session.PersistentToolAction != persistentAction)
        {
            await chats.SetPersistentToolActionAsync(session.Id, persistentAction, cancellationToken).ConfigureAwait(false);
            await emit(
                "session.changed",
                await BuildSnapshotAsync(cancellationToken).ConfigureAwait(false),
                envelope.RequestId).ConfigureAwait(false);
        }
        try
        {
            var serverAssistant = goAi
                ?? throw new InvalidOperationException("Der GO-AI-Clientdienst ist nicht verfügbar.");
            _ = await serverAssistant.SendAsync(
                sessionId,
                prompt,
                match,
                update => EmitGoAiUpdateAsync(update, emit, envelope.RequestId),
                cancellationToken).ConfigureAwait(false);
        }
        catch (GoAiStreamDetachedException)
        {
            // Navigating away only detaches the local SSE reader. The run is resumed from SQLite later.
        }
    }

    private async Task ResumePendingInBackgroundAsync(Func<string, object, string?, Task> emit,
        string requestId, CancellationToken cancellationToken, Task? previous = null)
    {
        try
        {
            if (previous is not null) await previous.WaitAsync(cancellationToken).ConfigureAwait(false);
            await goAi!.ResumePendingAsync(update => EmitGoAiUpdateAsync(update, emit, requestId), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (GoAiStreamDetachedException) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            try { await emit("host.error", new { message = exception.Message }, requestId).ConfigureAwait(false); }
            catch (Exception bridgeException) when (bridgeException is not OutOfMemoryException) { }
        }
    }

    internal async Task<object> BuildSessionSidebarSnapshotAsync(bool groupingCompleted, CancellationToken cancellationToken = default)
    {
        var sessions = await chats.ListSessionsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var groups = await chats.ListSessionGroupsAsync(cancellationToken).ConfigureAwait(false);
        // Grouping never owns the active conversation. The user may switch sessions,
        // type a draft or continue an AI run before this independent request finishes.
        return new
        {
            groupingCompleted,
            sessions = sessions.Select(ToSessionDto),
            sessionGroups = groups.Select(group => new
            {
                group.Id,
                group.Name,
                group.IsCollapsed,
                group.WorkspacePath,
                sessionIds = sessions.Where(session => session.SessionGroupId == group.Id).Select(session => session.Id),
            }),
        };
    }

    internal static PromptTriggerMatch CreateToolMatch(string toolAction, string prompt)
    {
        var action = toolAction switch
        {
            "audioAnalysis" => PromptTriggerAction.AudioAnalysis,
            "imageAnalysis" => PromptTriggerAction.ImageAnalysis,
            "imageGeneration" => PromptTriggerAction.ImageGeneration,
            "bricsCad" => PromptTriggerAction.BricsCad,
            "audiobook" => PromptTriggerAction.Audiobook,
            "coding" => PromptTriggerAction.Coding,
            "textToSpeech" => PromptTriggerAction.TextToSpeech,
            "translation" => PromptTriggerAction.Translation,
            "videoAnalysis" => PromptTriggerAction.VideoAnalysis,
            "webSearch" => PromptTriggerAction.WebSearch,
            "youTubeSearch" => PromptTriggerAction.YouTubeSearch,
            _ => throw new ArgumentException("Die ausgewählte Tool-Aktion ist nicht bekannt."),
        };
        var now = DateTimeOffset.UtcNow;
        var trigger = new PromptTrigger(
            Guid.Empty,
            action,
            toolAction,
            "Einmalig über das Prompt-Tools-Menü ausgewählt.",
            PromptTriggerMatchMode.Exact,
            true,
            int.MaxValue,
            0,
            now,
            now);
        return new PromptTriggerMatch(trigger, prompt, prompt.Trim());
    }

    private async Task<PromptTriggerMatch?> ResolvePromptMatchAsync(
        ChatSession session,
        string prompt,
        string? explicitTool,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(explicitTool))
        {
            return CreateToolMatch(explicitTool, prompt);
        }

        var databaseMatch = await promptTriggers.MatchAsync(prompt, cancellationToken).ConfigureAwait(false);
        if (databaseMatch?.Trigger.Action == PromptTriggerAction.TextToSpeech)
        {
            return databaseMatch;
        }
        if (databaseMatch is not null)
        {
            return databaseMatch;
        }
        return session.PersistentToolAction switch
        {
            PersistentToolAction.BricsCad => CreateToolMatch("bricsCad", prompt),
            PersistentToolAction.Audiobook => CreateToolMatch("audiobook", prompt),
            PersistentToolAction.Coding => CreateToolMatch("coding", prompt),
            _ => null,
        };
    }

    private async Task<bool> HasAudiobookContentAsync(Guid sessionId, CancellationToken cancellationToken) =>
        (await chats.ListMessagesAsync(sessionId, cancellationToken).ConfigureAwait(false)).Any(static message =>
            message.Role == ChatRole.Assistant
            && message.ContentProfile == MessageContentProfile.Audiobook
            && !string.IsNullOrWhiteSpace(message.Content)
            && message.Status is MessageStatus.Completed or MessageStatus.Cancelled or MessageStatus.Interrupted);

    private static bool IsAudiobookContinuationRequest(string prompt, PromptTriggerMatch? match)
    {
        if (match?.Trigger.Action != PromptTriggerAction.Audiobook)
        {
            return false;
        }
        var normalized = prompt.Trim();
        return normalized.StartsWith("Hörbuch fortsetzen", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("Hoerbuch fortsetzen", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Fortsetzen", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("Fortsetzen:", StringComparison.OrdinalIgnoreCase);
    }

    private static PersistentToolAction? PersistentToolActionFor(PromptTriggerAction? action) => action switch
    {
        PromptTriggerAction.BricsCad => PersistentToolAction.BricsCad,
        PromptTriggerAction.Audiobook => PersistentToolAction.Audiobook,
        PromptTriggerAction.Coding => PersistentToolAction.Coding,
        _ => null,
    };

    private static string? PersistentToolActionName(PersistentToolAction? action) => action switch
    {
        PersistentToolAction.BricsCad => "bricsCad",
        PersistentToolAction.Audiobook => "audiobook",
        PersistentToolAction.Coding => "coding",
        _ => null,
    };

    internal async Task EmitGoAiUpdateAsync(
        GoAiAssistantUpdate update,
        Func<string, object, string?, Task> emit,
        string requestId)
    {
        await ObserveDisplayStateAsync(update).ConfigureAwait(false);
        if (update.Kind == GoAiAssistantUpdateKind.FileChangesChanged)
        {
            if (update.ChangesSummary is { } summary)
                await emit("coding.changes", summary, requestId).ConfigureAwait(false);
            return;
        }
        goAi?.ObserveAutomaticSpeech(update,
            speech => emit("speech.status", new
            {
                active = speech.IsActive,
                status = speech.Status,
                detail = speech.Detail,
                model = speech.Model,
                error = speech.Error,
                cacheHit = speech.CacheHit,
            }, requestId),
            playback => emit("speech.progress", SpeechPlaybackProgressBridge.ToPayload(playback), requestId));
        var artifactsForMessage = update.Artifacts
            ?? await artifacts.ListForMessageAsync(update.Message.Id, CancellationToken.None).ConfigureAwait(false);
        switch (update.Kind)
        {
            case GoAiAssistantUpdateKind.Started:
                var sessionMessages = await chats.ListMessagesAsync(
                    update.Message.SessionId,
                    CancellationToken.None).ConfigureAwait(false);
                var pendingAttachments = await attachments.ListAsync(
                    update.Message.SessionId,
                    CancellationToken.None).ConfigureAwait(false);
                var precedingUserMessage = sessionMessages
                    .TakeWhile(message => message.Id != update.Message.Id)
                    .LastOrDefault(message => message.Role == ChatRole.User);
                var precedingUserArtifacts = precedingUserMessage is null
                    ? null
                    : await artifacts.ListForMessageAsync(precedingUserMessage.Id, CancellationToken.None).ConfigureAwait(false);
                await emit(
                    "conversation.snapshot",
                    await BuildConversationSnapshotAsync(update.Message.SessionId, CancellationToken.None).ConfigureAwait(false),
                    requestId).ConfigureAwait(false);
                await emit("chat.started", new
                {
                    sessionId = update.Message.SessionId,
                    userMessage = precedingUserMessage is null
                        ? null
                        : ToMessageDto(precedingUserMessage, precedingUserArtifacts),
                    message = ToMessageDto(update.Message, artifactsForMessage),
                    runId = goAi?.ActiveRunId,
                    contextUsed = update.ContextUsed,
                    contextLimit = update.ContextLimit,
                    contextWasTruncated = update.ContextWasCompacted,
                    runStatus = update.Status,
                    runDetail = update.Detail,
                    model = update.Model,
                    loadedFiles = update.LoadedFiles,
                    attachments = pendingAttachments.Select(ToAttachmentDto),
                }, requestId).ConfigureAwait(false);
                break;
            case GoAiAssistantUpdateKind.Delta:
                await EmitCommittedMessageAsync(update.Message.Id, emit, requestId).ConfigureAwait(false);
                await emit("chat.delta", new
                {
                    messageId = update.Message.Id,
                    sessionId = update.Message.SessionId,
                    content = update.Message.Content,
                    toolSteps = update.Message.ToolSteps ?? [],
                }, requestId).ConfigureAwait(false);
                break;
            case GoAiAssistantUpdateKind.Status:
                await emit("status.changed", new
                {
                    messageId = update.Message.Id,
                    sessionId = update.Message.SessionId,
                    runStatus = update.Status,
                    runDetail = update.Detail,
                    model = update.Model,
                    contextUsed = update.ContextUsed,
                    toolStep = update.ToolStep,
                    runId = goAi?.ActiveRunId,
                    contextLimit = update.ContextLimit,
                    contextWasTruncated = update.ContextUsed.HasValue ? update.ContextWasCompacted : (bool?)null,
                    loadedFiles = update.LoadedFiles,
                }, requestId).ConfigureAwait(false);
                break;
            case GoAiAssistantUpdateKind.ArtifactsChanged:
                await EmitCommittedMessageAsync(update.Message.Id, emit, requestId).ConfigureAwait(false);
                break;
            case GoAiAssistantUpdateKind.DocumentsChanged:
                await emit("session.changed", await BuildSnapshotAsync(CancellationToken.None), requestId).ConfigureAwait(false);
                break;
            case GoAiAssistantUpdateKind.Completed:

                await EmitCommittedMessageAsync(update.Message.Id, emit, requestId).ConfigureAwait(false);
                await emit("chat.completed", new
                {
                    sessionId = update.Message.SessionId,
                    message = ToMessageDto(update.Message, artifactsForMessage),
                    session = update.Session is null ? null : ToSessionDto(update.Session),
                    runStatus = update.Status,
                    runDetail = update.Detail,
                }, requestId).ConfigureAwait(false);
                break;
            case GoAiAssistantUpdateKind.Cancelled:
                await EmitCommittedMessageAsync(update.Message.Id, emit, requestId).ConfigureAwait(false);
                await emit("chat.cancelled", new
                {
                    sessionId = update.Message.SessionId,
                    message = ToMessageDto(update.Message, artifactsForMessage),
                    runStatus = update.Status,
                }, requestId).ConfigureAwait(false);
                break;
            case GoAiAssistantUpdateKind.Failed:
                await EmitCommittedMessageAsync(update.Message.Id, emit, requestId).ConfigureAwait(false);
                await emit("chat.failed", new
                {
                    sessionId = update.Message.SessionId,
                    message = ToMessageDto(update.Message, artifactsForMessage),
                    error = update.Error,
                    runStatus = update.Status,
                }, requestId).ConfigureAwait(false);
                break;
        }
    }

    private static bool IsCancelCommand(string prompt) =>
        prompt.Trim(' ', '.', ',', '!', '?').Equals("abbrechen", StringComparison.OrdinalIgnoreCase);

    private async Task ListWorkflowsAsync(
        WebBridgeEnvelope envelope,
        Func<string, object, string?, Task> emit,
        CancellationToken cancellationToken)
    {
        var search = GetOptionalString(envelope.Payload, "search", 200);
        var items = await workflows.ListAsync(search, cancellationToken).ConfigureAwait(false);
        await emit("workflow.snapshot", new { workflows = items.Select(ToWorkflowDto) }, envelope.RequestId);
    }

    private async Task InsertWorkflowAsync(
        WebBridgeEnvelope envelope,
        Func<string, object, string?, Task> emit,
        CancellationToken cancellationToken)
    {
        if (goAi?.IsRunning == true)
        {
            throw new InvalidOperationException("Ein Workflow kann nicht während einer laufenden Antwort eingefügt werden.");
        }

        var workflowId = GetRequiredGuid(envelope.Payload, "workflowId");
        var workflow = await workflows.GetAsync(workflowId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Der Workflow wurde nicht gefunden.");
        var session = await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false);
        await chats.SelectWorkflowAsync(session.Id, null, cancellationToken).ConfigureAwait(false);
        await chats.AddMessageAsync(
            session.Id,
            ChatRole.Assistant,
            WorkflowChatFormatter.Format(workflow),
            MessageStatus.Completed,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await recentActivity.RecordAsync(
            $"Workflow „{workflow.Title}“ in AI-Sitzung „{session.Title}“ eingefügt",
            CancellationToken.None).ConfigureAwait(false);
        await emit("session.changed", await BuildSnapshotAsync(cancellationToken), envelope.RequestId);
    }

    private async Task CreateWorkflowAsync(
        WebBridgeEnvelope envelope,
        Func<string, object, string?, Task> emit,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var title = GetRequiredString(envelope.Payload, "title", 160);
        var workflow = new WorkflowDefinition(
            Guid.NewGuid(),
            CreateSlug(title),
            title,
            GetOptionalString(envelope.Payload, "description", 4_000) ?? string.Empty,
            GetOptionalString(envelope.Payload, "domain", 120) ?? string.Empty,
            GetOptionalString(envelope.Payload, "contextSummary", 20_000) ?? string.Empty,
            GetRequiredJsonString(envelope.Payload, "contentJson"),
            false,
            1,
            now,
            now,
            GetStringArray(envelope.Payload, "tags", 40, 80));
        await workflows.CreateAsync(workflow, cancellationToken).ConfigureAwait(false);
        await emit("workflow.changed", await BuildSnapshotAsync(cancellationToken), envelope.RequestId);
    }

    private async Task UpdateWorkflowAsync(
        WebBridgeEnvelope envelope,
        Func<string, object, string?, Task> emit,
        CancellationToken cancellationToken)
    {
        var id = GetRequiredGuid(envelope.Payload, "workflowId");
        var existing = await workflows.GetAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Der Workflow wurde nicht gefunden.");
        if (existing.IsBuiltIn)
        {
            throw new InvalidOperationException("Integrierte Workflows sind schreibgeschützt.");
        }

        var updated = existing with
        {
            Title = GetRequiredString(envelope.Payload, "title", 160),
            Description = GetOptionalString(envelope.Payload, "description", 4_000) ?? string.Empty,
            Domain = GetOptionalString(envelope.Payload, "domain", 120) ?? string.Empty,
            ContextSummary = GetOptionalString(envelope.Payload, "contextSummary", 20_000) ?? string.Empty,
            ContentJson = GetRequiredJsonString(envelope.Payload, "contentJson"),
            Tags = GetStringArray(envelope.Payload, "tags", 40, 80),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await workflows.UpdateAsync(
            updated,
            GetRequiredInt64(envelope.Payload, "revision"),
            cancellationToken).ConfigureAwait(false);
        await emit("workflow.changed", await BuildSnapshotAsync(cancellationToken), envelope.RequestId);
    }

    private async Task CreateWorkflowFromMessageAsync(
        WebBridgeEnvelope envelope,
        Func<string, object, string?, Task> emit,
        CancellationToken cancellationToken)
    {
        var messageId = GetRequiredGuid(envelope.Payload, "messageId");
        var session = await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false);
        var message = (await chats.ListMessagesAsync(session.Id, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(item => item.Id == messageId)
            ?? throw new InvalidOperationException("Die Nachricht wurde nicht gefunden.");
        var title = GeneralAgentResponseParser.CreateWorkflowTitle(message.Content);
        var contextSummary = string.IsNullOrWhiteSpace(message.ContextSummary)
            ? GeneralAgentResponseParser.CreateContextSummary(null, message.Content)
            : GeneralAgentResponseParser.CreateContextSummary(message.ContextSummary, message.Content);
        var contentJson = JsonSerializer.Serialize(new
        {
            schema = "go.general.workflow.v1",
            blocks = new[] { new { type = "markdown", content = message.Content } },
        });
        await emit("workflow.draft", new
        {
            workflow = new
            {
                id = (Guid?)null,
                revision = 0,
                title,
                description = "Aus einer AI-Nachricht erstellt.",
                domain = "Allgemein",
                contextSummary,
                contentJson,
                isBuiltIn = false,
                tags = Array.Empty<string>(),
            },
        }, envelope.RequestId);
    }

    private static object ToSessionDto(ChatSession session) => new
    {
        id = session.Id,
        session.Title,
        session.CreatedAt,
        session.UpdatedAt,
        persistentToolAction = PersistentToolActionName(session.PersistentToolAction),
        sessionGroupId = session.SessionGroupId,
        session.IsPinned,
        session.PinnedAt,
        session.ConversationRevision,
    };

    private static object ToMessageDto(ChatMessage message, IReadOnlyList<ChatArtifact>? messageArtifacts = null)
    {
        return new
        {
            id = message.Id,
            sessionId = message.SessionId,
            role = message.Role.ToString().ToLowerInvariant(),
            message.Content,
            status = message.Status.ToString().ToLowerInvariant(),
            message.CreatedAt,
            message.UpdatedAt,
            message.Error,
            message.ContextSummary,
            contentProfile = message.ContentProfile.ToString().ToLowerInvariant(),
            message.Revision,
            tool = message.ToolExecution,
            toolSteps = message.ToolSteps ?? [],
            artifacts = (messageArtifacts ?? []).Select(ToArtifactDto),
        };
    }

    private static object ToArtifactDto(ChatArtifact artifact) => new
    {
        id = artifact.Id,
        artifact.FileName,
        contentType = artifact.ContentType,
        artifact.Length,
        artifact.Provider,
        artifact.CreatedAt,
        url = $"https://{AssistantWebBridge.VirtualHost}/artifacts/{artifact.Id:D}",
        downloadUrl = $"https://{AssistantWebBridge.VirtualHost}/artifacts/{artifact.Id:D}?download=1",
        artifact.Metadata,
    };

    private static object ToWorkflowDto(WorkflowDefinition workflow) => new
    {
        id = workflow.Id,
        workflow.Slug,
        workflow.Title,
        workflow.Description,
        workflow.Domain,
        workflow.ContextSummary,
        workflow.ContentJson,
        schema = GetWorkflowSchema(workflow.ContentJson),
        workflow.IsBuiltIn,
        workflow.Revision,
        tags = workflow.EffectiveTags,
    };

    private static string? GetWorkflowSchema(string contentJson)
    {
        try
        {
            using var document = JsonDocument.Parse(contentJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("schema", out var schema)
                && schema.ValueKind == JsonValueKind.String
                ? schema.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static object ToDocumentDto(StoredDocument document) => new
    {
        id = document.Id,
        document.FileName,
        document.ContentType,
        document.Length,
        document.PageCount,
        document.CreatedAt,
        preparationStatus = document.PreparationStatus.ToString().ToLowerInvariant(),
        preparationProgress = document.PreparationProgress,
        cacheHit = document.WasReused,
        preparationError = document.PreparationError,
    };

    private static object BuildDocumentGroupStatus(IReadOnlyList<StoredDocument> documents, int readyAttachments)
    {
        var ready = documents.Count(static item => item.PreparationStatus == DocumentPreparationStatus.Ready) + readyAttachments;
        var failed = documents.Count(static item => item.PreparationStatus == DocumentPreparationStatus.Failed);
        var processing = documents.Count - (ready - readyAttachments) - failed;
        return new
        {
            total = documents.Count + readyAttachments,
            ready,
            processing,
            failed,
            status = failed > 0 ? "failed" : processing > 0 ? "processing" : "ready",
        };
    }

    private static object ToAttachmentDto(AssistantAttachment attachment) => new
    {
        id = attachment.Id,
        attachment.FileName,
        contentType = attachment.ContentType,
        attachment.Length,
        attachment.CreatedAt,
    };

    private static string CreateSlug(string title)
    {
        var normalized = title.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        var lastDash = false;
        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                lastDash = false;
            }
            else if (!lastDash && builder.Length > 0)
            {
                builder.Append('-');
                lastDash = true;
            }
        }

        var slug = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? $"workflow-{Guid.NewGuid():N}" : slug;
    }

    private static Guid GetRequiredGuid(JsonElement payload, string name)
    {
        return GetOptionalGuid(payload, name)
            ?? throw new InvalidOperationException($"'{name}' fehlt oder ist ungültig.");
    }

    private static Guid? GetOptionalGuid(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
               && Guid.TryParse(property.GetString(), out var result)
            ? result
            : null;
    }

    private static string GetRequiredString(JsonElement payload, string name, int maximumLength)
    {
        var value = GetOptionalString(payload, name, maximumLength);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"'{name}' darf nicht leer sein.")
            : value.Trim();
    }

    private static string? GetOptionalString(JsonElement payload, string name, int maximumLength)
    {
        if (!payload.TryGetProperty(name, out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"'{name}' muss Text sein.");
        }

        var value = property.GetString() ?? string.Empty;
        return value.Length <= maximumLength
            ? value
            : throw new InvalidOperationException($"'{name}' ist zu lang.");
    }

    private static long GetRequiredInt64(JsonElement payload, string name)
    {
        return payload.TryGetProperty(name, out var property) && property.TryGetInt64(out var value)
            ? value
            : throw new InvalidOperationException($"'{name}' fehlt oder ist ungültig.");
    }

    private static bool GetRequiredBoolean(JsonElement payload, string name)
    {
        return payload.TryGetProperty(name, out var property)
            && property.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? property.GetBoolean()
                : throw new InvalidOperationException($"'{name}' fehlt oder ist ungültig.");
    }

    private static string GetRequiredJsonString(JsonElement payload, string name)
    {
        var json = GetRequiredString(payload, name, 1_000_000);
        using var _ = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64,
        });
        return json;
    }

    private static string[] GetStringArray(
        JsonElement payload,
        string name,
        int maximumItems,
        int maximumItemLength)
    {
        if (!payload.TryGetProperty(name, out var property) || property.ValueKind is JsonValueKind.Null)
        {
            return Array.Empty<string>();
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"'{name}' muss eine Liste sein.");
        }

        var values = property.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()?.Trim() : null)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (values.Length > maximumItems || values.Any(item => item.Length > maximumItemLength))
        {
            throw new InvalidOperationException($"'{name}' überschreitet das Größenlimit.");
        }

        return values;
    }
}
