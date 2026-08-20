using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using GoAi.Contracts;

namespace GoWinUI.App.Services;

public enum SpeechPlaybackState
{
    Buffering,
    Playing,
    Paused,
    Completed,
    Cancelled,
}

public sealed record SpeechSourceUnit(
    string Id,
    string BlockId,
    string Kind,
    int BlockIndex,
    int OrdinalInBlock,
    int Start,
    int Length,
    string Text,
    string SpeechText);

public sealed record PreparedSpeechSegment(
    string Id,
    string Text,
    IReadOnlyList<string> SourceUnitIds,
    string? PlaybackBatchId = null);

public sealed record SpeechPlaybackProgress(
    Guid SessionId,
    Guid? SourceMessageId,
    string SourceKind,
    Guid PlaybackId,
    long EventSequence,
    int SegmentIndex,
    int SegmentCount,
    IReadOnlyList<string> SourceUnitIds,
    SpeechPlaybackState State,
    IReadOnlyList<SpeechSourceUnit>? SourceUnits = null);

public sealed record SpeechStartAnchor(string Kind, int BlockIndex);

internal sealed record SpeechSegmentPlaybackUpdate(
    int SegmentIndex,
    SpeechPlaybackState State,
    string? Provider = null,
    IReadOnlyList<int>? SegmentIndexes = null);

internal sealed record SpeechPlaybackBatchPlan(
    string Id,
    IReadOnlyList<int> SegmentIndexes,
    int PauseAfterMilliseconds = 180);

internal static class SpeechPlaybackProgressBridge
{
    public static object ToPayload(SpeechPlaybackProgress progress) => new
    {
        progress.SessionId,
        progress.SourceMessageId,
        progress.SourceKind,
        progress.PlaybackId,
        progress.EventSequence,
        progress.SegmentIndex,
        progress.SegmentCount,
        progress.SourceUnitIds,
        state = progress.State.ToString().ToLowerInvariant(),
        sourceUnits = progress.SourceUnits?.Select(static unit => new
        {
            unit.Id,
            unit.BlockId,
            unit.Kind,
            unit.BlockIndex,
            unit.OrdinalInBlock,
            unit.Start,
            unit.Length,
            unit.Text,
        }),
    };
}

internal static partial class SpeechSourceSegmentation
{
    internal const int MaximumSegmentCharacters = 3_000;

