using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bricscad.ApplicationServices;
using GoWinUI.BricsCad.Protocol;
using Teigha.Runtime;

namespace GoWinUI.BricsCad.Plugin;

public sealed class Plugin : IExtensionApplication
{
    private static BridgeClient? _bridge;

    public void Initialize()
    {
        _bridge = new BridgeClient();
        _bridge.Start();
    }

    public void Terminate()
    {
        _bridge?.Dispose();
        _bridge = null;
    }

    [CommandMethod("GOPING")]
    public void Ping()
    {
        Application.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(
            "\nGO BricsCAD .NET plugin ready");
    }
}

internal sealed class BridgeClient : IDisposable
{
    private static readonly TimeSpan MaximumReconnectDelay = TimeSpan.FromSeconds(30);
    private readonly CancellationTokenSource _stop = new();
    private readonly CadService _cad = new();
    private TcpClient? _client;
    private Task? _connectionTask;
    private string _lastDiscoveryError = string.Empty;
    private int _reconnectAttempt;

    public void Start()
    {
        _connectionTask = Task.Run(ConnectionLoopAsync);
    }

    private async Task ConnectionLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                if (!TryDiscoverEndpoint(out BridgeRendezvousDescriptor? endpoint) || endpoint is null)
                {
                    await DelayBeforeReconnectAsync().ConfigureAwait(false);
                    continue;
                }

