using System.Net.Http.Headers;
using System.Text.Json;
using System.Xml.Linq;
using GoAi.Contracts;

namespace GoAi.Server.Core.Research;

public sealed record ScientificWorkCandidate(string Title, string Url, string Provider, string? Identifier = null);

/// <summary>
/// Resolves publication identities through fixed scientific metadata APIs. Returned records are discovery data;
/// they become evidence only after the existing safety-checked fetch path verifies a concrete source passage.
/// </summary>
public sealed class ScientificMetadataService(IHttpClientFactory httpClientFactory)
{
    private const int MaximumCandidatesPerProvider = 5;

    public async Task<IReadOnlyList<ScientificWorkCandidate>> ResolveAsync(
        string query, DeepResearchProfile profile, CancellationToken cancellationToken = default)
    {
        if (profile is DeepResearchProfile.Web or DeepResearchProfile.MathematicalInvestigation) return [];
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var bounded = query.Trim();
        if (bounded.Length > 300) bounded = bounded[..300];
        var tasks = new[]
        {
            TryProviderAsync(() => CrossrefAsync(bounded, cancellationToken)),
            TryProviderAsync(() => OpenAlexAsync(bounded, cancellationToken)),
            TryProviderAsync(() => PubMedAsync(bounded, cancellationToken)),
            TryProviderAsync(() => ArxivAsync(bounded, cancellationToken)),
            TryProviderAsync(() => DataCiteAsync(bounded, cancellationToken)),
        };
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.SelectMany(static result => result)
            .Where(static candidate => Uri.TryCreate(candidate.Url, UriKind.Absolute, out var uri)
                && uri.Scheme is "https" or "http")
            .DistinctBy(static candidate => NormalizeIdentity(candidate), StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();
    }

    private async Task<IReadOnlyList<ScientificWorkCandidate>> CrossrefAsync(string query, CancellationToken token)
    {
        var root = await GetJsonAsync("https://api.crossref.org/works?rows=5&select=DOI,title,URL&query=" + Uri.EscapeDataString(query), token).ConfigureAwait(false);
        if (!root.TryGetProperty("message", out var message) || !message.TryGetProperty("items", out var items)) return [];
        return items.EnumerateArray().Take(MaximumCandidatesPerProvider).Select(item =>
        {
            var doi = String(item, "DOI");
            var title = item.TryGetProperty("title", out var titles) && titles.ValueKind == JsonValueKind.Array
                ? titles.EnumerateArray().FirstOrDefault().GetString() : null;
            var url = !string.IsNullOrWhiteSpace(doi) ? "https://doi.org/" + doi : String(item, "URL");
            return Candidate(title, url, "crossref", doi);
        }).WhereNotNull().ToArray();
    }

    private async Task<IReadOnlyList<ScientificWorkCandidate>> OpenAlexAsync(string query, CancellationToken token)
    {
        var root = await GetJsonAsync("https://api.openalex.org/works?per-page=5&search=" + Uri.EscapeDataString(query), token).ConfigureAwait(false);
        if (!root.TryGetProperty("results", out var items)) return [];
        return items.EnumerateArray().Take(MaximumCandidatesPerProvider).Select(item =>
        {
            var title = String(item, "display_name") ?? String(item, "title");
            var doi = String(item, "doi")?.Replace("https://doi.org/", "", StringComparison.OrdinalIgnoreCase);
            var url = item.TryGetProperty("primary_location", out var location) && location.ValueKind == JsonValueKind.Object
                ? String(location, "landing_page_url") : null;
            url ??= !string.IsNullOrWhiteSpace(doi) ? "https://doi.org/" + doi : String(item, "id");
            return Candidate(title, url, "openalex", String(item, "id") ?? doi);
        }).WhereNotNull().ToArray();
    }

    private async Task<IReadOnlyList<ScientificWorkCandidate>> PubMedAsync(string query, CancellationToken token)
    {
        var search = await GetJsonAsync("https://eutils.ncbi.nlm.nih.gov/entrez/eutils/esearch.fcgi?db=pubmed&retmode=json&retmax=5&term=" + Uri.EscapeDataString(query), token).ConfigureAwait(false);
        if (!search.TryGetProperty("esearchresult", out var result) || !result.TryGetProperty("idlist", out var ids)) return [];
        return ids.EnumerateArray().Take(MaximumCandidatesPerProvider)
            .Select(static id => id.GetString()).Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => new ScientificWorkCandidate("PubMed " + id, "https://pubmed.ncbi.nlm.nih.gov/" + id + "/", "pubmed", id))
            .ToArray();
    }

