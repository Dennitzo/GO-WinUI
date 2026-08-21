namespace GoWinUI.App.Services;

internal enum LocalAutomationAction
{
    RunCodingCampaign,
    StopCodingCampaign,
}

internal sealed record LocalAutomationCommand(LocalAutomationAction Action, Guid SessionId)
{
    internal const string EnableEnvironmentVariable = "GO_ENABLE_LOCAL_AUTOMATION";

    public static bool TryParse(string? arguments, out LocalAutomationCommand? command)
    {
        command = null;
        if (!string.Equals(
                Environment.GetEnvironmentVariable(EnableEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            return false;
        }

        var tokens = (arguments ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length != 2 || !Guid.TryParse(tokens[1], out var sessionId))
        {
            return false;
        }

        var action = tokens[0] switch
        {
            "--coding-campaign-run" => LocalAutomationAction.RunCodingCampaign,
            "--coding-campaign-stop" => LocalAutomationAction.StopCodingCampaign,
            _ => (LocalAutomationAction?)null,
        };
        if (action is null)
        {
            return false;
        }

        command = new(action.Value, sessionId);
        return true;
    }
}
