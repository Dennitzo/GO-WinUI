using System.Text.Json;
using System.Text.RegularExpressions;
using GoAi.Contracts;

namespace GoAi.Server.Core.Models;

public sealed partial class ModelRuntimeClient
{
    internal static IReadOnlyList<LmChatMessage> PrepareLanguageBoundMessages(IReadOnlyList<LmChatMessage> messages)
    {
        var normalized = NormalizeMessageOrderForNativeRuntime(messages).ToList();
        var rule = GoAi.Server.Core.Coding.CodingAgentPolicy.ReasoningLanguagePrompt;
        const string reminder = "Der Reasoning-Kanal wird dem Nutzer angezeigt. Beginne bereits den ersten Denksatz auf Deutsch. "
            + "Die gewählte Reasoning-Stufe verändert nur die Denkleistung, niemals die Sprache. "
            + "Eine anderssprachige Abschlussantwort ändert diese Vorgabe für den Reasoning-Kanal nicht. "
            + "Beginne direkt mit dem fachlichen Inhalt, ohne die Sprache oder den Denkprozess anzukündigen.";
        if (normalized.Count > 0 && normalized[0].Role == "system")
            normalized[0] = normalized[0] with { Content = rule + "\n" + reminder + "\n\n"
                + (normalized[0].Content ?? "").Replace(rule, "", StringComparison.Ordinal).Replace(reminder, "", StringComparison.Ordinal).Trim() };
        else
            normalized.Insert(0, new("system", rule + "\n" + reminder));
        const string turnReminder = "GO-Laufanweisung zur Sprache: Führe den bestehenden Auftrag unverändert fort. "
            + "Schreibe die jetzt folgende Analyse und alle Denktexte ausschließlich auf Deutsch, bereits ab dem ersten Satz. "
            + "Das gilt unabhängig von der Reasoning-Stufe und von englischen Nachrichten oder Quellen oben. "
            + "Code, Befehle, Pfade und Zitate bleiben unverändert. Beginne direkt mit dem fachlichen Inhalt; "
            + "kündige weder die Sprache noch den Denkprozess an. Dies ist kein neuer Auftrag und verlangt keine Bestätigung.";
        if (normalized.Count == 0 || normalized[^1].Role != "user" || normalized[^1].Content != turnReminder)
            normalized.Add(new("user", turnReminder));
        return normalized;
    }

    private static readonly string[] HighestReasoningLevels = ["max", "ultra", "xhigh", "high", "medium", "low", "minimal"];
    internal string? ResolveReasoningEffort(string modelId, string role, string? requested) =>
        !string.IsNullOrWhiteSpace(requested) && !string.Equals(requested.Trim(), "auto", StringComparison.OrdinalIgnoreCase) ? requested.Trim().ToLowerInvariant()
            : ResolveRuntimeReasoningProfile(modelId, role).DefaultEffort;

    internal static ModelReasoningProfile? ReadReasoningMetadata(JsonElement data)
    {
        var tags = data.TryGetProperty("tags", out var tagData) && tagData.ValueKind == JsonValueKind.Array
            ? tagData.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
            : [];
        string? Tag(string prefix) => tags.FirstOrDefault(tag => tag.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
        if (Tag("go-reasoning-mode:") is { } mode)
        {
            var levels = (Tag("go-reasoning-levels:") ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Where(IsEffortIdentifier).Distinct(StringComparer.Ordinal).Take(24).ToArray();
            var preferred = Tag("go-reasoning-default:");
            return new(mode, levels, levels.Contains(preferred) ? preferred : null);
        }
        if (!data.TryGetProperty("chat_template", out var value) || value.ValueKind != JsonValueKind.String) return null;
        var template = value.GetString() ?? "";
        var levelsFromTemplate = new List<string>();
        foreach (Match match in Regex.Matches(template,
            @"(?:resolved_)?reasoning_(?:effort|strength)\s+(?:not\s+)?in\s*[\[(]([^\])]{1,512})[\])]",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            foreach (Match literal in Regex.Matches(match.Groups[1].Value, "['\"]([a-z][a-z0-9_-]{0,31})['\"]",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                if (!levelsFromTemplate.Contains(literal.Groups[1].Value)) levelsFromTemplate.Add(literal.Groups[1].Value);
        if (levelsFromTemplate.Count > 0)
        {
            if (template.Contains("enable_thinking", StringComparison.Ordinal) && !levelsFromTemplate.Contains("none")) levelsFromTemplate.Insert(0, "none");
            var highest = HighestReasoningLevels.FirstOrDefault(levelsFromTemplate.Contains);
            return new("llama-native", levelsFromTemplate, highest);
        }
        if (template.Contains("enable_thinking", StringComparison.Ordinal)) return new("llama-toggle", ["none", "on"], "on");
        // A template consuming an unconstrained effort string does not prove which
        // values the model was trained for. Retain catalog evidence or native defaults.
        return null;
    }

    private static bool IsEffortIdentifier(string value) => value.Length is > 0 and <= 32
        && char.IsAsciiLetter(value[0]) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
}
