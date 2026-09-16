using GoAi.Contracts;
using GoAi.Server.Core.Runs;
using System.Text.Json;
namespace GoAi.Server.Core.Coding;
internal static class CodingSubagentTools
{
    internal const string Start = "coding.agentStart", Wait = "coding.agentWait", Cancel = "coding.agentCancel";
    internal static bool IsTool(string name) => name is Start or Wait or Cancel;
    internal static IReadOnlyList<AgentToolSpec> CreateTools() => [
        Tool(Start, "Starte einen parallel arbeitenden Subagenten auf GPU1 mit eigenem Kontext. Höchstens einer gleichzeitig. Gib einen begrenzten Auftrag und nötige Belege. writePaths weist exakte relative Dateien exklusiv zum Bearbeiten zu; leer bedeutet nur Lesen/Recherche. Bearbeite diese Dateien bis agentWait nicht selbst. Kein Terminal und keine Unterdelegation beim Subagenten. Delegiere keine Testausführung, Python-Diagnose oder Paketinstallation; diese Aktionen führst du als Hauptagent selbst aus. Arbeite nach Start unabhängig auf GPU0 weiter.",
            "{\"task\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":16000},\"writePaths\":{\"type\":\"array\",\"maxItems\":32,\"items\":{\"type\":\"string\",\"maxLength\":1024}}}", ["task"]),
        Tool(Wait, "Warte auf den Subagenten und erhalte Ergebnis/Fehler mit Werkzeugbelegen. Nutze erst, wenn deine unabhängige Arbeit erledigt ist. Kein Zeitlimit.", "{}", []),
        Tool(Cancel, "Stoppe den Subagenten. Bereits geschriebene Dateien bleiben erhalten; prüfe seine Belege.", "{}", [])];
    private static AgentToolSpec Tool(string name, string description, string properties, string[] required)
    {
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = JsonSerializer.Deserialize<JsonElement>(properties), required, additionalProperties = false });
        return new(name, description, ToolRiskClass.ReadOnly, true, schema, required.ToHashSet(StringComparer.Ordinal),
            schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal));
    }
    internal static void Validate(string name, JsonElement args)
    {
        if (name != Start) return;
        if (!args.TryGetProperty("task", out var task) || task.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(task.GetString()) || task.GetString()!.Length > 16000)
            throw new ArgumentException("Ein begrenzter Subagentenauftrag ist erforderlich.");
        if (args.TryGetProperty("writePaths", out var paths))
        {
            if (paths.ValueKind != JsonValueKind.Array || paths.GetArrayLength() > 32) throw new ArgumentException("Höchstens 32 exakte Dateien zuweisen.");
            foreach (var path in paths.EnumerateArray()) NormalizePath(path.GetString()!);
        }
    }
    internal static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.Contains(':', StringComparison.Ordinal) || path.StartsWith('/') || path.StartsWith('\\'))
            throw new ArgumentException("Nur relative Workspace-Dateien zuweisen.");
        var parts = path.ToLowerInvariant().Replace('\\','/').Split('/');
        if (parts.Any(p => p is ".." or ".git" || p.IndexOfAny(['*','?']) >= 0 || (p != "." && (p.EndsWith('.') || p.EndsWith(' '))))) throw new ArgumentException("Ungültiger Dateibereich.");
        var normalized = string.Join('/', parts.Where(p => p is not ("" or ".")));
        if (normalized.Length == 0) throw new ArgumentException("Eine Datei muss angegeben werden.");
        return normalized;
    }
}
