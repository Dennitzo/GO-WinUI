using GoAi.Contracts;

namespace GoWinUI.Tests;

public sealed class QwenCoderWorkspaceMapLiveTests
{
    private const string WorkspaceEnvironmentVariable = "GO_AI_LIVE_WORKSPACE_MAP_WORKSPACE";
    private const string ModelEnvironmentVariable = "GO_AI_LIVE_CODING_MODEL";
    private const string PromptEnvironmentVariable = "GO_AI_LIVE_WORKSPACE_MAP_PROMPT";
    private const string StopAfterToolsEnvironmentVariable = "GO_AI_LIVE_WORKSPACE_MAP_STOP_AFTER_TOOLS";

    [Fact]
    [Trait("Category", "Live")]
    public async Task QwenCoderGeneratesAndExecutesWorkspaceMapForPhysikBuch()
    {
        var requestedWorkspace = Environment.GetEnvironmentVariable(WorkspaceEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(requestedWorkspace))
        {
            return;
        }

        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedWorkspace));
        Assert.True(Directory.Exists(workspace), $"Workspace fehlt: {workspace}");

        var modelId = Environment.GetEnvironmentVariable(ModelEnvironmentVariable)?.Trim();
        if (string.IsNullOrWhiteSpace(modelId))
        {
            modelId = "qwen3-coder-next";
        }

        var promptOverride = Environment.GetEnvironmentVariable(PromptEnvironmentVariable);
        var hasPromptOverride = !string.IsNullOrWhiteSpace(promptOverride);
        var prompt = hasPromptOverride
            ? promptOverride!
            : """
              Prüfe ausschließlich den freigegebenen Workspace. Rufe als ersten und einzigen
              Workspace-Schritt genau das Clientwerkzeug workspace.map auf, ohne maximumDepth
              oder maximumEntries vorzugeben. Verwende danach keine weiteren Werkzeuge, ändere
              keine Datei und beende den Lauf mit einer kurzen Bestätigung, dass die Workspacekarte
              erfolgreich empfangen wurde. Dies ist ein Vertragstest für die Toolnamen- und
              Argumentübertragung, keine Programmieraufgabe.
              """;
        var stopAfterToolCount = int.TryParse(
            Environment.GetEnvironmentVariable(StopAfterToolsEnvironmentVariable),
            out var configuredStopAfterToolCount)
            && configuredStopAfterToolCount > 0
                ? configuredStopAfterToolCount
                : 1;
        var completedToolCount = 0;

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var sessionId = $"live-workspace-map-{Guid.NewGuid():N}";
        await using var harness = await CodingAgentLiveTestHarness.CreateAsync(
            "workspace-map-physikbuch",
            workspace,
            modelId,
            sessionId,
            timeout.Token);

        var observation = await harness.ExecuteAsync(
            sessionId,
            prompt,
            "live-workspace-map",
            timeout.Token,
            stopAfterTool: (proposal, result) =>
            {
                if (!string.Equals(result.Status, "completed", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                completedToolCount++;
                return completedToolCount >= stopAfterToolCount
                    && (hasPromptOverride || proposal.Name == ClientToolNames.WorkspaceMap);
            });

        Assert.Equal(RunState.Cancelled, observation.Run.State);
        Assert.NotEmpty(observation.ToolNames);
        if (!hasPromptOverride)
        {
            Assert.Contains(ClientToolNames.WorkspaceMap, observation.ToolNames);
        }
        if (!hasPromptOverride)
        {
            Assert.DoesNotContain(
                observation.ToolNames,
                static name => name is ClientToolNames.FileSystemWriteText
                    or ClientToolNames.FileSystemReplaceText
                    or ClientToolNames.FileSystemMove
                    or ClientToolNames.FileSystemProposePatch
                    or ClientToolNames.FileSystemProposeCreate
                    or ClientToolNames.FileSystemProposeDelete);
        }
        Assert.DoesNotContain("<tool_call", observation.VisibleText, StringComparison.OrdinalIgnoreCase);
    }
}
