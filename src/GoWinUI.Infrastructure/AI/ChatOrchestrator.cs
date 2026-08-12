using System.Text;
using GoWinUI.Core.Chat;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using GoWinUI.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GoWinUI.Infrastructure.AI;

public sealed partial class ChatOrchestrator(
    IChatRepository chats,
    IDocumentIngestor documents,
    ILmStudioClient lmStudio,
    IContextAssembler contextAssembler,
    SqliteDatabase database,
    ILogger<ChatOrchestrator>? suppliedLogger = null) : IChatOrchestrator, IDisposable
{
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly ILogger<ChatOrchestrator> _logger = suppliedLogger ?? NullLogger<ChatOrchestrator>.Instance;
    private CancellationTokenSource? _activeRun;
    public event EventHandler<ChatStreamUpdate>? StreamUpdated;
    public bool IsRunning => _runLock.CurrentCount == 0;

    public async Task<ChatMessage> SendAsync(Guid sessionId, string prompt, string model, string systemPrompt, string? reasoningEffort = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        if (!await _runLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Es läuft bereits eine LM-Studio-Antwort.");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeRun = linked;
        ChatMessage? assistant = null;
        Guid? runId = null;
        var accumulated = new StringBuilder();
        try
        {
            _ = await chats.GetSessionAsync(sessionId, linked.Token).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Sitzung '{sessionId}' wurde nicht gefunden.");
            var history = await chats.ListMessagesAsync(sessionId, linked.Token).ConfigureAwait(false);
            _ = await chats.AddMessageAsync(sessionId, ChatRole.User, prompt.Trim(), MessageStatus.Completed, linked.Token).ConfigureAwait(false);
            assistant = await chats.AddMessageAsync(sessionId, ChatRole.Assistant, string.Empty, MessageStatus.Streaming, linked.Token).ConfigureAwait(false);

            var pages = new List<DocumentPage>();
            foreach (var document in await documents.ListAsync(sessionId, linked.Token).ConfigureAwait(false))
                pages.AddRange(await documents.ReadPagesAsync(document.Id, linked.Token).ConfigureAwait(false));

            var models = await lmStudio.ListModelsAsync(linked.Token).ConfigureAwait(false);
            var contextLength = models.FirstOrDefault(candidate => candidate.Id == model)?.ContextLength ?? 8_192;
            var context = contextAssembler.Build(new(systemPrompt, prompt.Trim(), history, null, pages, contextLength));
            runId = Guid.NewGuid();
            await SaveRunStartedAsync(database, runId.Value, sessionId, assistant.Id, model, reasoningEffort, context, linked.Token).ConfigureAwait(false);
            RunStarted(_logger, sessionId, model);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                GeneralEnvelopePrepared(
                    _logger,
                    context.PolicyReferences?.Count ?? 0,
                    context.MaxOutputTokens);
            }
            StreamUpdated?.Invoke(this, new(
                sessionId,
                assistant.Id,
                string.Empty,
                string.Empty,
                MessageStatus.Streaming,
                context.EstimatedTokens,
                contextLength,
                context.WasTruncated,
                context.TruncationNotice));
            var request = new LmChatRequest(
                model,
                context.Messages,
                reasoningEffort,
                context.MaxOutputTokens,
                RequireJsonObject: true);
            await foreach (var delta in lmStudio.StreamAsync(request, linked.Token).ConfigureAwait(false))
            {
                if (delta.Text.Length > 0)
                {
                    accumulated.Append(delta.Text);
                }
            }

            var parsedResponse = GeneralAgentResponseParser.Parse(accumulated.ToString(), prompt);
            var finalContent = parsedResponse.Message;
            if (string.IsNullOrWhiteSpace(finalContent))
            {
                throw new InvalidDataException("Die lokale AI hat keine sichtbare Antwort erzeugt.");
            }

            if (!parsedResponse.IsStructured)
            {
                ResponseContractFallback(_logger, sessionId);
            }

            await chats.UpdateMessageAsync(assistant.Id, finalContent, MessageStatus.Completed, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            await chats.RenameSessionAsync(sessionId, parsedResponse.SessionTitle, CancellationToken.None).ConfigureAwait(false);
            await SaveRunFinishedAsync(database, runId, MessageStatus.Completed, null).ConfigureAwait(false);
            RunCompleted(_logger, sessionId);
            StreamUpdated?.Invoke(this, new(sessionId, assistant.Id, string.Empty, finalContent, MessageStatus.Completed));
            return assistant with { Content = finalContent, Status = MessageStatus.Completed, UpdatedAt = DateTimeOffset.UtcNow };
        }
        catch (OperationCanceledException) when (assistant is not null)
        {
            var partialContent = GeneralAgentResponseParser.VisiblePartial(accumulated.ToString());
            await chats.UpdateMessageAsync(assistant.Id, partialContent, MessageStatus.Cancelled, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            await SaveRunFinishedAsync(database, runId, MessageStatus.Cancelled, null).ConfigureAwait(false);
            RunCancelled(_logger, sessionId);
            StreamUpdated?.Invoke(this, new(sessionId, assistant.Id, string.Empty, partialContent, MessageStatus.Cancelled));
            return assistant with { Content = partialContent, Status = MessageStatus.Cancelled, UpdatedAt = DateTimeOffset.UtcNow };
        }
        catch (Exception exception) when (assistant is not null)
        {
            var partialContent = GeneralAgentResponseParser.VisiblePartial(accumulated.ToString());
            await chats.UpdateMessageAsync(assistant.Id, partialContent, MessageStatus.Failed, exception.Message, CancellationToken.None).ConfigureAwait(false);
            await SaveRunFinishedAsync(database, runId, MessageStatus.Failed, exception.Message).ConfigureAwait(false);
            RunFailed(_logger, exception, sessionId);
            StreamUpdated?.Invoke(this, new(sessionId, assistant.Id, string.Empty, partialContent, MessageStatus.Failed));
            throw;
        }
        finally
        {
            _activeRun = null;
            _runLock.Release();
        }
    }

    public void Cancel() => _activeRun?.Cancel();

    public void Dispose()
    {
        _activeRun?.Cancel();
        _activeRun?.Dispose();
        _runLock.Dispose();
    }

    private static Task SaveRunStartedAsync(SqliteDatabase database, Guid runId, Guid sessionId, Guid messageId, string model, string? reasoningEffort, ContextBuildResult context, CancellationToken cancellationToken) =>
        database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO chat_runs(id,session_id,assistant_message_id,model,api_endpoint,reasoning_effort,status,
                    estimated_context_tokens,context_was_truncated,started_at)
                VALUES($id,$session,$message,$model,'auto',$reasoning,'streaming',$tokens,$truncated,$started);
                """;
            command.Parameters.AddWithValue("$id", runId.ToString("D"));
            command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
            command.Parameters.AddWithValue("$message", messageId.ToString("D"));
            command.Parameters.AddWithValue("$model", model);
            command.Parameters.AddWithValue("$reasoning", (object?)reasoningEffort ?? DBNull.Value);
            command.Parameters.AddWithValue("$tokens", context.EstimatedTokens);
            command.Parameters.AddWithValue("$truncated", context.WasTruncated ? 1 : 0);
            command.Parameters.AddWithValue("$started", DateTimeOffset.UtcNow.ToDb());
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    private static Task SaveRunFinishedAsync(SqliteDatabase database, Guid? runId, MessageStatus status, string? errorMessage)
    {
        if (runId is null) return Task.CompletedTask;
        return database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE chat_runs SET status=$status,completed_at=$completed,error=$error WHERE id=$id;";
            command.Parameters.AddWithValue("$id", runId.Value.ToString("D"));
            command.Parameters.AddWithValue("$status", SqliteMapping.EnumName(status));
            command.Parameters.AddWithValue("$completed", DateTimeOffset.UtcNow.ToDb());
            command.Parameters.AddWithValue("$error", (object?)errorMessage ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, CancellationToken.None);
    }

    [LoggerMessage(EventId = 2200, Level = LogLevel.Information, Message = "LM Studio chat run started for session {SessionId} with model {Model}")]
    private static partial void RunStarted(ILogger logger, Guid sessionId, string model);

    [LoggerMessage(EventId = 2201, Level = LogLevel.Information, Message = "LM Studio chat run completed for session {SessionId}")]
    private static partial void RunCompleted(ILogger logger, Guid sessionId);

    [LoggerMessage(EventId = 2202, Level = LogLevel.Information, Message = "LM Studio chat run cancelled for session {SessionId}")]
    private static partial void RunCancelled(ILogger logger, Guid sessionId);

    [LoggerMessage(EventId = 2203, Level = LogLevel.Warning, Message = "LM Studio chat run failed for session {SessionId}")]
    private static partial void RunFailed(ILogger logger, Exception exception, Guid sessionId);

    [LoggerMessage(EventId = 2204, Level = LogLevel.Information, Message = "General chat envelope prepared with {PolicyCount} policies and output limit {MaxOutputTokens}")]
    private static partial void GeneralEnvelopePrepared(ILogger logger, int policyCount, int maxOutputTokens);

    [LoggerMessage(EventId = 2205, Level = LogLevel.Warning, Message = "LM Studio response for session {SessionId} did not follow the structured message/session-title contract; visible fallback was used")]
    private static partial void ResponseContractFallback(ILogger logger, Guid sessionId);
}