    private async Task<IReadOnlyList<ScientificWorkCandidate>> ArxivAsync(string query, CancellationToken token)
    {
        var xml = await GetTextAsync("https://export.arxiv.org/api/query?max_results=5&search_query=all:" + Uri.EscapeDataString(query), token).ConfigureAwait(false);
        var document = XDocument.Parse(xml, LoadOptions.None);
        XNamespace atom = "http://www.w3.org/2005/Atom";
        return document.Root?.Elements(atom + "entry").Take(MaximumCandidatesPerProvider).Select(entry =>
        {
            var url = entry.Element(atom + "id")?.Value.Trim();
            var title = entry.Element(atom + "title")?.Value.Trim();
            var identifier = url?.Split('/').LastOrDefault();
            return Candidate(title, url, "arxiv", identifier);
        }).WhereNotNull().ToArray() ?? [];
    }

    private async Task<IReadOnlyList<ScientificWorkCandidate>> DataCiteAsync(string query, CancellationToken token)
    {
        var root = await GetJsonAsync("https://api.datacite.org/dois?page[size]=5&query=" + Uri.EscapeDataString(query), token).ConfigureAwait(false);
        if (!root.TryGetProperty("data", out var items)) return [];
        return items.EnumerateArray().Take(MaximumCandidatesPerProvider).Select(item =>
        {
            var id = String(item, "id");
            var attributes = item.TryGetProperty("attributes", out var value) ? value : default;
            var title = attributes.ValueKind == JsonValueKind.Object && attributes.TryGetProperty("titles", out var titles)
                && titles.ValueKind == JsonValueKind.Array && titles.EnumerateArray().FirstOrDefault() is var first
                && first.ValueKind == JsonValueKind.Object ? String(first, "title") : null;
            var url = attributes.ValueKind == JsonValueKind.Object ? String(attributes, "url") : null;
            url ??= !string.IsNullOrWhiteSpace(id) ? "https://doi.org/" + id : null;
            return Candidate(title, url, "datacite", id);
        }).WhereNotNull().ToArray();
    }

    private async Task<JsonElement> GetJsonAsync(string url, CancellationToken token)
    {
        var text = await GetTextAsync(url, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<JsonElement>(text);
    }

    private async Task<string> GetTextAsync(string url, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("LocalAssistant-Research", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await httpClientFactory.CreateClient(nameof(ScientificMetadataService))
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<ScientificWorkCandidate>> TryProviderAsync(
        Func<Task<IReadOnlyList<ScientificWorkCandidate>>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or System.Xml.XmlException)
        {
            return [];
        }
    }

    private static ScientificWorkCandidate? Candidate(string? title, string? url, string provider, string? identifier) =>
        string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url) ? null
            : new(title.Trim(), url.Trim(), provider, identifier?.Trim());
    private static string? String(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
    private static string NormalizeIdentity(ScientificWorkCandidate candidate) =>
        string.IsNullOrWhiteSpace(candidate.Identifier) ? candidate.Url.TrimEnd('/') : candidate.Provider + ":" + candidate.Identifier;
}

internal static class ScientificCandidateEnumerableExtensions
{
    public static IEnumerable<T> WhereNotNull<T>(this IEnumerable<T?> values) where T : class =>
        values.Where(static value => value is not null).Select(static value => value!);
}
