using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Runs;

namespace GoAi.Server.Tests;

public sealed class BlenderRunBudgetTests
{
    [Theory]
    [InlineData(RunMode.General, false, false, 30, 12)]
    [InlineData(RunMode.General, true, false, 30, 64)]
    [InlineData(RunMode.General, false, true, 30, 12)]
    [InlineData(RunMode.General, true, true, 96, 192)]
    [InlineData(RunMode.Auto, true, true, 30, 64)]
    public void ExpandedBudgetRequiresGeneralModeWorkspaceAndAvailableBlender(
        RunMode mode, bool workspace, bool blender, int expectedTools, int expectedRounds)
    {
        var request = Request(mode, workspace, blender);
        var available = new AgentToolCatalog().GetAvailableTools(request);

        Assert.Equal(expectedTools, RunProcessor.ResolveMaximumToolCalls(request, new GoAiServerOptions(), available));
        Assert.Equal(expectedRounds, RunProcessor.ResolveMaximumModelRounds(request, new GoAiServerOptions(), available));
    }

    [Fact]
    public void CapabilityAloneCannotEnableBudgetWhenToolIsUnavailable()
    {
        var request = Request(RunMode.General, workspace: true, blender: true);

        Assert.Equal(30, RunProcessor.ResolveMaximumToolCalls(request, new GoAiServerOptions(), []));
        Assert.Equal(64, RunProcessor.ResolveMaximumModelRounds(request, new GoAiServerOptions(), []));
    }

    [Theory]
    [InlineData(256, 192, 256)]
    [InlineData(64, 200, 200)]
    [InlineData(64, 0, 64)]
    public void BlenderModelRoundsPreserveLargerWorkspaceConfiguration(int workspace, int blender, int expected)
    {
        var request = Request(RunMode.General, workspace: true, blender: true);
        var options = new GoAiServerOptions { WorkspaceMaximumModelRounds = workspace, BlenderMaximumModelRounds = blender };

        Assert.Equal(expected, RunProcessor.ResolveMaximumModelRounds(request, options,
            new AgentToolCatalog().GetAvailableTools(request)));
    }

    [Theory]
    [InlineData(150, 96, 150)]
    [InlineData(30, 120, 120)]
    [InlineData(30, 0, 30)]
    public void BlenderBudgetPreservesLargerGeneralConfiguration(int general, int blender, int expected)
    {
        var request = Request(RunMode.General, workspace: true, blender: true);
        var options = new GoAiServerOptions { MaximumToolCalls = general, BlenderMaximumToolCalls = blender };

        Assert.Equal(expected, RunProcessor.ResolveMaximumToolCalls(request, options,
            new AgentToolCatalog().GetAvailableTools(request)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(45)]
    public void CodingKeepsItsOwnConfiguredBudget(int maximumToolCalls)
    {
        var request = Request(RunMode.Coding, workspace: true, blender: true);
        var options = new GoAiServerOptions { CodingMaximumToolCalls = maximumToolCalls, CodingMaximumModelRounds = maximumToolCalls };

        Assert.Equal(maximumToolCalls, RunProcessor.ResolveMaximumToolCalls(request, options,
            new AgentToolCatalog().GetAvailableTools(request)));
        Assert.Equal(maximumToolCalls, RunProcessor.ResolveMaximumModelRounds(request, options,
            new AgentToolCatalog().GetAvailableTools(request)));
    }

    private static RunRequest Request(RunMode mode, bool workspace, bool blender)
    {
        List<string> capabilities = [];
        if (workspace) capabilities.Add("workspace");
        if (blender) capabilities.Add("blender");
        return new RunRequest(GoAiProtocol.Version, mode, [new("user", [new("text", "Modelliere den Rover.")])],
            ClientCapabilities: capabilities);
    }
}
