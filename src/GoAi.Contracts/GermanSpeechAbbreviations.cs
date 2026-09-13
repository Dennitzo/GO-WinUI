using System.Text.RegularExpressions;

namespace GoAi.Contracts;

public static partial class GermanSpeechAbbreviations
{
    private static readonly (Regex Pattern, string Spoken)[] Replacements = new[]
    {
        ("z. B.", "zum Beispiel"), ("d. h.", "das heißt"), ("u. a.", "unter anderem"),
        ("u. U.", "unter Umständen"), ("bzw.", "beziehungsweise"), ("ca.", "circa"),
        ("ggf.", "gegebenenfalls"), ("evtl.", "eventuell"), ("usw.", "und so weiter"),
        ("inkl.", "einschließlich"), ("exkl.", "ausschließlich"), ("Nr.", "Nummer"),
        ("Abb.", "Abbildung"), ("Abs.", "Absatz"), ("Dr.", "Doktor"), ("Prof.", "Professor"),
    }.Select(item => (new Regex(@"(?<![\p{L}\p{N}_])" + Regex.Escape(item.Item1).Replace(@"\ ", @"\s*")
        + @"(?![\p{L}\p{N}_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1)), item.Item2)).ToArray();

    public static string Expand(string text)
    {
        foreach (var (pattern, spoken) in Replacements) text = pattern.Replace(text, spoken);
        return text;
    }

    // Check the original stream before splitting it; "z." may be only the
    // first part of "z. B." and cannot be sent to speech yet.
    public static bool IsAbbreviationPeriod(string text, int index)
    {
        if (index < 0 || index >= text.Length || text[index] != '.') return false;
        var start = Math.Max(0, index - 24);
        return AbbreviationEnd().IsMatch(text.AsSpan(start, index - start + 1));
    }

    [GeneratedRegex(@"\b(?:bzw|ca|ggf|evtl|usw|inkl|exkl|Nr|Abb|Abs|Dr|Prof|[a-z])\.$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AbbreviationEnd();
}
