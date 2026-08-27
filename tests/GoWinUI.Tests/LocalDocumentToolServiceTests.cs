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
        using var exporter = new DocumentPdfExporter(NullLogger<DocumentPdfExporter>.Instance);
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
            session.Id,
            firstMessage.Id,
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
            session.Id,
            secondMessage.Id,
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
            session.Id,
            CancellationToken.None);
        var windowJson = JsonSerializer.SerializeToElement(window, JsonOptions);
        var returnedText = windowJson.GetProperty("units")[0].GetProperty("text").GetString()!;

        Assert.True(returnedText.Length <= 1_000);
        Assert.Equal(1, windowJson.GetProperty("continuation").GetProperty("startUnit").GetInt32());
        Assert.True(windowJson.GetProperty("continuation").GetProperty("characterOffset").GetInt32() > 0);
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
        using var exporter = new DocumentPdfExporter(NullLogger<DocumentPdfExporter>.Instance);
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
            session.Id,
            message.Id,
            CancellationToken.None));
    }

    private static LocalDocumentToolService CreateService(
        TestEnvironment environment,
        DocumentPdfExporter exporter) => new(
            environment.Get<IGeneratedDocumentRepository>(),
            environment.Get<IDocumentIngestor>(),
            environment.Get<IChatArtifactRepository>(),
            environment.Get<IBinaryObjectStore>(),
            environment.Get<IChatRepository>(),
            environment.Get<IDocumentFileCodec>(),
            exporter);
}
