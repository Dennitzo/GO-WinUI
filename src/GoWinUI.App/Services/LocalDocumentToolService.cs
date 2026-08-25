using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;

namespace GoWinUI.App.Services;

/// <summary>
/// Implements the shared, bounded document tools for General AI and Coding.
/// The model exchanges outlines, sections and continuations instead of entire
/// large files. Session documents are revisioned in SQLite; coding documents
/// use a canonical Markdown source inside the authorized workspace.
/// </summary>
public sealed partial class LocalDocumentToolService(
    IGeneratedDocumentRepository generatedDocuments,
    IDocumentIngestor attachedDocuments,
    IChatArtifactRepository artifacts,
    IBinaryObjectStore blobs,
    IChatRepository chats,
    IDocumentFileCodec codec,
    CodingSolutionPdfExporter pdfExporter)
{
    private const int DefaultMaximumCharacters = 12_000;
    private const int MaximumCharacters = 40_000;
    private const int MaximumUnits = 30;
    private const int MaximumSourceCharacters = 4 * 1024 * 1024;
    private const long MaximumReadableDocumentBytes = 128L * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly HashSet<string> ReservedWindowsFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public async Task<object> ReadAsync(
        JsonElement arguments,
        string? workspacePath,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var scope = RequiredString(arguments, "scope");
        var mode = RequiredString(arguments, "mode");
        var maximumCharacters = OptionalInteger(arguments, "maximumCharacters", DefaultMaximumCharacters);
        var maximumUnits = OptionalInteger(arguments, "maximumUnits", mode == "outline" ? MaximumUnits : 6);
        var startUnit = OptionalInteger(arguments, "startUnit", 1);
        if (mode == "list")
        {
            if (scope != "session")
            {
                throw new InvalidDataException("document.read list ist nur für Sitzungsdokumente verfügbar.");
            }
            return await ListSessionDocumentsAsync(sessionId, startUnit, maximumUnits, cancellationToken).ConfigureAwait(false);
        }

        var reference = RequiredString(arguments, "reference");
        var source = scope switch
        {
            "session" => await ReadSessionSourceAsync(sessionId, reference, cancellationToken).ConfigureAwait(false),
            "workspace" => await ReadWorkspaceSourceAsync(workspacePath, reference, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidDataException("Der document.read-Bereich ist ungültig."),
        };
        var units = BuildUnits(source);
        return mode switch
        {
            "outline" => BuildOutline(source, units, startUnit, maximumUnits),
            "read" => BuildReadWindow(
                source,
                units,
                startUnit,
                OptionalInteger(arguments, "characterOffset", 0),
                maximumUnits,
                maximumCharacters),
            "search" => BuildSearch(
                source,
                units,
                RequiredString(arguments, "query"),
                maximumUnits,
                maximumCharacters),
            _ => throw new InvalidDataException("Der document.read-Modus ist ungültig."),
        };
    }

    public async Task<object> CreateAsync(
        JsonElement arguments,
        string? workspacePath,
        Guid sessionId,
        Guid assistantMessageId,
        bool codingMode,
        CancellationToken cancellationToken)
    {
        var operation = RequiredString(arguments, "operation");
        var reference = RequiredString(arguments, "reference");
        var format = RequiredString(arguments, "format");
        var sectionId = RequiredString(arguments, "sectionId");
        var heading = OptionalString(arguments, "heading");
        var content = RequiredString(arguments, "content", allowEmpty: true);
        var expectedSha256 = OptionalString(arguments, "expectedSha256");
        if (SectionMarkerRegex().IsMatch(content) || (heading is not null && SectionMarkerRegex().IsMatch(heading)))
        {
            throw new InvalidDataException("Dokumentinhalt darf keine internen GO-Abschnittsmarker enthalten.");
        }

        return codingMode
            ? await CreateWorkspaceDocumentAsync(
                workspacePath,
                operation,
                reference,
                format,
                sectionId,
                heading,
                content,
                expectedSha256,
                cancellationToken).ConfigureAwait(false)
            : await CreateSessionDocumentAsync(
                sessionId,
                assistantMessageId,
                operation,
                reference,
                format,
                sectionId,
                heading,
                content,
                expectedSha256,
                cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> ListSessionDocumentsAsync(
        Guid sessionId,
        int startUnit,
        int maximumUnits,
        CancellationToken cancellationToken)
    {
        var generated = await generatedDocuments.ListAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var attached = await attachedDocuments.ListAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var sessionArtifacts = await artifacts.ListForSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var generatedArtifactIds = generated.Select(static item => item.Id.ToString("D")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var artifactItems = sessionArtifacts.Values.SelectMany(static item => item)
            .Where(item => codec.ReadableExtensions.Contains(Path.GetExtension(item.FileName)))
            .Where(item => item.Metadata is null
                || !item.Metadata.TryGetValue("documentId", out var documentId)
                || !generatedArtifactIds.Contains(documentId))
            .Select(static item => new DocumentListItem(
                item.Id.ToString("D"),
                "artifact",
                item.FileName,
                Path.GetExtension(item.FileName).TrimStart('.').ToLowerInvariant(),
                item.Sha256));
        var items = generated.Select(static item => new DocumentListItem(
                item.Id.ToString("D"), "generated", item.FileName, item.Format, item.Sha256, item.Revision))
            .Concat(attached.Select(static item => new DocumentListItem(
                item.Id.ToString("D"),
                "attached",
                item.FileName,
                Path.GetExtension(item.FileName).TrimStart('.').ToLowerInvariant(),
                item.Sha256,
                PageCount: item.PageCount,
                Status: item.PreparationStatus.ToString().ToLowerInvariant())))
            .Concat(artifactItems)
            .OrderBy(static item => item.Source, StringComparer.Ordinal)
            .ThenBy(static item => item.FileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Reference, StringComparer.Ordinal)
            .ToArray();
        if (items.Length == 0)
        {
            return new { documents = Array.Empty<DocumentListItem>(), total = 0, complete = true, nextStartUnit = (int?)null };
        }
        if (startUnit < 1 || startUnit > items.Length)
        {
            throw new InvalidDataException("startUnit liegt außerhalb der Dokumentliste.");
        }
        maximumUnits = Math.Clamp(maximumUnits, 1, MaximumUnits);
        var selected = items.Skip(startUnit - 1).Take(maximumUnits).ToArray();
        var nextStartUnit = startUnit - 1 + selected.Length < items.Length
            ? startUnit + selected.Length
            : (int?)null;
        return new
        {
            documents = selected,
            total = items.Length,
            complete = nextStartUnit is null,
            nextStartUnit,
        };
    }

    private async Task<DocumentSource> ReadSessionSourceAsync(
        Guid sessionId,
        string reference,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(reference, out var id))
        {
            throw new InvalidDataException("Die Sitzungsreferenz muss eine von document.read list gelieferte GUID sein.");
        }
        var generated = await generatedDocuments.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (generated is not null)
        {
            if (generated.SessionId != sessionId) throw new UnauthorizedAccessException("Das Dokument gehört nicht zur aktuellen Sitzung.");
            return new DocumentSource(
                generated.Id.ToString("D"), generated.FileName, generated.Format, generated.Sha256,
                [generated.SourceMarkdown], IsCanonicalMarkdown: true);
        }

        var attached = (await attachedDocuments.ListAsync(sessionId, cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(item => item.Id == id);
        if (attached is not null)
        {
            if (attached.PreparationStatus != DocumentPreparationStatus.Ready)
            {
                throw new InvalidOperationException("Das angehängte Dokument ist noch nicht vollständig aufbereitet.");
            }
            var pages = await attachedDocuments.ReadPagesAsync(id, cancellationToken).ConfigureAwait(false);
            return new DocumentSource(
                id.ToString("D"), attached.FileName, Path.GetExtension(attached.FileName).TrimStart('.').ToLowerInvariant(),
                attached.Sha256, pages.Select(static page => page.Text).ToArray(), IsPaged: true);
        }

        var artifact = await artifacts.GetAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("Die Sitzungsdokument-Referenz wurde nicht gefunden.");
        var message = await chats.GetMessageAsync(artifact.MessageId, includeInternal: true, cancellationToken).ConfigureAwait(false);
        if (message?.SessionId != sessionId)
        {
            throw new UnauthorizedAccessException("Das Artefakt gehört nicht zur aktuellen Sitzung.");
        }
        var pagesFromArtifact = await ReadBlobAsync(artifact, cancellationToken).ConfigureAwait(false);
        return new DocumentSource(
            artifact.Id.ToString("D"), artifact.FileName,
            Path.GetExtension(artifact.FileName).TrimStart('.').ToLowerInvariant(), artifact.Sha256, pagesFromArtifact,
            IsPaged: Path.GetExtension(artifact.FileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<DocumentSource> ReadWorkspaceSourceAsync(
        string? workspacePath,
        string reference,
        CancellationToken cancellationToken)
    {
        var root = RequireWorkspace(workspacePath);
        var path = ResolveWithinWorkspace(root, reference, requireExisting: true);
        if (Directory.Exists(path)) throw new InvalidDataException("document.read erwartet eine Dokumentdatei und keinen Ordner.");
        if (new FileInfo(path).Length > MaximumReadableDocumentBytes)
        {
            throw new InvalidDataException("Das Workspace-Dokument überschreitet die sichere Lesegrenze von 128 MiB.");
        }
        var extension = Path.GetExtension(path);
        var format = extension.ToLowerInvariant() switch
        {
            ".md" or ".markdown" => "markdown",
            ".txt" => "text",
            ".docx" => "docx",
            ".pdf" => "pdf",
            _ => extension.TrimStart('.').ToLowerInvariant(),
        };
        var canonicalPath = CanonicalSourcePath(path, format);
        if (!string.Equals(canonicalPath, path, StringComparison.OrdinalIgnoreCase)
            && File.Exists(canonicalPath))
        {
            _ = ResolveWithinWorkspace(root, Path.GetRelativePath(root, canonicalPath), requireExisting: true);
            if (new FileInfo(canonicalPath).Length > MaximumSourceCharacters)
            {
                throw new InvalidDataException("Die kanonische Dokumentquelle überschreitet die sichere Lesegrenze.");
            }
            var canonicalSource = await File.ReadAllTextAsync(canonicalPath, cancellationToken).ConfigureAwait(false);
            if (MarkedSectionRegex().IsMatch(canonicalSource))
            {
                return new DocumentSource(
                    Path.GetRelativePath(root, path).Replace('\\', '/'),
                    Path.GetFileName(path),
                    format,
                    Hash(canonicalSource),
                    [canonicalSource],
                    IsCanonicalMarkdown: true);
            }
        }
        var pages = await codec.ReadAsync(path, cancellationToken).ConfigureAwait(false);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return new DocumentSource(
            Path.GetRelativePath(root, path).Replace('\\', '/'),
            Path.GetFileName(path),
            format,
            Hash(bytes),
            pages,
            IsPaged: extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase),
            IsCanonicalMarkdown: extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<IReadOnlyList<string>> ReadBlobAsync(ChatArtifact artifact, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(artifact.FileName);
        if (!codec.ReadableExtensions.Contains(extension))
        {
            throw new InvalidDataException($"Der Artefakttyp '{extension}' wird von document.read nicht unterstützt.");
        }
        if (artifact.Length > MaximumReadableDocumentBytes)
        {
            throw new InvalidDataException("Das Dokumentartefakt überschreitet die sichere Lesegrenze von 128 MiB.");
        }
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"GO-document-{Guid.NewGuid():N}{extension}");
        try
        {
            await using (var source = await blobs.OpenReadAsync(artifact.BlobId, cancellationToken).ConfigureAwait(false))
            await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, true))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }
            return await codec.ReadAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
        }
    }

    private async Task<object> CreateSessionDocumentAsync(
        Guid sessionId,
        Guid assistantMessageId,
        string operation,
        string reference,
        string format,
        string sectionId,
        string? heading,
        string content,
        string? expectedSha256,
        CancellationToken cancellationToken)
    {
        GeneratedDocument document;
        var changed = true;
        var create = operation == "create";
        long? expectedRevision = null;
        if (operation == "create")
        {
            var fileName = NormalizeOutputFileName(reference, format);
            if ((await generatedDocuments.ListAsync(sessionId, cancellationToken).ConfigureAwait(false))
                .Any(item => string.Equals(item.FileName, fileName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new IOException("In dieser Sitzung existiert bereits ein erzeugtes Dokument mit diesem Dateinamen. Verwende dessen documentId zum Bearbeiten.");
            }
            var updatedSource = BuildSection(sectionId, heading, content);
            var now = DateTimeOffset.UtcNow;
            document = new GeneratedDocument(
                Guid.NewGuid(), sessionId, fileName, format, updatedSource, Hash(updatedSource), 1, now, now);
        }
        else
        {
            if (!Guid.TryParse(reference, out var documentId))
            {
                throw new InvalidDataException("Zum Bearbeiten eines Sitzungsdokuments ist dessen documentId als reference erforderlich.");
            }
            document = await generatedDocuments.GetAsync(documentId, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("Das erzeugte Sitzungsdokument wurde nicht gefunden.");
            if (document.SessionId != sessionId) throw new UnauthorizedAccessException("Das Dokument gehört nicht zur aktuellen Sitzung.");
            if (!string.Equals(document.Format, format, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Das Ausgabeformat eines vorhandenen Dokuments kann nicht während einer Abschnittsänderung gewechselt werden.");
            }
            RequireExpectedHash(document.Sha256, expectedSha256);
            var mutation = ApplySectionMutation(document.SourceMarkdown, operation, sectionId, heading, content);
            var updatedSource = mutation.Source;
            changed = mutation.Changed;
            if (changed)
            {
                expectedRevision = document.Revision;
                document = document with
                {
                    SourceMarkdown = updatedSource,
                    Sha256 = Hash(updatedSource),
                    Revision = checked(document.Revision + 1),
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
            }
        }

        var rendered = await RenderAsync(document.FileName, document.Format, document.SourceMarkdown, cancellationToken).ConfigureAwait(false);
        if (create)
        {
            document = await generatedDocuments.CreateAsync(document, cancellationToken).ConfigureAwait(false);
        }
        else if (changed)
        {
            document = await generatedDocuments.UpdateAsync(
                document.Id,
                document.SourceMarkdown,
                document.Sha256,
                expectedRevision!.Value,
                cancellationToken).ConfigureAwait(false);
        }
        var artifact = await ImportArtifactAsync(
            assistantMessageId, document, rendered.Bytes, rendered.ContentType, cancellationToken).ConfigureAwait(false);
        return new
        {
            documentId = document.Id,
            artifactId = artifact.Id,
            document.FileName,
            document.Format,
            document.Revision,
            document.Sha256,
            sectionId,
            changed,
            artifact.Length,
        };
    }

    private async Task<object> CreateWorkspaceDocumentAsync(
        string? workspacePath,
        string operation,
        string reference,
        string format,
        string sectionId,
        string? heading,
        string content,
        string? expectedSha256,
        CancellationToken cancellationToken)
    {
        var root = RequireWorkspace(workspacePath);
        var outputPath = ResolveWithinWorkspace(root, NormalizeOutputReference(reference, format), requireExisting: false);
        var sourcePath = CanonicalSourcePath(outputPath, format);
        _ = ResolveWithinWorkspace(root, Path.GetRelativePath(root, sourcePath), requireExisting: false);
        string original = File.Exists(sourcePath)
            ? await File.ReadAllTextAsync(sourcePath, cancellationToken).ConfigureAwait(false)
            : string.Empty;
        string updated;
        var changed = true;
        if (operation == "create")
        {
            if (File.Exists(sourcePath) || (outputPath != sourcePath && File.Exists(outputPath)))
            {
                var desired = BuildSection(sectionId, heading, content);
                if (string.Equals(original, desired, StringComparison.Ordinal))
                {
                    updated = original;
                    changed = false;
                }
                else
                {
                    throw new IOException("Das Workspace-Dokument existiert bereits. Lies es und verwende appendSection oder replaceSection mit expectedSha256.");
                }
            }
            else
            {
                updated = BuildSection(sectionId, heading, content);
            }
        }
        else
        {
            if (!File.Exists(sourcePath)) throw new FileNotFoundException("Die kanonische Dokumentquelle wurde nicht gefunden.", sourcePath);
            RequireExpectedHash(Hash(original), expectedSha256);
            (updated, changed) = ApplySectionMutation(original, operation, sectionId, heading, content);
        }

        if (updated.Length > MaximumSourceCharacters) throw new InvalidDataException("Das Dokument überschreitet die lokale Größenbegrenzung.");
        if (changed) await WriteAtomicAsync(sourcePath, updated, cancellationToken).ConfigureAwait(false);
        if (format == "pdf")
        {
            outputPath = await pdfExporter.EnsureCurrentAsync(sourcePath, sourceChanged: changed, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Die PDF-Ausgabe konnte nicht erzeugt werden.");
        }
        else if (format == "docx")
        {
            await codec.WriteDocxAsync(updated, outputPath, cancellationToken).ConfigureAwait(false);
        }
        else if (format == "text")
        {
            await WriteAtomicAsync(outputPath, ToPlainText(updated), cancellationToken).ConfigureAwait(false);
        }

        var outputInfo = new FileInfo(outputPath);
        return new
        {
            path = Path.GetRelativePath(root, outputPath).Replace('\\', '/'),
            sourcePath = Path.GetRelativePath(root, sourcePath).Replace('\\', '/'),
            format,
            sha256 = Hash(updated),
            sectionId,
            changed,
            length = outputInfo.Exists ? outputInfo.Length : Encoding.UTF8.GetByteCount(updated),
        };
    }

    private async Task<ChatArtifact> ImportArtifactAsync(
        Guid messageId,
        GeneratedDocument document,
        byte[] bytes,
        string contentType,
        CancellationToken cancellationToken)
    {
        var sha = Hash(bytes);
        await using var stream = new MemoryStream(bytes, writable: false);
        return await artifacts.ImportAsync(
            messageId,
            $"document-{document.Id:N}-r{document.Revision}",
            document.FileName,
            contentType,
            sha,
            bytes.LongLength,
            "document-tool",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["documentId"] = document.Id.ToString("D"),
                ["documentRevision"] = document.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["format"] = document.Format,
                ["sourceSha256"] = document.Sha256,
            },
            stream,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<RenderedDocument> RenderAsync(
        string fileName,
        string format,
        string source,
        CancellationToken cancellationToken)
    {
        if (format == "markdown") return new(Utf8.GetBytes(source), "text/markdown");
        if (format == "text") return new(Utf8.GetBytes(ToPlainText(source)), "text/plain");

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "GO", "GeneratedDocuments", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var sourcePath = Path.Combine(temporaryDirectory, Path.GetFileNameWithoutExtension(fileName) + ".md");
            await File.WriteAllTextAsync(sourcePath, source, Utf8, cancellationToken).ConfigureAwait(false);
            string outputPath;
            string contentType;
            if (format == "pdf")
            {
                outputPath = await pdfExporter.EnsureCurrentAsync(sourcePath, sourceChanged: true, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Die PDF-Ausgabe konnte nicht erzeugt werden.");
                contentType = "application/pdf";
            }
            else
            {
                outputPath = Path.Combine(temporaryDirectory, Path.GetFileNameWithoutExtension(fileName) + ".docx");
                await codec.WriteDocxAsync(source, outputPath, cancellationToken).ConfigureAwait(false);
                contentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
            }
            return new(await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false), contentType);
        }
        finally
        {
            try { Directory.Delete(temporaryDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static object BuildOutline(
        DocumentSource source,
        IReadOnlyList<DocumentUnit> units,
        int startUnit,
        int maximumUnits)
    {
        if (startUnit < 1 || startUnit > units.Count) throw new InvalidDataException("startUnit liegt außerhalb des Dokuments.");
        var selected = units.Skip(startUnit - 1).Take(Math.Clamp(maximumUnits, 1, MaximumUnits)).Select(static unit => new
        {
            unit = unit.Index,
            unit.SectionId,
            unit.Label,
            characters = unit.Text.Length,
            preview = Compact(unit.Text, 160),
        }).ToArray();
        return new
        {
            source.Reference,
            source.FileName,
            source.Format,
            source.Sha256,
            unitCount = units.Count,
            units = selected,
            truncated = startUnit - 1 + selected.Length < units.Count,
            nextStartUnit = startUnit - 1 + selected.Length < units.Count ? startUnit + selected.Length : (int?)null,
        };
    }

    private static object BuildReadWindow(
        DocumentSource source,
        IReadOnlyList<DocumentUnit> units,
        int startUnit,
        int characterOffset,
        int maximumUnits,
        int maximumCharacters)
    {
        if (startUnit < 1 || startUnit > units.Count) throw new InvalidDataException("startUnit liegt außerhalb des Dokuments.");
        maximumUnits = Math.Clamp(maximumUnits, 1, MaximumUnits);
        maximumCharacters = Math.Clamp(maximumCharacters, 1_000, MaximumCharacters);
        var remaining = maximumCharacters;
        var result = new List<object>();
        int? nextUnit = null;
        int? nextOffset = null;
        for (var index = startUnit - 1; index < units.Count && result.Count < maximumUnits && remaining > 0; index++)
        {
            var unit = units[index];
            var offset = index == startUnit - 1 ? characterOffset : 0;
            if (offset < 0 || offset > unit.Text.Length) throw new InvalidDataException("characterOffset liegt außerhalb der Einheit.");
            var available = unit.Text[offset..];
            var take = Math.Min(available.Length, remaining);
            var text = available[..take];
            result.Add(new { unit = unit.Index, unit.SectionId, unit.Label, characterOffset = offset, text });
            remaining -= take;
            if (take < available.Length)
            {
                nextUnit = unit.Index;
                nextOffset = offset + take;
                break;
            }
            if (index + 1 < units.Count)
            {
                nextUnit = units[index + 1].Index;
                nextOffset = 0;
            }
            else
            {
                nextUnit = null;
                nextOffset = null;
            }
        }
        var complete = nextUnit is null;
        return new
        {
            source.Reference,
            source.FileName,
            source.Format,
            source.Sha256,
            units = result,
            complete,
            continuation = nextUnit is null ? null : new { startUnit = nextUnit, characterOffset = nextOffset },
        };
    }

    private static object BuildSearch(
        DocumentSource source,
        IReadOnlyList<DocumentUnit> units,
        string query,
        int maximumUnits,
        int maximumCharacters)
    {
        var terms = SearchTermRegex().Matches(query.ToLowerInvariant())
            .Select(static match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .Take(16)
            .ToArray();
        if (terms.Length == 0) throw new InvalidDataException("Die Dokumentensuche enthält keinen nutzbaren Suchbegriff.");
        maximumUnits = Math.Clamp(maximumUnits, 1, MaximumUnits);
        maximumCharacters = Math.Clamp(maximumCharacters, 1_000, MaximumCharacters);
        var candidates = units.Select(unit =>
            {
                var normalized = unit.Text.ToLowerInvariant();
                var score = terms.Sum(term => CountOccurrences(normalized, term));
                var first = terms.Select(term => normalized.IndexOf(term, StringComparison.Ordinal)).Where(static index => index >= 0).DefaultIfEmpty(-1).Min();
                return new { unit, score, first };
            })
            .Where(static item => item.score > 0)
            .OrderByDescending(static item => item.score)
            .ThenBy(static item => item.unit.Index)
            .ToArray();
        var hits = candidates.Take(maximumUnits).ToArray();
        var remaining = maximumCharacters;
        var result = new List<object>();
        foreach (var hit in hits)
        {
            if (remaining <= 0) break;
            var start = Math.Max(0, hit.first - 240);
            var take = Math.Min(Math.Min(1_200, hit.unit.Text.Length - start), remaining);
            result.Add(new
            {
                unit = hit.unit.Index,
                hit.unit.SectionId,
                hit.unit.Label,
                hit.score,
                snippet = hit.unit.Text.Substring(start, take),
            });
            remaining -= take;
        }
        return new
        {
            source.Reference,
            source.FileName,
            source.Format,
            source.Sha256,
            query,
            results = result,
            truncated = result.Count < candidates.Length,
        };
    }

    private static IReadOnlyList<DocumentUnit> BuildUnits(DocumentSource source)
    {
        if (source.IsCanonicalMarkdown)
        {
            var marked = ParseMarkedSections(source.Pages.Single());
            if (marked.Count > 0) return marked;
            return ParseMarkdownSections(source.Pages.Single());
        }

        var result = new List<DocumentUnit>();
        for (var pageIndex = 0; pageIndex < source.Pages.Count; pageIndex++)
        {
            var text = source.Pages[pageIndex].Trim();
            if (text.Length == 0) continue;
            var parts = SplitBounded(text, 6_000);
            for (var part = 0; part < parts.Length; part++)
            {
                var label = source.IsPaged
                    ? parts.Length == 1 ? $"Seite {pageIndex + 1}" : $"Seite {pageIndex + 1}, Teil {part + 1}"
                    : source.Pages.Count == 1 ? $"Abschnitt {part + 1}" : $"Abschnitt {pageIndex + 1}.{part + 1}";
                result.Add(new DocumentUnit(result.Count + 1, null, label, parts[part]));
            }
        }
        if (result.Count == 0) throw new InvalidDataException("Das Dokument enthält keinen lesbaren Text.");
        return result;
    }

    private static List<DocumentUnit> ParseMarkedSections(string source)
    {
        var result = new List<DocumentUnit>();
        foreach (Match match in MarkedSectionRegex().Matches(source))
        {
            var content = match.Groups["content"].Value.Trim();
            var id = match.Groups["id"].Value;
            var heading = FirstHeadingRegex().Match(content);
            result.Add(new DocumentUnit(
                result.Count + 1,
                id,
                heading.Success ? heading.Groups[1].Value.Trim() : id,
                content));
        }
        return result;
    }

    private static IReadOnlyList<DocumentUnit> ParseMarkdownSections(string source)
    {
        var matches = MarkdownHeadingRegex().Matches(source).Cast<Match>().ToArray();
        if (matches.Length == 0)
        {
            return SplitBounded(source.Trim(), 6_000)
                .Select((text, index) => new DocumentUnit(index + 1, null, $"Abschnitt {index + 1}", text))
                .ToArray();
        }
        var result = new List<DocumentUnit>();
        if (matches[0].Index > 0 && !string.IsNullOrWhiteSpace(source[..matches[0].Index]))
        {
            result.Add(new DocumentUnit(1, null, "Einleitung", source[..matches[0].Index].Trim()));
        }
        for (var index = 0; index < matches.Length; index++)
        {
            var end = index + 1 < matches.Length ? matches[index + 1].Index : source.Length;
            var text = source[matches[index].Index..end].Trim();
            foreach (var part in SplitBounded(text, 6_000))
            {
                result.Add(new DocumentUnit(result.Count + 1, null, matches[index].Groups[1].Value.Trim(), part));
            }
        }
        return result;
    }

    private static string[] SplitBounded(string value, int maximum)
    {
        if (value.Length <= maximum) return [value];
        var result = new List<string>();
        var offset = 0;
        while (offset < value.Length)
        {
            var end = Math.Min(value.Length, offset + maximum);
            if (end < value.Length)
            {
                var boundary = value.LastIndexOfAny(['\n', '.', ';', ','], end - 1, end - offset);
                if (boundary > offset + maximum / 2) end = boundary + 1;
            }
            result.Add(value[offset..end].Trim());
            offset = end;
            while (offset < value.Length && char.IsWhiteSpace(value[offset])) offset++;
        }
        return result.Where(static item => item.Length > 0).ToArray();
    }

    internal static (string Source, bool Changed) ApplySectionMutation(
        string source,
        string operation,
        string sectionId,
        string? heading,
        string content)
    {
        var section = BuildSection(sectionId, heading, content);
        var matches = MarkedSectionRegex().Matches(source)
            .Cast<Match>()
            .Where(match => string.Equals(match.Groups["id"].Value, sectionId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (operation == "appendSection")
        {
            if (matches.Length > 0)
            {
                if (matches.Length == 1 && string.Equals(matches[0].Value.Trim(), section.Trim(), StringComparison.Ordinal))
                {
                    return (source, false);
                }
                throw new InvalidDataException($"Der Abschnitt '{sectionId}' existiert bereits. Verwende replaceSection.");
            }
            return ((source.TrimEnd() + "\n\n" + section).Trim() + "\n", true);
        }
        if (operation == "replaceSection")
        {
            if (matches.Length != 1)
            {
                throw new InvalidDataException($"Der Abschnitt '{sectionId}' wurde nicht eindeutig gefunden.");
            }
            if (string.Equals(matches[0].Value.Trim(), section.Trim(), StringComparison.Ordinal)) return (source, false);
            return (source.Remove(matches[0].Index, matches[0].Length).Insert(matches[0].Index, section), true);
        }
        throw new InvalidDataException("Die document.create-Operation ist ungültig.");
    }

    internal static string BuildSection(string sectionId, string? heading, string content)
    {
        if (!SectionIdRegex().IsMatch(sectionId))
        {
            throw new InvalidDataException("sectionId muss mit einem Buchstaben oder einer Ziffer beginnen und darf nur a-z, 0-9, Punkt, Unterstrich oder Bindestrich enthalten.");
        }
        var body = content.Trim();
        if (!string.IsNullOrWhiteSpace(heading))
        {
            body = $"## {heading.Trim()}\n\n{body}".TrimEnd();
        }
        return $"<!-- GO-DOCUMENT-SECTION:{sectionId} -->\n{body}\n<!-- /GO-DOCUMENT-SECTION:{sectionId} -->";
    }

    private static string NormalizeOutputFileName(string reference, string format)
    {
        var fileName = Path.GetFileName(reference.Trim());
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 220 || fileName.Any(char.IsControl))
        {
            throw new InvalidDataException("Der Dokumentdateiname ist ungültig.");
        }
        return NormalizeOutputReference(fileName, format);
    }

    private static string NormalizeOutputReference(string reference, string format)
    {
        var extension = format switch
        {
            "markdown" => ".md",
            "text" => ".txt",
            "docx" => ".docx",
            "pdf" => ".pdf",
            _ => throw new InvalidDataException("Das Dokumentformat ist ungültig."),
        };
        var normalized = string.Equals(Path.GetExtension(reference), extension, StringComparison.OrdinalIgnoreCase)
            ? reference
            : Path.ChangeExtension(reference, extension);
        var fileName = Path.GetFileName(normalized);
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.Length > 240
            || fileName is "." or ".."
            || fileName.EndsWith(' ')
            || fileName.EndsWith('.')
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || ReservedWindowsFileNames.Contains(Path.GetFileNameWithoutExtension(fileName)))
        {
            throw new InvalidDataException("Der Dokumentdateiname ist ungültig.");
        }
        return normalized;
    }

    private static string CanonicalSourcePath(string outputPath, string format) => format switch
    {
        "pdf" or "docx" or "text" => Path.ChangeExtension(outputPath, ".md"),
        _ => outputPath,
    };

    private static async Task WriteAtomicAsync(string path, string content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Der Dokumentpfad ist ungültig.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, content, Utf8, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
        }
    }

    private static string ToPlainText(string source) => string.Join('\n', source
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n')
        .Split('\n')
        .Where(static line => !SectionMarkerRegex().IsMatch(line))
        .Select(static line => InlineMarkdownRegex().Replace(line, string.Empty)))
        .Trim();

    private static string RequireWorkspace(string? workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            throw new InvalidOperationException("Für document.read/create ist im Coding-Modus ein gültiger Workspace erforderlich.");
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
    }

    private static string ResolveWithinWorkspace(string root, string reference, bool requireExisting)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 1_024)
        {
            throw new InvalidDataException("Die Dokumentreferenz ist ungültig.");
        }
        var combined = Path.IsPathFullyQualified(reference) ? reference : Path.Combine(root, reference);
        var full = Path.GetFullPath(combined);
        var prefix = root + Path.DirectorySeparatorChar;
        if (!string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
            && !full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Der Dokumentpfad liegt außerhalb des freigegebenen Workspace.");
        }
        if (requireExisting && !File.Exists(full) && !Directory.Exists(full))
        {
            throw new FileNotFoundException("Das Workspace-Dokument wurde nicht gefunden.", full);
        }
        RejectReparsePoints(root, full);
        return full;
    }

    private static void RejectReparsePoints(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".") return;
        var current = root;
        foreach (var segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current)) break;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("Dokumentpfade über Verknüpfungen oder Reparse Points sind nicht freigegeben.");
            }
        }
    }

    private static void RequireExpectedHash(string actual, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            throw new InvalidDataException("Zum Bearbeiten ist expectedSha256 aus dem letzten document.read/create-Ergebnis erforderlich.");
        }
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Das Dokument wurde seit dem Lesen geändert. Lies den aktuellen Stand erneut.");
        }
    }

    private static string RequiredString(JsonElement arguments, string name, bool allowEmpty = false)
    {
        if (!arguments.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"document tool benötigt '{name}'.");
        }
        var result = value.GetString() ?? string.Empty;
        if (!allowEmpty && string.IsNullOrWhiteSpace(result)) throw new InvalidDataException($"'{name}' darf nicht leer sein.");
        return result;
    }

    private static string? OptionalString(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int OptionalInteger(JsonElement arguments, string name, int fallback) =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : fallback;

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    private static string Compact(string value, int maximum)
    {
        var normalized = WhitespaceRegex().Replace(value, " ").Trim();
        return normalized.Length <= maximum ? normalized : normalized[..maximum] + "…";
    }

    private static string Hash(string value) => Hash(Utf8.GetBytes(value));
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private sealed record DocumentSource(
        string Reference,
        string FileName,
        string Format,
        string Sha256,
        IReadOnlyList<string> Pages,
        bool IsPaged = false,
        bool IsCanonicalMarkdown = false);

    private sealed record DocumentUnit(int Index, string? SectionId, string Label, string Text);
    private sealed record DocumentListItem(
        string Reference,
        string Source,
        string FileName,
        string Format,
        string Sha256,
        long? Revision = null,
        int? PageCount = null,
        string? Status = null);
    private sealed record RenderedDocument(byte[] Bytes, string ContentType);

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,127}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SectionIdRegex();

    [GeneratedRegex("<!--\\s*/?GO-DOCUMENT-SECTION:", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SectionMarkerRegex();

    [GeneratedRegex("<!-- GO-DOCUMENT-SECTION:(?<id>[a-z0-9][a-z0-9._-]{0,127}) -->\\s*(?<content>[\\s\\S]*?)\\s*<!-- /GO-DOCUMENT-SECTION:\\k<id> -->", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MarkedSectionRegex();

    [GeneratedRegex("^#{1,6}\\s+(.+)$", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex FirstHeadingRegex();

    [GeneratedRegex("^#{1,6}\\s+(.+)$", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex MarkdownHeadingRegex();

    [GeneratedRegex("[\\p{L}\\p{N}][\\p{L}\\p{N}._-]*", RegexOptions.CultureInvariant)]
    private static partial Regex SearchTermRegex();

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex("(?:\\*\\*|__|`|^#{1,6}\\s+)", RegexOptions.CultureInvariant)]
    private static partial Regex InlineMarkdownRegex();
}
