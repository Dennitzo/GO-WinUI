namespace GoAi.Server.Core.Runtime;

public sealed record ServerLogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string EventId,
    string Message);
