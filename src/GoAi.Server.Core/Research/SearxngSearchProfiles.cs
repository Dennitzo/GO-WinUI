using GoAi.Contracts;
using System.Net;
using System.Text.RegularExpressions;

namespace GoAi.Server.Core.Research;

/// <summary>Allowlisted engines on the same local SearXNG instance; no provider fallback.</summary>
internal static class SearxngSearchProfiles
{
    private static readonly Regex Python = new(@"\b(python|asyncio|cpython|pytest|pypi|multiprocessing|ProcessPoolExecutor|ThreadPoolExecutor|numpy|scipy|cupy|pytorch|threadpoolctl)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Web = new(@"\b(html|css|javascript|iframe|webview|webview2|dom|browser)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Dotnet = new(@"(?<!\w)(\.net|dotnet|c#|csharp|asp\.net|winui|powershell)(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    internal static bool IsValid(string? profile) => profile is null or "auto" or "general" or "python" or "web" or "dotnet" or "images";

    internal static string Select(string query) => Python.IsMatch(query) ? "python"
        : Dotnet.IsMatch(query) ? "dotnet" : Web.IsMatch(query) ? "web" : "general";

    internal static string SearchLanguage(string? profile, string query, string? requestedLanguage) =>
        (profile is null or "auto" ? Select(query) : profile) is "python" or "web" or "dotnet"
            || query.Contains("site:", StringComparison.OrdinalIgnoreCase) ? "all" : requestedLanguage ?? "de-DE";

    internal static string? Engines(string? profile, string query)
    {
        if (!IsValid(profile)) throw new ArgumentException("Search profile must be auto, general, python, web, dotnet or images.", nameof(profile));
        return (profile is null or "auto" ? Select(query) : profile) switch
        {
            // Include general web engines so official library documentation is reachable.
            // A forum-only profile can return no evidence even when SearXNG is healthy.
            "python" => "google cse,bing,brave,stackoverflow",
            "web" => "google cse,bing,brave,mdn,microsoft learn",
            "dotnet" => "google cse,bing,brave,microsoft learn,stackoverflow",
            "images" => null,
            _ => "google cse,bing,brave",
        };
    }
}

internal sealed class SearxngEngineUnavailableException(IReadOnlyList<SearchEngineFailure> failures)
    : HttpRequestException("SearXNG lieferte keine Treffer und meldet gestörte Engines: "
        + string.Join("; ", failures.Select(static failure => failure.Engine + ": " + failure.Reason))
        + ". Es wurde kein anderer Suchanbieter verwendet. Wiederhole die gesperrten Engines nicht unmittelbar.", null, HttpStatusCode.ServiceUnavailable)
{
    internal IReadOnlyList<SearchEngineFailure> Failures { get; } = failures;
}
