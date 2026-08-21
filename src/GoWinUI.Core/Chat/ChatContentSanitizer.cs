using System.Text;
using System.Text.RegularExpressions;

namespace GoWinUI.Core.Chat;

/// <summary>
/// Removes legacy model metadata from text before it crosses the durable chat boundary.
/// Session titles are transported separately and must never become visible message content.
/// </summary>
public static partial class ChatContentSanitizer
{
    [GeneratedRegex(@"GO(?:\\?_)?SESSION(?:\\?_)?TITLE\s*:\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedSessionTitleMarker();

    [GeneratedRegex(@"(?:\*\*|__|`)+\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingMarkdownDelimiter();

    [GeneratedRegex(@"^\s*(?:\*\*|__|`)+", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingMarkdownDelimiter();

    [GeneratedRegex(@"(?:\*\*|__|`)+\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex EndingMarkdownDelimiter();

    [GeneratedRegex(@"\n{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex ExcessBlankLines();

    public static string Sanitize(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (!ContainsReservedMarker(normalized))
        {
            return normalized;
        }

        var output = new StringBuilder(normalized.Length);
        foreach (var originalLine in normalized.Split('\n'))
        {
            var line = originalLine;
            var lineForMatching = line.Replace('\u00A0', ' ');
            var marker = ReservedSessionTitleMarker().Match(lineForMatching);
            if (!marker.Success)
            {
                AppendLine(output, line);
                continue;
            }

            var prefix = line[..marker.Index];
            var suffixStart = Math.Min(line.Length, marker.Index + marker.Length);
            var suffix = line[suffixStart..];
            prefix = TrailingMarkdownDelimiter().Replace(prefix, string.Empty).TrimEnd();
            suffix = LeadingMarkdownDelimiter().Replace(suffix, string.Empty);
            suffix = EndingMarkdownDelimiter().Replace(suffix, string.Empty).Trim();

            var semanticPrefix = Regex.Replace(prefix, @"[\s#*_`]+", string.Empty, RegexOptions.CultureInvariant);
            if (semanticPrefix.Length == 0)
            {
                continue;
            }

            var cleaned = suffix.Length == 0 ? prefix : $"{prefix} {suffix}";
            while (ContainsReservedMarker(cleaned))
            {
                cleaned = ReservedSessionTitleMarker().Replace(cleaned, string.Empty, 1).Trim();
            }
            AppendLine(output, cleaned);
        }

        return ExcessBlankLines().Replace(output.ToString().Trim(), "\n\n");
    }

    public static bool ContainsReservedMarker(string? content) =>
        !string.IsNullOrEmpty(content)
        && ReservedSessionTitleMarker().IsMatch(content.Replace('\u00A0', ' '));

    public static bool TryExtractLegacyTitle(string? content, out string title)
    {
        title = string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        foreach (var originalLine in content.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = originalLine.Replace('\u00A0', ' ');
            var marker = ReservedSessionTitleMarker().Match(line);
            if (!marker.Success)
            {
                continue;
            }

            var suffixStart = Math.Min(line.Length, marker.Index + marker.Length);
            title = EndingMarkdownDelimiter().Replace(line[suffixStart..], string.Empty)
                .Trim('*', '_', '`', ' ', '\u00A0');
            return true;
        }

        return false;
    }

    private static void AppendLine(StringBuilder output, string line)
    {
        if (output.Length > 0)
        {
            output.Append('\n');
        }
        output.Append(line);
    }
}
