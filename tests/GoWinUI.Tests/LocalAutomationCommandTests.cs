using GoWinUI.App.Services;

namespace GoWinUI.Tests;

public sealed class LocalAutomationCommandTests : IDisposable
{
    private readonly string? _originalValue = Environment.GetEnvironmentVariable(
        LocalAutomationCommand.EnableEnvironmentVariable);

    [Fact]
    public void TryParseRejectsCommandWhenAutomationIsDisabled()
    {
        Environment.SetEnvironmentVariable(LocalAutomationCommand.EnableEnvironmentVariable, null);

        var parsed = LocalAutomationCommand.TryParse(
            "--coding-campaign-run 24e65f4c-34dd-483a-9305-bfc8b73394a3",
            out var command);

        Assert.False(parsed);
        Assert.Null(command);
    }

    [Theory]
    [InlineData("--coding-campaign-run", "RunCodingCampaign")]
    [InlineData("--coding-campaign-stop", "StopCodingCampaign")]
    public void TryParseAcceptsExactGatedCampaignCommand(
        string verb,
        string expectedAction)
    {
        Environment.SetEnvironmentVariable(LocalAutomationCommand.EnableEnvironmentVariable, "1");
        var expectedSession = Guid.Parse("24e65f4c-34dd-483a-9305-bfc8b73394a3");

        var parsed = LocalAutomationCommand.TryParse(
            $"{verb} {expectedSession:D}",
            out var command);

        Assert.True(parsed);
        Assert.NotNull(command);
        Assert.Equal(expectedAction, command.Action.ToString());
        Assert.Equal(expectedSession, command.SessionId);
    }

    [Theory]
    [InlineData("--coding-campaign-run")]
    [InlineData("--coding-campaign-run not-a-guid")]
    [InlineData("--coding-campaign-run 24e65f4c-34dd-483a-9305-bfc8b73394a3 unexpected")]
    [InlineData("--arbitrary-command 24e65f4c-34dd-483a-9305-bfc8b73394a3")]
    public void TryParseRejectsMalformedOrUnrecognizedCommands(string arguments)
    {
        Environment.SetEnvironmentVariable(LocalAutomationCommand.EnableEnvironmentVariable, "1");

        var parsed = LocalAutomationCommand.TryParse(arguments, out var command);

        Assert.False(parsed);
        Assert.Null(command);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(
            LocalAutomationCommand.EnableEnvironmentVariable,
            _originalValue);
    }
}
