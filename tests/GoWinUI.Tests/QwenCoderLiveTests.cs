namespace GoWinUI.Tests;

public sealed class QwenCoderLiveTests
{
    private const string WorkspaceEnvironmentVariable = "GO_AI_LIVE_CODING_WORKSPACE";
    private const string PromptEnvironmentVariable = "GO_AI_LIVE_CODING_PROMPT";
    private const string ModelEnvironmentVariable = "GO_AI_LIVE_CODING_MODEL";

    [Fact]
    [Trait("Category", "Live")]
    public async Task QwenCoderCanCompleteANaturalUserRequestInAnArbitraryWorkspace()
    {
        var requestedWorkspace = Environment.GetEnvironmentVariable(WorkspaceEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(requestedWorkspace))
        {
            // Echte Serverläufe und Workspace-Mutationen werden nur explizit aktiviert.
            return;
        }

        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedWorkspace));
        Assert.True(Directory.Exists(workspace), $"Live-Coding-Workspace fehlt: {workspace}");
        var modelId = Environment.GetEnvironmentVariable(ModelEnvironmentVariable)?.Trim();
        if (string.IsNullOrWhiteSpace(modelId))
        {
            modelId = "qwen3-coder-next";
        }
        var prompt = Environment.GetEnvironmentVariable(PromptEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            prompt = """
                Überarbeite die vorhandene Anzeige der technischen Laufzeit in der Hauptoberfläche. Sie soll im normalen
                Layout verständlich sein, bei wenig Platz kompakt bleiben und bei Bedienung die wichtigsten Details
                zugänglich machen. Achte auf Barrierefreiheit und füge passende Tests hinzu. Halte dich an Architektur,
                Gestaltung und Werkzeuge des vorhandenen Projekts. Implementiere die Änderung vollständig, prüfe den Diff
                und führe die im Repository vorgesehenen Tests, den regulären Build sowie einen kurzen Laufzeitcheck aus.
                Behebe Fehler, die durch deine Änderung entstehen, selbstständig.
                """;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromHours(4));
        var sessionId = $"live-coding-{Guid.NewGuid():N}";
        await using var harness = await CodingAgentLiveTestHarness.CreateAsync(
            "general-coding",
            workspace,
            modelId,
            sessionId,
            timeout.Token);
        var observation = await harness.ExecuteAsync(
            sessionId,
            prompt,
            "live-coding",
            timeout.Token);
        CodingAgentLiveTestHarness.AssertSuccessful(observation, modelId);
    }
}
