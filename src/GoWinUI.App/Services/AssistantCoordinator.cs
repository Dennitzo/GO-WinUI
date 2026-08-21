using GoWinUI.Core.Contracts;
using GoWinUI.Core.Chat;
using GoWinUI.Core.Models;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace GoWinUI.App.Services;

public sealed class AssistantCoordinator(
    IChatRepository chats,
    IWorkflowRepository workflows,
    IDocumentIngestor documents,
    ILmStudioClient lmStudio,
    IContextAssembler contextAssembler,
    IChatOrchestrator orchestrator,
    IPromptTriggerRepository promptTriggers,
    IAssistantAttachmentRepository attachments,
    IChatArtifactRepository artifacts,
    IConversationSnapshotRepository conversationSnapshots,
    GoAiAssistantService? goAi,
    SettingsCoordinator settings,
    RecentActivityService recentActivity,
    MicrophoneTranscriptionService? microphone = null,
    CodingCampaignService? campaigns = null) : IDisposable
{
    private const string DefaultSessionTitle = "Neue Sitzung";
    private const string DefaultSystemPrompt = "GO ist ein lokales Arbeitstool für TGA-Fachplanung. Unterstütze Fachplaner bei technischer Gebäudeausrüstung, Anlagenkonzepten, Berechnungen, Koordination und Dokumentation. GO ist hier ein Produktname und nicht die Programmiersprache Go. Weise auf Unsicherheit, fehlende Projektdaten und erforderliche fachliche Prüfungen hin; erfinde keine Norminhalte, Quellen oder Projektangaben.";
    private readonly SemaphoreSlim _chatGate = new(1, 1);
    private CancellationTokenSource? _activeChatCancellation;
    private string? _activeDiagnosticSessionId;
    private IReadOnlyList<LmModel> _knownModels = Array.Empty<LmModel>();
    private int _startupCodingRunsHandled;

    public Task SaveDraftAsync(Guid sessionId, string draft, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(draft.Length, 100_000);
        return chats.SaveDraftAsync(sessionId, draft, cancellationToken);
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

    public async Task SetActiveWorkspaceAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
        if (!Directory.Exists(normalized))
        {
            throw new DirectoryNotFoundException("Der ausgewählte Workspace wurde nicht gefunden.");
        }
        var session = await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false);
        await chats.SetAssistantContextAsync(
            session.Id,
            session.AssistantMode,
            normalized,
            WorkspaceRepositoryIndex.CreateWorkspaceFingerprint(normalized),
            cancellationToken).ConfigureAwait(false);
        await settings.UpdateAsync(
            current => current with { LocalToolWorkspacePath = normalized },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelCurrentAsync()
    {
        if (campaigns is not null
            && settings.Current.ActiveSessionId is { } campaignSessionId
            && await campaigns.StopForNewPromptAsync(campaignSessionId, CancellationToken.None).ConfigureAwait(false))
        {
            return;
        }
        _activeChatCancellation?.Cancel();
        orchestrator.Cancel();
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
        var session = await EnsureSessionWorkspaceAsync(
            await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
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
        var campaignSnapshot = campaigns is null
            ? new CodingCampaignUiSnapshot([], null)
            : await campaigns.GetSnapshotAsync(session.Id, cancellationToken).ConfigureAwait(false);
        var codingRun = conversation.CodingRun;
        var pages = new List<DocumentPage>();
        foreach (var document in documentItems)
        {
            pages.AddRange(await documents.ReadPagesAsync(document.Id, cancellationToken).ConfigureAwait(false));
        }

        // A snapshot is local UI state. Never make sidebar/session interaction wait for
        // LM Studio, which may take several seconds to time out when it is offline.
        var contextLimit = settings.Current.AiProvider == AiProviderKind.GoAiServer
            ? session.PersistentToolAction == PersistentToolAction.Code ? 262_144 : 131_072
            : ResolveKnownContextLimit();
        var context = contextAssembler.Build(new(
            DefaultSystemPrompt,
            string.IsNullOrWhiteSpace(session.Draft) ? "Nächste Benutzereingabe" : session.Draft,
            messages,
            null,
            pages,
            contextLimit));
        return new
        {
            sessions = sessions.Select(ToSessionDto),
            messages = messages.Select(message => ToMessageDto(
                message,
                artifactItems.TryGetValue(message.Id, out var messageArtifacts) ? messageArtifacts : null)),
            workflows = workflowItems.Select(ToWorkflowDto),
            codingCampaignDefinitions = campaignSnapshot.Definitions,
            codingCampaign = campaignSnapshot.ActiveCampaign,
            conversationRevision = session.ConversationRevision,
            codingRun = codingRun is null ? null : ToCodingRunDto(codingRun),
            documents = documentItems.Select(ToDocumentDto),
            attachments = attachmentItems.Select(ToAttachmentDto),
            documentGroupStatus,
            activeSessionId = session.Id,
            draft = session.Draft,
            isRunning = settings.Current.IsAiConnectionEnabled
                && (settings.Current.AiProvider == AiProviderKind.GoAiServer ? goAi?.IsRunning == true : orchestrator.IsRunning),
            model = settings.Current.AiProvider == AiProviderKind.GoAiServer ? "GO AI Server" : settings.Current.SelectedModel,
            provider = settings.Current.AiProvider.ToString(),
            reasoningEffort = settings.Current.ReasoningEffort,
            contextUsed = context.EstimatedTokens,
            contextLimit,
            contextWasTruncated = context.WasTruncated,
            contextNotice = context.TruncationNotice,
            selectedToolAction = PersistentToolActionName(session.PersistentToolAction),
            assistantMode = session.AssistantMode.ToString().ToLowerInvariant(),
            workspacePath = session.WorkspacePath,
            workspaceFingerprint = session.WorkspaceFingerprint,
            workspaceAvailable = !string.IsNullOrWhiteSpace(session.WorkspacePath) && Directory.Exists(session.WorkspacePath),
            workspaceName = string.IsNullOrWhiteSpace(session.WorkspacePath)
                ? null
                : Path.GetFileName(session.WorkspacePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
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
            messages = conversation.Messages.Select(message => ToMessageDto(
                message,
                conversation.Artifacts.TryGetValue(message.Id, out var messageArtifacts) ? messageArtifacts : null)),
            codingRun = conversation.CodingRun is null ? null : ToCodingRunDto(conversation.CodingRun),
        };
    }

    private async Task EmitCommittedMessageAsync(
        Guid messageId,
        Func<string, object, string?, Task> emit,
        string requestId)
    {
        var messageReference = await chats.GetMessageAsync(
            messageId,
            includeInternal: true,
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

    private async Task EmitCodingSnapshotAsync(
        Guid sessionId,
        Func<string, object, string?, Task> emit,
        string requestId)
    {
        var conversation = await conversationSnapshots.GetAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        if (conversation is null)
        {
            return;
        }
        await emit("coding.snapshotCommitted", new
        {
            sessionId,
            conversationRevision = conversation.Session.ConversationRevision,
            codingRun = conversation.CodingRun is null ? null : ToCodingRunDto(conversation.CodingRun),
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
                var isFirstReady = Interlocked.CompareExchange(ref _startupCodingRunsHandled, 1, 0) == 0;
                if (isFirstReady && campaigns is not null)
                {
                    await campaigns.PrepareForClientStartAsync(cancellationToken).ConfigureAwait(false);
                }
                if (isFirstReady && goAi is not null)
                {
                    await goAi.StopPersistedCampaignRunsAtStartupAsync(cancellationToken).ConfigureAwait(false);
                }
                campaigns?.AttachSinks(
                    update => EmitGoAiUpdateAsync(update, emit, "campaign"),
                    snapshot => emit("campaign.changed", snapshot, "campaign"));
                await emit("state.snapshot", await BuildSnapshotAsync(cancellationToken), envelope.RequestId);
                if (settings.Current.IsAiConnectionEnabled
                    && settings.Current.AiProvider == AiProviderKind.GoAiServer
                    && goAi is not null)
                {
                    try
                    {
                        await goAi.ResumePendingAsync(
                            update => EmitGoAiUpdateAsync(update, emit, envelope.RequestId),
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (GoAiStreamDetachedException)
                    {
                        // The persisted Last-Event-ID remains authoritative for the next WebView instance.
                    }
                }
                break;
            }
            case "session.create":
                await CreateSessionAsync(emit, envelope.RequestId, cancellationToken);
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
            case "session.mode":
                await SetSessionModeAsync(
                    GetRequiredString(envelope.Payload, "mode", 16),
                    emit,
                    envelope.RequestId,
                    cancellationToken).ConfigureAwait(false);
                break;
            case "session.tool":
                await SetSessionToolAsync(
                    GetOptionalString(envelope.Payload, "action", 32),
                    emit,
                    envelope.RequestId,
                    cancellationToken).ConfigureAwait(false);
                break;
            case "chat.send":
                await SendChatAsync(envelope, emit, cancellationToken);
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
            case "campaign.list":
            {
                var campaignService = campaigns ?? throw new InvalidOperationException("Coding-Workflows sind nicht verfügbar.");
                var active = await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false);
                await emit("campaign.snapshot", await campaignService.GetSnapshotAsync(active.Id, cancellationToken).ConfigureAwait(false), envelope.RequestId);
                break;
            }
            case "campaign.select":
            {
                var campaignService = campaigns ?? throw new InvalidOperationException("Coding-Workflows sind nicht verfügbar.");
                var campaignSessionId = GetOptionalGuid(envelope.Payload, "sessionId")
                    ?? (await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false)).Id;
                var snapshot = await campaignService.SelectAsync(
                    campaignSessionId,
                    GetRequiredString(envelope.Payload, "definitionId", 100),
                    cancellationToken).ConfigureAwait(false);
                await emit("campaign.changed", snapshot, envelope.RequestId);
                break;
            }
            case "campaign.run":
            {
                var campaignService = campaigns ?? throw new InvalidOperationException("Coding-Workflows sind nicht verfügbar.");
                var campaignSessionId = GetOptionalGuid(envelope.Payload, "sessionId")
                    ?? (await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false)).Id;
                var instruction = GetOptionalString(envelope.Payload, "instruction", 100_000);
                await chats.SaveDraftAsync(campaignSessionId, string.Empty, cancellationToken).ConfigureAwait(false);
                await emit(
                    "campaign.changed",
                    await campaignService.RunAsync(campaignSessionId, instruction, cancellationToken).ConfigureAwait(false),
                    envelope.RequestId).ConfigureAwait(false);
                break;
            }
            case "campaign.stop":
            {
                var campaignService = campaigns ?? throw new InvalidOperationException("Coding-Workflows sind nicht verfügbar.");
                var campaignSessionId = GetOptionalGuid(envelope.Payload, "sessionId")
                    ?? (await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false)).Id;
                await emit(
                    "campaign.changed",
                    await campaignService.StopAsync(campaignSessionId, cancellationToken).ConfigureAwait(false),
                    envelope.RequestId).ConfigureAwait(false);
                break;
            }
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
        if (orchestrator.IsRunning || goAi?.IsRunning == true)
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

    private async Task<ChatSession> EnsureSessionWorkspaceAsync(
        ChatSession session,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(session.WorkspacePath)
            || string.IsNullOrWhiteSpace(settings.Current.LocalToolWorkspacePath)
            || !Directory.Exists(settings.Current.LocalToolWorkspacePath))
        {
            return session;
        }
        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.Current.LocalToolWorkspacePath));
        await chats.SetAssistantContextAsync(
            session.Id,
            session.AssistantMode,
            workspace,
            WorkspaceRepositoryIndex.CreateWorkspaceFingerprint(workspace),
            cancellationToken).ConfigureAwait(false);
        return (await chats.GetSessionAsync(session.Id, cancellationToken).ConfigureAwait(false))!;
    }

    private async Task CreateSessionAsync(
        Func<string, object, string?, Task> emit,
        string requestId,
        CancellationToken cancellationToken)
    {
        var session = await chats.CreateSessionAsync(DefaultSessionTitle, cancellationToken).ConfigureAwait(false);
        session = await EnsureSessionWorkspaceAsync(session, cancellationToken).ConfigureAwait(false);
        await settings.UpdateAsync(current => current with { ActiveSessionId = session.Id }, cancellationToken).ConfigureAwait(false);
        await recentActivity.RecordAsync(
            $"AI-Sitzung „{session.Title}“ erstellt",
            CancellationToken.None).ConfigureAwait(false);
        await emit("session.changed", await BuildSnapshotAsync(cancellationToken), requestId);
    }

    private async Task SetSessionModeAsync(
        string requestedMode,
        Func<string, object, string?, Task> emit,
        string requestId,
        CancellationToken cancellationToken)
    {
        var mode = requestedMode.Equals("code", StringComparison.OrdinalIgnoreCase)
            ? AssistantMode.Code
            : requestedMode.Equals("general", StringComparison.OrdinalIgnoreCase)
                ? AssistantMode.General
                : throw new InvalidOperationException("Der angeforderte AI-Modus ist unbekannt.");
        var session = await EnsureSessionWorkspaceAsync(
            await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        await chats.SetPersistentToolActionAsync(
            session.Id,
            mode == AssistantMode.Code ? PersistentToolAction.Code : null,
            cancellationToken).ConfigureAwait(false);
        await emit("session.changed", await BuildSnapshotAsync(cancellationToken), requestId).ConfigureAwait(false);
    }

    private async Task SetSessionToolAsync(
        string? requestedAction,
        Func<string, object, string?, Task> emit,
        string requestId,
        CancellationToken cancellationToken)
    {
        var action = requestedAction?.Trim() switch
        {
            null or "" => (PersistentToolAction?)null,
            "code" => PersistentToolAction.Code,
            "bricsCad" => PersistentToolAction.BricsCad,
            "audiobook" => PersistentToolAction.Audiobook,
            _ => throw new InvalidOperationException("Die angeforderte persistente Tool-Aktion ist unbekannt."),
        };
        var session = await EnsureSessionWorkspaceAsync(
            await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
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

        if (campaigns is not null)
        {
            var campaign = (await campaigns.GetSnapshotAsync(sessionId, cancellationToken).ConfigureAwait(false))
                .ActiveCampaign;
            if (string.Equals(campaign?.Status, "running", StringComparison.OrdinalIgnoreCase))
            {
                _ = await campaigns.StopAsync(sessionId, cancellationToken).ConfigureAwait(false);
            }
        }

        if (goAi?.ActiveSessionId == sessionId)
        {
            await goAi.CancelCurrentAndWaitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        if (Guid.TryParse(Volatile.Read(ref _activeDiagnosticSessionId), out var diagnosticSessionId)
            && diagnosticSessionId == sessionId)
        {
            _activeChatCancellation?.Cancel();
            orchestrator.Cancel();
            await _chatGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            _chatGate.Release();
        }

        await chats.DeleteSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
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
        if (orchestrator.IsRunning || goAi?.IsRunning == true)
        {
            throw new InvalidOperationException("Die Sitzungen können während einer laufenden Antwort nicht gelöscht werden.");
        }

        var sessions = await chats.ListSessionsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var session in sessions)
        {
            await chats.DeleteSessionAsync(session.Id, cancellationToken).ConfigureAwait(false);
        }

        await settings.UpdateAsync(current => current with { ActiveSessionId = null }, cancellationToken).ConfigureAwait(false);
        if (sessions.Count > 0)
        {
            await recentActivity.RecordAsync(
                "Alle AI-Sitzungen gelöscht",
                CancellationToken.None).ConfigureAwait(false);
        }

        _ = await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false);
        await emit("session.changed", await BuildSnapshotAsync(cancellationToken), requestId);
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
        session = await EnsureSessionWorkspaceAsync(session, cancellationToken).ConfigureAwait(false);
        var reasoning = GetOptionalString(envelope.Payload, "reasoningEffort", 20)
            ?? settings.Current.ReasoningEffort;
        await settings.UpdateAsync(current => current with
        {
            ActiveSessionId = sessionId,
            ReasoningEffort = reasoning,
        }, cancellationToken).ConfigureAwait(false);
        await chats.SaveDraftAsync(sessionId, string.Empty, cancellationToken).ConfigureAwait(false);
        var explicitTool = GetOptionalString(envelope.Payload, "toolAction", 40);
        var match = await ResolvePromptMatchAsync(
            session,
            prompt,
            explicitTool,
            cancellationToken).ConfigureAwait(false);
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
        if (campaigns is not null)
        {
            _ = await campaigns.StopForNewPromptAsync(sessionId, cancellationToken).ConfigureAwait(false);
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

    internal static PromptTriggerMatch CreateToolMatch(string toolAction, string prompt)
    {
        var action = toolAction switch
        {
            "audioAnalysis" => PromptTriggerAction.AudioAnalysis,
            "imageAnalysis" => PromptTriggerAction.ImageAnalysis,
            "imageGeneration" => PromptTriggerAction.ImageGeneration,
            "bricsCad" => PromptTriggerAction.BricsCad,
            "code" => PromptTriggerAction.Code,
            "audiobook" => PromptTriggerAction.Audiobook,
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

        if (session.PersistentToolAction == PersistentToolAction.Code)
        {
            return CreateToolMatch("code", prompt);
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
        PromptTriggerAction.Code => PersistentToolAction.Code,
        PromptTriggerAction.BricsCad => PersistentToolAction.BricsCad,
        PromptTriggerAction.Audiobook => PersistentToolAction.Audiobook,
        _ => null,
    };

    private static string? PersistentToolActionName(PersistentToolAction? action) => action switch
    {
        PersistentToolAction.Code => "code",
        PersistentToolAction.BricsCad => "bricsCad",
        PersistentToolAction.Audiobook => "audiobook",
        _ => null,
    };

    private async Task EmitGoAiUpdateAsync(
        GoAiAssistantUpdate update,
        Func<string, object, string?, Task> emit,
        string requestId)
    {
        var artifactsForMessage = update.Artifacts
            ?? await artifacts.ListForMessageAsync(update.Message.Id, CancellationToken.None).ConfigureAwait(false);
        switch (update.Kind)
        {
            case GoAiAssistantUpdateKind.MessageAdded:
                await EmitCommittedMessageAsync(update.Message.Id, emit, requestId).ConfigureAwait(false);
                break;
            case GoAiAssistantUpdateKind.MessageRemoved:
                await emit("conversation.messageRemoved", new
                {
                    messageId = update.Message.Id,
                    sessionId = update.Message.SessionId,
                    conversationRevision = await chats.GetConversationRevisionAsync(
                        update.Message.SessionId,
                        CancellationToken.None).ConfigureAwait(false),
                }, requestId).ConfigureAwait(false);
                break;
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
                    userMessage = precedingUserMessage is null
                        ? null
                        : ToMessageDto(precedingUserMessage, precedingUserArtifacts),
                    message = ToMessageDto(update.Message, artifactsForMessage),
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
                    contextLimit = update.ContextLimit,
                    contextWasTruncated = update.ContextWasCompacted,
                    loadedFiles = update.LoadedFiles,
                }, requestId).ConfigureAwait(false);
                break;
            case GoAiAssistantUpdateKind.ArtifactsChanged:
                await EmitCommittedMessageAsync(update.Message.Id, emit, requestId).ConfigureAwait(false);
                break;
            case GoAiAssistantUpdateKind.DocumentsChanged:
                await emit("session.changed", await BuildSnapshotAsync(CancellationToken.None), requestId).ConfigureAwait(false);
                break;
            case GoAiAssistantUpdateKind.CodeDiffChanged:
                await EmitCommittedMessageAsync(update.Message.Id, emit, requestId).ConfigureAwait(false);
                await EmitCodingSnapshotAsync(update.Message.SessionId, emit, requestId).ConfigureAwait(false);
                await emit("chat.codeDiff", new
                {
                    messageId = update.Message.Id,
                    sessionId = update.Message.SessionId,
                    codeDiff = update.Message.CodeDiff,
                    detail = update.Detail,
                }, requestId).ConfigureAwait(false);
                break;
            case GoAiAssistantUpdateKind.CodingTraceChanged:
                if (update.CodingTrace is not null)
                {
                    await EmitCodingSnapshotAsync(update.Message.SessionId, emit, requestId).ConfigureAwait(false);
                }
                break;
            case GoAiAssistantUpdateKind.Completed:
                await EmitCommittedMessageAsync(update.Message.Id, emit, requestId).ConfigureAwait(false);
                await emit("chat.completed", new
                {
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
                    message = ToMessageDto(update.Message, artifactsForMessage),
                    runStatus = update.Status,
                }, requestId).ConfigureAwait(false);
                break;
            case GoAiAssistantUpdateKind.Failed:
                await EmitCommittedMessageAsync(update.Message.Id, emit, requestId).ConfigureAwait(false);
                await emit("chat.failed", new
                {
                    message = ToMessageDto(update.Message, artifactsForMessage),
                    error = update.Error,
                    runStatus = update.Status,
                }, requestId).ConfigureAwait(false);
                break;
        }
    }

    private static bool IsCancelCommand(string prompt) =>
        prompt.Trim(' ', '.', ',', '!', '?').Equals("abbrechen", StringComparison.OrdinalIgnoreCase);

    private async Task SendDiagnosticChatAsync(
        WebBridgeEnvelope envelope,
        Func<string, object, string?, Task> emit,
        CancellationToken cancellationToken)
    {
        if (!_chatGate.Wait(0, cancellationToken))
        {
            throw new InvalidOperationException("Es läuft bereits eine Antwort.");
        }

        Guid? runningSessionId = null;
        try
        {
            var prompt = GetRequiredString(envelope.Payload, "prompt", 100_000);
            var sessionId = GetOptionalGuid(envelope.Payload, "sessionId")
                ?? (await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false)).Id;
            runningSessionId = sessionId;
            Volatile.Write(ref _activeDiagnosticSessionId, sessionId.ToString("D"));
            var model = await ResolveModelAsync(cancellationToken).ConfigureAwait(false);
            var reasoning = GetOptionalString(envelope.Payload, "reasoningEffort", 20)
                ?? settings.Current.ReasoningEffort;
            await settings.UpdateAsync(current => current with
            {
                ActiveSessionId = sessionId,
                ReasoningEffort = reasoning,
            }, cancellationToken).ConfigureAwait(false);
            await chats.SaveDraftAsync(sessionId, string.Empty, cancellationToken).ConfigureAwait(false);

            _ = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Die Sitzung wurde nicht gefunden.");

            _activeChatCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var updates = Channel.CreateUnbounded<ChatStreamUpdate>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });
            EventHandler<ChatStreamUpdate> updateHandler = (_, update) =>
            {
                if (update.SessionId == sessionId)
                {
                    _ = updates.Writer.TryWrite(update);
                }
            };
            orchestrator.StreamUpdated += updateHandler;
            try
            {
                var sendTask = orchestrator.SendAsync(
                    sessionId,
                    prompt,
                    model,
                    DefaultSystemPrompt,
                    reasoning,
                    _activeChatCancellation.Token);
                await Task.Delay(25, CancellationToken.None).ConfigureAwait(false);
                await emit("session.changed", await BuildSnapshotAsync(CancellationToken.None), envelope.RequestId);
                var started = false;

                while (!sendTask.IsCompleted)
                {
                    while (updates.Reader.TryRead(out var update))
                    {
                        started = await EmitStreamUpdateAsync(update, started, emit, envelope.RequestId);
                    }

                    var updateAvailable = updates.Reader.WaitToReadAsync(CancellationToken.None).AsTask();
                    _ = await Task.WhenAny(sendTask, updateAvailable).ConfigureAwait(false);
                }

                while (updates.Reader.TryRead(out var update))
                {
                    started = await EmitStreamUpdateAsync(update, started, emit, envelope.RequestId);
                }

                var completed = await sendTask.ConfigureAwait(false);
                var updatedSession = await chats.GetSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Die Sitzung wurde nach dem AI-Lauf nicht gefunden.");
                await recentActivity.RecordAsync(
                    $"AI-Sitzung „{updatedSession.Title}“ bearbeitet",
                    CancellationToken.None).ConfigureAwait(false);
                await EmitCommittedMessageAsync(completed.Id, emit, envelope.RequestId).ConfigureAwait(false);
                await emit(EventTypeFor(completed.Status), new
                {
                    message = ToMessageDto(completed),
                    error = completed.Error,
                    session = ToSessionDto(updatedSession),
                }, envelope.RequestId);
            }
            finally
            {
                orchestrator.StreamUpdated -= updateHandler;
                updates.Writer.TryComplete();
            }
        }
        catch (OperationCanceledException)
        {
            ChatMessage? final = null;
            if (runningSessionId is { } sessionId)
            {
                final = (await chats.ListMessagesAsync(sessionId, CancellationToken.None).ConfigureAwait(false))
                    .LastOrDefault(message => message.Role == ChatRole.Assistant);
            }

            if (final is not null)
            {
                await EmitCommittedMessageAsync(final.Id, emit, envelope.RequestId).ConfigureAwait(false);
            }
            await emit("chat.cancelled", new { message = final is null ? null : ToMessageDto(final) }, envelope.RequestId);
        }
        catch (Exception exception)
        {
            ChatMessage? final = null;
            if (runningSessionId is { } sessionId)
            {
                final = (await chats.ListMessagesAsync(sessionId, CancellationToken.None).ConfigureAwait(false))
                    .LastOrDefault(message => message.Role == ChatRole.Assistant);
            }

            if (final is not null)
            {
                await EmitCommittedMessageAsync(final.Id, emit, envelope.RequestId).ConfigureAwait(false);
            }
            await emit("chat.failed", new
            {
                message = final is null ? null : ToMessageDto(final),
                error = exception.Message,
            }, envelope.RequestId);
        }
        finally
        {
            Volatile.Write(ref _activeDiagnosticSessionId, null);
            _activeChatCancellation?.Dispose();
            _activeChatCancellation = null;
            _chatGate.Release();
        }
    }

    private async Task<bool> EmitStreamUpdateAsync(
        ChatStreamUpdate update,
        bool started,
        Func<string, object, string?, Task> emit,
        string requestId)
    {
        if (!started)
        {
            started = true;
            await emit(
                "conversation.snapshot",
                await BuildConversationSnapshotAsync(update.SessionId, CancellationToken.None).ConfigureAwait(false),
                requestId).ConfigureAwait(false);
            await emit("chat.started", new
            {
                message = new
                {
                    id = update.MessageId,
                    sessionId = update.SessionId,
                    role = "assistant",
                    content = update.Content,
                    status = update.Status.ToString().ToLowerInvariant(),
                    createdAt = DateTimeOffset.UtcNow,
                    updatedAt = DateTimeOffset.UtcNow,
                },
                contextUsed = update.EstimatedContextTokens,
                contextLimit = update.ContextLimit,
                contextWasTruncated = update.ContextWasTruncated,
                contextNotice = update.ContextNotice,
            }, requestId);
        }

        if (update.Status == MessageStatus.Streaming)
        {
            await EmitCommittedMessageAsync(update.MessageId, emit, requestId).ConfigureAwait(false);
            await emit("chat.delta", new
            {
                messageId = update.MessageId,
                sessionId = update.SessionId,
                content = update.Content,
            }, requestId);
        }

        return started;
    }

    private async Task<string> ResolveModelAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(settings.Current.SelectedModel))
        {
            return settings.Current.SelectedModel;
        }

        var models = await lmStudio.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        _knownModels = models;
        if (models.Count == 0)
        {
            throw new InvalidOperationException("LM Studio ist nicht erreichbar oder es ist kein Modell geladen.");
        }

        if (models.Count > 1)
        {
            throw new InvalidOperationException("Mehrere Modelle sind geladen. Wähle zuerst ein Modell in den Einstellungen.");
        }

        var model = models[0].Id;
        await settings.UpdateAsync(current => current with { SelectedModel = model }, cancellationToken).ConfigureAwait(false);
        return model;
    }

    private int ResolveKnownContextLimit()
    {
        var models = _knownModels;
        var selected = settings.Current.SelectedModel;
        var model = string.IsNullOrWhiteSpace(selected)
            ? models.Count == 1 ? models[0] : null
            : models.FirstOrDefault(candidate => string.Equals(candidate.Id, selected, StringComparison.Ordinal));
        return model?.ContextLength is >= 2_048 and <= 10_000_000
            ? model.ContextLength.Value
            : 8_192;
    }

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
        if (orchestrator.IsRunning || goAi?.IsRunning == true)
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
        assistantMode = session.AssistantMode.ToString().ToLowerInvariant(),
        persistentToolAction = PersistentToolActionName(session.PersistentToolAction),
        session.WorkspacePath,
        session.WorkspaceFingerprint,
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
            codeDiff = message.CodeDiff,
            message.Revision,
            tool = message.ToolExecution,
            artifacts = (messageArtifacts ?? []).Select(ToArtifactDto),
        };
    }

    private static object ToCodingRunDto(CodingRunSnapshot run) => new
    {
        id = run.Id,
        localRunId = run.LocalRunId,
        run.ServerRunId,
        run.SessionId,
        run.MessageId,
        run.Status,
        codeDiff = run.CodeDiff,
        run.StartedAt,
        run.UpdatedAt,
        run.Revision,
        entries = run.Entries,
    };

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
        workflow.IsBuiltIn,
        workflow.Revision,
        tags = workflow.EffectiveTags,
    };

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

    private static string EventTypeFor(MessageStatus status) => status switch
    {
        MessageStatus.Cancelled => "chat.cancelled",
        MessageStatus.Failed => "chat.failed",
        MessageStatus.Interrupted => "chat.failed",
        _ => "chat.completed",
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

    public void Dispose()
    {
        campaigns?.DetachSinks();
        _activeChatCancellation?.Cancel();
        _activeChatCancellation?.Dispose();
        _chatGate.Dispose();
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
