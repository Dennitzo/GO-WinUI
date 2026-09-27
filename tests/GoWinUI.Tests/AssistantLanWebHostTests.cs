using GoWinUI.App.Services;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace GoWinUI.Tests;

[Collection("Assistant LAN host")]
public sealed class AssistantLanWebHostTests
{
    [Theory]
    [InlineData("chat.started", false)]
    [InlineData("chat.delta", false)]
    [InlineData("status.changed", false)]
    [InlineData("conversation.messageCommitted", false)]
    [InlineData("coding.changes", false)]
    [InlineData("chat.completed", false)]
    [InlineData("conversation.snapshot", true)]
    [InlineData("workflow.snapshot", true)]
    [InlineData("memory.snapshot", true)]
    [InlineData("artifact.previewReady", true)]
    [InlineData("speech.status", true)]
    public void LiveConversationEventsAreSharedWhilePrivateUiRepliesStayClientScoped(
        string messageType,
        bool expectedClientScoped)
    {
        Assert.Equal(expectedClientScoped, AssistantWebBridge.IsClientScopedOutgoingType(messageType));
    }

    [Fact]
    public async Task BroadcastsConversationLifecycleToEveryLanClientAndKeepsDialogRepliesClientLocal()
    {
        var root = Path.Combine(Path.GetTempPath(), "assistant-lan-realtime-" + Guid.NewGuid().ToString("N"));
        var web = Path.Combine(root, "web");
        Directory.CreateDirectory(web);
        await File.WriteAllTextAsync(Path.Combine(web, "index.html"), "<!doctype html><title>LAN Realtime</title>");
        var proxyBase = Environment.GetEnvironmentVariable("ASSISTANT_LAN_PROXY_TEST_URL")?.TrimEnd('/');
        var port = proxyBase is null ? GetFreePort() : 8090;
        var previousPort = Environment.GetEnvironmentVariable("ASSISTANT_LAN_WEB_PORT");
        Environment.SetEnvironmentVariable("ASSISTANT_LAN_WEB_PORT", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            await using var host = new AssistantLanWebHost(root, NullLogger.Instance);
            await host.StartAsync(web);
            using var access = JsonDocument.Parse(await File.ReadAllTextAsync(host.AccessFilePath));
            var browserUrl = proxyBase ?? access.RootElement.GetProperty("directLocalUrl").GetString()!.TrimEnd('/');
            var browserUri = new Uri(browserUrl + "/");
            var socketScheme = browserUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
            var bridgePath = proxyBase is null ? "/bridge" : "/assistant/bridge";
            using var first = new ClientWebSocket();
            using var second = new ClientWebSocket();
            await first.ConnectAsync(
                new Uri($"{socketScheme}://{browserUri.Authority}{bridgePath}?clientId=client-a"),
                CancellationToken.None);
            await second.ConnectAsync(
                new Uri($"{socketScheme}://{browserUri.Authority}{bridgePath}?clientId=client-b"),
                CancellationToken.None);

            var lifecycle = new[]
            {
                "{\"version\":1,\"type\":\"chat.started\",\"requestId\":\"host-run\",\"payload\":{\"sessionId\":\"session-a\"}}",
                "{\"version\":1,\"type\":\"chat.delta\",\"requestId\":\"host-run\",\"payload\":{\"messageId\":\"answer-a\",\"content\":\"Teil\"}}",
                "{\"version\":1,\"type\":\"status.changed\",\"requestId\":\"host-run\",\"payload\":{\"messageId\":\"answer-a\",\"toolStep\":{\"id\":\"tool-a\"}}}",
                "{\"version\":1,\"type\":\"conversation.messageCommitted\",\"requestId\":\"host-run\",\"payload\":{\"message\":{\"id\":\"answer-a\"}}}",
                "{\"version\":1,\"type\":\"chat.completed\",\"requestId\":\"host-run\",\"payload\":{\"message\":{\"id\":\"answer-a\",\"status\":\"completed\"}}}",
            };
            foreach (var outgoing in lifecycle)
            {
                await host.BroadcastAsync(outgoing);
                Assert.Equal(outgoing, await ReceiveTextAsync(first));
                Assert.Equal(outgoing, await ReceiveTextAsync(second));
            }

            const string localDialog = "{\"version\":1,\"type\":\"workflow.snapshot\",\"requestId\":\"dialog-a\",\"payload\":{}}";
            await host.SendToClientAsync("client-a", localDialog);
            Assert.Equal(localDialog, await ReceiveTextAsync(first));

            const string nextBroadcast = "{\"version\":1,\"type\":\"queue.changed\",\"requestId\":\"host-run\",\"payload\":{}}";
            await host.BroadcastAsync(nextBroadcast);
            Assert.Equal(nextBroadcast, await ReceiveTextAsync(first));
            Assert.Equal(nextBroadcast, await ReceiveTextAsync(second));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASSISTANT_LAN_WEB_PORT", previousPort);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task IdentityEndpointBindsTheLanHostToItsProfileProcessAndLaunchNonce()
    {
        var root = Path.Combine(Path.GetTempPath(), "assistant-lan-identity-" + Guid.NewGuid().ToString("N"));
        var web = Path.Combine(root, "web");
        Directory.CreateDirectory(web);
        await File.WriteAllTextAsync(Path.Combine(web, "index.html"), "<!doctype html><title>Identity</title>");
        var port = GetFreePort();
        var nonce = Guid.NewGuid().ToString("N");
        var previousPort = Environment.GetEnvironmentVariable("ASSISTANT_LAN_WEB_PORT");
        var previousProfile = Environment.GetEnvironmentVariable("ASSISTANT_PROFILE");
        var previousNonce = Environment.GetEnvironmentVariable("ASSISTANT_LAUNCH_NONCE");
        Environment.SetEnvironmentVariable("ASSISTANT_LAN_WEB_PORT", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("ASSISTANT_PROFILE", "stable");
        Environment.SetEnvironmentVariable("ASSISTANT_LAUNCH_NONCE", nonce);
        try
        {
            await using var host = new AssistantLanWebHost(root, NullLogger.Instance);
            await host.StartAsync(web);
            using var http = new HttpClient();
            using var identity = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/health/identity"));

            Assert.Equal("stable", identity.RootElement.GetProperty("profile").GetString());
            Assert.Equal(Environment.ProcessId, identity.RootElement.GetProperty("processId").GetInt32());
            Assert.Equal(nonce, identity.RootElement.GetProperty("launchNonce").GetString());
            Assert.Matches("^[a-f0-9]{64}$", identity.RootElement.GetProperty("executableSha256").GetString()!);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASSISTANT_LAN_WEB_PORT", previousPort);
            Environment.SetEnvironmentVariable("ASSISTANT_PROFILE", previousProfile);
            Environment.SetEnvironmentVariable("ASSISTANT_LAUNCH_NONCE", previousNonce);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ServesPrivateLanAssetsWithoutKeyAndBridgesMessagesBothWays()
    {
        var root = Path.Combine(Path.GetTempPath(), "assistant-lan-" + Guid.NewGuid().ToString("N"));
        var web = Path.Combine(root, "web");
        var previews = Path.Combine(root, "previews");
        Directory.CreateDirectory(web);
        Directory.CreateDirectory(previews);
        await File.WriteAllTextAsync(Path.Combine(web, "index.html"), "<!doctype html><title>LAN Assistant</title>");
        await File.WriteAllTextAsync(Path.Combine(previews, "preview.txt"), "preview-data");
        var proxyBase = Environment.GetEnvironmentVariable("ASSISTANT_LAN_PROXY_TEST_URL")?.TrimEnd('/');
        var port = proxyBase is null ? GetFreePort() : 8090;
        var previousPort = Environment.GetEnvironmentVariable("ASSISTANT_LAN_WEB_PORT");
        Environment.SetEnvironmentVariable("ASSISTANT_LAN_WEB_PORT", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            await using var host = new AssistantLanWebHost(root, NullLogger.Instance);
            var artifactId = Guid.NewGuid();
            var unsafeArtifactId = Guid.NewGuid();
            host.ArtifactResolver = (id, _) => Task.FromResult<LanArtifactResource?>(id switch
            {
                _ when id == artifactId => new LanArtifactResource(
                    new MemoryStream(Encoding.UTF8.GetBytes("artifact-data")), "text/plain", "result.txt"),
                _ when id == unsafeArtifactId => new LanArtifactResource(
                    new MemoryStream(Encoding.UTF8.GetBytes("<script>alert(1)</script>")), "text/html", "result.html"),
                _ => null,
            });
            host.CodingPreviewResolver = (_, stepId, _) => Task.FromResult<string?>(
                stepId == "html-step" ? "<!doctype html><title>Isolated preview</title>" : null);
            await host.StartAsync(web, previews);
            using var access = JsonDocument.Parse(await File.ReadAllTextAsync(host.AccessFilePath));
            var localUrl = access.RootElement.GetProperty("directLocalUrl").GetString()!;
            using var http = new HttpClient();
            Assert.DoesNotContain("token=", localUrl, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("LAN Assistant", await http.GetStringAsync(localUrl), StringComparison.Ordinal);
            var lanUrls = access.RootElement.GetProperty("directUrls").EnumerateArray().Select(item => item.GetString()).Where(item => item is not null).ToArray();
            if (lanUrls.Length > 0)
                Assert.Contains("LAN Assistant", await http.GetStringAsync(lanUrls[0]!), StringComparison.Ordinal);

            var browserUrl = proxyBase is null ? localUrl : $"{proxyBase}/";
            if (proxyBase is not null)
            {
                Assert.Contains("LAN Assistant", await http.GetStringAsync(browserUrl), StringComparison.Ordinal);
            }

            Assert.Equal("preview-data", await http.GetStringAsync(new Uri(new Uri(browserUrl), "preview/preview.txt")));
            using (var artifactResponse = await http.GetAsync(new Uri(new Uri(browserUrl), $"artifacts/{artifactId:D}?download=1")))
            {
                artifactResponse.EnsureSuccessStatusCode();
                Assert.Equal("artifact-data", await artifactResponse.Content.ReadAsStringAsync());
                Assert.Contains("result.txt", artifactResponse.Content.Headers.ContentDisposition?.FileNameStar
                    ?? artifactResponse.Content.Headers.ContentDisposition?.FileName ?? string.Empty, StringComparison.Ordinal);
            }
            using (var previewResponse = await http.GetAsync(new Uri(new Uri(browserUrl), $"coding-preview/{Guid.NewGuid():D}/html-step")))
            {
                previewResponse.EnsureSuccessStatusCode();
                Assert.Contains("Isolated preview", await previewResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
                Assert.Contains("connect-src 'none'", previewResponse.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
            }
            using (var unsafeResponse = await http.GetAsync(new Uri(new Uri(browserUrl), $"artifacts/{unsafeArtifactId:D}")))
            {
                unsafeResponse.EnsureSuccessStatusCode();
                Assert.Equal("application/octet-stream", unsafeResponse.Content.Headers.ContentType?.MediaType);
                Assert.NotNull(unsafeResponse.Content.Headers.ContentDisposition);
            }

            using (var foreignRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(browserUrl), "message")))
            {
                foreignRequest.Headers.Add("Origin", "http://foreign.invalid");
                foreignRequest.Content = new StringContent(
                    "{\"version\":1,\"type\":\"app.ready\",\"requestId\":\"foreign\",\"payload\":{}}",
                    Encoding.UTF8,
                    "application/json");
                using var foreignResponse = await http.SendAsync(foreignRequest);
                Assert.Equal(HttpStatusCode.Forbidden, foreignResponse.StatusCode);
            }

            var posted = new TaskCompletionSource<WebBridgeEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.MessageReceived += (_, args) => posted.TrySetResult(args.Envelope);
            using (var postContent = new StringContent(
                "{\"version\":1,\"type\":\"app.ready\",\"requestId\":\"lan-http-test\",\"payload\":{},\"clientId\":\"client-http\"}",
                Encoding.UTF8,
                "application/json"))
            {
                using var postResponse = await http.PostAsync(new Uri(new Uri(browserUrl), "message"), postContent);
                Assert.Equal(HttpStatusCode.Accepted, postResponse.StatusCode);
            }
            var postedEnvelope = await posted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("app.ready", postedEnvelope.Type);
            Assert.Equal("client-http", postedEnvelope.ClientId);

            using var socket = new ClientWebSocket();
            var browserUri = new Uri(browserUrl);
            var socketScheme = browserUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
            var socketPath = proxyBase is null ? "/bridge?clientId=client-a" : "/assistant/bridge?clientId=client-a";
            await socket.ConnectAsync(new Uri($"{socketScheme}://{browserUri.Authority}{socketPath}"), CancellationToken.None);
            var received = new TaskCompletionSource<WebBridgeEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.MessageReceived += (_, args) => received.TrySetResult(args.Envelope);
            var incoming = "{\"version\":1,\"type\":\"app.ready\",\"requestId\":\"lan-test\",\"payload\":{}}";
            await socket.SendAsync(Encoding.UTF8.GetBytes(incoming), WebSocketMessageType.Text, true, CancellationToken.None);
            var envelope = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("app.ready", envelope.Type);
            Assert.Equal("client-a", envelope.ClientId);

            using var eventCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var eventResponse = await http.GetAsync(
                new Uri(new Uri(browserUrl), "events?clientId=client-a"),
                HttpCompletionOption.ResponseHeadersRead,
                eventCancellation.Token);
            eventResponse.EnsureSuccessStatusCode();
            await using var eventStream = await eventResponse.Content.ReadAsStreamAsync(eventCancellation.Token);
            using var eventReader = new StreamReader(eventStream, Encoding.UTF8);
            const string outgoing = "{\"version\":1,\"type\":\"state.snapshot\",\"requestId\":\"lan-test\",\"payload\":{\"ok\":true}}";
            await host.BroadcastAsync(outgoing);
            var buffer = new byte[1024];
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            Assert.Equal(outgoing, Encoding.UTF8.GetString(buffer, 0, result.Count));
            string? eventLine;
            do
            {
                eventLine = await eventReader.ReadLineAsync(eventCancellation.Token);
            }
            while (eventLine is not null && !eventLine.StartsWith("data: ", StringComparison.Ordinal));
            Assert.Equal("data: " + outgoing, eventLine);

            const string targeted = "{\"version\":1,\"type\":\"workflow.snapshot\",\"requestId\":\"client-only\",\"payload\":{}}";
            await host.SendToClientAsync("client-a", targeted);
            result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            Assert.Equal(targeted, Encoding.UTF8.GetString(buffer, 0, result.Count));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASSISTANT_LAN_WEB_PORT", previousPort);
            Directory.Delete(root, true);
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<string> ReceiveTextAsync(ClientWebSocket socket)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var message = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellation.Token);
            Assert.Equal(WebSocketMessageType.Text, result.MessageType);
            message.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(message.ToArray());
    }
}

[CollectionDefinition("Assistant LAN host", DisableParallelization = true)]
public sealed class AssistantLanWebHostTestGroup;
