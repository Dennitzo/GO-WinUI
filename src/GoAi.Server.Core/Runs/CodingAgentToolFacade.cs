using GoAi.Contracts;
using GoAi.Server.Core.Models;
using System.Text.Json;

namespace GoAi.Server.Core.Runs;

/// <summary>
/// Stable, model-facing V2 tool surface. Each facade call resolves to exactly one
/// existing GO operation, so the client security boundary and granular UI events
/// remain authoritative without an additional model-driven selector turn.
/// </summary>
public static class CodingAgentToolFacade
{
    public const string WorkspaceInspect = "workspace.inspect";
    public const string WorkspaceChange = "workspace.change";
    public const string ExecutionRun = "execution.run";
    public const string ResearchQuery = "research.query";
    public const string ArtifactProcess = "artifact.process";
    public const string TaskFinish = "task.finish";

    private static readonly LmToolDefinition[] StableDefinitions =
    [
        Definition(WorkspaceInspect, "Untersuche den gebundenen Workspace. Nutze belegte Pfade, Dateiversionen und kleine Ausschnitte.", """
            {"type":"object","additionalProperties":false,"properties":{
              "operation":{"type":"string","enum":["map","list","stat","find","search","read","symbols","references"]},
              "path":{"type":"string"},"paths":{"type":"array","maxItems":8,"items":{"type":"object","additionalProperties":false,"properties":{"path":{"type":"string"},"startLine":{"type":"integer","minimum":1},"endLine":{"type":"integer","minimum":1}},"required":["path"]}},
              "query":{"type":"string"},"patterns":{"type":"array","maxItems":32,"items":{"type":"string"}},
              "startLine":{"type":"integer","minimum":1},"endLine":{"type":"integer","minimum":1},
              "maximumResults":{"type":"integer","minimum":1,"maximum":500},"maximumCharacters":{"type":"integer","minimum":1024,"maximum":65536},
              "maximumDepth":{"type":"integer","minimum":1,"maximum":16},"contextLines":{"type":"integer","minimum":0,"maximum":5},
              "includeGlobs":{"type":"array","maxItems":32,"items":{"type":"string"}},"excludeGlobs":{"type":"array","maxItems":32,"items":{"type":"string"}},
              "commentary":{"type":"string","minLength":1,"maxLength":600}
            },"required":["operation"]}
            """),
        Definition(WorkspaceChange, "Ändere genau eine Workspace-Datei transaktional. Wähle genau eine Operation und liefere nur deren Felder. Verwende bei bestehenden Dateien die gelesene SHA-256-Version.", """
            {"type":"object","additionalProperties":false,"properties":{
              "operation":{"type":"string","enum":["create","write","replace","patch","move","delete"],"description":"create: neue Datei mit content; write: vollständiger Inhalt einer bestehenden Datei; replace: exakt gelesenen oldText durch newText ersetzen; patch: Unified-Diff anwenden; move: verschieben; delete: löschen."},
              "path":{"type":"string","description":"Relativer Workspace-Pfad."},"destination":{"type":"string","description":"Nur für move."},
              "content":{"type":"string","description":"Vollständiger Dateiinhalt für create oder write."},
              "oldText":{"type":"string","description":"Nur für replace: nicht leerer, exakt gelesener Textblock."},"newText":{"type":"string","description":"Nur für replace: Ersatztext, darf leer sein."},
              "patch":{"type":"string","description":"Nur für patch: vollständiger Unified-Diff."},
              "expectedSha256":{"type":"string","pattern":"^[0-9a-f]{64}$","description":"Für jede bestehende Quelldatei erforderlich."},"replaceAll":{"type":"boolean"},"overwrite":{"type":"boolean"},
              "commentary":{"type":"string","minLength":1,"maxLength":600}
            },"required":["operation","path"]}
            """),
        Definition(ExecutionRun, "Führe genau eine typisierte Prüfung, einen Prozess oder eine Lean-Validierung im Workspace aus. Lean ist nur für eine im aktuellen Lauf erstellte oder geänderte konkrete Beweisdatei zulässig; verify ist der einzige vollständige Beweisbeleg.", """
            {"type":"object","additionalProperties":false,"properties":{
              "operation":{"type":"string","enum":["preset","command","lean"]},
              "preset":{"type":"string"},"target":{"type":"string","description":"Relativer Workspace-Pfad. Für Lean check, axioms und verify muss dies eine konkrete .lean-Datei sein, die im aktuellen Lauf erstellt oder geändert wurde; für Lean build darf es ein Lake-Projektordner mit einer im aktuellen Lauf geänderten .lean-Datei sein."},"executable":{"type":"string"},
              "arguments":{"type":"array","maxItems":128,"items":{"type":"string"}},"workingDirectory":{"type":"string"},
              "timeoutSeconds":{"type":"integer","minimum":1,"maximum":3600},"purpose":{"type":"string","enum":["inspect","setup","test","build","start"]},
              "startMode":{"type":"string","enum":["wait","smoke"]},"leanOperation":{"type":"string","enum":["check","build","axioms","verify"]},
              "theoremName":{"type":"string"},"commentary":{"type":"string","minLength":1,"maxLength":600}
            },"required":["operation"]}
            """),
        Definition(ResearchQuery, "Bearbeite genau einen seriellen Recherchepfad. Eine Websuche wird mit genau einem gültigen Treffer per webFetch fortgesetzt; danach wird dieser Beleg umgesetzt und geprüft. Eine neue Websuche ist erst zulässig, wenn der bisherige Beleg mit contextGap konkret als unzureichend abgeschlossen wurde. HTTP(S)-Adressen werden ausschließlich mit webFetch gelesen; documentRead akzeptiert nur lokale Workspace-Pfade oder Sitzungs-GUIDs. Verwende die Sprache des Nutzerprompts und wiederhole keine erfolgreiche Suche oder Quelle.", """
            {"type":"object","additionalProperties":false,"properties":{
              "operation":{"type":"string","enum":["webSearch","webFetch","youtubeSearch","documentList","documentSearch","documentRead"]},
              "query":{"type":"string"},"url":{"type":"string"},"language":{"type":"string"},"maximumResults":{"type":"integer","minimum":1,"maximum":20},
              "reference":{"type":"string","description":"Nur für lokale Dokumente: relativer Workspace-Pfad oder von documentList gelieferte Sitzungs-GUID; niemals eine HTTP(S)-URL."},"scope":{"type":"string","enum":["session","workspace"]},"startUnit":{"type":"integer","minimum":1},
              "maximumUnits":{"type":"integer","minimum":1,"maximum":30},"maximumCharacters":{"type":"integer","minimum":1000,"maximum":40000},
              "contextGap":{"type":"string","minLength":12,"maxLength":800,"description":"Nur für eine neue webSearch nach einer bereits gelesenen Quelle: konkret fehlende Information und ihr Verwendungszweck. Ohne diese begründete Lücke wird der aktive Recherchepfad zuerst umgesetzt und geprüft."},
              "commentary":{"type":"string","minLength":1,"maxLength":600}
            },"required":["operation"]}
            """),
        Definition(ArtifactProcess, "Lies, erstelle oder analysiere genau ein lokales Dokument, Medium, Bild oder mathematisches Artefakt über GO. HTTP(S)-Quellen müssen mit research.query/webFetch gelesen werden.", """
            {"type":"object","additionalProperties":false,"properties":{
              "operation":{"type":"string","enum":["documentRead","documentCreate","imageGenerate","mediaInspect","mediaAnalyze","mathEvaluate"]},
              "reference":{"type":"string","description":"Lokaler Workspace-Pfad, Dokument-ID oder Sitzungs-GUID; niemals eine HTTP(S)-URL."},"scope":{"type":"string","enum":["session","workspace"]},"mode":{"type":"string"},"format":{"type":"string"},
              "sectionId":{"type":"string"},"heading":{"type":"string"},"content":{"type":"string"},"expectedSha256":{"type":"string"},
              "prompt":{"type":"string"},"uploadId":{"type":"string"},"width":{"type":"integer"},"height":{"type":"integer"},"seed":{"type":"integer"},"count":{"type":"integer"},
              "mathOperation":{"type":"string"},"left":{"type":"array","items":{"type":"number"}},"right":{"type":"array","items":{"type":"number"}},"unit":{"type":"string"},
              "commentary":{"type":"string","minLength":1,"maxLength":600}
            },"required":["operation"]}
            """),
        Definition(TaskFinish, "Beende den Auftrag ausschließlich als verifiziert abgeschlossen oder mit einem konkret belegten Blocker.", """
            {"type":"object","additionalProperties":false,"properties":{
              "status":{"type":"string","enum":["completed","blocked"]},"summary":{"type":"string","minLength":1,"maxLength":12000},
              "evidenceIds":{"type":"array","maxItems":64,"items":{"type":"string"}},"changedPaths":{"type":"array","maxItems":128,"items":{"type":"string"}},
              "verificationIds":{"type":"array","maxItems":64,"items":{"type":"string"}},"blocker":{"type":"string","maxLength":4000},
              "commentary":{"type":"string","minLength":1,"maxLength":600}
            },"required":["status","summary"]}
            """),
    ];

