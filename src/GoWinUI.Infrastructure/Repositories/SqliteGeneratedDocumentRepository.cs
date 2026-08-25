using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using GoWinUI.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace GoWinUI.Infrastructure.Repositories;

public sealed class SqliteGeneratedDocumentRepository(SqliteDatabase database) : IGeneratedDocumentRepository
{
    public async Task<IReadOnlyList<GeneratedDocument>> ListAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE session_id=$session ORDER BY updated_at,id;";
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        return await ReadAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GeneratedDocument?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        return (await ReadAsync(command, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    public async Task<GeneratedDocument> CreateAsync(
        GeneratedDocument document,
        CancellationToken cancellationToken = default)
    {
        Validate(document);
        await database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO generated_documents
                    (id,session_id,file_name,format,source_markdown,sha256,revision,created_at,updated_at)
                VALUES($id,$session,$name,$format,$source,$sha,$revision,$created,$updated);
                """;
            Bind(command, document);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return document;
    }

    public async Task<GeneratedDocument> UpdateAsync(
        Guid id,
        string sourceMarkdown,
        string sha256,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var current = await GetAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("Das erzeugte Sitzungsdokument wurde nicht gefunden.");
        var updated = current with
        {
            SourceMarkdown = sourceMarkdown,
            Sha256 = sha256,
            Revision = checked(expectedRevision + 1),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        Validate(updated);
        var affected = await database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE generated_documents
                SET source_markdown=$source,sha256=$sha,revision=$revision,updated_at=$updated
                WHERE id=$id AND revision=$expected;
                """;
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$source", updated.SourceMarkdown);
            command.Parameters.AddWithValue("$sha", updated.Sha256);
            command.Parameters.AddWithValue("$revision", updated.Revision);
            command.Parameters.AddWithValue("$updated", SqlitePromptTriggerRepository.Format(updated.UpdatedAt));
            command.Parameters.AddWithValue("$expected", expectedRevision);
            return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new RevisionConflictException(nameof(GeneratedDocument), id);
        }
        return updated;
    }

    private static async Task<IReadOnlyList<GeneratedDocument>> ReadAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var result = new List<GeneratedDocument>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new GeneratedDocument(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt64(6),
                SqlitePromptTriggerRepository.ParseDate(reader.GetString(7)),
                SqlitePromptTriggerRepository.ParseDate(reader.GetString(8))));
        }
        return result;
    }

    private static void Bind(SqliteCommand command, GeneratedDocument document)
    {
        command.Parameters.AddWithValue("$id", document.Id.ToString("D"));
        command.Parameters.AddWithValue("$session", document.SessionId.ToString("D"));
        command.Parameters.AddWithValue("$name", document.FileName);
        command.Parameters.AddWithValue("$format", document.Format);
        command.Parameters.AddWithValue("$source", document.SourceMarkdown);
        command.Parameters.AddWithValue("$sha", document.Sha256);
        command.Parameters.AddWithValue("$revision", document.Revision);
        command.Parameters.AddWithValue("$created", SqlitePromptTriggerRepository.Format(document.CreatedAt));
        command.Parameters.AddWithValue("$updated", SqlitePromptTriggerRepository.Format(document.UpdatedAt));
    }

    private static void Validate(GeneratedDocument document)
    {
        if (document.Id == Guid.Empty || document.SessionId == Guid.Empty
            || string.IsNullOrWhiteSpace(document.FileName) || document.FileName.Length > 240
            || Path.GetFileName(document.FileName) != document.FileName
            || document.Format is not ("markdown" or "text" or "docx" or "pdf")
            || document.SourceMarkdown.Length > 4 * 1024 * 1024
            || document.Sha256.Length != 64 || document.Revision < 1)
        {
            throw new InvalidDataException("Das erzeugte Sitzungsdokument ist ungültig.");
        }
    }

    private const string SelectSql = """
        SELECT id,session_id,file_name,format,source_markdown,sha256,revision,created_at,updated_at
        FROM generated_documents
        """;
}
