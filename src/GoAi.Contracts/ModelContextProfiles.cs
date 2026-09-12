namespace GoAi.Contracts;

/// <summary>
/// Training context limits of the locally installed model families.
/// Clients use the same values when budgeting a request before the gateway is
/// contacted; the gateway remains authoritative through its capability data.
/// </summary>
public static class ModelContextProfiles
{
    public const int GptOss120BMaximum = 131_072;
    public const int Qwen38Maximum = 262_144;
    public const int Qwen3CoderNextMaximum = 262_144;
    public const int Qwen3VlMaximum = 262_144;
    public const int BgeM3Maximum = 8_192;
    // Conservative fallback for an unknown family. Loaded runtime capability data is authoritative.
    public const int NativeRuntimeDefault = 32_768;
    public const int CodingRuntimeDefault = NativeRuntimeDefault;

    public static int ResolveMaximum(string? modelId, string? role)
    {
        if (string.Equals(role, "embedding", StringComparison.OrdinalIgnoreCase)
            || modelId?.Contains("bge-m3", StringComparison.OrdinalIgnoreCase) == true)
        {
            return BgeM3Maximum;
        }
        if (modelId?.Contains("gpt-oss", StringComparison.OrdinalIgnoreCase) == true)
            return GptOss120BMaximum;
        if (modelId?.Contains("qwen3.8", StringComparison.OrdinalIgnoreCase) == true)
            return Qwen38Maximum;
        if (modelId?.Contains("qwen3-coder-next", StringComparison.OrdinalIgnoreCase) == true)
            return Qwen3CoderNextMaximum;
        if (modelId?.Contains("qwen3vl", StringComparison.OrdinalIgnoreCase) == true
            || modelId?.Contains("qwen3-vl", StringComparison.OrdinalIgnoreCase) == true)
            return Qwen3VlMaximum;
        return NativeRuntimeDefault;
    }
}
