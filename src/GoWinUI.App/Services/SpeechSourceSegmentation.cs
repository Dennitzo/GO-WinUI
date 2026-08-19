using System.Net;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GoWinUI.App.Services;

public enum SpeechPlaybackState
{
    Buffering,
    Playing,
    Paused,
    Completed,
    Cancelled,
}

public enum SpeechDelivery
{
    Narration,
    Dialogue,
}

public enum SpeechMood
{
    Neutral,
    Warm,
    Joyful,
    Tense,
    Sad,
    Relieved,
    Angry,
    Mysterious,
    Fearful,
    Tender,
}

public enum SpeechExpression
{
    Laugh,
    Breath,
    Sigh,
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
    string SpeechText,
    SpeechDelivery Delivery = SpeechDelivery.Narration);

public sealed record PreparedSpeechSegment(
    string Id,
    string Text,
    IReadOnlyList<string> SourceUnitIds,
    SpeechDelivery Delivery = SpeechDelivery.Narration,
    SpeechMood Mood = SpeechMood.Neutral,
    double Intensity = 0,
    double Speed = 1.0,
    int PauseAfterMilliseconds = 0,
    SpeechExpression? ExpressionBefore = null,
    SpeechExpression? ExpressionAfter = null,
    string? SynthesisText = null,
    bool DirectionResolved = false,
    string? PlaybackBatchId = null);

public sealed record SpeechPlaybackProgress(
    Guid SessionId,
    Guid? SourceMessageId,
    string SourceKind,
    int SegmentIndex,
    int SegmentCount,
    IReadOnlyList<string> SourceUnitIds,
    SpeechPlaybackState State,
    IReadOnlyList<SpeechSourceUnit>? SourceUnits = null);

internal sealed record SpeechSegmentPlaybackUpdate(
    int SegmentIndex,
    SpeechPlaybackState State,
    string? Provider = null);

internal sealed record SpeechPlaybackBatchPlan(
    string Id,
    IReadOnlyList<int> SegmentIndexes);

internal static class SpeechPlaybackProgressBridge
{
    public static object ToPayload(SpeechPlaybackProgress progress) => new
    {
        progress.SessionId,
        progress.SourceMessageId,
        progress.SourceKind,
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
    internal const int MaximumSegmentCharacters = 300;

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
            var spokenParts = atomic
                ? [new SpeechTextPart(spoken, DetectDelivery(spoken))]
                : SplitForPlaybackParts(spoken);
            var visibleParts = atomic ? [visible] : SplitForPlayback(visible);
            if (spokenParts.Count == 0)
            {
                spokenParts = [new SpeechTextPart(spoken, DetectDelivery(spoken))];
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
                    spokenParts[partIndex].Text,
                    spokenParts[partIndex].Delivery));
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
        var output = new List<PreparedSpeechSegment>();
        foreach (var unit in units)
        {
            foreach (var part in SplitForPlayback(unit.SpeechText))
            {
                output.Add(new(
                    $"s{output.Count + 1:0000}",
                    part,
                    [unit.Id],
                    unit.Delivery,
                    PlaybackBatchId: unit.BlockId));
            }
        }

