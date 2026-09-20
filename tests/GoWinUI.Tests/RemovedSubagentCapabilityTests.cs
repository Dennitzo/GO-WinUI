using GoAi.Contracts;
using GoWinUI.App.Services;
using System.Text.Json;

namespace GoWinUI.Tests;

public sealed class RemovedSubagentCapabilityTests
{
    [Fact]
    public void WorkspaceCapabilitiesKeepDirectDocumentToolsWithoutDelegation()
    {
        var capabilities = GoAiAssistantService.WorkspaceClientCapabilities;

        Assert.Contains("documentIo", capabilities);
        Assert.Contains("documents", capabilities);
        Assert.Contains("workspace", capabilities);
        Assert.Contains("visual-tools", capabilities);
        Assert.Contains("blender", capabilities);
        Assert.DoesNotContain(capabilities, IsRemovedCapability);
        Assert.Equal(capabilities.Length, capabilities.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CodingNegotiationNeverReintroducesRemovedCapabilities(bool upgradedGateway)
    {
        var snapshot = new CapabilitySnapshot(GoAiProtocol.Version, "test", [],
            upgradedGateway ? ["coding.updatePlan"] : [],
            upgradedGateway ? [ClientToolNames.CodingReadOutput, ClientToolNames.CodingSearchRunEvidence] : [],
            new Dictionary<string, long>(), [], true, GoAiProtocol.UploadChunkSize);
        var negotiated = GoAiAssistantService.NegotiateCodingOptions(snapshot);
        var request = new RunRequest(GoAiProtocol.Version, RunMode.Coding, [],
            ClientCapabilities: negotiated.Capabilities
                .Concat(GoAiAssistantService.WorkspaceClientCapabilities).Distinct().ToArray(),
            CodingOptions: negotiated.Options);

        var serialized = JsonSerializer.SerializeToElement(request, GoAiProtocol.CreateJsonOptions());
        var sentCapabilities = serialized.GetProperty("clientCapabilities")
            .EnumerateArray().Select(static item => item.GetString()!).ToArray();

        Assert.Contains("coding", sentCapabilities);
        Assert.Contains("documentIo", sentCapabilities);
        Assert.DoesNotContain(sentCapabilities, IsRemovedCapability);
        Assert.Equal(upgradedGateway, sentCapabilities.Contains("coding.evidence", StringComparer.Ordinal));
    }

    private static bool IsRemovedCapability(string capability) =>
        capability.Equals("document-agent", StringComparison.OrdinalIgnoreCase)
        || capability.Contains("subagent", StringComparison.OrdinalIgnoreCase)
        || capability.Contains("parallel", StringComparison.OrdinalIgnoreCase);
}
