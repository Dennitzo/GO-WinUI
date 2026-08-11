using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GoWinUI.BricsCad.Protocol;

public interface IBricsCadBridgeHost : IAsyncDisposable
{
    event EventHandler<BridgeConnectionChangedEventArgs>? ConnectionChanged;
    event EventHandler<BridgeEventReceivedEventArgs>? EventReceived;
    event EventHandler<BridgeCapabilitiesChangedEventArgs>? CapabilitiesChanged;
    event EventHandler<BridgeDiagnosticEventArgs>? Diagnostic;

    bool IsConnected { get; }
    BridgeHelloMessage? RemoteHello { get; }
    BridgeRendezvousDescriptor? Rendezvous { get; }
    JsonObject? Capabilities { get; }

    Task StartAsync(string? appBuildId = null, CancellationToken cancellationToken = default);
    Task<BridgeResponseMessage> RequestAsync(
        string method,
        JsonObject? parameters = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
    Task<JsonObject> RefreshCapabilitiesAsync(CancellationToken cancellationToken = default);
    Task StopAsync();
}

public sealed class BridgeConnectionChangedEventArgs : EventArgs
{
    public BridgeConnectionChangedEventArgs(bool connected, BridgeHelloMessage? hello, string? reason)
    {
        Connected = connected;
        Hello = hello;
        Reason = reason;
    }

    public bool Connected { get; }
    public BridgeHelloMessage? Hello { get; }
    public string? Reason { get; }
}

public sealed class BridgeEventReceivedEventArgs : EventArgs
{
    public BridgeEventReceivedEventArgs(string name, JsonObject message)
    {
        Name = name;
        Message = message;
    }

    public string Name { get; }
    public JsonObject Message { get; }
}

public sealed class BridgeCapabilitiesChangedEventArgs : EventArgs
{
    public BridgeCapabilitiesChangedEventArgs(JsonObject capabilities)
    {
        Capabilities = capabilities;
    }

    public JsonObject Capabilities { get; }
}

public sealed class BridgeDiagnosticEventArgs : EventArgs
{
    public BridgeDiagnosticEventArgs(string message, Exception? exception = null)
    {
        Message = message;
        Exception = exception;
    }

    public string Message { get; }
    public Exception? Exception { get; }
}

public sealed class BridgeRemoteException : IOException
{
    public BridgeRemoteException(BridgeResponseMessage response)
        : base(response.Error ?? response.ErrorCode ?? "BricsCAD bridge request failed.")
    {
        Response = response;
    }

    public BridgeResponseMessage Response { get; }
}

public sealed class BricsCadBridgeHost : IBricsCadBridgeHost
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CapabilityTimeout = TimeSpan.FromSeconds(15);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<BridgeResponseMessage>> _pending = new();
    private readonly object _stateLock = new();
    private TcpListener? _listener;
    private Task? _acceptTask;
    private ConnectionState? _connection;
    private BridgeRendezvousDescriptor? _rendezvous;
    private JsonObject? _capabilities;
    private int _nextRequestId;
    private bool _started;
    private bool _stopped;
    private bool _disposed;

    public event EventHandler<BridgeConnectionChangedEventArgs>? ConnectionChanged;
    public event EventHandler<BridgeEventReceivedEventArgs>? EventReceived;
    public event EventHandler<BridgeCapabilitiesChangedEventArgs>? CapabilitiesChanged;
    public event EventHandler<BridgeDiagnosticEventArgs>? Diagnostic;

    public bool IsConnected
    {
        get
        {
            lock (_stateLock)
            {
                return _connection is not null;
            }
        }
    }

    public BridgeHelloMessage? RemoteHello
    {
        get
        {
            lock (_stateLock)
            {
                return _connection?.Hello;
            }
        }
    }

    public BridgeRendezvousDescriptor? Rendezvous => _rendezvous;

