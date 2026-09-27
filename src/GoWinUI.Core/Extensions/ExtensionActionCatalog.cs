using System.Text.Json;

namespace GoWinUI.Core.Extensions;

public interface IExtensionActionCatalog
{
    IReadOnlyList<ExtensionActionDescriptor> GetActions(ExtensionChatMode mode);
    bool TryGetAction(string actionId, out ExtensionActionDescriptor? descriptor);
    bool TryGetTool(string actionId, out ExtensionToolDescriptor? descriptor);
    bool TryGetToolByModelName(string modelToolName, out ExtensionToolDescriptor? descriptor);
    void Register(ExtensionManifest manifest);
}

public sealed class ExtensionActionCatalog : IExtensionActionCatalog
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ExtensionActionDescriptor> _actions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExtensionToolDescriptor> _toolsByAction = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _actionsByToolName = new(StringComparer.Ordinal);

    public static ExtensionActionCatalog CreateWithBuiltIns()
    {
        var catalog = new ExtensionActionCatalog();
        catalog.RegisterDescriptors(BuiltInExtensionCatalog.Actions, BuiltInExtensionCatalog.Tools);
        return catalog;
    }

    public IReadOnlyList<ExtensionActionDescriptor> GetActions(ExtensionChatMode mode)
    {
        lock (_gate)
        {
            return _actions.Values
                .Where(action => action.SupportedChatModes.Contains(mode))
                .OrderBy(static action => action.GroupOrder)
                .ThenBy(static action => action.ItemOrder)
                .ThenBy(static action => action.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(CloneAction)
                .ToArray();
        }
    }

    public bool TryGetAction(string actionId, out ExtensionActionDescriptor? descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        lock (_gate)
        {
            if (_actions.TryGetValue(actionId, out var found))
            {
                descriptor = CloneAction(found);
                return true;
            }
        }

        descriptor = null;
        return false;
    }

    public bool TryGetTool(string actionId, out ExtensionToolDescriptor? descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        lock (_gate)
        {
            if (_toolsByAction.TryGetValue(actionId, out var found))
            {
                descriptor = CloneTool(found);
                return true;
            }
        }

        descriptor = null;
        return false;
    }

    public bool TryGetToolByModelName(string modelToolName, out ExtensionToolDescriptor? descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelToolName);
        lock (_gate)
        {
            if (_actionsByToolName.TryGetValue(modelToolName, out var actionId)
                && _toolsByAction.TryGetValue(actionId, out var found))
            {
                descriptor = CloneTool(found);
                return true;
            }
        }

        descriptor = null;
        return false;
    }

    public void Register(ExtensionManifest manifest)
    {
        ExtensionManifestValidator.Validate(manifest).ThrowIfInvalid();
        RegisterDescriptors(manifest.Actions, manifest.Tools);
    }

    private void RegisterDescriptors(
        IReadOnlyCollection<ExtensionActionDescriptor> actions,
        IReadOnlyCollection<ExtensionToolDescriptor> tools)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(tools);

        var actionCopies = actions.Select(CloneAction).ToArray();
        var toolCopies = tools.Select(CloneTool).ToArray();
        var incomingActionIds = new HashSet<string>(actionCopies.Select(static action => action.ActionId), StringComparer.Ordinal);
        if (incomingActionIds.Count != actionCopies.Length)
            throw new InvalidDataException("Der Action-Katalog enthält doppelte Action-IDs.");
        var incomingToolNames = new HashSet<string>(StringComparer.Ordinal);
        var incomingToolActions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in actionCopies)
        {
            var separator = action.ActionId.IndexOf('/');
            var expectedExtensionId = separator > 0 ? action.ActionId[..separator] : string.Empty;
            ExtensionManifestValidator.ValidateAction(action, expectedExtensionId).ThrowIfInvalid();
        }
        foreach (var tool in toolCopies)
        {
            if (!incomingActionIds.Contains(tool.ActionId))
                throw new InvalidDataException($"Tool '{tool.ModelToolName}' verweist nicht auf eine gleichzeitig registrierte Action.");
            if (!ExtensionIdentifiers.TryValidateModelToolName(tool.ModelToolName, out var toolError))
                throw new InvalidDataException(toolError);
            if (!incomingToolNames.Add(tool.ModelToolName))
                throw new InvalidDataException($"Modell-Toolname '{tool.ModelToolName}' ist im Registrierungssatz doppelt.");
            if (!incomingToolActions.Add(tool.ActionId))
                throw new InvalidDataException($"Action '{tool.ActionId}' besitzt im Registrierungssatz mehrere Modell-Tools.");
            if (tool.InputSchema.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"InputSchema von '{tool.ModelToolName}' muss ein JSON-Objekt sein.");
            if (!Enum.IsDefined(tool.RiskClass))
                throw new InvalidDataException($"RiskClass von '{tool.ModelToolName}' ist unbekannt.");
        }

        lock (_gate)
        {
            var actionCollision = actionCopies.FirstOrDefault(action => _actions.ContainsKey(action.ActionId));
            if (actionCollision is not null)
                throw new InvalidDataException($"Action-ID '{actionCollision.ActionId}' ist bereits registriert.");
            var toolNameCollision = toolCopies.FirstOrDefault(tool => _actionsByToolName.ContainsKey(tool.ModelToolName));
            if (toolNameCollision is not null)
                throw new InvalidDataException($"Modell-Toolname '{toolNameCollision.ModelToolName}' ist bereits registriert.");

            foreach (var action in actionCopies) _actions.Add(action.ActionId, action);
            foreach (var tool in toolCopies)
            {
                _toolsByAction.Add(tool.ActionId, tool);
                _actionsByToolName.Add(tool.ModelToolName, tool.ActionId);
            }
        }
    }

    private static ExtensionActionDescriptor CloneAction(ExtensionActionDescriptor action) =>
        action with { SupportedChatModes = [.. action.SupportedChatModes] };

    private static ExtensionToolDescriptor CloneTool(ExtensionToolDescriptor tool) =>
        tool with { InputSchema = tool.InputSchema.Clone() };
}

