using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using GoWinUI.Infrastructure.Storage;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace GoWinUI.Tests;

public sealed class BlobProjectAndDocumentTests
{
    [Fact]
    public async Task BlobStoreStreamsTwoMiBChunksDeduplicatesAndVerifies()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var store = environment.Get<IBinaryObjectStore>();
        var bytes = new byte[SqliteBinaryObjectStore.ChunkSize + 137];
        new Random(42).NextBytes(bytes);

        var first = await store.ImportAsync(new MemoryStream(bytes, writable: false), "application/octet-stream");
        var second = await store.ImportAsync(new MemoryStream(bytes, writable: false), "application/octet-stream");
        await using var exported = new MemoryStream();
        await store.ExportAsync(first.Id, exported);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(2, first.ChunkCount);
        Assert.Equal(bytes, exported.ToArray());
        Assert.True(await store.VerifyAsync(first.Id));

        await using var ranged = await store.OpenReadAsync(first.Id);
        Assert.True(ranged.CanSeek);
        var rangeStart = SqliteBinaryObjectStore.ChunkSize - 11;
        Assert.Equal(rangeStart, ranged.Seek(rangeStart, SeekOrigin.Begin));
        var range = new byte[64];
        Assert.Equal(range.Length, await ranged.ReadAsync(range));
        Assert.Equal(bytes.AsSpan(rangeStart, range.Length).ToArray(), range);
        ranged.Position = ranged.Length - 7;
        Assert.Equal(7, await ranged.ReadAsync(range));
    }

    [Fact]
    public async Task ProjectChecklistAndAssetRoundTripEnforceRevisions()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var projects = environment.Get<IProjectRepository>();
        var now = DateTimeOffset.UtcNow;
        var project = await projects.CreateAsync(new(Guid.Empty, "P1", "Bau", "D", "N", ProjectStatus.Active, 0, now, now));
        var item = await projects.SaveChecklistItemAsync(new(Guid.Empty, project.Id, "Prüfen", false, 10, 0, now, now));
        var blob = await environment.Get<IBinaryObjectStore>().ImportAsync(new MemoryStream([1, 2, 3]), "application/pdf");
        var asset = await projects.AddAssetAsync(new(Guid.Empty, project.Id, blob.Id, "plan.pdf", "application/pdf", AssetCategory.Pdf, null, blob.Sha256, blob.Length, 0, 0, now, now));
        var thumbnailBlob = await environment.Get<IBinaryObjectStore>().ImportAsync(new MemoryStream([9, 8, 7]), "image/png");
        await projects.SaveAssetThumbnailAsync(new(asset.Id, thumbnailBlob.Id, "image/png", 64, 48, now));

        await projects.ArchiveAsync(project.Id, project.Revision);
        Assert.Single(await projects.ListAsync(ProjectStatus.Archived));
        Assert.Single(await projects.ListChecklistAsync(project.Id));
        Assert.Single(await projects.ListAssetsAsync(project.Id));
        Assert.Equal(thumbnailBlob.Id, (await projects.GetAssetThumbnailAsync(asset.Id))?.BlobId);
        asset = await projects.UpdateAssetAsync(asset with { Title = "Freigegebener Ausführungsplan" }, asset.Revision);
        Assert.Equal("Freigegebener Ausführungsplan", Assert.Single(await projects.ListAssetsAsync(project.Id)).Title);
        await Assert.ThrowsAsync<RevisionConflictException>(() => projects.DeleteChecklistItemAsync(item.Id, 99));
        await projects.DeleteAssetAsync(asset.Id, asset.Revision);
        Assert.Empty(await projects.ListAssetsAsync(project.Id));
    }

    [Fact]
    public async Task SchemaThirteenStoresCpdbAndIfcProjectAssets()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var projects = environment.Get<IProjectRepository>();
        var blobs = environment.Get<IBinaryObjectStore>();
        var now = DateTimeOffset.UtcNow;
        var project = await projects.CreateAsync(new(Guid.Empty, "TGA", "BIM", "", "", ProjectStatus.Active, 0, now, now));
        var cpdbBlob = await blobs.ImportAsync(new MemoryStream([1]), "application/octet-stream");
        var ifcBlob = await blobs.ImportAsync(new MemoryStream([2]), "application/x-step");

        await projects.AddAssetAsync(new(Guid.Empty, project.Id, cpdbBlob.Id, "projekt.cpdb.vec", "application/octet-stream", AssetCategory.Cpdb, "C:\\TGA\\projekt.cpdb.vec", cpdbBlob.Sha256, cpdbBlob.Length, 0, 0, now, now));
        await projects.AddAssetAsync(new(Guid.Empty, project.Id, ifcBlob.Id, "modell.ifc", "application/x-step", AssetCategory.Ifc, "C:\\TGA\\modell.ifc", ifcBlob.Sha256, ifcBlob.Length, 1, 0, now, now));

        var assets = await projects.ListAssetsAsync(project.Id);
        Assert.Contains(assets, asset => asset.Category == AssetCategory.Cpdb);
        Assert.Contains(assets, asset => asset.Category == AssetCategory.Ifc);
        Assert.True(await environment.Get<IGoDatabase>().CheckIntegrityAsync());
    }

    [Fact]
    public async Task TextHtmlXmlAndRtfAreStoredWithOriginalAndExtractedContext()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Dokumente");
        var ingestor = environment.Get<IDocumentIngestor>();

        var text = await ingestor.ImportAsync(session.Id, "wissen.md", new MemoryStream(Encoding.UTF8.GetBytes("Hallo Welt")));
        var html = await ingestor.ImportAsync(session.Id, "seite.html", new MemoryStream(Encoding.UTF8.GetBytes("<script>bad()</script><p>Sicher &amp; gut</p>")));
        var xml = await ingestor.ImportAsync(session.Id, "daten.xml", new MemoryStream(Encoding.UTF8.GetBytes("<root><item>Wert</item></root>")));
        var rtf = await ingestor.ImportAsync(session.Id, "notiz.rtf", new MemoryStream(Encoding.ASCII.GetBytes(@"{\rtf1\ansi Hallo\par Welt}")));

        Assert.All(new[] { text, html, xml, rtf }, static result => Assert.True(result.Success, result.Error));
        Assert.Equal(4, (await ingestor.ListAsync(session.Id)).Count);
        Assert.Contains("Hallo Welt", (await ingestor.ReadPagesAsync(text.Document!.Id))[0].Text, StringComparison.Ordinal);
        var htmlText = (await ingestor.ReadPagesAsync(html.Document!.Id))[0].Text;
        Assert.Contains("Sicher & gut", htmlText, StringComparison.Ordinal);
        Assert.DoesNotContain("bad()", htmlText, StringComparison.Ordinal);
        Assert.Contains("Wert", (await ingestor.ReadPagesAsync(xml.Document!.Id))[0].Text, StringComparison.Ordinal);
        Assert.Contains("Welt", (await ingestor.ReadPagesAsync(rtf.Document!.Id))[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PdfAndDocxAreExtractedInTheDotNetBackend()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var session = await environment.Get<IChatRepository>().CreateSessionAsync("Office");
        var ingestor = environment.Get<IDocumentIngestor>();

        using var pdfBuilder = new PdfDocumentBuilder();
        var font = pdfBuilder.AddStandard14Font(Standard14Font.Helvetica);
        pdfBuilder.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddText("Erste PDF Seite", 12, new PdfPoint(40, 780), font);
        pdfBuilder.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddText("Zweite PDF Seite", 12, new PdfPoint(40, 780), font);
        var pdf = await ingestor.ImportAsync(session.Id, "probe.pdf", new MemoryStream(pdfBuilder.Build(), writable: false));

        var docxStream = new MemoryStream();
        using (var word = WordprocessingDocument.Create(docxStream, WordprocessingDocumentType.Document, autoSave: true))
        {
            var main = word.AddMainDocumentPart();
            main.Document = new Document(new Body(new Paragraph(new Run(new Text("DOCX Tabellen und Text")))));
        }
        var docx = await ingestor.ImportAsync(session.Id, "probe.docx", new MemoryStream(docxStream.ToArray(), writable: false));

        Assert.True(pdf.Success, pdf.Error);
        var pdfPages = await ingestor.ReadPagesAsync(pdf.Document!.Id);
        Assert.Equal(2, pdfPages.Count);
        Assert.Contains("Zweite PDF Seite", pdfPages[1].Text, StringComparison.Ordinal);
        Assert.True(docx.Success, docx.Error);
        Assert.Contains("DOCX Tabellen und Text", (await ingestor.ReadPagesAsync(docx.Document!.Id))[0].Text, StringComparison.Ordinal);
        var oldDoc = await ingestor.ImportAsync(session.Id, "alt.doc", new MemoryStream([1, 2, 3]));
        Assert.False(oldDoc.Success);
    }

    [Fact]
    public async Task PreparedDocumentsAreReusedAndSearchRemainsDiversifiedAcrossAttachments()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var session = await environment.Get<IChatRepository>().CreateSessionAsync("Persistenter Dokumentindex");
        var ingestor = environment.Get<IDocumentIngestor>();
        var firstBytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Heizlast Berechnung Bestand. ", 800)));
        var secondBytes = Encoding.UTF8.GetBytes("Die Auslegung der Lüftungsanlage nennt einen Volumenstrom von 4200 m3/h.");

        var first = await ingestor.ImportAsync(session.Id, "heizung.txt", new MemoryStream(firstBytes));
        var second = await ingestor.ImportAsync(session.Id, "lueftung.txt", new MemoryStream(secondBytes));
        Assert.True(first.Success, first.Error);
        Assert.True(second.Success, second.Error);
        Assert.All(await ingestor.ListAsync(session.Id), document => Assert.Equal(DocumentPreparationStatus.Ready, document.PreparationStatus));

        var hits = await ingestor.SearchAsync(session.Id, "Vergleiche Heizlast und Volumenstrom der Lüftungsanlage", 20_000);
        Assert.Contains(hits, hit => hit.FileName == "heizung.txt");
        Assert.Contains(hits, hit => hit.FileName == "lueftung.txt" && hit.PageNumber == 1);
        var assistant = await environment.Get<IChatRepository>().AddMessageAsync(session.Id, ChatRole.Assistant, "Analyse", MessageStatus.Completed);
        await ingestor.SaveEvidenceAsync(assistant.Id, hits);
        var citations = await ingestor.GetEvidenceCitationsAsync(assistant.Id);
        Assert.Contains("[heizung.txt, S. 1]", citations);
        Assert.Contains("[lueftung.txt, S. 1]", citations);

        await ingestor.RemoveAsync(second.Document!.Id);
        var rebound = await ingestor.ImportAsync(session.Id, "lueftung-neu.txt", new MemoryStream(secondBytes));
        Assert.True(rebound.Success, rebound.Error);
        Assert.True(rebound.Document!.WasReused);
        Assert.Equal("lueftung-neu.txt", rebound.Document.FileName);
        Assert.Contains("4200", (await ingestor.ReadPagesAsync(rebound.Document.Id)).Single().Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreparedSessionHistoryIsPersistedByRevisionAndModelBudget()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Persistenter Sitzungsverlauf");
        var through = await chats.AddMessageAsync(
            session.Id,
            ChatRole.Assistant,
            "Die Auslegung verwendet 4.200 m³/h.",
            MessageStatus.Completed);
        var preparation = new SessionContextPreparation(
            new string('a', 64),
            session.Id,
            new string('b', 64),
            "openai/gpt-oss-20b",
            120_000,
            through.Id,
            1,
            "Entscheidung: 4.200 m³/h bleiben als Auslegungswert erhalten.",
            DateTimeOffset.UtcNow);

        await chats.SaveSessionContextPreparationAsync(preparation);
        var restored = await chats.GetSessionContextPreparationAsync(preparation.CacheKey);
        var reusable = await chats.ListSessionContextPreparationsAsync(
            session.Id,
            preparation.ModelId,
            maximumMessageCount: 1);
        var wrongModel = await chats.ListSessionContextPreparationsAsync(
            session.Id,
            "qwen3-coder-next",
            maximumMessageCount: 1);

        Assert.NotNull(restored);
        Assert.Equal(preparation.SessionId, restored.SessionId);
        Assert.Equal(preparation.HistoryRevision, restored.HistoryRevision);
        Assert.Equal(preparation.ModelId, restored.ModelId);
        Assert.Equal(preparation.ContextBudget, restored.ContextBudget);
        Assert.Equal(preparation.ThroughMessageId, restored.ThroughMessageId);
        Assert.Equal(preparation.PreparedText, restored.PreparedText);
        Assert.Single(reusable);
        Assert.Equal(preparation.CacheKey, reusable[0].CacheKey);
        Assert.Empty(wrongModel);
    }

    [Fact]
    public async Task DocumentGroupStateTracksPreparationAndReturnsToReady()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Gruppenstatus");
        var ingestor = environment.Get<IDocumentIngestor>();
        Assert.True((await ingestor.ImportAsync(
            session.Id,
            "eins.txt",
            new MemoryStream(Encoding.UTF8.GetBytes("Erstes Dokument")))).Success);
        Assert.True((await ingestor.ImportAsync(
            session.Id,
            "zwei.txt",
            new MemoryStream(Encoding.UTF8.GetBytes("Zweites Dokument")))).Success);
        var message = await chats.AddMessageAsync(
            session.Id,
            ChatRole.Assistant,
            string.Empty,
            MessageStatus.Streaming);

        await ingestor.SetContextPreparationStateAsync(
            session.Id,
            message.Id,
            DocumentPreparationStatus.Preparing,
            40);
        var preparing = await ingestor.ListAsync(session.Id);
        Assert.All(preparing, static item =>
        {
            Assert.Equal(DocumentPreparationStatus.Preparing, item.PreparationStatus);
            Assert.Equal(40, item.PreparationProgress);
        });

        await ingestor.SetContextPreparationStateAsync(session.Id, message.Id, null);
        Assert.All(await ingestor.ListAsync(session.Id), static item =>
            Assert.Equal(DocumentPreparationStatus.Ready, item.PreparationStatus));
    }
}
