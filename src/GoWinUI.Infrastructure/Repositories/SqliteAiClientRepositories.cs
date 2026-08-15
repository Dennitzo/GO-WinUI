using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using GoWinUI.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace GoWinUI.Infrastructure.Repositories;

public sealed class SqlitePromptTriggerRepository(SqliteDatabase database) : IPromptTriggerRepository
{
    public async Task<IReadOnlyList<PromptTrigger>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, action, phrase, description, match_mode, is_enabled, priority, revision, created_at, updated_at
            FROM prompt_triggers
            ORDER BY priority DESC, length(phrase) DESC, phrase COLLATE NOCASE;
            """;
        return await ReadTriggersAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PromptTrigger?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, action, phrase, description, match_mode, is_enabled, priority, revision, created_at, updated_at
            FROM prompt_triggers WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        return (await ReadTriggersAsync(command, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    public async Task<PromptTrigger> CreateAsync(PromptTrigger trigger, CancellationToken cancellationToken = default)
    {
        Validate(trigger);
        var now = DateTimeOffset.UtcNow;
        var created = trigger with
        {
            Id = trigger.Id == Guid.Empty ? Guid.NewGuid() : trigger.Id,
            Revision = 1,
            Phrase = trigger.Phrase.Trim(),
            Description = trigger.Description.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        await database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO prompt_triggers
                    (id, action, phrase, description, match_mode, is_enabled, priority, revision, created_at, updated_at)
                VALUES($id, $action, $phrase, $description, $mode, $enabled, $priority, 1, $created, $updated);
                """;
            Bind(command, created);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return created;
    }

    public async Task<PromptTrigger> UpdateAsync(
        PromptTrigger trigger,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        Validate(trigger);
        var updated = trigger with
        {
            Phrase = trigger.Phrase.Trim(),
            Description = trigger.Description.Trim(),
            Revision = checked(expectedRevision + 1),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var affected = await database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE prompt_triggers
                SET action=$action, phrase=$phrase, description=$description, match_mode=$mode,
                    is_enabled=$enabled, priority=$priority, revision=$revision, updated_at=$updated
                WHERE id=$id AND revision=$expected;
                """;
            Bind(command, updated);
            command.Parameters.AddWithValue("$expected", expectedRevision);
            return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new RevisionConflictException(nameof(PromptTrigger), trigger.Id);
        }
        return updated;
    }

    public async Task DeleteAsync(Guid id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var affected = await database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM prompt_triggers WHERE id=$id AND revision=$revision;";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$revision", expectedRevision);
            return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new RevisionConflictException(nameof(PromptTrigger), id);
        }
    }

    public async Task<PromptTriggerMatch?> MatchAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        var normalized = NormalizeForMatch(prompt.Trim());
        foreach (var trigger in await ListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!trigger.IsEnabled)
            {
                continue;
            }

            var phrase = NormalizeForMatch(trigger.Phrase.Trim());
            var index = trigger.MatchMode switch
            {
                PromptTriggerMatchMode.Exact when string.Equals(normalized, phrase, StringComparison.OrdinalIgnoreCase) => 0,
                PromptTriggerMatchMode.Prefix when StartsWithPhrase(normalized, phrase) => 0,
                PromptTriggerMatchMode.Contains => normalized.IndexOf(phrase, StringComparison.OrdinalIgnoreCase),
                _ => -1,
            };
            if (index < 0)
            {
                continue;
            }

            var remaining = trigger.MatchMode == PromptTriggerMatchMode.Exact
                ? string.Empty
                : normalized.Remove(index, phrase.Length)
                    .TrimStart(' ', '\t', '\r', '\n', ':', '-', '–', '—', '‑', ',', '.', '!', '?', ';')
                    .TrimEnd();
            return new PromptTriggerMatch(trigger, normalized, remaining);
        }
        return null;
    }

    private static string NormalizeForMatch(string value) => value
        .Replace('\u00A0', ' ')
        .Replace('\u2010', '-')
        .Replace('\u2011', '-')
        .Replace('\u2012', '-')
        .Replace('\u2013', '-')
        .Replace('\u2014', '-')
        .Replace('\u2212', '-');

    private static bool StartsWithPhrase(string prompt, string phrase)
    {
        if (!prompt.StartsWith(phrase, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return prompt.Length == phrase.Length
            || char.IsWhiteSpace(prompt[phrase.Length])
            || prompt[phrase.Length] is ':' or '-' or '–' or '—' or ',' or '.' or '!' or '?' or ';';
    }

    private static void Validate(PromptTrigger trigger)
    {
        if (string.IsNullOrWhiteSpace(trigger.Phrase) || trigger.Phrase.Trim().Length > 160)
        {
            throw new ArgumentException("Eine Triggerphrase muss 1 bis 160 Zeichen enthalten.", nameof(trigger));
        }
        if (trigger.Description.Trim().Length > 500)
        {
            throw new ArgumentException("Die Triggerbeschreibung darf höchstens 500 Zeichen enthalten.", nameof(trigger));
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(trigger.Priority, -10_000);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(trigger.Priority, 10_000);
    }

    private static void Bind(SqliteCommand command, PromptTrigger trigger)
    {
        command.Parameters.AddWithValue("$id", trigger.Id.ToString("D"));
        command.Parameters.AddWithValue("$action", ToStorage(trigger.Action));
        command.Parameters.AddWithValue("$phrase", trigger.Phrase);
        command.Parameters.AddWithValue("$description", trigger.Description);
        command.Parameters.AddWithValue("$mode", ToStorage(trigger.MatchMode));
        command.Parameters.AddWithValue("$enabled", trigger.IsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$priority", trigger.Priority);
        command.Parameters.AddWithValue("$revision", trigger.Revision);
        command.Parameters.AddWithValue("$created", Format(trigger.CreatedAt));
        command.Parameters.AddWithValue("$updated", Format(trigger.UpdatedAt));
    }

    private static async Task<IReadOnlyList<PromptTrigger>> ReadTriggersAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var items = new List<PromptTrigger>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new PromptTrigger(
                Guid.Parse(reader.GetString(0)),
                ParseEnum<PromptTriggerAction>(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                ParseEnum<PromptTriggerMatchMode>(reader.GetString(4)),
                reader.GetInt64(5) != 0,
                reader.GetInt32(6),
                reader.GetInt64(7),
                ParseDate(reader.GetString(8)),
                ParseDate(reader.GetString(9))));
        }
        return items;
    }

    internal static string ToStorage<T>(T value) where T : struct, Enum
    {
        var text = value.ToString();
        return $"{char.ToLowerInvariant(text[0])}{text[1..]}";
    }

    internal static T ParseEnum<T>(string value) where T : struct, Enum =>
        Enum.TryParse<T>(value, true, out var parsed)
            ? parsed
            : throw new InvalidDataException($"Unbekannter Datenbankwert '{value}' für {typeof(T).Name}.");

    internal static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    internal static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

public sealed class SqliteAssistantAttachmentRepository(
    SqliteDatabase database,
    IBinaryObjectStore blobs) : IAssistantAttachmentRepository
{
    public async Task<IReadOnlyList<AssistantAttachment>> ListAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, session_id, blob_id, file_name, content_type, sha256, length, created_at
            FROM assistant_attachments WHERE session_id=$session ORDER BY created_at;
            """;
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        return await ReadAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AssistantAttachment> ImportAsync(
        Guid sessionId,
        string fileName,
        string contentType,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(content);
        var safeFileName = NormalizeFileName(fileName);
        var blob = await blobs.ImportAsync(content, NormalizeContentType(contentType), cancellationToken).ConfigureAwait(false);
        var attachment = new AssistantAttachment(
            Guid.NewGuid(), sessionId, blob.Id, safeFileName, blob.ContentType,
            blob.Sha256, blob.Length, DateTimeOffset.UtcNow);
        try
        {
            await database.WriteAsync(async (connection, transaction, token) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO assistant_attachments
                        (id, session_id, blob_id, file_name, content_type, sha256, length, created_at)
                    VALUES($id, $session, $blob, $name, $type, $sha, $length, $created);
                    """;
                command.Parameters.AddWithValue("$id", attachment.Id.ToString("D"));
                command.Parameters.AddWithValue("$session", attachment.SessionId.ToString("D"));
                command.Parameters.AddWithValue("$blob", attachment.BlobId.ToString("D"));
                command.Parameters.AddWithValue("$name", attachment.FileName);
                command.Parameters.AddWithValue("$type", attachment.ContentType);
                command.Parameters.AddWithValue("$sha", attachment.Sha256);
                command.Parameters.AddWithValue("$length", attachment.Length);
                command.Parameters.AddWithValue("$created", SqlitePromptTriggerRepository.Format(attachment.CreatedAt));
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await blobs.DeleteIfUnreferencedAsync(blob.Id, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        return attachment;
    }

    public async Task<AssistantAttachment?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, session_id, blob_id, file_name, content_type, sha256, length, created_at
            FROM assistant_attachments WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        return (await ReadAsync(command, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    public async Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var blobId = await database.WriteAsync<Guid?>(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM assistant_attachments WHERE id=$id RETURNING blob_id;";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            var value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
            return value is string text && Guid.TryParse(text, out var parsed)
                ? parsed
                : null;
        }, cancellationToken).ConfigureAwait(false);

        // Captured media is promoted from a pending attachment to a message artifact
        // as soon as a run starts. A still-rendered chip (or a double click) may
        // therefore request deletion after the row has already gone. DELETE remains
        // intentionally idempotent while malformed IDs are rejected by the bridge.
        if (blobId is { } removedBlobId)
        {
            await blobs.DeleteIfUnreferencedAsync(removedBlobId, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlyList<AssistantAttachment>> ReadAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var items = new List<AssistantAttachment>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new AssistantAttachment(
                Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt64(6),
                SqlitePromptTriggerRepository.ParseDate(reader.GetString(7))));
        }
        return items;
    }

    internal static string NormalizeContentType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 128
            || !MediaTypeHeaderValue.TryParse(value, out var parsed)
            || string.IsNullOrWhiteSpace(parsed.MediaType))
        {
            return "application/octet-stream";
        }
        return parsed.MediaType.ToLowerInvariant();
    }

    internal static string NormalizeFileName(string value)
    {
        var name = Path.GetFileName(value.Trim());
        if (name.Length is 0 or > 240 || name.Any(char.IsControl))
        {
            throw new ArgumentException("Der Dateiname ist ungültig oder zu lang.", nameof(value));
        }
        return name;
    }
}

public sealed class SqliteChatArtifactRepository(
    SqliteDatabase database,
    IBinaryObjectStore blobs) : IChatArtifactRepository
{
    public async Task<IReadOnlyList<ChatArtifact>> ListForMessageAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE a.message_id=$message ORDER BY a.created_at;";
        command.Parameters.AddWithValue("$message", messageId.ToString("D"));
        return await ReadAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<ChatArtifact>>> ListForSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " JOIN chat_messages m ON m.id=a.message_id WHERE m.session_id=$session ORDER BY a.created_at;";
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        return (await ReadAsync(command, cancellationToken).ConfigureAwait(false))
            .GroupBy(static item => item.MessageId)
            .ToDictionary(static group => group.Key, static group => (IReadOnlyList<ChatArtifact>)group.ToArray());
    }

    public async Task<ChatArtifact?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE a.id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        return (await ReadAsync(command, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    public async Task<ChatArtifact> ImportAsync(
        Guid messageId,
        string serverArtifactId,
        string fileName,
        string contentType,
        string sha256,
        long length,
        string provider,
        IReadOnlyDictionary<string, string>? metadata,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverArtifactId);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        var safeFileName = SqliteAssistantAttachmentRepository.NormalizeFileName(fileName);
        var safeContentType = SqliteAssistantAttachmentRepository.NormalizeContentType(contentType);
        if (serverArtifactId.Length > 200 || provider.Length > 200 || length < 0)
        {
            throw new InvalidDataException("Die Serverartefakt-Metadaten überschreiten die Clientgrenzen.");
        }
        var existing = await FindByServerIdAsync(messageId, serverArtifactId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var blob = await blobs.ImportAsync(content, safeContentType, cancellationToken).ConfigureAwait(false);
        if (blob.Length != length || !string.Equals(blob.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
        {
            await blobs.DeleteIfUnreferencedAsync(blob.Id, CancellationToken.None).ConfigureAwait(false);
            throw new InvalidDataException("Das heruntergeladene Serverartefakt stimmt nicht mit seinen Prüfsummen überein.");
        }
        var artifact = new ChatArtifact(
            Guid.NewGuid(), messageId, blob.Id, serverArtifactId, safeFileName, safeContentType,
            blob.Sha256, blob.Length, provider, DateTimeOffset.UtcNow, metadata);
        try
        {
            await database.WriteAsync(async (connection, transaction, token) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO chat_artifacts
                        (id, message_id, blob_id, server_artifact_id, file_name, content_type, sha256, length, provider, metadata_json, created_at)
                    VALUES($id, $message, $blob, $server, $name, $type, $sha, $length, $provider, $metadata, $created);
                    """;
                command.Parameters.AddWithValue("$id", artifact.Id.ToString("D"));
                command.Parameters.AddWithValue("$message", artifact.MessageId.ToString("D"));
                command.Parameters.AddWithValue("$blob", artifact.BlobId.ToString("D"));
                command.Parameters.AddWithValue("$server", artifact.ServerArtifactId);
                command.Parameters.AddWithValue("$name", artifact.FileName);
                command.Parameters.AddWithValue("$type", artifact.ContentType);
                command.Parameters.AddWithValue("$sha", artifact.Sha256);
                command.Parameters.AddWithValue("$length", artifact.Length);
                command.Parameters.AddWithValue("$provider", artifact.Provider);
                command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(artifact.Metadata ?? new Dictionary<string, string>()));
                command.Parameters.AddWithValue("$created", SqlitePromptTriggerRepository.Format(artifact.CreatedAt));
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await blobs.DeleteIfUnreferencedAsync(blob.Id, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        return artifact;
    }

    private async Task<ChatArtifact?> FindByServerIdAsync(Guid messageId, string serverArtifactId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE a.message_id=$message AND a.server_artifact_id=$server;";
        command.Parameters.AddWithValue("$message", messageId.ToString("D"));
        command.Parameters.AddWithValue("$server", serverArtifactId);
        return (await ReadAsync(command, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    private static async Task<IReadOnlyList<ChatArtifact>> ReadAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var items = new List<ChatArtifact>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(10))
                ?? new Dictionary<string, string>();
            items.Add(new ChatArtifact(
                Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetInt64(7),
                reader.GetString(8), SqlitePromptTriggerRepository.ParseDate(reader.GetString(9)), metadata));
        }
        return items;
    }

    private const string SelectSql = """
        SELECT a.id, a.message_id, a.blob_id, a.server_artifact_id, a.file_name, a.content_type,
               a.sha256, a.length, a.provider, a.created_at, a.metadata_json
        FROM chat_artifacts a
        """;
}

public sealed class SqliteClientToolExecutionRepository(SqliteDatabase database) : IClientToolExecutionRepository
{
    private const int MaximumResultJsonLength = (4 * 1024 * 1024) + 65_536;
    public async Task<ClientToolExecutionRecord?> GetAsync(
        string proposalId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(proposalId, nameof(proposalId));
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE proposal_id=$proposal;";
        command.Parameters.AddWithValue("$proposal", proposalId);
        return await ReadSingleAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ClientToolExecutionRecord> BeginAsync(
        ClientToolExecutionRecord execution,
        CancellationToken cancellationToken = default)
    {
        Validate(execution);
        var now = DateTimeOffset.UtcNow;
        var started = execution with
        {
            State = "executing",
            ResultJson = null,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO client_tool_executions
                    (proposal_id, local_run_id, server_run_id, event_id, tool_name, state, result_json, created_at, updated_at)
                VALUES($proposal, $localRun, $serverRun, $event, $tool, 'executing', NULL, $created, $updated);
                """;
            BindIdentity(command, started);
            command.Parameters.AddWithValue("$created", SqlitePromptTriggerRepository.Format(started.CreatedAt));
            command.Parameters.AddWithValue("$updated", SqlitePromptTriggerRepository.Format(started.UpdatedAt));
            _ = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        var stored = await GetAsync(started.ProposalId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Das lokale Client-Tooljournal konnte nicht angelegt werden.");
        if (stored.LocalRunId != started.LocalRunId
            || !string.Equals(stored.ServerRunId, started.ServerRunId, StringComparison.Ordinal)
            || stored.EventId != started.EventId
            || !string.Equals(stored.ToolName, started.ToolName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Eine Client-Tool-ID wurde mit abweichenden Laufdaten wiederverwendet.");
        }
        return stored;
    }

    public async Task<ClientToolExecutionRecord> CompleteAsync(
        string proposalId,
        string resultJson,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(proposalId, nameof(proposalId));
        if (string.IsNullOrWhiteSpace(resultJson) || resultJson.Length > MaximumResultJsonLength)
        {
            throw new InvalidDataException("Das lokale Client-Toolergebnis ist leer oder zu groß.");
        }
        using (JsonDocument.Parse(resultJson))
        {
            // Persist only syntactically valid JSON so a resumed run is always deserializable.
        }
        var affected = await database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE client_tool_executions
                SET state='completed', result_json=$result, updated_at=$updated
                WHERE proposal_id=$proposal AND state IN ('executing','completed');
                """;
            command.Parameters.AddWithValue("$proposal", proposalId);
            command.Parameters.AddWithValue("$result", resultJson);
            command.Parameters.AddWithValue("$updated", SqlitePromptTriggerRepository.Format(DateTimeOffset.UtcNow));
            return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new InvalidOperationException("Das lokale Client-Tooljournal ist nicht mehr im abschließbaren Zustand.");
        }
        return await GetAsync(proposalId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Das abgeschlossene Client-Tooljournal wurde nicht gefunden.");
    }

    public async Task MarkSubmittedAsync(string proposalId, CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(proposalId, nameof(proposalId));
        var affected = await database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE client_tool_executions
                SET state='submitted', updated_at=$updated
                WHERE proposal_id=$proposal AND state IN ('completed','submitted') AND result_json IS NOT NULL;
                """;
            command.Parameters.AddWithValue("$proposal", proposalId);
            command.Parameters.AddWithValue("$updated", SqlitePromptTriggerRepository.Format(DateTimeOffset.UtcNow));
            return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new InvalidOperationException("Das lokale Client-Toolergebnis kann nicht als übertragen markiert werden.");
        }
    }

    private static async Task<ClientToolExecutionRecord?> ReadSingleAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        return new ClientToolExecutionRecord(
            reader.GetString(0),
            Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            SqlitePromptTriggerRepository.ParseDate(reader.GetString(7)),
            SqlitePromptTriggerRepository.ParseDate(reader.GetString(8)));
    }

    private static void Validate(ClientToolExecutionRecord execution)
    {
        ValidateIdentifier(execution.ProposalId, nameof(execution.ProposalId));
        ValidateIdentifier(execution.ServerRunId, nameof(execution.ServerRunId));
        ValidateIdentifier(execution.ToolName, nameof(execution.ToolName));
        if (execution.LocalRunId == Guid.Empty || execution.EventId < 0)
        {
            throw new ArgumentException("Die Client-Tool-Laufzuordnung ist ungültig.", nameof(execution));
        }
    }

    private static void ValidateIdentifier(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200 || value.Any(char.IsControl))
        {
            throw new ArgumentException($"Der Client-Toolwert '{name}' ist ungültig.", name);
        }
    }

    private static void BindIdentity(SqliteCommand command, ClientToolExecutionRecord execution)
    {
        command.Parameters.AddWithValue("$proposal", execution.ProposalId);
        command.Parameters.AddWithValue("$localRun", execution.LocalRunId.ToString("D"));
        command.Parameters.AddWithValue("$serverRun", execution.ServerRunId);
        command.Parameters.AddWithValue("$event", execution.EventId);
        command.Parameters.AddWithValue("$tool", execution.ToolName);
    }

    private const string SelectSql = """
        SELECT proposal_id, local_run_id, server_run_id, event_id, tool_name, state,
               result_json, created_at, updated_at
        FROM client_tool_executions
        """;
}

public sealed class SqliteGoAiRunRepository(SqliteDatabase database) : IGoAiRunRepository
{
    public async Task<GoAiRunRecord> CreateAsync(GoAiRunRecord run, CancellationToken cancellationToken = default)
    {
        await database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO go_ai_runs
                    (id, session_id, assistant_message_id, action, idempotency_key, server_run_id,
                     last_event_id, state, selected_model, error_code, created_at, updated_at)
                VALUES($id, $session, $message, $action, $key, $server, $event, $state, $model, $error, $created, $updated);
                """;
            Bind(command, run);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return run;
    }

    public Task<GoAiRunRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        ReadSingleAsync("r.id=$value", id.ToString("D"), cancellationToken);

    public Task<GoAiRunRecord?> GetByServerRunIdAsync(string serverRunId, CancellationToken cancellationToken = default) =>
        ReadSingleAsync("r.server_run_id=$value", serverRunId, cancellationToken);

    public async Task<IReadOnlyList<GoAiRunRecord>> ListResumableAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE r.server_run_id IS NOT NULL AND r.state IN ('queued','running','waitingForClient') ORDER BY r.updated_at;";
        return await ReadAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(
        Guid id,
        string? serverRunId,
        long lastEventId,
        string state,
        string? selectedModel = null,
        string? errorCode = null,
        CancellationToken cancellationToken = default)
    {
        await database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE go_ai_runs
                SET server_run_id=COALESCE($server, server_run_id),
                    last_event_id=CASE WHEN $event > last_event_id THEN $event ELSE last_event_id END,
                    state=$state,
                    selected_model=COALESCE($model, selected_model),
                    error_code=$error,
                    updated_at=$updated
                WHERE id=$id;
                """;
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$server", (object?)serverRunId ?? DBNull.Value);
            command.Parameters.AddWithValue("$event", lastEventId);
            command.Parameters.AddWithValue("$state", state);
            command.Parameters.AddWithValue("$model", (object?)selectedModel ?? DBNull.Value);
            command.Parameters.AddWithValue("$error", (object?)errorCode ?? DBNull.Value);
            command.Parameters.AddWithValue("$updated", SqlitePromptTriggerRepository.Format(DateTimeOffset.UtcNow));
            if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("Der lokale GO-AI-Lauf wurde nicht gefunden.");
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GoAiRunRecord?> ReadSingleAsync(string predicate, object value, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql + $" WHERE {predicate};";
        command.Parameters.AddWithValue("$value", value);
        return (await ReadAsync(command, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    private static void Bind(SqliteCommand command, GoAiRunRecord run)
    {
        command.Parameters.AddWithValue("$id", run.Id.ToString("D"));
        command.Parameters.AddWithValue("$session", run.SessionId.ToString("D"));
        command.Parameters.AddWithValue("$message", run.AssistantMessageId.ToString("D"));
        command.Parameters.AddWithValue("$action", run.Action is null ? DBNull.Value : SqlitePromptTriggerRepository.ToStorage(run.Action.Value));
        command.Parameters.AddWithValue("$key", run.IdempotencyKey);
        command.Parameters.AddWithValue("$server", (object?)run.ServerRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$event", run.LastEventId);
        command.Parameters.AddWithValue("$state", run.State);
        command.Parameters.AddWithValue("$model", (object?)run.SelectedModel ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)run.ErrorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", SqlitePromptTriggerRepository.Format(run.CreatedAt));
        command.Parameters.AddWithValue("$updated", SqlitePromptTriggerRepository.Format(run.UpdatedAt));
    }

    private static async Task<IReadOnlyList<GoAiRunRecord>> ReadAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var items = new List<GoAiRunRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new GoAiRunRecord(
                Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)),
                reader.IsDBNull(3) ? null : SqlitePromptTriggerRepository.ParseEnum<PromptTriggerAction>(reader.GetString(3)),
                reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetInt64(6), reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9),
                SqlitePromptTriggerRepository.ParseDate(reader.GetString(10)),
                SqlitePromptTriggerRepository.ParseDate(reader.GetString(11))));
        }
        return items;
    }

    private const string SelectSql = """
        SELECT r.id, r.session_id, r.assistant_message_id, r.action, r.idempotency_key, r.server_run_id,
               r.last_event_id, r.state, r.selected_model, r.error_code, r.created_at, r.updated_at
        FROM go_ai_runs r
        """;
}
