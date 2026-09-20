using System.Text;
using GoAi.Contracts;

namespace GoWinUI.App.Services;

// Consumes only cumulative assistant-message text. Tool inputs/results never enter
// this buffer. A replay cannot speak an already consumed prefix a second time.
internal sealed class SpeechStreamingTextBuffer(string initialContent = "")
{
    private string _source = initialContent;
    private int _consumed = initialContent.Length;
    private bool _completed;
    private (char Marker, int Length) _initialFence = FenceAtEnd(initialContent);

    public bool HasConflictingRevision { get; private set; }

    public IReadOnlyList<string> Take(string content, bool complete = false, bool flushSentence = false, bool flushBlock = false)
    {
        if (_completed || HasConflictingRevision) return [];
        if (_source.StartsWith(content, StringComparison.Ordinal) && content.Length < _source.Length) return [];
        if (content.Length < _consumed || !content.AsSpan(0, _consumed).SequenceEqual(_source.AsSpan(0, _consumed)))
        {
            HasConflictingRevision = true;
            return [];
        }
        _source = content;
        var output = new List<string>();
        while (_consumed < _source.Length)
        {
            var end = FindBoundary(_source, _consumed, complete || flushBlock, flushSentence, _initialFence);
            if (end <= _consumed) break;
            var text = NarrationOnly(_source[_consumed..end], _initialFence);
            _consumed = end;
            _initialFence = default;
            if (!string.IsNullOrWhiteSpace(text)) output.Add(text);
        }
        _completed = complete;
        return output;
    }

    private static int FindBoundary(string text, int start, bool complete, bool flushSentence, (char Marker, int Length) initialFence)
    {
        var fence = initialFence.Marker;
        var fenceLength = initialFence.Length;
        var inlineTicks = 0;
        var brackets = 0;
        var parentheses = 0;
        for (var index = start; index < text.Length; index++)
        {
            var character = text[index];
            var lineStart = index == 0 || text[index - 1] == '\n';
            if (lineStart)
            {
                var marker = index;
                while (marker < text.Length && text[marker] is ' ' or '\t') marker++;
                var count = CountMarker(text, marker);
                if (count >= 3 && fence == '\0')
                {
                    fence = text[marker];
                    fenceLength = count;
                    index = marker + count - 1;
                    continue;
                }
                if (fence != '\0' && count >= fenceLength && text[marker] == fence)
                {
                    var end = text.IndexOf('\n', marker + count);
                    if (end < 0 && !complete) return -1;
                    var lineEnd = end < 0 ? text.Length : end;
                    if (text.AsSpan(marker + count, lineEnd - marker - count).Trim().Length == 0)
                        return end < 0 ? text.Length : end + 1;
                }
            }
            if (fence != '\0') continue;
            if (character == '`')
            {
                var count = CountMarker(text, index);
                if (inlineTicks == 0) inlineTicks = count;
                else if (inlineTicks == count) inlineTicks = 0;
                index += count - 1;
                continue;
            }
            if (inlineTicks != 0) continue;
            if (character == '[') brackets++;
            else if (character == ']' && brackets > 0) brackets--;
            else if (character == '(') parentheses++;
            else if (character == ')' && parentheses > 0) parentheses--;
            if (brackets != 0 || parentheses != 0) continue;
            if (character == '\n') return index + 1;
            if (character is '.' or '!' or '?' or '。' or '！' or '？')
            {
                if (GermanSpeechAbbreviations.IsAbbreviationPeriod(text, index)) continue;
                var end = index + 1;
                while (end < text.Length && text[end] is '.' or '!' or '?' or '"' or '\'' or '”' or '“' or '»' or '«' or '*' or '_') end++;
                if (end < text.Length && char.IsWhiteSpace(text[end])) return end + 1;
                if (end == text.Length && flushSentence) return end;
            }
            if (index - start >= 600 && char.IsWhiteSpace(character)) return index + 1;
        }
        return complete ? text.Length : -1;
    }

    private static int CountMarker(string text, int start)
    {
        if (start >= text.Length || text[start] is not ('`' or '~')) return 0;
        var end = start + 1;
        while (end < text.Length && text[end] == text[start]) end++;
        return end - start;
    }

    private static (char Marker, int Length) FenceAtEnd(string markdown)
    {
        char marker = '\0';
        var length = 0;
        foreach (var line in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var trimmed = line.TrimStart();
            var count = CountMarker(trimmed, 0);
            if (count >= 3 && marker == '\0') { marker = trimmed[0]; length = count; }
            else if (count >= length && marker != '\0' && trimmed.Length > 0 && trimmed[0] == marker
                && trimmed.AsSpan(count).Trim().Length == 0) { marker = '\0'; length = 0; }
        }
        return (marker, length);
    }

    private static string NarrationOnly(string markdown, (char Marker, int Length) initialFence)
    {
        var text = new StringBuilder();
        var fence = initialFence.Marker;
        var fenceLength = initialFence.Length;
        foreach (var line in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var trimmed = line.TrimStart();
            var count = CountMarker(trimmed, 0);
            if (fence == '\0' && count >= 3)
            {
                fence = trimmed[0];
                fenceLength = count;
                continue;
            }
            if (fence != '\0')
            {
                if (count >= fenceLength && trimmed[0] == fence && trimmed.AsSpan(count).Trim().Length == 0) fence = '\0';
                continue;
            }
            text.AppendLine(line);
        }
        return MicrophoneTranscriptionService.PrepareSpeechText(text.ToString());
    }
}
