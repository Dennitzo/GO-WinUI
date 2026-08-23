using GoWinUI.App.Services;
using System.Text.Json;

namespace GoWinUI.Tests;

public sealed class PromptDrivenCodingCampaignTests
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    private static readonly string[] DefaultAssumptions = ["Der Workspace ist die einzige Mutationsgrenze."];
    private static readonly string[] DefaultAcceptanceCriteria = ["Mindestens ein zielbezogener Checker läuft erfolgreich."];
    private static readonly string[] DefaultChecks = ["dotnet test"];

    [Fact]
    public void PromptWorkflowIsGenericAndRegisteredInCampaignCatalog()
    {
        var definition = new PromptDrivenCodingCampaignDefinition();
        var catalog = new CodingCampaignCatalog([definition]);
        var bootstrap = definition.BuildBootstrapPrompt();
        var iteration = definition.BuildIterationPrompt(3, definition.GetChallenge(3));

        Assert.Equal("prompt-workflow", definition.Descriptor.Id);
        Assert.Contains(catalog.List(), descriptor => descriptor.Id == "prompt-workflow");
        Assert.Contains(".go-campaign/prompt-workflow.json", bootstrap, StringComparison.Ordinal);
        Assert.Contains("hartcodierte Test- oder Domänenlogik", bootstrap, StringComparison.Ordinal);
        Assert.Contains("Nutzeranweisung", bootstrap, StringComparison.Ordinal);
        Assert.Contains("Neue Tests müssen", iteration, StringComparison.Ordinal);
        Assert.Contains("keine versteckte", iteration, StringComparison.Ordinal);
    }

    [Fact]
    public void CodeSpecialistExplainsPromptDerivedWorkflowContracts()
    {
        var policy = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "GoAi.Server.Core",
            "Policies",
            "TgaAgentPolicies.cs"));

        Assert.Contains(".go-campaign/prompt-workflow.json", policy, StringComparison.Ordinal);
        Assert.Contains("reproduzierbaren Prüfvertrag", policy, StringComparison.Ordinal);
        Assert.Contains("Schreibe für frei formulierte Coding-Aufgaben eigene Tests", policy, StringComparison.Ordinal);
        Assert.Contains("Workflow erstellen", policy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PromptWorkflowValidationAcceptsGenericContract()
    {
        var workspace = CreateWorkspace();
        try
        {
            WriteContract(workspace, new
            {
                schema = "go.prompt-workflow.v1",
                title = "Freier Testworkflow",
                objective = "Eine frei formulierte Coding-Aufgabe mit eigener Verifikation reproduzierbar bearbeiten.",
                iteration = 1,
                scope = new { root = "." },
                assumptions = DefaultAssumptions,
                acceptanceCriteria = DefaultAcceptanceCriteria,
                verificationCommands = new object[]
                {
                    new { purpose = "test", command = "dotnet test" },
                },
                artifacts = Array.Empty<object>(),
                openQuestions = Array.Empty<string>(),
                lastRun = new
                {
                    status = "completed",
                    changedFiles = Array.Empty<string>(),
                    checks = DefaultChecks,
                    nextAction = "Nächsten offenen Punkt wählen.",
                },
            });

            var result = await new PromptDrivenCodingCampaignDefinition().ValidateAsync(workspace);

            Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Issues));
            Assert.Empty(result.Issues);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task PromptWorkflowValidationRejectsMissingVerifiableContractFields()
    {
        var workspace = CreateWorkspace();
        try
        {
            WriteContract(workspace, new
            {
                schema = "go.prompt-workflow.v1",
                title = "Zu schwach",
                objective = "kurz",
                iteration = 0,
                scope = new { root = "." },
                assumptions = Array.Empty<string>(),
                acceptanceCriteria = Array.Empty<string>(),
                verificationCommands = Array.Empty<object>(),
                artifacts = Array.Empty<object>(),
                openQuestions = Array.Empty<string>(),
                lastRun = new { status = "draft" },
            });

            var result = await new PromptDrivenCodingCampaignDefinition().ValidateAsync(workspace);

            Assert.False(result.IsValid);
            Assert.Contains(result.Issues, issue => issue.Contains("objective", StringComparison.Ordinal));
            Assert.Contains(result.Issues, issue => issue.Contains("assumptions", StringComparison.Ordinal));
            Assert.Contains(result.Issues, issue => issue.Contains("acceptanceCriteria", StringComparison.Ordinal));
            Assert.Contains(result.Issues, issue => issue.Contains("verificationCommands", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private static string CreateWorkspace()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "GO", "PromptWorkflowTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(Path.Combine(workspace, ".go-campaign"));
        return workspace;
    }

    private static void WriteContract(string workspace, object value)
    {
        File.WriteAllText(
            Path.Combine(workspace, PromptDrivenCodingCampaignDefinition.ContractRelativePath.Replace('/', Path.DirectorySeparatorChar)),
            JsonSerializer.Serialize(value, IndentedJson));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GO.slnx"))
                || File.Exists(Path.Combine(directory.FullName, "GO-WinUI.slnx"))
                || File.Exists(Path.Combine(directory.FullName, "GO-WinUI.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Das GO-Repository wurde aus dem Testausgabeverzeichnis nicht gefunden.");
    }
}
