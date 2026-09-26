using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Research;
using GoAi.Server.Core.Runs;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text;
using System.Text.Json;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace GoAi.Server.Tests;

public sealed class WebResearchServiceTests
{
    [Fact]
    public void HtmlInlineMarkupPreservesExactApiNamesForTargetedFetch()
    {
        const string html = "<h2>Timeouts</h2><p><code><span>asyncio.</span><span>timeout</span></code> cancels the current task.</p>"
            + "<p>Use <strong>TaskGroup</strong> for subtasks.</p><script>fake timeout evidence</script>";
        var text = WebResearchService.ExtractFetchedContent(System.Text.Encoding.UTF8.GetBytes(html), "text/html", "utf-8");
        Assert.Equal("Timeouts asyncio.timeout cancels the current task. Use TaskGroup for subtasks.", text);
        var response = new WebFetchResponse("https://docs.python.org/3/library/asyncio-task.html", "text/html", text, true, DateTimeOffset.UtcNow, []);
        using var arguments = JsonDocument.Parse("""{"query":"asyncio.timeout"}""");
        var result = AgentToolExecutor.CreateTargetedFetchResult(response, arguments.RootElement);
        Assert.True(result.Found);
        Assert.Contains("asyncio.timeout cancels the current task.", Assert.Single(result.Matches).Text, StringComparison.Ordinal);
        Assert.Empty(result.MissingQueries);
    }

    [Fact]
    public void HtmlBlockAndTableBoundariesRemainSeparatedWhileInlineEntitiesStayLiteral()
    {
        const string html = "<p>one</p><p>two<br>three</p><table><tr><td>A</td><td>B</td></tr></table>"
            + "<p><code><span>x</span>&lt;<span>y</span></code> &amp; z</p><style>.fake { content: 'evidence'; }</style>";
        var text = WebResearchService.ExtractFetchedContent(System.Text.Encoding.UTF8.GetBytes(html), "text/html", "utf-8");
        Assert.Equal("one two three A B x<y & z", text);
    }

    [Fact]
    public void FetchUsesBrowserCompatibleRequestHeaders()
    {
        using var request = WebResearchService.CreateFetchRequest(new Uri("https://example.com/reference"));

        var userAgent = request.Headers.UserAgent.ToString();
        Assert.Contains("Mozilla/5.0", userAgent, StringComparison.Ordinal);
        Assert.Contains("GO-AI-Server/1.0", userAgent, StringComparison.Ordinal);
        Assert.Contains("text/html", string.Join(',', request.Headers.GetValues("Accept")), StringComparison.Ordinal);
        Assert.Contains("application/pdf", string.Join(',', request.Headers.GetValues("Accept")), StringComparison.Ordinal);
        Assert.Contains("wordprocessingml", string.Join(',', request.Headers.GetValues("Accept")), StringComparison.Ordinal);
        Assert.Contains("de-DE", string.Join(',', request.Headers.GetValues("Accept-Language")), StringComparison.Ordinal);
    }

