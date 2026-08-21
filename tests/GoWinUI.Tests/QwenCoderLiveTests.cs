using GoAi.Contracts;

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

    [Fact]
    [Trait("Category", "Live")]
    public async Task SelectedCoderCanIterativelyVerifyASmallLeanTheorem()
    {
        var requestedWorkspace = Environment.GetEnvironmentVariable(WorkspaceEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(requestedWorkspace)
            || !string.Equals(Environment.GetEnvironmentVariable("GO_AI_LIVE_LEAN_AGENT_TEST"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedWorkspace));
        var modelId = Environment.GetEnvironmentVariable(ModelEnvironmentVariable)?.Trim();
        if (string.IsNullOrWhiteSpace(modelId)) modelId = "qwen3-coder-next";
        using var timeout = new CancellationTokenSource(TimeSpan.FromHours(2));
        var sessionId = $"live-lean-{Guid.NewGuid():N}";
        await using var harness = await CodingAgentLiveTestHarness.CreateAsync(
            "lean-proof-tool",
            workspace,
            modelId,
            sessionId,
            timeout.Token);
        var observation = await harness.ExecuteAsync(
            sessionId,
            """
            Lege unter proofs/live-tool/ eine kleine Lean-Datei mit einem zunächst absichtlich fehlerhaften,
            anschließend korrigierten Theorem über natürliche Zahlen an. Prüfe die Zwischendiagnose und das korrigierte,
            vollständig qualifizierte Theorem mit proof.lean. Verwende abschließend verify, behebe alle Diagnosen und
            behaupte den formalen Beweis nur, wenn Kompilierung und Axiomprüfung bestanden sind. Verwende weder sorry
            noch admit, eigene Axiome oder Lean.trustCompiler.
            """,
            "live-lean",
            timeout.Token);

        CodingAgentLiveTestHarness.AssertSuccessful(
            observation,
            modelId,
            requireMutation: true,
            requireVerification: false);
        Assert.Contains(ClientToolNames.LeanProof, observation.ToolNames);
    }
}
