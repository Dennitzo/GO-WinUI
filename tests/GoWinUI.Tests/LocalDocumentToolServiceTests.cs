using System.Text.Json;
using GoWinUI.App.Services;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace GoWinUI.Tests;

public sealed class LocalDocumentToolServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SessionDocumentIsVersionedAndReadInBoundedUnits()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Dokumentwerkzeug");
        var firstMessage = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, string.Empty, MessageStatus.Streaming);
        using var exporter = new CodingSolutionPdfExporter(NullLogger<CodingSolutionPdfExporter>.Instance);
        var service = CreateService(environment, exporter);
        var longText = string.Join(' ', Enumerable.Repeat("Energieerhaltung und Impulsbilanz.", 500));

        var created = await service.CreateAsync(
            JsonSerializer.SerializeToElement(new
            {
                operation = "create",
                reference = "Mechanik.md",
                format = "markdown",
                sectionId = "mechanik.grundlagen",
                heading = "Grundlagen",
                content = longText,
            }),
            null,
            session.Id,
            firstMessage.Id,
            codingMode: false,
            CancellationToken.None);
        var createdJson = JsonSerializer.SerializeToElement(created, JsonOptions);
        var documentId = createdJson.GetProperty("documentId").GetGuid();
        var firstSha = createdJson.GetProperty("sha256").GetString()!;

        var secondMessage = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, string.Empty, MessageStatus.Streaming);
        var appended = await service.CreateAsync(
            JsonSerializer.SerializeToElement(new
            {
                operation = "appendSection",
                reference = documentId.ToString("D"),
                format = "markdown",
                sectionId = "mechanik.beispiel",
                heading = "Beispiel",
                content = "Ein Körper bewegt sich gleichförmig.",
                expectedSha256 = firstSha,
            }),
            null,
            session.Id,
            secondMessage.Id,
            codingMode: false,
            CancellationToken.None);
        var appendedJson = JsonSerializer.SerializeToElement(appended, JsonOptions);

        Assert.Equal(2, appendedJson.GetProperty("revision").GetInt64());
        Assert.NotEqual(firstSha, appendedJson.GetProperty("sha256").GetString());
        Assert.Single(await environment.Get<IChatArtifactRepository>().ListForMessageAsync(firstMessage.Id));
        Assert.Single(await environment.Get<IChatArtifactRepository>().ListForMessageAsync(secondMessage.Id));

        var listed = await service.ReadAsync(
            JsonSerializer.SerializeToElement(new
            {
                scope = "session",
                mode = "list",
                maximumUnits = 1,
            }),
            null,
            session.Id,
            CancellationToken.None);
        var listedJson = JsonSerializer.SerializeToElement(listed, JsonOptions);
        Assert.Equal(1, listedJson.GetProperty("total").GetInt32());
        Assert.Equal(documentId, listedJson.GetProperty("documents")[0].GetProperty("reference").GetGuid());

        var firstOutline = await service.ReadAsync(
            JsonSerializer.SerializeToElement(new
            {
                scope = "session",
                mode = "outline",
                reference = documentId.ToString("D"),
                maximumUnits = 1,
            }),
            null,
            session.Id,
            CancellationToken.None);
        var firstOutlineJson = JsonSerializer.SerializeToElement(firstOutline, JsonOptions);
        Assert.Equal(2, firstOutlineJson.GetProperty("nextStartUnit").GetInt32());
        var secondOutline = await service.ReadAsync(
            JsonSerializer.SerializeToElement(new
            {
                scope = "session",
                mode = "outline",
                reference = documentId.ToString("D"),
                startUnit = 2,
                maximumUnits = 1,
            }),
            null,
            session.Id,
            CancellationToken.None);
        Assert.Equal(
            "mechanik.beispiel",
            JsonSerializer.SerializeToElement(secondOutline, JsonOptions)
                .GetProperty("units")[0]
                .GetProperty("sectionId")
                .GetString());

        var window = await service.ReadAsync(
            JsonSerializer.SerializeToElement(new
            {
                scope = "session",
                mode = "read",
                reference = documentId.ToString("D"),
                startUnit = 1,
                maximumUnits = 1,
                maximumCharacters = 1_000,
            }),
            null,
            session.Id,
            CancellationToken.None);
        var windowJson = JsonSerializer.SerializeToElement(window, JsonOptions);
        var returnedText = windowJson.GetProperty("units")[0].GetProperty("text").GetString()!;

        Assert.True(returnedText.Length <= 1_000);
        Assert.Equal(1, windowJson.GetProperty("continuation").GetProperty("startUnit").GetInt32());
        Assert.True(windowJson.GetProperty("continuation").GetProperty("characterOffset").GetInt32() > 0);
    }

    [Fact]
    public async Task WorkspaceDocumentUsesStableSectionsAndOptimisticHash()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Path.Combine(environment.Directory, "workspace");
        Directory.CreateDirectory(workspace);
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Coding-Dokument");
        using var exporter = new CodingSolutionPdfExporter(NullLogger<CodingSolutionPdfExporter>.Instance);
        var service = CreateService(environment, exporter);

        var created = await service.CreateAsync(
            JsonSerializer.SerializeToElement(new
            {
                operation = "create",
                reference = "docs/handbuch.md",
                format = "markdown",
                sectionId = "start",
                heading = "Start",
                content = "Erster Inhalt",
            }),
            workspace,
            session.Id,
            Guid.NewGuid(),
            codingMode: true,
            CancellationToken.None);
        var createdJson = JsonSerializer.SerializeToElement(created, JsonOptions);
        var sha = createdJson.GetProperty("sha256").GetString()!;

        await service.CreateAsync(
            JsonSerializer.SerializeToElement(new
            {
                operation = "replaceSection",
                reference = "docs/handbuch.md",
                format = "markdown",
                sectionId = "start",
                heading = "Start",
                content = "Korrigierter Inhalt",
                expectedSha256 = sha,
            }),
            workspace,
            session.Id,
            Guid.NewGuid(),
            codingMode: true,
            CancellationToken.None);

        var source = await File.ReadAllTextAsync(Path.Combine(workspace, "docs", "handbuch.md"));
        Assert.Contains("Korrigierter Inhalt", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Erster Inhalt", source, StringComparison.Ordinal);
        await Assert.ThrowsAsync<IOException>(() => service.CreateAsync(
            JsonSerializer.SerializeToElement(new
            {
                operation = "appendSection",
                reference = "docs/handbuch.md",
                format = "markdown",
                sectionId = "weiter",
                content = "Weitere Daten",
                expectedSha256 = sha,
            }),
            workspace,
            session.Id,
            Guid.NewGuid(),
            codingMode: true,
            CancellationToken.None));
    }

    [Fact]
    public async Task WorkspaceTextDocumentKeepsEditableCanonicalSource()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Path.Combine(environment.Directory, "text-workspace");
        Directory.CreateDirectory(workspace);
        var session = await environment.Get<IChatRepository>().CreateSessionAsync("Text-Dokument");
        using var exporter = new CodingSolutionPdfExporter(NullLogger<CodingSolutionPdfExporter>.Instance);
        var service = CreateService(environment, exporter);

        await service.CreateAsync(
            JsonSerializer.SerializeToElement(new
            {
                operation = "create",
                reference = "notizen.txt",
                format = "text",
                sectionId = "einleitung",
                heading = "Einleitung",
                content = "Erster Absatz",
            }),
            workspace,
            session.Id,
            Guid.NewGuid(),
            codingMode: true,
            CancellationToken.None);
        var read = await service.ReadAsync(
            JsonSerializer.SerializeToElement(new
            {
                scope = "workspace",
                mode = "outline",
                reference = "notizen.txt",
            }),
            workspace,
            session.Id,
            CancellationToken.None);
        var readJson = JsonSerializer.SerializeToElement(read, JsonOptions);

        await service.CreateAsync(
            JsonSerializer.SerializeToElement(new
            {
                operation = "appendSection",
                reference = "notizen.txt",
                format = "text",
                sectionId = "ergebnis",
                heading = "Ergebnis",
                content = "Zweiter Absatz",
                expectedSha256 = readJson.GetProperty("sha256").GetString(),
            }),
            workspace,
            session.Id,
            Guid.NewGuid(),
            codingMode: true,
            CancellationToken.None);

        var source = await File.ReadAllTextAsync(Path.Combine(workspace, "notizen.md"));
        var output = await File.ReadAllTextAsync(Path.Combine(workspace, "notizen.txt"));
        Assert.Contains("GO-DOCUMENT-SECTION:einleitung", source, StringComparison.Ordinal);
        Assert.Contains("GO-DOCUMENT-SECTION:ergebnis", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GO-DOCUMENT-SECTION", output, StringComparison.Ordinal);
        Assert.Contains("Erster Absatz", output, StringComparison.Ordinal);
        Assert.Contains("Zweiter Absatz", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkspacePdfUsesDeterministicKatexRendererAndCanBeReadAgain()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Path.Combine(environment.Directory, "pdf-workspace");
        Directory.CreateDirectory(workspace);
        var session = await environment.Get<IChatRepository>().CreateSessionAsync("PDF-Dokument");
        using var exporter = new CodingSolutionPdfExporter(NullLogger<CodingSolutionPdfExporter>.Instance);
        var service = CreateService(environment, exporter);

        var created = await service.CreateAsync(
            JsonSerializer.SerializeToElement(new
            {
                operation = "create",
                reference = "bericht.pdf",
                format = "pdf",
                sectionId = "energie",
                heading = "Energie",
                content = "Die Energie-Masse-Beziehung lautet $$E = m c^2$$.",
            }),
            workspace,
            session.Id,
            Guid.NewGuid(),
            codingMode: true,
            CancellationToken.None);

        var pdfPath = Path.Combine(workspace, "bericht.pdf");
        Assert.True(new FileInfo(pdfPath).Length > 1_024);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString((await File.ReadAllBytesAsync(pdfPath))[..4]));
        var outline = await service.ReadAsync(
            JsonSerializer.SerializeToElement(new
            {
                scope = "workspace",
                mode = "outline",
                reference = "bericht.pdf",
                maximumUnits = 5,
            }),
            workspace,
            session.Id,
            CancellationToken.None);
        var outlineJson = JsonSerializer.SerializeToElement(outline, JsonOptions);
        Assert.True(outlineJson.GetProperty("unitCount").GetInt32() > 0);
        Assert.Equal(
            JsonSerializer.SerializeToElement(created, JsonOptions).GetProperty("sha256").GetString(),
            outlineJson.GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task DocxCodecRoundTripsReadableGermanContent()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var codec = environment.Get<IDocumentFileCodec>();
        var path = Path.Combine(environment.Directory, "bericht.docx");

        await codec.WriteDocxAsync("# Bericht\n\n## Ergebnis\n\nZwei Kilowatt Heizleistung.", path);
        var paragraphs = await codec.ReadAsync(path);

        Assert.Contains(paragraphs, paragraph => paragraph.Contains("Bericht", StringComparison.Ordinal));
        Assert.Contains(paragraphs, paragraph => paragraph.Contains("Zwei Kilowatt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SessionDocumentRejectsUnsafeWindowsFileNames()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Ungültiger Dokumentname");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, string.Empty, MessageStatus.Streaming);
        using var exporter = new CodingSolutionPdfExporter(NullLogger<CodingSolutionPdfExporter>.Instance);
        var service = CreateService(environment, exporter);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.CreateAsync(
            JsonSerializer.SerializeToElement(new
            {
                operation = "create",
                reference = "CON.pdf",
                format = "pdf",
                sectionId = "start",
                content = "Text",
            }),
            null,
            session.Id,
            message.Id,
            codingMode: false,
            CancellationToken.None));
    }

    private static LocalDocumentToolService CreateService(
        TestEnvironment environment,
        CodingSolutionPdfExporter exporter) => new(
            environment.Get<IGeneratedDocumentRepository>(),
            environment.Get<IDocumentIngestor>(),
            environment.Get<IChatArtifactRepository>(),
            environment.Get<IBinaryObjectStore>(),
            environment.Get<IChatRepository>(),
            environment.Get<IDocumentFileCodec>(),
            exporter);
}
