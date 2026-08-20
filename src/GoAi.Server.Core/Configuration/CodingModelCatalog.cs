namespace GoAi.Server.Core.Configuration;

public sealed record CodingModelProfile(
    string Id,
    string DisplayName,
    int ContextLength,
    string SamplingProfile);

public static class CodingModelCatalog
{
    public const string DeepSeekV4FlashId = "ud";
    public const string Qwen3CoderNextId = "qwen3-coder-next";
    public const string DefaultModelId = DeepSeekV4FlashId;

    public static IReadOnlyList<CodingModelProfile> Models { get; } =
    [
        new(
            DeepSeekV4FlashId,
            "DeepSeek-V4-Flash-0731 · UD-IQ2_M",
            262_144,
            "deepseek-agent"),
        new(
            Qwen3CoderNextId,
            "Qwen3-Coder-Next · Q6_K",
            262_144,
            "qwen-coder"),
    ];

    public static bool TryGet(string? modelId, out CodingModelProfile profile)
    {
        profile = Models.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, modelId?.Trim(), StringComparison.OrdinalIgnoreCase))!;
        return profile is not null;
    }

    public static CodingModelProfile Get(string? modelId) =>
        TryGet(modelId, out var profile)
            ? profile
            : throw new ArgumentException($"Nicht unterstütztes Coding-Modell: {modelId}", nameof(modelId));

    public static string GetDisplayName(string? modelId) =>
        TryGet(modelId, out var profile) ? profile.DisplayName : modelId ?? "Coding-Agent";
}
