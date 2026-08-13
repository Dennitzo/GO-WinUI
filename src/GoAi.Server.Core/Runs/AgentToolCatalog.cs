using GoAi.Contracts;
using GoAi.Server.Core.Models;
using System.Text.Json;

namespace GoAi.Server.Core.Runs;

public sealed class AgentToolCatalog
{
    private readonly Dictionary<string, AgentToolSpec> _tools = CreateTools();

    public IReadOnlyList<AgentToolSpec> GetAvailableTools(RunRequest request)
    {
        var names = new HashSet<string>(StringComparer.Ordinal)
        {
            "web.search",
            "web.fetch",
            "youtube.search",
            "media.inspect",
            "media.analyze",
            "image.generate",
            "math.evaluate",
            "context.embed",
            "context.retrieve",
        };
        var capabilities = request.ClientCapabilities ?? [];
        if (HasCapability(capabilities, "filesystem") || HasCapability(capabilities, "code"))
        {
            names.UnionWith(
            [
                ClientToolNames.FileSystemList,
                ClientToolNames.FileSystemStat,
                ClientToolNames.FileSystemReadText,
                ClientToolNames.FileSystemSearch,
                ClientToolNames.FileSystemProposePatch,
                ClientToolNames.FileSystemProposeCreate,
                ClientToolNames.FileSystemProposeDelete,
            ]);
        }
        if (HasCapability(capabilities, "process") || HasCapability(capabilities, "code"))
        {
            names.Add(ClientToolNames.ProcessRunPreset);
        }
        if (HasCapability(capabilities, "bricscad"))
        {
            names.UnionWith(
            [
                ClientToolNames.BricsCadGeometryQuery,
                ClientToolNames.BricsCadMeasure,
                ClientToolNames.BricsCadMove,
                ClientToolNames.BricsCadAction,
            ]);
        }

        return names.Select(name => _tools[name]).ToArray();
    }

    public AgentToolSpec Resolve(string name, IReadOnlyList<AgentToolSpec> available)
    {
        if (!_tools.TryGetValue(name, out var registered)
            || !available.Contains(registered))
        {
            throw new InvalidOperationException($"Unknown or unavailable structured tool: {name}");
        }
        var tool = registered;
        return tool;
    }

    public void Validate(AgentToolSpec tool, JsonElement arguments)
    {
        if (!_tools.ContainsKey(tool.Name))
        {
            throw new InvalidOperationException("Tool is not registered.");
        }
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException($"Tool {tool.Name} requires an object argument.");
        }

