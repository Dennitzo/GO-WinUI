using GoAi.Contracts;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Chat;
using GoWinUI.Core.Coding;
using GoWinUI.Core.Extensions;
using GoWinUI.App.Services.Extensions;
using GoWinUI.Core.Memory;
using GoWinUI.Core.Research;
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
    MicrophoneTranscriptionService? microphone = null,
    AssistantRuntimeProfile? runtimeProfile = null,
    IExtensionActionCatalog? extensionActions = null,
    IExtensionRuntimeService? extensionRuntime = null,
    IProjectMemoryStore? projectMemory = null,
    IAssistantRunScheduler? runScheduler = null,
    IScientificResearchRepository? scientificResearch = null,
    IScientificResearchExportService? scientificResearchExports = null)
{
    private const string DefaultSessionTitle = "Neue Sitzung";
    private const string DefaultSystemPrompt = "Du bist ein allgemeiner lokaler AI-Assistent. Unterstütze die konkrete Aufgabe des Nutzers, etwa beim Programmieren, Schreiben, Lernen, Analysieren oder Planen. Passe Sprache, Detailtiefe und Vorgehen an die Frage an. Unterscheide belegte Informationen von Annahmen, benenne relevante Unsicherheiten und erfinde keine Fakten, Quellen oder Ergebnisse.";
    private static readonly HashSet<string> GenericSessionTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Neue Sitzung", "Neuer Chat", "Hallo", "Antwort", "Frage", "Allgemeiner Chat",
        "Willkommen", "Workflow", "GO Assistent", "GO-Assistent", "Gespräch mit GO",
    };

    private static string? DerivePromptSessionTitle(string prompt)
    {
        var value = (prompt ?? string.Empty)
            .Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("__", string.Empty, StringComparison.Ordinal)
            .ReplaceLineEndings(" ")
            .Trim(' ', '\t', '`', '"', '\'', '.', '!', '?', ';', ':', ',', '-', '–', '—')
            .TrimStart('#');
        foreach (var prefix in new[] { "Kannst du bitte ", "Kannst du ", "Könntest du bitte ", "Könntest du ", "Bitte " })
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                value = value[prefix.Length..];
                break;
            }
        }
        value = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 6) value = string.Join(' ', words.Take(6));
        if (value.Length > 64) value = value[..64].TrimEnd();
        value = value.Trim();
        if (value.Length == 0 || GenericSessionTitles.Contains(value)) return null;
        return value;
    }
    private int _startupRunsHandled;
    private Task? _resumeTask;
    private readonly object _resumeLock = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, AssistantDisplayState> _displayStates = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Task> _scheduledRunObservers = new();
    private readonly IExtensionActionCatalog _extensionActions = extensionActions ?? ExtensionActionCatalog.CreateWithBuiltIns();
    private readonly IAssistantRunScheduler? _runScheduler = runScheduler;

    internal sealed record AssistantDisplayState(Guid MessageId, string? ModelSelection, bool IsCoding,
        bool IsRunning, string? Status = null, string? Detail = null, string? Model = null,
        int? ContextUsed = null, int? ContextLimit = null, int? LoadedFiles = null, bool ContextWasCompacted = false);

    private async Task ObserveDisplayStateAsync(GoAiAssistantUpdate update)
    {
        if (update.Kind is not (GoAiAssistantUpdateKind.Started or GoAiAssistantUpdateKind.Status
            or GoAiAssistantUpdateKind.Completed or GoAiAssistantUpdateKind.Cancelled or GoAiAssistantUpdateKind.Failed)) return;
        var isCoding = _displayStates.TryGetValue(update.Message.SessionId, out var previous)
            && previous.MessageId == update.Message.Id ? previous.IsCoding
                : IsCodingAgentSession((await chats.GetSessionAsync(update.Message.SessionId, CancellationToken.None).ConfigureAwait(false))?.ChatMode);
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
        EnsureContextCanChange(sessionId);
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException("Der Workspace-Ordner existiert nicht.");
        var session = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die ausgewählte Sitzung wurde nicht gefunden.");
        await CodingWorkspaceGit.EnsureRepositoryAsync(fullPath, cancellationToken).ConfigureAwait(false);
        // A workspace is session context in both modes, not a tool selection.
        await chats.SetCodingWorkspacePathAsync(session.Id, fullPath, activateCoding: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> HasAudiobookVoiceContextAsync(CancellationToken cancellationToken = default)
    {
        var session = await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false);
        return string.Equals(
                session.PersistentExtensionActionId,
                BuiltInActionIds.CreateAudiobook,
                StringComparison.Ordinal)
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
        var explicitExtensionActionId = GetExplicitExtensionActionId(payload);
        var match = await ResolvePromptMatchAsync(
            session,
            prompt,
            explicitExtensionActionId,
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
        var explicitExtensionActionId = GetExplicitExtensionActionId(payload);
        var match = await ResolvePromptMatchAsync(
            session,
            prompt,
            explicitExtensionActionId,
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

    public async Task CancelCurrentAsync(Guid? sessionId = null)
    {
        if (_runScheduler is not null)
        {
            var snapshot = _runScheduler.Snapshot;
            if (snapshot.Active is { } active
                && (!sessionId.HasValue || active.SessionId == sessionId.Value))
            {
                _runScheduler.TryCancel(active.TicketId);
            }
            else
            {
                var queued = snapshot.Pending.FirstOrDefault(item => !sessionId.HasValue || item.SessionId == sessionId.Value);
                if (queued is not null && _runScheduler.TryCancel(queued.TicketId))
                {
                    return;
                }
                if (sessionId.HasValue)
                {
                    return;
                }
            }
        }

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
        var selectedChatMode = session.ChatMode;
        var sessions = await chats.ListSessionsAsync(selectedChatMode, cancellationToken: cancellationToken).ConfigureAwait(false);
        var messages = conversation.Messages;
        var artifactItems = conversation.Artifacts;
        var workflowItems = await workflows.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var documentItems = await documents.ListAsync(session.Id, cancellationToken).ConfigureAwait(false);
        var attachmentItems = await attachments.ListAsync(session.Id, cancellationToken).ConfigureAwait(false);
        var sessionGroups = await chats.ListSessionGroupsAsync(selectedChatMode, cancellationToken).ConfigureAwait(false);
        var extensionWorkspaceRoot = ResolveSessionWorkspaceRoot(session, sessionGroups);
        var documentGroupStatus = BuildDocumentGroupStatus(documentItems, attachmentItems.Count);
        // A snapshot is local UI state. Never make sidebar/session interaction wait for
        // the native model runtime, which may be offline or loading a model.
        var isCodingSession = IsCodingAgentSession(session.ChatMode);
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
        var runQueue = _runScheduler?.Snapshot;
        return new
        {
            sessionGroups = sessionGroups.Select(group => new
            {
                group.Id,
                group.Name,
                group.IsCollapsed,
                group.CreatedAt,
                group.WorkspacePath,
                chatMode = ChatModeName(group.ChatMode),
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
            actionDescriptors = BuildActionDescriptors(selectedChatMode, messages.Count, extensionWorkspaceRoot),
            documentGroupStatus,
            activeSessionId = session.Id,
            productName = runtimeProfile?.ProductName ?? "AI Assistent",
            assistantTitle = runtimeProfile?.ProductName ?? "AI Assistent",
            productIdentity = new { assistantTitle = runtimeProfile?.ProductName ?? "AI Assistent" },
            bridgeContract = AssistantWebBridge.BuildProtocolContract(),
            scienceCapabilities = new
            {
                researchProjects = scientificResearch is not null,
                export = scientificResearchExports is not null,
                graph = scientificResearch is not null,
                literature = scientificResearch is not null,
                experiments = scientificResearch is not null,
                manuscript = scientificResearch is not null,
                review = scientificResearch is not null,
                provenance = scientificResearch is not null,
            },
            chatMode = ChatModeName(selectedChatMode),
            selectedChatMode = ChatModeName(selectedChatMode),
            draft = session.Draft,
            isRunning = isSessionRunning,
            isAiBusy = settings.Current.IsAiConnectionEnabled && (goAi?.IsRunning == true
                || _displayStates.Values.Any(item => item.IsRunning)
                || runQueue?.IsIdle == false),
            activeRunSessionId = goAi?.ActiveSessionId ?? runQueue?.Active?.SessionId,
            activeRunId = goAi?.ActiveRunId,
            runQueue = runQueue is null ? null : ToRunQueueDto(runQueue),
            runMessageId = isSessionRunning ? display?.MessageId : null,
            runStatus = isSessionRunning ? display?.Status ?? "Denkt nach" : null,
            runDetail = isSessionRunning ? display?.Detail : null,
            loadedFiles = display?.LoadedFiles,
            model = display?.Model ?? (isCodingSession ? selectedModel ?? "Lokaler AI-Server" : "Lokaler AI-Server"),
            provider = settings.Current.AiProvider.ToString(),
            contextUsed = display?.ContextUsed ?? context.EstimatedTokens,
            contextLimit = display?.ContextLimit ?? contextLimit,
            contextWasTruncated = display?.ContextUsed is not null ? display.ContextWasCompacted : context.WasTruncated,
            contextNotice = display?.ContextUsed is not null ? null : context.TruncationNotice,
            contextSource = display?.ContextUsed is not null ? "measured" : "estimated",
            contextMessageId = display?.ContextUsed is not null ? display.MessageId : (Guid?)null,
            selectedExtensionActionId = session.PersistentExtensionActionId,
            selectedToolAction = LegacyToolAliasFor(session.PersistentExtensionActionId),
            reasoningModelId = selectedModel,
            reasoningRole = isCodingSession ? "coding" : "general",
            workspacePath = session.CodingWorkspacePath,
            // Read alias for WebViews from the transition build.
            codingWorkspacePath = session.CodingWorkspacePath,
            codingWorkspaceRequired = isCodingSession
                && (string.IsNullOrWhiteSpace(session.CodingWorkspacePath)
                    || !Directory.Exists(session.CodingWorkspacePath)),
            codingToolStepsExpanded = settings.Current.CodingToolStepsExpanded,
            changesSummary = goAi is null ? null : await goAi.GetChangesSummaryAsync(session.Id, messages, cancellationToken).ConfigureAwait(false),
            isSessionPaneOpen = settings.Current.IsAssistantSessionPaneOpen,
        };
    }

    internal object BuildRunQueueSnapshot() => _runScheduler is null
        ? new { active = (object?)null, pending = Array.Empty<object>(), queueDepth = 0, isIdle = true }
        : ToRunQueueDto(_runScheduler.Snapshot);

    private object[] BuildActionDescriptors(ChatMode chatMode, int messageCount, string? workspaceRoot)
    {
        var extensionMode = chatMode switch
        {
            ChatMode.General => ExtensionChatMode.General,
            ChatMode.Coding => ExtensionChatMode.Coding,
            ChatMode.ClaudeScience => ExtensionChatMode.ClaudeScience,
            _ => throw new InvalidOperationException("Der Chatmodus ist ungültig."),
        };
        return _extensionActions.GetActions(extensionMode)
            // Claude Science owns Deep Research as part of its workbench. The shared
            // menu still exposes attachments, media, documents, export, speech and
            // workflows, but must not add a second Research activation switch.
            .Where(action => chatMode != ChatMode.ClaudeScience
                || !string.Equals(action.ActionId, BuiltInActionIds.DeepResearch, StringComparison.Ordinal))
            .Select(action =>
        {
            var disabledReason = action.DisabledReason;
            if (string.Equals(action.ActionId, BuiltInActionIds.ExportChatPdf, StringComparison.Ordinal)
                && messageCount == 0)
                disabledReason = "Der Chat enthält noch keine Nachrichten.";
            if (!action.ActionId.StartsWith("builtin.", StringComparison.Ordinal))
                disabledReason ??= extensionRuntime?.GetDisabledReason(action.ActionId, workspaceRoot)
                    ?? "Der ExtensionHost ist für diese Aktion nicht verfügbar.";
            var groupLabel = BuiltInExtensionCatalog.GroupLabels.TryGetValue(action.GroupId, out var label)
                ? label
                : "Erweiterungen";
            return (object)new
            {
                action.ActionId,
                action.DisplayName,
                action.Description,
                action.IconKey,
                action.GroupId,
                groupLabel,
                action.GroupOrder,
                action.ItemOrder,
                supportedChatModes = action.SupportedChatModes.Select(static mode => mode switch
                {
                    ExtensionChatMode.General => "general",
                    ExtensionChatMode.Coding => "coding",
                    ExtensionChatMode.ClaudeScience => "claudescience",
                    _ => throw new InvalidOperationException("Der Extension-Chatmodus ist ungültig."),
                }),
                actionKind = action.ActionKind switch
                {
                    ExtensionActionKind.Immediate => "immediate",
                    ExtensionActionKind.SelectableTool => "selectableTool",
                    _ => throw new InvalidOperationException("Die Extension-Aktionsart ist ungültig."),
                },
                selectionBehavior = action.SelectionBehavior switch
                {
                    ExtensionSelectionBehavior.None => "none",
                    ExtensionSelectionBehavior.Toggle => "toggle",
                    _ => throw new InvalidOperationException("Das Extension-Auswahlverhalten ist ungültig."),
                },
                disabledReason,
            };
        }).ToArray();
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
            chatMode = ChatModeName(conversation.Session.ChatMode),
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
            {
                var requestedMode = GetOptionalString(envelope.Payload, "chatMode", 16)
                    ?? GetOptionalString(envelope.Payload, "mode", 16);
                await CreateSessionAsync(
                    requestedMode is null ? null : ParseChatMode(requestedMode),
                    emit,
                    envelope.RequestId,
                    cancellationToken);
                break;
            }
            case "mode.switch":
                await SwitchModeAsync(
                    ParseChatMode(
                        GetOptionalString(envelope.Payload, "chatMode", 16)
                        ?? GetOptionalString(envelope.Payload, "mode", 16)),
                    emit,
                    envelope.RequestId,
                    cancellationToken).ConfigureAwait(false);
                break;
            case "session.projectCreate":
                await CreateWorkspaceSessionAsync(
                    GetRequiredString(envelope.Payload, "workspacePath", 32_768),
                    ParseChatMode(
                        GetOptionalString(envelope.Payload, "chatMode", 16)
                        ?? GetOptionalString(envelope.Payload, "mode", 16)
                        ?? ChatModeName(settings.Current.SelectedChatMode)),
                    emit,
                    envelope.RequestId,
                    cancellationToken).ConfigureAwait(false);
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
            {
                // The confirmation dialog belongs to the mode that was visible
                // when the user opened it. Capture that mode from the request so
                // a concurrent mode switch cannot redirect the destructive action.
                var requestedMode = GetOptionalString(envelope.Payload, "chatMode", 16)
                    ?? GetOptionalString(envelope.Payload, "mode", 16);
                var mode = requestedMode is null
                    ? settings.Current.SelectedChatMode
                    : ParseChatMode(requestedMode);
                await ClearSessionsAsync(mode, emit, envelope.RequestId, cancellationToken);
                break;
            }
            case "session.draft":
                await chats.SaveDraftAsync(
                    GetRequiredGuid(envelope.Payload, "sessionId"),
                    GetOptionalString(envelope.Payload, "draft", 100_000) ?? string.Empty,
                    cancellationToken);
                await emit("draft.saved", new { }, envelope.RequestId);
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
            case "action.invoke":
                await InvokeActionAsync(envelope, emit, cancellationToken).ConfigureAwait(false);
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
                await CancelCurrentAsync(GetOptionalGuid(envelope.Payload, "sessionId")).ConfigureAwait(false);
                if (_runScheduler is not null)
                {
                    await emit(
                        "queue.changed",
                        new { runQueue = ToRunQueueDto(_runScheduler.Snapshot) },
                        envelope.RequestId).ConfigureAwait(false);
                }
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
            case "memory.list":
                await EmitProjectMemoryAsync(envelope, emit, "memory.snapshot", cancellationToken).ConfigureAwait(false);
                break;
            case "memory.create":
                await CreateProjectMemoryAsync(envelope, emit, cancellationToken).ConfigureAwait(false);
                break;
            case "memory.update":
                await UpdateProjectMemoryAsync(envelope, emit, cancellationToken).ConfigureAwait(false);
                break;
            case "memory.pin":
                await PinProjectMemoryAsync(envelope, emit, cancellationToken).ConfigureAwait(false);
                break;
            case "memory.confirm":
                await ConfirmProjectMemoryAsync(envelope, emit, cancellationToken).ConfigureAwait(false);
                break;
            case "memory.delete":
                await DeleteProjectMemoryAsync(envelope, emit, cancellationToken).ConfigureAwait(false);
                break;
            case "memory.autoCapture":
                await SetProjectMemoryAutoCaptureAsync(envelope, emit, cancellationToken).ConfigureAwait(false);
                break;
            case "research.list":
                await EmitScientificResearchAsync(envelope, emit, selectedProjectId: null, cancellationToken).ConfigureAwait(false);
                break;
            case "research.open":
                await EmitScientificResearchAsync(envelope, emit,
                    GetRequiredString(envelope.Payload, "projectId", 128), cancellationToken).ConfigureAwait(false);
                break;
            case "research.export":
                await ExportScientificResearchAsync(envelope, emit, cancellationToken).ConfigureAwait(false);
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
        EnsureContextCanChange(sessionId);
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
        EnsureContextCanChange(sessionId);
        _ = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die Sitzung wurde nicht gefunden.");
        _ = await attachments.ImportAsync(sessionId, fileName, contentType, content, cancellationToken).ConfigureAwait(false);
        var session = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await recentActivity.RecordAsync(
            $"Datei „{fileName}“ zur AI-Sitzung „{session?.Title ?? DefaultSessionTitle}“ hinzugefügt",
            CancellationToken.None).ConfigureAwait(false);
    }

    private void EnsureContextCanChange(Guid? targetSessionId = null)
    {
        var selectedSessionId = targetSessionId
            ?? ActiveSessionIdFor(settings.Current, settings.Current.SelectedChatMode)
            ?? settings.Current.ActiveSessionId;
        var queue = _runScheduler?.Snapshot;
        var sessionIsBusy = selectedSessionId.HasValue && (
            goAi?.ActiveSessionId == selectedSessionId.Value
            || queue?.Active?.SessionId == selectedSessionId.Value
            || queue?.Pending.Any(item => item.SessionId == selectedSessionId.Value) == true);
        if (sessionIsBusy)
        {
            throw new InvalidOperationException("Anhänge und Dokumente können während eines laufenden AI-Auftrags nicht geändert werden.");
        }
    }

    private async Task<ChatSession> EnsureActiveSessionAsync(CancellationToken cancellationToken)
    {
        // One-time settings migration: older builds persisted only one active ID.
        // Resolve its durable mode before choosing the surface to restore.
        if (settings.Current.ActiveGeneralSessionId is null
            && settings.Current.ActiveCodingSessionId is null
            && settings.Current.ActiveSessionId is { } legacyId
            && await chats.GetSessionAsync(legacyId, cancellationToken).ConfigureAwait(false) is { } legacy)
        {
            await ActivateSessionAsync(legacy, cancellationToken).ConfigureAwait(false);
        }

        var mode = settings.Current.SelectedChatMode;
        var activeId = ActiveSessionIdFor(settings.Current, mode);
        if (activeId is { } selectedId
            && await chats.GetSessionAsync(selectedId, cancellationToken).ConfigureAwait(false) is { } active
            && active.ChatMode == mode)
        {
            if (settings.Current.ActiveSessionId != active.Id)
            {
                await ActivateSessionAsync(active, cancellationToken).ConfigureAwait(false);
            }
            return active;
        }

        var existing = await chats.ListSessionsAsync(mode, cancellationToken: cancellationToken).ConfigureAwait(false);
        var session = existing.Count > 0
            ? existing[0]
            : await chats.CreateSessionAsync(DefaultSessionTitle, mode, cancellationToken).ConfigureAwait(false);
        await ActivateSessionAsync(session, cancellationToken).ConfigureAwait(false);
        return session;
    }

    private Task ActivateSessionAsync(ChatSession session, CancellationToken cancellationToken) =>
        settings.UpdateAsync(current => session.ChatMode switch
        {
            ChatMode.General => current with
            {
                SelectedChatMode = ChatMode.General,
                ActiveGeneralSessionId = session.Id,
                ActiveSessionId = session.Id,
            },
            ChatMode.Coding => current with
            {
                SelectedChatMode = ChatMode.Coding,
                ActiveCodingSessionId = session.Id,
                ActiveSessionId = session.Id,
            },
            ChatMode.ClaudeScience => current with
            {
                SelectedChatMode = ChatMode.ClaudeScience,
                ActiveClaudeScienceSessionId = session.Id,
                ActiveSessionId = session.Id,
            },
            _ => throw new InvalidOperationException("Die Sitzung enthält einen unbekannten Chatmodus."),
        }, cancellationToken);

    private static Guid? ActiveSessionIdFor(AppSettings settingsValue, ChatMode mode) => mode switch
    {
        ChatMode.General => settingsValue.ActiveGeneralSessionId,
        ChatMode.Coding => settingsValue.ActiveCodingSessionId,
        ChatMode.ClaudeScience => settingsValue.ActiveClaudeScienceSessionId,
        _ => throw new InvalidOperationException("Der Chatmodus ist ungültig."),
    };

    private async Task SwitchModeAsync(
        ChatMode mode,
        Func<string, object, string?, Task> emit,
        string requestId,
        CancellationToken cancellationToken)
    {
        await settings.UpdateAsync(current => current with
        {
            SelectedChatMode = mode,
            ActiveSessionId = ActiveSessionIdFor(current, mode),
        }, cancellationToken).ConfigureAwait(false);
        _ = await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false);
        await emit("session.changed", await BuildSnapshotAsync(cancellationToken), requestId).ConfigureAwait(false);
    }

    private async Task CreateSessionAsync(
        ChatMode? requestedMode,
        Func<string, object, string?, Task> emit,
        string requestId,
        CancellationToken cancellationToken)
    {
        var session = await chats.CreateSessionAsync(
            DefaultSessionTitle,
            requestedMode ?? settings.Current.SelectedChatMode,
            cancellationToken).ConfigureAwait(false);
        await ActivateSessionAsync(session, cancellationToken).ConfigureAwait(false);
        await recentActivity.RecordAsync(
            $"AI-Sitzung „{session.Title}“ erstellt",
            CancellationToken.None).ConfigureAwait(false);
        await emit("session.changed", await BuildSnapshotAsync(cancellationToken), requestId);
    }

    public async Task CreateWorkspaceSessionAsync(
        string workspacePath,
        Func<string, object, string?, Task> emit,
        string requestId,
        CancellationToken cancellationToken) =>
        await CreateWorkspaceSessionAsync(
            workspacePath,
            settings.Current.SelectedChatMode,
            emit,
            requestId,
            cancellationToken).ConfigureAwait(false);

    public async Task CreateWorkspaceSessionAsync(
        string workspacePath,
        ChatMode chatMode,
        Func<string, object, string?, Task> emit,
        string requestId,
        CancellationToken cancellationToken)
    {
        EnsureContextCanChange();
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        if (!Enum.IsDefined(chatMode)) throw new ArgumentOutOfRangeException(nameof(chatMode));
        if (chatMode == ChatMode.ClaudeScience)
            throw new InvalidOperationException("Claude-Science-Projekte verwenden eine verwaltete Forschungssandbox und keine Coding-Workspace-Zuordnung.");
        var fullPath = Path.GetFullPath(workspacePath.Trim());
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException("Der Projektordner existiert nicht.");
        var session = await chats.CreateSessionAsync(DefaultSessionTitle, chatMode, cancellationToken).ConfigureAwait(false);
        try
        {
            // A workspace is shared session context in both modes. Assigning it
            // never changes the immutable mode selected by the user.
            await chats.SetCodingWorkspacePathAsync(session.Id, fullPath, activateCoding: false, cancellationToken).ConfigureAwait(false);
            session = await chats.GetSessionAsync(session.Id, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Die neue Workspace-Sitzung wurde nicht gespeichert.");
            if (session.SessionGroupId is not { } groupId)
                throw new InvalidOperationException("Die neue Workspace-Sitzung besitzt keine Projektgruppe.");
            await chats.SetSessionGroupCollapsedAsync(groupId, false, cancellationToken).ConfigureAwait(false);
            await ActivateSessionAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await chats.DeleteSessionAsync(session.Id, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        await recentActivity.RecordAsync(
            $"Neue Sitzung im Projekt \"{WorkspaceLabel(fullPath)}\" gestartet",
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
        var extensionActionId = requestedAction?.Trim() switch
        {
            null or "" => null,
            "audiobook" => BuiltInActionIds.CreateAudiobook,
            "planMode" => BuiltInActionIds.PlanMode,
            _ => throw new InvalidOperationException("Die angeforderte persistente Tool-Aktion ist unbekannt."),
        };
        var session = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die ausgewählte Tool-Sitzung wurde nicht gefunden.");
        await chats.SetPersistentExtensionActionIdAsync(session.Id, extensionActionId, cancellationToken).ConfigureAwait(false);
        await emit("session.changed", await BuildSnapshotAsync(cancellationToken), requestId).ConfigureAwait(false);
    }

    private async Task InvokeActionAsync(
        WebBridgeEnvelope envelope,
        Func<string, object, string?, Task> emit,
        CancellationToken cancellationToken)
    {
        var actionId = GetRequiredString(envelope.Payload, "actionId", ExtensionIdentifiers.MaximumIdentifierLength);
        if (!_extensionActions.TryGetAction(actionId, out var descriptor) || descriptor is null)
            throw new InvalidOperationException($"Die Erweiterungsaktion '{actionId}' ist nicht registriert.");
        var sessionId = GetOptionalGuid(envelope.Payload, "sessionId")
            ?? (await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false)).Id;
        var session = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die Sitzung der Erweiterungsaktion wurde nicht gefunden.");
        var extensionMode = ToExtensionChatMode(session.ChatMode);
        if (!descriptor.SupportedChatModes.Contains(extensionMode))
            throw new InvalidOperationException("Die Erweiterungsaktion unterstützt den aktuellen Chatmodus nicht.");
        if (!string.IsNullOrWhiteSpace(descriptor.DisabledReason))
            throw new InvalidOperationException(descriptor.DisabledReason);
        var enabled = true;
        if (envelope.Payload.TryGetProperty("enabled", out var enabledElement))
        {
            if (enabledElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidOperationException("'enabled' muss ein boolescher Wert sein.");
            enabled = enabledElement.GetBoolean();
        }
        if (descriptor.ActionKind == ExtensionActionKind.SelectableTool) EnsureContextCanChange(sessionId);
        if (!actionId.StartsWith("builtin.", StringComparison.Ordinal))
        {
            if (extensionRuntime is null)
                throw new InvalidOperationException("Der ExtensionHost ist für Drittanbieter-Aktionen nicht verfügbar.");
            var groups = await chats.ListSessionGroupsAsync(session.ChatMode, cancellationToken).ConfigureAwait(false);
            var workspaceRoot = ResolveSessionWorkspaceRoot(session, groups);
            var disabledReason = extensionRuntime.GetDisabledReason(actionId, workspaceRoot);
            if (enabled && disabledReason is not null) throw new InvalidOperationException(disabledReason);
            if (descriptor.ActionKind == ExtensionActionKind.SelectableTool)
            {
                await emit("action.completed", new { actionId, enabled }, envelope.RequestId).ConfigureAwait(false);
                return;
            }
            var arguments = envelope.Payload.TryGetProperty("arguments", out var argumentsElement)
                ? argumentsElement
                : JsonSerializer.SerializeToElement(new { });
            var result = await extensionRuntime.InvokeAsync(
                actionId, arguments, workspaceRoot, cancellationToken).ConfigureAwait(false);
            await emit("action.completed", new { actionId, result }, envelope.RequestId).ConfigureAwait(false);
            return;
        }

        if (string.Equals(actionId, BuiltInActionIds.DeepResearch, StringComparison.Ordinal))
        {
            await emit("action.completed", new
            {
                actionId,
                toolToggle = "deepResearch",
                enabled,
            }, envelope.RequestId).ConfigureAwait(false);
            return;
        }

        if (string.Equals(actionId, BuiltInActionIds.OpenMemoryLibrary, StringComparison.Ordinal))
        {
            await emit("action.completed", new { actionId }, envelope.RequestId).ConfigureAwait(false);
            return;
        }

        if (!PromptActionExtensionIds.TryGetPromptAction(actionId, out var promptAction)
            || promptAction is PromptTriggerAction.Coding or PromptTriggerAction.LiveCaptions)
            throw new InvalidOperationException($"Die integrierte Aktion '{actionId}' muss vom nativen UI-Host ausgeführt werden.");

        var toolAction = PromptToolActionName(promptAction);
        if (promptAction is PromptTriggerAction.Audiobook or PromptTriggerAction.PlanMode)
        {
            await SetSessionToolAsync(
                session.Id,
                enabled ? toolAction : null,
                emit,
                envelope.RequestId,
                cancellationToken).ConfigureAwait(false);
        }
        await emit("action.completed", new
        {
            actionId,
            toolAction,
            enabled,
            captureAction = enabled && promptAction is (PromptTriggerAction.AudioAnalysis
                or PromptTriggerAction.VideoAnalysis
                or PromptTriggerAction.ImageAnalysis)
                    ? toolAction
                    : null,
        }, envelope.RequestId).ConfigureAwait(false);
    }

    private static string PromptToolActionName(PromptTriggerAction action) => action switch
    {
        PromptTriggerAction.AudioAnalysis => "audioAnalysis",
        PromptTriggerAction.VideoAnalysis => "videoAnalysis",
        PromptTriggerAction.ImageAnalysis => "imageAnalysis",
        PromptTriggerAction.ImageGeneration => "imageGeneration",
        PromptTriggerAction.Audiobook => "audiobook",
        PromptTriggerAction.DocumentCreate => "documentCreate",
        PromptTriggerAction.TextToSpeech => "textToSpeech",
        PromptTriggerAction.Translation => "translation",
        PromptTriggerAction.WebSearch => "webSearch",
        PromptTriggerAction.PlanMode => "planMode",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Die Promptaktion ist nicht über das Werkzeugmenü auswählbar."),
    };

    private static string? GetExplicitExtensionActionId(JsonElement payload)
    {
        var extensionActionId = GetOptionalString(
            payload,
            "extensionActionId",
            ExtensionIdentifiers.MaximumIdentifierLength);
        if (!string.IsNullOrWhiteSpace(extensionActionId))
        {
            PromptActionExtensionIds.EnsureValid(extensionActionId);
            return extensionActionId;
        }

        // Read-only compatibility for pre-extension WebView assets and persisted retries.
        var legacyToolAction = GetOptionalString(payload, "toolAction", 40);
        return string.IsNullOrWhiteSpace(legacyToolAction)
            ? null
            : LegacyExtensionActionIdFor(legacyToolAction);
    }

    private static string LegacyExtensionActionIdFor(string toolAction) => toolAction switch
    {
        "audioAnalysis" => BuiltInActionIds.AudioAnalysis,
        "imageAnalysis" => BuiltInActionIds.ImageAnalysis,
        "imageGeneration" => BuiltInActionIds.GenerateImage,
        "audiobook" => BuiltInActionIds.CreateAudiobook,
        "coding" => PromptActionExtensionIds.CodingRun,
        "documentCreate" => BuiltInActionIds.CreateDocument,
        "textToSpeech" => BuiltInActionIds.ReadAloud,
        "translation" => BuiltInActionIds.Translate,
        "videoAnalysis" => BuiltInActionIds.VideoAnalysis,
        "webSearch" => BuiltInActionIds.WebSearch,
        _ => throw new ArgumentException("Die ausgewählte Tool-Aktion ist nicht bekannt.", nameof(toolAction)),
    };

    private async Task OpenSessionAsync(
        Guid sessionId,
        Func<string, object, string?, Task> emit,
        string requestId,
        CancellationToken cancellationToken)
    {
        var session = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die Sitzung wurde nicht gefunden.");
        await ActivateSessionAsync(session, cancellationToken).ConfigureAwait(false);
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

    private async Task DeleteSessionAsync(
        Guid sessionId,
        Func<string, object, string?, Task> emit,
        string requestId,
        CancellationToken cancellationToken)
    {
        var session = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die Sitzung wurde nicht gefunden.");

        await CancelScheduledSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);

        await chats.DeleteSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        _displayStates.TryRemove(sessionId, out _);
        await CleanupDeletedCodingChangesAsync([sessionId]).ConfigureAwait(false);
        if (settings.Current.ActiveSessionId == sessionId
            || ActiveSessionIdFor(settings.Current, session.ChatMode) == sessionId)
        {
            await settings.UpdateAsync(current => session.ChatMode switch
            {
                ChatMode.General => current with
                {
                    ActiveGeneralSessionId = null,
                    ActiveSessionId = current.SelectedChatMode == ChatMode.General ? null : current.ActiveSessionId,
                },
                ChatMode.Coding => current with
                {
                    ActiveCodingSessionId = null,
                    ActiveSessionId = current.SelectedChatMode == ChatMode.Coding ? null : current.ActiveSessionId,
                },
                ChatMode.ClaudeScience => current with
                {
                    ActiveClaudeScienceSessionId = null,
                    ActiveSessionId = current.SelectedChatMode == ChatMode.ClaudeScience ? null : current.ActiveSessionId,
                },
                _ => throw new InvalidOperationException("Die Sitzung enthält einen unbekannten Chatmodus."),
            }, cancellationToken).ConfigureAwait(false);
        }

        await recentActivity.RecordAsync(
            $"AI-Sitzung „{session.Title}“ gelöscht",
            CancellationToken.None).ConfigureAwait(false);
        _ = await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false);
        await emit("session.changed", await BuildSnapshotAsync(cancellationToken), requestId);
    }

    private async Task CancelScheduledSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var ticketIds = Array.Empty<Guid>();
        if (_runScheduler is not null)
        {
            var snapshot = _runScheduler.Snapshot;
            ticketIds = snapshot.Pending
                .Where(item => item.SessionId == sessionId)
                .Select(static item => item.TicketId)
                .Concat(snapshot.Active is { } active && active.SessionId == sessionId
                    ? [active.TicketId]
                    : [])
                .Distinct()
                .ToArray();
            foreach (var ticketId in ticketIds)
            {
                _runScheduler.TryCancel(ticketId);
            }
        }

        if (goAi?.ActiveSessionId == sessionId)
        {
            await goAi.CancelCurrentAndWaitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        var observers = ticketIds
            .Select(ticketId => _scheduledRunObservers.TryGetValue(ticketId, out var observer) ? observer : null)
            .Where(static observer => observer is not null)
            .Cast<Task>()
            .ToArray();
        if (observers.Length > 0)
        {
            await Task.WhenAll(observers)
                .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task ClearSessionsAsync(
        ChatMode mode,
        Func<string, object, string?, Task> emit,
        string requestId,
        CancellationToken cancellationToken)
    {
        if (goAi?.IsRunning == true || _runScheduler?.Snapshot.IsIdle == false)
        {
            throw new InvalidOperationException("Die Sitzungen können während einer laufenden Antwort nicht gelöscht werden.");
        }

        var previousSessions = await chats.ListSessionsAsync(mode, cancellationToken: cancellationToken).ConfigureAwait(false);
        var deletedCount = await chats.DeleteSessionsAsync(mode, cancellationToken).ConfigureAwait(false);
        if (deletedCount > 0)
            await CleanupDeletedCodingChangesAsync(previousSessions.Select(static session => session.Id)).ConfigureAwait(false);

        await settings.UpdateAsync(current => mode switch
        {
            ChatMode.General => current with
            {
                ActiveGeneralSessionId = null,
                ActiveSessionId = current.SelectedChatMode == ChatMode.General ? null : current.ActiveSessionId,
            },
            ChatMode.Coding => current with
            {
                ActiveCodingSessionId = null,
                ActiveSessionId = current.SelectedChatMode == ChatMode.Coding ? null : current.ActiveSessionId,
            },
            ChatMode.ClaudeScience => current with
            {
                ActiveClaudeScienceSessionId = null,
                ActiveSessionId = current.SelectedChatMode == ChatMode.ClaudeScience ? null : current.ActiveSessionId,
            },
            _ => throw new InvalidOperationException("Der Chatmodus ist ungültig."),
        }, cancellationToken).ConfigureAwait(false);
        if (deletedCount > 0)
        {
            await recentActivity.RecordAsync(
                $"Alle AI-Sitzungen der Ansicht „{mode}“ gelöscht",
                CancellationToken.None).ConfigureAwait(false);
        }

        _ = await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false);
        await emit("session.changed", await BuildSnapshotAsync(cancellationToken), requestId);
    }

    private async Task CleanupDeletedCodingChangesAsync(IEnumerable<Guid> candidateSessionIds)
    {
        // Re-read after the committed deletion so only removed sessions lose
        // their workspace baselines and evidence bundles.
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

        if (_runScheduler is null)
        {
            await SendGoAiChatAsync(envelope, emit, cancellationToken).ConfigureAwait(false);
            return;
        }

        var sessionId = GetOptionalGuid(envelope.Payload, "sessionId")
            ?? (await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false)).Id;
        _ = GetRequiredString(envelope.Payload, "prompt", 100_000);
        var session = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die AI-Sitzung wurde nicht gefunden.");
        if (session.ChatMode == ChatMode.Coding
            && (string.IsNullOrWhiteSpace(session.CodingWorkspacePath)
                || !Directory.Exists(session.CodingWorkspacePath)))
        {
            throw new InvalidOperationException("Wähle für diese Coding-Sitzung zuerst ein vorhandenes Projekt oder einen Workspace aus.");
        }

        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ticket = _runScheduler.Enqueue<object?>(
            sessionId,
            envelope.RequestId,
            async scheduledCancellationToken =>
            {
                await accepted.Task.WaitAsync(scheduledCancellationToken).ConfigureAwait(false);
                await WaitForDetachedRunAsync(scheduledCancellationToken).ConfigureAwait(false);
                await TryEmitAsync(
                    emit,
                    "queue.changed",
                    new { runQueue = ToRunQueueDto(_runScheduler.Snapshot) },
                    envelope.RequestId).ConfigureAwait(false);
                await SendGoAiChatAsync(envelope, emit, scheduledCancellationToken).ConfigureAwait(false);
                return null;
            },
            CancellationToken.None);

        var observer = ObserveScheduledRunAsync(ticket, emit, envelope.RequestId);
        _scheduledRunObservers[ticket.TicketId] = observer;
        if (observer.IsCompleted)
        {
            _scheduledRunObservers.TryRemove(ticket.TicketId, out _);
        }

        try
        {
            await emit("chat.queued", new
            {
                ticketId = ticket.TicketId,
                ticket.SessionId,
                ticket.RequestId,
                position = ticket.InitialPosition,
                runQueue = ToRunQueueDto(_runScheduler.Snapshot),
            }, envelope.RequestId).ConfigureAwait(false);
        }
        finally
        {
            accepted.TrySetResult();
        }
    }

    private async Task WaitForDetachedRunAsync(CancellationToken cancellationToken)
    {
        while (goAi?.IsRunning == true)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ObserveScheduledRunAsync(
        AssistantRunTicket<object?> ticket,
        Func<string, object, string?, Task> emit,
        string requestId)
    {
        try
        {
            await ticket.Completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await TryEmitAsync(emit, "chat.cancelled", new
            {
                sessionId = ticket.SessionId,
                queued = true,
                runStatus = "Aus Warteschlange entfernt",
            }, requestId).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await TryEmitAsync(emit, "host.error", new { message = exception.Message }, requestId).ConfigureAwait(false);
        }
        finally
        {
            _scheduledRunObservers.TryRemove(ticket.TicketId, out _);
            if (_runScheduler is not null)
            {
                await TryEmitAsync(
                    emit,
                    "queue.changed",
                    new { runQueue = ToRunQueueDto(_runScheduler.Snapshot) },
                    requestId).ConfigureAwait(false);
            }
        }
    }

    private static async Task TryEmitAsync(
        Func<string, object, string?, Task> emit,
        string type,
        object payload,
        string? requestId)
    {
        try
        {
            await emit(type, payload, requestId).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // A closed/reloading WebView must not fail a queued model run or session cleanup.
        }
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
        if (session.ChatMode == ChatMode.Coding
            && (string.IsNullOrWhiteSpace(session.CodingWorkspacePath)
                || !Directory.Exists(session.CodingWorkspacePath)))
        {
            throw new InvalidOperationException("Wähle für diese Coding-Sitzung zuerst ein vorhandenes Projekt oder einen Workspace aus.");
        }
        var promptTitle = DerivePromptSessionTitle(prompt);
        if (promptTitle is { } derivedTitle
            && !string.Equals(session.Title, derivedTitle, StringComparison.Ordinal))
        {
            await chats.RenameSessionAsync(sessionId, derivedTitle, cancellationToken).ConfigureAwait(false);
            session = session with { Title = derivedTitle };
        }
        await chats.SaveDraftAsync(sessionId, string.Empty, cancellationToken).ConfigureAwait(false);
        var explicitExtensionActionId = GetExplicitExtensionActionId(envelope.Payload);
        var match = await ResolvePromptMatchAsync(
            session,
            prompt,
            explicitExtensionActionId,
            cancellationToken).ConfigureAwait(false);
        if (envelope.Payload.TryGetProperty("deepResearch", out var deepResearch) && deepResearch.ValueKind == JsonValueKind.True)
        {
            if (match is not null && match.Trigger.Action is not (PromptTriggerAction.Coding or PromptTriggerAction.WebSearch))
                throw new ArgumentException("Deep Research ist mit General oder Coding nutzbar. Wähle andere einmalige Tools zuerst ab.");
            var researchProfile = GetOptionalString(envelope.Payload, "deepResearchProfile", 64)?.Trim() ?? "auto";
            if (!DeepResearchProfiles.IsValid(researchProfile))
                throw new ArgumentException("Das ausgewählte Deep-Research-Profil ist unbekannt.");
            match = (match ?? CreateToolMatch("webSearch", prompt)) with
            {
                DeepResearch = true,
                DeepResearchProfile = researchProfile,
            };
        }

        await ActivateSessionAsync(session, cancellationToken).ConfigureAwait(false);
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
        var requestedPersistentActionId = PersistentExtensionActionIdFor(match?.Trigger.Action);
        if (string.Equals(requestedPersistentActionId, BuiltInActionIds.CreateAudiobook, StringComparison.Ordinal)
            && IsAudiobookContinuationRequest(prompt, match)
            && !await HasAudiobookContentAsync(session.Id, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "In dieser Sitzung ist noch keine Hörbuchgeschichte vorhanden. Starte zuerst mit „Hörbuch erstellen“.");
        }
        if (requestedPersistentActionId is { } persistentActionId
            && !string.Equals(session.PersistentExtensionActionId, persistentActionId, StringComparison.Ordinal))
        {
            await chats.SetPersistentExtensionActionIdAsync(
                session.Id,
                persistentActionId,
                cancellationToken).ConfigureAwait(false);
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
        var mode = settings.Current.SelectedChatMode;
        var sessions = await chats.ListSessionsAsync(mode, cancellationToken: cancellationToken).ConfigureAwait(false);
        var groups = await chats.ListSessionGroupsAsync(mode, cancellationToken).ConfigureAwait(false);
        // Grouping never owns the active conversation. The user may switch sessions,
        // type a draft or continue an AI run before this independent request finishes.
        return new
        {
            groupingCompleted,
            productName = runtimeProfile?.ProductName ?? "AI Assistent",
            assistantTitle = runtimeProfile?.ProductName ?? "AI Assistent",
            productIdentity = new { assistantTitle = runtimeProfile?.ProductName ?? "AI Assistent" },
            chatMode = ChatModeName(mode),
            selectedChatMode = ChatModeName(mode),
            sessions = sessions.Select(ToSessionDto),
            sessionGroups = groups.Select(group => new
            {
                group.Id,
                group.Name,
                group.IsCollapsed,
                group.CreatedAt,
                group.WorkspacePath,
                chatMode = ChatModeName(group.ChatMode),
                sessionIds = sessions.Where(session => session.SessionGroupId == group.Id).Select(session => session.Id),
            }),
        };
    }

    internal static PromptTriggerMatch CreateToolMatch(string toolAction, string prompt) =>
        CreateExtensionMatch(LegacyExtensionActionIdFor(toolAction), prompt);

    internal static PromptTriggerMatch CreateExtensionMatch(string extensionActionId, string prompt)
    {
        PromptActionExtensionIds.EnsureValid(extensionActionId);
        var action = PromptActionExtensionIds.TryGetPromptAction(extensionActionId, out var builtInAction)
            ? builtInAction
            : PromptTriggerAction.Extension;
        var now = DateTimeOffset.UtcNow;
        var trigger = new PromptTrigger(
            Guid.Empty,
            action,
            extensionActionId,
            "Einmalig über das Prompt-Tools-Menü ausgewählt.",
            PromptTriggerMatchMode.Exact,
            true,
            int.MaxValue,
            0,
            now,
            now,
            extensionActionId);
        return new PromptTriggerMatch(trigger, prompt, prompt.Trim());
    }

    private async Task<PromptTriggerMatch?> ResolvePromptMatchAsync(
        ChatSession session,
        string prompt,
        string? explicitExtensionActionId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(explicitExtensionActionId))
        {
            if (!_extensionActions.TryGetAction(explicitExtensionActionId, out var descriptor) || descriptor is null
                || descriptor.ActionKind != ExtensionActionKind.SelectableTool)
                throw new InvalidOperationException("Die ausgewählte Erweiterungsaktion ist nicht als Modellwerkzeug verfügbar.");
            var mode = ToExtensionChatMode(session.ChatMode);
            if (!descriptor.SupportedChatModes.Contains(mode))
                throw new InvalidOperationException("Die ausgewählte Erweiterungsaktion unterstützt diesen Chatmodus nicht.");
            if (!string.IsNullOrWhiteSpace(descriptor.DisabledReason))
                throw new InvalidOperationException(descriptor.DisabledReason);
            if (!explicitExtensionActionId.StartsWith("builtin.", StringComparison.Ordinal))
            {
                if (extensionRuntime is null)
                    throw new InvalidOperationException("Der ExtensionHost ist für das ausgewählte Modellwerkzeug nicht verfügbar.");
                var groups = await chats.ListSessionGroupsAsync(session.ChatMode, cancellationToken).ConfigureAwait(false);
                var disabledReason = extensionRuntime.GetDisabledReason(
                    explicitExtensionActionId,
                    ResolveSessionWorkspaceRoot(session, groups));
                if (disabledReason is not null) throw new InvalidOperationException(disabledReason);
            }
            var match = CreateExtensionMatch(explicitExtensionActionId, prompt);
            if (session.ChatMode == ChatMode.Coding && match.Trigger.Action == PromptTriggerAction.Extension)
                match = match with { Trigger = match.Trigger with { Action = PromptTriggerAction.Coding } };
            return match;
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
        if (string.Equals(
            session.PersistentExtensionActionId,
            BuiltInActionIds.CreateAudiobook,
            StringComparison.Ordinal))
        {
            return CreateToolMatch("audiobook", prompt);
        }
        if (string.Equals(
            session.PersistentExtensionActionId,
            BuiltInActionIds.PlanMode,
            StringComparison.Ordinal))
        {
            return CreateExtensionMatch(BuiltInActionIds.PlanMode, prompt);
        }
        return session.ChatMode == ChatMode.Coding
            ? CreateToolMatch("coding", prompt)
            : null;
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

    private static string? PersistentExtensionActionIdFor(PromptTriggerAction? action) => action switch
    {
        PromptTriggerAction.Audiobook => BuiltInActionIds.CreateAudiobook,
        PromptTriggerAction.PlanMode => BuiltInActionIds.PlanMode,
        _ => null,
    };

    private static string? LegacyToolAliasFor(string? extensionActionId) => extensionActionId switch
    {
        BuiltInActionIds.CreateAudiobook => "audiobook",
        BuiltInActionIds.PlanMode => "planMode",
        _ => null,
    };

    internal static bool IsCodingAgentSession(ChatMode? mode) => mode is ChatMode.Coding;

    private static ExtensionChatMode ToExtensionChatMode(ChatMode mode) => mode switch
    {
        ChatMode.General => ExtensionChatMode.General,
        ChatMode.Coding => ExtensionChatMode.Coding,
        ChatMode.ClaudeScience => ExtensionChatMode.ClaudeScience,
        _ => throw new InvalidOperationException("Der Chatmodus ist ungültig."),
    };

    private static string ChatModeName(ChatMode mode) => mode switch
    {
        ChatMode.General => "general",
        ChatMode.Coding => "coding",
        ChatMode.ClaudeScience => "claudescience",
        _ => throw new InvalidOperationException("Der Chatmodus ist ungültig."),
    };

    internal static ChatMode ParseChatMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "general" => ChatMode.General,
        "coding" => ChatMode.Coding,
        "claudescience" or "claude-science" or "science" => ChatMode.ClaudeScience,
        _ => throw new ArgumentException("Die angeforderte Chatansicht ist unbekannt."),
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
        if (settings.Current.IsAutomaticSpeechEnabled)
        {
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
        }
        var artifactsForMessage = update.Artifacts
            ?? await artifacts.ListForMessageAsync(update.Message.Id, CancellationToken.None).ConfigureAwait(false);
        switch (update.Kind)
        {
            case GoAiAssistantUpdateKind.Started:
                var sessionMessages = await chats.ListMessagesAsync(
                    update.Message.SessionId,
                    CancellationToken.None).ConfigureAwait(false);
                var currentSession = await chats.GetSessionAsync(
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
                    conversationRevision = currentSession?.ConversationRevision,
                    session = currentSession is null ? null : ToSessionDto(currentSession),
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

    private async Task EmitScientificResearchAsync(
        WebBridgeEnvelope envelope,
        Func<string, object, string?, Task> emit,
        string? selectedProjectId,
        CancellationToken cancellationToken)
    {
        if (scientificResearch is null)
        {
            await emit("research.snapshot", new { available = false, projects = Array.Empty<object>(),
                disabledReason = "Der wissenschaftliche Forschungsspeicher ist nicht verfügbar." }, envelope.RequestId).ConfigureAwait(false);
            return;
        }
        var sessionId = GetOptionalGuid(envelope.Payload, "sessionId")
            ?? (await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false)).Id;
        _ = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die Forschungssitzung wurde nicht gefunden.");
        var projects = await scientificResearch.ListSessionProjectsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var selected = selectedProjectId is null ? null : projects.SingleOrDefault(project => project.Id == selectedProjectId)
            ?? throw new UnauthorizedAccessException("Das Forschungsprojekt gehört nicht zu dieser Sitzung.");
        object? detail = null;
        if (selected is not null)
        {
            var graph = await scientificResearch.LoadGraphAsync(selected.Id, cancellationToken).ConfigureAwait(false);
            var checkpoint = await scientificResearch.GetLatestCheckpointAsync(selected.Id, cancellationToken).ConfigureAwait(false);
            var results = await scientificResearch.LoadResultSnapshotAsync(selected.Id, cancellationToken).ConfigureAwait(false);
            var archive = await scientificResearch.LoadArchiveSnapshotAsync(selected.Id, cancellationToken).ConfigureAwait(false);
            detail = new
            {
                project = ResearchProjectDto(selected),
                nodes = graph.Nodes.Select(static node => new { node.Id, node.NodeType, node.Title, node.Status,
                    node.Priority, node.Confidence, node.EvidenceRequirementsJson, node.VerificationRequirementsJson,
                    node.AttemptCount, node.CheckpointId, node.PayloadJson, node.CreatedAt, node.UpdatedAt }),
                edges = graph.Edges.Select(static edge => new { edge.Id, edge.FromNodeId, edge.ToNodeId, edge.EdgeType }),
                checkpoint = checkpoint is null ? null : new { checkpoint.Id, checkpoint.RunId, checkpoint.Revision,
                    checkpoint.Stage, checkpoint.StateJson, checkpoint.CreatedAt },
                hypotheses = results.Hypotheses.Select(static item => new { item.Id, item.Statement, item.Classification,
                    item.Status, item.Confidence, item.NodeId, item.UpdatedAt }),
                experiments = results.Experiments.Select(static item => new { item.Id, item.CommandText,
                    item.EnvironmentLock, item.SourceFilesJson, item.RandomSeedsJson, item.InputHashesJson,
                    item.ResourceLimitsJson, item.StdoutEvidence, item.StderrEvidence, item.VerificationStatus,
                    item.ResultArtifactsJson, item.HypothesisId, item.CreatedAt, item.UpdatedAt }),
                verifications = results.Verifications.Select(static item => new { item.Id, item.TargetType,
                    item.TargetId, item.Dimension, item.Method, item.Status, item.EvidenceJson, item.CreatedAt }),
                claims = results.Claims.Select(static item => new { item.Id, item.Statement, item.ClaimClass,
                    item.ConclusionStatus, item.Confidence, item.PayloadJson, item.UpdatedAt }),
                works = archive.Works.Select(static item => new { item.WorkId, item.Title, item.CanonicalUrl,
                    item.VersionKind, item.MetadataJson, item.ScreeningStatus, item.EvidenceLevel, item.Doi,
                    item.Pmid, item.Pmcid, item.ArxivId, item.DataCiteId, item.OpenAlexId, item.UpdatedAt }),
                evidence = archive.Evidence.Select(static item => new { item.Id, item.WorkId, item.ExactExcerpt,
                    item.NormalizedStatement, item.ContentHash, item.EvidenceLevel, item.LocatorJson,
                    item.RetrievedAt, item.Page, item.Section, item.TableOrFigure }),
                report = archive.Report is null ? null : new { archive.Report.Id, archive.Report.ReportKind,
                    archive.Report.ConclusionStatus, archive.Report.ContentMarkdown, archive.Report.ManifestJson, archive.Report.CreatedAt },
            };
        }
        await emit("research.snapshot", new
        {
            available = true,
            sessionId,
            projects = projects.Select(ResearchProjectDto),
            selectedProjectId,
            detail,
        }, envelope.RequestId).ConfigureAwait(false);
    }

    private static object ResearchProjectDto(ScientificResearchProject project) => new
    {
        project.Id, project.Profile, project.OriginalQuestion, project.InterpretedQuestion, project.AutonomyLevel,
        project.VerificationLevel, project.Status, project.ProtocolVersion, project.Revision,
        project.WorkspacePath, project.LatestCheckpointId, project.CreatedAt, project.UpdatedAt,
    };

    private async Task ExportScientificResearchAsync(WebBridgeEnvelope envelope,
        Func<string, object, string?, Task> emit, CancellationToken cancellationToken)
    {
        if (scientificResearch is null || scientificResearchExports is null)
            throw new InvalidOperationException("Der wissenschaftliche Exportdienst ist nicht verfügbar.");
        var sessionId = GetOptionalGuid(envelope.Payload, "sessionId")
            ?? (await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false)).Id;
        var projectId = GetRequiredString(envelope.Payload, "projectId", 128);
        var project = await scientificResearch.GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Das Forschungsprojekt wurde nicht gefunden.");
        if (project.SessionId != sessionId) throw new UnauthorizedAccessException("Das Forschungsprojekt gehört nicht zu dieser Sitzung.");
        if (string.IsNullOrWhiteSpace(project.WorkspacePath))
            throw new InvalidOperationException("Ein vollständiges Exportpaket benötigt eine Coding-Sitzung mit Workspace.");
        var bundle = await scientificResearchExports.ExportAllAsync(projectId, project.WorkspacePath, cancellationToken).ConfigureAwait(false);
        await emit("research.exported", new { sessionId, projectId, bundle.Directory, bundle.Files,
            bundle.ManifestPath, bundle.CreatedAt }, envelope.RequestId).ConfigureAwait(false);
    }

    private async Task EmitProjectMemoryAsync(
        WebBridgeEnvelope envelope,
        Func<string, object, string?, Task> emit,
        string messageType,
        CancellationToken cancellationToken)
    {
        var session = await ResolveProjectMemorySessionAsync(envelope, cancellationToken).ConfigureAwait(false);
        var search = string.Equals(envelope.Type, "memory.list", StringComparison.Ordinal)
            ? GetOptionalString(envelope.Payload, "search", 200)
            : null;
        await emit(
            messageType,
            await BuildProjectMemoryPayloadAsync(session, search, cancellationToken).ConfigureAwait(false),
            envelope.RequestId).ConfigureAwait(false);
    }

    private async Task CreateProjectMemoryAsync(
        WebBridgeEnvelope envelope,
        Func<string, object, string?, Task> emit,
        CancellationToken cancellationToken)
    {
        var (store, session, resolved) = await RequireProjectMemoryAsync(envelope, cancellationToken).ConfigureAwait(false);
        _ = await store.CreateAsync(new(
            resolved.Scope,
            ParseProjectMemoryKind(GetRequiredString(envelope.Payload, "kind", 40)),
            GetRequiredString(envelope.Payload, "content", 100_000),
            GetOptionalString(envelope.Payload, "source", 2_000)?.Trim() ?? "manual"),
            cancellationToken).ConfigureAwait(false);
        await EmitProjectMemoryChangedAsync(session, envelope.RequestId, emit, cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateProjectMemoryAsync(
        WebBridgeEnvelope envelope,
        Func<string, object, string?, Task> emit,
        CancellationToken cancellationToken)
    {
        var (store, session, resolved) = await RequireProjectMemoryAsync(envelope, cancellationToken).ConfigureAwait(false);
        var entry = await GetScopedProjectMemoryEntryAsync(
            store, resolved.Scope, GetRequiredGuid(envelope.Payload, "entryId"), cancellationToken).ConfigureAwait(false);
        _ = await store.UpdateAsync(
            entry.Id,
            GetRequiredInt64(envelope.Payload, "version"),
            new(
                ParseProjectMemoryKind(GetRequiredString(envelope.Payload, "kind", 40)),
                GetRequiredString(envelope.Payload, "content", 100_000),
                GetOptionalString(envelope.Payload, "source", 2_000)?.Trim() ?? entry.Source,
                entry.LastConfirmedAt),
            cancellationToken).ConfigureAwait(false);
        await EmitProjectMemoryChangedAsync(session, envelope.RequestId, emit, cancellationToken).ConfigureAwait(false);
    }

    private async Task PinProjectMemoryAsync(
        WebBridgeEnvelope envelope,
        Func<string, object, string?, Task> emit,
        CancellationToken cancellationToken)
    {
        var (store, session, resolved) = await RequireProjectMemoryAsync(envelope, cancellationToken).ConfigureAwait(false);
        var entry = await GetScopedProjectMemoryEntryAsync(
            store, resolved.Scope, GetRequiredGuid(envelope.Payload, "entryId"), cancellationToken).ConfigureAwait(false);
        _ = await store.SetPinnedAsync(
            entry.Id,
            GetRequiredInt64(envelope.Payload, "version"),
            GetRequiredBoolean(envelope.Payload, "isPinned"),
            cancellationToken).ConfigureAwait(false);
        await EmitProjectMemoryChangedAsync(session, envelope.RequestId, emit, cancellationToken).ConfigureAwait(false);
    }

    private async Task ConfirmProjectMemoryAsync(
        WebBridgeEnvelope envelope,
        Func<string, object, string?, Task> emit,
        CancellationToken cancellationToken)
    {
        var (store, session, resolved) = await RequireProjectMemoryAsync(envelope, cancellationToken).ConfigureAwait(false);
        var entry = await GetScopedProjectMemoryEntryAsync(
            store, resolved.Scope, GetRequiredGuid(envelope.Payload, "entryId"), cancellationToken).ConfigureAwait(false);
        _ = await store.ConfirmAsync(
            entry.Id,
            GetRequiredInt64(envelope.Payload, "version"),
            DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);
        await EmitProjectMemoryChangedAsync(session, envelope.RequestId, emit, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteProjectMemoryAsync(
        WebBridgeEnvelope envelope,
        Func<string, object, string?, Task> emit,
        CancellationToken cancellationToken)
    {
        var (store, session, resolved) = await RequireProjectMemoryAsync(envelope, cancellationToken).ConfigureAwait(false);
        var entry = await GetScopedProjectMemoryEntryAsync(
            store, resolved.Scope, GetRequiredGuid(envelope.Payload, "entryId"), cancellationToken).ConfigureAwait(false);
        await store.DeleteAsync(
            entry.Id,
            GetRequiredInt64(envelope.Payload, "version"),
            cancellationToken).ConfigureAwait(false);
        await EmitProjectMemoryChangedAsync(session, envelope.RequestId, emit, cancellationToken).ConfigureAwait(false);
    }

    private async Task SetProjectMemoryAutoCaptureAsync(
        WebBridgeEnvelope envelope,
        Func<string, object, string?, Task> emit,
        CancellationToken cancellationToken)
    {
        var (store, session, resolved) = await RequireProjectMemoryAsync(envelope, cancellationToken).ConfigureAwait(false);
        _ = await store.SetAutoCaptureAsync(
            resolved.Scope,
            GetRequiredBoolean(envelope.Payload, "enabled"),
            cancellationToken).ConfigureAwait(false);
        await EmitProjectMemoryChangedAsync(session, envelope.RequestId, emit, cancellationToken).ConfigureAwait(false);
    }

    private async Task EmitProjectMemoryChangedAsync(
        ChatSession session,
        string requestId,
        Func<string, object, string?, Task> emit,
        CancellationToken cancellationToken) =>
        await emit(
            "memory.changed",
            await BuildProjectMemoryPayloadAsync(session, null, cancellationToken).ConfigureAwait(false),
            requestId).ConfigureAwait(false);

    private async Task<object> BuildProjectMemoryPayloadAsync(
        ChatSession session,
        string? search,
        CancellationToken cancellationToken)
    {
        if (projectMemory is null)
            return UnavailableProjectMemoryPayload("Das Projektgedächtnis ist in dieser Installation nicht verfügbar.");

        var resolved = await ResolveProjectMemoryScopeAsync(session, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
            return UnavailableProjectMemoryPayload("Öffne zuerst ein Projekt oder einen Workspace.");

        var settingsValue = await projectMemory.GetSettingsAsync(resolved.Scope, cancellationToken).ConfigureAwait(false);
        var entries = await projectMemory.ListAsync(resolved.Scope, search, cancellationToken).ConfigureAwait(false);
        return new
        {
            available = true,
            projectKey = resolved.Scope.ProjectKey,
            projectId = resolved.Scope.ProjectId,
            projectLabel = resolved.Label,
            autoCaptureEnabled = settingsValue.AutoCaptureEnabled,
            entries = entries.Select(ToProjectMemoryEntryDto),
            disabledReason = (string?)null,
        };
    }

    private static object UnavailableProjectMemoryPayload(string disabledReason) => new
    {
        available = false,
        projectKey = (string?)null,
        projectId = (string?)null,
        projectLabel = "Projektgedächtnis",
        autoCaptureEnabled = false,
        entries = Array.Empty<object>(),
        disabledReason,
    };

    private async Task<ChatSession> ResolveProjectMemorySessionAsync(
        WebBridgeEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var sessionId = GetOptionalGuid(envelope.Payload, "sessionId")
            ?? (await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false)).Id;
        return await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die Sitzung des Projektgedächtnisses wurde nicht gefunden.");
    }

    private async Task<(IProjectMemoryStore Store, ChatSession Session, ResolvedProjectMemory Resolved)> RequireProjectMemoryAsync(
        WebBridgeEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var store = projectMemory
            ?? throw new InvalidOperationException("Das Projektgedächtnis ist in dieser Installation nicht verfügbar.");
        var session = await ResolveProjectMemorySessionAsync(envelope, cancellationToken).ConfigureAwait(false);
        var resolved = await ResolveProjectMemoryScopeAsync(session, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Öffne zuerst ein Projekt oder einen Workspace.");
        return (store, session, resolved);
    }

    private async Task<ResolvedProjectMemory?> ResolveProjectMemoryScopeAsync(
        ChatSession session,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(session.CodingWorkspacePath))
        {
            var fullPath = Path.GetFullPath(session.CodingWorkspacePath);
            return new(
                ProjectMemoryScope.FromWorkspace(fullPath),
                WorkspaceLabel(fullPath));
        }

        if (session.SessionGroupId is not { } groupId) return null;
        var group = (await chats.ListSessionGroupsAsync(session.ChatMode, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(candidate => candidate.Id == groupId);
        if (group is null) return null;
        return !string.IsNullOrWhiteSpace(group.WorkspacePath)
            ? new(
                ProjectMemoryScope.FromWorkspace(group.WorkspacePath, group.Id.ToString("D")),
                group.Name)
            : new(ProjectMemoryScope.FromProjectId(group.Id.ToString("D")), group.Name);
    }

    private static async Task<ProjectMemoryEntry> GetScopedProjectMemoryEntryAsync(
        IProjectMemoryStore store,
        ProjectMemoryScope scope,
        Guid entryId,
        CancellationToken cancellationToken)
    {
        var entry = await store.GetAsync(entryId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Der Gedächtniseintrag wurde nicht gefunden.");
        if (!string.Equals(entry.ProjectKey, scope.ProjectKey, StringComparison.Ordinal))
            throw new InvalidOperationException("Der Gedächtniseintrag gehört nicht zum aktuellen Projekt.");
        return entry;
    }

    private static object ToProjectMemoryEntryDto(ProjectMemoryEntry entry) => new
    {
        id = entry.Id,
        projectKey = entry.ProjectKey,
        projectId = entry.ProjectId,
        kind = ProjectMemoryKindName(entry.Kind),
        entry.Content,
        entry.Source,
        entry.Version,
        entry.CreatedAt,
        entry.UpdatedAt,
        entry.LastConfirmedAt,
        entry.IsPinned,
    };

    private static ProjectMemoryKind ParseProjectMemoryKind(string value) =>
        value.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Trim().ToLowerInvariant() switch
        {
            "requirement" => ProjectMemoryKind.Requirement,
            "decision" => ProjectMemoryKind.Decision,
            "milestone" => ProjectMemoryKind.Milestone,
            "errorresolution" => ProjectMemoryKind.ErrorResolution,
            "verification" => ProjectMemoryKind.Verification,
            _ => throw new InvalidOperationException("Die Art des Gedächtniseintrags ist unbekannt."),
        };

    private static string ProjectMemoryKindName(ProjectMemoryKind kind) => kind switch
    {
        ProjectMemoryKind.Requirement => "requirement",
        ProjectMemoryKind.Decision => "decision",
        ProjectMemoryKind.Milestone => "milestone",
        ProjectMemoryKind.ErrorResolution => "errorResolution",
        ProjectMemoryKind.Verification => "verification",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Die Art des Gedächtniseintrags ist unbekannt."),
    };

    private static string WorkspaceLabel(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        return Path.GetFileName(trimmed) is { Length: > 0 } name ? name : trimmed;
    }

    private static string? ResolveSessionWorkspaceRoot(
        ChatSession session,
        IReadOnlyList<ChatSessionGroup> groups)
    {
        if (!string.IsNullOrWhiteSpace(session.CodingWorkspacePath))
            return Path.GetFullPath(session.CodingWorkspacePath);
        if (session.SessionGroupId is not { } groupId) return null;
        var workspace = groups.FirstOrDefault(group => group.Id == groupId)?.WorkspacePath;
        return string.IsNullOrWhiteSpace(workspace) ? null : Path.GetFullPath(workspace);
    }

    private sealed record ResolvedProjectMemory(ProjectMemoryScope Scope, string Label);

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
            schema = "assistant.workflow.v1",
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
        chatMode = ChatModeName(session.ChatMode),
        workspacePath = session.CodingWorkspacePath,
        persistentExtensionActionId = session.PersistentExtensionActionId,
        sessionGroupId = session.SessionGroupId,
        session.ConversationRevision,
    };

    private static object ToRunQueueDto(AssistantRunScheduleSnapshot snapshot) => new
    {
        active = snapshot.Active is null ? null : ToScheduledRunDto(snapshot.Active),
        pending = snapshot.Pending.Select(ToScheduledRunDto),
        snapshot.QueueDepth,
        snapshot.IsIdle,
    };

    private static object ToScheduledRunDto(AssistantScheduledRun run) => new
    {
        run.TicketId,
        run.SessionId,
        run.RequestId,
        state = run.State.ToString().ToLowerInvariant(),
        run.Position,
        run.EnqueuedAt,
        run.StartedAt,
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
        stepId = artifact.StepId,
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
