using GoAi.Contracts;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GoAi.Server.Core.Runs;

internal sealed record CodingCommentaryDirective(
    bool Required,
    string Reason,
    string MilestoneKey)
{
    public static CodingCommentaryDirective None { get; } = new(false, "none", "none");
}

internal static partial class CodingCommentary
{
    public const int MaximumCharacters = 600;
    public const int MaximumSentences = 3;
    public const int MaximumDeltas = 8;

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var sanitized = ReservedTitleRegex().Replace(value, string.Empty);
        sanitized = WhitespaceRegex().Replace(sanitized, " ").Trim();
        if (sanitized.Length is < 12 or > MaximumCharacters
            || ForbiddenStructureRegex().IsMatch(sanitized)
            || ForbiddenReasoningRegex().IsMatch(sanitized)
            || CountSentences(sanitized) > MaximumSentences)
        {
            return null;
        }

        return sanitized;
    }

    public static string CreateFallback(
        CodingCommentaryDirective directive,
        TaskLedgerSnapshot ledger,
        AgentActionEnvelope? previousAction,
        AgentObservation? observation,
        CodingToolDispatch nextAction)
    {
        var target = DescribeTarget(nextAction);
        var previousTarget = previousAction is null ? null : DescribeTarget(previousAction);
        var failure = observation?.ErrorCode ?? observation?.Message;

        return directive.Reason switch
        {
            "initial" => $"Ich orientiere mich zunächst am vorhandenen Workspace und prüfe {target}. Danach wähle ich den kleinsten belegbaren Arbeitsschritt.",
            "workspaceChanged" => $"Die Änderung an {previousTarget ?? "der Workspace-Datei"} ist gespeichert. Ich prüfe jetzt {target}, bevor ich den Auftrag abschließe.",
            "verification" when observation?.Succeeded == true => $"Die letzte Prüfung für {previousTarget ?? "die Änderung"} war erfolgreich. Ich gleiche nun Ergebnis und Abnahmekriterien ab und bearbeite nur noch offene Punkte.",
            "verification" => $"Die Prüfung für {previousTarget ?? "die Änderung"} ist fehlgeschlagen. Ich nutze die konkrete Diagnose und korrigiere als Nächstes {target}.",
            "toolFailure" => $"Der letzte Werkzeugschritt für {previousTarget ?? "den aktuellen Arbeitsstand"} ist an {Shorten(failure ?? "einer konkreten Werkzeugdiagnose", 180)} gescheitert. Ich korrigiere jetzt Pfad, Argumente oder Vorgehen und versuche einen belegbaren Folgeschritt.",
            "research" => $"Die Recherche hat einen neuen Quellenbeleg geliefert. Ich gleiche ihn jetzt mit {target} ab, statt weitere Treffer ohne konkreten Nutzen zu sammeln.",
            "reorientation" => $"Die letzten Schritte brachten keinen neuen belastbaren Fortschritt. Ich orientiere mich am gespeicherten Arbeitsstand neu und verfolge mit {target} einen anderen Ansatz.",
            "contextEpoch" => $"Der Arbeitskontext wurde kompakt aus Ledger und Belegen neu aufgebaut. Ich setze beim nächsten offenen Schritt {target} fort, ohne bereits bestätigte Dateien erneut vollständig zu lesen.",
            _ => $"Der Arbeitsstand hat sich relevant geändert. Ich führe jetzt {target} aus und gleiche das Ergebnis anschließend mit dem Nutzerziel ab.",
        };
    }

    public static string CreateProviderFailure(string model, string detail) =>
        $"Der Modellturn mit {Shorten(model, 80)} wurde beendet, bevor ein gültiger Werkzeugaufruf vorlag. Der Workspace blieb unverändert; GO beendet diesen Versuch mit der Diagnose {Shorten(detail, 220)}.";

    public static string Fingerprint(string milestoneKey, string text)
    {
        _ = text;
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(milestoneKey))).ToLowerInvariant();
    }

    public static IReadOnlyList<string> Split(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var desiredChunks = Math.Clamp((int)Math.Ceiling(value.Length / 96d), 1, MaximumDeltas);
        var targetLength = (int)Math.Ceiling(value.Length / (double)desiredChunks);
        var chunks = new List<string>(desiredChunks);
        var offset = 0;
        while (offset < value.Length && chunks.Count < MaximumDeltas - 1)
        {
            var remaining = value.Length - offset;
            if (remaining <= targetLength)
            {
                break;
            }

            var end = Math.Min(value.Length, offset + targetLength);
            var searchLimit = Math.Min(value.Length, end + 32);
            while (end < searchLimit && !char.IsWhiteSpace(value[end]))
            {
                end++;
            }
            if (end >= value.Length)
            {
                break;
            }

            chunks.Add(value[offset..end]);
            offset = end;
        }
        chunks.Add(value[offset..]);
        return chunks;
    }

    private static int CountSentences(string value)
    {
        var count = SentenceBoundaryRegex().Count(value);
        return Math.Max(1, count);
    }

    private static string DescribeTarget(CodingToolDispatch dispatch)
    {
        var target = ReadString(dispatch.Arguments, "path")
            ?? ReadString(dispatch.Arguments, "reference")
            ?? ReadString(dispatch.Arguments, "target")
            ?? ReadString(dispatch.Arguments, "query")
            ?? ReadString(dispatch.Arguments, "url")
            ?? dispatch.Operation;
        return $"{dispatch.FacadeTool} für {Shorten(target, 120)}";
    }

    private static string DescribeTarget(AgentActionEnvelope action)
    {
        var target = ReadString(action.Arguments, "path")
            ?? ReadString(action.Arguments, "reference")
            ?? ReadString(action.Arguments, "target")
            ?? ReadString(action.Arguments, "query")
            ?? ReadString(action.Arguments, "url")
            ?? action.Operation;
        return Shorten(target, 120);
    }

    private static string? ReadString(System.Text.Json.JsonElement value, string name) =>
        value.ValueKind == System.Text.Json.JsonValueKind.Object
        && value.TryGetProperty(name, out var property)
        && property.ValueKind == System.Text.Json.JsonValueKind.String
            ? property.GetString()
            : null;

    private static string Shorten(string value, int maximum) => value.Length <= maximum
        ? value
        : value[..Math.Max(1, maximum - 1)].TrimEnd() + "…";

    [GeneratedRegex(@"(?im)^\s*(?:#{1,6}\s*)?(?:\*{1,2})?GO(?:\\?_)?SESSION(?:\\?_)?TITLE\s*:\s*.*(?:\r?\n|$)", RegexOptions.CultureInvariant)]
    private static partial Regex ReservedTitleRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"(?i)(?:<\/?think>|chain[ -]?of[ -]?thought|(?:^|\s)(?:reasoning|analysis|gedankengang)\s*:)", RegexOptions.CultureInvariant)]
    private static partial Regex ForbiddenReasoningRegex();

    [GeneratedRegex(@"^(?:[#{\[]|[-*+]\s|\d+[.)]\s)|""(?:operation|arguments|tool|name)""\s*:", RegexOptions.CultureInvariant)]
    private static partial Regex ForbiddenStructureRegex();

    [GeneratedRegex(@"[.!?](?:\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceBoundaryRegex();
}