    public static IReadOnlyList<SpeechSourceUnit> CreateUnits(string? markdown)
    {
        var source = (markdown ?? string.Empty).ReplaceLineEndings("\n");
        if (string.IsNullOrWhiteSpace(source))
        {
            return [];
        }

        var lines = ReadLines(source);
        var units = new List<SpeechSourceUnit>();
        var blockCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var blockNumber = 0;
        var unitNumber = 0;

        void AddBlock(
            string kind,
            int start,
            int length,
            string raw,
            string? displayText = null,
            string? speechText = null,
            bool atomic = false)
        {
            var visible = NormalizeVisibleText(displayText ?? PlainMarkdownText(raw));
            var spoken = NormalizeVisibleText(speechText ?? MicrophoneTranscriptionService.PrepareSpeechText(raw));
            if (visible.Length == 0 && spoken.Length == 0)
            {
                return;
            }

            if (visible.Length == 0)
            {
                visible = spoken;
            }
            if (spoken.Length == 0)
            {
                spoken = visible;
            }

            var blockIndex = blockCounts.GetValueOrDefault(kind);
            blockCounts[kind] = blockIndex + 1;
            var blockId = $"b{++blockNumber:0000}";
            var spokenParts = atomic ? [spoken] : SplitForPlaybackParts(spoken);
            var visibleParts = atomic ? [visible] : SplitForPlayback(visible);
            if (spokenParts.Count == 0)
            {
                spokenParts = [spoken];
            }
            if (visibleParts.Count == 0)
            {
                visibleParts = [visible];
            }

            for (var partIndex = 0; partIndex < spokenParts.Count; partIndex++)
            {
                var matchingVisible = partIndex < visibleParts.Count
                    ? visibleParts[partIndex]
                    : partIndex == 0
                        ? visible
                        : visibleParts[^1];
                units.Add(new(
                    $"u{++unitNumber:0000}",
                    blockId,
                    kind,
                    blockIndex,
                    partIndex,
                    start,
                    length,
                    matchingVisible,
                    spokenParts[partIndex]));
            }
        }

        for (var index = 0; index < lines.Count;)
        {
            var line = lines[index];
            var trimmed = line.Text.Trim();
            if (trimmed.Length == 0)
            {
                index++;
                continue;
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                var startIndex = index;
                var content = new StringBuilder();
                index++;
                while (index < lines.Count && !lines[index].Text.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    if (content.Length > 0) content.Append('\n');
                    content.Append(lines[index].Text);
                    index++;
                }
                if (index < lines.Count) index++;
                var block = Slice(source, lines, startIndex, index);
                AddBlock(
                    "code",
                    block.Start,
                    block.Length,
                    block.Text,
                    content.ToString(),
                    "Codeblock ausgelassen.",
                    atomic: true);
                continue;
            }

            if (StartsDisplayMath(trimmed))
            {
                var startIndex = index;
                var delimiter = trimmed.StartsWith("\\[", StringComparison.Ordinal) ? "\\]" : "$$";
                var firstHasClosing = trimmed.Length > delimiter.Length
                    && trimmed[delimiter.Length..].Contains(delimiter, StringComparison.Ordinal);
                index++;
                while (!firstHasClosing
                    && index < lines.Count
                    && !lines[index].Text.Contains(delimiter, StringComparison.Ordinal))
                {
                    index++;
                }
                if (!firstHasClosing && index < lines.Count) index++;
                var block = Slice(source, lines, startIndex, index);
                AddBlock("math", block.Start, block.Length, block.Text, block.Text, atomic: true);
                continue;
            }

            if (index + 1 < lines.Count && IsTableRow(line.Text) && IsTableSeparator(lines[index + 1].Text))
            {
                var header = TableCells(line.Text);
                AddBlock(
                    "tableRow",
                    line.Start,
                    line.Text.Length,
                    line.Text,
                    string.Join(' ', header),
                    string.Join(", ", header),
                    atomic: true);
                index += 2;
                while (index < lines.Count && IsTableRow(lines[index].Text))
                {
                    var row = lines[index];
                    var cells = TableCells(row.Text);
                    AddBlock(
                        "tableRow",
                        row.Start,
                        row.Text.Length,
                        row.Text,
                        string.Join(' ', cells),
                        string.Join(", ", cells),
                        atomic: true);
                    index++;
                }
                continue;
            }

            var heading = HeadingRegex().Match(line.Text);
            if (heading.Success)
            {
                AddBlock("heading", line.Start, line.Text.Length, line.Text, heading.Groups[1].Value);
                index++;
                continue;
            }

            var unordered = UnorderedListRegex().Match(line.Text);
            var ordered = OrderedListRegex().Match(line.Text);
            if (unordered.Success || ordered.Success)
            {
                var value = unordered.Success ? unordered.Groups[1].Value : ordered.Groups[1].Value;
                AddBlock("listItem", line.Start, line.Text.Length, line.Text, value);
                index++;
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                var startIndex = index;
                var quote = new StringBuilder();
                while (index < lines.Count && lines[index].Text.TrimStart().StartsWith('>'))
                {
                    if (quote.Length > 0) quote.Append(' ');
                    quote.Append(QuotePrefixRegex().Replace(lines[index].Text, string.Empty));
                    index++;
                }
                var block = Slice(source, lines, startIndex, index);
                AddBlock("quote", block.Start, block.Length, block.Text, quote.ToString());
                continue;
            }

            var paragraphStart = index;
            while (index < lines.Count)
            {
                if (string.IsNullOrWhiteSpace(lines[index].Text)) break;
                if (index > paragraphStart && IsStructuralStart(lines, index)) break;
                index++;
            }
            var paragraphBlock = Slice(source, lines, paragraphStart, index);
            AddBlock("paragraph", paragraphBlock.Start, paragraphBlock.Length, paragraphBlock.Text);
        }

        return units;
    }

    public static IReadOnlyList<PreparedSpeechSegment> CreateDirectSegments(
        IReadOnlyList<SpeechSourceUnit> units,
        string? fallbackText = null)
    {
        var directSegments = new List<PreparedSpeechSegment>();
        foreach (var unit in units)
        {
            var spoken = MicrophoneTranscriptionService.PrepareSpeechText(unit.SpeechText);
            if (string.IsNullOrWhiteSpace(spoken))
            {
                continue;
            }
            directSegments.Add(new(
                $"source-{directSegments.Count + 1:0000}",
                spoken,
                [unit.Id],
                PlaybackBatchId: unit.BlockId));
        }

        var output = NormalizePreparedSegments(directSegments);
        if (output.Count == 0 && !string.IsNullOrWhiteSpace(fallbackText))
        {
            foreach (var part in SplitForPlayback(fallbackText))
            {
                directSegments.Add(new(
                    $"source-{directSegments.Count + 1:0000}",
                    MicrophoneTranscriptionService.PrepareSpeechText(part),
                    []));
            }
            output = NormalizePreparedSegments(directSegments);
        }
        return output;
    }