        if (output.Count == 0 && !string.IsNullOrWhiteSpace(fallbackText))
        {
            foreach (var part in SplitForPlayback(fallbackText))
            {
                output.Add(new(
                    $"s{output.Count + 1:0000}",
                    part,
                    [],
                    DetectDelivery(part)));
            }
        }
        return output;
    }

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
            var delivery = Enum.IsDefined(segment.Delivery)
                ? segment.Delivery
                : SpeechDelivery.Narration;
            var isDialogue = delivery == SpeechDelivery.Dialogue;
            var mood = isDialogue && Enum.IsDefined(segment.Mood)
                ? segment.Mood
                : SpeechMood.Neutral;
            var intensity = isDialogue && double.IsFinite(segment.Intensity)
                ? Math.Clamp(segment.Intensity, 0, 1)
                : 0;
            var speed = isDialogue && double.IsFinite(segment.Speed)
                ? Math.Clamp(segment.Speed, 0.82, 1.32)
                : 1.0;
            var pause = isDialogue
                ? Math.Clamp(segment.PauseAfterMilliseconds, 0, 1_500)
                : 0;
            var synthesisParts = string.IsNullOrWhiteSpace(segment.SynthesisText)
                ? []
                : parts.Count == 1
                    ? [NormalizeVisibleText(segment.SynthesisText)]
                    : SplitForPlayback(segment.SynthesisText);
            var canMapSynthesisParts = synthesisParts.Count == parts.Count;
            var playbackBatchId = string.IsNullOrWhiteSpace(segment.PlaybackBatchId)
                ? null
                : segment.PlaybackBatchId.Trim();
            for (var partIndex = 0; partIndex < parts.Count; partIndex++)
            {
                var isFirst = partIndex == 0;
                var isLast = partIndex == parts.Count - 1;
                var synthesisText = canMapSynthesisParts
                    ? synthesisParts[partIndex]
                    : null;
                output.Add(new(
                    $"s{output.Count + 1:0000}",
                    parts[partIndex],
                    ids,
                    delivery,
                    mood,
                    intensity,
                    speed,
                    isLast ? pause : 0,
                    isDialogue && isFirst && IsKnownExpression(segment.ExpressionBefore)
                        ? segment.ExpressionBefore
                        : null,
                    isDialogue && isLast && IsKnownExpression(segment.ExpressionAfter)
                        ? segment.ExpressionAfter
                        : null,
                    synthesisText,
                    isDialogue && segment.DirectionResolved && canMapSynthesisParts,
                    playbackBatchId));
            }
        }
        return output;
    }

    internal static IReadOnlyList<SpeechPlaybackBatchPlan> CreatePlaybackBatches(
        IReadOnlyList<PreparedSpeechSegment> segments)
    {
        const int maximumFallbackCharacters = 1_800;
        var output = new List<SpeechPlaybackBatchPlan>();
        var currentIndexes = new List<int>();
        string? currentId = null;
        var currentIsFallback = false;
        var currentCharacters = 0;

        void CompleteCurrent()
        {
            if (currentIndexes.Count == 0 || currentId is null)
            {
                return;
            }
            output.Add(new(currentId, currentIndexes.ToArray()));
            currentIndexes.Clear();
            currentId = null;
            currentIsFallback = false;
            currentCharacters = 0;
        }

        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            var explicitId = string.IsNullOrWhiteSpace(segment.PlaybackBatchId)
                ? null
                : segment.PlaybackBatchId.Trim();
            var startsNewBatch = currentIndexes.Count > 0
                && (explicitId is not null
                    ? currentIsFallback || !string.Equals(currentId, explicitId, StringComparison.Ordinal)
                    : !currentIsFallback || currentCharacters + segment.Text.Length > maximumFallbackCharacters);
            if (startsNewBatch)
            {
                CompleteCurrent();
            }
            if (currentIndexes.Count == 0)
            {
                currentIsFallback = explicitId is null;
                currentId = explicitId ?? $"auto-{output.Count + 1:0000}";
            }
            currentIndexes.Add(index);
            currentCharacters += segment.Text.Length;
        }
        CompleteCurrent();
        return output;
    }

    internal static string PrepareForSynthesis(PreparedSpeechSegment segment)
    {
        var text = NormalizeVisibleText(segment.SynthesisText ?? segment.Text);
        if (text.Length == 0)
        {
            return string.Empty;
        }

        var before = ExpressionTag(segment.ExpressionBefore);
        var after = ExpressionTag(segment.ExpressionAfter);
        return string.Concat(
            before.Length == 0 ? string.Empty : before + " ",
            text,
            after.Length == 0 ? string.Empty : " " + after);
    }

    internal static bool HasSameSpokenWords(string original, string candidate)
    {
        var originalWords = SpokenWordRegex().Matches(original.Normalize(NormalizationForm.FormKC))
            .Select(static match => match.Value)
            .ToArray();
        var candidateWords = SpokenWordRegex().Matches(candidate.Normalize(NormalizationForm.FormKC))
            .Select(static match => match.Value)
            .ToArray();
        return originalWords.SequenceEqual(candidateWords, StringComparer.Ordinal)
            && string.Equals(
                NonPunctuationContent(original),
                NonPunctuationContent(candidate),
                StringComparison.Ordinal);
    }

    internal static double ResolveDialogueSpeed(SpeechMood mood, double intensity)
    {
        var amount = double.IsFinite(intensity) ? Math.Clamp(intensity, 0, 1) : 0;
        var (minimum, maximum, strongerIsFaster) = mood switch
        {
            SpeechMood.Warm or SpeechMood.Tender => (0.88, 0.98, false),
            SpeechMood.Joyful => (1.12, 1.28, true),
            SpeechMood.Tense or SpeechMood.Fearful => (1.08, 1.30, true),
            SpeechMood.Sad or SpeechMood.Mysterious => (0.82, 0.94, false),
            SpeechMood.Relieved => (0.90, 1.00, false),
            SpeechMood.Angry => (1.15, 1.32, true),
            _ => (0.98, 1.05, true),
        };
        var interpolation = strongerIsFaster ? amount : 1 - amount;
        return Math.Round(minimum + ((maximum - minimum) * interpolation), 3);
    }

    internal static int ResolveDialoguePause(double intensity)
    {
        var amount = double.IsFinite(intensity) ? Math.Clamp(intensity, 0, 1) : 0;
        return (int)Math.Round(80 + (720 * amount), MidpointRounding.AwayFromZero);
    }

    private static string NonPunctuationContent(string value) => string.Concat(
        value.Normalize(NormalizationForm.FormKC).Where(static character =>
        {
            if (char.IsWhiteSpace(character)) return false;
            return CharUnicodeInfo.GetUnicodeCategory(character) is not (
                UnicodeCategory.ConnectorPunctuation
                or UnicodeCategory.DashPunctuation
                or UnicodeCategory.OpenPunctuation
                or UnicodeCategory.ClosePunctuation
                or UnicodeCategory.InitialQuotePunctuation
                or UnicodeCategory.FinalQuotePunctuation
                or UnicodeCategory.OtherPunctuation);
        }));

    internal static IReadOnlyList<string> SplitForPlayback(string? value)
        => SplitForPlaybackParts(value).Select(static part => part.Text).ToArray();

    private static List<SpeechTextPart> SplitForPlaybackParts(string? value)
    {
        var text = NormalizeVisibleText(value ?? string.Empty);
        if (text.Length == 0)
        {
            return [];
        }

        var sentences = new List<SpeechTextPart>();
        foreach (var run in SplitDialogueRuns(text))
        {
            AddSentenceParts(run.Text, run.Delivery, sentences);
        }
        return sentences;
    }

    private static void AddSentenceParts(
        string text,
        SpeechDelivery delivery,
        List<SpeechTextPart> sentences)
    {
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('.' or '!' or '?')) continue;
            var end = index + 1;
            while (end < text.Length && text[end] is '.' or '!' or '?') end++;
            if (end < text.Length && !char.IsWhiteSpace(text[end])) continue;
            AddLongPart(text[start..end], delivery, sentences);
            start = end;
            while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
            index = start - 1;
        }
        if (start < text.Length)
        {
            AddLongPart(text[start..], delivery, sentences);
        }
    }

    private static void AddLongPart(
        string value,
        SpeechDelivery delivery,
        List<SpeechTextPart> output)
    {
        var remaining = value.Trim().TrimStart(',', ';', ':');
        while (remaining.Length > MaximumSegmentCharacters)
        {
            var boundary = FindSpeechBoundary(remaining, MaximumSegmentCharacters);
            output.Add(new(remaining[..boundary].Trim(), delivery));
            remaining = remaining[boundary..].TrimStart();
        }
        if (remaining.Length > 0)
        {
            output.Add(new(remaining, delivery));
        }
    }

    private static List<SpeechTextPart> SplitDialogueRuns(string text)
    {
        var output = new List<SpeechTextPart>();
        var cursor = 0;
        while (cursor < text.Length)
        {
            var opening = FindNextDialogueOpening(text, cursor);
            if (opening < 0)
            {
                AddRun(text[cursor..], SpeechDelivery.Narration, output);
                break;
            }

            if (opening > cursor)
            {
                AddRun(text[cursor..opening], SpeechDelivery.Narration, output);
            }

            var closing = FindDialogueClosing(text, opening);
            if (closing < 0)
            {
                AddRun(text[opening..], SpeechDelivery.Narration, output);
                break;
            }

            var end = closing + 1;
            while (end < text.Length && text[end] is ',' or ';' or ':') end++;
            AddRun(text[opening..end], SpeechDelivery.Dialogue, output);
            cursor = end;
        }

        if (output.Count == 0)
        {
            output.Add(new(text, DetectDelivery(text)));
        }
        return output;
    }

    private static void AddRun(
        string value,
        SpeechDelivery delivery,
        List<SpeechTextPart> output)
    {
        var normalized = NormalizeVisibleText(value).Trim().TrimStart(',', ';', ':');
        if (normalized.Length > 0)
        {
            output.Add(new(normalized, delivery));
        }
    }

    private static int FindNextDialogueOpening(string text, int start)
    {
        for (var index = start; index < text.Length; index++)
        {
            if (text[index] is '„' or '»' or '«' or '\"' or '“')
            {
                return index;
            }
        }
        return -1;
    }

    private static int FindDialogueClosing(string text, int opening)
    {
        var expected = text[opening] switch
        {
            '„' => new[] { '“', '”' },
            '»' => new[] { '«' },
            '«' => new[] { '»' },
            '“' => new[] { '”' },
            _ => new[] { '\"' },
        };
        for (var index = opening + 1; index < text.Length; index++)
        {
            if (expected.Contains(text[index]))
            {
                return index;
            }
        }
        return -1;
    }

    private static SpeechDelivery DetectDelivery(string text)
    {
        var trimmed = text.Trim();
        return trimmed.StartsWith('„')
            || trimmed.StartsWith('»')
            || trimmed.StartsWith('«')
            || trimmed.StartsWith('\"')
            || trimmed.StartsWith('“')
            || trimmed.EndsWith('“')
            || trimmed.EndsWith('”')
            || trimmed.EndsWith('«')
            || trimmed.EndsWith('»')
            || trimmed.EndsWith('\"')
                ? SpeechDelivery.Dialogue
                : SpeechDelivery.Narration;
    }

    private static bool IsKnownExpression(SpeechExpression? expression) =>
        expression is null || Enum.IsDefined(expression.Value);

    private static string ExpressionTag(SpeechExpression? expression) => expression switch
    {
        SpeechExpression.Laugh => "<laugh>",
        SpeechExpression.Breath => "<breath>",
        SpeechExpression.Sigh => "<sigh>",
        _ => string.Empty,
    };

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

    private sealed record SpeechTextPart(string Text, SpeechDelivery Delivery);

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

    [GeneratedRegex(@"[\p{L}\p{M}\p{Nd}]+", RegexOptions.CultureInvariant)]
    private static partial Regex SpokenWordRegex();
}
