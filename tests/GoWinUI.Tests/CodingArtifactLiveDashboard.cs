using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace GoWinUI.Tests;

/// <summary>
/// Lokale, nur auf Loopback erreichbare Liveansicht für Plot- und
/// Simulationsartefakte eines Coding-Agent-Laufs. Die Ansicht liest ausschließlich
/// aus dem explizit freigegebenen Testworkspace und führt dort keinen Code aus.
/// </summary>
internal sealed class CodingArtifactLiveDashboard : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly string workspace;
    private readonly string webAssetRoot;
    private readonly Action<string, object?> record;
    private readonly HttpListener listener;
    private readonly CancellationTokenSource shutdown = new();
    private readonly CancellationTokenRegistration externalCancellation;
    private readonly Dictionary<string, string> observedSignatures = new(StringComparer.OrdinalIgnoreCase);
    private readonly object observationGate = new();
    private readonly Task serverTask;
    private readonly Task monitorTask;
    private long observedRevisionCount;
    private bool disposed;

    private CodingArtifactLiveDashboard(
        string workspace,
        Action<string, object?> record,
        bool openBrowser,
        CancellationToken cancellationToken)
    {
        this.workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        webAssetRoot = LocateWebAssetRoot();
        this.record = record;
        var port = ReserveLoopbackPort();
        Url = new Uri($"http://127.0.0.1:{port}/", UriKind.Absolute);
        listener = new HttpListener();
        listener.Prefixes.Add(Url.AbsoluteUri);
        listener.Start();
        externalCancellation = cancellationToken.Register(static state =>
        {
            var dashboard = (CodingArtifactLiveDashboard)state!;
            dashboard.shutdown.Cancel();
            dashboard.listener.Close();
        }, this);
        serverTask = ServeAsync(shutdown.Token);
        monitorTask = MonitorArtifactsAsync(shutdown.Token);
        record("artifact.dashboard.started", new
        {
            url = Url.AbsoluteUri,
            this.workspace,
            refreshMilliseconds = 1_000,
        });
        if (openBrowser)
        {
            OpenBrowser();
        }
    }

    public Uri Url { get; }

    public long ObservedRevisionCount => Interlocked.Read(ref observedRevisionCount);

    public static CodingArtifactLiveDashboard Start(
        string workspace,
        Action<string, object?> record,
        CancellationToken cancellationToken,
        bool? openBrowser = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        ArgumentNullException.ThrowIfNull(record);
        var shouldOpen = openBrowser ?? !string.Equals(
            Environment.GetEnvironmentVariable("GO_AI_LIVE_DASHBOARD"),
            "0",
            StringComparison.OrdinalIgnoreCase);
        return new CodingArtifactLiveDashboard(workspace, record, shouldOpen, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        record("artifact.dashboard.stopped", new
        {
            observedRevisions = ObservedRevisionCount,
        });
        await externalCancellation.DisposeAsync().ConfigureAwait(false);
        shutdown.Cancel();
        listener.Close();
        try
        {
            await Task.WhenAll(serverTask, monitorTask).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or HttpListenerException or ObjectDisposedException)
        {
            // Erwarteter Abschluss beim Stoppen des Livetests.
        }
        shutdown.Dispose();
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync()
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or HttpListenerException or ObjectDisposedException)
            {
                break;
            }

            try
            {
                await HandleRequestAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await WriteTextAsync(
                    context.Response,
                    HttpStatusCode.ServiceUnavailable,
                    "text/plain; charset=utf-8",
                    "Das Artefakt wird gerade aktualisiert.",
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                context.Response.Close();
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";
        if (path == "/")
        {
            await WriteTextAsync(
                context.Response,
                HttpStatusCode.OK,
                "text/html; charset=utf-8",
                DashboardHtml,
                cancellationToken).ConfigureAwait(false);
            return;
        }
        if (path.Equals("/api/state", StringComparison.OrdinalIgnoreCase))
        {
            var json = JsonSerializer.Serialize(BuildState(), JsonOptions);
            await WriteTextAsync(
                context.Response,
                HttpStatusCode.OK,
                "application/json; charset=utf-8",
                json,
                cancellationToken).ConfigureAwait(false);
            return;
        }
        if (TryResolveDashboardAsset(path, out var dashboardAsset))
        {
            if (!File.Exists(dashboardAsset))
            {
                await WriteTextAsync(
                    context.Response,
                    HttpStatusCode.NotFound,
                    "text/plain; charset=utf-8",
                    "Dashboard-Asset nicht gefunden.",
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            await WriteArtifactAsync(context.Response, dashboardAsset, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (path.Equals("/artifact", StringComparison.OrdinalIgnoreCase))
        {
            var relativePath = context.Request.QueryString["path"];
            if (!TryResolveArtifact(relativePath, out var artifactPath))
            {
                await WriteTextAsync(
                    context.Response,
                    HttpStatusCode.BadRequest,
                    "text/plain; charset=utf-8",
                    "Ungültiger Artefaktpfad.",
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            if (!File.Exists(artifactPath))
            {
                await WriteTextAsync(
                    context.Response,
                    HttpStatusCode.NotFound,
                    "text/plain; charset=utf-8",
                    "Artefakt nicht gefunden.",
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            await WriteArtifactAsync(context.Response, artifactPath, cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteTextAsync(
            context.Response,
            HttpStatusCode.NotFound,
            "text/plain; charset=utf-8",
            "Nicht gefunden.",
            cancellationToken).ConfigureAwait(false);
    }

    private object BuildState()
    {
        var artifacts = EnumerateArtifacts()
            .Select(CreateArtifactState)
            .OrderByDescending(static item => item.ModifiedAt)
            .ToArray();
        var casesPath = Path.Combine(workspace, "einstein_cases.json");
        var attemptsPath = Path.Combine(workspace, "einstein_attempts.json");
        var progressPath = Path.Combine(workspace, "simulation_data", "live_progress.json");
        return new
        {
            generatedAt = DateTimeOffset.UtcNow,
            workspace = Path.GetFileName(workspace),
            cases = CountJsonArray(casesPath, "cases"),
            verified = CountJsonArrayItems(casesPath, "cases", "classification", "verified"),
            attempts = CountJsonArray(attemptsPath, "attempts"),
            liveProgress = ReadJsonElement(progressPath),
            observedRevisions = ObservedRevisionCount,
            artifacts,
        };
    }

    private async Task MonitorArtifactsAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var path in EnumerateArtifacts())
            {
                FileInfo info;
                try
                {
                    info = new FileInfo(path);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                var relativePath = Path.GetRelativePath(workspace, path).Replace('\\', '/');
                var signature = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{info.Length}:{info.LastWriteTimeUtc.Ticks}");
                var changed = false;
                lock (observationGate)
                {
                    if (!observedSignatures.TryGetValue(relativePath, out var previous)
                        || !string.Equals(previous, signature, StringComparison.Ordinal))
                    {
                        observedSignatures[relativePath] = signature;
                        changed = true;
                    }
                }
                if (!changed)
                {
                    continue;
                }
                var revision = Interlocked.Increment(ref observedRevisionCount);
                record("artifact.live.updated", new
                {
                    revision,
                    path = relativePath,
                    info.Length,
                    modifiedAt = info.LastWriteTimeUtc,
                });
            }
        }
    }

    private IEnumerable<string> EnumerateArtifacts()
    {
        foreach (var directoryName in new[] { "visualizations", "simulation_data", "solutions" })
        {
            var directory = Path.Combine(workspace, directoryName);
            if (!Directory.Exists(directory))
            {
                continue;
            }
            IEnumerable<string> paths;
            try
            {
                paths = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var path in paths)
            {
                yield return path;
            }
        }
        foreach (var fileName in new[] { "einstein_cases.json", "einstein_attempts.json", "einstein_analysis.md" })
        {
            var path = Path.Combine(workspace, fileName);
            if (File.Exists(path))
            {
                yield return path;
            }
        }
    }

    private ArtifactState CreateArtifactState(string path)
    {
        var info = new FileInfo(path);
        var relativePath = Path.GetRelativePath(workspace, path).Replace('\\', '/');
        var extension = info.Extension.ToLowerInvariant();
        var kind = extension switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".svg" => "image",
            ".mp4" or ".webm" => "video",
            _ => "data",
        };
        var version = string.Create(
            CultureInfo.InvariantCulture,
            $"{info.LastWriteTimeUtc.Ticks}-{info.Length}");
        return new ArtifactState(
            relativePath,
            kind,
            info.Length,
            info.LastWriteTimeUtc,
            $"/artifact?path={Uri.EscapeDataString(relativePath)}&v={version}",
            kind == "data" ? ReadPreview(path) : null,
            version);
    }

    private bool TryResolveArtifact(string? relativePath, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            return false;
        }
        try
        {
            var candidate = Path.GetFullPath(Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var root = workspace + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            path = candidate;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private bool TryResolveDashboardAsset(string requestPath, out string path)
    {
        path = string.Empty;
        if (requestPath.Equals("/assets/markdown.js", StringComparison.OrdinalIgnoreCase))
        {
            path = Path.Combine(webAssetRoot, "markdown.js");
            return true;
        }

        const string katexPrefix = "/assets/katex/";
        if (!requestPath.StartsWith(katexPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var katexRoot = Path.GetFullPath(Path.Combine(webAssetRoot, "vendor", "katex", "0.16.10"));
        var relativePath = requestPath[katexPrefix.Length..].Replace('/', Path.DirectorySeparatorChar);
        try
        {
            var candidate = Path.GetFullPath(Path.Combine(katexRoot, relativePath));
            var rootPrefix = Path.TrimEndingDirectorySeparator(katexRoot) + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            path = candidate;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string LocateWebAssetRoot()
    {
        var candidates = new List<string>
        {
            Path.Combine(Directory.GetCurrentDirectory(), "src", "GoWinUI.App", "Assets", "Web"),
        };
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            candidates.Add(Path.Combine(directory.FullName, "src", "GoWinUI.App", "Assets", "Web"));
        }

        return candidates.FirstOrDefault(candidate =>
                   File.Exists(Path.Combine(candidate, "markdown.js"))
                   && File.Exists(Path.Combine(candidate, "vendor", "katex", "0.16.10", "katex.min.js")))
               ?? throw new DirectoryNotFoundException("Die lokalen GO-WebView-/KaTeX-Assets wurden nicht gefunden.");
    }

    private static async Task WriteArtifactAsync(
        HttpListenerResponse response,
        string path,
        CancellationToken cancellationToken)
    {
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = GetContentType(Path.GetExtension(path));
        response.Headers[HttpResponseHeader.CacheControl] = "no-store, no-cache, must-revalidate";
        await using var source = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        response.ContentLength64 = source.Length;
        await source.CopyToAsync(response.OutputStream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteTextAsync(
        HttpListenerResponse response,
        HttpStatusCode status,
        string contentType,
        string text,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        response.StatusCode = (int)status;
        response.ContentType = contentType;
        response.Headers[HttpResponseHeader.CacheControl] = "no-store, no-cache, must-revalidate";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static int CountJsonArray(string path, string propertyName)
    {
        var element = ReadJsonElement(path);
        return element is { ValueKind: JsonValueKind.Object }
            && element.Value.TryGetProperty(propertyName, out var values)
            && values.ValueKind == JsonValueKind.Array
                ? values.GetArrayLength()
                : 0;
    }

    private static int CountJsonArrayItems(
        string path,
        string arrayProperty,
        string itemProperty,
        string expectedValue)
    {
        var element = ReadJsonElement(path);
        if (element is not { ValueKind: JsonValueKind.Object }
            || !element.Value.TryGetProperty(arrayProperty, out var values)
            || values.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }
        return values.EnumerateArray().Count(item =>
            item.ValueKind == JsonValueKind.Object
            && item.TryGetProperty(itemProperty, out var value)
            && string.Equals(value.GetString(), expectedValue, StringComparison.OrdinalIgnoreCase));
    }

    private static JsonElement? ReadJsonElement(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.Clone();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? ReadPreview(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".json" or ".csv" or ".tsv" or ".txt" or ".md" or ".log"))
        {
            return null;
        }
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, true, 4_096, false);
            var buffer = new char[12_000];
            var length = reader.ReadBlock(buffer, 0, buffer.Length);
            return new string(buffer, 0, length);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return null;
        }
    }

    private void OpenBrowser()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Url.AbsoluteUri,
                UseShellExecute = true,
            });
            record("artifact.dashboard.opened", new { url = Url.AbsoluteUri });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            record("artifact.dashboard.open_failed", new
            {
                url = Url.AbsoluteUri,
                error = exception.GetType().Name,
            });
        }
    }

    private static int ReserveLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            probe.Start();
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private static string GetContentType(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".json" => "application/json; charset=utf-8",
        ".csv" => "text/csv; charset=utf-8",
        ".md" => "text/markdown; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".woff2" => "font/woff2",
        ".woff" => "font/woff",
        ".ttf" => "font/ttf",
        _ => "application/octet-stream",
    };

    private sealed record ArtifactState(
        string Path,
        string Kind,
        long Size,
        DateTime ModifiedAt,
        string Url,
        string? Preview,
        string Version);

    private const string DashboardHtml = """
        <!doctype html>
        <html lang="de">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width,initial-scale=1">
          <title>GO Einstein · Live-Simulation</title>
          <link rel="stylesheet" href="/assets/katex/katex.min.css">
          <style>
            :root { color-scheme: dark; --accent:#a765ff; --panel:#211b2b; --line:#4c405e; --muted:#b9aec8; --border:var(--line); --surface-raised:#2a2335; --background-accent:#a765ff; --bg:#121016; --success:#8fbd45; --radius-md:10px; --radius-sm:8px; }
            * { box-sizing:border-box; }
            body { margin:0; background:#121016; color:#f5f1fa; font:14px/1.5 "Segoe UI",sans-serif; }
            header { position:sticky; top:0; z-index:5; padding:16px 22px; background:rgba(18,16,22,.95); border-bottom:1px solid var(--line); backdrop-filter:blur(14px); }
            h1 { margin:0 0 8px; font-size:20px; }
            .status { display:flex; flex-wrap:wrap; gap:8px; color:var(--muted); }
            .chip { padding:5px 10px; border:1px solid var(--line); border-radius:999px; background:var(--panel); }
            .live::before { content:""; display:inline-block; width:8px; height:8px; margin-right:7px; border-radius:50%; background:var(--accent); box-shadow:0 0 10px var(--accent); }
            main { padding:20px; max-width:1800px; margin:auto; }
            h2 { margin:22px 0 12px; font-size:16px; color:#d8c3f3; }
            .grid { display:grid; grid-template-columns:repeat(auto-fit,minmax(360px,1fr)); gap:14px; }
            article { min-width:0; overflow:hidden; border:1px solid var(--line); border-radius:14px; background:var(--panel); }
            .module-header { display:flex; align-items:center; min-height:42px; border-bottom:1px solid var(--line); }
            article h3 { flex:1; min-width:0; margin:0; padding:10px 12px; font-size:13px; color:#e6daf4; overflow-wrap:anywhere; }
            .module-toggle { display:grid; place-items:center; flex:0 0 34px; width:34px; height:34px; margin-right:5px; padding:0; border:1px solid transparent; border-radius:8px; background:transparent; color:#d8c3f3; cursor:pointer; }
            .module-toggle:hover,.module-toggle:focus-visible { border-color:var(--accent); background:color-mix(in srgb,var(--accent) 14%,transparent); outline:none; }
            .module-toggle svg { width:17px; height:17px; fill:none; stroke:currentColor; stroke-width:1.8; stroke-linecap:round; stroke-linejoin:round; pointer-events:none; }
            body.module-maximized { overflow:hidden; }
            body.module-maximized::before { content:""; position:fixed; inset:0; z-index:90; background:rgba(8,6,11,.82); backdrop-filter:blur(8px); }
            article.maximized { position:fixed; inset:14px; z-index:100; display:flex; flex-direction:column; min-width:0; min-height:0; margin:0; border-color:var(--accent); box-shadow:0 22px 90px rgba(0,0,0,.72); }
            article.maximized .module-header { flex:0 0 auto; }
            article.maximized > img,article.maximized > video,article.maximized > pre,article.maximized > .markdown { flex:1 1 auto; min-height:0; max-height:none; }
            article.maximized > img,article.maximized > video { height:100%; object-fit:contain; }
            img,video { display:block; width:100%; max-height:620px; object-fit:contain; background:#0c0a0f; }
            pre { margin:0; padding:12px; max-height:420px; overflow:auto; white-space:pre-wrap; color:#ddd4e7; font:12px/1.45 Consolas,monospace; }
            .markdown { max-height:620px; padding:14px 16px; overflow:auto; color:#eee8f5; }
            .markdown p { margin:0 0 12px; }
            .markdown p:last-child { margin-bottom:0; }
            .markdown h1,.markdown h2,.markdown h3,.markdown h4 { margin:18px 0 9px; color:#f4ecff; font-family:"Segoe UI Variable Text","Segoe UI",sans-serif; }
            .markdown ul,.markdown ol { padding-left:24px; }
            .markdown blockquote { margin:12px 0; padding:4px 14px; border-left:3px solid var(--accent); color:var(--muted); }
            .markdown a { color:var(--accent); }
            .markdown code { padding:2px 5px; border-radius:5px; background:#17131d; font-family:"Cascadia Mono",Consolas,monospace; }
            .code-block { margin:13px 0; overflow:hidden; border:1px solid var(--line); border-radius:10px; background:#111; }
            .code-header { display:flex; align-items:center; justify-content:space-between; padding:7px 11px; background:#2a2335; color:#e3dde8; font-size:12px; }
            .code-header button { border:0; background:transparent; color:inherit; }
            .code-block pre { margin:0; padding:15px; }
            .table-wrap { width:fit-content; max-width:100%; margin:12px 0; overflow:auto; border:1px solid var(--accent); border-radius:8px; }
            table { width:max-content; border-collapse:collapse; }
            th,td { padding:8px 10px; border-bottom:1px solid var(--accent); text-align:left; }
            th:not(:last-child),td:not(:last-child) { border-right:1px solid var(--accent); }
            th { background:var(--accent); color:#000; }
            tbody tr:nth-child(odd) td { background:#2a2335; }
            tbody tr:nth-child(even) td { background:#121016; }
            .math-selectable { position:relative; display:inline-block; max-width:100%; vertical-align:baseline; border-radius:6px; cursor:pointer; }
            .math-selectable.display { display:block; width:fit-content; max-width:100%; margin:12px auto; padding:6px 10px; overflow-x:auto; overflow-y:hidden; text-align:center; }
            .math-selectable.display .math-render { font-size:1.14em; }
            .math-render,.math-render * { pointer-events:none; }
            .math-source-text { position:absolute; inset:0; z-index:2; overflow:hidden; color:transparent; white-space:pre; cursor:text; user-select:text; }
            .math-source-text.fallback { position:relative; inset:auto; z-index:auto; overflow:visible; color:inherit; white-space:pre-wrap; }
            .math-selectable.copied { outline:1px solid var(--success); background:color-mix(in srgb,var(--success) 15%,transparent); }
            .empty { padding:26px; border:1px dashed var(--line); border-radius:14px; color:var(--muted); }
            .error { color:#ff8b91; }
          </style>
        </head>
        <body>
          <header>
            <h1>Einsteinsche Feldgleichungen · Live-Fortschritt</h1>
            <div class="status">
              <span id="connection" class="chip live">Live verbunden</span>
              <span id="workspace" class="chip">Workspace</span>
              <span id="counts" class="chip">Noch keine Daten</span>
              <span id="progress" class="chip">Warte auf Berechnung …</span>
            </div>
          </header>
          <main>
            <h2>Plots und Simulationen</h2>
            <section id="media" class="grid"><div class="empty">Warte auf das erste Plot- oder Simulationsartefakt …</div></section>
            <h2>Daten und Herleitungen</h2>
            <section id="data" class="grid"><div class="empty">Noch keine maschinenlesbaren Zwischenstände.</div></section>
          </main>
          <script src="/assets/katex/katex.min.js"></script>
          <script src="/assets/markdown.js"></script>
          <script>
            let lastSignature = "";
            let maximizedPath = null;
            const moduleScrollPositions = new Map();
            const byId = id => document.getElementById(id);
            const maximizeIcon = `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M8 3H3v5M16 3h5v5M21 16v5h-5M3 16v5h5"/></svg>`;
            const restoreIcon = `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M9 3v6H3M15 3v6h6M21 15h-6v6M3 15h6v6"/></svg>`;
            function applyModuleSize(article, button, expanded) {
              article.classList.toggle("maximized", expanded);
              button.innerHTML = expanded ? restoreIcon : maximizeIcon;
              button.title = expanded ? "Verkleinern" : "Maximieren";
              button.setAttribute("aria-label", button.title);
              button.setAttribute("aria-pressed", String(expanded));
              document.body.classList.toggle("module-maximized", Boolean(maximizedPath));
            }
            function setMaximized(path) {
              maximizedPath = maximizedPath === path ? null : path;
              document.querySelectorAll("article[data-path]").forEach(article => {
                const button = article.querySelector(".module-toggle");
                applyModuleSize(article, button, article.dataset.path === maximizedPath);
              });
            }
            function captureScrollState() {
              document.querySelectorAll("article[data-path]").forEach(article => {
                const surface = article.querySelector(".markdown, pre");
                if (surface) {
                  moduleScrollPositions.set(article.dataset.path, {
                    top: surface.scrollTop,
                    left: surface.scrollLeft,
                  });
                }
              });
              return { x: window.scrollX, y: window.scrollY };
            }
            function restoreScrollState(viewport) {
              const restore = () => {
                document.querySelectorAll("article[data-path]").forEach(article => {
                  const position = moduleScrollPositions.get(article.dataset.path);
                  const surface = article.querySelector(".markdown, pre");
                  if (position && surface) {
                    surface.scrollTop = position.top;
                    surface.scrollLeft = position.left;
                  }
                });
                window.scrollTo(viewport.x, viewport.y);
              };
              restore();
              requestAnimationFrame(() => {
                restore();
                document.querySelectorAll("article img").forEach(image => {
                  if (!image.complete) image.addEventListener("load", restore, { once:true });
                });
              });
            }
            function card(item) {
              const article = document.createElement("article");
              article.dataset.path = item.path;
              const header = document.createElement("div");
              header.className = "module-header";
              const title = document.createElement("h3");
              title.textContent = `${item.path} · ${(item.size/1024).toFixed(1)} KB`;
              const toggle = document.createElement("button");
              toggle.type = "button";
              toggle.className = "module-toggle";
              toggle.addEventListener("click", () => setMaximized(item.path));
              header.append(title, toggle);
              article.appendChild(header);
              if (item.kind === "image") {
                const image = document.createElement("img");
                image.src = item.url;
                image.alt = item.path;
                article.appendChild(image);
              } else if (item.kind === "video") {
                const video = document.createElement("video");
                video.src = item.url;
                video.controls = true;
                video.preload = "metadata";
                article.appendChild(video);
              } else if (item.path.toLowerCase().endsWith(".md") && globalThis.goMarkdown) {
                const preview = document.createElement("div");
                preview.className = "markdown";
                preview.append(globalThis.goMarkdown.render(item.preview || ""));
                article.appendChild(preview);
              } else {
                const preview = document.createElement("pre");
                preview.textContent = item.preview || "Binäres Simulationsartefakt";
                article.appendChild(preview);
              }
              applyModuleSize(article, toggle, item.path === maximizedPath);
              return article;
            }
            function render(state) {
              byId("workspace").textContent = state.workspace;
              byId("counts").textContent = `${state.cases} Fälle · ${state.verified} verifiziert · ${state.attempts} Versuche`;
              const progress = state.liveProgress || {};
              byId("progress").textContent = progress.phase
                ? `${progress.caseId || "Berechnung"} · ${progress.phase} · ${progress.step || 0}/${progress.totalSteps || "?"}`
                : `Beobachtete Aktualisierungen: ${state.observedRevisions}`;
              const signature = JSON.stringify(state.artifacts.map(x => [x.path,x.version]).concat([["progress",JSON.stringify(progress)]]));
              if (signature === lastSignature) return;
              const scrollState = captureScrollState();
              lastSignature = signature;
              const media = state.artifacts.filter(x => x.kind === "image" || x.kind === "video");
              const data = state.artifacts.filter(x => x.kind === "data");
              const currentPaths = new Set(state.artifacts.map(x => x.path));
              moduleScrollPositions.forEach((_, path) => {
                if (!currentPaths.has(path)) moduleScrollPositions.delete(path);
              });
              if (maximizedPath && !state.artifacts.some(x => x.path === maximizedPath)) maximizedPath = null;
              byId("media").replaceChildren(...(media.length ? media.map(card) : [Object.assign(document.createElement("div"), {className:"empty", textContent:"Warte auf das erste Plot- oder Simulationsartefakt …"})]));
              byId("data").replaceChildren(...(data.length ? data.map(card) : [Object.assign(document.createElement("div"), {className:"empty", textContent:"Noch keine maschinenlesbaren Zwischenstände."})]));
              document.body.classList.toggle("module-maximized", Boolean(maximizedPath));
              restoreScrollState(scrollState);
            }
            async function refresh() {
              try {
                const response = await fetch("/api/state", {cache:"no-store"});
                if (!response.ok) throw new Error(response.statusText);
                render(await response.json());
                byId("connection").textContent = "Live verbunden";
                byId("connection").className = "chip live";
              } catch (error) {
                byId("connection").textContent = "Verbindung beendet";
                byId("connection").className = "chip error";
              }
            }
            refresh();
            setInterval(refresh, 1000);
            document.addEventListener("keydown", event => {
              if (event.key === "Escape" && maximizedPath) {
                event.preventDefault();
                setMaximized(maximizedPath);
              }
            });
          </script>
        </body>
        </html>
        """;
}
