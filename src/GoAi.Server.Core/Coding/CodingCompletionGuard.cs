using System.Text.RegularExpressions;

namespace GoAi.Server.Core.Coding;

internal static class CodingCompletionGuard
{
    internal const string RepairPrompt = "Die letzte Antwort kündigt nur weitere Arbeit an und schließt den Auftrag nicht ab. "
        + "Führe den angekündigten nächsten Schritt jetzt mit einem tatsächlichen Werkzeugaufruf aus. "
        + "Ein angekündigter Plan ist noch kein coding.updatePlan-Aufruf. Nutze die vorhandenen Ergebnisse und wiederhole "
        + "keine bereits ausgeführten Änderungen. Wenn der Auftrag bereits erfüllt ist, liefere den belegten Abschluss; "
        + "wenn eine echte Blockade besteht, benenne sie konkret. Erfinde keine Ausführung oder Testergebnisse.";

    internal static bool IsActionAnnouncement(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 1800 || text.Contains("```", StringComparison.Ordinal)) return false;
        if (Regex.IsMatch(text, @"\b(?:nicht möglich|kann nicht|benötige|fehlt|sobald|cannot|unable|need your|Ergebnis|bestanden|geändert|erledigt|ergibt|Ursache)\b|=",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) return false;
        return Regex.IsMatch(text.Trim(),
            @"\A(?:Ich\s+(?:beginne|starte|werde|erstelle|untersuche|analysiere|prüfe|lese|suche|plane)\b|Als\s+[Nn]ächstes\s+(?:werde\s+)?ich\b|I\s+(?:will|am going to)\b|Let me\s+(?:inspect|create|read|check|search|implement|plan)\b)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }
}
