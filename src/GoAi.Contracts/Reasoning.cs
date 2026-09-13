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
    public const string Qwen3CoderNextFamily = "qwen3-coder-next";
    public const string UnknownFamily = "automatic";

    private static readonly IReadOnlyList<string> DisabledOnlyEfforts = ["none"];
    private static readonly IReadOnlyList<string> GptOssEfforts = ["low", "medium", "high"];
    private static readonly IReadOnlyList<string> Qwen38Efforts = ["none", "low", "medium", "xhigh"];

    public static ModelReasoningProfile Resolve(string? modelId, string? role)
    {
        var normalizedModelId = modelId?.Trim() ?? string.Empty;
        var normalizedRole = role?.Trim() ?? string.Empty;
        if (normalizedModelId.Contains("gpt-oss", StringComparison.OrdinalIgnoreCase))
        {
            return new(
                GptOssFamily,
                GptOssEfforts,
                "high");
        }

        if (normalizedModelId.Contains("qwen3.8-", StringComparison.OrdinalIgnoreCase))
        {
            return new(Qwen38Family, Qwen38Efforts, "xhigh");
        }

        if (normalizedModelId.Contains("qwen3-coder-next", StringComparison.OrdinalIgnoreCase))
        {
            // Qwen3-Coder-Next is a non-thinking model. Expose that state as an
            // explicit UI option while the runtime deliberately sends no
            // reasoning parameter.
            return new(Qwen3CoderNextFamily, DisabledOnlyEfforts, "none");
        }

        // Missing metadata is not proof that reasoning is disabled. Unknown
        // models remain usable with their native defaults until discovered.
        return new(UnknownFamily, [], null);
    }

    public static string? ResolveEffort(string? modelId, string? role, string? requestedEffort) =>
        Resolve(modelId, role).Resolve(
            string.Equals(requestedEffort?.Trim(), Automatic, StringComparison.OrdinalIgnoreCase)
                ? null
                : requestedEffort);
}
