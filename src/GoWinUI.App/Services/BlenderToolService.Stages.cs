using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GoAi.Contracts;
using GoWinUI.Core.Coding;

namespace GoWinUI.App.Services;

public sealed partial class BlenderToolService
{
    private static async Task<object> ExecuteStageAsync(string executable, string workspace, string scriptPath,
        string scriptHash, JsonElement args, Func<CodingCommandProgress, Task>? progress, CancellationToken token)
    {
        if (new FileInfo(scriptPath).Length > WorkspaceTools.MaximumBlenderStageScriptCharacters * 4L + 4
            || (await File.ReadAllTextAsync(scriptPath, token).ConfigureAwait(false)).Length > WorkspaceTools.MaximumBlenderStageScriptCharacters)
            throw new InvalidDataException($"Ein Blender-Etappenskript darf höchstens {WorkspaceTools.MaximumBlenderStageScriptCharacters} Zeichen enthalten. Zerlege die Arbeit in kleinere Schritte und eigene Bauteilmodule.");
        var outputPath = WorkspaceFilePath.Resolve(workspace, args.GetProperty("outputPath").GetString()!);
        RequireExtension(outputPath, ".blend");
        if (File.Exists(outputPath) || Directory.Exists(outputPath))
            throw new IOException("Die Etappenszene existiert bereits. Wähle eine neue Szenenrevision; vorhandene Arbeit bleibt erhalten.");
        var basePath = args.TryGetProperty("baseScene", out var baseValue)
            ? WorkspaceFilePath.Resolve(workspace, baseValue.GetString()!) : null;
        if (basePath is not null) RequireExtension(basePath, ".blend");
        await using var baseLease = basePath is null ? null : await OpenVerifiedSourceAsync(basePath,
            args.GetProperty("baseSceneSha256").GetString()!, token).ConfigureAwait(false);
        var label = args.GetProperty("label").GetString()!;
        var stageId = Guid.NewGuid().ToString("N");
        var stageDirectory = WorkspaceFilePath.Resolve(workspace, ".go-blender-stages/" + stageId);
        CreateFreshDirectory(workspace, stageDirectory);
        foreach (var resource in new[] { "go_blender.py", "go_blender_tool.py", "go_blender_stage.py" })
            await WriteResourceAsync(resource, Path.Combine(stageDirectory, resource), token).ConfigureAwait(false);
        var outputParent = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(outputParent);
        _ = WorkspaceFilePath.Resolve(workspace, Relative(workspace, outputPath));
        // Keep temporary and final files beside each other: moving a saved .blend
        // to another directory would invalidate relative texture/library paths.
        var temporaryScene = Path.Combine(outputParent, ".go-stage-" + stageId + ".blend");
        var reportPath = Path.Combine(stageDirectory, "report.json");
        var requestPath = Path.Combine(stageDirectory, "request.json");
        // Optional review renders of the new revision, stored next to it as
        // <revision>_renders/<view>.png so every stage produces auditable images.
        var views = args.TryGetProperty("views", out var requestedViews) ? requestedViews.Deserialize<string[]>() : null;
        var renderDirectory = views is null ? null
            : Path.Combine(outputParent, Path.GetFileNameWithoutExtension(outputPath) + "_renders");
        if (renderDirectory is not null && (Directory.Exists(renderDirectory) || File.Exists(renderDirectory)))
            throw new IOException("Der Renderordner der neuen Revision existiert bereits; wähle einen neuen outputPath.");
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new
        {
            scriptPath, baseScene = basePath, outputPath = temporaryScene, reportPath, label,
            projectDirectory = Path.GetDirectoryName(scriptPath),
            views, renderDirectory,
            resolution = args.TryGetProperty("resolution", out var resolution) ? resolution.GetInt32() : 768,
            samples = args.TryGetProperty("samples", out var samples) ? samples.GetInt32() : 32,
        }, JsonOptions), token).ConfigureAwait(false);
        var execution = await ExecuteProcessAsync(executable, workspace,
            Path.Combine(stageDirectory, "go_blender_stage.py"), requestPath, args, progress, token).ConfigureAwait(false);
        var processSucceeded = execution.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True;
        if (!processSucceeded || !File.Exists(temporaryScene) || !File.Exists(reportPath))
            return new { success = false, operation = "stage", label, scriptPath = Relative(workspace, scriptPath),
                baseScene = basePath is null ? null : Relative(workspace, basePath), execution,
                reportPath = File.Exists(reportPath) ? Relative(workspace, reportPath) : null,
                error = "Etappe nicht abgeschlossen. Vorherige Szene bleibt erhalten; Prozessausgabe und Prüfbericht für eine gezielte Korrektur lesen." };
        if (new FileInfo(reportPath).Length > 16 * 1024 * 1024)
            throw new InvalidDataException("Blender-Etappenbericht überschreitet 16 MiB.");
        var report = JsonNode.Parse(await File.ReadAllTextAsync(reportPath, token).ConfigureAwait(false))!.AsObject();
        if (report["success"]?.GetValue<bool>() != true)
            return new { success = false, operation = "stage", label, execution, reportPath = Relative(workspace, reportPath),
                error = report["error"]?.GetValue<string>() ?? "Der Etappenbericht bestätigt keinen gespeicherten Zwischenstand." };
        token.ThrowIfCancellationRequested();
        File.Move(temporaryScene, outputPath, overwrite: false);
        await using var savedScene = File.Open(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var sceneHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(savedScene, token).ConfigureAwait(false));
        report["outputPath"] = Relative(workspace, outputPath);
        report["sceneSha256"] = sceneHash;
        report["scriptPath"] = Relative(workspace, scriptPath);
        report["scriptSha256"] = scriptHash;
        report["baseScene"] = basePath is null ? null : Relative(workspace, basePath);
        report["baseSceneSha256"] = basePath is null ? null : args.GetProperty("baseSceneSha256").GetString();
        report["stageId"] = stageId;
        report["createdAt"] = DateTimeOffset.UtcNow.ToString("O");
        var renderCompleted = report["renderCompleted"]?.GetValue<bool>() ?? false;
        if (report["images"] is JsonArray images && renderDirectory is not null)
        {
            foreach (var image in images.OfType<JsonObject>())
            {
                var imagePath = Path.GetFullPath(image["path"]!.GetValue<string>());
                if (!imagePath.StartsWith(renderDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(imagePath) || new FileInfo(imagePath).Length < 100)
                    throw new InvalidDataException("Blender lieferte kein gültiges Renderbild im Renderordner der Revision.");
                var relative = Relative(workspace, imagePath);
                _ = WorkspaceFilePath.Resolve(workspace, relative);
                image["path"] = relative;
                await using var imageStream = File.OpenRead(imagePath);
                image["sha256"] = Convert.ToHexStringLower(await SHA256.HashDataAsync(imageStream, token).ConfigureAwait(false));
                image["bytes"] = imageStream.Length;
            }
        }
        await File.WriteAllTextAsync(reportPath, report.ToJsonString(JsonOptions), token).ConfigureAwait(false);
        var preview = await EnsurePreviewAsync(executable, workspace, outputPath, sceneHash, label, token).ConfigureAwait(false);
        return new
        {
            success = true, operation = "stage", stageId, label, scriptPath = Relative(workspace, scriptPath), scriptSha256 = scriptHash,
            baseScene = basePath is null ? null : Relative(workspace, basePath), outputPath = Relative(workspace, outputPath),
            sceneSha256 = sceneHash, reportPath = Relative(workspace, reportPath), valid = report["valid"]?.GetValue<bool>() ?? false,
            inspectionCompleted = report["inspectionCompleted"]?.GetValue<bool>() ?? false,
            counts = report["counts"], bounds = report["bounds"], issues = report["issues"]?.AsArray().Take(32).ToArray(),
            issueCount = report["issues"]?.AsArray().Count ?? 0,
            images = views is null ? null : report["images"],
            renderCompleted = views is null ? null : (bool?)renderCompleted,
            renderDirectory = renderDirectory is null ? null : Relative(workspace, renderDirectory),
            preview, execution,
            instruction = views is null
                ? "Zwischenstand gespeichert und zur Blender-Vorschau übergeben. preview.state beachten. Jetzt strukturelle Befunde auswerten, render und tatsächliche Vision-Prüfung dieser Etappe durchführen (stage mit views rendert beim nächsten Mal direkt). Neue Umlenkung berücksichtigen. Erst danach mit einem neuen kleinen Skript auf dieser baseScene weiterarbeiten; akzeptierte Bauteile erhalten und design.json aktualisieren."
                : renderCompleted
                    ? "Zwischenstand gespeichert, Vorschau aktualisiert und Prüfansichten in images gerendert. Jetzt jedes Bild mit image.input file laden und mit media.analyze prüfen: Renderbild als uploadId, die passenden Referenzbilder als referenceUploadIds, konkrete Prüffrage je Ansicht. Die Vision-Antwort liefert Unterschiede und Änderungsanweisungen; leite daraus die nächste kleine Korrekturetappe auf dieser baseScene ab. Kein Kriterium ist erfüllt, solange Vision Unterschiede nennt. design.json (visualChecks, stageHistory, currentScene) aktualisieren."
                    : "Zwischenstand gespeichert, aber die Prüfansichten wurden nicht vollständig gerendert; issues beachten und render mit frischem outputDirectory wiederholen. Erst nach tatsächlicher Vision-Prüfung weiterbauen.",
        };
    }

    private static async Task<FileStream> OpenVerifiedSourceAsync(string path, string expectedHash, CancellationToken token)
    {
        var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
            if (!hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Die Ausgangsszene wurde seit dem Lesen verändert. Ermittle ihren aktuellen SHA-256 erneut.");
            return stream;
        }
        catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
    }
}
