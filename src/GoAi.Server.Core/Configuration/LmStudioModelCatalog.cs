using GoAi.Contracts;

namespace GoAi.Server.Core.Configuration;

public sealed record LmStudioModelProfile(
    string Id,
    string RuntimeModelId,
    string DisplayName,
    int ContextLength,
    string SamplingProfile);

public static class LmStudioModelCatalog
{
    public const string GptOss120BId = "gpt-oss-120b";
    // LM Studio 0.4.18 exposes and loads the downloaded catalog entry by this
    // stable key. The former openai/ prefix is listed by some newer builds but
    // is rejected by the pinned runtime's /api/v1/models/load endpoint.
    public const string GptOss120BRuntimeId = "gpt-oss-120b";
    public const string Qwen38Id = "qwen3.8-27b";
    public const string Qwen38RuntimeId = "qwen/qwen3.8-27b";
    public const string Qwen3CoderNextQ8Id = "qwen3-coder-next";
    public const string Qwen3CoderNextLegacyId = "qwen3-coder-next-q8_0";
    public const string Qwen3CoderNextRuntimeId = "qwen3-coder-next";
    public const string DefaultModelId = GptOss120BId;

    public static IReadOnlyList<LmStudioModelProfile> Models { get; } =
    [
        new(
            Qwen38Id,
            Qwen38RuntimeId,
            "Qwen3.8 27B · Q4_K_M",
            ModelContextProfiles.Qwen38Maximum,
            "qwen3.8"),
        new(
            GptOss120BId,
            GptOss120BRuntimeId,
            "gpt-oss-120b",
            ModelContextProfiles.GptOss120BMaximum,
            "gpt-oss-coder"),
        new(
            Qwen3CoderNextQ8Id,
            Qwen3CoderNextRuntimeId,
            "Qwen3-Coder-Next · Q8_0",
            ModelContextProfiles.Qwen3CoderNextMaximum,
            "qwen3-coder-next"),
    ];

    public static bool TryGet(string? modelId, out LmStudioModelProfile profile)
    {
        var normalized = string.Equals(
            modelId?.Trim(),
            Qwen3CoderNextLegacyId,
            StringComparison.OrdinalIgnoreCase)
                ? Qwen3CoderNextQ8Id
                : modelId?.Trim();
        profile = Models.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, normalized, StringComparison.OrdinalIgnoreCase))!;
        return profile is not null;
    }

    public static LmStudioModelProfile Get(string? modelId) =>
        TryGet(modelId, out var profile)
            ? profile
            : throw new ArgumentException($"Nicht unterstütztes LM-Studio-Modell: {modelId}", nameof(modelId));

    public static string GetDisplayName(string? modelId) =>
        TryGet(modelId, out var profile) ? profile.DisplayName : modelId ?? "AI-Modell";
}
