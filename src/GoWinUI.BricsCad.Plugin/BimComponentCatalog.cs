using System.Text.Json.Nodes;
using Bricscad.ApplicationServices;
using System.IO;

namespace GoWinUI.BricsCad.Plugin;

internal sealed record BimComponent(
    string ComponentId,
    string Classification,
    string DisplayName,
    string RelativePath,
    string? ResolvedPath)
{
    public bool Available => ResolvedPath is not null && File.Exists(ResolvedPath);

    public JsonObject ToJson() => new()
    {
        ["componentId"] = ComponentId,
        ["provider"] = "bricscad-dotnet-library",
        ["classification"] = Classification,
        ["displayName"] = DisplayName,
        ["relativePath"] = RelativePath.Replace('\\', '/'),
        ["available"] = Available
    };
}

internal static class BimComponentCatalog
{
    private static readonly object Sync = new();
    private static IReadOnlyList<BimComponent>? _cached;

    public static IReadOnlyList<BimComponent> GetAll()
    {
        lock (Sync)
            return _cached ??= Build();
    }

    public static BimComponent Resolve(string classification, string componentId)
    {
        string requested = componentId.Trim();
        string canonicalClass = CanonicalClassification(classification);
        BimComponent? component = GetAll().FirstOrDefault(item =>
            item.ComponentId.Equals(requested, StringComparison.OrdinalIgnoreCase));
        if (component is null)
            throw new ArgumentException($"Unbekannte lokale BricsCAD-BIM-Komponente: {componentId}");
        if (!component.Classification.Equals(canonicalClass, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Komponente {component.ComponentId} gehört zu {component.Classification}, nicht zu {canonicalClass}.");
        if (!component.Available)
            throw new FileNotFoundException($"BricsCAD-BIM-Komponente ist lokal nicht verfügbar: {component.RelativePath}");
        return component;
    }

    private static IReadOnlyList<BimComponent> Build()
    {
        IReadOnlyList<string> roots = ComponentRoots();
        var result = new List<BimComponent>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddKnown(result, ids, roots, "window.advanced.window11", "Window", "Window 11", Path.Combine("Windows Advanced", "Window 11.dwg"));
        AddKnown(result, ids, roots, "window.advanced.window1", "Window", "Window 1", Path.Combine("Windows Advanced", "Window 1.dwg"));
        AddKnown(result, ids, roots, "door.advanced.door_d", "Door", "Door D", Path.Combine("Doors Advanced", "Door D.dwg"));

        foreach (string root in roots)
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.dwg", SearchOption.AllDirectories); }
            catch { continue; }
            foreach (string file in files)
            {
                string relative;
                try { relative = Path.GetRelativePath(root, file); }
                catch { continue; }
                string classification = ClassificationForPath(relative);
                if (classification.Length == 0) continue;
                string id = "bricscad-bim:" + Path.ChangeExtension(relative, null)!
                    .Replace('\\', '/').Replace(' ', '-').ToLowerInvariant();
                if (!ids.Add(id)) continue;
                result.Add(new BimComponent(id, classification, Path.GetFileNameWithoutExtension(file), relative, Path.GetFullPath(file)));
            }
        }
        return result.OrderBy(item => item.Classification).ThenBy(item => item.DisplayName).ToArray();
    }

    private static void AddKnown(List<BimComponent> target, HashSet<string> ids, IReadOnlyList<string> roots,
        string id, string classification, string displayName, string relativePath)
    {
        string? resolved = roots.Select(root => Path.Combine(root, relativePath))
            .FirstOrDefault(File.Exists);
        ids.Add(id);
        target.Add(new BimComponent(id, classification, displayName, relativePath, resolved is null ? null : Path.GetFullPath(resolved)));
    }

    private static IReadOnlyList<string> ComponentRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string install = Path.GetDirectoryName(typeof(Application).Assembly.Location) ?? string.Empty;
        string userDataCache = Path.Combine(install, "UserDataCache", "Support");
        AddLocaleRoots(roots, userDataCache);

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string userRoot = Path.Combine(appData, "Bricsys", "BricsCAD");
        if (Directory.Exists(userRoot))
        {
            try
            {
                foreach (string bimDirectory in Directory.EnumerateDirectories(userRoot, "Bim", SearchOption.AllDirectories))
                {
                    string components = Path.Combine(bimDirectory, "Components");
                    if (Directory.Exists(components)) roots.Add(Path.GetFullPath(components));
                }
            }
            catch { }
        }
        return roots.ToArray();
    }

    private static void AddLocaleRoots(HashSet<string> roots, string supportRoot)
    {
        if (!Directory.Exists(supportRoot)) return;
        try
        {
            foreach (string locale in Directory.EnumerateDirectories(supportRoot))
            {
                string components = Path.Combine(locale, "Bim", "Components");
                if (Directory.Exists(components)) roots.Add(Path.GetFullPath(components));
            }
        }
        catch { }
    }

    private static string ClassificationForPath(string path)
    {
        string normalized = path.Replace('\\', '/');
        if (normalized.Contains("window", StringComparison.OrdinalIgnoreCase)) return "Window";
        if (normalized.Contains("door", StringComparison.OrdinalIgnoreCase)) return "Door";
        return string.Empty;
    }

    private static string CanonicalClassification(string classification)
    {
        if (classification.Equals("Window", StringComparison.OrdinalIgnoreCase)) return "Window";
        if (classification.Equals("Door", StringComparison.OrdinalIgnoreCase)) return "Door";
        throw new ArgumentException("bim.create unterstützt ausschließlich Window und Door aus dem lokalen BricsCAD-Komponentenkatalog.");
    }
}


