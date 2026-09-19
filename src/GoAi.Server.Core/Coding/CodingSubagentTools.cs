using GoAi.Contracts;
using GoAi.Server.Core.Runs;
using System.Text.Json;
namespace GoAi.Server.Core.Coding;
internal static class CodingSubagentTools
{
    internal const string Start = "coding.agentStart", Wait = "coding.agentWait", Cancel = "coding.agentCancel";
    internal static bool IsTool(string name) => name is Start or Wait or Cancel;
    internal static IReadOnlyList<AgentToolSpec> CreateTools() => [
        Tool(Start, "Starte einen parallel arbeitenden Subagenten auf GPU1 mit gemeinsamem Elternkontext. Höchstens einer gleichzeitig. Bevorzuge bei Änderungsaufträgen einen konkreten unabhängigen Schreibauftrag. writePaths weist relative Dateien oder Verzeichnisse mit abschließendem / exklusiv zu; leer erlaubt keine Rückübernahme von Änderungen. Bearbeite zugewiesene Bereiche bis agentWait nicht selbst. Der Subagent erhält die verfügbaren Coding-Werkzeuge einschließlich Terminal für Tests, Diagnose und projektlokale Abhängigkeiten in einer isolierten Arbeitskopie mit kontrollierter Rückübernahme. Keine Unterdelegation. Arbeite nach Start unabhängig auf GPU0 weiter; eigene Terminalbefehle erst nach agentWait.",
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
            if (paths.ValueKind != JsonValueKind.Array || paths.GetArrayLength() > 32) throw new ArgumentException("Höchstens 32 Datei- oder Verzeichnisbereiche zuweisen.");
            foreach (var path in paths.EnumerateArray())
            {
                if (path.ValueKind != JsonValueKind.String) throw new ArgumentException("Schreibbereiche müssen relative Pfade sein.");
                NormalizeScope(path.GetString()!);
            }
        }
    }
    internal static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.Contains(':', StringComparison.Ordinal) || path.StartsWith('/') || path.StartsWith('\\')
            || path.Any(char.IsControl))
            throw new ArgumentException("Nur relative Workspace-Dateien zuweisen.");
        var parts = path.ToLowerInvariant().Replace('\\','/').Split('/');
        if (parts.Any(p => p is ".." or ".git" || p.IndexOfAny(['*','?', '<', '>', '"', '|']) >= 0
            || (p != "." && (p.EndsWith('.') || p.EndsWith(' '))) || IsDeviceName(p))) throw new ArgumentException("Ungültiger Dateibereich.");
        var normalized = string.Join('/', parts.Where(p => p is not ("" or ".")));
        if (normalized.Length == 0) throw new ArgumentException("Eine Datei muss angegeben werden.");
        return normalized;
    }

    internal static string NormalizeScope(string path) => NormalizePath(path) + (path.EndsWith('/') || path.EndsWith('\\') ? "/" : "");

    internal static bool IsWithinScope(string path, string scope)
    {
        var normalized = NormalizePath(path);
        var assigned = NormalizeScope(scope);
        return assigned.EndsWith('/')
            ? normalized.StartsWith(assigned, StringComparison.OrdinalIgnoreCase)
            : string.Equals(normalized, assigned, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ScopesOverlap(string first, string second)
    {
        var left = NormalizeScope(first);
        var right = NormalizeScope(second);
        return string.Equals(left.TrimEnd('/'), right.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
            || (left.EndsWith('/') && right.StartsWith(left, StringComparison.OrdinalIgnoreCase))
            || (right.EndsWith('/') && left.StartsWith(right, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDeviceName(string part)
    {
        var name = part.Split('.')[0];
        return name is "con" or "prn" or "aux" or "nul"
            || (name.Length == 4 && (name.StartsWith("com", StringComparison.Ordinal) || name.StartsWith("lpt", StringComparison.Ordinal))
                && name[3] is >= '1' and <= '9');
    }
}
