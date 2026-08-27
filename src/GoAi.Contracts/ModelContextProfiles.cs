namespace GoAi.Contracts;

/// <summary>
/// Native context limits of the model revisions pinned by the GO Docker stack.
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

    public static int ResolveMaximum(string? modelId, string? role)
    {
        var normalized = modelId?.Trim() ?? string.Empty;
        if (normalized.Contains("qwen3.8-27b", StringComparison.OrdinalIgnoreCase))
        {
            return Qwen38Maximum;
        }
        if (normalized.Contains("qwen3-coder-next", StringComparison.OrdinalIgnoreCase))
        {
            return Qwen3CoderNextMaximum;
        }
        if (normalized.Contains("qwen3-vl", StringComparison.OrdinalIgnoreCase))
        {
            return Qwen3VlMaximum;
        }
        if (normalized.Contains("bge-m3", StringComparison.OrdinalIgnoreCase))
        {
            return BgeM3Maximum;
        }
        if (normalized.Contains("gpt-oss", StringComparison.OrdinalIgnoreCase))
        {
            return GptOss120BMaximum;
        }

        return string.Equals(role?.Trim(), "code", StringComparison.OrdinalIgnoreCase)
            ? Qwen38Maximum
            : GptOss120BMaximum;
    }
}
