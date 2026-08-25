using GoAi.Contracts;

namespace GoAi.Server.Core.Configuration;

public sealed record CodingModelProfile(
    string Id,
    string DisplayName,
    int ContextLength,
    string SamplingProfile);

public static class CodingModelCatalog
{
    public const string GptOss120BId = "gpt-oss-120b";
    public const string Qwen3CoderNextQ8Id = "qwen3-coder-next-q8_0";
    public const string DefaultModelId = Qwen3CoderNextQ8Id;

    public static IReadOnlyList<CodingModelProfile> Models { get; } =
    [
        new(
            GptOss120BId,
            "gpt-oss-120b",
            ModelContextProfiles.GptOss120BMaximum,
            "gpt-oss-coder"),
        new(
            Qwen3CoderNextQ8Id,
            "Qwen3-Coder-Next · Q8_0",
            ModelContextProfiles.Qwen3CoderNextMaximum,
            "qwen3-coder-next"),
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
