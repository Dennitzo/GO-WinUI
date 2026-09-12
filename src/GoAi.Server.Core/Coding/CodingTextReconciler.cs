using GoAi.Contracts;
using System.Text;
using System.Text.Json;

namespace GoAi.Server.Core.Coding;

/// <summary>Replays one unfinished model turn against its durable visible prefix.</summary>
internal sealed class CodingTextReconciler(int baseOffset, string visibleText = "")
{
    private static readonly JsonSerializerOptions Json = GoAiProtocol.CreateJsonOptions();
    private int _cursor;
    internal int BaseOffset { get; } = baseOffset;
    internal string VisibleText { get; private set; } = visibleText;

    internal void RestartAttempt() => _cursor = 0;

    internal TextDeltaEvent? Push(string delta)
    {
        var matching = 0;
        while (matching < delta.Length && _cursor + matching < VisibleText.Length
            && VisibleText[_cursor + matching] == delta[matching]) matching++;
        _cursor += matching;
        if (matching == delta.Length) return null;
        var addition = delta[matching..];
        var replaceFrom = _cursor < VisibleText.Length ? BaseOffset + _cursor : (int?)null;
        VisibleText = VisibleText[.._cursor] + addition;
        _cursor += addition.Length;
        return new(addition, replaceFrom);
    }

    internal TextDeltaEvent? Complete()
    {
        if (_cursor >= VisibleText.Length) return null;
        VisibleText = VisibleText[.._cursor];
        return new("", BaseOffset + _cursor);
    }

    internal static string Project(IReadOnlyList<RunEvent> events, int baseOffset = 0)
    {
        var text = new StringBuilder();
        foreach (var item in events)
        {
            if (item.Type != RunEventTypes.TextDelta) continue;
            var delta = item.Data.Deserialize<TextDeltaEvent>(Json)
                ?? throw new InvalidDataException("Das sichtbare Coding-Journal enthält ein ungültiges Textdelta.");
            if (delta.ReplaceFrom is { } replaceFrom)
            {
                // Wire revisions always carry the entire authoritative run text.
                // This also avoids positions drifting after client text sanitization.
                if (replaceFrom != 0 || delta.Delta.Length < baseOffset)
                    throw new InvalidDataException("Die Coding-Textrevision passt nicht zum gespeicherten Modellturn.");
                text.Clear();
                text.Append(delta.Delta.AsSpan(baseOffset));
            }
            else text.Append(delta.Delta);
        }
        return text.ToString();
    }

    internal static TextDeltaEvent ToAuthoritativeRevision(string currentText, TextDeltaEvent delta)
    {
        if (delta.ReplaceFrom is not { } position) return delta;
        if (position < 0 || position > currentText.Length)
            throw new InvalidDataException("Die Coding-Textrevision liegt außerhalb der gespeicherten Narration.");
        return new(currentText[..position] + delta.Delta, ReplaceFrom: 0);
    }
}
