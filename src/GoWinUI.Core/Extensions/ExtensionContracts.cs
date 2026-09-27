using System.Text.Json;

namespace GoWinUI.Core.Extensions;

public enum ExtensionChatMode
{
    General,
    Coding,
    ClaudeScience,
}

public enum ExtensionActionKind
{
    Immediate,
    SelectableTool,
}

public enum ExtensionSelectionBehavior
{
    None,
    Toggle,
}

public enum ExtensionEntrypointKind
{
    Desktop,
    Gateway,
}

public enum ExtensionToolRiskClass
{
    ReadOnly,
    LocalMutation,
    Process,
}

public enum ExtensionPermissionKind
{
    FileRead,
    FileWrite,
    Network,
    Process,
    WorkspaceRead,
    WorkspaceWrite,
}

public sealed record ExtensionActionDescriptor(
    string ActionId,
    string DisplayName,
    string Description,
    string IconKey,
    string GroupId,
    int GroupOrder,
    int ItemOrder,
    ExtensionChatMode[] SupportedChatModes,
    ExtensionActionKind ActionKind,
    ExtensionSelectionBehavior SelectionBehavior,
    string? DisabledReason = null);

public sealed record ExtensionToolDescriptor(
    string ActionId,
    string ModelToolName,
    string Description,
    JsonElement InputSchema,
    int TimeoutSeconds,
    int MaximumOutputBytes,
    ExtensionToolRiskClass RiskClass = ExtensionToolRiskClass.ReadOnly);

public sealed record ExtensionEntrypointDescriptor(
    ExtensionEntrypointKind Kind,
    string Path);

public sealed record ExtensionManifest(
    int SchemaVersion,
    string Id,
    string Version,
    string DisplayName,
    string Publisher,
    ExtensionEntrypointDescriptor[] Entrypoints,
    ExtensionPermissionKind[] Permissions,
    ExtensionActionDescriptor[] Actions,
    ExtensionToolDescriptor[] Tools);

public sealed record ExtensionValidationResult(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;

    public void ThrowIfInvalid()
    {
        if (!IsValid)
            throw new InvalidDataException("Das Extension-Manifest ist ungültig: " + string.Join(" ", Errors));
    }
}
