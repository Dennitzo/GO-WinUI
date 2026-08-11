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
