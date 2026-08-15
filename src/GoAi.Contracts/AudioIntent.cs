namespace GoAi.Contracts;

public enum UtteranceIntent
{
    Question,
    Instruction,
    Cancel,
    Noise,
}

public sealed record UtteranceIntentRequest(string Text, string? Language = null);

public sealed record UtteranceIntentResponse(UtteranceIntent Intent, string? NormalizedText = null);
