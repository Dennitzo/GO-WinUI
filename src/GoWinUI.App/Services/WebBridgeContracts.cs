using System.Text.Json;

namespace GoWinUI.App.Services;

public sealed record WebBridgeEnvelope(
    int Version,
    string Type,
    string RequestId,
    JsonElement Payload);

public sealed class WebBridgeMessageEventArgs(WebBridgeEnvelope envelope) : EventArgs
{
    public WebBridgeEnvelope Envelope { get; } = envelope;
}

public sealed record ReadFromContextTarget(
    Guid SessionId,
    Guid MessageId,
    DateTimeOffset MessageUpdatedAt,
    string Kind,
    int BlockIndex);

public sealed class ReadFromContextRequestedEventArgs(ReadFromContextTarget target) : EventArgs
{
    public ReadFromContextTarget Target { get; } = target;
}
