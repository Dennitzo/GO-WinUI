using GoAi.Contracts;
using GoWinUI.App.Services;
using System.Text.Json;

namespace GoWinUI.Tests;

public sealed class LocalToolBrokerValidationTests
{
    private static readonly string[] VersionArguments = ["--version"];
    private static readonly string[] VoiceSearchTerms = ["voice", "speech", "SpeechRecognition"];
    private static readonly string[] AllFilesGlob = ["**/*"];

    [Fact]
    public void CodeRunPresetIsAcceptedWithAWorkspaceRelativeTarget()
    {
        var now = DateTimeOffset.UtcNow;
        var proposal = Create(
            ClientToolNames.ProcessRunPreset,
            ToolRiskClass.Process,
            new { preset = "code.run", target = "test.py" },
            now);

        LocalToolBroker.ValidateProposal(proposal, now);
    }

    [Theory]
    [InlineData("cargo", "test")]
    [InlineData("zig", "build")]
    [InlineData("cmake", "build")]
    [InlineData("java", "start")]
    [InlineData("ruby", "start")]
    public void DirectWorkspaceProcessesAreNotRestrictedToSpecificLanguages(
        string executable,
        string purpose)
    {
        var now = DateTimeOffset.UtcNow;
        var proposal = Create(
            ClientToolNames.ProcessRun,
            ToolRiskClass.Process,
            new
            {
                executable,
                arguments = VersionArguments,
                workingDirectory = ".",
                purpose,
                startMode = "wait",
            },
            now);

        LocalToolBroker.ValidateProposal(proposal, now);
    }

    [Fact]
    public void MultiSearchAndArbitraryFileExtensionsAreAccepted()
    {
        var now = DateTimeOffset.UtcNow;
        var search = Create(
            ClientToolNames.FileSystemSearch,
            ToolRiskClass.ReadOnly,
            new
            {
                path = ".",
                queries = VoiceSearchTerms,
                matchMode = "literal",
                includeGlobs = AllFilesGlob,
                contextLines = 2,
            },
            now);
        var read = Create(
            ClientToolNames.FileSystemReadMany,
            ToolRiskClass.ReadOnly,
            new
            {
                items = new[]
                {
                    new { path = "firmware/main.zig", startLine = 1, endLine = 500 },
                    new { path = "config/toolchain.customlang", startLine = 1, endLine = 500 },
                },
            },
            now);

        LocalToolBroker.ValidateProposal(search, now);
        LocalToolBroker.ValidateProposal(read, now);
    }

    [Theory]
    [InlineData("/workspace/primzahlen_bis_1000.py", "primzahlen_bis_1000.py")]
    [InlineData("workspace/primzahlen_bis_1000.py", "primzahlen_bis_1000.py")]
    [InlineData("primzahlen_bis_1000.py", "primzahlen_bis_1000.py")]
    public void ModelWorkspaceAliasesBecomeClientRelativePaths(string input, string expected)
    {
        Assert.Equal(expected, LocalToolBroker.NormalizeWorkspaceAlias(input));
    }

    [Fact]
    public void ReadOnlyFileProposalWithContractArgumentsIsAccepted()
    {
        var now = DateTimeOffset.UtcNow;
        var proposal = Create(
            ClientToolNames.FileSystemReadText,
            ToolRiskClass.ReadOnly,
            new { path = "planung/heizung.txt" },
            now);

        LocalToolBroker.ValidateProposal(proposal, now);
    }

    [Fact]
    public void AdditionalPropertiesAndRiskDowngradesAreRejectedLocally()
    {
        var now = DateTimeOffset.UtcNow;
        var additional = Create(
            ClientToolNames.FileSystemReadText,
            ToolRiskClass.ReadOnly,
            new { path = "planung.txt", unexpected = true },
            now);
        var downgraded = Create(
            ClientToolNames.FileSystemProposeDelete,
            ToolRiskClass.ReadOnly,
            new { path = "planung.txt" },
            now);

        Assert.Throws<InvalidDataException>(() => LocalToolBroker.ValidateProposal(additional, now));
        Assert.Throws<InvalidDataException>(() => LocalToolBroker.ValidateProposal(downgraded, now));
    }

    [Fact]
    public void ExpiredAndUnversionedProcessProposalsAreRejectedLocally()
    {
        var now = DateTimeOffset.UtcNow;
        var expired = Create(
            ClientToolNames.FileSystemList,
            ToolRiskClass.ReadOnly,
            new { path = "." },
            now) with { ExpiresAt = now.AddSeconds(-1) };
        var freeShell = Create(
            ClientToolNames.ProcessRunPreset,
            ToolRiskClass.Process,
            new { preset = "powershell.freeShell" },
            now);

        Assert.Throws<InvalidDataException>(() => LocalToolBroker.ValidateProposal(expired, now));
        Assert.Throws<InvalidDataException>(() => LocalToolBroker.ValidateProposal(freeShell, now));
    }

    [Fact]
    public void BricsCadOperationsMustMatchTheTypedClientTool()
    {
        var now = DateTimeOffset.UtcNow;
        var valid = Create(
            ClientToolNames.BricsCadAction,
            ToolRiskClass.CadMutation,
            new { operation = "layers.create", arguments = new { name = "TGA" } },
            now);
        var invalid = Create(
            ClientToolNames.BricsCadMeasure,
            ToolRiskClass.ReadOnly,
            new { operation = "measurement.runAnything", arguments = new { } },
            now);

        LocalToolBroker.ValidateProposal(valid, now);
        Assert.Throws<InvalidOperationException>(() => LocalToolBroker.ValidateProposal(invalid, now));
    }

    private static ToolProposal Create(
        string name,
        ToolRiskClass risk,
        object arguments,
        DateTimeOffset now) => new(
            "proposal-" + Guid.NewGuid().ToString("N"),
            "run-" + Guid.NewGuid().ToString("N"),
            name,
            JsonSerializer.SerializeToElement(arguments, GoAiProtocol.CreateJsonOptions()),
            risk,
            "Lokalen Vertrag prüfen",
            now.AddMinutes(5));
}
