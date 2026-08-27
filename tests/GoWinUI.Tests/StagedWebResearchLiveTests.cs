namespace GoWinUI.Tests;

public sealed class StagedWebResearchLiveTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task SelectedCodingModelCompletesIsolatedSearxngResearchBeforeCoding()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("GO_AI_RUN_WEB_RESEARCH_LIVE"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var modelId = Environment.GetEnvironmentVariable("GO_AI_LIVE_CODING_MODEL")?.Trim();
        if (string.IsNullOrWhiteSpace(modelId))
        {
            modelId = "gpt-oss-120b";
        }
        var workspace = Path.Combine(
            Path.GetTempPath(),
            "GO-Web-Research-Live",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(
            Path.Combine(workspace, "README.md"),
            "# Web research smoke workspace\n");

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            var sessionId = $"live-web-research-{Guid.NewGuid():N}";
            await using var harness = await CodingAgentLiveTestHarness.CreateAsync(
                "staged-web-research",
                workspace,
                modelId,
                sessionId,
                timeout.Token);
            var observation = await harness.ExecuteAsync(
                sessionId,
                "Führe eine Websuche in der offiziellen LM-Studio-Dokumentation zum nativen Tool-Calling durch. "
                    + "Lies die relevanten Seiten und fasse zwei belegte Hinweise mit Titel und URL zusammen. "
                    + "Analysiere danach kurz, wie sie für diesen leeren Workspace gelten. Verändere keine Dateien.",
                "live-web-research",
                timeout.Token);

            CodingAgentLiveTestHarness.AssertSuccessful(
                observation,
                modelId,
                requireMutation: false,
                requireVerification: false);
            Assert.Equal(1, observation.ToolNames.Count(static name => name == "web.search"));
            Assert.InRange(observation.ToolNames.Count(static name => name == "web.fetch"), 1, 3);
        }
        finally
        {
            var canonicalWorkspace = Path.GetFullPath(workspace);
            var canonicalRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "GO-Web-Research-Live"));
            if (canonicalWorkspace.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(canonicalWorkspace))
            {
                Directory.Delete(canonicalWorkspace, recursive: true);
            }
        }
    }
}
