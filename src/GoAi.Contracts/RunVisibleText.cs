using System.Text;
using System.Text.RegularExpressions;

namespace GoAi.Contracts;

/// <summary>Canonical durable chat text, independent of desktop/UI assemblies.</summary>
public static partial class RunVisibleText
{
    [GeneratedRegex(@"GO(?:\\?_)?SESSION(?:\\?_)?TITLE\s*:\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedMarker();
    [GeneratedRegex(@"(?:\*\*|__|`)+\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingDelimiter();
    [GeneratedRegex(@"^\s*(?:\*\*|__|`)+", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingDelimiter();
    [GeneratedRegex(@"\n{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex ExcessLines();
    [GeneratedRegex(@"[\s#*_`]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonsemanticPrefix();

    public static string Canonicalize(string? content)
    {
        if (string.IsNullOrEmpty(content)) return string.Empty;
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (!HasMarker(normalized)) return normalized;
        var output = new StringBuilder(normalized.Length);
        foreach (var line in normalized.Split('\n'))
        {
            var marker = ReservedMarker().Match(line.Replace('\u00A0', ' '));
            var cleaned = line;
            if (marker.Success)
            {
                var prefix = TrailingDelimiter().Replace(line[..marker.Index], string.Empty).TrimEnd();
                var suffix = LeadingDelimiter().Replace(line[Math.Min(line.Length, marker.Index + marker.Length)..], string.Empty);
                suffix = TrailingDelimiter().Replace(suffix, string.Empty).Trim();
                if (NonsemanticPrefix().Replace(prefix, string.Empty).Length == 0) continue;
                cleaned = suffix.Length == 0 ? prefix : $"{prefix} {suffix}";
                while (HasMarker(cleaned)) cleaned = ReservedMarker().Replace(cleaned, string.Empty, 1).Trim();
            }
            if (output.Length > 0) output.Append('\n');
            output.Append(cleaned);
        }
        return ExcessLines().Replace(output.ToString().Trim(), "\n\n");
    }

    private static bool HasMarker(string content) => ReservedMarker().IsMatch(content.Replace('\u00A0', ' '));
}
