using System.Text;

namespace GoAi.Server.Core.Models;

/// <summary>
/// A bounded, fragment-independent repetition detector for the reasoning channel.
/// It does not judge a plan's semantics and never treats mere token arrival as useful work.
/// </summary>
internal sealed class ReasoningLoopGuard
{
    internal static readonly TimeSpan MaximumReasoningOnlyDuration = TimeSpan.FromMinutes(30);
    internal const int WindowWords = 256;
    internal const int MinimumWords = 768;
    internal const int RequiredRepeatedWindows = 3;
    private const int SequenceWords = 8;
    private const int RememberedSequences = 8192;
    private const ulong HashOffset = 14695981039346656037UL;
    private const ulong HashPrime = 1099511628211UL;
    private readonly TimeSpan _maximumDuration;
    private readonly ulong[] _words = new ulong[SequenceWords];
    private readonly Dictionary<ulong, int> _seen = [];
    private readonly Queue<ulong> _recent = new(RememberedSequences);
    private readonly char[] _responseCharacters = new char[SequenceWords];
    private readonly HashSet<ulong> _responseSeen = [];
    private readonly Queue<ulong> _responseRecent = new(RememberedSequences);
    private readonly StringBuilder _reasoningTail = new();
    private int _responseIndex;
    private int _responseLength;
    private int _responseNewSequences;
    private ulong _word = HashOffset;
    private bool _insideWord;
    private int _wordIndex;
    private int _windowWords;
    private int _newSequences;
    private int _repeatedWindows;
    private TimeSpan? _reasoningStarted;

    internal ReasoningLoopGuard(TimeSpan? maximumDuration = null)
    {
        _maximumDuration = maximumDuration ?? MaximumReasoningOnlyDuration;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_maximumDuration, TimeSpan.Zero);
    }

    internal int ReasoningCharacters { get; private set; }
    internal int ReasoningWords { get; private set; }
    internal int StoredSequenceCount => _recent.Count;

    internal void ObserveReasoning(string text, TimeSpan elapsed)
    {
        if (string.IsNullOrEmpty(text)) return;
        _reasoningStarted ??= elapsed;
        _reasoningTail.Append(text);
        if (_reasoningTail.Length > 16_384) _reasoningTail.Remove(0, _reasoningTail.Length - 16_384);
        ReasoningCharacters = (int)Math.Min(int.MaxValue, (long)ReasoningCharacters + text.Length);
        ThrowIfWatchdogExpired(elapsed);
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                _insideWord = true;
                _word = unchecked((_word ^ char.ToLowerInvariant(character)) * HashPrime);
                // CJK text need not separate words with spaces. Keep its repeated
                // phrases detectable without assuming German/English tokenization.
                if (character is >= '\u2e80' and <= '\u9fff') CompleteWord();
            }
            else CompleteWord();
        }
    }

    internal bool ObserveResponseProgress(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var advanced = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character)) continue;
            _responseCharacters[_responseIndex] = char.ToLowerInvariant(character);
            _responseIndex = (_responseIndex + 1) % SequenceWords;
            _responseLength = Math.Min(_responseLength + 1, SequenceWords);
            if (_responseLength < SequenceWords) continue;
            var sequence = HashOffset;
            for (var index = 0; index < SequenceWords; index++)
                sequence = unchecked((sequence ^ _responseCharacters[(_responseIndex + index) % SequenceWords]) * HashPrime);
            if (!_responseSeen.Add(sequence)) continue;
            _responseRecent.Enqueue(sequence);
            if (_responseRecent.Count > RememberedSequences) _responseSeen.Remove(_responseRecent.Dequeue());
            if (++_responseNewSequences < SequenceWords) continue;
            _responseNewSequences = 0;
            advanced = true;
        }
        if (advanced) ResetReasoning();
        return advanced;
    }

    private void ResetReasoning()
    {
        // A visible answer or a real tool payload starts a different phase. A long
        // task can use many independent model rounds without a global time limit.
        _reasoningStarted = null;
        _insideWord = false;
        _word = HashOffset;
        _wordIndex = _windowWords = _newSequences = _repeatedWindows = 0;
        ReasoningCharacters = ReasoningWords = 0;
        _seen.Clear();
        _recent.Clear();
        _reasoningTail.Clear();
        Array.Clear(_words);
    }

    internal TimeSpan? Remaining(TimeSpan elapsed) => _reasoningStarted is { } started
        ? _maximumDuration - (elapsed - started) : null;

    internal void ThrowIfWatchdogExpired(TimeSpan elapsed)
    {
        if (Remaining(elapsed) is { } remaining && remaining <= TimeSpan.Zero)
            throw WatchdogFailure();
    }

    internal ReasoningLoopDetectedException WatchdogFailure() => new("reasoning_watchdog",
        ReasoningCharacters, ReasoningWords,
        $"Die Modellrunde wurde nach {_maximumDuration.TotalMinutes:0.##} Minuten ausschließlich Denktext kontrolliert beendet. "
        + "Es entstand kein neuer Antwort- oder Werkzeugfortschritt. Der gespeicherte Arbeitsstand und die bisherigen Belege bleiben erhalten; keine automatische Wiederholung.")
        { ReasoningTail = _reasoningTail.ToString() };

    private void CompleteWord()
    {
        if (!_insideWord) return;
        _insideWord = false;
        _words[_wordIndex] = _word;
        _word = HashOffset;
        _wordIndex = (_wordIndex + 1) % SequenceWords;
        ReasoningWords++;
        _windowWords++;
        if (ReasoningWords >= SequenceWords)
        {
            var sequence = HashOffset;
            for (var index = 0; index < SequenceWords; index++)
                sequence = unchecked((sequence ^ _words[(_wordIndex + index) % SequenceWords]) * HashPrime);
            if (_seen.TryGetValue(sequence, out var count)) _seen[sequence] = count + 1;
            else { _seen[sequence] = 1; _newSequences++; }
            _recent.Enqueue(sequence);
            if (_recent.Count > RememberedSequences)
            {
                var oldest = _recent.Dequeue();
                if (_seen[oldest] == 1) _seen.Remove(oldest);
                else _seen[oldest]--;
            }
        }
        if (_windowWords < WindowWords) return;
        // At least three consecutive windows must be >87.5% familiar phrases.
        // A repeated heading, quotation, checklist or short reconsideration is insufficient.
        _repeatedWindows = ReasoningWords >= MinimumWords && _newSequences < WindowWords / 8
            ? _repeatedWindows + 1 : 0;
        _windowWords = _newSequences = 0;
        if (_repeatedWindows >= RequiredRepeatedWindows)
            throw new ReasoningLoopDetectedException("reasoning_repetition", ReasoningCharacters, ReasoningWords,
                "Die Modellrunde wurde gestoppt, weil längere Denkpassagen mehrfach nahezu unverändert wiederholt wurden. "
                + "Der gespeicherte Arbeitsstand und die bisherigen Belege bleiben erhalten; die identische Generierung wird nicht automatisch neu gestartet.")
                { ReasoningTail = _reasoningTail.ToString() };
    }
}

public sealed class ReasoningLoopDetectedException(string failureKind, int reasoningCharacters, int reasoningWords, string message)
    : InvalidOperationException(message)
{
    public string FailureKind { get; } = failureKind;
    public int ReasoningCharacters { get; } = reasoningCharacters;
    public int ReasoningWords { get; } = reasoningWords;
    public string ReasoningTail { get; init; } = string.Empty;
}
