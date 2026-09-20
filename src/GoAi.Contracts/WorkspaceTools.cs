using System.Text.Json;

namespace GoAi.Contracts;

/// <summary>Shared schema validation for the native workspace tools.</summary>
public static class WorkspaceTools
{
    public const string ImageInput = "image.input";
    public const string Blender = "blender.execute";
    public const int MaximumBlenderStageScriptCharacters = 12_000;
    public const string Open = "workspace.open";
    public static bool IsLocal(string name) => name is ImageInput or Blender or Open;

    public static void Validate(string name, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException("Werkzeugargumente müssen ein Objekt sein.");
        string[] allowed = name switch
        {
            ImageInput => ["operation", "path", "windowId"],
            Blender => ["operation", "path", "expectedSha256", "timeoutSeconds", "brief", "outputDirectory", "views", "resolution", "samples", "outputPath", "label", "baseScene", "baseSceneSha256"],
            Open => ["path"],
            _ => throw new ArgumentException("Unbekanntes Workspace-Werkzeug."),
        };
        if (args.EnumerateObject().Any(p => !allowed.Contains(p.Name, StringComparer.Ordinal)))
            throw new ArgumentException("Unbekannte Werkzeugeigenschaft.");
        if (name == Open) { Text(args, "path", 1024); return; }
        var operation = Text(args, "operation", 20);
        if (name == ImageInput)
        {
            if (operation == "file") Text(args, "path", 1024);
            else if (operation == "capture") Text(args, "windowId", 100);
            else if (operation != "windows") throw new ArgumentException("Ungültige Bildoperation.");
        }
        else
        {
            if (operation is "run" or "open" or "scaffold" or "inspect" or "render" or "stage" or "preview") Text(args, "path", 1024);
            else if (operation != "info") throw new ArgumentException("Ungültige Blenderoperation.");
            if (operation == "info" && args.TryGetProperty("path", out _)) Text(args, "path", 1024);
            if (operation is "run" or "inspect" or "render" or "stage" or "preview")
            {
                Hash(args, "expectedSha256");
            }
            if (operation == "stage")
            {
                FilePath(args, "path", ".py");
                FilePath(args, "outputPath", ".blend");
                Text(args, "label", 200);
                if (args.TryGetProperty("baseScene", out _) || args.TryGetProperty("baseSceneSha256", out _))
                {
                    var baseScene = FilePath(args, "baseScene", ".blend");
                    Hash(args, "baseSceneSha256");
                    if (string.Equals(baseScene.Replace('\\', '/'), Text(args, "outputPath", 1024).Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("stage benötigt eine neue outputPath-Szene; baseScene bleibt erhalten.");
                }
            }
            else if (args.TryGetProperty("outputPath", out _) || args.TryGetProperty("baseScene", out _) || args.TryGetProperty("baseSceneSha256", out _))
                throw new ArgumentException("outputPath, baseScene und baseSceneSha256 sind nur für stage erlaubt.");
            if (operation == "preview") FilePath(args, "path", ".blend");
            if (args.TryGetProperty("label", out _))
            {
                if (operation is not ("stage" or "preview")) throw new ArgumentException("label ist nur für stage/preview erlaubt.");
                Text(args, "label", 200);
            }
            if (operation is "inspect" or "render") Text(args, "outputDirectory", 1024);
            if (args.TryGetProperty("brief", out _))
            {
                if (operation != "scaffold") throw new ArgumentException("brief ist nur für scaffold erlaubt.");
                Text(args, "brief", 8000);
            }
            if (args.TryGetProperty("outputDirectory", out _) && operation is not ("inspect" or "render"))
                throw new ArgumentException("outputDirectory ist nur für inspect/render erlaubt.");
            if (args.TryGetProperty("views", out var views))
            {
                string[] allowedViews = ["perspective", "front", "right", "top", "back", "left"];
                if (operation != "render" || views.ValueKind != JsonValueKind.Array || views.GetArrayLength() is < 1 or > 6
                    || views.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String || !allowedViews.Contains(v.GetString(), StringComparer.Ordinal))
                    || views.EnumerateArray().Select(v => v.GetString()).Distinct(StringComparer.Ordinal).Count() != views.GetArrayLength())
                    throw new ArgumentException("render benötigt eine bis sechs unterschiedliche gültige Ansichten.");
            }
            RenderInteger(args, operation, "resolution", 128, 2048);
            RenderInteger(args, operation, "samples", 1, 128);
            if (args.TryGetProperty("timeoutSeconds", out var seconds)
                && (seconds.ValueKind != JsonValueKind.Number || !seconds.TryGetInt32(out var value) || value < 1 || value > 3600))
                throw new ArgumentException("Blender-Zeitlimit muss zwischen 1 und 3600 Sekunden liegen.");
        }
    }

    private static void RenderInteger(JsonElement args, string operation, string name, int minimum, int maximum)
    {
        if (args.TryGetProperty(name, out var value) && (operation != "render" || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var number) || number < minimum || number > maximum))
            throw new ArgumentException($"{name} ist nur für render im Bereich {minimum}–{maximum} erlaubt.");
    }

    private static void Hash(JsonElement args, string name)
    {
        var hash = Text(args, name, 64);
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new ArgumentException($"Aktueller Datei-SHA-256 erforderlich: {name}.");
    }

    private static string FilePath(JsonElement args, string name, string extension)
    {
        var path = Text(args, name, 1024);
        if (!path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{name} benötigt eine {extension}-Datei.");
        return path;
    }

    private static string Text(JsonElement args, string name, int maximum) =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { } text && !string.IsNullOrWhiteSpace(text) && text.Length <= maximum
            ? text : throw new ArgumentException($"Ungültiges Feld: {name}.");
}
