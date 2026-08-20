using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Workers;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace GoAi.Server.Core.Audio;

public sealed class UtteranceIntentService
{
    private static readonly HashSet<string> CancelWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "abbrechen", "abbruch", "stopp", "stop",
    };
    private static readonly HashSet<string> Fillers = new(StringComparer.OrdinalIgnoreCase)
    {
        "äh", "ähm", "hm", "hmm", "mhm", "uh", "um",
    };
    private static readonly string[] QuestionPrefixes =
    [
        "wer ", "was ", "wie ", "wo ", "wann ", "warum ", "wieso ", "weshalb ",
        "welche ", "welcher ", "welches ", "kann ", "kannst ", "können ",
        "könnte ", "könntest ", "ist ", "sind ", "hat ", "haben ", "darf ",
        "soll ", "muss ",
    ];

    private readonly GoAiServerOptions _options;
    private readonly GpuLeaseScheduler _scheduler;
    private readonly WorkerOrchestrator _workers;
    private readonly LmStudioClient _lmStudio;

    public UtteranceIntentService(
        IOptions<GoAiServerOptions> options,
        GpuLeaseScheduler scheduler,
        WorkerOrchestrator workers,
        LmStudioClient lmStudio)
    {
        _options = options.Value;
        _scheduler = scheduler;
        _workers = workers;
        _lmStudio = lmStudio;
    }

    public async Task<UtteranceIntentResponse> ClassifyAsync(
        UtteranceIntentRequest request,
        CancellationToken cancellationToken)
    {
        var text = request.Text?.Trim() ?? string.Empty;
        if (text.Length > 10_000 || request.Language?.Length > 16)
        {
            throw new ArgumentException("Utterance text or language is outside the protocol limits.");
        }
        if (text.Length == 0 || Fillers.Contains(text.Trim(' ', '.', ',', '!', '?')))
        {
            return new(UtteranceIntent.Noise);
        }
        if (CancelWords.Contains(text.Trim(' ', '.', ',', '!', '?')))
        {
            return new(UtteranceIntent.Cancel);
        }
        if (_scheduler.ActiveActivities.Any(static activity =>
                activity.Mode == GpuLeaseMode.Exclusive
                && string.Equals(activity.Workload, "llm-code", StringComparison.Ordinal)))
        {
            // Qwen owns LM Studio while a coding run is active. Voice input must
            // remain responsive instead of queuing an otherwise hidden General-AI
            // intent request behind that potentially long run.
            return ClassifyLocallyDuringCoding(text);
        }

        await using var lease = await _scheduler.AcquireAsync(
            "voice-intent", null, GpuLeaseMode.Shared, cancellationToken).ConfigureAwait(false);
        _ = await _workers.PrepareLmModelAsync(
            _options.GeneralModelId, _options.GeneralContextLength, cancellationToken).ConfigureAwait(false);
        var result = await _lmStudio.CompleteChatAsync(
            _options.GeneralModelId,
            [
                new("system", """
                    Klassifiziere eine finale deutsche Spracheingabe. Antworte ausschliesslich als kompaktes JSON:
                    {"intent":"question|instruction|cancel|noise","normalizedText":"..."}
                    question = echte Frage; instruction = konkrete ausfuehrbare Anweisung; cancel = Abbruch/Stopp;
                    noise = Fuelllaut, Hintergrundsprache, unvollstaendiges Fragment oder keine erkennbare Absicht.
                    Erfinde keinen Inhalt. normalizedText darf nur Rechtschreibung und Zeichensetzung vorsichtig normalisieren.
                    """),
                new("user", text),
            ],
            [],
            maximumOutputTokens: 128,
            cancellationToken).ConfigureAwait(false);

        return Parse(result.Content, text);
    }

    internal static UtteranceIntentResponse ClassifyLocallyDuringCoding(string text)
    {
        var normalized = text.Trim();
        if (normalized.Length < 3)
        {
            return new(UtteranceIntent.Noise);
        }

        var comparison = normalized.ToLowerInvariant();
        var intent = normalized.EndsWith('?')
            || QuestionPrefixes.Any(prefix => comparison.StartsWith(prefix, StringComparison.Ordinal))
                ? UtteranceIntent.Question
                : UtteranceIntent.Instruction;
        return new(intent, normalized);
    }

    private static UtteranceIntentResponse Parse(string? content, string original)
    {
        var json = content?.Trim() ?? string.Empty;
        var start = json.IndexOf('{');
        var end = json.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new InvalidOperationException("The voice intent model returned no valid JSON object.");
        }
        using var document = JsonDocument.Parse(json[start..(end + 1)]);
        var intentText = document.RootElement.GetProperty("intent").GetString();
        var intent = intentText?.ToLowerInvariant() switch
        {
            "question" => UtteranceIntent.Question,
            "instruction" => UtteranceIntent.Instruction,
            "cancel" => UtteranceIntent.Cancel,
            "noise" => UtteranceIntent.Noise,
            _ => throw new InvalidOperationException("The voice intent model returned an unknown intent."),
        };
        var normalized = document.RootElement.TryGetProperty("normalizedText", out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
        return new(intent, intent is UtteranceIntent.Question or UtteranceIntent.Instruction
            ? (string.IsNullOrWhiteSpace(normalized) ? original : normalized)
            : null);
    }
}
