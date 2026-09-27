using System.Text.Json;

namespace GoWinUI.Core.Extensions;

public static class ExtensionManifestValidator
{
    public const int MaximumActions = 256;
    public const int MaximumTools = 256;
    public const int MaximumEntrypoints = 8;

    public static ExtensionValidationResult Validate(ExtensionManifest? manifest)
    {
        var errors = new List<string>();
        if (manifest is null)
        {
            errors.Add("Das Manifest fehlt.");
            return new(errors);
        }

        if (manifest.SchemaVersion != ExtensionPackageFormat.CurrentSchemaVersion)
            errors.Add($"SchemaVersion {manifest.SchemaVersion} wird nicht unterstützt.");
        if (!ExtensionIdentifiers.TryValidateExtensionId(manifest.Id, out var idError)) errors.Add(idError);
        if (!ExtensionIdentifiers.IsValidSemanticVersion(manifest.Version))
            errors.Add("Version muss eine gültige semantische Version sein.");
        ValidateText(manifest.DisplayName, "DisplayName", 120, errors);
        ValidateText(manifest.Publisher, "Publisher", 160, errors);

        var entrypoints = manifest.Entrypoints ?? [];
        if (entrypoints.Length is < 1 or > MaximumEntrypoints)
            errors.Add($"Entrypoints muss zwischen 1 und {MaximumEntrypoints} Einträge enthalten.");
        var entrypointPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entrypoint in entrypoints)
        {
            if (!Enum.IsDefined(entrypoint.Kind)) errors.Add($"Entrypoint-Art '{entrypoint.Kind}' ist unbekannt.");
            if (!ExtensionPathRules.TryNormalizeRelativePath(entrypoint.Path, out var normalized, out var pathError))
                errors.Add($"Entrypoint '{entrypoint.Path}': {pathError}");
            else if (normalized.EndsWith('/'))
                errors.Add($"Entrypoint '{entrypoint.Path}' muss eine Datei bezeichnen.");
            else if (!entrypointPaths.Add(normalized))
                errors.Add($"Entrypoint-Pfad '{normalized}' ist mehrfach vorhanden.");
        }
        foreach (var duplicateKind in entrypoints.GroupBy(static entrypoint => entrypoint.Kind)
            .Where(static group => group.Count() > 1))
            errors.Add($"Entrypoint-Art '{duplicateKind.Key}' darf höchstens einmal vorkommen.");

        var permissions = manifest.Permissions ?? [];
        if (permissions.Length != permissions.Distinct().Count())
            errors.Add("Permissions enthält doppelte Werte.");
        foreach (var permission in permissions)
            if (!Enum.IsDefined(permission)) errors.Add($"Permission '{permission}' ist unbekannt.");