    [Fact]
    public void FetchExtractsPdfTextAndPreservesPageEvidence()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4)
            .AddText("Classical mechanics reference", 12, new PdfPoint(40, 780), font);
        var bytes = builder.Build();

        var mediaType = WebResearchService.DetectMediaType(
            new Uri("https://example.com/reference"),
            "application/octet-stream",
            bytes);
        var content = WebResearchService.ExtractFetchedContent(bytes, mediaType, null);

        Assert.Equal("application/pdf", mediaType);
        Assert.Contains("[Seite 1]", content, StringComparison.Ordinal);
        Assert.Contains("Classical mechanics reference", content, StringComparison.Ordinal);
    }

    [Fact]
    public void FetchExtractsModernWordDocumentsWithoutExecutingContent()
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(
            stream,
            WordprocessingDocumentType.Document,
            autoSave: true))
        {
            var mainPart = document.AddMainDocumentPart();
            mainPart.Document = new Document(
                new Body(
                    new Paragraph(new Run(new Text("Newtonian mechanics"))),
                    new Paragraph(new Run(new Text("Force equals mass times acceleration")))));
        }
        var bytes = stream.ToArray();

        var mediaType = WebResearchService.DetectMediaType(
            new Uri("https://example.com/download?id=42"),
            "application/octet-stream",
            bytes);
        var content = WebResearchService.ExtractFetchedContent(bytes, mediaType, null);

        Assert.Equal(
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            mediaType);
        Assert.Contains("Newtonian mechanics", content, StringComparison.Ordinal);
        Assert.Contains("Force equals mass times acceleration", content, StringComparison.Ordinal);
    }

    [Fact]
    public void ForbiddenFetchBecomesActionableAlternativeSourceResult()
    {
        var exception = new HttpRequestException(
            "Forbidden",
            inner: null,
            HttpStatusCode.Forbidden);

        var failure = AgentToolExecutor.DescribeResearchFailure("web.fetch", exception);

        Assert.Equal("web.fetch.unavailable", failure.ErrorCode);
        Assert.False(failure.Retryable);
        Assert.Contains("HTTP 403", failure.Message, StringComparison.Ordinal);
        Assert.Contains("anderen Suchtreffer", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedFetchedMediaTypeIsReturnedToTheAgentAsARecoverableToolFailure()
    {
        var exception = new InvalidDataException("Unsupported web response media type image/png.");

        Assert.True(AgentToolExecutor.IsRecoverableResearchFailure("web.fetch", exception));
        var failure = AgentToolExecutor.DescribeResearchFailure("web.fetch", exception);
        Assert.Equal("web.fetch.unavailable", failure.ErrorCode);
        Assert.True(failure.Retryable);
        Assert.Contains("anderen Suchtreffer", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FetchWithoutPhraseReturnsOnlyABoundedPreview()
    {
        var response = new WebFetchResponse(
            "https://example.com/large-reference",
            "text/plain",
            new string('x', 80_000),
            IsUntrusted: true,
            DateTimeOffset.UtcNow,
            []);
        using var arguments = JsonDocument.Parse("""{"url":"https://example.com/large-reference"}""");

        var targeted = AgentToolExecutor.CreateTargetedFetchResult(response, arguments.RootElement);

        Assert.Equal("query_required", targeted.State);
        Assert.True(targeted.RequiresTargetedFetch);
        Assert.Equal(80_000, targeted.SourceCharacters);
        Assert.NotNull(targeted.Preview);
        Assert.InRange(targeted.Preview!.Length, 1, 2_000);
        Assert.Empty(targeted.Matches);
    }

    [Fact]
    public void FetchWithPhraseReturnsOnlyBoundedMatchWindows()
    {
        var response = new WebFetchResponse(
            "https://example.com/mechanics",
            "text/plain",
            new string('a', 10_000)
                + " Newtons zweites Gesetz lautet Kraft gleich Masse mal Beschleunigung. "
                + new string('z', 10_000),
            IsUntrusted: true,
            DateTimeOffset.UtcNow,
            []);
        using var arguments = JsonDocument.Parse("""
            {"url":"https://example.com/mechanics","query":"Newtons zweites Gesetz","contextCharacters":120,"maximumCharacters":1000}
            """);

        var targeted = AgentToolExecutor.CreateTargetedFetchResult(response, arguments.RootElement);

        Assert.Equal("matches_found", targeted.State);
        Assert.True(targeted.Found);
        var match = Assert.Single(targeted.Matches);
        Assert.Contains("Kraft gleich Masse", match.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('a', 500), match.Text, StringComparison.Ordinal);
        Assert.Null(targeted.Preview);
    }

    [Fact]
    public void MissingFetchPhraseIsReportedAuthoritatively()
    {
        var response = new WebFetchResponse(
            "https://example.com/mechanics",
            "text/plain",
            "Impuls und Energie",
            IsUntrusted: true,
            DateTimeOffset.UtcNow,
            []);
        using var arguments = JsonDocument.Parse("""
            {"url":"https://example.com/mechanics","query":"Lagrangefunktion"}
            """);

        var targeted = AgentToolExecutor.CreateTargetedFetchResult(response, arguments.RootElement);

        Assert.Equal("not_present", targeted.State);
        Assert.False(targeted.Found);
        Assert.Equal(["Lagrangefunktion"], targeted.MissingQueries);
        Assert.Empty(targeted.Matches);
    }

    [Theory]
    [InlineData("http://127.0.0.1/private")]
    [InlineData("http://192.168.0.1/router")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://100.64.0.1/carrier-grade-nat")]
    [InlineData("http://198.18.0.1/benchmark")]
    [InlineData("http://192.0.2.1/documentation")]
    [InlineData("http://[::]/unspecified")]
    [InlineData("http://[::ffff:127.0.0.1]/mapped-loopback")]
    [InlineData("http://[2001:db8::1]/documentation")]
    public async Task FetchBlocksPrivateAndMetadataAddresses(string url)
    {
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            WebResearchService.FetchAsync(new WebFetchRequest(url)));

        Assert.Contains("forbidden", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

}
