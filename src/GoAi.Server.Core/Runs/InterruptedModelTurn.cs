namespace GoAi.Server.Core.Runs;

public sealed partial class RunProcessor
{
    internal static bool TryParseInterruptedDeepSeekTail(string? raw, bool reasoningEnabled,
        out string content, out string reasoning)
    {
        content = reasoning = string.Empty;
        if (raw is null || raw.Contains('\uFFFD')) return false;
        const string end = "<｜end▁of▁sentence｜>";
        if (raw.EndsWith(end, StringComparison.Ordinal)) raw = raw[..^end.Length];
        // A partially emitted tool protocol cannot safely become a chat message.
        // Never manufacture a tool result from raw native token state.
        if (raw.Contains("<｜", StringComparison.Ordinal) || raw.Contains("<|", StringComparison.Ordinal)) return false;
        if (!reasoningEnabled)
        {
            if (raw.Contains("<think>", StringComparison.Ordinal) || raw.Contains("</think>", StringComparison.Ordinal)) return false;
            content = raw;
            return true;
        }
        var boundary = raw.IndexOf("</think>", StringComparison.Ordinal);
        if (boundary < 0) reasoning = raw;
        else
        {
            reasoning = raw[..boundary];
            content = raw[(boundary + "</think>".Length)..];
        }
        return true;
    }
}
