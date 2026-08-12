using System.Text.Json;

namespace GoWinUI.Core.Chat;

public sealed record GeneralAgentResponse(
    string Message,
    string SessionTitle,
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
            return new(message.Trim(), title, true);
        }

        var visibleMessage = StripOuterCodeFence(raw).Trim();
        return new(
            visibleMessage,
            CreateFallbackTitle(userPrompt, visibleMessage),
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
            : StripOuterCodeFence(raw).Trim();
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
