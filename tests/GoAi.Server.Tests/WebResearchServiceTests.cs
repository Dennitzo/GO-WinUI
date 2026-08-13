using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Research;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text;

namespace GoAi.Server.Tests;

public sealed class WebResearchServiceTests
{
    [Fact]
    public async Task YouTubeKeyUsesOfficialApiWithoutExposingKeyInUrl()
    {
        var handler = new YouTubeHandler();
        var service = new WebResearchService(
            new TestHttpClientFactory(handler),
            Options.Create(new GoAiServerOptions { YouTubeApiKey = "secret-youtube-key" }));

        var response = await service.SearchAsync(
            new WebSearchRequest("TGA Planung", 5, "de-DE"),
            youtubeFallback: true);

        Assert.Equal("youtube-data-api-v3", response.Provider);
        Assert.False(response.IsFallback);
        var result = Assert.Single(response.Results);
        Assert.Equal("TGA & Einführung", result.Title);
        Assert.Equal("https://www.youtube.com/watch?v=video-1", result.Url);
        Assert.Equal("Fachkanal", result.Source);
        Assert.Equal("1:02", result.Duration);
        Assert.Equal(2, handler.RequestCount);
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

    private sealed class YouTubeHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Assert.NotNull(request.RequestUri);
            Assert.DoesNotContain("key=", request.RequestUri.Query, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("secret-youtube-key", Assert.Single(request.Headers.GetValues("X-Goog-Api-Key")));
            var json = request.RequestUri.AbsolutePath.EndsWith("/search", StringComparison.Ordinal)
                ? """
                  {"items":[{"id":{"videoId":"video-1"},"snippet":{"title":"TGA &amp; Einführung","description":"Planungswissen","channelTitle":"Fachkanal","publishedAt":"2026-08-13T12:00:00Z","thumbnails":{"high":{"url":"https://example.invalid/high.jpg"}}}}]}
                  """
                : """
                  {"items":[{"id":"video-1","contentDetails":{"duration":"PT1M2S"}}]}
                  """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
