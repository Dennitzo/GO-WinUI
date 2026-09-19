namespace GoAi.Contracts;

/// <summary>An additional instruction for the same active conversation, model and workspace.</summary>
public sealed record RunSteeringRequest(string SessionId, string InputId, string Text);

public sealed record RunSteeringAccepted(string RunId, string InputId, long Sequence, string State, bool Duplicate);

/// <summary>VisibleTextOffset is an absolute character offset in canonical persisted main text (RunVisibleText.Canonicalize), not raw model bytes.</summary>
public sealed record RunSteeringEvent(string InputId, long Sequence, string SessionId, string Text,
    int? VisibleTextOffset = null);

public static class RunSteeringEventTypes
{
    public const string Accepted = "run.steering.accepted";
    public const string Applied = "run.steering.applied";
}
