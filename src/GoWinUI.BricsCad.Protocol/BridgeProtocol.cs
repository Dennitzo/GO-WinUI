using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GoWinUI.BricsCad.Protocol;

public static class BridgeProtocol
{
    public const int Version = 4;
    public const int MinimumBimCreateRevision = 7;
    public const int MaximumFrameBytes = 8 * 1024 * 1024;
    public const string Provider = "bricscad-dotnet";
    public const string BridgeBuild = "bridge-json-v4";
    public const string ContractVersion = "bricscad-dotnet-tools-v2";
    public const string RendezvousSchema = "go.bricscad.bridge.rendezvous.v1";

    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        return new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = false,
            WriteIndented = false
        };
    }
}

public static class ContractIdentity
{
    public const string ResourceName =
        "GoWinUI.BricsCad.Protocol.Contracts.bricscad-dotnet-tools-v2.json";

    private static readonly Lazy<byte[]> ContractBytesValue = new(LoadContractBytes);
    private static readonly Lazy<string> ContractHashValue = new(
        () => Convert.ToHexString(SHA256.HashData(ContractBytesValue.Value)).ToLowerInvariant());

    public static string Sha256 => ContractHashValue.Value;

    public static ReadOnlyMemory<byte> Bytes => ContractBytesValue.Value;

    public static string Json => Encoding.UTF8.GetString(ContractBytesValue.Value);

    public static JsonObject Parse()
    {
        return JsonNode.Parse(ContractBytesValue.Value)?.AsObject()
            ?? throw new InvalidOperationException("Embedded BricsCAD tool contract is invalid.");
    }

    public static bool MatchesSha256(string? candidate)
    {
        if (candidate is null || candidate.Length != 64)
        {
            return false;
        }

        try
        {
            byte[] expected = Convert.FromHexString(Sha256);
            byte[] actual = Convert.FromHexString(candidate);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] LoadContractBytes()
    {
        using Stream stream = typeof(ContractIdentity).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("Embedded BricsCAD tool contract is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
