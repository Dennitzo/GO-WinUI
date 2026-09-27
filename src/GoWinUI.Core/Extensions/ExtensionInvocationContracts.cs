using System.Text.Json;

namespace GoWinUI.Core.Extensions;

public sealed record ExtensionPermissionGrant(
    ExtensionPermissionKind[] Permissions,
    string? WorkspaceRoot);

public sealed record ExtensionActionInvocation(
    string ActionId,
    JsonElement Arguments,
    ExtensionPermissionGrant Grant);

public sealed record ExtensionActionResult(
    JsonElement Result);

public static class ExtensionInvocationPolicy
{
    private static readonly HashSet<string> PathPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "path", "file", "filePath", "folder", "folderPath", "directory", "directoryPath", "workspacePath",
    };
    private static readonly HashSet<string> NetworkPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "url", "uri", "endpoint",
    };
    private static readonly HashSet<string> ProcessPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "command", "commandLine", "executable", "process",
    };

    public static void Validate(
        ExtensionActionInvocation invocation,
        IReadOnlyCollection<ExtensionPermissionKind> declaredPermissions,
        string? expectedWorkspaceRoot)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(declaredPermissions);
        if (!ExtensionIdentifiers.TryValidateActionId(invocation.ActionId, expectedExtensionId: null, out var actionError))
            throw new InvalidDataException(actionError);
        if (invocation.Arguments.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Extension-Aktionsargumente müssen ein JSON-Objekt sein.");
        if (invocation.Grant is null) throw new InvalidDataException("Extension-Berechtigungsfreigabe fehlt.");

        var declared = new HashSet<ExtensionPermissionKind>(declaredPermissions);
        var granted = new HashSet<ExtensionPermissionKind>(invocation.Grant.Permissions ?? []);
        if (!declared.SetEquals(granted))
            throw new InvalidDataException("Extension-Anfrage enthält andere Berechtigungen als das aktivierte Manifest.");
        var normalizedExpectedRoot = NormalizeWorkspaceRoot(expectedWorkspaceRoot);
        var normalizedGrantedRoot = NormalizeWorkspaceRoot(invocation.Grant.WorkspaceRoot);
        if (!string.Equals(normalizedExpectedRoot, normalizedGrantedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Extension-Anfrage enthält eine unerwartete Workspacegrenze.");
        if (RequiresWorkspaceBoundary(declared) && normalizedGrantedRoot is null)
            throw new InvalidDataException("Datei- und Workspaceberechtigungen benötigen eine explizite Workspacegrenze.");

        ValidateJson(invocation.Arguments, declared, normalizedGrantedRoot, depth: 0);
    }

    private static void ValidateJson(
        JsonElement element,
        HashSet<ExtensionPermissionKind> permissions,
        string? workspaceRoot,
        int depth)
    {
        if (depth > 64) throw new InvalidDataException("Extension-Aktionsargumente überschreiten die maximale Verschachtelung.");
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                    ValidateSensitiveString(property.Name, property.Value.GetString() ?? string.Empty, permissions, workspaceRoot);
                else ValidateJson(property.Value, permissions, workspaceRoot, depth + 1);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) ValidateJson(item, permissions, workspaceRoot, depth + 1);
        }
    }

    private static void ValidateSensitiveString(
        string propertyName,
        string value,
        HashSet<ExtensionPermissionKind> permissions,
        string? workspaceRoot)
    {
        if (PathPropertyNames.Contains(propertyName))
        {
            if (!permissions.Overlaps(
                [ExtensionPermissionKind.FileRead, ExtensionPermissionKind.FileWrite,
                 ExtensionPermissionKind.WorkspaceRead, ExtensionPermissionKind.WorkspaceWrite]))
                throw new InvalidDataException($"Argument '{propertyName}' benötigt eine deklarierte Datei- oder Workspaceberechtigung.");
            if (workspaceRoot is null) throw new InvalidDataException($"Argument '{propertyName}' besitzt keine Workspacegrenze.");
            var candidate = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(workspaceRoot, value));
            var prefix = Path.TrimEndingDirectorySeparator(workspaceRoot) + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(candidate, Path.TrimEndingDirectorySeparator(workspaceRoot), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Argument '{propertyName}' verlässt die freigegebene Workspacegrenze.");
        }
        else if (NetworkPropertyNames.Contains(propertyName)
            && Uri.TryCreate(value, UriKind.Absolute, out _)
            && !permissions.Contains(ExtensionPermissionKind.Network))
            throw new InvalidDataException($"Argument '{propertyName}' benötigt die deklarierte Netzwerkberechtigung.");
        else if (ProcessPropertyNames.Contains(propertyName)
            && !permissions.Contains(ExtensionPermissionKind.Process))
            throw new InvalidDataException($"Argument '{propertyName}' benötigt die deklarierte Prozessberechtigung.");
    }

    private static bool RequiresWorkspaceBoundary(HashSet<ExtensionPermissionKind> permissions) =>
        permissions.Overlaps(
            [ExtensionPermissionKind.FileRead, ExtensionPermissionKind.FileWrite,
             ExtensionPermissionKind.WorkspaceRead, ExtensionPermissionKind.WorkspaceWrite]);

    private static string? NormalizeWorkspaceRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        var fullPath = Path.GetFullPath(root);
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException("Die freigegebene Extension-Workspacegrenze existiert nicht.");
        return Path.TrimEndingDirectorySeparator(fullPath);
    }
}
