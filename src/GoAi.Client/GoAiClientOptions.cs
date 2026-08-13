namespace GoAi.Client;

public sealed record GoAiClientOptions(
    Uri ServerUri,
    string ApiKey,
    string? ExpectedCaSha256 = null,
    TimeSpan? RequestTimeout = null);
