using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GoWinUI.BricsCad.Protocol;

public sealed record BridgeHelloMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "hello";

    [JsonPropertyName("token")]
    public required string Token { get; init; }

    [JsonPropertyName("plugin")]
    public string Plugin { get; init; } = "GOBricsCad.DotNet";

    [JsonPropertyName("provider")]
    public string Provider { get; init; } = BridgeProtocol.Provider;

    [JsonPropertyName("bridgeBuild")]
    public string BridgeBuild { get; init; } = BridgeProtocol.BridgeBuild;

    [JsonPropertyName("protocol")]
    public int Protocol { get; init; } = BridgeProtocol.Version;

    [JsonPropertyName("contractVersion")]
    public string ContractVersion { get; init; } = BridgeProtocol.ContractVersion;

    [JsonPropertyName("contractHash")]
    public string ContractHash { get; init; } = ContractIdentity.Sha256;

    [JsonPropertyName("pluginBuildId")]
    public required string PluginBuildId { get; init; }

    [JsonPropertyName("pluginVersion")]
    public required string PluginVersion { get; init; }

    [JsonPropertyName("pluginBuiltAt")]
    public required string PluginBuiltAt { get; init; }

    [JsonPropertyName("runtimeInstanceId")]
    public required string RuntimeInstanceId { get; init; }

    [JsonPropertyName("bricscadVersion")]
    public required string BricsCadVersion { get; init; }

    [JsonPropertyName("dotnetRuntime")]
    public required string DotNetRuntime { get; init; }

    [JsonPropertyName("modulePath")]
    public required string ModulePath { get; init; }

    [JsonPropertyName("bimCreateRevision")]
    public int BimCreateRevision { get; init; }
}

public sealed record BridgeRequestMessage
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = "request";

    [JsonPropertyName("method")]
    public required string Method { get; init; }

    [JsonPropertyName("params")]
    public JsonObject Parameters { get; init; } = new();
}

public sealed record BridgeResponseMessage
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = "response";

    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("result")]
    public JsonNode? Result { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; init; }

    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("details")]
    public JsonNode? Details { get; init; }
}

public sealed record BridgeEventMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "event";

    [JsonPropertyName("event")]
    public required string Event { get; init; }

    [JsonExtensionData]
    public IDictionary<string, JsonNode?> Data { get; init; } =
        new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
}
