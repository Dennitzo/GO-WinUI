namespace GoAi.Server.Core.Security;

public sealed record IssuedApiKey(string KeyId, string PlainText, DateTimeOffset CreatedAt);
