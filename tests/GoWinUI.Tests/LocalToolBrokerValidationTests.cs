using System.Text.Json;
using GoAi.Contracts;
using GoWinUI.App.Services;

namespace GoWinUI.Tests;

public sealed class LocalToolBrokerValidationTests
{
    [Fact]
    public void UnicodeScalarNormalizationDropsUnpairedSurrogates()
    {
        var normalized = LocalToolBroker.NormalizeUnicodeScalarText("A\ud800B\udc00C😀");

        Assert.Equal("ABC😀", normalized);
    }

    [Fact]
    public void SessionDocumentToolsAreAccepted()
    {
        LocalToolBroker.ValidateProposal(Create(
            ClientToolNames.DocumentRead,
            ToolRiskClass.ReadOnly,
            new { scope = "session", mode = "list" }));
        LocalToolBroker.ValidateProposal(Create(
            ClientToolNames.DocumentCreate,
            ToolRiskClass.LocalMutation,
            new
            {
                operation = "create",
                reference = "bericht.md",
                format = "markdown",
                sectionId = "start",
                content = "Inhalt",
            }));
    }

    [Fact]
    public void WorkspaceDocumentScopeIsRejected()
    {
        var proposal = Create(
            ClientToolNames.DocumentRead,
            ToolRiskClass.ReadOnly,
            new { scope = "workspace", mode = "outline", reference = "README.md" });

        Assert.Throws<InvalidDataException>(() => LocalToolBroker.ValidateProposal(proposal));
    }

    [Fact]
    public void BricsCadToolsKeepTheirBoundedContract()
    {
        LocalToolBroker.ValidateProposal(Create(
            ClientToolNames.BricsCadMeasure,
            ToolRiskClass.ReadOnly,
            new { operation = "measurement.length", arguments = new { entityId = "42" } }));

        var invalid = Create(
            ClientToolNames.BricsCadMove,
            ToolRiskClass.CadMutation,
            new { operation = "process.run" });
        Assert.Throws<InvalidOperationException>(() => LocalToolBroker.ValidateProposal(invalid));
    }

    [Fact]
    public void ExpiredOrDowngradedProposalsAreRejected()
    {
        var valid = Create(
            ClientToolNames.BricsCadAction,
            ToolRiskClass.CadMutation,
            new { operation = "document.save" });

        Assert.Throws<InvalidDataException>(() => LocalToolBroker.ValidateProposal(
            valid with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) }));
        Assert.Throws<InvalidDataException>(() => LocalToolBroker.ValidateProposal(
            valid with { RiskClass = ToolRiskClass.ReadOnly }));
    }

    private static ToolProposal Create(string name, ToolRiskClass riskClass, object arguments) => new(
        "proposal-test",
        "run-test",
        name,
        JsonSerializer.SerializeToElement(arguments),
        riskClass,
        "Lokales Werkzeug ausführen",
        DateTimeOffset.UtcNow.AddMinutes(5));
}
