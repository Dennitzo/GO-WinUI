using GoAi.Contracts;
using GoWinUI.App.Services;
using System.Text.Json;
using Xunit;

namespace GoWinUI.Tests;

public sealed class CodingCapabilityNegotiationTests
{
    private static CapabilitySnapshot Snapshot(string[] server, string[] client) => new(GoAiProtocol.Version, "test", [], server,
        client, new Dictionary<string, long>(), [], true, GoAiProtocol.UploadChunkSize);

    [Fact]
    public void WorkingStateAndMaximumReasoningAreAlwaysRequested()
    {
        var result = GoAiAssistantService.NegotiateCodingOptions(Snapshot(["coding.updatePlan"],
            [ClientToolNames.CodingReadOutput, ClientToolNames.CodingSearchRunEvidence]));
        Assert.True(result.Options!.UseWorkingState);
        Assert.Equal("maximum", result.Options.ReasoningPolicy);
        Assert.Contains("coding.evidence", result.Capabilities);
    }

    [Fact]
    public void LegacyGatewayReceivesNoNewFieldsOrCapabilityNames()
    {
        var negotiated = GoAiAssistantService.NegotiateCodingOptions(Snapshot([], []));
        Assert.Equal("coding", Assert.Single(negotiated.Capabilities));
        Assert.Null(negotiated.Options);
        var request = new RunRequest(GoAiProtocol.Version, RunMode.Coding, [], ClientCapabilities: negotiated.Capabilities,
            CodingOptions: negotiated.Options);
        Assert.False(JsonSerializer.SerializeToElement(request, GoAiProtocol.CreateJsonOptions()).TryGetProperty("codingOptions", out _));
    }

    [Fact]
    public void PartialUpgradeNeverAdvertisesIncompleteEvidenceProtocol()
    {
        var negotiated = GoAiAssistantService.NegotiateCodingOptions(Snapshot(["coding.updatePlan"],
            [ClientToolNames.CodingReadOutput]));
        Assert.Equal("coding", Assert.Single(negotiated.Capabilities));
        Assert.Equal("maximum", negotiated.Options?.ReasoningPolicy);
    }

    [Fact]
    public void CompleteGatewayUsesFixedCodingPolicy()
    {
        var negotiated = GoAiAssistantService.NegotiateCodingOptions(Snapshot(["coding.updatePlan"],
            [ClientToolNames.CodingReadOutput, ClientToolNames.CodingSearchRunEvidence]));
        Assert.Contains("coding.evidence", negotiated.Capabilities);
        Assert.Equal("maximum", negotiated.Options?.ReasoningPolicy);
        Assert.True(negotiated.Options?.UseWorkingState);
    }
    [Fact]
    public void ParallelModelIsSentOnlyToSupportingGateways()
    {
        var legacy = Snapshot(["coding.updatePlan"], []);
        var unsupported = GoAiAssistantService.NegotiateCodingOptions(legacy, "coding/same-model");
        Assert.Null(unsupported.Options!.ParallelModelId);
        Assert.False(JsonSerializer.SerializeToElement(unsupported.Options, GoAiProtocol.CreateJsonOptions()).TryGetProperty("parallelModelId", out _));
        var supported = GoAiAssistantService.NegotiateCodingOptions(legacy with { SupportsParallelCoding = true }, "  coding/same-model  ");
        Assert.Equal("coding/same-model", supported.Options!.ParallelModelId);
        var disabled = GoAiAssistantService.NegotiateCodingOptions(legacy with { SupportsParallelCoding = true }, " ");
        Assert.Null(disabled.Options!.ParallelModelId);
    }

}
