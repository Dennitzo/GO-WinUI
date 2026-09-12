using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using Xunit.Abstractions;

namespace GoAi.Server.Tests;

/// <summary>Opt-in acceptance of all migrated roles through the actual server runtime client.</summary>
public sealed class NativeRoleLiveTests(ITestOutputHelper output)
{
    private static readonly string[] EmbeddingInputs = ["Die rote Fläche ist ein Quadrat.", "Native lokale Modelle verarbeiten Text."];
    // Deterministic 128x128 RGB PNG: a red 96x96 square centered on a white background.
    private static readonly byte[] RedSquarePng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAIAAAACACAIAAABMXPacAAABFklEQVR4nO3RwQmEAAADQYuw/8rsRTvwJYS7nbAFBOa4bbpjfaA+AOMBGA/AeADGAzAegPEAjAdgPADjARgPwHgAxgMwHoBfA7jOUy8BANAOAIB2AAC0AwCgHQAA7QAAaAcAQDsAANoBANAOAIB2AAC0AwCgHQAA7QAAaAcAQDsAANoBANAOAIB2AAC0AwCgHQAA7QAAaAcAQDsAANoBANAOAIB2AAC0AwCgHQAA7QAAaAcAQDsAANoBANAOAIB2AAC0AwCgHQAA7QAAaAcAQDsAANoBANAOAIB2AAC0AwCgHQAA7QAAaAcAQDsA/w5g3w7AeADGAzAegPEAjAdgPADjARgPwHgAxgMwHoDxAIwHYDwA93YPHk5DCcAoWakAAAAASUVORK5CYII=");

    [Fact]
    [Trait("Category", "Live")]
    public async Task GeneralVisionAndEmbeddingUseInstalledNativeModels()
    {
        if (Environment.GetEnvironmentVariable("GO_AI_NATIVE_LIVE") != "1") return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        using var http = new HttpClient();
        using var client = new ModelRuntimeClient(http, Options.Create(new GoAiServerOptions
        {
            ModelRuntimeUri = new Uri(Environment.GetEnvironmentVariable("GO_AI_NATIVE_RUNTIME_URL") ?? "http://127.0.0.1:8081"),
        }), NullLogger<ModelRuntimeClient>.Instance);
        var clock = Stopwatch.StartNew();
        var status = await client.GetStatusAsync(timeout.Token);
        Assert.True(status.ProviderReachable, $"Native catalog is unreachable: {status.ErrorCode}");
        var general = ModelRuntimeClient.ResolveModelStatus(status.Models,
            Environment.GetEnvironmentVariable("GO_AI_NATIVE_GENERAL_MODEL") ?? "gpt-oss-120b", "general");
        var vision = ModelRuntimeClient.ResolveModelStatus(status.Models,
            Environment.GetEnvironmentVariable("GO_AI_NATIVE_VISION_MODEL") ?? "qwen3-vl-30b-a3b-instruct", "vision");
        var embedding = ModelRuntimeClient.ResolveModelStatus(status.Models,
            Environment.GetEnvironmentVariable("GO_AI_NATIVE_EMBEDDING_MODEL") ?? "text-embedding-bge-m3", "embedding");
        Assert.NotNull(general);
        Assert.NotNull(vision);
        Assert.NotNull(embedding);
        output.WriteLine($"Installed native roles: general={general.Id}; vision={vision.Id}; embedding={embedding.Id}");

        var fixture = Path.Combine(Path.GetTempPath(), "go-native-vision-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            output.WriteLine("Starting General inference (including native model load).");
            var streamedCharacters = 0;
            var generalResult = await client.CompleteChatAsync(general.Id,
                [new LmChatMessage("user", "Berechne 6 mal 7. Antworte ausschließlich mit der Zahl.")], [],
                maximumOutputTokens: 128, modelRole: "general", reasoningEffort: "low", requiredContextLength: 32_768,
                nativeProgress: (progress, _) =>
                {
                    streamedCharacters += progress.ContentDelta?.Length ?? 0;
                    return ValueTask.CompletedTask;
                }, cancellationToken: timeout.Token);
            output.WriteLine($"General after {clock.Elapsed.TotalSeconds:F1}s: {Bound(generalResult.Content)}; streamed characters={streamedCharacters}");
            Assert.Contains("42", generalResult.Content ?? "", StringComparison.Ordinal);
            Assert.Empty(generalResult.ToolCalls);
            Assert.True(streamedCharacters > 0, "General inference must deliver native streamed text.");

            await File.WriteAllBytesAsync(fixture, RedSquarePng, timeout.Token);
            output.WriteLine("Starting Vision inference with the deterministic red-square PNG.");
            var visionResult = await client.AnalyzeImagesAsync(vision.Id,
                "Welche Farbe hat die große Fläche in der Bildmitte? Antworte auf Deutsch mit genau einem Farbnamen.",
                [fixture], timeout.Token);
            output.WriteLine($"Vision after {clock.Elapsed.TotalSeconds:F1}s: {Bound(visionResult)}");
            Assert.Contains("rot", visionResult, StringComparison.OrdinalIgnoreCase);

            output.WriteLine("Starting BGE-M3 embedding inference.");
            var vectors = await client.CreateEmbeddingsAsync(embedding.Id, EmbeddingInputs, timeout.Token);
            Assert.Equal(EmbeddingInputs.Length, vectors.Count);
            foreach (var vector in vectors)
            {
                Assert.Equal(1_024, vector.Count);
                Assert.All(vector, value => Assert.True(double.IsFinite(value), "Embedding contains a non-finite value."));
                Assert.True(vector.Any(static value => Math.Abs(value) > 1e-8), "Embedding is a zero vector.");
            }
            output.WriteLine($"Embedding after {clock.Elapsed.TotalSeconds:F1}s: {vectors.Count} finite, nonzero vectors of {vectors[0].Count} dimensions.");
        }
        finally
        {
            // The only fixture created by this test; no directory or model cleanup is performed.
            File.Delete(fixture);
        }
    }

    private static string Bound(string? value) => string.IsNullOrEmpty(value) ? "<empty>" : value[..Math.Min(value.Length, 1_000)];
}
