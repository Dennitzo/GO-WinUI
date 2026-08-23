namespace GoAi.Server.Core.Configuration;

public sealed record CodingModelProfile(
    string Id,
    string DisplayName,
    int ContextLength,
    string SamplingProfile);

public static class CodingModelCatalog
{
    public const string Qwen38BId = "qwen3.8-27b";
    public const string Qwen3CoderNextId = "qwen3-coder-next";
    public const string GptOss120BId = "openai/gpt-oss-120b";
    public const string DefaultModelId = Qwen38BId;

    public static IReadOnlyList<CodingModelProfile> Models { get; } =
    [
        new(
            Qwen38BId,
            "Qwen3.8-27B · Q8_0",
            262_144,
            "qwen38-coder"),
        new(
            Qwen3CoderNextId,
            "Qwen3-Coder-Next · Q6_K",
            262_144,
            "qwen-coder"),
        new(
            GptOss120BId,
            "gpt-oss-120b",
            131_072,
            "gpt-oss-coder"),
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
