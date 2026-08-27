using GoAi.Contracts;
using GoAi.Server.Core.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GoAi.Server.Core.Runs;

public sealed class AgentToolCatalog
{
    public const string SelectorToolName = "go.selectTool";
    private static readonly string[] SelectorRequiredProperties = ["name"];
    private static readonly string[] DefaultServerTools =
    [
        "web.search", "web.fetch", "youtube.search", "media.inspect", "media.analyze",
        "image.generate", "math.evaluate", "context.embed", "context.retrieve",
    ];
    private readonly Dictionary<string, AgentToolSpec> _tools = CreateTools();

    public IReadOnlyList<AgentToolSpec> GetAvailableTools(RunRequest request)
    {
        var requestedServerTools = request.AllowedServerTools ?? DefaultServerTools;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in requestedServerTools)
        {
            if (!_tools.TryGetValue(name, out var tool) || !tool.ServerSide)
            {
                throw new ArgumentException($"Unknown or unavailable server tool: {name}");
            }
            names.Add(name);
        }
        var capabilities = request.ClientCapabilities ?? [];
        if (HasCapability(capabilities, "documentIo"))
        {
            names.UnionWith([ClientToolNames.DocumentRead, ClientToolNames.DocumentCreate]);
        }
        if (HasCapability(capabilities, "documents"))
        {
            names.UnionWith([ClientToolNames.DocumentsList, ClientToolNames.DocumentsSearch, ClientToolNames.DocumentsReadPages]);
        }
        if (HasCapability(capabilities, "filesystem") || HasCapability(capabilities, "code"))
        {
            names.UnionWith(
            [
                ClientToolNames.FileSystemList,
                ClientToolNames.FileSystemStat,
                ClientToolNames.FileSystemFindFiles,
                ClientToolNames.FileSystemReadText,
                ClientToolNames.FileSystemSearch,
                ClientToolNames.FileSystemWriteText,
                ClientToolNames.FileSystemReplaceText,
                ClientToolNames.FileSystemMove,
                ClientToolNames.FileSystemProposePatch,
                ClientToolNames.FileSystemProposeCreate,
                ClientToolNames.FileSystemProposeDelete,
            ]);
        }
        if (HasCapability(capabilities, "process") || HasCapability(capabilities, "code"))
        {
            names.Add(ClientToolNames.ProcessRunPreset);
            names.Add(ClientToolNames.ProcessRun);
            names.Add(ClientToolNames.LeanProof);
        }
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
        var catalog = string.Join("\n", ordered.Select(static tool => $"- {tool.Name}"));
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
        switch (name)
        {
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
                if (documentScope is not ("session" or "workspace"))
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
                RequireString(value, "path", 0, 1024);
                break;
            case ClientToolNames.FileSystemFindFiles:
                OptionalString(value, "path", 0, 1024);
                RequireStringArray(value, "patterns", 1, 64, 256);
                OptionalInteger(value, "maximumResults", 1, 5000);
                break;
            case ClientToolNames.FileSystemReadText:
                RequireString(value, "path", 1, 1024);
                OptionalInteger(value, "startLine", 1, 10_000_000);
                OptionalInteger(value, "endLine", 1, 10_000_000);
                OptionalInteger(value, "maximumCharacters", 1_024, 12_000);
                OptionalString(value, "matchText", 1, 4_096);
                break;
            case ClientToolNames.FileSystemSearch:
                RequireString(value, "path", 0, 1024);
                var hasQuery = value.TryGetProperty("query", out _);
                var hasQueries = value.TryGetProperty("queries", out _);
                if (hasQuery == hasQueries)
                {
                    throw new ArgumentException("fs.search requires exactly one of query or queries.");
                }
                if (hasQuery) RequireString(value, "query", 1, 1024);
                if (hasQueries) RequireStringArray(value, "queries", 1, 64, 1024);
                OptionalEnum(value, "matchMode", ["literal", "regex"]);
                OptionalStringArray(value, "includeGlobs", 64, 256);
                OptionalStringArray(value, "excludeGlobs", 64, 256);
                OptionalInteger(value, "maximumResults", 1, 100);
                OptionalInteger(value, "contextLines", 0, 5);
                break;
            case ClientToolNames.FileSystemWriteText:
                RequireString(value, "path", 1, 1024);
                RequireString(value, "content", 0, 4 * 1024 * 1024);
                RequireString(value, "expectedContent", 0, 65_536);
                OptionalEnum(value, "expectedContentMode", ["complete", "fragment"]);
                break;
            case ClientToolNames.FileSystemReplaceText:
                RequireString(value, "path", 1, 1024);
                RequireString(value, "oldText", 1, 2 * 1024 * 1024);
                RequireString(value, "newText", 0, 2 * 1024 * 1024);
                RequireString(value, "expectedContent", 0, 65_536);
                OptionalEnum(value, "expectedContentMode", ["complete", "fragment"]);
                break;
            case ClientToolNames.FileSystemMove:
                RequireString(value, "source", 1, 1024);
                RequireString(value, "destination", 1, 1024);
                OptionalBoolean(value, "overwrite");
                break;
            case ClientToolNames.FileSystemProposePatch:
                RequireString(value, "path", 1, 1024);
                RequireString(value, "patch", 1, 4 * 1024 * 1024);
                RequireString(value, "expectedContent", 0, 65_536);
                OptionalEnum(value, "expectedContentMode", ["complete", "fragment"]);
                break;
            case ClientToolNames.FileSystemProposeCreate:
                RequireString(value, "path", 1, 1024);
                RequireString(value, "content", 0, 12_000);
                break;
            case ClientToolNames.FileSystemProposeDelete:
                RequireString(value, "path", 1, 1024);
                RequireString(value, "expectedContent", 0, 65_536);
                OptionalEnum(value, "expectedContentMode", ["complete", "fragment"]);
                break;
            case ClientToolNames.ProcessRunPreset:
                RequireString(value, "preset", 1, 64);
                OptionalString(value, "workspace", 1, 1024);
                break;
            case ClientToolNames.ProcessRun:
                var executable = RequireString(value, "executable", 1, 1024);
                if (IsLeanExecutable(executable))
                {
                    throw new ArgumentException("Lean und Lake dürfen ausschließlich über das typisierte Werkzeug proof.lean ausgeführt werden.");
                }
                RequireString(value, "purpose", 1, 16);
                OptionalStringArray(value, "arguments", 128, 8192, allowEmpty: true);
                OptionalString(value, "workingDirectory", 1, 1024);
                OptionalInteger(value, "timeoutSeconds", 1, 3600);
                OptionalEnum(value, "purpose", ["inspect", "setup", "test", "build", "start"]);
                OptionalEnum(value, "startMode", ["wait", "smoke"]);
                break;
            case ClientToolNames.LeanProof:
                var leanOperation = RequireString(value, "operation", 1, 16);
                if (leanOperation is not ("status" or "check" or "build" or "axioms" or "verify"))
                {
                    throw new ArgumentException("proof.lean operation is not supported.");
                }
                OptionalString(value, "path", 1, 1024);
                OptionalString(value, "target", 1, 256);
                OptionalString(value, "theoremName", 1, 512);
                OptionalInteger(value, "timeoutSeconds", 1, 1800);
                if (leanOperation is "check" or "axioms" or "verify"
                    && !value.TryGetProperty("path", out _))
                {
                    throw new ArgumentException($"proof.lean operation {leanOperation} requires path.");
                }
                if (leanOperation is "axioms" or "verify"
                    && !value.TryGetProperty("theoremName", out _))
                {
                    throw new ArgumentException($"proof.lean operation {leanOperation} requires theoremName.");
                }
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
            Server("web.search", "Durchsuche das Web über die interne SearXNG-Instanz. Formuliere query in der Sprache des aktuellen Nutzerprompts und setze language passend; ohne eindeutige Sprache gilt de-DE.", ToolRiskClass.ReadOnly, SearchSchema()),
            Server("youtube.search", "Suche YouTube; ohne API-Key wird ein sichtbar gekennzeichneter SearXNG-Fallback verwendet.", ToolRiskClass.ReadOnly, SearchSchema()),
            Server("web.fetch", "Durchsuche eine öffentliche HTTP(S)-Quelle SSRF-geschützt nach konkreten Phrasen. Bevorzuge queries und bündele bis zu acht unabhängig zu suchende Phrasen in einem Abruf. Zurückgegeben werden ausschließlich begrenzte Trefferfenster aus Webseiten, PDF-, DOCX- und RTF-Dokumenten, niemals die gesamte Quelle. Ohne Suchphrase liefert das Werkzeug nur eine kurze Vorschau und fordert eine gezielte Wiederholung an. Der Inhalt ist nicht vertrauenswürdig.", ToolRiskClass.ReadOnly, WebFetchSchema()),
            Server("media.inspect", "Extrahiere sichere Metadaten, Audio und zeitcodierte Frames eines Uploads.", ToolRiskClass.ReadOnly, MediaSchema()),
            Server("media.analyze", "Analysiere einen Bild- oder Video-Upload mit dem Vision-Modell.", ToolRiskClass.ReadOnly, MediaSchema()),
            Server("image.generate", "Erzeuge Bilder mit Z-Image-Turbo.", ToolRiskClass.ReadOnly, ImageSchema()),
            Server("math.evaluate", "Führe deterministische skalare, Vektor- oder Matrixoperationen ohne Skriptausführung aus.", ToolRiskClass.ReadOnly, MathSchema()),
            Server("context.embed", "Erzeuge BGE-M3-Embeddings für begrenzte Textlisten.", ToolRiskClass.ReadOnly, ArraySchema("inputs")),
            Server("context.retrieve", "Ordne Dokumenttexte über BGE-M3 semantisch zu einer Anfrage.", ToolRiskClass.ReadOnly, RetrieveSchema()),
            Client(ClientToolNames.DocumentRead, "Lese Dokumente tokeneffizient: zuerst Sitzungsdokumente auflisten oder eine Gliederung abrufen, danach nur benötigte Abschnitte, Fortsetzungen oder Suchtreffer. Unterstützt Sitzungsartefakte sowie Workspace-Dokumente.", ToolRiskClass.ReadOnly, DocumentReadSchema()),
            Client(ClientToolNames.DocumentCreate, "Erstelle oder bearbeite ein Dokument abschnittsweise über stabile sectionId-Werte. General AI erzeugt ein versioniertes Chat-Artefakt; Coding schreibt eine kanonische Workspace-Quelle. PDF wird deterministisch mit GO und KaTeX gerendert.", ToolRiskClass.LocalMutation, DocumentCreateSchema()),
            Client(ClientToolNames.DocumentsList, "Liste alle fertig aufbereiteten Dokumente der aktuellen GO-Sitzung mit Dateiname und Seitenzahl.", ToolRiskClass.ReadOnly, Parse("""{"type":"object","properties":{},"required":[],"additionalProperties":false}""")),
            Client(ClientToolNames.DocumentsSearch, "Durchsuche den persistenten lokalen Dokumentindex promptbezogen und liefere Originalbelege mit Dateiname und Seite.", ToolRiskClass.ReadOnly, Parse("""{"type":"object","properties":{"query":{"type":"string"},"maximumCharacters":{"type":"integer","minimum":1000,"maximum":200000}},"required":["query"],"additionalProperties":false}""")),
            Client(ClientToolNames.DocumentsReadPages, "Lese einen konkreten Seitenbereich eines Sitzungsdokuments als zitierfähigen Originalbeleg.", ToolRiskClass.ReadOnly, Parse("""{"type":"object","properties":{"documentId":{"type":"string"},"startPage":{"type":"integer","minimum":1},"endPage":{"type":"integer","minimum":1}},"required":["documentId","startPage","endPage"],"additionalProperties":false}""")),
            Client(ClientToolNames.FileSystemList, "Liste Einträge eines nachweislich vorhandenen Ordners im freigegebenen Client-Workspace. Verwende . oder einen leeren Pfad für die Workspace-Wurzel.", ToolRiskClass.ReadOnly, Schema("path", ("path", "string"))),
            Client(ClientToolNames.FileSystemStat, "Lese Metadaten eines nachweislich vorhandenen Workspace-Pfads. Verwende . oder einen leeren Pfad für die Workspace-Wurzel.", ToolRiskClass.ReadOnly, Schema("path", ("path", "string"))),
            Client(ClientToolNames.FileSystemFindFiles, "Finde aktuell vorhandene Dateien direkt im Workspace per Glob oder Dateiname.", ToolRiskClass.ReadOnly, FindFilesSchema()),
            Client(ClientToolNames.FileSystemReadText, "Lese höchstens 12.000 Zeichen aus einer vorhandenen Textdatei. Bei größeren Dateien musst du zuerst fs.search verwenden und anschließend genau den gelieferten Zeilenbereich oder einen eindeutigen matchText nachladen. Ein Aufruf nur mit path liefert für große Dateien keinen Quelltext.", ToolRiskClass.ReadOnly, ReadTextSchema()),
            Client(ClientToolNames.FileSystemSearch, "Suche vor dem Lesen großer Dateien gezielt nach Funktionsnamen, Symbolen oder Textphrasen. Treffer liefern Pfad und begrenzte Zeilenbereiche für fs.readText. found=false und state=not_present bedeutet verbindlich, dass der gesuchte Inhalt im durchsuchten Bestand noch nicht existiert; wiederhole dann nicht dieselbe Suche, sondern erstelle oder ergänze ihn am passenden Pfad.", ToolRiskClass.ReadOnly, FileSearchSchema()),
            Client(ClientToolNames.FileSystemWriteText, "Schreibe eine unmittelbar zuvor vollständig gelesene Bestandsdatei atomar.", ToolRiskClass.LocalMutation, WriteTextSchema()),
            Client(ClientToolNames.FileSystemReplaceText, "Ersetze einen exakt gelesenen Textblock atomar in einer vorhandenen Workspace-Datei. oldText muss mit allen Zeichen und Zeilenumbrüchen genau einmal vorkommen.", ToolRiskClass.LocalMutation, ReplaceTextSchema()),
            Client(ClientToolNames.FileSystemMove, "Verschiebe oder benenne eine Workspace-Datei nur um, wenn das Nutzerziel dies verlangt.", ToolRiskClass.LocalMutation, MoveSchema()),
            Client(ClientToolNames.FileSystemProposePatch, "Wende einen Patch nur auf eine unmittelbar zuvor vollständig gelesene Clientdatei an.", ToolRiskClass.LocalMutation, MutationSchema(["path", "patch"], ("path", "string"), ("patch", "string"))),
            Client(ClientToolNames.FileSystemProposeCreate, "Erstelle eine neue Workspace-Datei am ausdrücklich verlangten Pfad. Liefere eine kompakte, vollständige Arbeitsversion mit höchstens 12.000 Unicode-Zeichen; erweitere sie bei Bedarf in späteren Werkzeugschritten. Quellcode muss syntaktisch gültig sein.", ToolRiskClass.LocalMutation, CreateFileSchema()),
            Client(ClientToolNames.FileSystemProposeDelete, "Lösche nur eine unmittelbar zuvor vollständig gelesene Clientdatei.", ToolRiskClass.LocalMutation, MutationSchema(["path"], ("path", "string"))),
            Client(ClientToolNames.ProcessRunPreset, "Führe ein versioniertes Build-, Test-, Start- oder Git-Preset im freigegebenen Workspace aus. PDF-Artefakte werden von GO deterministisch nach einer Quellenänderung erzeugt.", ToolRiskClass.Process, ProcessSchema()),
            Client(ClientToolNames.ProcessRun, "Führe ein direktes Programm mit getrennter Argumentliste und Workspace-Arbeitsverzeichnis für Analyse, Setup, Test, Build oder Smoke-Start aus.", ToolRiskClass.Process, ProcessRunSchema()),
            Client(ClientToolNames.LeanProof, "Prüfe freiwillig einen mathematischen oder algorithmischen Beweis mit der gepinnten lokalen Lean-/Lake-Toolchain. Verwende niemals process.run für lean oder lake. check kompiliert eine Datei; verify kompiliert und prüft die Axiomabhängigkeiten des exakt deklarierten Theorems. Ein Dateiname erzeugt keinen Lean-Namespace.", ToolRiskClass.Process, LeanProofSchema()),
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
        {"type":"object","properties":{"query":{"type":"string","description":"Kurze Suchanfrage in der Sprache des aktuellen Nutzerprompts; technische Eigennamen unveraendert lassen."},"maximumResults":{"type":"integer","minimum":1,"maximum":20},"language":{"type":"string","description":"BCP-47-Suchsprache passend zum aktuellen Prompt; Standard de-DE."}},"required":["query"],"additionalProperties":false}
        """);

    private static JsonElement WebFetchSchema() => Parse("""
        {"type":"object","properties":{"url":{"type":"string","maxLength":2048},"query":{"type":"string","minLength":1,"maxLength":512,"description":"Eine konkrete Phrase oder ein prägnanter Fachbegriff. Keine Liste mehrerer Begriffe als ein gemeinsamer String."},"queries":{"type":"array","maxItems":8,"items":{"type":"string","minLength":1,"maxLength":512},"description":"Bevorzugt verwenden, wenn mehrere Themen gesucht werden: jede Phrase als eigener Arrayeintrag in demselben Abruf."},"maximumResults":{"type":"integer","minimum":1,"maximum":20,"default":8},"contextCharacters":{"type":"integer","minimum":100,"maximum":2000,"default":500},"maximumCharacters":{"type":"integer","minimum":1000,"maximum":12000,"default":8000}},"required":["url"],"additionalProperties":false}
        """);

    private static JsonElement DocumentReadSchema() => Parse("""
        {"type":"object","properties":{"scope":{"type":"string","enum":["session","workspace"]},"mode":{"type":"string","enum":["list","outline","read","search"]},"reference":{"type":"string","description":"GUID aus list oder relativer Workspace-Pfad."},"query":{"type":"string"},"startUnit":{"type":"integer","minimum":1},"characterOffset":{"type":"integer","minimum":0},"maximumUnits":{"type":"integer","minimum":1,"maximum":30},"maximumCharacters":{"type":"integer","minimum":1000,"maximum":40000}},"required":["scope","mode"],"additionalProperties":false}
        """);

    private static JsonElement DocumentCreateSchema() => Parse("""
        {"type":"object","properties":{"operation":{"type":"string","enum":["create","appendSection","replaceSection"]},"reference":{"type":"string","description":"Beim Erstellen Dateiname/Pfad, beim Bearbeiten documentId oder derselbe Workspace-Pfad."},"format":{"type":"string","enum":["markdown","text","docx","pdf"]},"sectionId":{"type":"string","description":"Stabile eindeutige Abschnitts-ID."},"heading":{"type":"string"},"content":{"type":"string","description":"Nur der neue oder geänderte Abschnitt als Markdown, nie das gesamte bestehende Dokument erneut."},"expectedSha256":{"type":"string","description":"Für Bearbeitungen verpflichtender SHA-256 aus document.read oder document.create."}},"required":["operation","reference","format","sectionId","content"],"additionalProperties":false}
        """);

    private static JsonElement FindFilesSchema() => Parse("""
        {"type":"object","properties":{"path":{"type":"string"},"patterns":{"type":"array","minItems":1,"maxItems":64,"items":{"type":"string"}},"maximumResults":{"type":"integer","minimum":1,"maximum":5000}},"required":["patterns"],"additionalProperties":false}
        """);

    private static JsonElement ReadTextSchema() => Parse("""
        {"type":"object","properties":{"path":{"type":"string"},"startLine":{"type":"integer","minimum":1,"description":"Erste gezielt zu lesende Zeile aus fs.search."},"endLine":{"type":"integer","minimum":1,"description":"Letzte gezielt zu lesende Zeile aus fs.search."},"maximumCharacters":{"type":"integer","minimum":1024,"maximum":12000,"default":8000},"matchText":{"type":"string","maxLength":4096,"description":"Eindeutige Funktion oder Textphrase, um nur deren unmittelbaren Quellblock zu laden."}},"required":["path"],"additionalProperties":false}
        """);

    private static JsonElement FileSearchSchema() => Parse("""
        {"type":"object","properties":{"path":{"type":"string"},"query":{"type":"string","description":"Ein konkreter Funktionsname, ein Symbol oder eine Textphrase; im literal-Modus wird der ältere Wert a|b kompatibel geteilt."},"queries":{"type":"array","minItems":1,"maxItems":64,"description":"Gebündelte konkrete Suchbegriffe. Jedes Element enthält genau ein Literal oder einen regulären Ausdruck.","items":{"type":"string"}},"matchMode":{"type":"string","enum":["literal","regex"]},"includeGlobs":{"type":"array","maxItems":64,"items":{"type":"string"}},"excludeGlobs":{"type":"array","maxItems":64,"items":{"type":"string"}},"maximumResults":{"type":"integer","minimum":1,"maximum":100,"default":20},"contextLines":{"type":"integer","minimum":0,"maximum":5,"default":2}},"required":["path"],"additionalProperties":false}
        """);

    private static JsonElement WriteTextSchema() => Parse("""
        {"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"},"expectedContent":{"type":"string"},"expectedContentMode":{"type":"string","enum":["complete","fragment"]}},"required":["path","content","expectedContent"],"additionalProperties":false}
        """);

    private static JsonElement ReplaceTextSchema() => Parse("""
        {"type":"object","properties":{"path":{"type":"string"},"oldText":{"type":"string"},"newText":{"type":"string"},"expectedContent":{"type":"string"},"expectedContentMode":{"type":"string","enum":["complete","fragment"]}},"required":["path","oldText","newText","expectedContent"],"additionalProperties":false}
        """);

    private static JsonElement MoveSchema() => Parse("""
        {"type":"object","properties":{"source":{"type":"string"},"destination":{"type":"string"},"overwrite":{"type":"boolean"}},"required":["source","destination"],"additionalProperties":false}
        """);

    private static JsonElement CreateFileSchema() => Parse("""
        {"type":"object","properties":{"path":{"type":"string","maxLength":1024},"content":{"type":"string","maxLength":12000,"description":"Kompakte, syntaktisch vollständige Arbeitsversion; höchstens 12.000 Unicode-Zeichen."}},"required":["path","content"],"additionalProperties":false}
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
        {"type":"object","properties":{"preset":{"type":"string","enum":["git.status","git.diff","dotnet.build","dotnet.test","repository.build","repository.verify","repository.start","code.run","code.test"]},"target":{"type":"string","description":"Optionaler relativer Datei-, Projekt- oder Solutionpfad."}},"required":["preset"],"additionalProperties":false}
        """);

    private static JsonElement ProcessRunSchema() => Parse("""
        {"type":"object","properties":{"executable":{"type":"string","description":"Ausschließlich der echte Programmname oder Programmpfad, zum Beispiel py, python, npm.cmd, cargo, go, dotnet oder git. Keine komplette Befehlszeile, keine Argumente, keine Shell und keine Umleitung."},"arguments":{"type":"array","maxItems":128,"items":{"type":"string"},"description":"Jedes Programmargument als eigener Arrayeintrag, zum Beispiel [\"-3.11\",\"-m\",\"pytest\"] oder [\"install\"]. Keine Shell-Verkettung oder Ausgabeumleitung."},"workingDirectory":{"type":"string","description":"Relatives Arbeitsverzeichnis innerhalb des freigegebenen Workspace."},"timeoutSeconds":{"type":"integer","minimum":1,"maximum":3600},"purpose":{"type":"string","enum":["inspect","setup","test","build","start"]},"startMode":{"type":"string","enum":["wait","smoke"]}},"required":["executable","purpose"],"additionalProperties":false}
        """);

    private static JsonElement LeanProofSchema() => Parse("""
        {"type":"object","properties":{"operation":{"type":"string","enum":["status","check","build","axioms","verify"]},"path":{"type":"string","description":"Relativer Pfad zu einer Lean-Datei oder einem Lake-Projekt im Workspace."},"target":{"type":"string","description":"Optionales Lake-Build-Target."},"theoremName":{"type":"string","description":"Vollständig qualifizierter Name des zu prüfenden Theorems."},"timeoutSeconds":{"type":"integer","minimum":1,"maximum":1800}},"required":["operation"],"additionalProperties":false}
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

    private static JsonElement MutationSchema(
        IReadOnlyList<string> required,
        params (string Name, string Type)[] properties)
    {
        var all = properties
            .Concat([("expectedContent", "string"), ("expectedContentMode", "string")])
            .ToArray();
        return Schema(required.Concat(["expectedContent"]).ToArray(), all);
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static bool HasCapability(IReadOnlyList<string> capabilities, string expected) =>
        capabilities.Any(value => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase));

    private static bool IsLeanExecutable(string executable)
    {
        var name = Path.GetFileNameWithoutExtension(executable.Trim());
        return name.Equals("lean", StringComparison.OrdinalIgnoreCase)
            || name.Equals("lake", StringComparison.OrdinalIgnoreCase);
    }

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

    private static void OptionalBoolean(JsonElement value, string name)
    {
        if (value.TryGetProperty(name, out var property)
            && property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new ArgumentException($"Property '{name}' must be a boolean.");
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
