using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Status;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text;
using System.Text.Json;

namespace GoAi.Server.Tests;

public sealed class NativeCatalogDetectionTests
{
    private static readonly string[] TextIds =
    [
        "coding/gpt-oss-120b-MXFP4~fd8e76ec296f",
        "coding/gpt-oss-20b-MXFP4~b71e6772a3a0",
        "coding/Qwen3VL-30B-A3B-Instruct-Q4_K_M~db0da1303aa5",
        "coding/Qwen3.8-27B-UD-Q8_K_XL~86704cb9d896",
        "coding/Qwen3.8-Flash-Next-UD-Q2_K_XL~c3df87cb03cc",
        "coding/NewlyInstalled-Text-Q4_K_M~new",
    ];
    private static readonly string[] SeparateIds =
    [
        "vision/Qwen3VL-30B-A3B-Instruct-Q4_K_M~db0da1303aa5",
        "vision/Qwen3.8-27B-UD-Q8_K_XL~86704cb9d896",
        "vision/Qwen3.8-Flash-Next-UD-Q2_K_XL~c3df87cb03cc",
        "embedding/bge-m3-q8_0~7044c4e97e6c",
    ];

    [Fact]
    public async Task EveryDiscoveredTextPresetIsExposedForGeneralAndCodingWithoutAStaticWhitelist()
    {
        using var handler = new CatalogHandler("installed");
        using var http = new HttpClient(handler);
        using var runtime = CreateRuntime(http);
        var status = await runtime.GetStatusAsync();
        var coding = await runtime.GetCodingModelsAsync();
        var capabilities = await new CapabilityService(Options.Create(new GoAiServerOptions()), runtime).GetSnapshotAsync();

        Assert.True(status.ProviderReachable);
        Assert.Null(status.ErrorMessage);
        Assert.Equal(TextIds, status.Models.Where(static model => model.Role == "general").Select(static model => model.Id));
        Assert.Equal(TextIds, coding.Models.Select(static model => model.Id));
        Assert.Equal(3, status.Models.Count(static model => model.Role == "vision"));
        Assert.Single(status.Models, static model => model.Role == "embedding");
        Assert.Equal(status.Models.Count, capabilities.Models.Count);
        Assert.All(coding.Models, static model =>
        {
            Assert.True(model.Downloaded);
            Assert.True(model.SupportsTools);
            Assert.False(model.SupportsVision);
        });
        Assert.Null(ModelRuntimeClient.ResolveModelStatus(status.Models, "coding/Qwen3.8-27B-removed-Q4~old", "coding"));
        Assert.Equal(TextIds[3], ModelRuntimeClient.ResolveModelStatus(status.Models, "qwen3.8-27b", "general")?.Id);
    }

    [Theory]
    [InlineData("offline", "modelRuntime.unreachable", "Connection refused")]
    [InlineData("invalid", "modelRuntime.invalidResponse", "ungültigen Modellstatus")]
    [InlineData("timeout", "modelRuntime.timeout", "5 Sekunden")]
    public async Task RuntimeFailurePreservesConcreteDiagnosisAndDoesNotInventInstalledCapabilities(
        string mode, string errorCode, string detail)
    {
        using var handler = new CatalogHandler(mode);
        using var http = new HttpClient(handler);
        using var runtime = CreateRuntime(http);
        var status = await runtime.GetStatusAsync();
        var coding = await runtime.GetCodingModelsAsync();
        var capabilities = await new CapabilityService(Options.Create(new GoAiServerOptions()), runtime).GetSnapshotAsync();

        Assert.False(status.ProviderReachable);
        Assert.Equal(errorCode, status.ErrorCode);
        Assert.Contains("http://native.test:8081", status.ErrorMessage!, StringComparison.Ordinal);
        Assert.Contains(detail, status.ErrorMessage!, StringComparison.Ordinal);
        Assert.InRange(status.ErrorMessage!.Length, 1, 1_200);
        Assert.DoesNotContain(status.ErrorMessage!, char.IsControl);
        Assert.Empty(status.Models);
        Assert.False(coding.RuntimeReachable);
        Assert.Equal(status.ErrorMessage, coding.Message);
        Assert.Empty(capabilities.Models);
    }

    [Fact]
    public async Task ReachableEmptyCatalogDoesNotClaimConfiguredModelsWereDiscovered()
    {
        using var handler = new CatalogHandler("empty");
        using var http = new HttpClient(handler);
        using var runtime = CreateRuntime(http);
        var status = await runtime.GetStatusAsync();
        var capabilities = await new CapabilityService(Options.Create(new GoAiServerOptions()), runtime).GetSnapshotAsync();
        Assert.True(status.ProviderReachable);
        Assert.Empty(status.Models);
        Assert.Empty(capabilities.Models);
        Assert.Contains("Keine vollständigen", (await runtime.GetCodingModelsAsync()).Message!, StringComparison.Ordinal);
    }

    [Fact]
    public void OlderStatusPayloadWithoutDiagnosticRemainsCompatible()
    {
        var json = """{"providerReachable":false,"providerUrl":"http://native.test:8081","models":[],"checkedAt":"2026-09-12T00:00:00Z","errorCode":"modelRuntime.unreachable"}""";
        var status = JsonSerializer.Deserialize<ModelStatusSnapshot>(json, JsonSerializerOptions.Web);
        Assert.NotNull(status);
        Assert.Null(status.ErrorMessage);
    }

    private static ModelRuntimeClient CreateRuntime(HttpClient http) => new(http,
        Options.Create(new GoAiServerOptions { ModelRuntimeUri = new Uri("http://native.test:8081") }),
        NullLogger<ModelRuntimeClient>.Instance);

    private sealed class CatalogHandler(string mode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/v1/models", request.RequestUri!.AbsolutePath);
            if (mode == "offline") throw new HttpRequestException("Connection refused\r\n" + new string('x', 2_000));
            if (mode == "timeout") throw new OperationCanceledException("Native model status timed out.");
            var body = mode == "invalid" ? "{}" : JsonSerializer.Serialize(new
            {
                data = (mode == "empty" ? Array.Empty<string>() : TextIds.Concat(SeparateIds)).Select(static id => new
                {
                    id,
                    tags = new[] { "go-context-train:" + (id.StartsWith("embedding/", StringComparison.Ordinal) ? "8192" : "262144") },
                    status = new { value = "unloaded" },
                }),
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