    internal static bool ContainsForbiddenSpeechQuotation(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (!IsSpeechQuotation(character))
            {
                continue;
            }
            var previousIsWord = index > 0 && char.IsLetterOrDigit(value[index - 1]);
            var nextIsWord = index + 1 < value.Length && char.IsLetterOrDigit(value[index + 1]);
            if (!IsApostrophe(character) || !previousIsWord || !nextIsWord)
            {
                return true;
            }
        }
        return false;
    }

    internal static string NormalizeSpeechPunctuation(string? value)
    {
        var text = QuotePrefixRegex().Replace(value ?? string.Empty, string.Empty);
        if (text.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (!IsSpeechQuotation(character))
            {
                builder.Append(character);
                continue;
            }

            var previousIsWord = index > 0 && char.IsLetterOrDigit(text[index - 1]);
            var nextIsWord = index + 1 < text.Length && char.IsLetterOrDigit(text[index + 1]);
            if (IsApostrophe(character) && previousIsWord && nextIsWord)
            {
                builder.Append('\'');
            }
            else if (builder.Length > 0 && !char.IsWhiteSpace(builder[^1]))
            {
                builder.Append(' ');
            }
        }

        text = WhitespaceRegex().Replace(builder.ToString(), " ").Trim();
        text = SpaceBeforePunctuationRegex().Replace(text, "$1");
        text = RepeatedCommaRegex().Replace(text, ",");
        return text.Trim(' ', ',');
    }

    private static bool IsSpeechQuotation(char character) => character is
        '"' or '\'' or '\u2018' or '\u2019' or '\u201A' or '\u201B'
        or '\u201C' or '\u201D' or '\u201E' or '\u201F'
        or '\u00AB' or '\u00BB' or '\u2039' or '\u203A';

    private static bool IsApostrophe(char character) =>
        character is '\'' or '\u2018' or '\u2019' or '\u201B';


    public static IReadOnlyList<PreparedSpeechSegment> NormalizePreparedSegments(
        IEnumerable<PreparedSpeechSegment> segments)
    {
        var output = new List<PreparedSpeechSegment>();
        foreach (var segment in segments)
        {
            var ids = segment.SourceUnitIds
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var parts = SplitForPlayback(segment.Text);
            var playbackBatchId = string.IsNullOrWhiteSpace(segment.PlaybackBatchId)
                ? null
                : segment.PlaybackBatchId.Trim();
            for (var partIndex = 0; partIndex < parts.Count; partIndex++)
            {
                // Splitting happens before quotation marks are removed so the
                // visible source mapping remains stable. A closing quote followed
                // by a comma can therefore become a punctuation-only helper part.
                // Such a part must never reach the TTS request as an empty string.
                var spokenText = NormalizeSpeechPunctuation(parts[partIndex]);
                if (spokenText.Length == 0)
                {
                    continue;
                }
                output.Add(new(
                    $"s{output.Count + 1:0000}",
                    spokenText,
                    ids,
                    playbackBatchId));
            }
        }
        return output;
    }

    internal static IReadOnlyList<SpeechPlaybackBatchPlan> CreatePlaybackBatches(
        IReadOnlyList<PreparedSpeechSegment> segments)
    {
        var output = new List<SpeechPlaybackBatchPlan>(segments.Count);
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            var sameVisibleBlock = index + 1 < segments.Count
                && !string.IsNullOrWhiteSpace(segment.PlaybackBatchId)
                && string.Equals(
                    segment.PlaybackBatchId,
                    segments[index + 1].PlaybackBatchId,
                    StringComparison.Ordinal);
            output.Add(new(
                $"sentence-{index + 1:0000}-{segment.Id}",
                [index],
                sameVisibleBlock ? 40 : 180));
        }
        return output;
    }

    internal static string PrepareForSynthesis(PreparedSpeechSegment segment)
    {
        return NormalizeSpeechPunctuation(segment.Text);
    }


    internal static IReadOnlyList<string> SplitForPlayback(string? value) =>
        SplitForPlaybackParts(value);

    private static List<string> SplitForPlaybackParts(string? value)
    {
        var text = NormalizeVisibleText(value ?? string.Empty);
        if (text.Length == 0)
        {
            return [];
        }

        var sentences = new List<string>();
        AddSentenceParts(text, sentences);
        return sentences;
    }

    private static void AddSentenceParts(
        string text,
        List<string> sentences)
    {
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('.' or '!' or '?')) continue;
            var end = index + 1;
            while (end < text.Length && text[end] is '.' or '!' or '?') end++;
            while (end < text.Length && IsSpeechQuotation(text[end])) end++;
            if (end < text.Length && !char.IsWhiteSpace(text[end])) continue;
            AddLongPart(text[start..end], sentences);
            start = end;
            while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
            index = start - 1;
        }
        if (start < text.Length)
        {
            AddLongPart(text[start..], sentences);
        }
    }

    private static void AddLongPart(
        string value,
        List<string> output)
    {
        var remaining = value.Trim().TrimStart(',', ';', ':');
        while (remaining.Length > MaximumSegmentCharacters)
        {
            var boundary = FindSpeechBoundary(remaining, MaximumSegmentCharacters);
            output.Add(remaining[..boundary].Trim());
            remaining = remaining[boundary..].TrimStart();
        }
        if (remaining.Length > 0)
        {
            output.Add(remaining);
        }
    }

    private static int FindSpeechBoundary(string value, int maximum)
    {
        var minimum = maximum / 2;
        for (var index = Math.Min(maximum, value.Length - 1); index >= minimum; index--)
        {
            if (value[index] is ';' or ':' or ',' || char.IsWhiteSpace(value[index]))
            {
                return value[index] is ';' or ':' or ',' ? index + 1 : index;
            }
        }
        return Math.Min(maximum, value.Length);
    }

    private static bool IsStructuralStart(IReadOnlyList<MarkdownLine> lines, int index)
    {
        var value = lines[index].Text;
        var trimmed = value.Trim();
        return trimmed.StartsWith("```", StringComparison.Ordinal)
            || StartsDisplayMath(trimmed)
            || HeadingRegex().IsMatch(value)
            || UnorderedListRegex().IsMatch(value)
            || OrderedListRegex().IsMatch(value)
            || trimmed.StartsWith('>')
            || index + 1 < lines.Count && IsTableRow(value) && IsTableSeparator(lines[index + 1].Text);
    }

    private static bool StartsDisplayMath(string value) =>
        value.StartsWith("$$", StringComparison.Ordinal)
        || value.StartsWith("\\[", StringComparison.Ordinal);

    private static bool IsTableRow(string value) => TableCells(value).Length > 1;

    private static bool IsTableSeparator(string value)
    {
        var cells = TableCells(value);
        return cells.Length > 0 && cells.All(static cell => TableSeparatorCellRegex().IsMatch(cell));
    }

    private static string[] TableCells(string value)
    {
        var trimmed = value.Trim().Trim('|');
        if (!trimmed.Contains('|', StringComparison.Ordinal)) return [];
        return trimmed.Split('|').Select(static cell => cell.Trim()).ToArray();
    }

    private static string PlainMarkdownText(string value)
    {
        var text = ImageRegex().Replace(value, "$1");
        text = LinkRegex().Replace(text, "$1");
        text = InlineCodeRegex().Replace(text, "$1");
        text = MarkdownMarkersRegex().Replace(text, string.Empty);
        return WebUtility.HtmlDecode(text);
    }

    private static string NormalizeVisibleText(string value) =>
        WhitespaceRegex().Replace(value.Replace('\u00a0', ' '), " ").Trim();

    private static List<MarkdownLine> ReadLines(string source)
    {
        var output = new List<MarkdownLine>();
        var start = 0;
        while (start <= source.Length)
        {
            var end = source.IndexOf('\n', start);
            if (end < 0) end = source.Length;
            output.Add(new(source[start..end], start));
            if (end == source.Length) break;
            start = end + 1;
        }
        return output;
    }

    private static MarkdownSlice Slice(
        string source,
        IReadOnlyList<MarkdownLine> lines,
        int startLine,
        int endLineExclusive)
    {
        var start = lines[startLine].Start;
        var end = endLineExclusive >= lines.Count
            ? source.Length
            : lines[endLineExclusive].Start;
        while (end > start && source[end - 1] == '\n') end--;
        return new(source[start..end], start, end - start);
    }

    private sealed record MarkdownLine(string Text, int Start);

    private sealed record MarkdownSlice(string Text, int Start, int Length);

    [GeneratedRegex(@"^\s*#{1,4}\s+(.+?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^\s*[-*\u2022]\s+(.+?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex UnorderedListRegex();

    [GeneratedRegex(@"^\s*\d+[.)]\s+(.+?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex OrderedListRegex();

    [GeneratedRegex(@"^\s*>\s?", RegexOptions.CultureInvariant)]
    private static partial Regex QuotePrefixRegex();

    [GeneratedRegex(@"^:?-{3,}:?$", RegexOptions.CultureInvariant)]
    private static partial Regex TableSeparatorCellRegex();

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex ImageRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"`([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"(?:^|\s)(?:#{1,6}|[-*\u2022]|\d+[.)]|>)\s+|[*_~]", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownMarkersRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\s+([,.;:!?])", RegexOptions.CultureInvariant)]
    private static partial Regex SpaceBeforePunctuationRegex();

    [GeneratedRegex(@",(?:\s*,)+", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedCommaRegex();
}
