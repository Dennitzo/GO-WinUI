using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using GoWinUI.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace GoWinUI.Infrastructure.Documents;

public sealed partial class DocumentIngestor(SqliteDatabase database, IBinaryObjectStore blobStore) : IDocumentIngestor
{
    private const int IndexSchemaVersion = 1;
    private const string IndexModelProfile = "local-hybrid-v1";
    private const int ChunkCharacters = 4_000;
    private const int ChunkOverlap = 400;
    private static readonly JsonSerializerOptions CacheJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".txt", ".md", ".csv", ".json", ".xml", ".html", ".htm", ".rtf", ".log", ".ini",
        ".yaml", ".yml", ".css", ".js", ".ts", ".py", ".cpp", ".c", ".h", ".hpp", ".cs", ".java", ".sql",
    };

    public IReadOnlySet<string> SupportedExtensions => Extensions;

    public async Task<DocumentIngestResult> ImportAsync(Guid sessionId, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(content);
        var safeName = Path.GetFileName(fileName);
        var extension = Path.GetExtension(safeName);
        if (extension.Equals(".doc", StringComparison.OrdinalIgnoreCase))
        {
            return new(null, false, "Das alte binäre DOC-Format wird nicht unterstützt. Bitte als DOCX oder Text speichern.", false);
        }

        if (!Extensions.Contains(extension))
        {
            return new(null, false, $"Der Dateityp '{extension}' wird nicht unterstützt.", false);
        }

        var contentType = GetContentType(extension);
        BinaryObjectDescriptor blob;
        try
        {
            blob = await blobStore.ImportAsync(content, contentType, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(null, false, "Die Datei konnte nicht sicher in die lokale Datenbank importiert werden.", false);
        }

        StoredDocument? existingBinding;
        IReadOnlyList<string> cachedPages;
        try
        {
            existingBinding = await FindSessionDocumentAsync(sessionId, blob.Sha256, cancellationToken).ConfigureAwait(false);
            cachedPages = existingBinding is null
                ? await ReadCachedPagesAsync(blob.Sha256, cancellationToken).ConfigureAwait(false)
                : [];
        }
        catch (OperationCanceledException)
        {
            await blobStore.DeleteIfUnreferencedAsync(blob.Id, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        if (existingBinding is not null)
        {
            return new(existingBinding with { WasReused = true }, true, null, existingBinding.PageCount > 0);
        }

        var temporaryPath = Path.Combine(Path.GetTempPath(), $"GO-{Guid.NewGuid():N}{extension}");
        IReadOnlyList<string> pages;
        try
        {
            if (cachedPages.Count > 0)
            {
                pages = cachedPages;
            }
            else
            {
                await using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await blobStore.ExportAsync(blob.Id, file, cancellationToken).ConfigureAwait(false);
                }

                pages = await ExtractAsync(temporaryPath, extension, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await blobStore.DeleteIfUnreferencedAsync(blob.Id, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await blobStore.DeleteIfUnreferencedAsync(blob.Id, CancellationToken.None).ConfigureAwait(false);
            return new(null, false, $"'{safeName}' konnte nicht gelesen werden: {exception.Message}", false);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Der Betriebssystem-Tempordner räumt eine noch kurz gesperrte Parserdatei später auf.
            }
        }

        var now = DateTimeOffset.UtcNow;
        var document = new StoredDocument(Guid.NewGuid(), sessionId, blob.Id, safeName, contentType, blob.Sha256, blob.Length, pages.Count, now);
        try
        {
            await database.WriteAsync(async (connection, transaction, token) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO documents(id,session_id,blob_id,file_name,content_type,sha256,length,page_count,created_at)
                    VALUES($id,$session,$blob,$name,$type,$sha,$length,$pages,$created);
                    """;
                command.Parameters.AddWithValue("$id", document.Id.ToString("D"));
                command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
                command.Parameters.AddWithValue("$blob", blob.Id.ToString("D"));
                command.Parameters.AddWithValue("$name", safeName);
                command.Parameters.AddWithValue("$type", contentType);
                command.Parameters.AddWithValue("$sha", blob.Sha256);
                command.Parameters.AddWithValue("$length", blob.Length);
                command.Parameters.AddWithValue("$pages", pages.Count);
                command.Parameters.AddWithValue("$created", now.ToDb());
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                for (var index = 0; index < pages.Count; index++)
                {
                    command.Parameters.Clear();
                    command.CommandText = "INSERT INTO document_pages(document_id,page_number,text) VALUES($id,$page,$text);";
                    command.Parameters.AddWithValue("$id", document.Id.ToString("D"));
                    command.Parameters.AddWithValue("$page", index + 1);
                    command.Parameters.AddWithValue("$text", pages[index]);
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                command.Parameters.Clear();
                command.CommandText = """
                    INSERT INTO document_index_entries
                        (sha256,schema_version,model_profile,status,progress,document_map,error,created_at,updated_at)
                    VALUES($sha,$version,$profile,'ready',100,$map,NULL,$now,$now)
                    ON CONFLICT(sha256) DO UPDATE SET
                        schema_version=excluded.schema_version,model_profile=excluded.model_profile,
                        status='ready',progress=100,error=NULL,updated_at=excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("$sha", blob.Sha256);
                command.Parameters.AddWithValue("$version", IndexSchemaVersion);
                command.Parameters.AddWithValue("$profile", IndexModelProfile);
                command.Parameters.AddWithValue("$map", BuildDocumentMap(safeName, pages));
                command.Parameters.AddWithValue("$now", now.ToDb());
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                if (cachedPages.Count == 0)
                {
                    command.Parameters.Clear();
                    command.CommandText = "DELETE FROM document_index_pages WHERE sha256=$sha; DELETE FROM document_index_chunks WHERE sha256=$sha;";
                    command.Parameters.AddWithValue("$sha", blob.Sha256);
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
                    {
                        command.Parameters.Clear();
                        command.CommandText = "INSERT INTO document_index_pages(sha256,page_number,text) VALUES($sha,$page,$text);";
                        command.Parameters.AddWithValue("$sha", blob.Sha256);
                        command.Parameters.AddWithValue("$page", pageIndex + 1);
                        command.Parameters.AddWithValue("$text", pages[pageIndex]);
                        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                        var chunkNumber = 0;
                        foreach (var chunk in SplitChunks(pages[pageIndex]))
                        {
                            command.Parameters.Clear();
                            command.CommandText = "INSERT INTO document_index_chunks(id,sha256,page_number,chunk_number,text,normalized_text) VALUES($id,$sha,$page,$chunk,$text,$normalized);";
                            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
                            command.Parameters.AddWithValue("$sha", blob.Sha256);
                            command.Parameters.AddWithValue("$page", pageIndex + 1);
                            command.Parameters.AddWithValue("$chunk", chunkNumber++);
                            command.Parameters.AddWithValue("$text", chunk);
                            command.Parameters.AddWithValue("$normalized", NormalizeSearchText(chunk));
                            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                        }
                    }
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await blobStore.DeleteIfUnreferencedAsync(blob.Id, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        var hasText = pages.Any(static page => !string.IsNullOrWhiteSpace(page));
        return new(document with { WasReused = cachedPages.Count > 0 }, true, hasText ? null : "Das Dokument wurde gespeichert, enthält aber keinen extrahierbaren Text. OCR ist in v1 nicht enthalten.", hasText);
    }

    public async Task<IReadOnlyList<StoredDocument>> ListAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.id,d.session_id,d.blob_id,d.file_name,d.content_type,d.sha256,d.length,d.page_count,d.created_at,
                   COALESCE(s.status,i.status,'preparing'),COALESCE(s.progress,i.progress,0),COALESCE(s.error,i.error)
            FROM documents d
            LEFT JOIN document_index_entries i ON i.sha256=d.sha256
            LEFT JOIN document_context_states s ON s.document_id=d.id AND s.session_id=d.session_id
            WHERE d.session_id=$id ORDER BY d.created_at;
            """;
        command.Parameters.AddWithValue("$id", sessionId.ToString("D"));
        var result = new List<StoredDocument>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new(reader.ReadGuid(0), reader.ReadGuid(1), reader.ReadGuid(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt64(6), reader.GetInt32(7), reader.ReadDate(8), ParseStatus(reader.GetString(9)), reader.GetInt32(10), false, reader.IsDBNull(11) ? null : reader.GetString(11)));
        }

        return result;
    }

    public async Task<IReadOnlyList<DocumentPage>> ReadPagesAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.document_id,p.page_number,p.text,d.file_name
            FROM document_pages p JOIN documents d ON d.id=p.document_id
            WHERE p.document_id=$id ORDER BY p.page_number;
            """;
        command.Parameters.AddWithValue("$id", documentId.ToString("D"));
        var result = new List<DocumentPage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new(reader.ReadGuid(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3)));
        }

        return result;
    }

    public async Task<IReadOnlyList<DocumentContextHit>> SearchAsync(Guid sessionId, string query, int maximumCharacters = 160_000, CancellationToken cancellationToken = default)
        => await SearchCoreAsync(sessionId, query, null, null, maximumCharacters, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<DocumentContextHit>> SearchHybridAsync(
        Guid sessionId,
        string query,
        string embeddingModelId,
        IReadOnlyList<double> queryEmbedding,
        int maximumCharacters = 160_000,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(embeddingModelId);
        ArgumentNullException.ThrowIfNull(queryEmbedding);
        if (queryEmbedding.Count == 0)
        {
            throw new ArgumentException("Der semantische Suchvektor ist leer.", nameof(queryEmbedding));
        }
        return await SearchCoreAsync(
            sessionId,
            query,
            embeddingModelId,
            queryEmbedding,
            maximumCharacters,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<DocumentContextHit>> SearchCoreAsync(
        Guid sessionId,
        string query,
        string? embeddingModelId,
        IReadOnlyList<double>? queryEmbedding,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCharacters, 1_000);
        var terms = SearchTerms(query);
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.id,d.sha256,d.file_name,c.id,c.page_number,c.text,c.normalized_text,e.vector,e.dimensions
            FROM documents d
            JOIN document_index_entries i ON i.sha256=d.sha256 AND i.status='ready'
            JOIN document_index_chunks c ON c.sha256=d.sha256
            LEFT JOIN document_chunk_embeddings e ON e.chunk_id=c.id AND e.model_id=$model
            WHERE d.session_id=$session
            ORDER BY d.created_at,c.page_number,c.chunk_number;
            """;
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        command.Parameters.AddWithValue("$model", (object?)embeddingModelId ?? string.Empty);
        var candidates = new List<DocumentContextHit>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var documentId = reader.ReadGuid(0);
            var sha256 = reader.GetString(1);
            var fileName = reader.GetString(2);
            var chunkId = reader.GetString(3);
            var pageNumber = reader.GetInt32(4);
            var chunk = reader.GetString(5);
            var score = Score(reader.GetString(6), terms);
            if (queryEmbedding is not null && !reader.IsDBNull(7))
            {
                var vector = DecodeVector((byte[])reader.GetValue(7), reader.GetInt32(8));
                if (vector.Length == queryEmbedding.Count)
                {
                    score += Math.Max(0, CosineSimilarity(queryEmbedding, vector)) * 8d;
                }
            }
            candidates.Add(new(documentId, sha256, fileName, pageNumber, chunk, score, chunkId));
        }

        var ranked = candidates.OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.FileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.PageNumber).ToArray();
        var selected = new List<DocumentContextHit>();
        var used = 0;
        // Reserve at least one best matching section per document so a large first
        // attachment can never crowd all later documents out of the request.
        foreach (var group in ranked.GroupBy(static item => item.DocumentId))
        {
            var hit = group.First();
            if (used + hit.Text.Length > maximumCharacters && selected.Count > 0) continue;
            selected.Add(hit);
            used += hit.Text.Length;
        }
        foreach (var hit in ranked)
        {
            if (selected.Contains(hit)) continue;
            if (used + hit.Text.Length > maximumCharacters) break;
            selected.Add(hit);
            used += hit.Text.Length;
        }
        return selected.OrderByDescending(static item => item.Score).ToArray();
    }

    public async Task<IReadOnlyList<DocumentIndexChunk>> ListIndexChunksAsync(
        Guid sessionId,
        string embeddingModelId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(embeddingModelId);
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id,d.id,d.sha256,d.file_name,c.page_number,c.text,e.vector,e.dimensions
            FROM documents d
            JOIN document_index_entries i ON i.sha256=d.sha256 AND i.status='ready'
            JOIN document_index_chunks c ON c.sha256=d.sha256
            LEFT JOIN document_chunk_embeddings e ON e.chunk_id=c.id AND e.model_id=$model
            WHERE d.session_id=$session
            ORDER BY d.created_at,c.page_number,c.chunk_number;
            """;
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        command.Parameters.AddWithValue("$model", embeddingModelId);
        var result = new List<DocumentIndexChunk>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            IReadOnlyList<double>? embedding = reader.IsDBNull(6)
                ? null
                : DecodeVector((byte[])reader.GetValue(6), reader.GetInt32(7));
            result.Add(new(
                reader.GetString(0),
                reader.ReadGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetString(5),
                embedding));
        }
        return result;
    }

    public Task SaveEmbeddingsAsync(
        IReadOnlyList<DocumentChunkEmbedding> embeddings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        return database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            foreach (var embedding in embeddings)
            {
                if (string.IsNullOrWhiteSpace(embedding.ChunkId)
                    || string.IsNullOrWhiteSpace(embedding.ModelId)
                    || embedding.Values.Count == 0)
                {
                    throw new InvalidDataException("Ein Dokument-Embedding ist unvollständig.");
                }
                command.Parameters.Clear();
                command.CommandText = """
                    INSERT INTO document_chunk_embeddings(chunk_id,model_id,dimensions,vector,created_at)
                    VALUES($chunk,$model,$dimensions,$vector,$created)
                    ON CONFLICT(chunk_id,model_id) DO UPDATE SET
                        dimensions=excluded.dimensions,vector=excluded.vector,created_at=excluded.created_at;
                    """;
                command.Parameters.AddWithValue("$chunk", embedding.ChunkId);
                command.Parameters.AddWithValue("$model", embedding.ModelId);
                command.Parameters.AddWithValue("$dimensions", embedding.Values.Count);
                command.Parameters.AddWithValue("$vector", EncodeVector(embedding.Values));
                command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToDb());
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }, cancellationToken);
    }

    public async Task<DocumentContextPreparation?> GetContextPreparationAsync(
        string cacheKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT cache_key,session_id,corpus_revision,prompt_fingerprint,model_id,context_budget,
                   prepared_text,evidence_json,created_at
            FROM document_context_preparations WHERE cache_key=$key;
            """;
        command.Parameters.AddWithValue("$key", cacheKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        var evidence = JsonSerializer.Deserialize<DocumentContextHit[]>(reader.GetString(7), CacheJsonOptions)
            ?? [];
        return new(
            reader.GetString(0),
            reader.ReadGuid(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.GetString(6),
            evidence,
            reader.ReadDate(8));
    }

    public Task SaveContextPreparationAsync(
        DocumentContextPreparation preparation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        return database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO document_context_preparations
                    (cache_key,session_id,corpus_revision,prompt_fingerprint,model_id,context_budget,prepared_text,evidence_json,created_at)
                VALUES($key,$session,$corpus,$prompt,$model,$budget,$text,$evidence,$created)
                ON CONFLICT(cache_key) DO UPDATE SET
                    prepared_text=excluded.prepared_text,evidence_json=excluded.evidence_json,created_at=excluded.created_at;
                """;
            command.Parameters.AddWithValue("$key", preparation.CacheKey);
            command.Parameters.AddWithValue("$session", preparation.SessionId.ToString("D"));
            command.Parameters.AddWithValue("$corpus", preparation.CorpusRevision);
            command.Parameters.AddWithValue("$prompt", preparation.PromptFingerprint);
            command.Parameters.AddWithValue("$model", preparation.ModelId);
            command.Parameters.AddWithValue("$budget", preparation.ContextBudget);
            command.Parameters.AddWithValue("$text", preparation.PreparedText);
            command.Parameters.AddWithValue("$evidence", JsonSerializer.Serialize(preparation.Evidence, CacheJsonOptions));
            command.Parameters.AddWithValue("$created", preparation.CreatedAt.ToDb());
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task SetContextPreparationStateAsync(
        Guid sessionId,
        Guid messageId,
        DocumentPreparationStatus? status,
        int progress = 100,
        string? errorMessage = null,
        CancellationToken cancellationToken = default)
    {
        if (status is not null && status is not (DocumentPreparationStatus.Preparing or DocumentPreparationStatus.Failed))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
        progress = Math.Clamp(progress, 0, 100);
        return database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
            command.Parameters.AddWithValue("$message", messageId.ToString("D"));
            if (status is null)
            {
                command.CommandText = "DELETE FROM document_context_states WHERE session_id=$session;";
            }
            else
            {
                command.CommandText = """
                    INSERT INTO document_context_states(document_id,session_id,message_id,status,progress,error,updated_at)
                    SELECT id,$session,$message,$status,$progress,$error,$updated
                    FROM documents WHERE session_id=$session AND true
                    ON CONFLICT(document_id) DO UPDATE SET
                        message_id=excluded.message_id,status=excluded.status,progress=excluded.progress,
                        error=excluded.error,updated_at=excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("$status", status == DocumentPreparationStatus.Failed ? "failed" : "preparing");
                command.Parameters.AddWithValue("$progress", progress);
                command.Parameters.AddWithValue("$error", (object?)errorMessage ?? DBNull.Value);
                command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToDb());
            }
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task SaveEvidenceAsync(Guid messageId, IReadOnlyList<DocumentContextHit> evidence, CancellationToken cancellationToken = default) =>
        database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM document_evidence_snapshots WHERE message_id=$message;";
            command.Parameters.AddWithValue("$message", messageId.ToString("D"));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            foreach (var hit in evidence.DistinctBy(static item => (item.Sha256, item.FileName, item.PageNumber)))
            {
                command.Parameters.Clear();
                command.CommandText = """
                    INSERT INTO document_evidence_snapshots(message_id,sha256,file_name,page_number,chunk_id,created_at)
                    VALUES($message,$sha,$name,$page,$chunk,$created);
                    """;
                command.Parameters.AddWithValue("$message", messageId.ToString("D"));
                command.Parameters.AddWithValue("$sha", hit.Sha256);
                command.Parameters.AddWithValue("$name", hit.FileName);
                command.Parameters.AddWithValue("$page", hit.PageNumber);
                command.Parameters.AddWithValue("$chunk", (object?)hit.ChunkId ?? string.Empty);
                command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToDb());
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }, cancellationToken);

    public async Task<IReadOnlyList<string>> GetEvidenceCitationsAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT file_name,page_number FROM document_evidence_snapshots
            WHERE message_id=$message ORDER BY file_name COLLATE NOCASE,page_number;
            """;
        command.Parameters.AddWithValue("$message", messageId.ToString("D"));
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add($"[{reader.GetString(0)}, S. {reader.GetInt32(1)}]");
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public Task RemoveAsync(Guid documentId, CancellationToken cancellationToken = default) => database.WriteAsync(async (connection, transaction, token) =>
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT blob_id FROM documents WHERE id=$id;";
        command.Parameters.AddWithValue("$id", documentId.ToString("D"));
        var blobId = await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
        command.CommandText = "DELETE FROM documents WHERE id=$id;";
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        if (blobId is not null)
        {
            command.Parameters.Clear();
            command.CommandText = """
                DELETE FROM binary_objects WHERE id=$id
                  AND NOT EXISTS(SELECT 1 FROM documents WHERE blob_id=$id)
                  AND NOT EXISTS(SELECT 1 FROM project_assets WHERE blob_id=$id)
                  AND NOT EXISTS(SELECT 1 FROM project_asset_thumbnails WHERE blob_id=$id);
                """;
            command.Parameters.AddWithValue("$id", blobId);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
    }, cancellationToken);

    private async Task<StoredDocument?> FindSessionDocumentAsync(Guid sessionId, string sha256, CancellationToken cancellationToken)
    {
        var items = await ListAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return items.FirstOrDefault(item => string.Equals(item.Sha256, sha256, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<IReadOnlyList<string>> ReadCachedPagesAsync(string sha256, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.text FROM document_index_entries i
            JOIN document_index_pages p ON p.sha256=i.sha256
            WHERE i.sha256=$sha AND i.schema_version=$version AND i.model_profile=$profile AND i.status='ready'
            ORDER BY p.page_number;
            """;
        command.Parameters.AddWithValue("$sha", sha256);
        command.Parameters.AddWithValue("$version", IndexSchemaVersion);
        command.Parameters.AddWithValue("$profile", IndexModelProfile);
        var pages = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) pages.Add(reader.GetString(0));
        return pages;
    }

    private static IEnumerable<string> SplitChunks(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        for (var start = 0; start < text.Length; start += ChunkCharacters - ChunkOverlap)
        {
            var length = Math.Min(ChunkCharacters, text.Length - start);
            yield return text.Substring(start, length);
            if (start + length >= text.Length) yield break;
        }
    }

    private static string BuildDocumentMap(string fileName, IReadOnlyList<string> pages)
    {
        var headings = pages.SelectMany(static page => page.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Where(static line => line.Length is >= 4 and <= 140).Take(24);
        return $"Dokument: {fileName}\nSeiten: {pages.Count}\nGliederung:\n{string.Join("\n", headings)}";
    }

    private static string NormalizeSearchText(string value) => value.ToLowerInvariant().Replace('\r', ' ').Replace('\n', ' ');

    private static string[] SearchTerms(string query) => Regex.Matches(query.ToLowerInvariant(), @"[\p{L}\p{N}][\p{L}\p{N}_\-]{2,}")
        .Select(static match => match.Value)
        .Where(static term => term is not "und" and not "oder" and not "der" and not "die" and not "das" and not "eine" and not "einen" and not "einem" and not "aus" and not "dem" and not "den")
        .Distinct(StringComparer.Ordinal).Take(32).ToArray();

    private static double Score(string text, string[] terms)
    {
        if (terms.Length == 0) return 0;
        var score = 0d;
        foreach (var term in terms)
        {
            var index = 0;
            while ((index = text.IndexOf(term, index, StringComparison.Ordinal)) >= 0)
            {
                score += 1 + Math.Min(2, term.Length / 8d);
                index += term.Length;
            }
        }
        return score;
    }

    private static byte[] EncodeVector(IReadOnlyList<double> values)
    {
        var single = new float[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            single[index] = checked((float)values[index]);
        }
        var bytes = new byte[single.Length * sizeof(float)];
        Buffer.BlockCopy(single, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static double[] DecodeVector(byte[] bytes, int dimensions)
    {
        if (dimensions <= 0 || bytes.Length != dimensions * sizeof(float))
        {
            throw new InvalidDataException("Ein gespeichertes Dokument-Embedding besitzt ungültige Abmessungen.");
        }
        var single = new float[dimensions];
        Buffer.BlockCopy(bytes, 0, single, 0, bytes.Length);
        return single.Select(static value => (double)value).ToArray();
    }

    private static double CosineSimilarity(IReadOnlyList<double> left, double[] right)
    {
        if (left.Count != right.Length || left.Count == 0)
        {
            return 0;
        }
        var dot = 0d;
        var leftNorm = 0d;
        var rightNorm = 0d;
        for (var index = 0; index < left.Count; index++)
        {
            dot += left[index] * right[index];
            leftNorm += left[index] * left[index];
            rightNorm += right[index] * right[index];
        }
        return leftNorm <= 0 || rightNorm <= 0
            ? 0
            : dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm));
    }

    private static DocumentPreparationStatus ParseStatus(string status) => status switch
    {
        "extracting" => DocumentPreparationStatus.Extracting,
        "preparing" => DocumentPreparationStatus.Preparing,
        "failed" => DocumentPreparationStatus.Failed,
        _ => DocumentPreparationStatus.Ready,
    };

    private static Task<IReadOnlyList<string>> ExtractAsync(string path, string extension, CancellationToken cancellationToken) => extension.ToLowerInvariant() switch
    {
        ".pdf" => Task.FromResult<IReadOnlyList<string>>(ExtractPdf(path, cancellationToken)),
        ".docx" => Task.FromResult<IReadOnlyList<string>>(ExtractDocx(path, cancellationToken)),
        ".xml" => ExtractXmlAsync(path, cancellationToken),
        ".html" or ".htm" => ExtractHtmlAsync(path, cancellationToken),
        ".rtf" => ExtractRtfAsync(path, cancellationToken),
        _ => ExtractTextAsync(path, cancellationToken),
    };

    private static List<string> ExtractPdf(string path, CancellationToken cancellationToken)
    {
        using var document = PdfDocument.Open(path);
        var pages = new List<string>(document.NumberOfPages);
        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            pages.Add(ContentOrderTextExtractor.GetText(page));
        }

        return pages;
    }

    private static IReadOnlyList<string> ExtractDocx(string path, CancellationToken cancellationToken)
    {
        using var document = WordprocessingDocument.Open(path, false);
        var mainPart = document.MainDocumentPart ?? throw new InvalidDataException("DOCX enthält keinen Hauptteil.");
        var mainDocument = mainPart.Document ?? throw new InvalidDataException("DOCX enthält kein Hauptdokument.");
        var body = mainDocument.Body ?? throw new InvalidDataException("DOCX enthält keinen Haupttext.");
        var result = new StringBuilder();
        foreach (var paragraph in body.Descendants<Paragraph>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = string.Concat(paragraph.Descendants<Text>().Select(static node => node.Text));
            if (text.Length > 0)
            {
                result.AppendLine(text);
            }
        }

        return [result.ToString()];
    }

    private static async Task<IReadOnlyList<string>> ExtractTextAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return [await ReadWithEncodingAsync(path, new UTF8Encoding(false, true), cancellationToken).ConfigureAwait(false)];
        }
        catch (DecoderFallbackException)
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            return [DecodeWindows1252(bytes)];
        }
    }

    private static async Task<IReadOnlyList<string>> ExtractXmlAsync(string path, CancellationToken cancellationToken)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, Async = true };
        var result = new StringBuilder();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = XmlReader.Create(stream, settings);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace)
            {
                result.AppendLine(reader.Value);
            }
        }

        return [result.ToString()];
    }

    private static async Task<IReadOnlyList<string>> ExtractHtmlAsync(string path, CancellationToken cancellationToken)
    {
        var html = (await ExtractTextAsync(path, cancellationToken).ConfigureAwait(false))[0];
        html = ScriptAndStyleRegex().Replace(html, " ");
        html = BlockBreakRegex().Replace(html, "\n");
        return [WebUtility.HtmlDecode(TagRegex().Replace(html, " ")).Trim()];
    }

    private static async Task<IReadOnlyList<string>> ExtractRtfAsync(string path, CancellationToken cancellationToken)
    {
        var rtf = (await ExtractTextAsync(path, cancellationToken).ConfigureAwait(false))[0];
        return [RtfToText(rtf)];
    }

    private static async Task<string> ReadWithEncodingAsync(string path, Encoding encoding, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true, 64 * 1024, leaveOpen: false);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string DecodeWindows1252(byte[] bytes)
    {
        ReadOnlySpan<char> replacements = "€�‚ƒ„…†‡ˆ‰Š‹Œ�Ž��‘’“”•–—˜™š›œ�žŸ";
        var chars = new char[bytes.Length];
        for (var index = 0; index < bytes.Length; index++)
        {
            var value = bytes[index];
            chars[index] = value is >= 0x80 and <= 0x9f ? replacements[value - 0x80] : (char)value;
        }

        return new string(chars);
    }

    private static string RtfToText(string rtf)
    {
        var result = new StringBuilder(rtf.Length);
        var depth = 0;
        var skipDepth = -1;
        for (var index = 0; index < rtf.Length; index++)
        {
            var current = rtf[index];
            if (current == '{') { depth++; continue; }
            if (current == '}') { if (depth == skipDepth) skipDepth = -1; depth--; continue; }
            if (skipDepth >= 0) continue;
            if (current != '\\') { result.Append(current); continue; }
            if (++index >= rtf.Length) break;
            current = rtf[index];
            if (current is '\\' or '{' or '}') { result.Append(current); continue; }
            if (current == '*') { skipDepth = depth; continue; }
            if (current == '\'')
            {
                if (index + 2 < rtf.Length && byte.TryParse(rtf.AsSpan(index + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out var hex))
                {
                    result.Append(DecodeWindows1252([hex]));
                    index += 2;
                }
                continue;
            }

            var start = index;
            while (index < rtf.Length && char.IsLetter(rtf[index])) index++;
            var word = rtf[start..index];
            while (index < rtf.Length && (char.IsDigit(rtf[index]) || rtf[index] == '-')) index++;
            if (index < rtf.Length && rtf[index] != ' ') index--;
            if (word is "par" or "line") result.AppendLine();
            else if (word == "tab") result.Append('\t');
        }

        return result.ToString().Trim();
    }

    private static string GetContentType(string extension) => extension.ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".html" or ".htm" => "text/html",
        ".xml" => "application/xml",
        ".json" => "application/json",
        ".rtf" => "application/rtf",
        _ => "text/plain",
    };

    [GeneratedRegex(@"<(script|style)\b[^>]*>[\s\S]*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScriptAndStyleRegex();
    [GeneratedRegex(@"</?(?:p|div|br|li|tr|h[1-6])\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BlockBreakRegex();
    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex TagRegex();
}
