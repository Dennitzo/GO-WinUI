using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Research;

namespace GoWinUI.Infrastructure.Research;

public sealed class ScientificResearchExportService(
    IScientificResearchRepository repository,
    IDocumentFileCodec documents) : IScientificResearchExportService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<ScientificResearchExportBundle> ExportAllAsync(string projectId, string workspacePath,
        CancellationToken cancellationToken = default)
    {
        var project = await repository.GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Das Forschungsprojekt wurde nicht gefunden.");
        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
        if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.WorkspacePath ?? "")), workspace,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Der Export-Workspace gehört nicht zum Forschungsprojekt.");
        var results = await repository.LoadResultSnapshotAsync(projectId, cancellationToken).ConfigureAwait(false);
        var archive = await repository.LoadArchiveSnapshotAsync(projectId, cancellationToken).ConfigureAwait(false);
        var stamp = DateTimeOffset.UtcNow;
        var isResearchSandbox = string.Equals(Path.GetFileName(Path.GetDirectoryName(workspace)), "ResearchSandbox", StringComparison.OrdinalIgnoreCase);
        var exportRoot = isResearchSandbox
            ? Path.Combine(workspace, "artifacts", "exports")
            : Path.Combine(workspace, ".assistant", "research", projectId, "exports");
        var root = Path.Combine(exportRoot, stamp.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(root);
        var markdown = archive.Report?.ContentMarkdown ?? $"# Deep Research\n\n{project.InterpretedQuestion}\n";
        var files = new List<string>();
        await WriteAsync("report.md", markdown).ConfigureAwait(false);
        await WriteAsync("report.tex", Latex(project, markdown)).ConfigureAwait(false);
        await WriteAsync("literature.csv", Csv(archive.Works)).ConfigureAwait(false);
        await WriteAsync("bibliography.bib", BibTex(archive.Works)).ConfigureAwait(false);
        await WriteAsync("bibliography.ris", Ris(archive.Works)).ConfigureAwait(false);
        await WriteAsync("bibliography.csl.json", JsonSerializer.Serialize(archive.Works.Select(Csl), Json)).ConfigureAwait(false);
        await WriteAsync("claim-evidence-graph.json", JsonSerializer.Serialize(new { project, results.Claims, archive.Evidence }, Json)).ConfigureAwait(false);
        await WriteAsync("research-notebook.ipynb", Notebook(project, markdown, results.Experiments)).ConfigureAwait(false);
        await documents.WriteDocxAsync(markdown, Path.Combine(root, "report.docx"), cancellationToken).ConfigureAwait(false); files.Add("report.docx");
        await documents.WriteXlsxAsync(LiteratureTable(archive.Works), Path.Combine(root, "literature.xlsx"), cancellationToken).ConfigureAwait(false); files.Add("literature.xlsx");
        var pdfPath = Path.Combine(root, "report.pdf");
        await File.WriteAllBytesAsync(pdfPath, MinimalPdf(markdown), cancellationToken).ConfigureAwait(false); files.Add("report.pdf");
        var manifest = new
        {
            projectId, project.Revision, project.ProtocolVersion, project.Profile, project.VerificationLevel,
            createdAt = stamp, files = files.Order(StringComparer.Ordinal).Select(name => new
            {
                name, sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, name)))),
            }),
        };
        var manifestPath = Path.Combine(root, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, Json), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        files.Add("manifest.json");
        return new(root, files, manifestPath, stamp);

        async Task WriteAsync(string name, string content)
        {
            await File.WriteAllTextAsync(Path.Combine(root, name), content, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            files.Add(name);
        }
    }

    private static string Csv(IEnumerable<ResearchLiteratureEntry> works) =>
        "title,url,screeningStatus,evidenceLevel\r\n" + string.Join("\r\n", works.Select(work =>
            string.Join(',', Q(work.Title), Q(work.CanonicalUrl), Q(work.ScreeningStatus), Q(work.EvidenceLevel ?? "")))) + "\r\n";
    private static string Q(string value) => '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    private static string LiteratureTable(IEnumerable<ResearchLiteratureEntry> works) =>
        "Titel | URL | Screening | Evidenz\n" + string.Join('\n', works.Select(work => $"{work.Title} | {work.CanonicalUrl} | {work.ScreeningStatus} | {work.EvidenceLevel}"));
    private static string BibTex(IEnumerable<ResearchLiteratureEntry> works) => string.Join("\n\n", works.Select((work, index) =>
        $"@misc{{research{index + 1},\n  title = {{{work.Title.Replace("}", "\\}", StringComparison.Ordinal)}}},\n  url = {{{work.CanonicalUrl}}}\n}}"));
    private static string Ris(IEnumerable<ResearchLiteratureEntry> works) => string.Join("\r\n", works.Select(work =>
        $"TY  - GEN\r\nTI  - {work.Title}\r\nUR  - {work.CanonicalUrl}\r\nER  - \r\n"));
    private static object Csl(ResearchLiteratureEntry work) => new { id = work.WorkId, type = "article", title = work.Title, URL = work.CanonicalUrl };
    private static string Latex(ScientificResearchProject project, string markdown) =>
        "\\documentclass{article}\n\\usepackage[utf8]{inputenc}\n\\usepackage{hyperref}\n\\begin{document}\n\\section*{" + EscapeLatex(project.InterpretedQuestion) + "}\n\\begin{verbatim}\n" + markdown.Replace("\\end{verbatim}", "", StringComparison.Ordinal) + "\n\\end{verbatim}\n\\end{document}\n";
    private static string EscapeLatex(string value) => value.Replace("\\", "\\textbackslash{}", StringComparison.Ordinal).Replace("{", "\\{", StringComparison.Ordinal).Replace("}", "\\}", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("&", "\\&", StringComparison.Ordinal).Replace("#", "\\#", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
    private static string Notebook(ScientificResearchProject project, string markdown, IReadOnlyList<ResearchExperiment> experiments) =>
        JsonSerializer.Serialize(new { nbformat = 4, nbformat_minor = 5, metadata = new { projectId = project.Id }, cells = new object[]
        {
            new { cell_type = "markdown", metadata = new { }, source = markdown.Split('\n').Select(static line => line + "\n") },
            new { cell_type = "markdown", metadata = new { }, source = experiments.Select(item => $"Experiment `{item.Id}`: {item.VerificationStatus}\n") },
        } }, Json);

    private static byte[] MinimalPdf(string markdown)
    {
        var lines = markdown.Replace("\r", "", StringComparison.Ordinal).Split('\n').Where(static line => line.Length > 0).Take(45)
            .Select(static line => line.Length > 100 ? line[..100] : line).ToArray();
        var stream = new StringBuilder("BT /F1 10 Tf 50 790 Td 13 TL ");
        foreach (var line in lines) stream.Append('(').Append(PdfText(line)).Append(") Tj T* ");
        stream.Append("ET");
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(stream.ToString())} >>\nstream\n{stream}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var output = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int> { 0 };
        for (var index = 0; index < objects.Length; index++) { offsets.Add(Encoding.ASCII.GetByteCount(output.ToString())); output.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n"); }
        var xref = Encoding.ASCII.GetByteCount(output.ToString()); output.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) output.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        output.Append("trailer << /Size 6 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(output.ToString());
    }
    private static string PdfText(string value) => string.Concat(value.Normalize(NormalizationForm.FormD).Where(static c => c <= 127 && !char.IsControl(c)))
        .Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal);
}