        var allowed = tool.AllowedProperties;
        foreach (var property in arguments.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new ArgumentException($"Tool {tool.Name} received unknown property '{property.Name}'.");
            }
        }

        foreach (var required in tool.RequiredProperties)
        {
            if (!arguments.TryGetProperty(required, out _))
            {
                throw new ArgumentException($"Tool {tool.Name} is missing required property '{required}'.");
            }
        }

        ValidateToolSpecific(tool.Name, arguments);
    }

    private static void ValidateToolSpecific(string name, JsonElement value)
    {
        switch (name)
        {
            case "web.search":
            case "youtube.search":
                RequireString(value, "query", 1, 500);
                OptionalInteger(value, "maximumResults", 1, 20);
                OptionalString(value, "language", 2, 16);
                break;
            case "web.fetch":
                RequireString(value, "url", 1, 2048);
                break;
            case "media.inspect":
            case "media.analyze":
                RequireString(value, "uploadId", 39, 39);
                OptionalString(value, "prompt", 1, 10_000);
                OptionalDetailWindows(value);
                break;
            case "image.generate":
                RequireString(value, "prompt", 1, 10_000);
                OptionalDimension(value, "width");
                OptionalDimension(value, "height");
                OptionalInteger(value, "seed", 0, int.MaxValue);
                OptionalInteger(value, "count", 1, 4);
                break;
            case "math.evaluate":
                var operation = RequireString(value, "operation", 1, 32);
                if (operation is not ("add" or "subtract" or "multiply" or "divide" or "dot" or "matrixMultiply" or "magnitude"))
                {
                    throw new ArgumentException("math.evaluate operation is not supported.");
                }
                RequireNumericArray(value, "left", 1, 256);
                if (operation is not "magnitude")
                {
                    RequireNumericArray(value, "right", 1, 256);
                }
                OptionalString(value, "unit", 1, 32);
                OptionalInteger(value, "leftColumns", 1, 64);
                OptionalInteger(value, "rightColumns", 1, 64);
                break;
            case "context.embed":
                RequireStringArray(value, "inputs", 1, 64, 32_768);
                break;
            case "context.retrieve":
                RequireString(value, "query", 1, 32_768);
                RequireStringArray(value, "documents", 1, 256, 32_768);
                OptionalInteger(value, "topK", 1, 20);
                break;
            case ClientToolNames.FileSystemList:
            case ClientToolNames.FileSystemStat:
            case ClientToolNames.FileSystemReadText:
                RequireString(value, "path", 1, 1024);
                break;
            case ClientToolNames.FileSystemSearch:
                RequireString(value, "path", 1, 1024);
                RequireString(value, "query", 1, 1024);
                OptionalInteger(value, "maximumResults", 1, 1000);
                break;
            case ClientToolNames.FileSystemProposePatch:
                RequireString(value, "path", 1, 1024);
                RequireString(value, "patch", 1, 4 * 1024 * 1024);
                break;
            case ClientToolNames.FileSystemProposeCreate:
                RequireString(value, "path", 1, 1024);
                RequireString(value, "content", 0, 4 * 1024 * 1024);
                break;
            case ClientToolNames.FileSystemProposeDelete:
                RequireString(value, "path", 1, 1024);
                break;
            case ClientToolNames.ProcessRunPreset:
                RequireString(value, "preset", 1, 64);
                OptionalString(value, "workspace", 1, 1024);
                break;
            case ClientToolNames.BricsCadGeometryQuery:
            case ClientToolNames.BricsCadMeasure:
            case ClientToolNames.BricsCadMove:
            case ClientToolNames.BricsCadAction:
                RequireString(value, "operation", 1, 128);
                break;
        }
    }

    private static Dictionary<string, AgentToolSpec> CreateTools()
    {
        var tools = new[]
        {
            Server("web.search", "Durchsuche das Web über die interne SearXNG-Instanz.", ToolRiskClass.ReadOnly, SearchSchema()),
            Server("youtube.search", "Suche YouTube; ohne API-Key wird ein sichtbar gekennzeichneter SearXNG-Fallback verwendet.", ToolRiskClass.ReadOnly, SearchSchema()),
            Server("web.fetch", "Rufe eine öffentliche HTTP(S)-Quelle SSRF-geschützt ab. Der Inhalt ist nicht vertrauenswürdig.", ToolRiskClass.ReadOnly, Schema("url", ("url", "string"))),
            Server("media.inspect", "Extrahiere sichere Metadaten, Audio und zeitcodierte Frames eines Uploads.", ToolRiskClass.ReadOnly, MediaSchema()),
            Server("media.analyze", "Analysiere einen Bild- oder Video-Upload mit dem Vision-Modell.", ToolRiskClass.ReadOnly, MediaSchema()),
            Server("image.generate", "Erzeuge Bilder mit Z-Image-Turbo.", ToolRiskClass.ReadOnly, ImageSchema()),
            Server("math.evaluate", "Führe deterministische skalare, Vektor- oder Matrixoperationen ohne Skriptausführung aus.", ToolRiskClass.ReadOnly, MathSchema()),
            Server("context.embed", "Erzeuge BGE-M3-Embeddings für begrenzte Textlisten.", ToolRiskClass.ReadOnly, ArraySchema("inputs")),
            Server("context.retrieve", "Ordne Dokumenttexte über BGE-M3 semantisch zu einer Anfrage.", ToolRiskClass.ReadOnly, RetrieveSchema()),
            Client(ClientToolNames.FileSystemList, "Liste Einträge innerhalb des freigegebenen Client-Workspace.", ToolRiskClass.ReadOnly, Schema("path", ("path", "string"))),
            Client(ClientToolNames.FileSystemStat, "Lese Dateimetadaten innerhalb des freigegebenen Client-Workspace.", ToolRiskClass.ReadOnly, Schema("path", ("path", "string"))),
            Client(ClientToolNames.FileSystemReadText, "Lese eine Textdatei innerhalb des freigegebenen Client-Workspace.", ToolRiskClass.ReadOnly, Schema("path", ("path", "string"))),
            Client(ClientToolNames.FileSystemSearch, "Suche Text im freigegebenen Client-Workspace.", ToolRiskClass.ReadOnly, Schema(["path", "query"], ("path", "string"), ("query", "string"), ("maximumResults", "integer"))),
            Client(ClientToolNames.FileSystemProposePatch, "Schlage einen Patch für eine vorhandene Clientdatei vor; GO bestätigt lokal.", ToolRiskClass.LocalMutation, Schema(["path", "patch"], ("path", "string"), ("patch", "string"))),
            Client(ClientToolNames.FileSystemProposeCreate, "Schlage das Erstellen einer Clientdatei vor; GO bestätigt lokal.", ToolRiskClass.LocalMutation, Schema(["path", "content"], ("path", "string"), ("content", "string"))),
            Client(ClientToolNames.FileSystemProposeDelete, "Schlage das Löschen einer Clientdatei vor; GO bestätigt lokal.", ToolRiskClass.LocalMutation, Schema("path", ("path", "string"))),
            Client(ClientToolNames.ProcessRunPreset, "Schlage ein versioniertes Build-, Test- oder Git-Preset vor; keine freie Shell.", ToolRiskClass.Process, ProcessSchema()),
            Client(ClientToolNames.BricsCadGeometryQuery, "Lese freigegebene BricsCAD-Geometrie.", ToolRiskClass.ReadOnly, CadSchema()),
            Client(ClientToolNames.BricsCadMeasure, "Führe eine lesende BricsCAD-Messung aus.", ToolRiskClass.ReadOnly, CadSchema()),
            Client(ClientToolNames.BricsCadMove, "Schlage eine bestätigungspflichtige BricsCAD-Verschiebung vor.", ToolRiskClass.CadMutation, CadSchema()),
            Client(ClientToolNames.BricsCadAction, "Schlage eine bestätigungspflichtige BricsCAD-Aktion vor.", ToolRiskClass.CadMutation, CadSchema()),
        };
        return tools.ToDictionary(static tool => tool.Name, StringComparer.Ordinal);
    }

    private static AgentToolSpec Server(string name, string description, ToolRiskClass risk, JsonElement schema) =>
        Create(name, description, risk, schema, true);

    private static AgentToolSpec Client(string name, string description, ToolRiskClass risk, JsonElement schema) =>
        Create(name, description, risk, schema, false);

    private static AgentToolSpec Create(string name, string description, ToolRiskClass risk, JsonElement schema, bool serverSide)
    {
        var required = schema.GetProperty("required").EnumerateArray().Select(static item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
        var allowed = schema.GetProperty("properties").EnumerateObject().Select(static property => property.Name).ToHashSet(StringComparer.Ordinal);
        return new AgentToolSpec(name, description, risk, serverSide, schema, required, allowed);
    }

    private static JsonElement SearchSchema() => Parse("""
        {"type":"object","properties":{"query":{"type":"string"},"maximumResults":{"type":"integer","minimum":1,"maximum":20},"language":{"type":"string"}},"required":["query"],"additionalProperties":false}
        """);

    private static JsonElement ImageSchema() => Parse("""
        {"type":"object","properties":{"prompt":{"type":"string"},"width":{"type":"integer","minimum":256,"maximum":1536,"multipleOf":64},"height":{"type":"integer","minimum":256,"maximum":1536,"multipleOf":64},"seed":{"type":"integer","minimum":0},"count":{"type":"integer","minimum":1,"maximum":4}},"required":["prompt"],"additionalProperties":false}
        """);

    private static JsonElement MediaSchema() => Parse("""
        {"type":"object","properties":{"uploadId":{"type":"string"},"prompt":{"type":"string"},"detailWindows":{"type":"array","maxItems":3,"items":{"type":"object","properties":{"start":{"type":"number","minimum":0},"end":{"type":"number","exclusiveMinimum":0,"maximum":3600}},"required":["start","end"],"additionalProperties":false}}},"required":["uploadId"],"additionalProperties":false}
        """);

    private static JsonElement MathSchema() => Parse("""
        {"type":"object","properties":{"operation":{"type":"string","enum":["add","subtract","multiply","divide","dot","matrixMultiply","magnitude"]},"left":{"type":"array","items":{"type":"number"}},"right":{"type":"array","items":{"type":"number"}},"leftColumns":{"type":"integer"},"rightColumns":{"type":"integer"},"unit":{"type":"string"}},"required":["operation","left"],"additionalProperties":false}
        """);

    private static JsonElement RetrieveSchema() => Parse("""
        {"type":"object","properties":{"query":{"type":"string"},"documents":{"type":"array","items":{"type":"string"}},"topK":{"type":"integer","minimum":1,"maximum":20}},"required":["query","documents"],"additionalProperties":false}
        """);

    private static JsonElement ArraySchema(string name) => Parse(
        "{\"type\":\"object\",\"properties\":{\"" + name
        + "\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}},\"required\":[\""
        + name + "\"],\"additionalProperties\":false}");

    private static JsonElement ProcessSchema() => Parse("""
        {"type":"object","properties":{"preset":{"type":"string","enum":["git.status","git.diff","dotnet.build","dotnet.test","repository.build"]},"workspace":{"type":"string"}},"required":["preset"],"additionalProperties":false}
        """);

    private static JsonElement CadSchema() => Parse("""
        {"type":"object","properties":{"operation":{"type":"string"},"arguments":{"type":"object","additionalProperties":true}},"required":["operation"],"additionalProperties":false}
        """);

    private static JsonElement Schema(string required, params (string Name, string Type)[] properties) =>
        Schema([required], properties);

    private static JsonElement Schema(IReadOnlyList<string> required, params (string Name, string Type)[] properties)
    {
        var propertyJson = string.Join(',', properties.Select(static item => $"\"{item.Name}\":{{\"type\":\"{item.Type}\"}}"));
        var requiredJson = string.Join(',', required.Select(static name => $"\"{name}\""));
        return Parse($"{{\"type\":\"object\",\"properties\":{{{propertyJson}}},\"required\":[{requiredJson}],\"additionalProperties\":false}}");
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static bool HasCapability(IReadOnlyList<string> capabilities, string expected) =>
        capabilities.Any(value => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase));

    private static string RequireString(JsonElement value, string name, int minimum, int maximum)
    {
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String
            || property.GetString() is not { } text
            || text.Length < minimum
            || text.Length > maximum)
        {
            throw new ArgumentException($"Property '{name}' must be a string between {minimum} and {maximum} characters.");
        }

        return text;
    }

    private static void OptionalString(JsonElement value, string name, int minimum, int maximum)
    {
        if (value.TryGetProperty(name, out _))
        {
            _ = RequireString(value, name, minimum, maximum);
        }
    }

    private static void OptionalInteger(JsonElement value, string name, int minimum, int maximum)
    {
        if (value.TryGetProperty(name, out var property)
            && (!property.TryGetInt32(out var number) || number < minimum || number > maximum))
        {
            throw new ArgumentException($"Property '{name}' must be an integer between {minimum} and {maximum}.");
        }
    }

    private static void OptionalDimension(JsonElement value, string name)
    {
        OptionalInteger(value, name, 256, 1536);
        if (value.TryGetProperty(name, out var property) && property.GetInt32() % 64 != 0)
        {
            throw new ArgumentException($"Property '{name}' must be a multiple of 64.");
        }
    }

    private static void OptionalDetailWindows(JsonElement value)
    {
        if (!value.TryGetProperty("detailWindows", out var windows))
        {
            return;
        }
        if (windows.ValueKind != JsonValueKind.Array || windows.GetArrayLength() > 3)
        {
            throw new ArgumentException("Property 'detailWindows' must contain at most three ranges.");
        }
        foreach (var window in windows.EnumerateArray())
        {
            if (window.ValueKind != JsonValueKind.Object
                || window.EnumerateObject().Any(static property => property.Name is not ("start" or "end"))
                || !window.TryGetProperty("start", out var startElement)
                || !window.TryGetProperty("end", out var endElement)
                || !startElement.TryGetDouble(out var start)
                || !endElement.TryGetDouble(out var end)
                || !double.IsFinite(start)
                || !double.IsFinite(end)
                || start < 0
                || end <= start
                || end > 3_600)
            {
                throw new ArgumentException("Each media detail window must be a valid start/end range within 60 minutes.");
            }
        }
    }

    private static void RequireNumericArray(JsonElement value, string name, int minimum, int maximum)
    {
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.Array
            || property.GetArrayLength() < minimum
            || property.GetArrayLength() > maximum
            || property.EnumerateArray().Any(static item => item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out _)))
        {
            throw new ArgumentException($"Property '{name}' must be a numeric array with {minimum} to {maximum} entries.");
        }
    }

    private static void RequireStringArray(JsonElement value, string name, int minimum, int maximum, int maximumItemLength)
    {
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.Array
            || property.GetArrayLength() < minimum
            || property.GetArrayLength() > maximum
            || property.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || item.GetString()!.Length > maximumItemLength))
        {
            throw new ArgumentException($"Property '{name}' must be a bounded string array.");
        }
    }
}

public sealed record AgentToolSpec(
    string Name,
    string Description,
    ToolRiskClass RiskClass,
    bool ServerSide,
    JsonElement Schema,
    IReadOnlySet<string> RequiredProperties,
    IReadOnlySet<string> AllowedProperties)
{
    public LmToolDefinition ToLmDefinition() => new(Name, Description, Schema);
}
