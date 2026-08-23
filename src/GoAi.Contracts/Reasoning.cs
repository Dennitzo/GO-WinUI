namespace GoAi.Contracts;

public sealed record ModelReasoningProfile(
    string Family,
    IReadOnlyList<string> SupportedEfforts,
    string? DefaultEffort)
{
    public bool IsConfigurable => SupportedEfforts.Count > 0;

    public bool Supports(string? effort) =>
        !string.IsNullOrWhiteSpace(effort)
        && SupportedEfforts.Contains(effort.Trim(), StringComparer.OrdinalIgnoreCase);

    public string? Resolve(string? requestedEffort)
    {
        var requested = requestedEffort?.Trim();
        if (Supports(requested))
        {
            return requested!.ToLowerInvariant();
        }

        return DefaultEffort;
    }
}

public static class ModelReasoningProfiles
{
    public const string Automatic = "auto";
    public const string GptOssFamily = "gpt-oss";
    public const string Qwen38Family = "qwen3.8";
    public const string FixedFamily = "fixed";
    public const string UnknownFamily = "automatic";

    private static readonly IReadOnlyList<string> GptOssEfforts = ["low", "medium", "high"];
    private static readonly IReadOnlyList<string> Qwen38Efforts = ["off", "on"];

    public static ModelReasoningProfile Resolve(string? modelId, string? role)
    {
        var normalizedModelId = modelId?.Trim() ?? string.Empty;
        var normalizedRole = role?.Trim() ?? string.Empty;

        if (normalizedModelId.Contains("gpt-oss", StringComparison.OrdinalIgnoreCase))
        {
            return new(
                GptOssFamily,
                GptOssEfforts,
                string.Equals(normalizedRole, "code", StringComparison.OrdinalIgnoreCase) ? "high" : "low");
        }

        if (normalizedModelId.Contains("qwen3.8", StringComparison.OrdinalIgnoreCase)
            || normalizedModelId.Contains("qwen3_8", StringComparison.OrdinalIgnoreCase)
            || normalizedModelId.Contains("qwen38", StringComparison.OrdinalIgnoreCase))
        {
            // LM Studio exposes Qwen3.8 GGUF reasoning as the model-specific
            // on/off switch. The low/medium/xhigh Jinja values from the upstream
            // Transformers template are not public options of this runtime.
            return new(Qwen38Family, Qwen38Efforts, "on");
        }

        if (normalizedModelId.Contains("qwen3-coder-next", StringComparison.OrdinalIgnoreCase))
        {
            return new(FixedFamily, [], null);
        }

        return new(UnknownFamily, [], null);
    }

    public static string? ResolveEffort(string? modelId, string? role, string? requestedEffort) =>
        Resolve(modelId, role).Resolve(
            string.Equals(requestedEffort?.Trim(), Automatic, StringComparison.OrdinalIgnoreCase)
                ? null
                : requestedEffort);
}
