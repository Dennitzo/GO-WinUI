using System.Text.Json;
using System.Text.Json.Serialization;

namespace GoWinUI.Core.Extensions;

public sealed record ExtensionHostMessage(
    int ProtocolVersion,
    string Type,
    string RequestId,
    JsonElement Payload);

public sealed record ExtensionHostHello(
    string Protocol,
    string ExtensionId,
    string PackageSha256,
    string Challenge);

public sealed record ExtensionHostReady(
    string Protocol,
    string ExtensionId,
    string PackageSha256,
    string Challenge,
    int ProcessId,
    string[] Capabilities);

public sealed record ExtensionHostError(
    string Code,
    string Message);

public sealed record ExtensionHostIdentity(
    string ExtensionId,
    string PackageSha256);

public static class ExtensionHostMessageTypes
{
    public const string Hello = "host.hello";
    public const string Ready = "host.ready";
    public const string InvokeAction = "action.invoke";
    public const string ActionResult = "action.result";
    public const string Shutdown = "host.shutdown";
    public const string Stopped = "host.stopped";
    public const string Error = "host.error";
}

public static class ExtensionHostProtocol
{
    public const int CurrentVersion = 1;
    public const string Name = "extension-host.v1";
    // Tool descriptors may explicitly allow up to 64 MiB of structured output.
    // Reserve framing headroom so the supervising process can carry that result
    // without silently imposing a smaller protocol-wide limit.
    public const int MaximumLineLength = 65 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public static ExtensionHostMessage Create<T>(string type, string requestId, T payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(payload);
        return new(CurrentVersion, type, requestId, JsonSerializer.SerializeToElement(payload, JsonOptions));
    }

    public static ExtensionHostMessage CreateHello(
        ExtensionHostIdentity identity,
        string requestId,
        string challenge) =>
        Create(ExtensionHostMessageTypes.Hello, requestId,
            new ExtensionHostHello(Name, identity.ExtensionId, identity.PackageSha256, challenge));

    public static string SerializeLine(ExtensionHostMessage message)
    {
        ValidateMessage(message);
        return JsonSerializer.Serialize(message, JsonOptions);
    }

    public static ExtensionHostMessage ParseLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) throw new InvalidDataException("Die ExtensionHost-Nachricht ist leer.");
        if (line.Length > MaximumLineLength) throw new InvalidDataException("Die ExtensionHost-Nachricht überschreitet das Größenlimit.");
        try
        {
            var message = JsonSerializer.Deserialize<ExtensionHostMessage>(line, JsonOptions)
                ?? throw new InvalidDataException("Die ExtensionHost-Nachricht ist leer.");
            ValidateMessage(message);
            return message;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Die ExtensionHost-Nachricht ist kein gültiges JSON-Lines-Objekt.", exception);
        }
    }

    public static T ReadPayload<T>(ExtensionHostMessage message)
    {
        ValidateMessage(message);
        try
        {
            return message.Payload.Deserialize<T>(JsonOptions)
                ?? throw new InvalidDataException("Die ExtensionHost-Nutzlast ist leer.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Die ExtensionHost-Nutzlast ist ungültig.", exception);
        }
    }

    public static ExtensionHostMessage AcceptHello(
        ExtensionHostMessage message,
        ExtensionHostIdentity expectedIdentity,
        int processId,
        IReadOnlyCollection<string>? capabilities = null)
    {
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        if (!string.Equals(message.Type, ExtensionHostMessageTypes.Hello, StringComparison.Ordinal))
            throw new InvalidDataException($"Erste ExtensionHost-Nachricht muss '{ExtensionHostMessageTypes.Hello}' sein.");
        var hello = ReadPayload<ExtensionHostHello>(message);
        ValidateHello(hello, expectedIdentity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        var advertised = capabilities?.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() ?? [];
        return Create(ExtensionHostMessageTypes.Ready, message.RequestId,
            new ExtensionHostReady(Name, expectedIdentity.ExtensionId, expectedIdentity.PackageSha256,
                hello.Challenge, processId, advertised));
    }

    public static ExtensionHostReady ValidateReady(
        ExtensionHostMessage message,
        ExtensionHostIdentity expectedIdentity,
        string expectedRequestId,
        string expectedChallenge)
    {
        if (!string.Equals(message.Type, ExtensionHostMessageTypes.Ready, StringComparison.Ordinal)
            || !string.Equals(message.RequestId, expectedRequestId, StringComparison.Ordinal))
            throw new InvalidDataException("ExtensionHost hat die Handshake-Anfrage nicht korrekt beantwortet.");
        var ready = ReadPayload<ExtensionHostReady>(message);
        if (!string.Equals(ready.Protocol, Name, StringComparison.Ordinal)
            || !string.Equals(ready.ExtensionId, expectedIdentity.ExtensionId, StringComparison.Ordinal)
            || !FixedTimeSha256Equals(ready.PackageSha256, expectedIdentity.PackageSha256)
            || !string.Equals(ready.Challenge, expectedChallenge, StringComparison.Ordinal)
            || ready.ProcessId <= 0)
            throw new InvalidDataException("ExtensionHost-Identität oder Challenge stimmt nicht mit dem gestarteten Prozess überein.");
        return ready;
    }

    private static void ValidateHello(ExtensionHostHello hello, ExtensionHostIdentity expectedIdentity)
    {
        if (!string.Equals(hello.Protocol, Name, StringComparison.Ordinal)
            || !string.Equals(hello.ExtensionId, expectedIdentity.ExtensionId, StringComparison.Ordinal)
            || !FixedTimeSha256Equals(hello.PackageSha256, expectedIdentity.PackageSha256)
            || hello.Challenge.Length is < 32 or > 256)
            throw new InvalidDataException("ExtensionHost-Hello enthält eine falsche Identität, Protokollversion oder Challenge.");
    }

    private static void ValidateMessage(ExtensionHostMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.ProtocolVersion != CurrentVersion)
            throw new InvalidDataException($"ExtensionHost-Protokollversion {message.ProtocolVersion} wird nicht unterstützt.");
        if (string.IsNullOrWhiteSpace(message.Type) || message.Type.Length > 80)
            throw new InvalidDataException("ExtensionHost-Nachrichtentyp fehlt oder ist zu lang.");
        if (string.IsNullOrWhiteSpace(message.RequestId) || message.RequestId.Length > 128)
            throw new InvalidDataException("ExtensionHost-Request-ID fehlt oder ist zu lang.");
        if (message.Payload.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("ExtensionHost-Nutzlast muss ein JSON-Objekt sein.");
    }

    private static bool FixedTimeSha256Equals(string left, string right)
    {
        if (!ExtensionHashes.IsSha256(left) || !ExtensionHashes.IsSha256(right)) return false;
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left), Convert.FromHexString(right));
    }
}
