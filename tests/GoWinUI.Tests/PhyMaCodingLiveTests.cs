using System.Diagnostics;
using GoWinUI.App.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GoWinUI.Tests;

/// <summary>
/// Optionaler echter Dauerlauf für den PhyMa-Coding-Workflow. Der reguläre
/// Testlauf bleibt deterministisch; ein Workspace aktiviert den lokalen
/// GO-AI-Server- und Coding-Modell-Test ausdrücklich.
/// </summary>
public sealed class PhyMaCodingLiveTests
{
    private const string WorkspaceEnvironmentVariable = "GO_AI_LIVE_PHYMA_WORKSPACE";
    private const string ModelEnvironmentVariable = "GO_AI_LIVE_CODING_MODEL";
    private const string IterationsEnvironmentVariable = "GO_AI_LIVE_PHYMA_ITERATIONS";
    private const string ContinuousEnvironmentVariable = "GO_AI_LIVE_PHYMA_CONTINUOUS";
    private static readonly string[] MandatoryGates = ["formal", "symbolic", "numerical"];

    [Fact]
    [Trait("Category", "Live")]
    public async Task CodingAgentContinuouslyExtendsTheValidatedPhyMaBook()
    {
        var requestedWorkspace = Environment.GetEnvironmentVariable(WorkspaceEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(requestedWorkspace)) return;

        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedWorkspace));
        Assert.True(Directory.Exists(workspace), $"PhyMa-Live-Workspace fehlt: {workspace}");
        await EnsureGitRepositoryAsync(workspace);
        var modelId = Environment.GetEnvironmentVariable(ModelEnvironmentVariable)?.Trim();
        if (string.IsNullOrWhiteSpace(modelId)) modelId = "qwen3-coder-next";
        var continuous = string.Equals(
            Environment.GetEnvironmentVariable(ContinuousEnvironmentVariable),
            "1",
            StringComparison.Ordinal);
        var iterationLimit = ParseIterationLimit(
            Environment.GetEnvironmentVariable(IterationsEnvironmentVariable),
            continuous);
        using var timeout = new CancellationTokenSource(continuous ? TimeSpan.FromDays(30) : TimeSpan.FromHours(12));
        var sessionId = $"live-phyma-{Guid.NewGuid():N}";
        await using var harness = await CodingAgentLiveTestHarness.CreateAsync(
            "phyma",
            workspace,
            modelId,
            sessionId,
            timeout.Token);
        var campaign = new PhyMaCodingCampaignDefinition(new CodingProofVerifier());
        harness.Record("workflow.configuration", new
        {
            continuous,
            iterationLimit,
            book = PhyMaCodingCampaignDefinition.BookRelativePath,
            mandatoryGates = MandatoryGates,
        });

        if (!campaign.HasFoundation(workspace))
        {
            var bootstrap = await harness.ExecuteAsync(
                sessionId,
                campaign.BuildBootstrapPrompt(),
                "live-phyma-bootstrap",
                timeout.Token);
            CodingAgentLiveTestHarness.AssertSuccessful(bootstrap, modelId);
            await ValidateAndRepairAsync(campaign, harness, workspace, sessionId, 0, "Grundaufbau", modelId, timeout.Token);
        }

        var startingRevision = campaign.ReadIteration(workspace);
        for (var localIteration = 0; localIteration < iterationLimit; localIteration++)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var revision = startingRevision + localIteration;
            var challenge = campaign.GetChallenge(revision);
            harness.Record("iteration.started", new { revision, challenge });
            var observation = await harness.ExecuteAsync(
                sessionId,
                campaign.BuildIterationPrompt(revision, challenge),
                $"live-phyma-{revision}",
                timeout.Token);
            CodingAgentLiveTestHarness.AssertSuccessful(observation, modelId);
            await ValidateAndRepairAsync(
                campaign,
                harness,
                workspace,
                sessionId,
                revision,
                challenge,
                modelId,
                timeout.Token);
        }
    }

    private static async Task ValidateAndRepairAsync(
        PhyMaCodingCampaignDefinition campaign,
        CodingAgentLiveTestHarness harness,
        string workspace,
        string sessionId,
        int revision,
        string challenge,
        string modelId,
        CancellationToken cancellationToken)
    {
        var validation = await campaign.ValidateAsync(workspace, cancellationToken);
        harness.Record("iteration.validation", new
        {
            revision,
            validation.IsValid,
            validation.Issues,
            proofs = validation.Proofs.Select(proof => new
            {
                proof.CaseId,
                proof.ManifestPath,
                proof.Kind,
                proof.Passed,
            }),
        });
        for (var correctionAttempt = 1; !validation.IsValid && correctionAttempt <= 2; correctionAttempt++)
        {
            var correction = await harness.ExecuteAsync(
                sessionId,
                campaign.BuildCorrectionPrompt(revision, challenge, validation.Issues),
                $"live-phyma-correction-{revision}-{correctionAttempt}",
                cancellationToken);
            CodingAgentLiveTestHarness.AssertSuccessful(correction, modelId);
            validation = await campaign.ValidateAsync(workspace, cancellationToken);
            harness.Record("iteration.revalidation", new
            {
                revision,
                correctionAttempt,
                validation.IsValid,
                validation.Issues,
            });
        }

        Assert.True(
            validation.IsValid,
            "PhyMa-Abnahme fehlgeschlagen:\n- " + string.Join("\n- ", validation.Issues)
            + $"\nLive-Protokoll: {harness.LogPath}");
        var bookPath = Path.Combine(workspace, "solutions", "PhyMa.md");
        Assert.True(File.Exists(bookPath));
        using var exporter = new CodingSolutionPdfExporter(NullLogger<CodingSolutionPdfExporter>.Instance);
        var pdfPath = await exporter.EnsureCurrentAsync(bookPath, sourceChanged: true, cancellationToken);
        Assert.NotNull(pdfPath);
        Assert.True(File.Exists(pdfPath), $"PhyMa-PDF fehlt: {pdfPath}");
        var pdfLength = new FileInfo(pdfPath).Length;
        Assert.True(pdfLength >= 1024, $"PhyMa-PDF ist zu klein: {pdfPath}");
        harness.Record("solution.pdf", new
        {
            revision,
            path = Path.GetRelativePath(workspace, pdfPath).Replace('\\', '/'),
            bytes = pdfLength,
        });
    }

    private static int ParseIterationLimit(string? value, bool continuous)
    {
        if (continuous) return int.MaxValue;
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : 1;
    }

    private static async Task EnsureGitRepositoryAsync(string workspace)
    {
        if (Directory.Exists(Path.Combine(workspace, ".git"))) return;
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workspace,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("init");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Git-Repository für den PhyMa-Live-Test konnte nicht initialisiert werden.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var detail = string.Join(Environment.NewLine, await output, await error).Trim();
        Assert.True(process.ExitCode == 0, $"git init fehlgeschlagen: {detail}");
    }
}