    public JsonObject? Capabilities
    {
        get
        {
            lock (_stateLock)
            {
                return _capabilities?.DeepClone().AsObject();
            }
        }
    }

    public async Task StartAsync(
        string? appBuildId = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            throw new InvalidOperationException("The BricsCAD bridge host has already been started.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Server.ExclusiveAddressUse = true;
        listener.Start();

        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            BridgeRendezvousDescriptor rendezvous = BridgeRendezvousFile.Create(port, appBuildId);
            await BridgeRendezvousFile.WriteAsync(rendezvous, cancellationToken).ConfigureAwait(false);
            _listener = listener;
            _rendezvous = rendezvous;
            _started = true;
            _acceptTask = AcceptLoopAsync(listener);
        }
        catch
        {
            listener.Stop();
            throw;
        }
    }

    public async Task<BridgeResponseMessage> RequestAsync(
        string method,
        JsonObject? parameters = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        if (effectiveTimeout <= TimeSpan.Zero && effectiveTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        ConnectionState connection = GetConnection();
        int id = NextRequestId();
        var completion = new TaskCompletionSource<BridgeResponseMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException("Unable to allocate a bridge request id.");
        }

        var request = new BridgeRequestMessage
        {
            Id = id,
            Method = method,
            Parameters = parameters?.DeepClone().AsObject() ?? new JsonObject()
        };

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token,
            connection.Closed.Token);
        try
        {
            await connection.WriteAsync(request, linked.Token).ConfigureAwait(false);
            return await completion.Task.WaitAsync(effectiveTimeout, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async Task<JsonObject> RefreshCapabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        BridgeResponseMessage response = await RequestAsync(
            "capabilities.list",
            new JsonObject(),
            CapabilityTimeout,
            cancellationToken).ConfigureAwait(false);
        if (!response.Ok || response.Result is not JsonObject capabilities)
        {
            throw new BridgeRemoteException(response);
        }
        if (!string.Equals(StringValue(capabilities, "provider"), BridgeProtocol.Provider, StringComparison.Ordinal)
            || !string.Equals(
                StringValue(capabilities, "contractVersion"),
                BridgeProtocol.ContractVersion,
                StringComparison.Ordinal)
            || !ContractIdentity.MatchesSha256(StringValue(capabilities, "contractHash")))
        {
            throw new BridgeProtocolException("Plugin capabilities do not match the authenticated contract.");
        }

        JsonObject snapshot = capabilities.DeepClone().AsObject();
        lock (_stateLock)
        {
            _capabilities = snapshot;
        }

        CapabilitiesChanged?.Invoke(
            this,
            new BridgeCapabilitiesChangedEventArgs(snapshot.DeepClone().AsObject()));
        return snapshot.DeepClone().AsObject();
    }

    public async Task StopAsync()
    {
        if (!_started || _stopped)
        {
            return;
        }

        _lifetime.Cancel();
        _listener?.Stop();
        ConnectionState? connection;
        lock (_stateLock)
        {
            connection = _connection;
            _connection = null;
            _capabilities = null;
        }

        connection?.Dispose();
        FailPending(new OperationCanceledException("BricsCAD bridge host stopped."));
        if (_acceptTask is not null)
        {
            try
            {
                await _acceptTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
        }

        if (_rendezvous is not null)
        {
            BridgeRendezvousFile.TryDeleteOwned(_rendezvous.Token);
        }

        _rendezvous = null;
        _listener = null;
        _stopped = true;
    }

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        while (!_lifetime.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await listener.AcceptTcpClientAsync(_lifetime.Token).ConfigureAwait(false);
                client.NoDelay = true;
                await HandleConnectionAsync(client).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                client?.Dispose();
                break;
            }
            catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested)
            {
                client?.Dispose();
                break;
            }
            catch (Exception exception)
            {
                client?.Dispose();
                Diagnostic?.Invoke(
                    this,
                    new BridgeDiagnosticEventArgs("BricsCAD bridge connection failed.", exception));
            }
        }
    }

    private async Task HandleConnectionAsync(TcpClient client)
    {
        BridgeRendezvousDescriptor rendezvous = _rendezvous
            ?? throw new InvalidOperationException("Bridge rendezvous is unavailable.");
        using (client)
        using (NetworkStream stream = client.GetStream())
        using (var handshakeCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
        {
            handshakeCancellation.CancelAfter(HandshakeTimeout);
            var reader = new BridgeFrameReader();
            JsonObject helloJson = await reader.ReadObjectAsync(stream, handshakeCancellation.Token).ConfigureAwait(false)
                ?? throw new BridgeProtocolException("Plugin disconnected before its hello message.");
            BridgeHelloMessage hello = DeserializeHello(helloJson);
            ValidateHello(hello, rendezvous);

            var connection = new ConnectionState(client, stream, reader, hello);
            lock (_stateLock)
            {
                _connection?.Dispose();
                _connection = connection;
                _capabilities = null;
            }

            await connection.WriteObjectAsync(new JsonObject
            {
                ["type"] = "event",
                ["event"] = "hello.ok",
                ["token"] = rendezvous.Token,
                ["provider"] = BridgeProtocol.Provider,
                ["protocol"] = BridgeProtocol.Version,
                ["bridgeBuild"] = BridgeProtocol.BridgeBuild,
                ["contractVersion"] = BridgeProtocol.ContractVersion,
                ["contractHash"] = ContractIdentity.Sha256,
                ["appBuildId"] = rendezvous.AppBuildId
            }, _lifetime.Token).ConfigureAwait(false);

            ConnectionChanged?.Invoke(this, new BridgeConnectionChangedEventArgs(true, hello, null));
            Task readTask = ReadLoopAsync(connection);
            try
            {
                await RefreshCapabilitiesAsync(_lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Diagnostic?.Invoke(
                    this,
                    new BridgeDiagnosticEventArgs("BricsCAD capabilities could not be loaded.", exception));
            }

            string reason = "Plugin connection closed.";
            try
            {
                await readTask.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                reason = exception.Message;
                Diagnostic?.Invoke(
                    this,
                    new BridgeDiagnosticEventArgs("BricsCAD bridge read loop failed.", exception));
            }
            finally
            {
                bool wasCurrent;
                lock (_stateLock)
                {
                    wasCurrent = ReferenceEquals(_connection, connection);
                    if (wasCurrent)
                    {
                        _connection = null;
                        _capabilities = null;
                    }
                }

                connection.Dispose();
                if (wasCurrent)
                {
                    FailPending(new IOException(reason));
                    ConnectionChanged?.Invoke(
                        this,
                        new BridgeConnectionChangedEventArgs(false, hello, reason));
                }
            }
        }
    }

    private async Task ReadLoopAsync(ConnectionState connection)
    {
        while (!_lifetime.IsCancellationRequested && !connection.Closed.IsCancellationRequested)
        {
            JsonObject? message = await connection.Reader.ReadObjectAsync(
                connection.Stream,
                _lifetime.Token).ConfigureAwait(false);
            if (message is null)
            {
                return;
            }

            string type = StringValue(message, "type");
            if (type == "response")
            {
                BridgeResponseMessage response = message.Deserialize<BridgeResponseMessage>(BridgeProtocol.JsonOptions)
                    ?? throw new BridgeProtocolException("Plugin response is invalid.");
                if (_pending.TryRemove(response.Id, out TaskCompletionSource<BridgeResponseMessage>? completion))
                {
                    completion.TrySetResult(response);
                }

                continue;
            }

            if (type == "event")
            {
                string eventName = StringValue(message, "event");
                if (!string.IsNullOrWhiteSpace(eventName))
                {
                    EventReceived?.Invoke(
                        this,
                        new BridgeEventReceivedEventArgs(eventName, message.DeepClone().AsObject()));
                }

                continue;
            }

            throw new BridgeProtocolException($"Unexpected authenticated bridge message type '{type}'.");
        }
    }

    private static BridgeHelloMessage DeserializeHello(JsonObject message)
    {
        if (!string.Equals(StringValue(message, "type"), "hello", StringComparison.Ordinal))
        {
            throw new BridgeProtocolException("First plugin frame must be hello.");
        }

        try
        {
            return message.Deserialize<BridgeHelloMessage>(BridgeProtocol.JsonOptions)
                ?? throw new BridgeProtocolException("Plugin hello is invalid.");
        }
        catch (JsonException exception)
        {
            throw new BridgeProtocolException("Plugin hello is invalid.", exception);
        }
    }

    private static void ValidateHello(
        BridgeHelloMessage hello,
        BridgeRendezvousDescriptor rendezvous)
    {
        bool valid = BridgeRendezvousFile.SecureEquals(hello.Token, rendezvous.Token)
            && hello.Protocol == BridgeProtocol.Version
            && string.Equals(hello.Provider, BridgeProtocol.Provider, StringComparison.Ordinal)
            && string.Equals(hello.BridgeBuild, BridgeProtocol.BridgeBuild, StringComparison.Ordinal)
            && string.Equals(hello.ContractVersion, BridgeProtocol.ContractVersion, StringComparison.Ordinal)
            && ContractIdentity.MatchesSha256(hello.ContractHash)
            && !string.IsNullOrWhiteSpace(hello.RuntimeInstanceId)
            && !string.IsNullOrWhiteSpace(hello.PluginBuildId)
            && !string.IsNullOrWhiteSpace(hello.PluginVersion)
            && hello.BimCreateRevision >= BridgeProtocol.MinimumBimCreateRevision
            && hello.BricsCadVersion.StartsWith("V26", StringComparison.OrdinalIgnoreCase);
        if (!valid)
        {
            throw new BridgeProtocolException("Plugin hello failed token, protocol or contract validation.");
        }
    }

    private ConnectionState GetConnection()
    {
        lock (_stateLock)
        {
            return _connection
                ?? throw new InvalidOperationException("BricsCAD plugin is not connected.");
        }
    }

    private int NextRequestId()
    {
        int id = Interlocked.Increment(ref _nextRequestId);
        if (id > 0)
        {
            return id;
        }

        Interlocked.Exchange(ref _nextRequestId, 1);
        return 1;
    }

    private void FailPending(Exception exception)
    {
        foreach ((int id, TaskCompletionSource<BridgeResponseMessage> completion) in _pending)
        {
            if (_pending.TryRemove(id, out _))
            {
                completion.TrySetException(exception);
            }
        }
    }

    private static string StringValue(JsonObject value, string key)
    {
        return value[key]?.GetValue<string>()?.Trim() ?? string.Empty;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private sealed class ConnectionState : IDisposable
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private int _disposed;

        public ConnectionState(
            TcpClient client,
            NetworkStream stream,
            BridgeFrameReader reader,
            BridgeHelloMessage hello)
        {
            Client = client;
            Stream = stream;
            Reader = reader;
            Hello = hello;
        }

        public TcpClient Client { get; }
        public NetworkStream Stream { get; }
        public BridgeFrameReader Reader { get; }
        public BridgeHelloMessage Hello { get; }
        public CancellationTokenSource Closed { get; } = new();

        public async Task WriteAsync<T>(T message, CancellationToken cancellationToken)
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await BridgeJsonFraming.WriteAsync(Stream, message, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public Task WriteObjectAsync(JsonObject message, CancellationToken cancellationToken)
        {
            return WriteAsync(message, cancellationToken);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (!Closed.IsCancellationRequested)
            {
                Closed.Cancel();
            }

            Client.Dispose();
            Closed.Dispose();
            _writeLock.Dispose();
        }
    }
}
