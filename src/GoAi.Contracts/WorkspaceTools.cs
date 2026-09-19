using System.Text.Json;

namespace GoAi.Contracts;

/// <summary>Shared schema validation for the native workspace tools.</summary>
public static class WorkspaceTools
{
    public const string ImageInput = "image.input";
    public const string Blender = "blender.execute";
    public const string DocumentAgent = "document.agent";
    public const string Open = "workspace.open";
    public static bool IsLocal(string name) => name is ImageInput or Blender or Open;

    public static void Validate(string name, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException("Werkzeugargumente müssen ein Objekt sein.");
        string[] allowed = name switch
        {
            ImageInput => ["operation", "path", "windowId"],
            Blender => ["operation", "path", "expectedSha256", "timeoutSeconds"],
            DocumentAgent => ["task", "writePaths"],
            Open => ["path"],
            _ => throw new ArgumentException("Unbekanntes Workspace-Werkzeug."),
        };
        if (args.EnumerateObject().Any(p => !allowed.Contains(p.Name, StringComparer.Ordinal)))
            throw new ArgumentException("Unbekannte Werkzeugeigenschaft.");
        if (name == Open) { Text(args, "path", 1024); return; }
        if (name == DocumentAgent)
        {
            Text(args, "task", 16000);
            if (args.TryGetProperty("writePaths", out var paths)
                && (paths.ValueKind != JsonValueKind.Array || paths.GetArrayLength() > 32
                    || paths.EnumerateArray().Any(p => p.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(p.GetString()) || p.GetString()!.Length > 1024)))
                throw new ArgumentException("Ungültige Dokument-Schreibbereiche.");
            return;
        }
        var operation = Text(args, "operation", 20);
        if (name == ImageInput)
        {
            if (operation == "file") Text(args, "path", 1024);
            else if (operation == "capture") Text(args, "windowId", 100);
            else if (operation != "windows") throw new ArgumentException("Ungültige Bildoperation.");
        }
        else
        {
            if (operation is "run" or "open") Text(args, "path", 1024);
            else if (operation != "info") throw new ArgumentException("Ungültige Blenderoperation.");
            if (operation == "run")
            {
                var hash = Text(args, "expectedSha256", 64);
                if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new ArgumentException("Aktueller Skript-SHA-256 erforderlich.");
            }
            if (args.TryGetProperty("timeoutSeconds", out var seconds)
                && (seconds.ValueKind != JsonValueKind.Number || !seconds.TryGetInt32(out var value) || value < 1 || value > 3600))
                throw new ArgumentException("Blender-Zeitlimit muss zwischen 1 und 3600 Sekunden liegen.");
        }
    }

    private static string Text(JsonElement args, string name, int maximum) =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { } text && !string.IsNullOrWhiteSpace(text) && text.Length <= maximum
            ? text : throw new ArgumentException($"Ungültiges Feld: {name}.");
}
