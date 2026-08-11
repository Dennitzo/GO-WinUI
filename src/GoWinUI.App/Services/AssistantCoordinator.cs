using GoWinUI.App.ViewModels;
using GoWinUI.Core.Contracts;
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
    SettingsCoordinator settings,
    ShellViewModel shell) : IDisposable
{
    private const string DefaultSystemPrompt = "Du bist GO, ein hilfreicher lokaler AI-Assistent. Antworte klar, korrekt und in der Sprache des Benutzers. Weise auf Unsicherheit hin und erfinde keine Dokumentquellen.";
    private readonly SemaphoreSlim _chatGate = new(1, 1);
    private CancellationTokenSource? _activeChatCancellation;

    public Task SaveDraftAsync(Guid sessionId, string draft, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(draft.Length, 100_000);
        return chats.SaveDraftAsync(sessionId, draft, cancellationToken);
    }

    public async Task<object> BuildSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var session = await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false);
        var sessions = await chats.ListSessionsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var messages = await chats.ListMessagesAsync(session.Id, cancellationToken).ConfigureAwait(false);
        var workflowItems = await workflows.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var documentItems = await documents.ListAsync(session.Id, cancellationToken).ConfigureAwait(false);
        var pages = new List<DocumentPage>();
        foreach (var document in documentItems)
        {
            pages.AddRange(await documents.ReadPagesAsync(document.Id, cancellationToken).ConfigureAwait(false));
        }

        var contextLimit = await ResolveContextLimitAsync(cancellationToken).ConfigureAwait(false);
        var selectedWorkflow = session.SelectedWorkflowId is { } workflowId
            ? workflowItems.FirstOrDefault(workflow => workflow.Id == workflowId)
            : null;
        var context = contextAssembler.Build(new(
            DefaultSystemPrompt,
            string.IsNullOrWhiteSpace(session.Draft) ? "Nächste Benutzereingabe" : session.Draft,
            messages,
            selectedWorkflow,
            pages,
            contextLimit));
        return new
        {
            sessions = sessions.Select(ToSessionDto),
            messages = messages.Select(ToMessageDto),
            workflows = workflowItems.Select(ToWorkflowDto),
            documents = documentItems.Select(ToDocumentDto),
            activeSessionId = session.Id,
            selectedWorkflowId = session.SelectedWorkflowId,
            draft = session.Draft,
            isRunning = orchestrator.IsRunning,
            model = settings.Current.SelectedModel,
            reasoningEffort = settings.Current.ReasoningEffort,
            contextUsed = context.EstimatedTokens,
            contextLimit,
            contextWasTruncated = context.WasTruncated,
            contextNotice = context.TruncationNotice,
        };
    }

    public async Task HandleAsync(
        WebBridgeEnvelope envelope,
        Func<string, object, string?, Task> emit,
        CancellationToken cancellationToken = default)
    {
        switch (envelope.Type)
        {
            case "app.ready":
                await emit("state.snapshot", await BuildSnapshotAsync(cancellationToken), envelope.RequestId);
                break;
            case "session.create":
                await CreateSessionAsync(emit, envelope.RequestId, cancellationToken);
                break;
            case "session.open":
                await OpenSessionAsync(GetRequiredGuid(envelope.Payload, "sessionId"), emit, envelope.RequestId, cancellationToken);
                break;
            case "session.rename":
                await chats.RenameSessionAsync(
                    GetRequiredGuid(envelope.Payload, "sessionId"),
                    GetRequiredString(envelope.Payload, "title", 160),
                    cancellationToken);
                await emit("session.changed", await BuildSnapshotAsync(cancellationToken), envelope.RequestId);
                break;
            case "session.delete":
                await DeleteSessionAsync(GetRequiredGuid(envelope.Payload, "sessionId"), emit, envelope.RequestId, cancellationToken);
                break;
            case "session.draft":
                await chats.SaveDraftAsync(
                    GetRequiredGuid(envelope.Payload, "sessionId"),
                    GetOptionalString(envelope.Payload, "draft", 100_000) ?? string.Empty,
                    cancellationToken);
                await emit("draft.saved", new { }, envelope.RequestId);
                break;
            case "chat.send":
                await SendChatAsync(envelope, emit, cancellationToken);
                break;
            case "chat.cancel":
                _activeChatCancellation?.Cancel();
                orchestrator.Cancel();
                break;
            case "document.remove":
                await documents.RemoveAsync(GetRequiredGuid(envelope.Payload, "documentId"), cancellationToken);
                await emit("document.changed", await BuildSnapshotAsync(cancellationToken), envelope.RequestId);
                break;
            case "workflow.list":
                await ListWorkflowsAsync(envelope, emit, cancellationToken);
                break;
            case "workflow.select":
                await SelectWorkflowAsync(GetOptionalGuid(envelope.Payload, "workflowId"), emit, envelope.RequestId, cancellationToken);
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
            case "workflow.clone":
                await workflows.CloneAsync(
                    GetRequiredGuid(envelope.Payload, "workflowId"),
                    GetRequiredString(envelope.Payload, "title", 160),
                    cancellationToken);
                await emit("workflow.changed", await BuildSnapshotAsync(cancellationToken), envelope.RequestId);
                break;
            case "workflow.createFromMessage":
                await CreateWorkflowFromMessageAsync(envelope, emit, cancellationToken);
                break;
        }
    }

    public async Task ImportDocumentAsync(
        Guid sessionId,
        string fileName,
        Stream content,
        CancellationToken cancellationToken = default)
    {
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
    }

    public IReadOnlySet<string> SupportedDocumentExtensions => documents.SupportedExtensions;

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
            : await chats.CreateSessionAsync("Neuer Chat", cancellationToken).ConfigureAwait(false);
        await settings.UpdateAsync(current => current with { ActiveSessionId = session.Id }, cancellationToken).ConfigureAwait(false);
        return session;
    }

    private async Task CreateSessionAsync(
        Func<string, object, string?, Task> emit,
        string requestId,
        CancellationToken cancellationToken)
    {
        var session = await chats.CreateSessionAsync("Neuer Chat", cancellationToken).ConfigureAwait(false);
        await settings.UpdateAsync(current => current with { ActiveSessionId = session.Id }, cancellationToken).ConfigureAwait(false);
        await emit("session.changed", await BuildSnapshotAsync(cancellationToken), requestId);
    }

    private async Task OpenSessionAsync(
        Guid sessionId,
        Func<string, object, string?, Task> emit,
        string requestId,
        CancellationToken cancellationToken)
    {
        _ = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die Sitzung wurde nicht gefunden.");
        await settings.UpdateAsync(current => current with { ActiveSessionId = sessionId }, cancellationToken).ConfigureAwait(false);
        await emit("session.changed", await BuildSnapshotAsync(cancellationToken), requestId);
    }

    private async Task DeleteSessionAsync(
        Guid sessionId,
        Func<string, object, string?, Task> emit,
        string requestId,
        CancellationToken cancellationToken)
    {
        await chats.DeleteSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (settings.Current.ActiveSessionId == sessionId)
        {
            await settings.UpdateAsync(current => current with { ActiveSessionId = null }, cancellationToken).ConfigureAwait(false);
        }

        _ = await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false);
        await emit("session.changed", await BuildSnapshotAsync(cancellationToken), requestId);
    }

    private async Task SendChatAsync(
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
            var model = await ResolveModelAsync(cancellationToken).ConfigureAwait(false);
            var reasoning = GetOptionalString(envelope.Payload, "reasoningEffort", 20)
                ?? settings.Current.ReasoningEffort;
            await settings.UpdateAsync(current => current with
            {
                ActiveSessionId = sessionId,
                ReasoningEffort = reasoning,
            }, cancellationToken).ConfigureAwait(false);
            await chats.SaveDraftAsync(sessionId, string.Empty, cancellationToken).ConfigureAwait(false);

            var session = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Die Sitzung wurde nicht gefunden.");
            if (string.Equals(session.Title, "Neuer Chat", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(prompt))
            {
                var title = prompt.Length <= 60 ? prompt : $"{prompt[..57]}…";
                await chats.RenameSessionAsync(sessionId, title, cancellationToken).ConfigureAwait(false);
            }

            _activeChatCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            shell.IsAiRunning = true;
            shell.LmStudioStatus = $"{model} antwortet";
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
                await emit(EventTypeFor(completed.Status), new
                {
                    message = ToMessageDto(completed),
                    error = completed.Error,
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

            await emit("chat.failed", new
            {
                message = final is null ? null : ToMessageDto(final),
                error = exception.Message,
            }, envelope.RequestId);
        }
        finally
        {
            _activeChatCancellation?.Dispose();
            _activeChatCancellation = null;
            shell.IsAiRunning = false;
            shell.LmStudioStatus = settings.Current.SelectedModel ?? "Nicht verbunden";
            _chatGate.Release();
        }
    }

    private static async Task<bool> EmitStreamUpdateAsync(
        ChatStreamUpdate update,
        bool started,
        Func<string, object, string?, Task> emit,
        string requestId)
    {
        if (!started)
        {
            started = true;
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

    private async Task<int> ResolveContextLimitAsync(CancellationToken cancellationToken)
    {
        try
        {
            var models = await lmStudio.ListModelsAsync(cancellationToken).ConfigureAwait(false);
            var selected = settings.Current.SelectedModel;
            var model = string.IsNullOrWhiteSpace(selected)
                ? models.Count == 1 ? models[0] : null
                : models.FirstOrDefault(candidate => string.Equals(candidate.Id, selected, StringComparison.Ordinal));
            return model?.ContextLength is >= 2_048 and <= 10_000_000
                ? model.ContextLength.Value
                : 8_192;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return 8_192;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            return 8_192;
        }
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

    private async Task SelectWorkflowAsync(
        Guid? workflowId,
        Func<string, object, string?, Task> emit,
        string requestId,
        CancellationToken cancellationToken)
    {
        var session = await EnsureActiveSessionAsync(cancellationToken).ConfigureAwait(false);
        await chats.SelectWorkflowAsync(session.Id, workflowId, cancellationToken).ConfigureAwait(false);
        await emit("workflow.changed", await BuildSnapshotAsync(cancellationToken), requestId);
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
        var titleSeed = message.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "Workflow aus Chat";
        var title = titleSeed.Length <= 100 ? titleSeed : string.Concat(titleSeed.AsSpan(0, 97), "…");
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
                contextSummary = message.Content,
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
        session.UpdatedAt,
    };

    private static object ToMessageDto(ChatMessage message) => new
    {
        id = message.Id,
        sessionId = message.SessionId,
        role = message.Role.ToString().ToLowerInvariant(),
        message.Content,
        status = message.Status.ToString().ToLowerInvariant(),
        message.CreatedAt,
        message.UpdatedAt,
        message.Error,
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
