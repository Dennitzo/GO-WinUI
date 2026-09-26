using System.Text.Json;

namespace GoAi.Contracts;

/// <summary>Shared schema validation for native workspace tools.</summary>
public static class WorkspaceTools
{
    public const string ImageInput = "image.input";
    public const string Open = "workspace.open";
    public static bool IsLocal(string name) => name is ImageInput or Open;

    public static void Validate(string name, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException("Werkzeugargumente müssen ein Objekt sein.");
        string[] allowed = name switch
        {
            ImageInput => ["operation", "path", "windowId"],
            Open => ["path"],
            _ => throw new ArgumentException("Unbekanntes Workspace-Werkzeug."),
        };
        if (args.EnumerateObject().Any(p => !allowed.Contains(p.Name, StringComparer.Ordinal)))
            throw new ArgumentException("Unbekannte Werkzeugeigenschaft.");
        if (name == Open) { Text(args, "path", 1024); return; }
        var operation = Text(args, "operation", 20);
        if (operation == "file") Text(args, "path", 1024);
        else if (operation == "capture") Text(args, "windowId", 100);
        else if (operation != "windows") throw new ArgumentException("Ungültige Bildoperation.");
    }

    private static string Text(JsonElement args, string name, int maximum) =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { } text && !string.IsNullOrWhiteSpace(text) && text.Length <= maximum
            ? text : throw new ArgumentException($"Ungültiges Feld: {name}.");
}
