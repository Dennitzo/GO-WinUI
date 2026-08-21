using System.Text.Json;

namespace GoWinUI.Core.Chat;

public sealed record GeneralAgentResponse(
    string Message,
    string SessionTitle,
    string ContextSummary,
    bool IsStructured);

public static class GeneralAgentResponseParser
{
    public const string ResponseSchema = "barebone.agent.response.v2";
    private const string GreetingFallbackTitle = "Einstieg in die TGA-Fachplanung";

    private static readonly HashSet<string> GenericTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Antwort",
        "Allgemeiner Chat",
        "Frage",
        "GO Assistent",
        "GO-Assistent",
        "Hallo",
        "Neue Sitzung",
        "Neuer Chat",
        "TGA",
        "TGA Planung",
        "TGA-Planung",
        "Willkommen",
        "Workflow",
    };

    public static GeneralAgentResponse Parse(string rawContent, string userPrompt)
    {
        var raw = rawContent?.Trim() ?? string.Empty;
        if (TryParseObject(raw, out var root)
            && TryGetString(root, "message", out var message)
            && !string.IsNullOrWhiteSpace(message))
        {
            var suggestedTitle = GetSuggestedTitle(root);
            var title = NormalizeTitle(suggestedTitle)
                ?? CreateFallbackTitle(userPrompt, message);
            var summary = GetSuggestedContextSummary(root);
            return new(CleanVisibleMessage(message), title, CreateContextSummary(summary, message), true);
        }

        var visibleMessage = CleanVisibleMessage(StripOuterCodeFence(raw));
        var legacyTitle = ExtractLegacySessionTitle(raw);
        return new(
            visibleMessage,
            NormalizeTitle(legacyTitle) ?? CreateFallbackTitle(userPrompt, visibleMessage),
            CreateContextSummary(null, visibleMessage),
            false);
    }

    public static string VisiblePartial(string rawContent)
    {
        var raw = rawContent?.Trim() ?? string.Empty;
        if (raw.Length == 0)
        {
            return string.Empty;
        }

        if (TryParseObject(raw, out var root)
            && TryGetString(root, "message", out var message))
        {
            return message.Trim();
        }

        return LooksLikeStructuredEnvelope(raw)
            ? string.Empty
            : CleanVisibleMessage(StripOuterCodeFence(raw));
    }

    public static string CreateContextSummary(string? suggestedSummary, string message)
    {
        var source = string.IsNullOrWhiteSpace(suggestedSummary) ? message : suggestedSummary;
        var lines = new List<string>(2);
        foreach (var rawLine in (source ?? string.Empty).ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = rawLine.Trim();
            if (trimmed.Length == 0
                || trimmed == "---"
                || trimmed.Contains('|', StringComparison.Ordinal)
                || ChatContentSanitizer.ContainsReservedMarker(trimmed)
                || lines.Count > 0 && trimmed.StartsWith('#'))
            {
                continue;
            }
            var plain = PlainText(trimmed);
            if (plain.Length == 0 || plain.All(static character => character is '-' or ':' or ' ')) continue;
            lines.Add(plain);
            if (lines.Count == 2) break;
        }
        var value = string.Join(' ', lines);
        value = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (value.Length > 320)
        {
            value = value[..320].TrimEnd();
            var boundary = value.LastIndexOfAny(['.', '!', '?', ';']);
            if (boundary >= 100) value = value[..(boundary + 1)];
            else value = value.TrimEnd(',', ':', '-', ' ') + "…";
        }
        return value.Length == 0 ? "AI-gestützter Arbeitsablauf aus der zugehörigen Sitzung." : value;
    }

    public static string CreateWorkflowTitle(string message)
    {
        var first = (message ?? string.Empty).ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => PlainText(line))
            .FirstOrDefault(static line => line.Length > 0 && line != "---")
            ?? "Workflow aus Chat";
        return first.Length <= 100 ? first : first[..97].TrimEnd() + "…";
    }

    public static string? NormalizeTitle(string? title)
    {
        var value = (title ?? string.Empty)
            .Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("__", string.Empty, StringComparison.Ordinal)
            .Replace('—', ' ')
            .Replace('–', ' ')
            .ReplaceLineEndings(" ")
            .Trim(' ', '\t', '#', '`', '"', '\'', '.', '!', '?', ';', ':', ',', '-', '–', '—');
        value = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (value.StartsWith("Titel:", StringComparison.OrdinalIgnoreCase))
        {
            value = value[6..].TrimStart('-', ' ');
        }
        else if (value.StartsWith("Titel ", StringComparison.OrdinalIgnoreCase))
        {
            value = value[6..].TrimStart(':', '-', ' ');
        }

        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 6)
        {
            value = string.Join(' ', words.Take(6));
        }

        if (value.Length > 64)
        {
            value = value[..64].TrimEnd();
            var boundary = value.LastIndexOf(' ');
            if (boundary >= 24)
            {
                value = value[..boundary];
            }
        }

        return value.Length == 0 || GenericTitles.Contains(value)
            ? null
            : value;
    }

    private static string CreateFallbackTitle(string userPrompt, string assistantMessage)
    {
        var prompt = userPrompt?.Trim() ?? string.Empty;
        if (IsLowSignalPrompt(prompt))
        {
            return GreetingFallbackTitle;
        }

        var candidate = prompt;
        foreach (var prefix in new[]
        {
            "Kannst du bitte ",
            "Kannst du ",
            "Könntest du bitte ",
            "Könntest du ",
            "Bitte ",
        })
        {
            if (candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                candidate = candidate[prefix.Length..];
                break;
            }
        }

        var normalized = NormalizeTitle(candidate);
        if (normalized is not null)
        {
            return normalized;
        }

        normalized = NormalizeTitle(assistantMessage);
        return normalized ?? GreetingFallbackTitle;
    }

    private static bool IsLowSignalPrompt(string prompt)
    {
        var value = prompt.Trim().TrimEnd('.', '!', '?').ToLowerInvariant();
        return value.Length <= 3
            || value is "hallo" or "hello" or "hi" or "hey" or "moin" or "servus" or "guten tag"
            || value is "ja" or "nein" or "ok" or "okay" or "weiter" or "danke";
    }

    private static string GetSuggestedTitle(JsonElement root)
    {
        foreach (var name in new[] { "sessionTitle", "conversationTitle", "chatTitle" })
        {
            if (TryGetString(root, name, out var title) && !string.IsNullOrWhiteSpace(title))
            {
                return title;
            }
        }

        if (root.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "sessionTitle", "conversationTitle" })
            {
                if (TryGetString(meta, name, out var title) && !string.IsNullOrWhiteSpace(title))
                {
                    return title;
                }
            }
        }

        return string.Empty;
    }

    private static string GetSuggestedContextSummary(JsonElement root)
    {
        foreach (var name in new[] { "contextSummary", "workflowContextSummary", "summary" })
        {
            if (TryGetString(root, name, out var summary) && !string.IsNullOrWhiteSpace(summary)) return summary;
        }
        return string.Empty;
    }

    private static string CleanVisibleMessage(string content) =>
        ChatContentSanitizer.Sanitize(content).Trim();

    private static string? ExtractLegacySessionTitle(string content) =>
        ChatContentSanitizer.TryExtractLegacyTitle(content, out var title) ? title : null;

    private static string PlainText(string line)
    {
        var value = line.Trim().TrimStart('#').Trim();
        value = value.Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("__", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal)
            .Replace("|", " ", StringComparison.Ordinal)
            .Replace("*", string.Empty, StringComparison.Ordinal);
        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryParseObject(string content, out JsonElement root)
    {
        root = default;
        var candidate = StripOuterCodeFence(content).Trim();
        if (TryParseCandidate(candidate, out root))
        {
            return true;
        }

        var start = candidate.IndexOf('{');
        var end = candidate.LastIndexOf('}');
        return start >= 0
            && end > start
            && TryParseCandidate(candidate[start..(end + 1)], out root);
    }

    private static bool TryParseCandidate(string candidate, out JsonElement root)
    {
        root = default;
        try
        {
            using var document = JsonDocument.Parse(candidate);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            root = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool LooksLikeStructuredEnvelope(string content)
    {
        var value = content.TrimStart();
        return value.StartsWith('{')
            || value.StartsWith("```json", StringComparison.OrdinalIgnoreCase);
    }

    private static string StripOuterCodeFence(string content)
    {
        var value = content.Trim();
        if (!value.StartsWith("```", StringComparison.Ordinal))
        {
            return value;
        }

        var firstLineEnd = value.IndexOf('\n');
        if (firstLineEnd < 0)
        {
            return value;
        }

        value = value[(firstLineEnd + 1)..];
        var closingFence = value.LastIndexOf("```", StringComparison.Ordinal);
        return closingFence >= 0
            ? value[..closingFence]
            : value;
    }
}
