using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocumentFormat.OpenXml.Spreadsheet;
using GoWinUI.Core.Contracts;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace GoWinUI.Infrastructure.Documents;

/// <summary>
/// Format-aware document I/O used by the bounded AI document tools. Binary
/// formats are never passed through text file operations.
/// </summary>
public sealed partial class DocumentFileCodec : IDocumentFileCodec
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".xlsx", ".txt", ".md", ".markdown", ".csv", ".json", ".xml",
        ".html", ".htm", ".rtf", ".log", ".ini", ".yaml", ".yml", ".tex",
    };

    public IReadOnlySet<string> ReadableExtensions => Extensions;

    public async Task<IReadOnlyList<string>> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Das Dokument wurde nicht gefunden.", fullPath);
        }
        var extension = Path.GetExtension(fullPath);
        if (!Extensions.Contains(extension))
        {
            throw new InvalidDataException($"Der Dokumenttyp '{extension}' wird von document.read nicht unterstützt.");
        }

        return extension.ToLowerInvariant() switch
        {
            ".pdf" => ReadPdf(fullPath, cancellationToken),
            ".docx" => ReadDocx(fullPath, cancellationToken),
            ".xlsx" => ReadXlsx(fullPath, cancellationToken),
            ".xml" => await ReadXmlAsync(fullPath, cancellationToken).ConfigureAwait(false),
            ".html" or ".htm" => await ReadHtmlAsync(fullPath, cancellationToken).ConfigureAwait(false),
            ".rtf" => [StripRtf(await ReadTextAsync(fullPath, cancellationToken).ConfigureAwait(false))],
            _ => [await ReadTextAsync(fullPath, cancellationToken).ConfigureAwait(false)],
        };
    }

    public async Task WriteDocxAsync(
        string sourceMarkdown,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceMarkdown);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Der DOCX-Zielpfad ist ungültig.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var document = WordprocessingDocument.Create(
                    temporaryPath,
                    WordprocessingDocumentType.Document,
                    autoSave: true);
                var mainPart = document.AddMainDocumentPart();
                var body = new Body();
                mainPart.Document = new Document(body);

                foreach (var rawLine in sourceMarkdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsSectionMarker(rawLine)) continue;
                    var line = rawLine.TrimEnd();
                    var heading = HeadingRegex().Match(line);
                    var paragraph = new Paragraph();
                    if (heading.Success)
                    {
                        var level = Math.Clamp(heading.Groups[1].Value.Length, 1, 6);
                        paragraph.ParagraphProperties = new ParagraphProperties(
                            new ParagraphStyleId { Val = $"Heading{Math.Min(level, 3)}" });
                        line = heading.Groups[2].Value;
                    }
                    else if (BulletRegex().IsMatch(line))
                    {
                        line = "• " + BulletRegex().Replace(line, string.Empty, 1);
                    }

                    line = NormalizeInlineMarkdown(line);
                    paragraph.Append(new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text(line) { Space = SpaceProcessingModeValues.Preserve }));
                    body.Append(paragraph);
                }
                mainPart.Document.Save();
            }, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
        }
    }

    public async Task WriteXlsxAsync(
        string sourceMarkdown,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceMarkdown);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Der XLSX-Zielpfad ist ungültig.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var document = SpreadsheetDocument.Create(
                    temporaryPath,
                    SpreadsheetDocumentType.Workbook,
                    autoSave: true);
                var workbook = document.AddWorkbookPart();
                workbook.Workbook = new Workbook();
                var worksheetPart = workbook.AddNewPart<WorksheetPart>();
                worksheetPart.Worksheet = new Worksheet();
                var sheetData = new SheetData();
                worksheetPart.Worksheet.Append(sheetData);
                var sheets = new Sheets();
                var sheet = new Sheet
                {
                    Id = workbook.GetIdOfPart(worksheetPart),
                    SheetId = 1U,
                    Name = "Dokument",
                };
                sheets.Append(sheet);
                workbook.Workbook.Append(sheets);
                foreach (var rawLine in sourceMarkdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsSectionMarker(rawLine) || string.IsNullOrWhiteSpace(rawLine)) continue;
                    var row = new Row();
                    foreach (var cellText in rawLine.Split('|', StringSplitOptions.TrimEntries))
                    {
                        var cell = new Cell
                        {
                            DataType = CellValues.String,
                            CellValue = new CellValue(NormalizeInlineMarkdown(cellText)),
                        };
                        row.Append(cell);
                    }
                    sheetData.Append(row);
                }
                workbook.Workbook.Save();
            }, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
        }
    }

    private static List<string> ReadPdf(string path, CancellationToken cancellationToken)
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

    private static List<string> ReadDocx(string path, CancellationToken cancellationToken)
    {
        using var document = WordprocessingDocument.Open(path, false);
        var body = document.MainDocumentPart?.Document?.Body
            ?? throw new InvalidDataException("DOCX enthält keinen Haupttext.");
        var paragraphs = new List<string>();
        foreach (var paragraph in body.Descendants<Paragraph>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = string.Concat(paragraph.Descendants<DocumentFormat.OpenXml.Wordprocessing.Text>().Select(static node => node.Text)).Trim();
            if (text.Length > 0) paragraphs.Add(text);
        }
        return paragraphs.Count == 0 ? [string.Empty] : paragraphs;
    }

    private static List<string> ReadXlsx(string path, CancellationToken cancellationToken)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        var workbook = document.WorkbookPart?.WorksheetParts.FirstOrDefault()?.Worksheet
            ?? throw new InvalidDataException("XLSX enthält kein Arbeitsblatt.");
        var rows = workbook.Descendants<Row>().ToList();
        var lines = new List<string>(rows.Count);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cells = row.Descendants<Cell>().Select(static cell => cell.InnerText?.Trim() ?? string.Empty);
            var line = string.Join(" | ", cells.Where(static text => text.Length > 0));
            if (line.Length > 0) lines.Add(line);
        }
        return lines.Count == 0 ? [string.Empty] : lines;
    }

    private static async Task<IReadOnlyList<string>> ReadXmlAsync(string path, CancellationToken cancellationToken)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, Async = true };
        var result = new StringBuilder();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
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

    private static async Task<IReadOnlyList<string>> ReadHtmlAsync(string path, CancellationToken cancellationToken)
    {
        var html = await ReadTextAsync(path, cancellationToken).ConfigureAwait(false);
        html = ScriptAndStyleRegex().Replace(html, " ");
        html = BlockBreakRegex().Replace(html, "\n");
        return [WebUtility.HtmlDecode(TagRegex().Replace(html, " ")).Trim()];
    }

    private static async Task<string> ReadTextAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllTextAsync(path, new UTF8Encoding(false, true), cancellationToken).ConfigureAwait(false);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
        }
    }

    private static string NormalizeInlineMarkdown(string value) => value
        .Replace("**", string.Empty, StringComparison.Ordinal)
        .Replace("__", string.Empty, StringComparison.Ordinal)
        .Replace("`", string.Empty, StringComparison.Ordinal)
        .Trim();

    private static bool IsSectionMarker(string value) =>
        value.TrimStart().StartsWith("<!-- GO-DOCUMENT-SECTION:", StringComparison.Ordinal)
        || value.Trim().StartsWith("<!-- /GO-DOCUMENT-SECTION:", StringComparison.Ordinal);

    private static string StripRtf(string value) => RtfControlRegex().Replace(value, " ")
        .Replace('{', ' ')
        .Replace('}', ' ')
        .Replace("\\par", "\n", StringComparison.OrdinalIgnoreCase)
        .Trim();

    [GeneratedRegex("^(#{1,6})\\s+(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingRegex();

    [GeneratedRegex("^\\s*(?:[-*+] |\\d+[.)] )", RegexOptions.CultureInvariant)]
    private static partial Regex BulletRegex();

    [GeneratedRegex("<script\\b[^>]*>[\\s\\S]*?</script>|<style\\b[^>]*>[\\s\\S]*?</style>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScriptAndStyleRegex();

    [GeneratedRegex("</(?:p|div|h[1-6]|li|tr|section|article|br)>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BlockBreakRegex();

    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex TagRegex();

    [GeneratedRegex("\\\\[a-zA-Z]+-?\\d* ?|\\\\'[0-9a-fA-F]{2}", RegexOptions.CultureInvariant)]
    private static partial Regex RtfControlRegex();
}
