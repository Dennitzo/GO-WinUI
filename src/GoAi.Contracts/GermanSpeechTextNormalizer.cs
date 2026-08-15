using System.Text;
using System.Text.RegularExpressions;

namespace GoAi.Contracts;

/// <summary>
/// Converts common KaTeX/LaTeX notation into German text that can be spoken by
/// ordinary TTS engines. The conversion is deterministic and does not require
/// another model run.
/// </summary>
public static partial class GermanSpeechTextNormalizer
{
    private static readonly Dictionary<string, string> Commands =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["alpha"] = "Alpha", ["beta"] = "Beta", ["gamma"] = "Gamma",
            ["delta"] = "Delta", ["epsilon"] = "Epsilon", ["varepsilon"] = "Epsilon",
            ["zeta"] = "Zeta", ["eta"] = "Eta", ["theta"] = "Theta",
            ["vartheta"] = "Theta", ["iota"] = "Jota", ["kappa"] = "Kappa",
            ["lambda"] = "Lambda", ["mu"] = "Mü", ["nu"] = "Nü",
            ["xi"] = "Xi", ["omicron"] = "Omikron", ["pi"] = "Pi",
            ["varpi"] = "Pi", ["rho"] = "Rho", ["varrho"] = "Rho",
            ["sigma"] = "Sigma", ["varsigma"] = "Sigma", ["tau"] = "Tau",
            ["upsilon"] = "Ypsilon", ["phi"] = "Phi", ["varphi"] = "Phi",
            ["chi"] = "Chi", ["psi"] = "Psi", ["omega"] = "Omega",
            ["Gamma"] = "Gamma", ["Delta"] = "Delta", ["Theta"] = "Theta",
            ["Lambda"] = "Lambda", ["Xi"] = "Xi", ["Pi"] = "Pi",
            ["Sigma"] = "Sigma", ["Upsilon"] = "Ypsilon", ["Phi"] = "Phi",
            ["Psi"] = "Psi", ["Omega"] = "Omega",
            ["cdot"] = "mal", ["times"] = "mal", ["ast"] = "mal",
            ["div"] = "geteilt durch", ["pm"] = "plus oder minus",
            ["mp"] = "minus oder plus", ["le"] = "kleiner oder gleich",
            ["leq"] = "kleiner oder gleich", ["ge"] = "größer oder gleich",
            ["geq"] = "größer oder gleich", ["ne"] = "ungleich",
            ["neq"] = "ungleich", ["approx"] = "ungefähr gleich",
            ["simeq"] = "ungefähr gleich", ["equiv"] = "äquivalent zu",
            ["propto"] = "proportional zu", ["in"] = "ist Element von",
            ["notin"] = "ist kein Element von", ["subset"] = "ist Teilmenge von",
            ["subseteq"] = "ist Teilmenge oder gleich", ["cup"] = "vereinigt mit",
            ["cap"] = "geschnitten mit", ["to"] = "geht gegen",
            ["rightarrow"] = "geht gegen", ["longrightarrow"] = "geht gegen",
            ["leftarrow"] = "kommt von", ["leftrightarrow"] = "genau dann wenn",
            ["Rightarrow"] = "daraus folgt", ["Leftrightarrow"] = "genau dann wenn",
            ["infty"] = "unendlich", ["sum"] = "Summe", ["prod"] = "Produkt",
            ["int"] = "Integral", ["iint"] = "Doppelintegral", ["iiint"] = "Dreifachintegral",
            ["oint"] = "Kurvenintegral", ["partial"] = "partielle Ableitung",
            ["nabla"] = "Nabla", ["lim"] = "Grenzwert", ["min"] = "Minimum",
            ["max"] = "Maximum", ["sin"] = "Sinus", ["cos"] = "Kosinus",
            ["tan"] = "Tangens", ["cot"] = "Kotangens", ["arcsin"] = "Arkussinus",
            ["arccos"] = "Arkuskosinus", ["arctan"] = "Arkustangens",
            ["ln"] = "natürlicher Logarithmus", ["log"] = "Logarithmus",
            ["exp"] = "Exponentialfunktion", ["forall"] = "für alle",
            ["exists"] = "es existiert", ["neg"] = "nicht", ["land"] = "und",
            ["lor"] = "oder", ["degree"] = "Grad", ["circ"] = "Grad",
        };

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = value;
        text = DisplayDollarMathRegex().Replace(text, static match => SpeakDelimited(match.Groups[1].Value));
        text = DisplayBracketMathRegex().Replace(text, static match => SpeakDelimited(match.Groups[1].Value));
        text = InlineParenthesisMathRegex().Replace(text, static match => SpeakDelimited(match.Groups[1].Value));
        text = InlineDollarMathRegex().Replace(text, static match => SpeakDelimited(match.Groups[1].Value));

        // AI answers occasionally contain raw LaTeX commands without delimiters.
        // Structural commands still need parsing, while ordinary prose operators
        // must remain untouched.
        if (text.Contains('\\'))
        {
            text = NormalizeExpression(text, mathOperators: false);
        }

        text = NormalizeUnicodeMathematics(text);
        return WhitespaceRegex().Replace(text, " ").Trim();
    }

    private static string SpeakDelimited(string expression) =>
        $" {NormalizeExpression(expression, mathOperators: true)} ";

    private static string NormalizeExpression(string expression, bool mathOperators)
    {
        var text = expression;

        foreach (var command in new[] { "frac", "dfrac", "tfrac" })
        {
            text = ReplaceBracedCommand(text, command, 2, static arguments =>
                $"{NormalizeExpression(arguments[0], true)} geteilt durch {NormalizeExpression(arguments[1], true)}");
        }
        text = ReplaceBracedCommand(text, "binom", 2, static arguments =>
            $"{NormalizeExpression(arguments[0], true)} über {NormalizeExpression(arguments[1], true)}");
        text = ReplaceSquareRoot(text);
        text = ReplaceBracedCommand(text, "SI", 2, static arguments =>
            $"{NormalizeExpression(arguments[0], true)} {NormalizeExpression(arguments[1], true)}");

        foreach (var command in new[] { "text", "textrm", "textnormal", "mathrm", "mathbf", "mathit", "mathsf", "mathtt", "operatorname" })
        {
            text = ReplaceBracedCommand(text, command, 1, static arguments => NormalizeExpression(arguments[0], false));
        }
        text = ReplaceBracedCommand(text, "dot", 1, static arguments => $"{NormalizeExpression(arguments[0], true)} Punkt");
        text = ReplaceBracedCommand(text, "ddot", 1, static arguments => $"{NormalizeExpression(arguments[0], true)} Doppelpunkt");
        text = ReplaceBracedCommand(text, "hat", 1, static arguments => $"{NormalizeExpression(arguments[0], true)} Dach");
        text = ReplaceBracedCommand(text, "bar", 1, static arguments => $"{NormalizeExpression(arguments[0], true)} Querstrich");
        text = ReplaceBracedCommand(text, "overline", 1, static arguments => $"{NormalizeExpression(arguments[0], true)} Querstrich");
        text = ReplaceBracedCommand(text, "vec", 1, static arguments => $"Vektor {NormalizeExpression(arguments[0], true)}");

        text = EnvironmentRegex().Replace(text, string.Empty);
        text = text.Replace(@"\\", "; ", StringComparison.Ordinal);
        text = SpacingCommandRegex().Replace(text, " ");
        text = text.Replace(@"\%", " Prozent ", StringComparison.Ordinal)
            .Replace(@"\&", " und ", StringComparison.Ordinal)
            .Replace(@"\_", " Unterstrich ", StringComparison.Ordinal)
            .Replace(@"\#", " Nummer ", StringComparison.Ordinal);

        text = BracedSuperscriptRegex().Replace(text, static match =>
            $" hoch {NormalizeExpression(match.Groups[1].Value, true)} ");
        text = SimpleSuperscriptRegex().Replace(text, static match => $" hoch {match.Groups[1].Value} ");
        text = BracedSubscriptRegex().Replace(text, static match =>
            $" Index {NormalizeExpression(match.Groups[1].Value, true)} ");
        text = SimpleSubscriptRegex().Replace(text, static match => $" Index {match.Groups[1].Value} ");

        text = CommandRegex().Replace(text, static match =>
            Commands.TryGetValue(match.Groups[1].Value, out var spoken)
                ? $" {spoken} "
                : $" {SplitCommandName(match.Groups[1].Value)} ");

        text = text.Replace('{', ' ').Replace('}', ' ').Replace('&', ',');
        if (mathOperators)
        {
            text = text.Replace("≤", " kleiner oder gleich ", StringComparison.Ordinal)
                .Replace("≥", " größer oder gleich ", StringComparison.Ordinal)
                .Replace("≠", " ungleich ", StringComparison.Ordinal)
                .Replace("≈", " ungefähr gleich ", StringComparison.Ordinal)
                .Replace("∞", " unendlich ", StringComparison.Ordinal)
                .Replace("∑", " Summe ", StringComparison.Ordinal)
                .Replace("∫", " Integral ", StringComparison.Ordinal)
                .Replace("√", " Wurzel aus ", StringComparison.Ordinal)
                .Replace("×", " mal ", StringComparison.Ordinal)
                .Replace("÷", " geteilt durch ", StringComparison.Ordinal)
                .Replace("·", " mal ", StringComparison.Ordinal)
                .Replace("=", " gleich ", StringComparison.Ordinal)
                .Replace("+", " plus ", StringComparison.Ordinal)
                .Replace("-", " minus ", StringComparison.Ordinal)
                .Replace("/", " geteilt durch ", StringComparison.Ordinal)
                .Replace("<", " kleiner als ", StringComparison.Ordinal)
                .Replace(">", " größer als ", StringComparison.Ordinal)
                .Replace("(", " Klammer auf ", StringComparison.Ordinal)
                .Replace(")", " Klammer zu ", StringComparison.Ordinal)
                .Replace("[", " eckige Klammer auf ", StringComparison.Ordinal)
                .Replace("]", " eckige Klammer zu ", StringComparison.Ordinal);
        }

        text = text.Replace('\\', ' ');
        return WhitespaceRegex().Replace(text, " ").Trim();
    }

    private static string ReplaceSquareRoot(string input)
    {
        const string command = @"\sqrt";
        var searchFrom = 0;
        while (TryFindCommand(input, command, searchFrom, out var commandIndex))
        {
            var cursor = SkipWhitespace(input, commandIndex + command.Length);
            string? degree = null;
            if (cursor < input.Length && input[cursor] == '['
                && TryReadBalanced(input, cursor, '[', ']', out var degreeValue, out var afterDegree))
            {
                degree = NormalizeExpression(degreeValue, true);
                cursor = SkipWhitespace(input, afterDegree);
            }
            if (cursor >= input.Length || input[cursor] != '{'
                || !TryReadBalanced(input, cursor, '{', '}', out var radicand, out var afterRadicand))
            {
                searchFrom = commandIndex + command.Length;
                continue;
            }

            var spoken = degree is null
                ? $"Wurzel aus {NormalizeExpression(radicand, true)}"
                : $"{SpeakRootDegree(degree)} Wurzel aus {NormalizeExpression(radicand, true)}";
            input = string.Concat(input.AsSpan(0, commandIndex), spoken, input.AsSpan(afterRadicand));
            searchFrom = commandIndex + spoken.Length;
        }
        return input;
    }

    private static string SpeakRootDegree(string degree) => degree switch
    {
        "2" => "zweite",
        "3" => "dritte",
        "4" => "vierte",
        "5" => "fünfte",
        "6" => "sechste",
        "7" => "siebte",
        "8" => "achte",
        "9" => "neunte",
        "10" => "zehnte",
        _ => degree + ".",
    };

    private static string ReplaceBracedCommand(
        string input,
        string commandName,
        int argumentCount,
        Func<IReadOnlyList<string>, string> replacement)
    {
        var command = "\\" + commandName;
        var searchFrom = 0;
        while (TryFindCommand(input, command, searchFrom, out var commandIndex))
        {
            var cursor = commandIndex + command.Length;
            var arguments = new List<string>(argumentCount);
            for (var index = 0; index < argumentCount; index++)
            {
                cursor = SkipWhitespace(input, cursor);
                if (cursor >= input.Length || input[cursor] != '{'
                    || !TryReadBalanced(input, cursor, '{', '}', out var argument, out cursor))
                {
                    arguments.Clear();
                    break;
                }
                arguments.Add(argument);
            }
            if (arguments.Count != argumentCount)
            {
                searchFrom = commandIndex + command.Length;
                continue;
            }

            var spoken = replacement(arguments);
            input = string.Concat(input.AsSpan(0, commandIndex), spoken, input.AsSpan(cursor));
            searchFrom = commandIndex + spoken.Length;
        }
        return input;
    }

    private static bool TryFindCommand(string input, string command, int startIndex, out int index)
    {
        index = input.IndexOf(command, startIndex, StringComparison.Ordinal);
        while (index >= 0)
        {
            var after = index + command.Length;
            if (after >= input.Length || !char.IsLetter(input[after]))
            {
                return true;
            }
            index = input.IndexOf(command, after, StringComparison.Ordinal);
        }
        return false;
    }

    private static int SkipWhitespace(string input, int index)
    {
        while (index < input.Length && char.IsWhiteSpace(input[index]))
        {
            index++;
        }
        return index;
    }

    private static bool TryReadBalanced(
        string input,
        int openingIndex,
        char opening,
        char closing,
        out string value,
        out int nextIndex)
    {
        var depth = 0;
        for (var index = openingIndex; index < input.Length; index++)
        {
            if (input[index] == opening)
            {
                depth++;
            }
            else if (input[index] == closing && --depth == 0)
            {
                value = input[(openingIndex + 1)..index];
                nextIndex = index + 1;
                return true;
            }
        }
        value = string.Empty;
        nextIndex = openingIndex;
        return false;
    }

    private static string SplitCommandName(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        foreach (var character in value)
        {
            if (builder.Length > 0 && char.IsUpper(character) && !char.IsUpper(builder[^1]))
            {
                builder.Append(' ');
            }
            builder.Append(character);
        }
        return builder.ToString();
    }

    private static string NormalizeUnicodeMathematics(string input)
    {
        var text = UnitDivisionRegex().Replace(input, " geteilt durch ");
        var symbols = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["α"] = " Alpha ", ["β"] = " Beta ", ["γ"] = " Gamma ", ["δ"] = " Delta ",
            ["ε"] = " Epsilon ", ["ζ"] = " Zeta ", ["η"] = " Eta ", ["θ"] = " Theta ",
            ["ι"] = " Jota ", ["κ"] = " Kappa ", ["λ"] = " Lambda ", ["μ"] = " Mü ",
            ["ν"] = " Nü ", ["ξ"] = " Xi ", ["ο"] = " Omikron ", ["π"] = " Pi ",
            ["ρ"] = " Rho ", ["σ"] = " Sigma ", ["τ"] = " Tau ", ["υ"] = " Ypsilon ",
            ["φ"] = " Phi ", ["χ"] = " Chi ", ["ψ"] = " Psi ", ["ω"] = " Omega ",
            ["Γ"] = " Gamma ", ["Δ"] = " Delta ", ["Θ"] = " Theta ", ["Λ"] = " Lambda ",
            ["Ξ"] = " Xi ", ["Π"] = " Pi ", ["Σ"] = " Sigma ", ["Φ"] = " Phi ",
            ["Ψ"] = " Psi ", ["Ω"] = " Omega ", ["≤"] = " kleiner oder gleich ",
            ["≥"] = " größer oder gleich ", ["≠"] = " ungleich ", ["≈"] = " ungefähr gleich ",
            ["∞"] = " unendlich ", ["∑"] = " Summe ", ["∏"] = " Produkt ",
            ["∫"] = " Integral ", ["√"] = " Wurzel aus ", ["×"] = " mal ",
            ["÷"] = " geteilt durch ", ["·"] = " mal ", ["−"] = " minus ",
            ["→"] = " geht gegen ", ["⇒"] = " daraus folgt ", ["∂"] = " partielle Ableitung ",
            ["∇"] = " Nabla ",
        };
        foreach (var (symbol, spoken) in symbols)
        {
            text = text.Replace(symbol, spoken, StringComparison.Ordinal);
        }

        text = SuperscriptUnicodeRegex().Replace(text, static match =>
            $" hoch {TranslateScriptDigits(match.Value)} ");
        text = SubscriptUnicodeRegex().Replace(text, static match =>
            $" Index {TranslateScriptDigits(match.Value)} ");
        text = EqualsRegex().Replace(text, " gleich ");
        return text;
    }

    private static string TranslateScriptDigits(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(character switch
            {
                '⁰' or '₀' => '0',
                '¹' or '₁' => '1',
                '²' or '₂' => '2',
                '³' or '₃' => '3',
                '⁴' or '₄' => '4',
                '⁵' or '₅' => '5',
                '⁶' or '₆' => '6',
                '⁷' or '₇' => '7',
                '⁸' or '₈' => '8',
                '⁹' or '₉' => '9',
                '⁺' or '₊' => '+',
                '⁻' or '₋' => '-',
                _ => character,
            });
        }
        return builder.ToString()
            .Replace("+", "plus ", StringComparison.Ordinal)
            .Replace("-", "minus ", StringComparison.Ordinal)
            .Trim();
    }

    [GeneratedRegex(@"\$\$([\s\S]*?)\$\$", RegexOptions.CultureInvariant)]
    private static partial Regex DisplayDollarMathRegex();

    [GeneratedRegex(@"\\\[([\s\S]*?)\\\]", RegexOptions.CultureInvariant)]
    private static partial Regex DisplayBracketMathRegex();

    [GeneratedRegex(@"\\\(([\s\S]*?)\\\)", RegexOptions.CultureInvariant)]
    private static partial Regex InlineParenthesisMathRegex();

    [GeneratedRegex(@"(?<!\\)\$(?!\$)([^$\r\n]+?)(?<!\\)\$", RegexOptions.CultureInvariant)]
    private static partial Regex InlineDollarMathRegex();

    [GeneratedRegex(@"\\(?:begin|end)\s*\{[^{}]+\}", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentRegex();

    [GeneratedRegex(@"\\(?:left|right|quad|qquad)\b|\\[,;:!]", RegexOptions.CultureInvariant)]
    private static partial Regex SpacingCommandRegex();

    [GeneratedRegex(@"\^\s*\{([^{}]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex BracedSuperscriptRegex();

    [GeneratedRegex(@"\^\s*([+-]?\d+(?:[.,]\d+)?|[A-Za-z])", RegexOptions.CultureInvariant)]
    private static partial Regex SimpleSuperscriptRegex();

    [GeneratedRegex(@"_\s*\{([^{}]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex BracedSubscriptRegex();

    [GeneratedRegex(@"_\s*([+-]?\d+(?:[.,]\d+)?|[A-Za-z])", RegexOptions.CultureInvariant)]
    private static partial Regex SimpleSubscriptRegex();

    [GeneratedRegex(@"\\([A-Za-z]+)", RegexOptions.CultureInvariant)]
    private static partial Regex CommandRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[⁰¹²³⁴⁵⁶⁷⁸⁹⁺⁻]+", RegexOptions.CultureInvariant)]
    private static partial Regex SuperscriptUnicodeRegex();

    [GeneratedRegex(@"[₀₁₂₃₄₅₆₇₈₉₊₋]+", RegexOptions.CultureInvariant)]
    private static partial Regex SubscriptUnicodeRegex();

    [GeneratedRegex(@"(?<![<>=!])=(?!=)", RegexOptions.CultureInvariant)]
    private static partial Regex EqualsRegex();

    [GeneratedRegex(@"(?<=[⁰¹²³⁴⁵⁶⁷⁸⁹])/(?=[\p{L}])|(?<=[\p{L}])/(?=[\p{L}][⁰¹²³⁴⁵⁶⁷⁸⁹])", RegexOptions.CultureInvariant)]
    private static partial Regex UnitDivisionRegex();
}
