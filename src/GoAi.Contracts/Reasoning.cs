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
    public const string Qwen3CoderNextFamily = "qwen3-coder-next";
    public const string UnknownFamily = "automatic";

    private static readonly IReadOnlyList<string> GptOssEfforts = ["low", "medium", "high"];

    public static ModelReasoningProfile Resolve(string? modelId, string? role)
    {
        var normalizedModelId = modelId?.Trim() ?? string.Empty;
        var normalizedRole = role?.Trim() ?? string.Empty;

        if (normalizedModelId.Contains("gpt-oss", StringComparison.OrdinalIgnoreCase))
        {
            return new(
                GptOssFamily,
                GptOssEfforts,
                string.Equals(normalizedRole, "code", StringComparison.OrdinalIgnoreCase) ? "high" : "medium");
        }

        if (normalizedModelId.Contains("qwen3-coder-next", StringComparison.OrdinalIgnoreCase))
        {
            // Qwen3-Coder-Next is a non-thinking model. Sending a reasoning
            // parameter would make the runtime reject an otherwise valid turn.
            return new(Qwen3CoderNextFamily, [], null);
        }

        return new(UnknownFamily, [], null);
    }

    public static string? ResolveEffort(string? modelId, string? role, string? requestedEffort) =>
        Resolve(modelId, role).Resolve(
            string.Equals(requestedEffort?.Trim(), Automatic, StringComparison.OrdinalIgnoreCase)
                ? null
                : requestedEffort);
}