    public static IReadOnlyList<LmToolDefinition> Definitions => StableDefinitions;

    public static CodingToolDispatch Resolve(
        LmToolCall call,
        IReadOnlyList<AgentToolSpec> availableTools)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(availableTools);
        if (call.Arguments.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException($"{call.Name} requires an object argument.");
        }

        return call.Name switch
        {
            WorkspaceInspect => ResolveInspection(call, availableTools),
            WorkspaceChange => ResolveChange(call, availableTools),
            ExecutionRun => ResolveExecution(call, availableTools),
            ResearchQuery => ResolveResearch(call, availableTools),
            ArtifactProcess => ResolveArtifact(call, availableTools),
            TaskFinish => ResolveFinish(call),
            _ => throw new ArgumentException($"Unknown Coding Agent V2 tool '{call.Name}'."),
        };
    }

    private static CodingToolDispatch ResolveInspection(LmToolCall call, IReadOnlyList<AgentToolSpec> available)
    {
        var operation = RequiredString(call.Arguments, "operation");
        return operation switch
        {
            "map" => Client(call, operation, ClientToolNames.WorkspaceMap, available, new
            {
                maximumDepth = OptionalInt(call.Arguments, "maximumDepth"),
                maximumEntries = OptionalInt(call.Arguments, "maximumResults"),
            }),
            "list" => Client(call, operation, ClientToolNames.FileSystemList, available, new { path = WorkspacePath(call.Arguments, "path", defaultToRoot: true) }),
            "stat" => Client(call, operation, ClientToolNames.FileSystemStat, available, new { path = WorkspacePath(call.Arguments, "path", defaultToRoot: true) }),
            "find" => Client(call, operation, ClientToolNames.FileSystemFindFiles, available, new
            {
                path = WorkspacePath(call.Arguments, "path", defaultToRoot: true),
                patterns = RequiredStringArray(call.Arguments, "patterns", 1, 32),
                maximumResults = OptionalInt(call.Arguments, "maximumResults"),
            }),
            "search" => Client(call, operation, ClientToolNames.FileSystemSearch, available, new
            {
                path = WorkspacePath(call.Arguments, "path", defaultToRoot: true),
                query = RequiredString(call.Arguments, "query"),
                matchMode = "literal",
                includeGlobs = OptionalStringArray(call.Arguments, "includeGlobs"),
                excludeGlobs = OptionalStringArray(call.Arguments, "excludeGlobs"),
                maximumResults = OptionalInt(call.Arguments, "maximumResults"),
                contextLines = OptionalInt(call.Arguments, "contextLines"),
            }),
            "read" => ResolveRead(call, available),
            "symbols" or "references" => Client(call, operation, ClientToolNames.WorkspaceIndexQuery, available, new
            {
                operation,
                path = OptionalString(call.Arguments, "path"),
                query = RequiredString(call.Arguments, "query"),
                maximumResults = OptionalInt(call.Arguments, "maximumResults"),
            }),
            _ => throw new ArgumentException($"workspace.inspect operation '{operation}' is unsupported."),
        };
    }

    private static CodingToolDispatch ResolveRead(LmToolCall call, IReadOnlyList<AgentToolSpec> available)
    {
        if (call.Arguments.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Array)
        {
            if (paths.GetArrayLength() is < 1 or > 8)
            {
                throw new ArgumentException("workspace.inspect read accepts one to eight paths.");
            }
            return Client(call, "read", ClientToolNames.FileSystemReadMany, available, new
            {
                items = paths.Clone(),
                maximumCharacters = Math.Min(OptionalInt(call.Arguments, "maximumCharacters") ?? 65_536, 65_536),
            });
        }

        return Client(call, "read", ClientToolNames.FileSystemReadText, available, new
        {
            path = WorkspacePath(call.Arguments, "path"),
            startLine = OptionalInt(call.Arguments, "startLine"),
            endLine = OptionalInt(call.Arguments, "endLine"),
        });
    }

    private static CodingToolDispatch ResolveChange(LmToolCall call, IReadOnlyList<AgentToolSpec> available)
    {
        var operation = RequiredString(call.Arguments, "operation");
        var path = RequiredString(call.Arguments, "path");
        var content = StringProperty(call.Arguments, "content");
        var oldText = StringProperty(call.Arguments, "oldText");
        var newText = StringProperty(call.Arguments, "newText");
        var patch = StringProperty(call.Arguments, "patch");
        return operation switch
        {
            "create" => Client(call, "create", ClientToolNames.FileSystemProposeCreate, available, new { path, content = content ?? newText ?? throw MissingChangeProperty("create", "content") }),
            "write" => Client(call, "write", ClientToolNames.FileSystemWriteText, available, new { path, content = content ?? (oldText is null ? newText : null) ?? throw MissingChangeProperty("write", "content"), expectedSha256 = RequiredString(call.Arguments, "expectedSha256") }),
            "replace" when oldText is not null => Client(call, "replace", ClientToolNames.FileSystemReplaceText, available, new { path, oldText = RequireNonEmpty(oldText, "oldText"), newText = newText ?? throw MissingChangeProperty("replace", "newText"), expectedSha256 = RequiredString(call.Arguments, "expectedSha256"), replaceAll = OptionalBool(call.Arguments, "replaceAll") }),
            "replace" when !string.IsNullOrWhiteSpace(patch) => Client(call, "patch", ClientToolNames.FileSystemProposePatch, available, new { path, patch, expectedSha256 = RequiredString(call.Arguments, "expectedSha256") }),
            "replace" when content is not null || newText is not null => Client(call, "write", ClientToolNames.FileSystemWriteText, available, new { path, content = content ?? newText!, expectedSha256 = RequiredString(call.Arguments, "expectedSha256") }),
            "replace" => throw MissingChangeProperty("replace", "oldText and newText, or full content"),
            "patch" when !string.IsNullOrWhiteSpace(patch) => Client(call, "patch", ClientToolNames.FileSystemProposePatch, available, new { path, patch, expectedSha256 = RequiredString(call.Arguments, "expectedSha256") }),
            "patch" when content is not null || newText is not null => Client(call, "write", ClientToolNames.FileSystemWriteText, available, new { path, content = content ?? newText!, expectedSha256 = RequiredString(call.Arguments, "expectedSha256") }),
            "patch" => throw MissingChangeProperty("patch", "patch"),
            "move" => Client(call, operation, ClientToolNames.FileSystemMove, available, new { source = path, destination = RequiredString(call.Arguments, "destination"), expectedSha256 = RequiredString(call.Arguments, "expectedSha256"), overwrite = OptionalBool(call.Arguments, "overwrite") }),
            "delete" => Client(call, operation, ClientToolNames.FileSystemProposeDelete, available, new { path, expectedSha256 = RequiredString(call.Arguments, "expectedSha256") }),
            _ => throw new ArgumentException($"workspace.change operation '{operation}' is unsupported."),
        };
    }

    private static ArgumentException MissingChangeProperty(string operation, string property) =>
        new($"workspace.change/{operation} requires {property}.");

    private static string RequireNonEmpty(string value, string property) =>
        value.Length > 0 ? value : throw new ArgumentException($"Property '{property}' must not be empty.");

    private static CodingToolDispatch ResolveExecution(LmToolCall call, IReadOnlyList<AgentToolSpec> available)
    {
        var operation = RequiredString(call.Arguments, "operation");
        return operation switch
        {
            "preset" => Client(call, operation, ClientToolNames.ProcessRunPreset, available, new { preset = RequiredString(call.Arguments, "preset"), target = OptionalString(call.Arguments, "target") }),
            "command" => Client(call, operation, ClientToolNames.ProcessRun, available, new
            {
                executable = RequiredString(call.Arguments, "executable"),
                arguments = OptionalStringArray(call.Arguments, "arguments") ?? [],
                workingDirectory = OptionalString(call.Arguments, "workingDirectory"),
                timeoutSeconds = OptionalInt(call.Arguments, "timeoutSeconds"),
                purpose = RequiredString(call.Arguments, "purpose"),
                startMode = OptionalString(call.Arguments, "startMode"),
            }),
            "lean" => ResolveLean(call, operation, available),
            _ => throw new ArgumentException($"execution.run operation '{operation}' is unsupported."),
        };
    }

    private static CodingToolDispatch ResolveLean(
        LmToolCall call,
        string operation,
        IReadOnlyList<AgentToolSpec> available)
    {
        var leanOperation = RequiredString(call.Arguments, "leanOperation");
        if (string.Equals(leanOperation, "status", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "proof.lean status ist im Coding-Agenten nicht zulässig. Erstelle oder ändere zuerst eine konkrete Lean-Datei im Workspace und verwende danach check, axioms oder verify.");
        }

        return Client(call, operation, ClientToolNames.LeanProof, available, new
        {
            operation = leanOperation,
            path = RequiredString(call.Arguments, "target"),
            theoremName = OptionalString(call.Arguments, "theoremName"),
            timeoutSeconds = OptionalInt(call.Arguments, "timeoutSeconds"),
        });
    }

    private static CodingToolDispatch ResolveResearch(LmToolCall call, IReadOnlyList<AgentToolSpec> available)
    {
        var operation = RequiredString(call.Arguments, "operation");
        return operation switch
        {
            "webSearch" => Server(call, operation, "web.search", available, new { query = RequiredString(call.Arguments, "query"), maximumResults = OptionalInt(call.Arguments, "maximumResults"), language = OptionalString(call.Arguments, "language") }),
            "webFetch" => Server(call, operation, "web.fetch", available, new { url = RequiredString(call.Arguments, "url") }),
            "youtubeSearch" => Server(call, operation, "youtube.search", available, new { query = RequiredString(call.Arguments, "query"), maximumResults = OptionalInt(call.Arguments, "maximumResults"), language = OptionalString(call.Arguments, "language") }),
            "documentList" => Client(call, operation, ClientToolNames.DocumentRead, available, new { scope = OptionalString(call.Arguments, "scope") ?? "workspace", mode = "list" }),
            "documentSearch" => Client(call, operation, ClientToolNames.DocumentRead, available, new { scope = OptionalString(call.Arguments, "scope") ?? "workspace", mode = "search", reference = OptionalString(call.Arguments, "reference"), query = RequiredString(call.Arguments, "query"), maximumCharacters = OptionalInt(call.Arguments, "maximumCharacters") }),
            "documentRead" => Client(call, operation, ClientToolNames.DocumentRead, available, new { scope = OptionalString(call.Arguments, "scope") ?? "workspace", mode = "read", reference = RequiredString(call.Arguments, "reference"), startUnit = OptionalInt(call.Arguments, "startUnit"), maximumUnits = OptionalInt(call.Arguments, "maximumUnits"), maximumCharacters = OptionalInt(call.Arguments, "maximumCharacters") }),
            _ => throw new ArgumentException($"research.query operation '{operation}' is unsupported."),
        };
    }

    private static CodingToolDispatch ResolveArtifact(LmToolCall call, IReadOnlyList<AgentToolSpec> available)
    {
        var operation = RequiredString(call.Arguments, "operation");
        return operation switch
        {
            "documentRead" => Client(call, operation, ClientToolNames.DocumentRead, available, new { scope = OptionalString(call.Arguments, "scope") ?? "workspace", mode = OptionalString(call.Arguments, "mode") ?? "read", reference = RequiredString(call.Arguments, "reference") }),
            "documentCreate" => Client(call, operation, ClientToolNames.DocumentCreate, available, new { operation = OptionalString(call.Arguments, "mode") ?? "create", reference = RequiredString(call.Arguments, "reference"), format = RequiredString(call.Arguments, "format"), sectionId = RequiredString(call.Arguments, "sectionId"), heading = OptionalString(call.Arguments, "heading"), content = RequiredString(call.Arguments, "content"), expectedSha256 = OptionalString(call.Arguments, "expectedSha256") }),
            "imageGenerate" => Server(call, operation, "image.generate", available, new { prompt = RequiredString(call.Arguments, "prompt"), width = OptionalInt(call.Arguments, "width"), height = OptionalInt(call.Arguments, "height"), seed = OptionalInt(call.Arguments, "seed"), count = OptionalInt(call.Arguments, "count") }),
            "mediaInspect" => Server(call, operation, "media.inspect", available, new { uploadId = RequiredString(call.Arguments, "uploadId"), prompt = OptionalString(call.Arguments, "prompt") }),
            "mediaAnalyze" => Server(call, operation, "media.analyze", available, new { uploadId = RequiredString(call.Arguments, "uploadId"), prompt = OptionalString(call.Arguments, "prompt") }),
            "mathEvaluate" => Server(call, operation, "math.evaluate", available, new { operation = RequiredString(call.Arguments, "mathOperation"), left = RequiredNumberArray(call.Arguments, "left"), right = OptionalNumberArray(call.Arguments, "right"), unit = OptionalString(call.Arguments, "unit") }),
            _ => throw new ArgumentException($"artifact.process operation '{operation}' is unsupported."),
        };
    }

    private static CodingToolDispatch ResolveFinish(LmToolCall call)
    {
        var status = RequiredString(call.Arguments, "status");
        if (status is not ("completed" or "blocked"))
        {
            throw new ArgumentException("task.finish status must be completed or blocked.");
        }
        return new CodingToolDispatch(
            call.Name,
            "finish",
            null,
            WithoutCommentary(call.Arguments),
            null,
            true,
            OptionalString(call.Arguments, "commentary"));
    }

    private static CodingToolDispatch Client(LmToolCall call, string operation, string name, IReadOnlyList<AgentToolSpec> available, object arguments) =>
        Dispatch(call, operation, name, available, arguments, serverSide: false);

    private static CodingToolDispatch Server(LmToolCall call, string operation, string name, IReadOnlyList<AgentToolSpec> available, object arguments) =>
        Dispatch(call, operation, name, available, arguments, serverSide: true);

    private static CodingToolDispatch Dispatch(LmToolCall call, string operation, string name, IReadOnlyList<AgentToolSpec> available, object arguments, bool serverSide)
    {
        var spec = available.FirstOrDefault(tool => string.Equals(tool.Name, name, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"The required GO operation '{name}' is not available in this run.");
        if (spec.ServerSide != serverSide)
        {
            throw new InvalidOperationException($"The GO operation '{name}' has an inconsistent execution boundary.");
        }
        return new CodingToolDispatch(
            call.Name,
            operation,
            name,
            Clean(arguments),
            spec,
            false,
            OptionalString(call.Arguments, "commentary"));
    }

    public static JsonElement WithoutCommentary(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("commentary", out _))
        {
            return value.Clone();
        }

        var values = value.EnumerateObject()
            .Where(static property => !property.NameEquals("commentary"))
            .ToDictionary(
                static property => property.Name,
                static property => property.Value.Clone(),
                StringComparer.Ordinal);
        return JsonSerializer.SerializeToElement(values, GoAiProtocol.CreateJsonOptions());
    }

    private static JsonElement Clean(object value)
    {
        var element = JsonSerializer.SerializeToElement(value, GoAiProtocol.CreateJsonOptions());
        var values = element.EnumerateObject()
            .Where(static property => property.Value.ValueKind is not JsonValueKind.Null)
            .ToDictionary(static property => property.Name, static property => property.Value.Clone(), StringComparer.Ordinal);
        return JsonSerializer.SerializeToElement(values, GoAiProtocol.CreateJsonOptions());
    }

    private static LmToolDefinition Definition(string name, string description, string schema)
    {
        using var document = JsonDocument.Parse(schema);
        return new LmToolDefinition(name, description, document.RootElement.Clone());
    }

    private static string RequiredString(JsonElement value, string name) =>
        OptionalString(value, name) is { Length: > 0 } result
            ? result
        : throw new ArgumentException($"Property '{name}' is required.");

    internal static string WorkspacePath(JsonElement value, string name, bool defaultToRoot = false)
    {
        var path = OptionalString(value, name);
        if (string.IsNullOrWhiteSpace(path))
        {
            return defaultToRoot
                ? "."
                : throw new ArgumentException($"Property '{name}' is required.");
        }

        var normalized = path.Trim().Replace('\\', '/');
        if (normalized is "/" or "." or "./"
            || normalized.Equals("workspace", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("/workspace", StringComparison.OrdinalIgnoreCase))
        {
            return ".";
        }
        if (normalized.StartsWith("/workspace/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["/workspace/".Length..];
        }
        else if (normalized.StartsWith("workspace/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["workspace/".Length..];
        }
        else if (normalized.StartsWith('/') && !Path.IsPathFullyQualified(normalized))
        {
            normalized = normalized.TrimStart('/');
        }
        return string.IsNullOrWhiteSpace(normalized) ? "." : normalized;
    }

    private static string? OptionalString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? StringProperty(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? OptionalInt(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.TryGetInt32(out var number) ? number : null;

    private static bool? OptionalBool(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : null;

    private static string[] RequiredStringArray(JsonElement value, string name, int minimum, int maximum)
    {
        var values = OptionalStringArray(value, name) ?? [];
        return values.Length >= minimum && values.Length <= maximum
            ? values
            : throw new ArgumentException($"Property '{name}' requires {minimum} to {maximum} strings.");
    }

    private static string[]? OptionalStringArray(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Array
            ? property.EnumerateArray().Where(static item => item.ValueKind == JsonValueKind.String).Select(static item => item.GetString()!).ToArray()
            : null;

    private static double[] RequiredNumberArray(JsonElement value, string name) =>
        OptionalNumberArray(value, name) is { Length: > 0 } result
            ? result
            : throw new ArgumentException($"Property '{name}' requires numbers.");

    private static double[]? OptionalNumberArray(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Array
            ? property.EnumerateArray().Where(static item => item.ValueKind == JsonValueKind.Number).Select(static item => item.GetDouble()).ToArray()
            : null;
}

public sealed record CodingToolDispatch(
    string FacadeTool,
    string Operation,
    string? UnderlyingTool,
    JsonElement Arguments,
    AgentToolSpec? Spec,
    bool IsFinish,
    string? Commentary);
