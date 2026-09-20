using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GoAi.Contracts;
using GoWinUI.Core.Coding;

namespace GoWinUI.App.Services;

/// <summary>Versioned authoring projects and non-destructive Blender review using the installed runtime.</summary>
public sealed partial class BlenderToolService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Func<string?> _findExecutable;

    public BlenderToolService() : this(FindBlender) { }
    internal BlenderToolService(Func<string?> findExecutable) => _findExecutable = findExecutable;

    public async Task<object> ExecuteAsync(JsonElement args, string? workspace,
        Func<CodingCommandProgress, Task>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        WorkspaceTools.Validate(WorkspaceTools.Blender, args);
        var operation = args.GetProperty("operation").GetString()!;
        var executable = _findExecutable();
        if (operation == "info")
        {
            object? file = null;
            if (args.TryGetProperty("path", out var value))
            {
                var path = WorkspaceFilePath.Resolve(workspace ?? "", value.GetString()!);
                RequireExtension(path, ".blend", ".py");
                await using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                file = new { path = Relative(workspace!, path), bytes = input.Length,
                    sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token).ConfigureAwait(false)) };
            }
            var preview = executable is not null && !string.IsNullOrWhiteSpace(workspace)
                ? await EnsurePreviewAsync(executable, workspace, null, null, null, token).ConfigureAwait(false) : null;
            return new { available = executable is not null, executable, file, toolkitVersion = 2, preview,
                instruction = file is null
                    ? "Für ein neues Projekt scaffold verwenden, danach MODELING_GUIDE.md vollständig lesen und die Modellierungsmethode anwenden. Bestehende Projekte anhand design.json und aktueller Szene fortsetzen. Kleine stage-Skripte verwenden, jede Revision rendern und mit echter Vision prüfen; preview.state und neue Nutzerhinweise beachten."
                    : "file.sha256 bestätigt genau diesen Dateistand. Für stage/preview/inspect/render den passenden erwarteten Hash übernehmen. Bestehende Revisionen erhalten; Modellierungsmethode und MODELING_GUIDE.md weiter anwenden." };
        }
        if (operation == "scaffold")
        {
            var scaffold = JsonSerializer.SerializeToNode(await ScaffoldAsync(args, workspace ?? "", token).ConfigureAwait(false), JsonOptions)!.AsObject();
            if (executable is not null)
                scaffold["preview"] = JsonSerializer.SerializeToNode(await EnsurePreviewAsync(executable, workspace!, null, null, null, token).ConfigureAwait(false), JsonOptions);
            return scaffold;
        }
        if (executable is null) throw new FileNotFoundException("Blender wurde nicht gefunden. Installiere Blender oder setze GO_BLENDER_EXECUTABLE.");
        var fullPath = WorkspaceFilePath.Resolve(workspace ?? "", args.GetProperty("path").GetString()!);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Blender-Eingabedatei fehlt.", fullPath);
        RequireExtension(fullPath, operation is "run" or "stage" ? ".py" : ".blend");
        if (operation == "open")
        {
            await using var openedSource = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(openedSource, token).ConfigureAwait(false));
            var preview = await EnsurePreviewAsync(executable, workspace!, fullPath, hash, null, token).ConfigureAwait(false);
            var state = JsonSerializer.SerializeToElement(preview, JsonOptions);
            return new { success = state.GetProperty("success").GetBoolean(), opened = state.GetProperty("success").GetBoolean(),
                path = Relative(workspace!, fullPath), processId = state.GetProperty("processId"), preview };
        }

        // Keep a read lease throughout execution: on Windows another process cannot replace
        // the exact file whose hash the model confirmed between verification and Blender load.
        await using var source = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var sourceHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(source, token).ConfigureAwait(false));
        if (!sourceHash.Equals(args.GetProperty("expectedSha256").GetString(), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Blender-Eingabedatei wurde seit dem Lesen verändert; aktuellen SHA-256 erneut ermitteln.");
        if (operation == "stage")
            return await ExecuteStageAsync(executable, workspace!, fullPath, sourceHash, args, progress, token).ConfigureAwait(false);
        if (operation == "preview")
            return await EnsurePreviewAsync(executable, workspace!, fullPath, sourceHash,
                args.TryGetProperty("label", out var label) ? label.GetString() : null, token).ConfigureAwait(false);
        if (operation == "run")
        {
            _ = await EnsurePreviewAsync(executable, workspace!, null, null, null, token).ConfigureAwait(false);
            return await ExecuteProcessAsync(executable, workspace!, fullPath, null, args, progress, token).ConfigureAwait(false);
        }

        _ = await EnsurePreviewAsync(executable, workspace!, fullPath, sourceHash, null, token).ConfigureAwait(false);

        var output = Path.TrimEndingDirectorySeparator(WorkspaceFilePath.Resolve(workspace!, args.GetProperty("outputDirectory").GetString()!));
        CreateFreshDirectory(workspace!, output);
        var runtime = Path.Combine(output, ".runtime");
        Directory.CreateDirectory(runtime);
        await WriteResourceAsync("go_blender.py", Path.Combine(runtime, "go_blender.py"), token).ConfigureAwait(false);
        var script = Path.Combine(runtime, "go_blender_tool.py");
        await WriteResourceAsync("go_blender_tool.py", script, token).ConfigureAwait(false);
        var reportPath = Path.Combine(output, "report.json");
        var views = args.TryGetProperty("views", out var requested) ? requested.Deserialize<string[]>()! : ["perspective", "front", "right", "top"];
        var request = new { operation, blendPath = fullPath, outputDirectory = output, reportPath,
            views, resolution = args.TryGetProperty("resolution", out var resolution) ? resolution.GetInt32() : 768,
            samples = args.TryGetProperty("samples", out var samples) ? samples.GetInt32() : 32 };
        var requestPath = Path.Combine(runtime, "request.json");
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request, JsonOptions), token).ConfigureAwait(false);
        var execution = await ExecuteProcessAsync(executable, workspace!, script, requestPath, args, progress, token).ConfigureAwait(false);
        var processSucceeded = execution.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True;
        if (!File.Exists(reportPath))
            return new { success = false, operation, sourceSha256 = sourceHash, execution,
                error = "Blender-Prüfung wurde nicht vollständig abgeschlossen. Prozessausgabe beachten; frisches Ausgabeverzeichnis beim Wiederholen verwenden." };
        if (new FileInfo(reportPath).Length > 16 * 1024 * 1024)
            throw new InvalidDataException("Blender-Prüfbericht überschreitet 16 MiB.");
        var report = JsonNode.Parse(await File.ReadAllTextAsync(reportPath, token).ConfigureAwait(false))!.AsObject();
        report["sourceSha256"] = sourceHash;
        report["blendPath"] = Relative(workspace!, fullPath);
        if (report["images"] is JsonArray images)
        {
            foreach (var image in images.OfType<JsonObject>())
            {
                var imagePath = Path.GetFullPath(image["path"]!.GetValue<string>());
                var relative = Relative(workspace!, imagePath);
                _ = WorkspaceFilePath.Resolve(workspace!, relative);
                if (!imagePath.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(imagePath) || new FileInfo(imagePath).Length < 100)
                    throw new InvalidDataException("Blender lieferte kein gültiges Renderbild im neuen Ausgabeverzeichnis.");
                image["path"] = relative;
                await using var imageStream = File.OpenRead(imagePath);
                image["sha256"] = Convert.ToHexStringLower(await SHA256.HashDataAsync(imageStream, token).ConfigureAwait(false));
                image["bytes"] = imageStream.Length;
            }
        }
        var allViewsRendered = operation != "render" || report["images"] is JsonArray rendered && rendered.Count == views.Length;
        await File.WriteAllTextAsync(reportPath, report.ToJsonString(JsonOptions), token).ConfigureAwait(false);
        var totalObjects = report["objects"]?.AsArray().Count ?? 0;
        var totalIssues = report["issues"]?.AsArray().Count ?? 0;
        return new { success = processSucceeded && allViewsRendered, operation, sourceSha256 = sourceHash, reportPath = Relative(workspace!, reportPath),
            valid = report["valid"]?.GetValue<bool>() ?? false, counts = report["counts"], bounds = report["bounds"], units = report["units"],
            issues = report["issues"]?.AsArray().Take(32).ToArray(), issueCount = totalIssues,
            objects = report["objects"]?.AsArray().Take(64).ToArray(), truncatedObjects = totalObjects > 64,
            images = report["images"], render = report["render"], execution,
            error = !processSucceeded || !allViewsRendered ? "Prüfung unvollständig oder strukturelle Fehler erkannt; konkrete Befunde in issues und reportPath beachten." : null,
            instruction = "Geometrieprüfung ist keine visuelle Freigabe. reportPath enthält alle Objekte/Befunde. Renderbilder mit image.input und media.analyze auf Anforderungen, Proportionen, Verbindungen und Sichtbarkeit prüfen; konkrete Fehler gezielt in einer neuen Szenenrevision beheben." };
    }

    private static async Task<object> ScaffoldAsync(JsonElement args, string workspace, CancellationToken token)
    {
        var directory = Path.TrimEndingDirectorySeparator(WorkspaceFilePath.Resolve(workspace, args.GetProperty("path").GetString()!));
        CreateFreshDirectory(workspace, directory);
        var helper = Path.Combine(directory, "go_blender.py");
        await WriteResourceAsync("go_blender.py", helper, token).ConfigureAwait(false);
        var scene = Path.Combine(directory, "scene.py");
        await WriteResourceAsync("scene.py", scene, token).ConfigureAwait(false);
        var steps = Path.Combine(directory, "steps");
        Directory.CreateDirectory(steps);
        var firstStep = Path.Combine(steps, "01_blockout.py");
        await WriteResourceAsync("step.py", firstStep, token).ConfigureAwait(false);
        var modelingGuide = Path.Combine(directory, "MODELING_GUIDE.md");
        await WriteResourceAsync("MODELING_GUIDE.md", modelingGuide, token).ConfigureAwait(false);
        var brief = args.TryGetProperty("brief", out var text) ? text.GetString() : "";
        var manifest = new { schemaVersion = 2, brief, units = "meters", currentScene = (string?)null, currentStage = (string?)null,
            requirements = Array.Empty<string>(), decisions = Array.Empty<string>(),
            references = Array.Empty<object>(), acceptedFeatures = Array.Empty<string>(),
            stagePlan = Array.Empty<object>(), revisions = Array.Empty<object>(), stageHistory = Array.Empty<object>(), visualChecks = Array.Empty<object>() };
        var designPath = Path.Combine(directory, "design.json");
        await File.WriteAllTextAsync(designPath, JsonSerializer.Serialize(manifest, JsonOptions), token).ConfigureAwait(false);
        var readme = Path.Combine(directory, "README.md");
        await File.WriteAllTextAsync(readme, ProjectReadme, new UTF8Encoding(false), token).ConfigureAwait(false);
        return new { success = true, projectPath = Relative(workspace, directory),
            helperPath = Relative(workspace, helper), scriptPath = Relative(workspace, firstStep), legacyScriptPath = Relative(workspace, scene),
            designPath = Relative(workspace, designPath), readmePath = Relative(workspace, readme),
            modelingGuidePath = Relative(workspace, modelingGuide),
            instruction = "Zuerst MODELING_GUIDE.md vollständig lesen und die dortige Modellierungsmethode anwenden; danach README.md, steps/01_blockout.py und design.json. Die öffentliche Helper-API steht in Guide und README; bei unklaren Parametern die betreffende Funktion in go_blender.py gezielt lesen. Referenzanhänge wirklich auswerten. Mit kleinen Etappenskripten und stage arbeiten: zuerst Hauptformen, danach geprüfte Baugruppen, Details und gezielte Korrekturen. stage übernimmt Laden/Speichern/Strukturprüfung und automatische Blender-Vorschau. Das Skript enthält nur Änderungen, höchstens 12000 Zeichen. Vor jeder nächsten Etappe Render und echte Vision prüfen; Nutzer-Umlenkungen berücksichtigen. currentScene und stageHistory im Designbrief aktualisieren. scene.py ist nur ein Legacy-Beispiel für run, kein Einstieg für komplexe neue Modelle." };
    }

    private static async Task<JsonElement> ExecuteProcessAsync(string executable, string workspace, string script,
        string? requestPath, JsonElement args, Func<CodingCommandProgress, Task>? progress, CancellationToken token)
    {
        var arguments = new List<string> { "--background", "--factory-startup", "--disable-autoexec", "--python-exit-code", "1", "--python", script };
        if (requestPath is not null) { arguments.Add("--"); arguments.Add(requestPath); }
        var command = JsonSerializer.SerializeToElement(new { executable, arguments,
            timeoutSeconds = args.TryGetProperty("timeoutSeconds", out var duration) ? duration.GetInt32() : 300 });
        return await new LocalCodingToolExecutor(workspace, progress).ExecuteAsync("coding.command", command, token).ConfigureAwait(false);
    }

    private static void CreateFreshDirectory(string workspace, string target)
    {
        if (Directory.Exists(target) || File.Exists(target))
            throw new IOException("Das Ziel existiert bereits; für jede Revision/Prüfung einen neuen Ordner verwenden.");
        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        _ = WorkspaceFilePath.Resolve(workspace, Relative(workspace, target));
        var temporary = Path.Combine(parent, ".go-blender-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try { Directory.Move(temporary, target); }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: false); }
    }

    private static async Task WriteResourceAsync(string name, string target, CancellationToken token)
    {
        await using var input = typeof(BlenderToolService).Assembly.GetManifestResourceStream("GoWinUI.App.Assets.Blender." + name)
            ?? throw new InvalidDataException("Blender-Baukasten fehlt im Client-Build: " + name);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output, token).ConfigureAwait(false);
    }

    private static void RequireExtension(string path, params string[] extensions)
    {
        if (!extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Diese Blender-Operation benötigt " + string.Join(" oder ", extensions) + ".");
    }

    private static string Relative(string workspace, string path) => Path.GetRelativePath(workspace, path).Replace('\\', '/');

    public static string? FindBlender()
    {
        if (Environment.GetEnvironmentVariable("GO_BLENDER_EXECUTABLE") is { Length: > 0 } configured)
            return File.Exists(configured) ? Path.GetFullPath(configured) : null;
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Blender Foundation");
        return Directory.Exists(directory) ? Directory.EnumerateDirectories(directory).OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(p => Path.Combine(p, "blender.exe")).FirstOrDefault(File.Exists) : null;
    }

    private const string ProjectReadme = """
        # Blender project toolkit (version 2)

        Read MODELING_GUIDE.md first: it explains how to turn a simple request into a coherent,
        editable model, with dimension planning, blockout, components, visual criteria and repairs.
        Work in small, visible stages. Start with steps/01_blockout.py; author the geometry yourself.
        Use blender.execute stage with path, current expectedSha256, label and a NEW outputPath (.blend).
        For a follow-up also supply baseScene and its current baseSceneSha256. The stage wrapper
        loads the base, runs your small script, saves a new revision, inspects it, and updates Blender.
        Each stage script is at most 12000 characters and changes only its own stage: no clearing,
        scene loading, saving or rendering. Use new numbered scripts for components and corrections.
        Review every stage with actual renders and Vision before starting the next; check preview.state.
        The user can steer the next stage while following the visible Blender window.
        scene.py is a legacy standalone run example, not the workflow for new complex models.
        go_blender.py is a local bpy convenience library, not a limit on Blender features.
        Read its function definitions when a parameter is unclear. All sizes are full dimensions;
        rotations are radians, Z is up. Use named component collections and reusable functions.
        Keep each coding.write within its advertised content limit and split larger projects into
        component modules. Before replacing ANY existing file, including scene.py and design.json,
        use coding.read and pass its current sha256 as the write/edit expectedSha256.

        Core API:
        - clear_scene(); collection(name, parent=None)
        - material(name, color=(r,g,b,a), metallic=0.0, roughness=0.45)
        - box(name, size=(x,y,z), location=(0,0,0), bevel=0.04, rotation=(0,0,0), material=None, collection=None)
        - cylinder(name, radius=1, depth=2, location=(0,0,0), vertices=48, bevel=0.02, rotation=(0,0,0), material=None, collection=None)
        - sphere(name, radius=1, location=(0,0,0), scale=(1,1,1), material=None, collection=None)
        - beam(name, start, end, width=0.1, depth=None, material=None, collection=None)
        - tube_curve(name, points, radius=0.05, closed=False, material=None, collection=None)
        - mesh(name, vertices, faces, material=None, collection=None)
        - ground(size=20, z=0, material=None); setup_review_scene(view='perspective', resolution=768)
        - save_revision(path): legacy standalone scripts only; stage saves automatically.

        Keep design.json current: requirements include measurable constraints, decisions record
        creative choices, references record actual image/document findings, acceptedFeatures
        preserve what worked. stageHistory records label, script, base revision, new scene and checks;
        currentScene/currentStage point to the latest accepted step. revisions records scene path and changes;
        visualChecks records rendered image paths and actual Vision findings, including unresolved defects.

        Follow-up: read design.json and currentScene, then pass that scene as stage baseScene.
        Modify named components only; keep the source revision intact and choose a new outputPath.
        Run scripts from workspace cwd; __file__ locates this project directory.
        Use blender.execute info with path to obtain the binary scene sha256. inspect and render
        require that expectedSha256 and a NEW outputDirectory; render supports perspective, front,
        right, top, back, left. Defaults: four views, 768 px, 32 samples. Start low-cost, refine later.
        Geometry warnings are evidence to investigate, not automatic visual or manufacturing rejection.
        Review at least overview and another view with image.input plus media.analyze. Compare against
        attachment findings and acceptance criteria. Save any correction in a new revision and recheck.
        """;
}
