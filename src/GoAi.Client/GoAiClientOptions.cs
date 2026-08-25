namespace GoAi.Client;

public sealed record GoAiClientOptions(
    Uri ServerUri,
    string? ClientId = null,
    TimeSpan? RequestTimeout = null);
