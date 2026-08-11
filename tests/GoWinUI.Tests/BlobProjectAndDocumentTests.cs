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
        await Assert.ThrowsAsync<RevisionConflictException>(() => projects.DeleteChecklistItemAsync(item.Id, 99));
        await projects.DeleteAssetAsync(asset.Id, asset.Revision);
        Assert.Empty(await projects.ListAssetsAsync(project.Id));
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
}