public static class BuiltInExtensionCatalog
{
    private static readonly ExtensionChatMode[] AllModes =
        [ExtensionChatMode.General, ExtensionChatMode.Coding, ExtensionChatMode.ClaudeScience];

    public static IReadOnlyDictionary<string, string> GroupLabels { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["add"] = "Hinzufügen",
            ["planning"] = "Arbeitsweise",
            ["research"] = "Recherche und Analyse",
            ["create"] = "Erstellen",
            ["export"] = "Exportieren",
            ["speech"] = "Sprache und Audio",
            ["workflows"] = "Workflows",
            ["extensions"] = "Erweiterungen",
        };

    public static IReadOnlyList<ExtensionActionDescriptor> Actions { get; } =
    [
        Immediate(BuiltInActionIds.AttachFilesAndFolders, "Dateien und Ordner", "Dateien oder Ordner zum aktuellen Chat hinzufügen", "attachment", "add", 10, 10),
        SelectableCoding(BuiltInActionIds.PlanMode, "Planmodus", "Projekt schreibgeschützt analysieren und vor Änderungen einen umsetzbaren Plan abstimmen", "plan", "planning", 15, 10),
        Selectable(BuiltInActionIds.WebSearch, "Websuche", "Aktuelle Quellen im Web durchsuchen", "web", "research", 20, 10),
        Selectable(BuiltInActionIds.DeepResearch, "Deep Research", "Eine mehrstufige, quellenbasierte Recherche starten", "research", "research", 20, 20),
        Selectable(BuiltInActionIds.ImageAnalysis, "Bild analysieren", "Bilder inhaltlich untersuchen", "image", "research", 20, 30),
        Selectable(BuiltInActionIds.AudioAnalysis, "Audio analysieren", "Audiodateien inhaltlich untersuchen", "audio", "research", 20, 40),
        Selectable(BuiltInActionIds.VideoAnalysis, "Video analysieren", "Videodateien inhaltlich untersuchen", "video", "research", 20, 50),
        Selectable(BuiltInActionIds.CreateDocument, "Dokument", "Ein strukturiertes Dokument erstellen", "document", "create", 30, 10),
        Selectable(BuiltInActionIds.GenerateImage, "Bild", "Ein Bild aus einer Beschreibung erzeugen", "image", "create", 30, 20),
        Selectable(BuiltInActionIds.CreateAudiobook, "Hörbuch", "Text als gegliedertes Hörbuch erzeugen", "audiobook", "create", 30, 30),
        Immediate(BuiltInActionIds.ExportChatPdf, "Chat als PDF exportieren", "Den vollständigen Chat über den Speicherdialog als PDF exportieren", "pdf", "export", 40, 10),
        Selectable(BuiltInActionIds.Translate, "Übersetzen", "Text in eine andere Sprache übertragen", "translate", "speech", 50, 10),
        Selectable(BuiltInActionIds.ReadAloud, "Vorlesen", "Text mit lokaler Sprachausgabe vorlesen", "speech", "speech", 50, 20),
        Immediate(BuiltInActionIds.LiveCaptions, "Live-Untertitel", "Untertitel für laufende Sprache ein- oder ausschalten", "captions", "speech", 50, 30),
        Immediate(BuiltInActionIds.OpenWorkflowLibrary, "Workflows öffnen und verwalten", "Die Workflow-Bibliothek öffnen", "workflow", "workflows", 60, 10),
        Immediate(BuiltInActionIds.OpenMemoryLibrary, "Projektgedächtnis", "Anforderungen, Entscheidungen und Meilensteine verwalten", "memory", "extensions", 70, 10),
    ];