        ValidateActions(manifest, errors);
        ValidateTools(manifest, errors);
        return new(errors.AsReadOnly());
    }

    public static ExtensionValidationResult ValidateAction(ExtensionActionDescriptor? action, string expectedExtensionId)
    {
        var errors = new List<string>();
        ValidateActionCore(action, expectedExtensionId, errors);
        return new(errors.AsReadOnly());
    }

    private static void ValidateActions(ExtensionManifest manifest, List<string> errors)
    {
        var actions = manifest.Actions ?? [];
        if (actions.Length > MaximumActions) errors.Add($"Actions darf höchstens {MaximumActions} Einträge enthalten.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in actions)
        {
            ValidateActionCore(action, manifest.Id, errors);
            if (action is not null && !ids.Add(action.ActionId))
                errors.Add($"Action-ID '{action.ActionId}' ist mehrfach vorhanden.");
        }
    }

    private static void ValidateActionCore(ExtensionActionDescriptor? action, string expectedExtensionId, List<string> errors)
    {
        if (action is null)
        {
            errors.Add("Actions enthält einen leeren Eintrag.");
            return;
        }

        if (!ExtensionIdentifiers.TryValidateActionId(action.ActionId, expectedExtensionId, out var actionError))
            errors.Add(actionError);
        ValidateText(action.DisplayName, $"DisplayName von '{action.ActionId}'", 120, errors);
        ValidateText(action.Description, $"Description von '{action.ActionId}'", 300, errors);
        if (!ExtensionIdentifiers.TryValidateToken(action.IconKey, "IconKey", out var iconError)) errors.Add(iconError);
        if (!ExtensionIdentifiers.TryValidateToken(action.GroupId, "GroupId", out var groupError)) errors.Add(groupError);
        if (action.GroupOrder is < 0 or > 10_000) errors.Add($"GroupOrder von '{action.ActionId}' liegt außerhalb 0..10000.");
        if (action.ItemOrder is < 0 or > 10_000) errors.Add($"ItemOrder von '{action.ActionId}' liegt außerhalb 0..10000.");
        if (!Enum.IsDefined(action.ActionKind)) errors.Add($"ActionKind von '{action.ActionId}' ist unbekannt.");
        if (!Enum.IsDefined(action.SelectionBehavior)) errors.Add($"SelectionBehavior von '{action.ActionId}' ist unbekannt.");
        if (action.ActionKind == ExtensionActionKind.Immediate && action.SelectionBehavior != ExtensionSelectionBehavior.None)
            errors.Add($"Unmittelbare Action '{action.ActionId}' darf kein Auswahlverhalten besitzen.");
        if (action.ActionKind == ExtensionActionKind.SelectableTool && action.SelectionBehavior == ExtensionSelectionBehavior.None)
            errors.Add($"Auswählbare Action '{action.ActionId}' benötigt ein Auswahlverhalten.");
        if (action.SupportedChatModes is null || action.SupportedChatModes.Length == 0)
            errors.Add($"Action '{action.ActionId}' unterstützt keinen Chatmodus.");
        else
        {
            if (action.SupportedChatModes.Length != action.SupportedChatModes.Distinct().Count())
                errors.Add($"Action '{action.ActionId}' enthält doppelte Chatmodi.");
            foreach (var mode in action.SupportedChatModes)
                if (!Enum.IsDefined(mode)) errors.Add($"Action '{action.ActionId}' enthält einen unbekannten Chatmodus.");
        }
        if (action.DisabledReason is { Length: > 300 }) errors.Add($"DisabledReason von '{action.ActionId}' ist zu lang.");
    }

    private static void ValidateTools(ExtensionManifest manifest, List<string> errors)
    {
        var tools = manifest.Tools ?? [];
        if (tools.Length > MaximumTools) errors.Add($"Tools darf höchstens {MaximumTools} Einträge enthalten.");
        var actionIds = new HashSet<string>((manifest.Actions ?? []).Select(static action => action.ActionId), StringComparer.Ordinal);
        var toolNames = new HashSet<string>(StringComparer.Ordinal);
        var toolActions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            if (tool is null)
            {
                errors.Add("Tools enthält einen leeren Eintrag.");
                continue;
            }
            if (!ExtensionIdentifiers.TryValidateActionId(tool.ActionId, manifest.Id, out var actionError)) errors.Add(actionError);
            else if (!actionIds.Contains(tool.ActionId)) errors.Add($"Tool '{tool.ModelToolName}' verweist auf eine unbekannte Action '{tool.ActionId}'.");
            if (!toolActions.Add(tool.ActionId)) errors.Add($"Action '{tool.ActionId}' besitzt mehr als einen Modell-Toolnamen.");
            if (!ExtensionIdentifiers.TryValidateModelToolName(tool.ModelToolName, out var toolError)) errors.Add(toolError);
            else if (!toolNames.Add(tool.ModelToolName)) errors.Add($"Modell-Toolname '{tool.ModelToolName}' ist mehrfach vorhanden.");
            ValidateText(tool.Description, $"Description von '{tool.ModelToolName}'", 300, errors);
            if (tool.InputSchema.ValueKind != JsonValueKind.Object) errors.Add($"InputSchema von '{tool.ModelToolName}' muss ein JSON-Objekt sein.");
            else ValidateToolSchema(tool, errors);
            if (!Enum.IsDefined(tool.RiskClass)) errors.Add($"RiskClass von '{tool.ModelToolName}' ist unbekannt.");
            if (tool.RiskClass == ExtensionToolRiskClass.Process
                && !(manifest.Permissions ?? []).Contains(ExtensionPermissionKind.Process))
                errors.Add($"Process-Tool '{tool.ModelToolName}' benötigt die Process-Permission.");
            if (tool.RiskClass == ExtensionToolRiskClass.LocalMutation
                && !(manifest.Permissions ?? []).Any(static permission => permission is ExtensionPermissionKind.FileWrite or ExtensionPermissionKind.WorkspaceWrite))
                errors.Add($"Mutierendes Tool '{tool.ModelToolName}' benötigt eine Schreib-Permission.");
            if (tool.TimeoutSeconds is < 1 or > 7_200) errors.Add($"TimeoutSeconds von '{tool.ModelToolName}' liegt außerhalb 1..7200.");
            if (tool.MaximumOutputBytes is < 1_024 or > 64 * 1024 * 1024) errors.Add($"MaximumOutputBytes von '{tool.ModelToolName}' liegt außerhalb 1024..67108864.");
        }
    }

    private static void ValidateToolSchema(ExtensionToolDescriptor tool, List<string> errors)
    {
        var schema = tool.InputSchema;
        if (schema.GetRawText().Length > 65_536)
        {
            errors.Add($"InputSchema von '{tool.ModelToolName}' überschreitet 65536 Zeichen.");
            return;
        }
        if (!schema.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || !string.Equals(type.GetString(), "object", StringComparison.Ordinal)
            || !schema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object
            || properties.EnumerateObject().Count() > 64
            || !schema.TryGetProperty("required", out var required)
            || required.ValueKind != JsonValueKind.Array
            || !schema.TryGetProperty("additionalProperties", out var additional)
            || additional.ValueKind != JsonValueKind.False)
        {
            errors.Add($"InputSchema von '{tool.ModelToolName}' benötigt type=object, properties, required und additionalProperties=false.");
            return;
        }
        var propertyNames = properties.EnumerateObject().Select(static property => property.Name).ToHashSet(StringComparer.Ordinal);
        if (propertyNames.Any(static property => string.IsNullOrWhiteSpace(property) || property.Length > 80))
            errors.Add($"InputSchema von '{tool.ModelToolName}' enthält ungültige Property-Namen.");
        var requiredNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in required.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } name
                || !propertyNames.Contains(name) || !requiredNames.Add(name))
                errors.Add($"InputSchema von '{tool.ModelToolName}' enthält eine ungültige required-Property.");
        }
    }

    private static void ValidateText(string? value, string field, int maximumLength, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) errors.Add($"{field} fehlt.");
        else if (value.Length > maximumLength) errors.Add($"{field} überschreitet {maximumLength} Zeichen.");
        else if (!string.Equals(value, value.Trim(), StringComparison.Ordinal)) errors.Add($"{field} enthält äußere Leerzeichen.");
    }
}
