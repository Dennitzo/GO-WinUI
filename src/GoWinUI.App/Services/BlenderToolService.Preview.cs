using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using GoWinUI.Core.Coding;

namespace GoWinUI.App.Services;

public sealed partial class BlenderToolService
{
    private static readonly SemaphoreSlim PreviewGate = new(1, 1);
    private static readonly Dictionary<string, PreviewSession> PreviewSessions = new(StringComparer.OrdinalIgnoreCase);

    private sealed class PreviewSession(Process process, string directory, string token) : IDisposable
    {
        public Process Process { get; } = process;
        public string Directory { get; } = directory;
        public string Token { get; } = token;
        public int Revision { get; set; }
        public string? ScenePath { get; set; }
        public string? SceneHash { get; set; }
        public string? Label { get; set; }
        public void Dispose() => Process.Dispose();
    }

    private sealed record PreviewStatus(string? SessionToken, int Revision, string? State, string? Message, string? BlendPath);

    private static async Task<object> EnsurePreviewAsync(string executable, string workspace, string? scenePath,
        string? sceneHash, string? label, CancellationToken token)
    {
        var workspacePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        _ = WorkspaceFilePath.Resolve(workspacePath, ".go-blender-preview");
        if (!Directory.Exists(workspacePath)) throw new DirectoryNotFoundException("Der Blender-Workspace fehlt.");
        await PreviewGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            foreach (var key in PreviewSessions.Where(static pair => pair.Value.Process.HasExited).Select(static pair => pair.Key).ToArray())
            {
                PreviewSessions[key].Dispose();
                PreviewSessions.Remove(key);
            }
            if (!PreviewSessions.TryGetValue(workspacePath, out var session))
            {
                var sessionToken = Guid.NewGuid().ToString("N");
                var directory = WorkspaceFilePath.Resolve(workspacePath, ".go-blender-preview/" + sessionToken);
                CreateFreshDirectory(workspacePath, directory);
                var scriptPath = Path.Combine(directory, "go_blender_preview.py");
                await WriteResourceAsync("go_blender_preview.py", scriptPath, token).ConfigureAwait(false);
                var requestPath = Path.Combine(directory, "request.json");
                await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new
                {
                    workspace = workspacePath, statePath = Path.Combine(directory, "state.json"),
                    sessionToken, statusPath = Path.Combine(directory, "status.json"),
                }, JsonOptions), token).ConfigureAwait(false);
                await WritePreviewStateAsync(Path.Combine(directory, "state.json"), sessionToken, 0,
                    null, null, "Bereit für die erste Modellierungsetappe", token).ConfigureAwait(false);
                var start = new ProcessStartInfo(executable)
                {
                    // Detach the GUI from the caller's redirected process streams.
                    // Otherwise a test/console host can wait forever for Blender's
                    // inherited stdout handle even after all tool work has finished.
                    WorkingDirectory = workspacePath, UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Normal,
                };
                foreach (var argument in new[] { "--factory-startup", "--disable-autoexec", "--python", scriptPath, "--", requestPath })
                    start.ArgumentList.Add(argument);
                Process? opened;
                try { opened = Process.Start(start); }
                catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException)
                {
                    return new { success = false, operation = "preview", state = "error", revision = 0,
                        path = scenePath is null ? null : Relative(workspacePath, scenePath), processId = (int?)null,
                        label, message = "Blender-Vorschaufenster konnte nicht geöffnet werden: " + exception.Message };
                }
                if (opened is null) throw new IOException("Blender-Vorschaufenster konnte nicht gestartet werden.");
                session = new PreviewSession(opened, directory, sessionToken);
                PreviewSessions.Add(workspacePath, session);
            }

            if (scenePath is not null && (!string.Equals(scenePath, session.ScenePath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(sceneHash, session.SceneHash, StringComparison.OrdinalIgnoreCase)))
            {
                var nextRevision = session.Revision + 1;
                var nextLabel = label ?? Path.GetFileName(scenePath);
                await WritePreviewStateAsync(Path.Combine(session.Directory, "state.json"), session.Token,
                    nextRevision, scenePath, sceneHash, nextLabel, token).ConfigureAwait(false);
                // Commit only after publication, so a failed/cancelled write can be
                // retried instead of leaving the host ahead of the GUI watcher.
                session.Revision = nextRevision;
                session.ScenePath = scenePath;
                session.SceneHash = sceneHash;
                session.Label = nextLabel;
            }
            var clock = Stopwatch.StartNew();
            PreviewStatus? status = null;
            while (clock.Elapsed < TimeSpan.FromSeconds(30) && !session.Process.HasExited)
            {
                token.ThrowIfCancellationRequested();
                status = await ReadPreviewStatusAsync(Path.Combine(session.Directory, "status.json"), token).ConfigureAwait(false);
                if (status?.SessionToken == session.Token && status.Revision == session.Revision
                    && status.State is "ready" or "blocked" or "error") break;
                await Task.Delay(150, token).ConfigureAwait(false);
            }
            var matched = status?.SessionToken == session.Token && status.Revision == session.Revision;
            var state = session.Process.HasExited ? "error" : matched ? status!.State ?? "error" : "pending";
            var message = session.Process.HasExited ? "Das Blender-Vorschaufenster wurde geschlossen oder konnte nicht initialisiert werden."
                : matched ? status!.Message : "Blender lädt die Vorschau noch. Die Szene ist gespeichert; preview erneut abfragen.";
            return new
            {
                success = state == "ready", operation = "preview", state, revision = session.Revision,
                path = session.ScenePath is null ? null : Relative(workspacePath, session.ScenePath),
                processId = session.Process.Id, label = session.Label, message,
            };
        }
        finally { PreviewGate.Release(); }
    }

    private static async Task WritePreviewStateAsync(string path, string sessionToken, int revision,
        string? blendPath, string? expectedSha256, string? label, CancellationToken token)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new
            { sessionToken, revision, blendPath, expectedSha256, label }, JsonOptions), token).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(temporary, path, overwrite: true); break; }
                catch (Exception exception) when (attempt < 4 && (exception is IOException or UnauthorizedAccessException))
                { await Task.Delay(50, token).ConfigureAwait(false); }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<PreviewStatus?> ReadPreviewStatusAsync(string path, CancellationToken token)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024) return null;
            return JsonSerializer.Deserialize<PreviewStatus>(await File.ReadAllTextAsync(path, token).ConfigureAwait(false), JsonOptions);
        }
        catch (Exception exception) when (exception is IOException or JsonException) { return null; }
    }
}