                using var client = new TcpClient(endpoint.Host.Contains(':')
                    ? AddressFamily.InterNetworkV6
                    : AddressFamily.InterNetwork);
                _client = client;
                await client.ConnectAsync(
                    IPAddress.Parse(endpoint.Host),
                    endpoint.Port,
                    _stop.Token).ConfigureAwait(false);
                _lastDiscoveryError = string.Empty;
                _reconnectAttempt = 0;
                await HandleClientAsync(client, endpoint).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException exception)
            {
                CadService.Log($"Bridge connect failed: {exception.SocketErrorCode}");
            }
            catch (System.Exception exception)
            {
                CadService.Log($"Bridge connection failed: {exception}");
            }
            finally
            {
                _client = null;
            }

            await DelayBeforeReconnectAsync().ConfigureAwait(false);
        }
    }

    private bool TryDiscoverEndpoint(out BridgeRendezvousDescriptor? endpoint)
    {
        if (!BridgeRendezvousFile.TryRead(out endpoint, out string error))
        {
            LogDiscoveryError(error);
            return false;
        }

        if (endpoint is null || !BridgeRendezvousFile.IsOwnerProcessRunning(endpoint))
        {
            endpoint = null;
            LogDiscoveryError("Bridge rendezvous owner is not running.");
            return false;
        }

        return true;
    }

    private void LogDiscoveryError(string error)
    {
        if (string.Equals(error, _lastDiscoveryError, StringComparison.Ordinal))
        {
            return;
        }

        _lastDiscoveryError = error;
        CadService.Log($"Bridge discovery waiting at {BridgeRendezvousFile.ActivePath}: {error}");
    }

    private async Task DelayBeforeReconnectAsync()
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }

        try
        {
            var exponent = Math.Min(_reconnectAttempt++, 5);
            var seconds = Math.Min(MaximumReconnectDelay.TotalSeconds, Math.Pow(2, exponent));
            await Task.Delay(TimeSpan.FromSeconds(seconds), _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
    }

    private async Task HandleClientAsync(
        TcpClient client,
        BridgeRendezvousDescriptor endpoint)
    {
        using NetworkStream stream = client.GetStream();
        var reader = new BridgeFrameReader();
        bool authenticated = false;

        try
        {
            await BridgeJsonFraming.WriteAsync(stream, new BridgeHelloMessage
            {
                Token = endpoint.Token,
                PluginBuildId = BuildIdentity.Id,
                PluginVersion = BuildIdentity.Version,
                PluginBuiltAt = BuildIdentity.BuiltAt,
                RuntimeInstanceId = RuntimeIdentity.Id,
                BricsCadVersion =
                    $"V{Application.Version.Major}.{Application.Version.Minor}.{Application.Version.Build}",
                DotNetRuntime = Environment.Version.ToString(),
                ModulePath = typeof(Plugin).Assembly.Location,
                BimCreateRevision = BuildIdentity.BimCreateRevision
            }, _stop.Token).ConfigureAwait(false);

            while (!_stop.IsCancellationRequested && client.Connected)
            {
                JsonObject? request = await reader.ReadObjectAsync(stream, _stop.Token).ConfigureAwait(false);
                if (request is null)
                {
                    break;
                }

                string type = StringValue(request, "type");
                if (!authenticated)
                {
                    authenticated = type == "event"
                        && string.Equals(StringValue(request, "event"), "hello.ok", StringComparison.Ordinal)
                        && AuthenticateHost(request, endpoint);
                    if (!authenticated)
                    {
                        await BridgeJsonFraming.WriteObjectAsync(
                            stream,
                            new JsonObject { ["type"] = "error", ["error"] = "invalid-host-handshake" },
                            _stop.Token).ConfigureAwait(false);
                        break;
                    }

                    continue;
                }

                if (type == "event")
                {
                    continue;
                }

                if (type != "request")
                {
                    continue;
                }

                JsonObject response = await DispatchAsync(request).ConfigureAwait(false);
                await BridgeJsonFraming.WriteObjectAsync(stream, response, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (BridgeProtocolException exception)
        {
            CadService.Log($"Bridge framing failed: {exception.Message}");
        }
        catch (System.Exception exception)
        {
            CadService.Log($"Bridge client failed: {exception}");
        }
    }

    private static bool AuthenticateHost(
        JsonObject request,
        BridgeRendezvousDescriptor endpoint)
    {
        return BridgeRendezvousFile.SecureEquals(StringValue(request, "token"), endpoint.Token)
            && IntegerValue(request, "protocol") == BridgeProtocol.Version
            && string.Equals(StringValue(request, "provider"), BridgeProtocol.Provider, StringComparison.Ordinal)
            && string.Equals(StringValue(request, "bridgeBuild"), BridgeProtocol.BridgeBuild, StringComparison.Ordinal)
            && string.Equals(
                StringValue(request, "contractVersion"),
                BridgeProtocol.ContractVersion,
                StringComparison.Ordinal)
            && ContractIdentity.MatchesSha256(StringValue(request, "contractHash"));
    }

    private async Task<JsonObject> DispatchAsync(JsonObject request)
    {
        int id = IntegerValue(request, "id");
        string method = StringValue(request, "method");
        JsonObject parameters = request["params"] as JsonObject ?? new JsonObject();
        try
        {
            if (method == "capabilities.list")
            {
                return Ok(id, CapabilityRegistry.Capabilities());
            }

            if (method == "actions.list")
            {
                return Ok(id, CapabilityRegistry.Actions());
            }

            if (method == "actions.validate")
            {
                return Ok(id, await _cad.ValidateAsync(parameters).ConfigureAwait(false));
            }

            if (!CapabilityRegistry.TryGetMethod(method, out _))
            {
                return Error(id, "unknown-method", $"Unbekannte BricsCAD-.NET-Methode: {method}");
            }

            JsonValidationResult validation = CapabilityRegistry.ValidateParameters(method, parameters);
            if (!validation.Valid)
            {
                return Error(id, "invalid-params", validation.Summary, validation.ToJson());
            }

            JsonObject result = await _cad.ExecuteAsync(method, parameters).ConfigureAwait(false);
            return Ok(id, result);
        }
        catch (ArgumentException exception)
        {
            return DispatchFailure(id, method, "invalid-operation-params", exception);
        }
        catch (KeyNotFoundException exception)
        {
            return DispatchFailure(id, method, "precondition-failed", exception);
        }
        catch (InvalidOperationException exception)
        {
            return DispatchFailure(id, method, "precondition-failed", exception);
        }
        catch (IOException exception)
        {
            return DispatchFailure(id, method, "io-failure", exception);
        }
        catch (System.Exception exception)
        {
            return DispatchFailure(id, method, "execution-failed", exception);
        }
    }

    private static JsonObject DispatchFailure(
        int id,
        string method,
        string errorCode,
        System.Exception exception)
    {
        CadService.Log($"{method} failed ({errorCode}): {exception}");
        return Error(id, errorCode, exception.Message);
    }

    private static JsonObject Ok(int id, JsonNode result)
    {
        return new JsonObject
        {
            ["id"] = id,
            ["type"] = "response",
            ["ok"] = true,
            ["result"] = result
        };
    }

    private static JsonObject Error(
        int id,
        string code,
        string error,
        JsonNode? details = null)
    {
        var response = new JsonObject
        {
            ["id"] = id,
            ["type"] = "response",
            ["ok"] = false,
            ["error"] = error,
            ["errorCode"] = code,
            ["provider"] = CapabilityRegistry.Provider
        };
        if (details is not null)
        {
            response["details"] = details.DeepClone();
        }

        return response;
    }

    private static string StringValue(JsonObject value, string key)
    {
        return value[key]?.GetValue<string>()?.Trim() ?? string.Empty;
    }

    private static int IntegerValue(JsonObject value, string key)
    {
        return value[key]?.GetValue<int>() ?? 0;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _client?.Dispose();
        try
        {
            _connectionTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _stop.Dispose();
    }
}

internal static class BuildIdentity
{
    public static string Id => Metadata("GoBuildId", "unconfigured-build");
    public static string BuiltAt => Metadata("GoBuiltAt", "unknown");
    public static int BimCreateRevision =>
        int.TryParse(Metadata("GoBimCreateRevision", "0"), out int value) ? value : 0;
    public static string Version =>
        typeof(Plugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private static string Metadata(string key, string fallback)
    {
        return typeof(Plugin).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == key)?.Value ?? fallback;
    }
}

internal static class RuntimeIdentity
{
    public static string Id { get; } = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
}
