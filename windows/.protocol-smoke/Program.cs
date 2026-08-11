using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using GoWinUI.BricsCad.Protocol;

var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var capabilities = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var debugEvent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
await using var host = new BricsCadBridgeHost();
host.ConnectionChanged += (_, args) => { if (args.Connected) connected.TrySetResult(); };
host.CapabilitiesChanged += (_, _) => capabilities.TrySetResult();
host.EventReceived += (_, args) => { if (args.Name == "debug") debugEvent.TrySetResult(); };
await host.StartAsync("protocol-smoke");
BridgeRendezvousDescriptor endpoint = host.Rendezvous ?? throw new InvalidOperationException("No rendezvous.");
if (!File.Exists(BridgeRendezvousFile.ActivePath) || endpoint.Port == 0) throw new InvalidOperationException("Rendezvous was not published.");

using (var rejectedClient = new TcpClient(AddressFamily.InterNetwork))
{
    await rejectedClient.ConnectAsync(IPAddress.Loopback, endpoint.Port);
    using NetworkStream rejectedStream = rejectedClient.GetStream();
    await BridgeJsonFraming.WriteAsync(rejectedStream, CreateHello(new string('0', 64)));
    using var rejectionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    JsonObject? rejectedFrame = await new BridgeFrameReader().ReadObjectAsync(rejectedStream, rejectionTimeout.Token);
    if (rejectedFrame is not null) throw new InvalidOperationException("Invalid hello was not rejected.");
}

using var client = new TcpClient(AddressFamily.InterNetwork);
await client.ConnectAsync(IPAddress.Loopback, endpoint.Port);
using NetworkStream stream = client.GetStream();
var reader = new BridgeFrameReader();
var pluginLoop = Task.Run(async () =>
{
    await BridgeJsonFraming.WriteAsync(stream, CreateHello(endpoint.Token));

    bool sentDebug = false;
    bool hostAuthenticated = false;
    while (true)
    {
        JsonObject? message = await reader.ReadObjectAsync(stream);
        if (message is null) return;
        if (message["type"]?.GetValue<string>() == "event"
            && message["event"]?.GetValue<string>() == "hello.ok")
        {
            if (!BridgeRendezvousFile.SecureEquals(message["token"]?.GetValue<string>(), endpoint.Token)
                || message["protocol"]?.GetValue<int>() != BridgeProtocol.Version
                || message["provider"]?.GetValue<string>() != BridgeProtocol.Provider
                || message["bridgeBuild"]?.GetValue<string>() != BridgeProtocol.BridgeBuild
                || message["contractVersion"]?.GetValue<string>() != BridgeProtocol.ContractVersion
                || !ContractIdentity.MatchesSha256(message["contractHash"]?.GetValue<string>()))
            {
                throw new InvalidOperationException("Host hello was not authenticated.");
            }

            hostAuthenticated = true;
            continue;
        }

        if (message["type"]?.GetValue<string>() != "request") continue;
        if (!hostAuthenticated) throw new InvalidOperationException("Host sent a request before authentication.");
        int id = message["id"]?.GetValue<int>() ?? 0;
        string method = message["method"]?.GetValue<string>() ?? string.Empty;
        if (method == "smoke.timeout") continue;
        JsonNode result = method == "capabilities.list"
            ? new JsonObject
            {
                ["schema"] = "barebone.bricscad.capabilities.dotnet.v2",
                ["provider"] = BridgeProtocol.Provider,
                ["contractVersion"] = BridgeProtocol.ContractVersion,
                ["contractHash"] = ContractIdentity.Sha256,
                ["methods"] = new JsonArray()
            }
            : new JsonObject { ["echo"] = method };
        await BridgeJsonFraming.WriteObjectAsync(stream, new JsonObject
        {
            ["id"] = id,
            ["type"] = "response",
            ["ok"] = true,
            ["result"] = result
        });
        if (!sentDebug)
        {
            sentDebug = true;
            await BridgeJsonFraming.WriteObjectAsync(stream, new JsonObject
            {
                ["type"] = "event",
                ["event"] = "debug",
                ["message"] = "smoke"
            });
        }
    }
});

await connected.Task.WaitAsync(TimeSpan.FromSeconds(10));
await capabilities.Task.WaitAsync(TimeSpan.FromSeconds(10));
await debugEvent.Task.WaitAsync(TimeSpan.FromSeconds(10));
try
{
    _ = await host.RequestAsync("smoke.timeout", new JsonObject(), TimeSpan.FromMilliseconds(100));
    throw new InvalidOperationException("Request timeout was not enforced.");
}
catch (TimeoutException)
{
}
BridgeResponseMessage response = await host.RequestAsync(
    "smoke.echo",
    new JsonObject(),
    TimeSpan.FromSeconds(5));
if (!response.Ok || response.Result?["echo"]?.GetValue<string>() != "smoke.echo") throw new InvalidOperationException("Correlation failed.");
await host.StopAsync();
if (File.Exists(BridgeRendezvousFile.ActivePath)) throw new InvalidOperationException("Rendezvous cleanup failed.");
client.Dispose();
try { await pluginLoop; } catch (IOException) { } catch (ObjectDisposedException) { }
Console.WriteLine("Protocol host smoke passed.");

static BridgeHelloMessage CreateHello(string token)
{
    return new BridgeHelloMessage
    {
        Token = token,
        PluginBuildId = "smoke-plugin",
        PluginVersion = "2.0.0",
        PluginBuiltAt = DateTimeOffset.UtcNow.ToString("O"),
        RuntimeInstanceId = Guid.NewGuid().ToString("N"),
        BricsCadVersion = "V26.2.1",
        DotNetRuntime = Environment.Version.ToString(),
        ModulePath = "smoke.dll",
        BimCreateRevision = BridgeProtocol.MinimumBimCreateRevision
    };
}
