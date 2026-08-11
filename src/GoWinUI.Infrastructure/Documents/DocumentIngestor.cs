using System.Net;
using System.Text;
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

        var temporaryPath = Path.Combine(Path.GetTempPath(), $"GO-{Guid.NewGuid():N}{extension}");
        IReadOnlyList<string> pages;
        try
        {
            await using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await blobStore.ExportAsync(blob.Id, file, cancellationToken).ConfigureAwait(false);
            }

            pages = await ExtractAsync(temporaryPath, extension, cancellationToken).ConfigureAwait(false);
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
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await blobStore.DeleteIfUnreferencedAsync(blob.Id, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        var hasText = pages.Any(static page => !string.IsNullOrWhiteSpace(page));
        return new(document, true, hasText ? null : "Das Dokument wurde gespeichert, enthält aber keinen extrahierbaren Text. OCR ist in v1 nicht enthalten.", hasText);
    }

    public async Task<IReadOnlyList<StoredDocument>> ListAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,session_id,blob_id,file_name,content_type,sha256,length,page_count,created_at FROM documents WHERE session_id=$id ORDER BY created_at;";
        command.Parameters.AddWithValue("$id", sessionId.ToString("D"));
        var result = new List<StoredDocument>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new(reader.ReadGuid(0), reader.ReadGuid(1), reader.ReadGuid(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt64(6), reader.GetInt32(7), reader.ReadDate(8)));
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
