using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace GoWinUI.App.Services;

/// <summary>Serves the desktop WebView to browsers on the private local network.</summary>
internal sealed class AssistantLanWebHost : IAsyncDisposable
{
    private static readonly JsonSerializerOptions AccessJsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly Action<ILogger, Exception?> LogDisconnected = LoggerMessage.Define(
        LogLevel.Debug, new EventId(9401, "LanBrowserDisconnected"), "LAN browser bridge disconnected.");
    private readonly ConcurrentDictionary<Guid, LanWebSocketClient> _clients = new();
    private readonly ConcurrentDictionary<Guid, LanEventClient> _eventClients = new();
    private readonly SemaphoreSlim _broadcastGate = new(1, 1);
    private readonly ILogger _logger;
    private readonly int _port;
    private readonly int _gatewayPort;
    private readonly string _profile;
    private readonly string _launchNonce;
    private readonly string _executableSha256;
    private WebApplication? _application;

    public AssistantLanWebHost(string dataDirectory, ILogger logger)
    {
        _logger = logger;
        _profile = Environment.GetEnvironmentVariable("ASSISTANT_PROFILE") ?? "stable";
        _launchNonce = Environment.GetEnvironmentVariable("ASSISTANT_LAUNCH_NONCE") ?? string.Empty;
        _executableSha256 = ComputeExecutableSha256();
        const int defaultPort = 8090;
        _port = int.TryParse(Environment.GetEnvironmentVariable("ASSISTANT_LAN_WEB_PORT"), out var configured)
            && configured is >= 1024 and <= 65535 ? configured : defaultPort;
        _gatewayPort = int.TryParse(Environment.GetEnvironmentVariable("ASSISTANT_GATEWAY_PORT"), out var gatewayPort)
            && gatewayPort is >= 1024 and <= 65535 ? gatewayPort : 8080;
        var accessRoot = Path.Combine(dataDirectory, "LanWeb");
        Directory.CreateDirectory(accessRoot);
        var tokenPath = Path.Combine(accessRoot, "access-token.txt");
        if (File.Exists(tokenPath)) File.Delete(tokenPath);
        AccessFilePath = Path.Combine(accessRoot, "access.json");
    }

    public event EventHandler<WebBridgeMessageEventArgs>? MessageReceived;

    public Func<Guid, CancellationToken, Task<LanArtifactResource?>>? ArtifactResolver { get; set; }

    public Func<Guid, string, CancellationToken, Task<string?>>? CodingPreviewResolver { get; set; }

    public string AccessFilePath { get; }