    public static IReadOnlyList<ExtensionToolDescriptor> Tools { get; } =
    [
        Tool(BuiltInActionIds.WebSearch, "web.search", "Aktuelle Quellen im Web durchsuchen", 120, 2 * 1024 * 1024),
        Tool(BuiltInActionIds.DeepResearch, "research.deep", "Mehrstufige Recherche mit Quellen ausführen", 1_800, 16 * 1024 * 1024),
        Tool(BuiltInActionIds.ImageAnalysis, "media.image.analyze", "Bildinhalte analysieren", 600, 16 * 1024 * 1024),
        Tool(BuiltInActionIds.AudioAnalysis, "media.audio.analyze", "Audioinhalte analysieren", 600, 16 * 1024 * 1024),
        Tool(BuiltInActionIds.VideoAnalysis, "media.video.analyze", "Videoinhalte analysieren", 600, 16 * 1024 * 1024),
        Tool(BuiltInActionIds.CreateDocument, "document.create", "Ein Dokument erzeugen", 600, 16 * 1024 * 1024),
        Tool(BuiltInActionIds.GenerateImage, "image.generate", "Ein Bild erzeugen", 600, 16 * 1024 * 1024),
        Tool(BuiltInActionIds.CreateAudiobook, "audiobook.create", "Ein Hörbuch erzeugen", 1_800, 32 * 1024 * 1024),
        Tool(BuiltInActionIds.Translate, "speech.translate", "Text übersetzen", 300, 4 * 1024 * 1024),
        Tool(BuiltInActionIds.ReadAloud, "speech.synthesize", "Text lokal vorlesen", 300, 4 * 1024 * 1024),
    ];

    private static ExtensionActionDescriptor Immediate(
        string actionId,
        string name,
        string description,
        string icon,
        string group,
        int groupOrder,
        int itemOrder) =>
        new(actionId, name, description, icon, group, groupOrder, itemOrder, [.. AllModes],
            ExtensionActionKind.Immediate, ExtensionSelectionBehavior.None);

    private static ExtensionActionDescriptor Selectable(
        string actionId,
        string name,
        string description,
        string icon,
        string group,
        int groupOrder,
        int itemOrder) =>
        new(actionId, name, description, icon, group, groupOrder, itemOrder, [.. AllModes],
            ExtensionActionKind.SelectableTool, ExtensionSelectionBehavior.Toggle);

    private static ExtensionActionDescriptor SelectableCoding(
        string actionId,
        string name,
        string description,
        string icon,
        string group,
        int groupOrder,
        int itemOrder) =>
        new(actionId, name, description, icon, group, groupOrder, itemOrder, [ExtensionChatMode.Coding],
            ExtensionActionKind.SelectableTool, ExtensionSelectionBehavior.Toggle);

    private static ExtensionToolDescriptor Tool(
        string actionId,
        string modelToolName,
        string description,
        int timeoutSeconds,
        int maximumOutputBytes,
        ExtensionToolRiskClass riskClass = ExtensionToolRiskClass.ReadOnly,
        object? schema = null) =>
        new(actionId, modelToolName, description,
            JsonSerializer.SerializeToElement(schema ?? new { type = "object", additionalProperties = true }),
            timeoutSeconds, maximumOutputBytes, riskClass);
}
