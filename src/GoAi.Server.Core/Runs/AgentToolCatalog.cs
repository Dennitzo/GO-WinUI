using GoAi.Contracts;
using GoAi.Server.Core.Coding;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Research;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GoAi.Server.Core.Runs;

public sealed partial class AgentToolCatalog
{
    public const string SelectorToolName = "go.selectTool";
    private static readonly string[] SelectorRequiredProperties = ["name"];
    private static readonly string[] DefaultServerTools =
    [
        "web.search", "web.fetch", "youtube.search", "media.inspect", "media.analyze",
        "image.generate", "speech.synthesize", "math.evaluate", "context.embed", "context.retrieve",
    ];
    private readonly Dictionary<string, AgentToolSpec> _tools = CreateTools();

    public IReadOnlyList<AgentToolSpec> GetAvailableTools(RunRequest request)
    {
        var requestedServerTools = request.AllowedServerTools ?? (request.Mode == RunMode.Coding ? [] : DefaultServerTools);
        if (requestedServerTools.Contains(CodingDeepResearchPipeline.ToolName, StringComparer.Ordinal)
            && (!requestedServerTools.Contains("web.search", StringComparer.Ordinal)
                || !requestedServerTools.Contains("web.fetch", StringComparer.Ordinal)))
            throw new ArgumentException("web.deepResearch requires explicit web.search/web.fetch permission.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in requestedServerTools)
        {
            if (!_tools.TryGetValue(name, out var tool) || !tool.ServerSide)
            {
                throw new ArgumentException($"Unknown or unavailable server tool: {name}");
            }
            names.Add(name);
        }
        if (names.Contains(CodingWorkingStateTools.PlanTool))
            throw new ArgumentException("Coding state tools are selected by coding capabilities/options, not server-tool permissions.");
        var capabilities = request.ClientCapabilities ?? [];
        if (HasCapability(capabilities, "coding") && (request.Mode == RunMode.Coding || HasCapability(capabilities, "workspace")))
        {
            names.UnionWith(CodingToolCatalog.CreateTools().Where(tool => tool.Name is not ("coding.readOutput" or "coding.searchRunEvidence")
                || HasCapability(capabilities, "coding.evidence")).Select(static tool => tool.Name));
            names.Add(CodingWorkingStateTools.PlanTool);
        }
        if (HasCapability(capabilities, "documentIo"))
        {
            names.UnionWith([ClientToolNames.DocumentRead, ClientToolNames.DocumentCreate]);
        }
        if (HasCapability(capabilities, "documents"))
        {
            names.UnionWith([ClientToolNames.DocumentsList, ClientToolNames.DocumentsSearch, ClientToolNames.DocumentsReadPages]);
        }
        if (HasCapability(capabilities, "visual-tools")) names.Add(WorkspaceTools.ImageInput);
        if (HasCapability(capabilities, "blender")) names.Add(WorkspaceTools.Blender);
        if (HasCapability(capabilities, "workspace")) names.Add(WorkspaceTools.Open);
        // PDF bytes are never model-generated. document.create edits a bounded
        // canonical source and delegates rendering to GO's deterministic path.
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

        return names
            .OrderBy(static name => name, StringComparer.Ordinal)
            .Select(name => _tools[name])
            .ToArray();
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

    public static LmToolDefinition CreateSelectorDefinition(IReadOnlyList<AgentToolSpec> available)
    {
        ArgumentNullException.ThrowIfNull(available);
        if (available.Count == 0)
        {
            throw new ArgumentException("A tool selector requires at least one available tool.", nameof(available));
        }
        var ordered = available.OrderBy(static tool => tool.Name, StringComparer.Ordinal).ToArray();
        var catalog = string.Join("\n", ordered.Select(static tool => $"- {tool.Name}: {tool.Description[..Math.Min(180, tool.Description.Length)]}"));
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                name = new
                {
                    type = "string",
                    @enum = ordered.Select(static tool => tool.Name).ToArray(),
                    description = "Name des Werkzeugs, dessen vollständiges Schema im nächsten Modellturn benötigt wird.",
                },
            },
            required = SelectorRequiredProperties,
        }, GoAiProtocol.CreateJsonOptions());
        return new LmToolDefinition(
            SelectorToolName,
            "Wähle genau einen Werkzeugnamen. GO stellt im nächsten Modellturn ausschließlich Beschreibung und vollständiges Schema dieses Werkzeugs bereit.\n" + catalog,
            schema);
    }

    public AgentToolSpec ResolveSelection(JsonElement arguments, IReadOnlyList<AgentToolSpec> available)
    {
        if (arguments.ValueKind != JsonValueKind.Object
            || arguments.EnumerateObject().Any(static property => property.Name != "name")
            || !arguments.TryGetProperty("name", out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new ArgumentException("go.selectTool requires exactly one non-empty string property named 'name'.");
        }
        return Resolve(value.GetString()!, available);
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
        if (WorkspaceTools.IsLocal(name)) { WorkspaceTools.Validate(name, value); return; }
        if (name == CodingWorkingStateTools.PlanTool)
        {
            CodingWorkingStateTools.Validate(value);
            return;
        }
        if (name.StartsWith("coding.", StringComparison.Ordinal))
        {
            CodingToolCatalog.Validate(name, value);
            return;
        }
        switch (name)
        {
            case CodingDeepResearchPipeline.ToolName:
                RequireString(value, "task", 1, 4_000);
                OptionalInteger(value, "maximumSearches", 2, 3);
                OptionalInteger(value, "maximumSources", 2, 6);
                break;
            case "speech.synthesize":
                RequireString(value, "text", 1, 20_000);
                break;
            case "web.fetch":
                RequireString(value, "url", 1, 2_048);
                OptionalString(value, "query", 1, 512);
                OptionalStringArray(value, "queries", 8, 512);
                OptionalInteger(value, "maximumResults", 1, 20);
                OptionalInteger(value, "contextCharacters", 100, 2_000);
                OptionalInteger(value, "maximumCharacters", 1_000, 12_000);
                break;
            case ClientToolNames.DocumentRead:
                var documentScope = RequireString(value, "scope", 1, 16);
                var documentReadMode = RequireString(value, "mode", 1, 16);
                if (documentScope != "session")
                {
                    throw new ArgumentException("document.read scope is not supported.");
                }
                if (documentReadMode is not ("list" or "outline" or "read" or "search"))
                {
                    throw new ArgumentException("document.read mode is not supported.");
                }
                OptionalString(value, "reference", 1, 1024);
                OptionalString(value, "query", 1, 2000);
                OptionalInteger(value, "startUnit", 1, 1_000_000);
                OptionalInteger(value, "characterOffset", 0, 4 * 1024 * 1024);
                OptionalInteger(value, "maximumUnits", 1, 30);
                OptionalInteger(value, "maximumCharacters", 1000, 40_000);
                if (documentReadMode != "list" && !value.TryGetProperty("reference", out _))
                {
                    throw new ArgumentException("document.read requires reference outside list mode.");
                }
                if (documentReadMode == "search" && !value.TryGetProperty("query", out _))
                {
                    throw new ArgumentException("document.read search requires query.");
                }
                break;
            case ClientToolNames.DocumentCreate:
                var documentOperation = RequireString(value, "operation", 1, 32);
                if (documentOperation is not ("create" or "appendSection" or "replaceSection"))
                {
                    throw new ArgumentException("document.create operation is not supported.");
                }
                RequireString(value, "reference", 1, 1024);
                var documentFormat = RequireString(value, "format", 1, 16);
                if (documentFormat is not ("markdown" or "text" or "docx" or "pdf"))
                {
                    throw new ArgumentException("document.create format is not supported.");
                }
                RequireString(value, "sectionId", 1, 128);
                RequireString(value, "content", 0, 120_000);
                OptionalString(value, "heading", 1, 500);
                OptionalString(value, "expectedSha256", 64, 64);
                if (documentOperation != "create" && !value.TryGetProperty("expectedSha256", out _))
                {
                    throw new ArgumentException("document.create edits require expectedSha256.");
                }
                break;
            case ClientToolNames.DocumentsList:
                break;
            case ClientToolNames.DocumentsSearch:
                RequireString(value, "query", 1, 20_000);
                OptionalInteger(value, "maximumCharacters", 1_000, 200_000);
                break;
            case ClientToolNames.DocumentsReadPages:
                RequireString(value, "documentId", 36, 36);
                OptionalInteger(value, "startPage", 1, 1_000_000);
                OptionalInteger(value, "endPage", 1, 1_000_000);
                break;
            case "web.search":
            case "youtube.search":
                RequireString(value, "query", 1, 500);
                OptionalInteger(value, "maximumResults", 1, 20);
                OptionalString(value, "language", 2, 16);
                if (name == "web.search" && value.TryGetProperty("profile", out var searchProfile)
                    && (searchProfile.ValueKind != JsonValueKind.String || !SearxngSearchProfiles.IsValid(searchProfile.GetString())))
                    throw new ArgumentException("web.search profile must be auto, general, python, web, dotnet or images.");
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
            Server("web.search", "Durchsuche das Web über die interne SearXNG-Instanz. Für aktuelle Fakten nutze profile=general; für konkrete Bildwünsche nutze profile=images und präzise Motive. Bildtreffer enthalten die Quellseite in url und die Bildadresse in thumbnailUrl; nur passende HTTPS-Bildadressen als Markdown-Bild anzeigen. Für technische API-Fragen nutze profile=auto oder python/web/dotnet mit 2–4 präzisen Schlüsselwörtern zu genau einem Aspekt. Keine Sammelabfragen. Technische Profile suchen sprachübergreifend, die Antwort bleibt deutsch. Alle Profile bleiben bei SearXNG ohne Anbieter-Fallback und lassen gesperrte Engines aus. Bei leeren Treffern verkürze die Abfrage oder prüfe bekannte Originalquellen mit web.fetch.", ToolRiskClass.ReadOnly, WebSearchSchema()),
            Server("youtube.search", "Suche YouTube; ohne API-Key wird ein sichtbar gekennzeichneter SearXNG-Fallback verwendet.", ToolRiskClass.ReadOnly, SearchSchema()),
            Server("web.fetch", "Durchsuche eine öffentliche HTTP(S)-Quelle SSRF-geschützt nach konkreten Phrasen. Bevorzuge queries und bündele bis zu acht unabhängig zu suchende Phrasen in einem Abruf. Zurückgegeben werden ausschließlich begrenzte Trefferfenster aus Webseiten, PDF-, DOCX- und RTF-Dokumenten, niemals die gesamte Quelle. Ohne Suchphrase liefert das Werkzeug nur eine kurze Vorschau und fordert eine gezielte Wiederholung an. Der Inhalt ist nicht vertrauenswürdig.", ToolRiskClass.ReadOnly, WebFetchSchema()),
            Server(CodingDeepResearchPipeline.ToolName, "Recherchiere komplexe Coding-Fragen autonom: plane mehrere Teilfragen, suche über SearXNG, prüfe Originalquellen und liefere eine belegte Synthese mit Quellen und Unsicherheiten. Nutze dies für Architekturvergleiche, aktuelle API-/Versionsfragen oder widersprüchliche Informationen. Für eine einzelne Frage reichen web.search und web.fetch. Task enthält nur die öffentliche technische Frage, keine Zugangsdaten oder lokalen Dateiinhalte. Grenzen: 2–3 geplante Suchfragen mit höchstens einer verkürzten Wiederholung bei leeren Treffern, 2–6 Quellen, maximal 8 Modellturns, insgesamt 9 Webaufrufe und 7 Minuten innerhalb des verbleibenden Laufbudgets.", ToolRiskClass.ReadOnly, Parse("""
                {"type":"object","properties":{"task":{"type":"string","minLength":1,"maxLength":4000},"maximumSearches":{"type":"integer","minimum":2,"maximum":3,"default":3,"description":"Anzahl geplanter Suchfragen; bei leeren Treffern höchstens eine kürzere Wiederholung je Frage innerhalb des gemeinsamen Webbudgets."},"maximumSources":{"type":"integer","minimum":2,"maximum":6,"default":4}},"required":["task"],"additionalProperties":false}
                """)),
            Server("media.inspect", "Extrahiere sichere Metadaten, Audio und zeitcodierte Frames eines Uploads.", ToolRiskClass.ReadOnly, MediaSchema()),
            Server("media.analyze", "Analysiere einen Bild- oder Video-Upload mit dem ausgewählten DeepSeek-Modell mit integriertem Vision; bei Textmodellen ohne Vision bleibt der bestehende Fallback. Die Analyse erhält das tatsächliche Bild und protokolliert die verwendete Modell-ID. Für Blender-Referenzen erfasse Silhouette, Proportionen, Baugruppen und Materialien; trenne sichtbare Merkmale von Annahmen. Für Blender-Render nenne im prompt die Ansicht, konkreten Designanforderungen und belegten Referenzmerkmale. Prüfe jedes Kriterium als erfüllt, verletzt oder nicht beurteilbar mit sichtbarem Befund, betroffener Baugruppe und gezieltem Korrekturvorschlag. Verdeckte Details gelten nicht als bestanden. Prüfe Anzahl, Anordnung, Symmetrie, Anschlussstellen, Durchdringungen, Bodenabstand, Farben, Licht und Sichtbarkeit nur soweit im Bild erkennbar.", ToolRiskClass.ReadOnly, MediaSchema()),
            Server("image.generate", "Erzeuge Bilder mit Z-Image-Turbo.", ToolRiskClass.ReadOnly, ImageSchema()),
            Server("math.evaluate", "Führe deterministische skalare, Vektor- oder Matrixoperationen ohne Skriptausführung aus.", ToolRiskClass.ReadOnly, MathSchema()),
            Server("context.embed", "Erzeuge BGE-M3-Embeddings für begrenzte Textlisten.", ToolRiskClass.ReadOnly, ArraySchema("inputs")),
            Server("context.retrieve", "Ordne Dokumenttexte über BGE-M3 semantisch zu einer Anfrage.", ToolRiskClass.ReadOnly, RetrieveSchema()),
            Client(ClientToolNames.DocumentRead, "Lese Sitzungsdokumente tokeneffizient: zuerst auflisten oder eine Gliederung abrufen, danach nur benötigte Abschnitte, Fortsetzungen oder Suchtreffer.", ToolRiskClass.ReadOnly, DocumentReadSchema()),
            Client(ClientToolNames.DocumentCreate, "Erstelle oder bearbeite ein Sitzungsdokument abschnittsweise über stabile sectionId-Werte. GO erzeugt ein versioniertes Chat-Artefakt; PDF wird deterministisch mit GO und KaTeX gerendert.", ToolRiskClass.LocalMutation, DocumentCreateSchema()),
            Client(ClientToolNames.DocumentsList, "Liste alle fertig aufbereiteten Dokumente der aktuellen GO-Sitzung mit Dateiname und Seitenzahl.", ToolRiskClass.ReadOnly, Parse("""{"type":"object","properties":{},"required":[],"additionalProperties":false}""")),
            Client(ClientToolNames.DocumentsSearch, "Durchsuche den persistenten lokalen Dokumentindex promptbezogen und liefere Originalbelege mit Dateiname und Seite.", ToolRiskClass.ReadOnly, Parse("""{"type":"object","properties":{"query":{"type":"string"},"maximumCharacters":{"type":"integer","minimum":1000,"maximum":200000}},"required":["query"],"additionalProperties":false}""")),
            Client(ClientToolNames.DocumentsReadPages, "Lese einen konkreten Seitenbereich eines Sitzungsdokuments als zitierfähigen Originalbeleg.", ToolRiskClass.ReadOnly, Parse("""{"type":"object","properties":{"documentId":{"type":"string"},"startPage":{"type":"integer","minimum":1},"endPage":{"type":"integer","minimum":1}},"required":["documentId","startPage","endPage"],"additionalProperties":false}""")),
            Client(ClientToolNames.BricsCadGeometryQuery, "Lese freigegebene BricsCAD-Geometrie.", ToolRiskClass.ReadOnly, CadSchema()),
            Client(ClientToolNames.BricsCadMeasure, "Führe eine lesende BricsCAD-Messung aus.", ToolRiskClass.ReadOnly, CadSchema()),
            Client(ClientToolNames.BricsCadMove, "Führe eine typisierte BricsCAD-Verschiebung automatisch aus.", ToolRiskClass.CadMutation, CadSchema()),
            Client(ClientToolNames.BricsCadAction, "Führe eine typisierte BricsCAD-Aktion automatisch aus.", ToolRiskClass.CadMutation, CadSchema()),
        };
        return tools.Concat(WorkspaceToolSpecs()).Concat(CodingToolCatalog.CreateTools()).Concat(CodingWorkingStateTools.CreateTools()).ToDictionary(static tool => tool.Name, StringComparer.Ordinal);
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
        {"type":"object","properties":{"query":{"type":"string","description":"Kurze Suchanfrage in der Sprache des aktuellen Nutzerprompts; technische Eigennamen unveraendert lassen."},"maximumResults":{"type":"integer","minimum":1,"maximum":20},"language":{"type":"string","description":"BCP-47-Suchsprache passend zum aktuellen Prompt; Standard de-DE."}},"required":["query"],"additionalProperties":false}
        """);

    private static JsonElement WebSearchSchema() => Parse("""
        {"type":"object","properties":{"query":{"type":"string","description":"Kurze präzise Suchanfrage; technische API-Namen unverändert lassen."},"maximumResults":{"type":"integer","minimum":1,"maximum":20},"language":{"type":"string"},"profile":{"type":"string","enum":["auto","general","python","web","dotnet","images"],"description":"Passende Engines derselben lokalen SearXNG-Instanz; images sucht tatsächliche Bild-URLs."}},"required":["query"],"additionalProperties":false}
        """);

    private static JsonElement WebFetchSchema() => Parse("""
        {"type":"object","properties":{"url":{"type":"string","maxLength":2048},"query":{"type":"string","minLength":1,"maxLength":512,"description":"Eine konkrete Phrase oder ein prägnanter Fachbegriff. Keine Liste mehrerer Begriffe als ein gemeinsamer String."},"queries":{"type":"array","maxItems":8,"items":{"type":"string","minLength":1,"maxLength":512},"description":"Bevorzugt verwenden, wenn mehrere Themen gesucht werden: jede Phrase als eigener Arrayeintrag in demselben Abruf."},"maximumResults":{"type":"integer","minimum":1,"maximum":20,"default":8},"contextCharacters":{"type":"integer","minimum":100,"maximum":2000,"default":500},"maximumCharacters":{"type":"integer","minimum":1000,"maximum":12000,"default":8000}},"required":["url"],"additionalProperties":false}
        """);

    private static JsonElement DocumentReadSchema() => Parse("""
        {"type":"object","properties":{"scope":{"type":"string","enum":["session"]},"mode":{"type":"string","enum":["list","outline","read","search"]},"reference":{"type":"string","description":"Dokument-GUID aus list."},"query":{"type":"string"},"startUnit":{"type":"integer","minimum":1},"characterOffset":{"type":"integer","minimum":0},"maximumUnits":{"type":"integer","minimum":1,"maximum":30},"maximumCharacters":{"type":"integer","minimum":1000,"maximum":40000}},"required":["scope","mode"],"additionalProperties":false}
        """);

    private static JsonElement DocumentCreateSchema() => Parse("""
        {"type":"object","properties":{"operation":{"type":"string","enum":["create","appendSection","replaceSection"]},"reference":{"type":"string","description":"Beim Erstellen ein Dateiname, beim Bearbeiten die documentId des Sitzungsdokuments."},"format":{"type":"string","enum":["markdown","text","docx","pdf"]},"sectionId":{"type":"string","description":"Stabile eindeutige Abschnitts-ID."},"heading":{"type":"string"},"content":{"type":"string","description":"Nur der neue oder geänderte Abschnitt als Markdown, nie das gesamte bestehende Dokument erneut."},"expectedSha256":{"type":"string","description":"Für Bearbeitungen verpflichtender SHA-256 aus document.read oder document.create."}},"required":["operation","reference","format","sectionId","content"],"additionalProperties":false}
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

    private static void OptionalEnum(JsonElement value, string name, IReadOnlyList<string> allowed)
    {
        if (!value.TryGetProperty(name, out _))
        {
            return;
        }
        var selected = RequireString(value, name, 1, 64);
        if (!allowed.Contains(selected, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Property '{name}' contains an unsupported value.");
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

    private static void OptionalStringArray(
        JsonElement value,
        string name,
        int maximum,
        int maximumItemLength,
        bool allowEmpty = false)
    {
        if (!value.TryGetProperty(name, out var property))
        {
            return;
        }
        if (property.ValueKind != JsonValueKind.Array
            || property.GetArrayLength() > maximum
            || property.EnumerateArray().Any(item =>
                item.ValueKind != JsonValueKind.String
                || item.GetString()!.Length > maximumItemLength
                || !allowEmpty && string.IsNullOrWhiteSpace(item.GetString())))
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
