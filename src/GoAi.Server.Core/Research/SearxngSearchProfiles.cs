using GoAi.Contracts;
using System.Net;
using System.Text.RegularExpressions;

namespace GoAi.Server.Core.Research;

/// <summary>Allowlisted engines on the same local SearXNG instance; no extra requests or provider fallback.</summary>
internal static class SearxngSearchProfiles
{
    private static readonly Regex Python = new(@"\b(python|asyncio|cpython|pytest|pypi)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Web = new(@"\b(html|css|javascript|iframe|webview|webview2|dom|browser)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Dotnet = new(@"(?<!\w)(\.net|dotnet|c#|csharp|asp\.net|winui|powershell)(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    internal static bool IsValid(string? profile) => profile is null or "auto" or "general" or "python" or "web" or "dotnet";

    internal static string Select(string query) => Python.IsMatch(query) ? "python"
        : Dotnet.IsMatch(query) ? "dotnet" : Web.IsMatch(query) ? "web" : "general";

    internal static string? Engines(string? profile, string query)
    {
        if (!IsValid(profile)) throw new ArgumentException("Search profile must be auto, general, python, web or dotnet.", nameof(profile));
        return (profile == "auto" ? Select(query) : profile) switch
        {
            "python" => "discuss.python,stackoverflow",
            "web" => "mdn,microsoft learn",
            "dotnet" => "microsoft learn,stackoverflow",
            _ => null,
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
