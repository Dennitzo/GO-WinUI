using System.Net;
using System.Text;
using GoAi.Contracts;
using GoAi.Server.Core.Research;

namespace GoAi.Server.Tests;

public sealed class ScientificMetadataServiceTests
{
    [Fact]
    public async Task ScientificProfilesResolveStableIdentifiersAcrossStructuredProviders()
    {
        var handler = new FixtureHandler();
        var service = new ScientificMetadataService(new FixtureFactory(handler));

        var candidates = await service.ResolveAsync("local artificial intelligence research",
            DeepResearchProfile.ScientificEvidence);

        Assert.Contains(candidates, item => item.Provider == "crossref" && item.Identifier == "10.1000/example");
        Assert.Contains(candidates, item => item.Provider == "openalex" && item.Identifier == "https://openalex.org/W1");
        Assert.Contains(candidates, item => item.Provider == "pubmed" && item.Identifier == "12345");
        Assert.Contains(candidates, item => item.Provider == "arxiv" && item.Identifier == "2601.00001");
        Assert.Contains(candidates, item => item.Provider == "datacite" && item.Identifier == "10.9999/data");
        Assert.Equal(5, handler.Requests.Count);
    }

    [Fact]
    public async Task PlainWebAndMathematicsDoNotCallMetadataProviders()
    {
        var handler = new FixtureHandler();
        var service = new ScientificMetadataService(new FixtureFactory(handler));
        Assert.Empty(await service.ResolveAsync("ordinary question", DeepResearchProfile.Web));
        Assert.Empty(await service.ResolveAsync("prove x", DeepResearchProfile.MathematicalInvestigation));
        Assert.Empty(handler.Requests);
    }

    private sealed class FixtureFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var host = request.RequestUri!.Host;
            var content = host switch
            {
                "api.crossref.org" => "{\"message\":{\"items\":[{\"DOI\":\"10.1000/example\",\"title\":[\"Crossref Work\"],\"URL\":\"https://doi.org/10.1000/example\"}]}}",
                "api.openalex.org" => "{\"results\":[{\"id\":\"https://openalex.org/W1\",\"display_name\":\"OpenAlex Work\",\"doi\":\"https://doi.org/10.1000/open\",\"primary_location\":{\"landing_page_url\":\"https://doi.org/10.1000/open\"}}]}",
                "eutils.ncbi.nlm.nih.gov" => "{\"esearchresult\":{\"idlist\":[\"12345\"]}}",
                "export.arxiv.org" => "<?xml version=\"1.0\"?><feed xmlns=\"http://www.w3.org/2005/Atom\"><entry><id>https://arxiv.org/abs/2601.00001</id><title>Arxiv Work</title></entry></feed>",
                "api.datacite.org" => "{\"data\":[{\"id\":\"10.9999/data\",\"attributes\":{\"titles\":[{\"title\":\"Dataset\"}],\"url\":\"https://doi.org/10.9999/data\"}}]}",
                _ => throw new InvalidOperationException(host),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, host == "export.arxiv.org" ? "application/atom+xml" : "application/json"),
            });
        }
    }
}