    public async Task StartAsync(string webRoot, string? previewRoot = null, CancellationToken cancellationToken = default)
    {
        if (_application is not null) return;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.ListenAnyIP(_port);
            server.AddServerHeader = false;
            server.Limits.MaxRequestBodySize = 100_663_296;
        });
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
            var requestPath = context.Request.Path;
            if ((requestPath == "/bridge" || requestPath == "/message" || requestPath == "/events")
                && !IsAllowedBrowserOrigin(context))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await next();
        });
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        app.Map("/bridge", HandleBridgeAsync);
        app.MapPost("/message", HandleMessageAsync);
        app.MapGet("/events", HandleEventsAsync);
        app.MapGet("/health/identity", () => Results.Json(new
        {
            profile = _profile,
            processId = Environment.ProcessId,
            launchNonce = _launchNonce,
            executableSha256 = _executableSha256,
        }));
        app.MapGet("/artifacts/{artifactId:guid}", (Delegate)HandleArtifactAsync);
        app.MapGet("/coding-preview/{messageId:guid}/{stepId}", (Delegate)HandleCodingPreviewAsync);
        if (!string.IsNullOrWhiteSpace(previewRoot))
        {
            Directory.CreateDirectory(previewRoot);
            app.UseStaticFiles(new StaticFileOptions
            {
                RequestPath = "/preview",
                FileProvider = new PhysicalFileProvider(previewRoot),
                ContentTypeProvider = new FileExtensionContentTypeProvider(),
                ServeUnknownFileTypes = false,
            });
        }
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = new PhysicalFileProvider(webRoot) });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(webRoot),
            ContentTypeProvider = new FileExtensionContentTypeProvider(),
            ServeUnknownFileTypes = false,
        });
        _application = app;
        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            WriteAccessDescription();
        }
        catch
        {
            _application = null;
            try { await app.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static string ComputeExecutableSha256()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return string.Empty;
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public async Task BroadcastAsync(string json, CancellationToken cancellationToken = default)
    {
        await _broadcastGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var entry in _clients.ToArray())
            {
                if (entry.Value.Socket.State != WebSocketState.Open)
                {
                    RemoveClient(entry.Key);
                    continue;
                }
                try
                {
                    using var sendCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    sendCancellation.CancelAfter(TimeSpan.FromSeconds(5));
                    await entry.Value.Socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, sendCancellation.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or IOException or InvalidOperationException)
                {
                    RemoveClient(entry.Key);
                }
            }
            foreach (var eventClient in _eventClients.ToArray())
            {
                if (eventClient.Value.Channel.Writer.TryWrite(json)) continue;
                if (_eventClients.TryRemove(eventClient.Key, out var removed))
                    removed.Channel.Writer.TryComplete(new IOException("Der LAN-Ereignisstrom war zu langsam und wird neu verbunden."));
            }
        }
        finally
        {
            _broadcastGate.Release();
        }
    }

    public async Task SendToClientAsync(string clientId, string json, CancellationToken cancellationToken = default)
    {
        await _broadcastGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var entry in _clients.Where(item => item.Value.ClientId == clientId).ToArray())
            {
                if (entry.Value.Socket.State != WebSocketState.Open)
                {
                    RemoveClient(entry.Key);
                    continue;
                }
                try
                {
                    using var sendCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    sendCancellation.CancelAfter(TimeSpan.FromSeconds(5));
                    await entry.Value.Socket.SendAsync(
                        Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, sendCancellation.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or IOException or InvalidOperationException)
                {
                    RemoveClient(entry.Key);
                }
            }
            foreach (var eventClient in _eventClients.Where(item => item.Value.ClientId == clientId).ToArray())
            {
                if (eventClient.Value.Channel.Writer.TryWrite(json)) continue;
                if (_eventClients.TryRemove(eventClient.Key, out var removed))
                    removed.Channel.Writer.TryComplete(new IOException("Der LAN-Ereignisstrom war zu langsam und wird neu verbunden."));
            }
        }
        finally
        {
            _broadcastGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Keys.ToArray()) RemoveClient(client);
        foreach (var eventClient in _eventClients.Values) eventClient.Channel.Writer.TryComplete();
        _eventClients.Clear();
        if (_application is not null)
        {
            await _application.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await _application.DisposeAsync().ConfigureAwait(false);
            _application = null;
        }
    }

    private async Task HandleMessageAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, false, 64 * 1024, leaveOpen: true);
        var json = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
        if (!AssistantWebBridge.TryParseIncomingEnvelope(json, out var envelope))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "Ungültige Bridge-Nachricht." }, context.RequestAborted)
                .ConfigureAwait(false);
            return;
        }
        MessageReceived?.Invoke(this, new WebBridgeMessageEventArgs(envelope!));
        context.Response.StatusCode = StatusCodes.Status202Accepted;
    }

    private async Task HandleEventsAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Connection = "keep-alive";
        var id = Guid.NewGuid();
        var clientId = ResolveClientId(context.Request.Query["clientId"]);
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(512)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        _eventClients[id] = new(clientId, channel);
        try
        {
            await context.Response.WriteAsync(": connected\n\n", context.RequestAborted).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
            await foreach (var json in channel.Reader.ReadAllAsync(context.RequestAborted).ConfigureAwait(false))
            {
                await context.Response.WriteAsync("data: " + json + "\n\n", context.RequestAborted).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            // A full client queue is terminated deliberately so EventSource reconnects and requests a fresh snapshot.
        }
        finally
        {
            _eventClients.TryRemove(id, out _);
            channel.Writer.TryComplete();
        }
    }

    private async Task<IResult> HandleArtifactAsync(Guid artifactId, HttpContext context)
    {
        var resource = ArtifactResolver is null
            ? null
            : await ArtifactResolver(artifactId, context.RequestAborted).ConfigureAwait(false);
        if (resource is null) return Results.NotFound();
        var download = string.Equals(context.Request.Query["download"], "1", StringComparison.Ordinal);
        var inline = IsSafeInlineContentType(resource.ContentType);
        context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox; base-uri 'none'; form-action 'none'";
        return Results.Stream(
            resource.Content,
            inline ? resource.ContentType : "application/octet-stream",
            download || !inline ? resource.FileName : null,
            enableRangeProcessing: true);
    }

    private async Task<IResult> HandleCodingPreviewAsync(Guid messageId, string stepId, HttpContext context)
    {
        var html = CodingPreviewResolver is null
            ? null
            : await CodingPreviewResolver(messageId, stepId, context.RequestAborted).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(html)) return Results.NotFound();
        context.Response.Headers["Content-Security-Policy"] =
            "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data: blob:; connect-src 'none'; base-uri 'none'; form-action 'none'; frame-src 'none'; frame-ancestors 'self'; sandbox allow-scripts";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.Text(html, "text/html", Encoding.UTF8);
    }

    private async Task HandleBridgeAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            return;
        }
        var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var id = Guid.NewGuid();
        var clientId = ResolveClientId(context.Request.Query["clientId"]);
        _clients[id] = new(clientId, socket);
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, context.RequestAborted).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.MessageType != WebSocketMessageType.Text) continue;
                message.Write(buffer, 0, result.Count);
                if (message.Length > 100_663_296)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Nachricht zu groß", context.RequestAborted);
                    break;
                }
                if (!result.EndOfMessage) continue;
                var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
                message.SetLength(0);
                if (AssistantWebBridge.TryParseIncomingEnvelope(json, out var envelope))
                    MessageReceived?.Invoke(this, new WebBridgeMessageEventArgs(envelope! with { ClientId = clientId }));
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or IOException)
        {
            LogDisconnected(_logger, exception);
        }
        finally
        {
            RemoveClient(id);
        }
    }

    private void RemoveClient(Guid id)
    {
        if (!_clients.TryRemove(id, out var client)) return;
        try { client.Socket.Dispose(); } catch (WebSocketException) { }
    }

    private static bool IsAllowedBrowserOrigin(HttpContext context)
    {
        var originValue = context.Request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(originValue)) return true;
        if (!Uri.TryCreate(originValue, UriKind.Absolute, out var origin)
            || origin.Scheme is not ("http" or "https")) return false;
        var requestHost = context.Request.Host;
        if (!string.Equals(origin.Host, requestHost.Host, StringComparison.OrdinalIgnoreCase)) return false;
        var originPort = origin.IsDefaultPort ? (origin.Scheme == "https" ? 443 : 80) : origin.Port;
        var requestPort = requestHost.Port ?? (context.Request.IsHttps ? 443 : 80);
        return originPort == requestPort;
    }

    private static bool IsSafeInlineContentType(string contentType)
    {
        var normalized = contentType.Split(';', 2)[0].Trim().ToLowerInvariant();
        return normalized == "application/pdf"
            || normalized == "text/plain"
            || normalized.StartsWith("audio/", StringComparison.Ordinal)
            || normalized.StartsWith("video/", StringComparison.Ordinal)
            || normalized.StartsWith("image/", StringComparison.Ordinal) && normalized != "image/svg+xml";
    }

    private static string ResolveClientId(string? value)
    {
        var candidate = value?.Trim();
        return candidate is { Length: > 0 and <= 128 }
            && candidate.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
                ? candidate
                : Guid.NewGuid().ToString("D");
    }

    private void WriteAccessDescription()
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up
                && adapter.NetworkInterfaceType is not NetworkInterfaceType.Loopback)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Select(entry => entry.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
            .Distinct()
            .Select(address => address.ToString())
            .ToArray();
        var payload = JsonSerializer.Serialize(new
        {
            port = _port,
            urls = addresses.Select(address => $"http://{address}:{_gatewayPort}/assistant/").ToArray(),
            directUrls = addresses.Select(address => $"http://{address}:{_port}/").ToArray(),
            localUrl = $"http://127.0.0.1:{_gatewayPort}/assistant/",
            directLocalUrl = $"http://127.0.0.1:{_port}/",
            generatedAtUtc = DateTimeOffset.UtcNow,
        }, AccessJsonOptions);
        File.WriteAllText(AccessFilePath, payload, new UTF8Encoding(false));
    }
}

internal sealed record LanArtifactResource(Stream Content, string ContentType, string FileName);

internal sealed record LanWebSocketClient(string ClientId, WebSocket Socket);

internal sealed record LanEventClient(string ClientId, Channel<string> Channel);
