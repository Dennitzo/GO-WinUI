using GoAi.Contracts;
using GoAi.Server.Core.Runtime;
using GoAi.Server.Core.Workers;
using Microsoft.Extensions.Hosting;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace GoAi.Server.Core.Audio;

public sealed class LiveCaptionService : BackgroundService
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);
    private const int MaximumTranscriptCharacters = 500_000;
    private const double MinimumConfidentGermanProbability = 0.80;
    private static readonly HashSet<string> EnglishLanguageMarkers = new(
        [
            "a", "about", "and", "are", "as", "at", "be", "but", "can", "do", "does",
            "for", "from", "has", "have", "how", "in", "is", "it", "not", "of", "on",
            "that", "the", "this", "to", "was", "we", "were", "what", "where", "why",
            "will", "with", "you",
        ],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> GermanLanguageMarkers = new(
        [
            "aber", "als", "auf", "aus", "bei", "das", "dass", "der", "die", "dies", "ein",
            "eine", "es", "für", "haben", "hat", "in", "ist", "kann", "mit", "nicht", "sie",
            "sind", "über", "und", "von", "wir", "wird", "zu",
        ],
        StringComparer.OrdinalIgnoreCase);
    private readonly WorkerOrchestrator _workers;
    private readonly ServerRuntimeState _runtime;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _sessionTransitionGate = new(1, 1);
    private CaptionSession? _active;

    public LiveCaptionService(WorkerOrchestrator workers, ServerRuntimeState runtime)
    {
        _workers = workers;
        _runtime = runtime;
    }

    public async Task<LiveCaptionSessionSnapshot> CreateAsync(
        LiveCaptionSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        await _sessionTransitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CaptionSession? expired = null;
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_active is { } existing)
                {
                    if (!existing.IsExpired(DateTimeOffset.UtcNow))
                    {
                        throw new InvalidOperationException("Es ist bereits eine Live-Untertitel-Sitzung aktiv.");
                    }

                    existing.IsStopping = true;
                    expired = existing;
                    _active = null;
                }
            }
            finally
            {
                _lifecycleGate.Release();
            }

            if (expired is not null)
            {
                await DrainAsync(expired).ConfigureAwait(false);
                _runtime.WriteLog("Information", "caption.session.expired", $"Live-Untertitel {expired.SessionId} wegen Inaktivität beendet.");
            }

            var now = DateTimeOffset.UtcNow;
            var sessionId = $"caption-{Guid.NewGuid():N}";
            await _workers.PrepareLiveCaptionResourcesAsync(
                sessionId,
                request.Profile,
                cancellationToken).ConfigureAwait(false);
            await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                _active = new CaptionSession(
                    sessionId,
                    request,
                    now,
                    now);
                _runtime.WriteLog("Information", "caption.session.started", $"Live-Untertitel {_active.SessionId} gestartet.");
                return _active.ToSnapshot("active");
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }
        finally
        {
            _sessionTransitionGate.Release();
        }
    }

    public async Task<LiveCaptionSessionSnapshot> GetAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = GetRequiredSession(sessionId);
            if (session.IsStopping || session.IsExpired(DateTimeOffset.UtcNow))
            {
                throw new KeyNotFoundException("Live-Untertitel-Sitzung ist abgelaufen.");
            }

            return session.ToSnapshot("active");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<LiveCaptionSessionSnapshot> KeepAliveAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = GetRequiredSession(sessionId);
            if (session.IsStopping || session.IsExpired(DateTimeOffset.UtcNow))
            {
                throw new KeyNotFoundException("Live-Untertitel-Sitzung ist abgelaufen.");
            }

            session.UpdatedAt = DateTimeOffset.UtcNow;
            return session.ToSnapshot("active");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<LiveCaptionChunkResponse> ProcessChunkAsync(
        string sessionId,
        long sequence,
        ReadOnlyMemory<byte> waveAudio,
        LiveCaptionChunkMetadata? metadata = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        CaptionSession session;
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            session = GetRequiredSession(sessionId);
            if (session.IsStopping || session.IsExpired(DateTimeOffset.UtcNow))
            {
                throw new KeyNotFoundException("Live-Untertitel-Sitzung ist nicht mehr aktiv.");
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }

        await session.ChunkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(_active, session)
                    || session.IsStopping
                    || session.IsExpired(DateTimeOffset.UtcNow))
                {
                    throw new KeyNotFoundException("Live-Untertitel-Sitzung ist nicht mehr aktiv.");
                }
                if (session.Responses.TryGetValue(sequence, out var cached))
                {
                    return cached;
                }
                if (sequence != session.NextSequence)
                {
                    throw new InvalidOperationException($"Erwartete Audiosequenz {session.NextSequence}, empfangen wurde {sequence}.");
                }
                session.UpdatedAt = DateTimeOffset.UtcNow;
            }
            finally
            {
                _lifecycleGate.Release();
            }

            var waveInfo = ValidateWave(
                waveAudio.Span,
                session.Request.SampleRate,
                session.Request.Channels,
                session.Request.WindowMilliseconds + 1_000);
            var isDictation = session.Request.Profile == LiveCaptionProfile.Dictation;
            if (isDictation)
            {
                ValidateDictationMetadata(metadata);
            }
            else if (metadata is not null)
            {
                throw new ArgumentException("Diktiermetadaten sind nur im Diktierprofil zulässig.", nameof(metadata));
            }

            var dictationState = isDictation
                ? session.GetDictationTurn(metadata!.TurnId)
                : null;
            var transcriptionAudio = waveAudio;
            var transcriptionMetadata = metadata;
            if (isDictation && metadata!.IsFinal && dictationState is not null)
            {
                var trimmed = TrimFinalDictationWave(
                    waveAudio,
                    metadata.WindowStartMilliseconds,
                    dictationState.CommittedWords);
                transcriptionAudio = trimmed.Audio;
                transcriptionMetadata = metadata with
                {
                    WindowStartMilliseconds = trimmed.WindowStartMilliseconds,
                };
                waveInfo = ValidateWave(
                    transcriptionAudio.Span,
                    session.Request.SampleRate,
                    session.Request.Channels,
                    session.Request.WindowMilliseconds + 1_000);
            }
            var transcriptionLanguage = session.Request.Language;
            if (string.IsNullOrWhiteSpace(transcriptionLanguage)
                && !string.IsNullOrWhiteSpace(dictationState?.Language))
            {
                transcriptionLanguage = dictationState.Language;
            }
            var transcriptionContext = isDictation && dictationState is not null
                ? JoinTextParts(
                    session.RawTranscript,
                    JoinDictationWords(dictationState.CommittedWords))
                : session.RawTranscript;
            var transcription = await _workers.TranscribeLiveCaptionAsync(
                transcriptionAudio,
                transcriptionLanguage,
                session.Request.Mode,
                session.Request.Profile,
                metadata?.IsFinal ?? true,
                session.SessionId,
                transcriptionContext,
                cancellationToken).ConfigureAwait(false);

            if (isDictation)
            {
                var response = BuildDictationResponse(
                    session,
                    sequence,
                    transcriptionMetadata!,
                    waveInfo.DurationMilliseconds,
                    transcription);
                await CommitResponseAsync(session, sequence, response, cancellationToken).ConfigureAwait(false);
                return response;
            }

            IReadOnlyList<TranscriptionSegment> rawSegments = transcription.Segments.Count > 0
                ? transcription.Segments
                : string.IsNullOrWhiteSpace(transcription.Text)
                    ? []
                    : [new TranscriptionSegment(0, session.Request.WindowMilliseconds / 1000d, transcription.Text, "Person 1")];
            var uniqueRawSegments = RemoveRepeatedSegments(session.RawTranscript, rawSegments);
            var uniqueRawText = string.Join(' ', uniqueRawSegments.Select(static segment => segment.Text)).Trim();
            IReadOnlyList<TranscriptionSegment> displaySegments = uniqueRawSegments;
            var provider = transcription.Provider;
            // Bereits als Deutsch erkannte Abschnitte bleiben unverändert. Dadurch
            // entstehen weder ein unnötiger General-AI-Lauf noch Verfälschungen bei
            // deutschen Sätzen mit üblichen englischen Fachbegriffen (Denglisch).
            if (displaySegments.Count > 0 && RequiresGermanTranslation(
                    transcription.Language,
                    transcription.LanguageProbability,
                    uniqueRawText))
            {
                try
                {
                    var translation = await _workers.TranslateCaptionSegmentsAsync(
                        displaySegments,
                        session.SessionId,
                        cancellationToken).ConfigureAwait(false);
                    displaySegments = translation.Segments;
                    provider += $" + {translation.ModelId} → Deutsch";
                }
                catch (Exception exception) when (exception is HttpRequestException or JsonException)
                {
                    // Ein einzelner Upstream-Fehler darf die dauerhaft laufende
                    // Untertitelsitzung nicht beenden. Unübersetzten Text zeigen wir
                    // nicht an; das überlappende Folgefenster versucht ihn erneut.
                    _runtime.WriteLog(
                        "Warning",
                        "caption.translation.skipped",
                        $"Untertitelfenster nach {exception.GetType().Name} verworfen; Sitzung bleibt aktiv.");
                    displaySegments = [];
                    uniqueRawText = string.Empty;
                    provider += " + Übersetzung wird wiederholt";
                }
            }

            var uniqueText = FormatDialogueChunk(displaySegments);
            var windowStepSeconds = (session.Request.WindowMilliseconds - session.Request.OverlapMilliseconds) / 1000d;
            var windowOffset = sequence * windowStepSeconds;
            IReadOnlyList<TranscriptionSegment> segments = displaySegments
                .Select(segment => segment with
                {
                    Start = windowOffset + segment.Start,
                    End = windowOffset + segment.End,
                })
                .ToArray();

            if (!string.IsNullOrWhiteSpace(uniqueRawText))
            {
                AppendBoundedPlainTranscript(session, uniqueRawText);
                AppendDialogueTranscript(session, displaySegments);
            }
            var captionResponse = new LiveCaptionChunkResponse(
                session.SessionId,
                sequence,
                uniqueText,
                session.Transcript,
                transcription.Language,
                transcription.LanguageProbability,
                segments,
                true,
                provider,
                DateTimeOffset.UtcNow);
            await CommitResponseAsync(session, sequence, captionResponse, cancellationToken).ConfigureAwait(false);
            return captionResponse;
        }
        finally
        {
            session.ChunkGate.Release();
        }
    }

    public async Task<LiveCaptionSessionSnapshot> StopSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        await _sessionTransitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CaptionSession session;
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                session = GetRequiredSession(sessionId);
                if (session.IsStopping)
                {
                    throw new KeyNotFoundException("Live-Untertitel-Sitzung ist nicht mehr aktiv.");
                }
                session.IsStopping = true;
            }
            finally
            {
                _lifecycleGate.Release();
            }

            await session.ChunkGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                LiveCaptionSessionSnapshot snapshot;
                await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (!ReferenceEquals(_active, session))
                    {
                        throw new KeyNotFoundException("Live-Untertitel-Sitzung ist nicht mehr aktiv.");
                    }
                    session.UpdatedAt = DateTimeOffset.UtcNow;
                    snapshot = session.ToSnapshot("completed");
                    _active = null;
                }
                finally
                {
                    _lifecycleGate.Release();
                }

                _runtime.WriteLog("Information", "caption.session.completed", $"Live-Untertitel {session.SessionId} beendet.");
                return snapshot;
            }
            finally
            {
                session.ChunkGate.Release();
            }
        }
        finally
        {
            _sessionTransitionGate.Release();
        }
    }

    private async Task CommitResponseAsync(
        CaptionSession session,
        long sequence,
        LiveCaptionChunkResponse response,
        CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_active, session) || session.IsStopping)
            {
                throw new KeyNotFoundException("Live-Untertitel-Sitzung wurde beendet.");
            }
            session.NextSequence++;
            session.UpdatedAt = DateTimeOffset.UtcNow;
            session.Responses[sequence] = response;
            foreach (var oldSequence in session.Responses.Keys.Where(value => value < sequence - 15).ToArray())
            {
                _ = session.Responses.Remove(oldSequence);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private static void ValidateDictationMetadata(LiveCaptionChunkMetadata? metadata)
    {
        if (metadata is null
            || metadata.TurnId.Length is < 1 or > 128
            || metadata.TurnId.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
            || metadata.Revision < 0
            || metadata.WindowStartMilliseconds < 0)
        {
            throw new ArgumentException("Das Diktierfenster enthält keine gültigen Metadaten.", nameof(metadata));
        }
    }

    internal static (ReadOnlyMemory<byte> Audio, int WindowStartMilliseconds) TrimFinalDictationWave(
        ReadOnlyMemory<byte> waveAudio,
        int windowStartMilliseconds,
        IReadOnlyList<DictationWord> committedWords)
    {
        if (committedWords.Count == 0
            || waveAudio.Length < 44
            || !waveAudio.Span.Slice(36, 4).SequenceEqual("data"u8))
        {
            return (waveAudio, windowStartMilliseconds);
        }

        const int contextLeadMilliseconds = 250;
        const int bytesPerMillisecond = GoAiProtocol.LiveCaptionSampleRate * sizeof(short) / 1_000;
        const int minimumRemainingMilliseconds = 200;
        var relativeStartMilliseconds = committedWords[^1].End * 1_000
            - windowStartMilliseconds
            - contextLeadMilliseconds;
        var bytesToSkip = (int)Math.Floor(Math.Max(0, relativeStartMilliseconds) * bytesPerMillisecond);
        bytesToSkip -= bytesToSkip & 1;
        var declaredDataLength = BinaryPrimitives.ReadInt32LittleEndian(waveAudio.Span.Slice(40, 4));
        var minimumRemainingBytes = minimumRemainingMilliseconds * bytesPerMillisecond;
        if (declaredDataLength <= 0
            || 44L + declaredDataLength > waveAudio.Length
            || bytesToSkip <= 0
            || declaredDataLength - bytesToSkip < minimumRemainingBytes)
        {
            return (waveAudio, windowStartMilliseconds);
        }

        var remainingBytes = declaredDataLength - bytesToSkip;
        var trimmed = new byte[44 + remainingBytes];
        waveAudio.Span[..44].CopyTo(trimmed);
        waveAudio.Span.Slice(44 + bytesToSkip, remainingBytes).CopyTo(trimmed.AsSpan(44));
        BinaryPrimitives.WriteInt32LittleEndian(trimmed.AsSpan(4, 4), trimmed.Length - 8);
        BinaryPrimitives.WriteInt32LittleEndian(trimmed.AsSpan(40, 4), remainingBytes);
        return (
            trimmed,
            checked(windowStartMilliseconds + bytesToSkip / bytesPerMillisecond));
    }

    private static LiveCaptionChunkResponse BuildDictationResponse(
        CaptionSession session,
        long sequence,
        LiveCaptionChunkMetadata metadata,
        double durationMilliseconds,
        TranscriptionResponse transcription)
    {
        var state = session.GetDictationTurn(metadata.TurnId);
        if (metadata.Revision <= state.LastRevision)
        {
            throw new InvalidOperationException(
                $"Die Diktierrevision muss größer als {state.LastRevision} sein.");
        }

        state.LastRevision = metadata.Revision;
        state.ObservationCount++;
        if (string.IsNullOrWhiteSpace(session.Request.Language)
            && string.IsNullOrWhiteSpace(state.Language)
            && transcription.LanguageProbability >= MinimumConfidentGermanProbability
            && !string.IsNullOrWhiteSpace(transcription.Language))
        {
            state.Language = transcription.Language.Trim();
        }

        var windowStartSeconds = metadata.WindowStartMilliseconds / 1000d;
        var audioEndSeconds = windowStartSeconds + durationMilliseconds / 1000d;
        var current = ExtractDictationWords(transcription, windowStartSeconds, audioEndSeconds);
        var previous = state.PreviousWords;

        // Once the six-second rolling window advances, words that would otherwise
        // leave the window are retained from the preceding, already observed
        // hypothesis. Normal operation confirms them through Local Agreement first.
        if (state.ObservationCount > 2 && previous.Count > 0)
        {
            AppendDistinctWords(
                state.CommittedWords,
                previous.Where(word => word.End <= windowStartSeconds + 0.12));
        }

        if (previous.Count > 0 && current.Count > 0)
        {
            var committedEndBeforeAgreement = state.CommittedWords.Count == 0
                ? double.NegativeInfinity
                : state.CommittedWords[^1].End;
            var previousUncommitted = previous
                .Where(word => word.End > committedEndBeforeAgreement + 0.04)
                .ToArray();
            var currentUncommitted = current
                .Where(word => word.End > committedEndBeforeAgreement + 0.04)
                .ToArray();
            var agreementLength = FindPrefixAgreementLength(
                previousUncommitted,
                currentUncommitted);
            var safeAudioEnd = audioEndSeconds - (metadata.IsFinal ? 0 : 0.20);
            if (agreementLength > 0)
            {
                AppendDistinctWords(
                    state.CommittedWords,
                    currentUncommitted
                        .Take(agreementLength)
                        .Where(word => word.End <= safeAudioEnd));
            }
        }

        if (metadata.IsFinal)
        {
            AppendDistinctWords(
                state.CommittedWords,
                current.Count > 0 ? current : previous);
        }

        var lastCommittedEnd = state.CommittedWords.Count == 0
            ? double.NegativeInfinity
            : state.CommittedWords[^1].End;
        var provisionalWords = metadata.IsFinal
            ? []
            : current
                .Where(word => word.End > lastCommittedEnd + 0.04)
                .ToList();
        var stableText = JoinDictationWords(state.CommittedWords);
        var provisionalText = JoinDictationWords(provisionalWords);
        var turnText = JoinTextParts(stableText, provisionalText);

        state.PreviousWords = current;
        state.LastWindowStartSeconds = windowStartSeconds;
        if (metadata.IsFinal)
        {
            state.IsFinal = true;
            if (!string.IsNullOrWhiteSpace(turnText))
            {
                AppendBoundedPlainTranscript(session, turnText);
                session.Transcript = JoinTextParts(session.Transcript, turnText);
                if (session.Transcript.Length > MaximumTranscriptCharacters)
                {
                    session.Transcript = session.Transcript[^MaximumTranscriptCharacters..];
                }
            }
        }

        IReadOnlyList<TranscriptionSegment> absoluteSegments = transcription.Segments
            .Select(segment => segment with
            {
                Start = windowStartSeconds + segment.Start,
                End = windowStartSeconds + segment.End,
                Speaker = null,
                Words = segment.Words?.Select(word => word with
                {
                    Start = windowStartSeconds + word.Start,
                    End = windowStartSeconds + word.End,
                }).ToArray(),
            })
            .ToArray();
        return new LiveCaptionChunkResponse(
            session.SessionId,
            sequence,
            turnText,
            JoinTextParts(session.Transcript, metadata.IsFinal ? null : turnText),
            transcription.Language,
            transcription.LanguageProbability,
            absoluteSegments,
            metadata.IsFinal,
            transcription.Provider,
            DateTimeOffset.UtcNow,
            metadata.TurnId,
            metadata.Revision,
            stableText,
            provisionalText);
    }

    private static List<DictationWord> ExtractDictationWords(
        TranscriptionResponse transcription,
        double windowStartSeconds,
        double audioEndSeconds)
    {
        var words = transcription.Segments
            .SelectMany(static segment => segment.Words ?? [])
            .Where(static word => !string.IsNullOrWhiteSpace(word.Text))
            .Select(word => new DictationWord(
                windowStartSeconds + Math.Max(0, word.Start),
                windowStartSeconds + Math.Max(word.Start, word.End),
                word.Text))
            .OrderBy(static word => word.Start)
            .ToList();
        if (words.Count > 0)
        {
            return words;
        }

        var tokens = (transcription.Text ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return [];
        }
        var duration = Math.Max(0.20, audioEndSeconds - windowStartSeconds);
        return tokens.Select((token, index) => new DictationWord(
            windowStartSeconds + duration * index / tokens.Length,
            windowStartSeconds + duration * (index + 1) / tokens.Length,
            index == 0 ? token : " " + token)).ToList();
    }

    internal static int FindPrefixAgreementLength(
        IReadOnlyList<DictationWord> previous,
        IReadOnlyList<DictationWord> current)
    {
        var length = 0;
        while (length < previous.Count && length < current.Count)
        {
            var previousWord = previous[length];
            var currentWord = current[length];
            if (Math.Abs(previousWord.Start - currentWord.Start) > 0.75
                || !string.Equals(
                    NormalizeWord(previousWord.Text),
                    NormalizeWord(currentWord.Text),
                    StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            length++;
        }
        return length;
    }

    private static void AppendDistinctWords(
        List<DictationWord> committed,
        IEnumerable<DictationWord> candidates)
    {
        foreach (var word in candidates.OrderBy(static word => word.Start))
        {
            if (string.IsNullOrWhiteSpace(word.Text))
            {
                continue;
            }
            if (committed.Count > 0)
            {
                var last = committed[^1];
                if (word.End <= last.End + 0.04
                    || (Math.Abs(word.Start - last.Start) <= 0.45
                        && string.Equals(
                            NormalizeWord(word.Text),
                            NormalizeWord(last.Text),
                            StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
            }
            committed.Add(word);
        }
    }

    private static string JoinDictationWords(IEnumerable<DictationWord> words)
    {
        var result = string.Concat(words.Select(static word => word.Text)).Trim();
        return string.Join(' ', result.Split(
            [' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string JoinTextParts(string? first, string? second)
    {
        var left = (first ?? string.Empty).Trim();
        var right = (second ?? string.Empty).Trim();
        if (left.Length == 0)
        {
            return right;
        }
        if (right.Length == 0)
        {
            return left;
        }
        return $"{left} {right}";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
                await ExpireInactiveSessionAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await _sessionTransitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CaptionSession? session;
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                session = _active;
                if (session is not null)
                {
                    session.IsStopping = true;
                    _active = null;
                }
            }
            finally
            {
                _lifecycleGate.Release();
            }
            if (session is not null)
            {
                await DrainAsync(session).ConfigureAwait(false);
            }
        }
        finally
        {
            _sessionTransitionGate.Release();
        }
    }

    private async Task ExpireInactiveSessionAsync(CancellationToken cancellationToken)
    {
        await _sessionTransitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CaptionSession? session = null;
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_active is { } active && active.IsExpired(DateTimeOffset.UtcNow))
                {
                    active.IsStopping = true;
                    session = active;
                    _active = null;
                }
            }
            finally
            {
                _lifecycleGate.Release();
            }
            if (session is null)
            {
                return;
            }

            await DrainAsync(session).ConfigureAwait(false);
            _runtime.WriteLog("Information", "caption.session.expired", $"Live-Untertitel {session.SessionId} wegen Inaktivität beendet.");
        }
        finally
        {
            _sessionTransitionGate.Release();
        }
    }

    private static async Task DrainAsync(CaptionSession session)
    {
        await session.ChunkGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        session.ChunkGate.Release();
    }

    private CaptionSession GetRequiredSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)
            || _active is not { } session
            || !string.Equals(session.SessionId, sessionId, StringComparison.Ordinal))
        {
            throw new KeyNotFoundException("Live-Untertitel-Sitzung wurde nicht gefunden.");
        }
        return session;
    }

    internal static bool RequiresGermanTranslation(
        string? language,
        double languageProbability,
        string? transcript)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return true;
        }

        var normalized = language.Trim().Replace('_', '-').ToLowerInvariant();
        var reportsGerman = normalized is "de" or "deutsch" or "german"
            || normalized.StartsWith("de-", StringComparison.Ordinal);
        if (!reportsGerman
            || !double.IsFinite(languageProbability)
            || languageProbability < MinimumConfidentGermanProbability)
        {
            return true;
        }

        // Short Whisper windows can inherit a stale German language decision
        // from the preceding overlap. This conservative lexical guard catches
        // coherent English passages while leaving German with a few English
        // technical terms untouched.
        var words = (transcript ?? string.Empty)
            .Split(
                [' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeWord)
            .Where(static word => word.Length > 0)
            .ToArray();
        var englishMarkers = words.Count(EnglishLanguageMarkers.Contains);
        var germanMarkers = words.Count(GermanLanguageMarkers.Contains);
        return englishMarkers >= 4 && englishMarkers >= Math.Max(4, germanMarkers * 2);
    }

    private static void ValidateRequest(LiveCaptionSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Mode) || !Enum.IsDefined(request.Profile))
        {
            throw new ArgumentException("Live-Untertitel-Modus oder -Profil ist ungültig.", nameof(request));
        }
        if (request.Language?.Length is > 16
            || request.Language is { Length: > 0 } language
                && language.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
        {
            throw new ArgumentException("Live-Untertitel-Sprache ist ungültig.", nameof(request));
        }
        if (request.SampleRate != GoAiProtocol.LiveCaptionSampleRate
            || request.Channels != 1
            || request.WindowMilliseconds is < 1_000 or > 10_000
            || request.OverlapMilliseconds < 0
            || request.OverlapMilliseconds > Math.Min(2_000, request.WindowMilliseconds / 2))
        {
            throw new ArgumentException("Live-Untertitel benötigen 16-kHz-Mono-PCM und gültige Fenster-/Überlappungswerte.", nameof(request));
        }
        if (request.Profile == LiveCaptionProfile.Dictation
            && (request.Mode != LiveCaptionMode.Transcribe
                || request.WindowMilliseconds != 6_000
                || request.OverlapMilliseconds != 0))
        {
            throw new ArgumentException(
                "Das Diktierprofil benötigt Transkription mit einem rollierenden Sechs-Sekunden-Fenster ohne serverseitige Überlappung.",
                nameof(request));
        }
    }

    internal static PcmWaveInfo ValidateWave(
        ReadOnlySpan<byte> data,
        int expectedSampleRate,
        int expectedChannels,
        int maximumDurationMilliseconds)
    {
        if (data.Length is < 44 or > GoAiProtocol.MaximumLiveCaptionChunkBytes
            || !data[..4].SequenceEqual("RIFF"u8)
            || !data.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("Live-Untertitel-Chunk ist keine gültige WAV-Datei.");
        }

        var offset = 12;
        int? sampleRate = null;
        int? channels = null;
        int? bitsPerSample = null;
        int? audioFormat = null;
        int? dataLength = null;
        while (offset + 8 <= data.Length)
        {
            var chunkLengthUnsigned = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4, 4));
            if (chunkLengthUnsigned > int.MaxValue)
            {
                throw new InvalidDataException("WAV-Chunkgröße ist ungültig.");
            }
            var chunkLength = (int)chunkLengthUnsigned;
            var contentOffset = offset + 8;
            if (contentOffset + (long)chunkLength > data.Length)
            {
                throw new InvalidDataException("WAV-Chunk ist abgeschnitten.");
            }

            var identifier = data.Slice(offset, 4);
            if (identifier.SequenceEqual("fmt "u8))
            {
                if (chunkLength < 16)
                {
                    throw new InvalidDataException("WAV-Formatblock ist unvollständig.");
                }
                audioFormat = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(contentOffset, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(contentOffset + 2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(contentOffset + 4, 4));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(contentOffset + 14, 2));
            }
            else if (identifier.SequenceEqual("data"u8))
            {
                dataLength = chunkLength;
            }

            offset = checked(contentOffset + chunkLength + (chunkLength & 1));
        }

        if (audioFormat != 1
            || sampleRate != expectedSampleRate
            || channels != expectedChannels
            || bitsPerSample != 16
            || dataLength is null or <= 0)
        {
            throw new InvalidDataException("Live-Untertitel erwarten PCM16-WAV mit 16 kHz und einem Monokanal.");
        }

        var bytesPerSecond = checked(sampleRate.Value * channels.Value * (bitsPerSample.Value / 8));
        var durationMilliseconds = 1000d * dataLength.Value / bytesPerSecond;
        if (durationMilliseconds is < 200 || durationMilliseconds > maximumDurationMilliseconds)
        {
            throw new InvalidDataException("Live-Untertitel-Audiofenster liegt außerhalb der erlaubten Dauer.");
        }
        return new PcmWaveInfo(sampleRate.Value, channels.Value, bitsPerSample.Value, dataLength.Value, durationMilliseconds);
    }

    internal static string RemoveRepeatedPrefix(string previous, string current)
    {
        var candidate = current.Trim();
        if (candidate.Length == 0 || string.IsNullOrWhiteSpace(previous))
        {
            return candidate;
        }

        var priorWords = previous.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var currentWords = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var maximum = Math.Min(16, Math.Min(priorWords.Length, currentWords.Length));
        for (var overlap = maximum; overlap > 0; overlap--)
        {
            var equal = true;
            for (var index = 0; index < overlap; index++)
            {
                if (!string.Equals(
                    NormalizeWord(priorWords[priorWords.Length - overlap + index]),
                    NormalizeWord(currentWords[index]),
                    StringComparison.OrdinalIgnoreCase))
                {
                    equal = false;
                    break;
                }
            }
            if (equal)
            {
                return string.Join(' ', currentWords.Skip(overlap));
            }
        }
        return candidate;
    }

    internal static IReadOnlyList<TranscriptionSegment> RemoveRepeatedSegments(
        string previous,
        IReadOnlyList<TranscriptionSegment> segments)
    {
        if (segments.Count == 0)
        {
            return [];
        }
        var current = string.Join(' ', segments.Select(static segment => segment.Text)).Trim();
        var unique = RemoveRepeatedPrefix(previous, current);
        if (unique.Length == 0)
        {
            return [];
        }
        var currentWordCount = current.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var uniqueWordCount = unique.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var wordsToSkip = Math.Max(0, currentWordCount - uniqueWordCount);
        if (wordsToSkip == 0)
        {
            return segments;
        }

        var result = new List<TranscriptionSegment>(segments.Count);
        foreach (var segment in segments)
        {
            var words = segment.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (wordsToSkip >= words.Length)
            {
                wordsToSkip -= words.Length;
                continue;
            }
            var text = string.Join(' ', words.Skip(wordsToSkip));
            wordsToSkip = 0;
            if (text.Length > 0)
            {
                result.Add(segment with { Text = text });
            }
        }
        return result;
    }

    internal static string FormatDialogueChunk(IReadOnlyList<TranscriptionSegment> segments)
    {
        var result = new StringBuilder();
        string? previousSpeaker = null;
        foreach (var segment in segments.Where(static item => !string.IsNullOrWhiteSpace(item.Text)))
        {
            var speaker = string.IsNullOrWhiteSpace(segment.Speaker) ? "Person 1" : segment.Speaker.Trim();
            if (!string.Equals(previousSpeaker, speaker, StringComparison.Ordinal))
            {
                if (result.Length > 0)
                {
                    result.AppendLine();
                }
                result.Append(speaker).Append(": ");
                previousSpeaker = speaker;
            }
            else if (result.Length > 0 && result[^1] != ' ')
            {
                result.Append(' ');
            }
            result.Append(segment.Text.Trim());
        }
        return result.ToString();
    }

    private static void AppendBoundedPlainTranscript(CaptionSession session, string text)
    {
        session.RawTranscript += session.RawTranscript.Length == 0 ? text : " " + text;
        if (session.RawTranscript.Length > MaximumTranscriptCharacters)
        {
            session.RawTranscript = session.RawTranscript[^MaximumTranscriptCharacters..];
        }
    }

    private static void AppendDialogueTranscript(
        CaptionSession session,
        IReadOnlyList<TranscriptionSegment> segments)
    {
        foreach (var segment in segments.Where(static item => !string.IsNullOrWhiteSpace(item.Text)))
        {
            var speaker = string.IsNullOrWhiteSpace(segment.Speaker) ? "Person 1" : segment.Speaker.Trim();
            if (session.Transcript.Length == 0)
            {
                session.Transcript = $"{speaker}: {segment.Text.Trim()}";
            }
            else if (string.Equals(session.LastSpeaker, speaker, StringComparison.Ordinal))
            {
                session.Transcript += " " + segment.Text.Trim();
            }
            else
            {
                session.Transcript += $"{Environment.NewLine}{speaker}: {segment.Text.Trim()}";
            }
            session.LastSpeaker = speaker;
        }
        if (session.Transcript.Length > MaximumTranscriptCharacters)
        {
            var start = session.Transcript.Length - MaximumTranscriptCharacters;
            var nextLine = session.Transcript.IndexOf('\n', start);
            session.Transcript = nextLine >= 0
                ? session.Transcript[(nextLine + 1)..]
                : session.Transcript[^MaximumTranscriptCharacters..];
        }
    }

    private static string NormalizeWord(string value) => value.Trim(
        ' ', '.', ',', ':', ';', '!', '?', '-', '–', '—', '(', ')', '[', ']', '{', '}', '"', '\'');

    private sealed class CaptionSession
    {
        public CaptionSession(
            string sessionId,
            LiveCaptionSessionRequest request,
            DateTimeOffset createdAt,
            DateTimeOffset updatedAt)
        {
            SessionId = sessionId;
            Request = request;
            CreatedAt = createdAt;
            UpdatedAt = updatedAt;
        }

        public string SessionId { get; }

        public LiveCaptionSessionRequest Request { get; }

        public DateTimeOffset CreatedAt { get; }

        public DateTimeOffset UpdatedAt { get; set; }

        public long NextSequence { get; set; }

        public bool IsStopping { get; set; }

        public string Transcript { get; set; } = string.Empty;

        public string RawTranscript { get; set; } = string.Empty;

        public string? LastSpeaker { get; set; }

        public Dictionary<long, LiveCaptionChunkResponse> Responses { get; } = [];

        public Dictionary<string, DictationTurnState> DictationTurns { get; } = new(StringComparer.Ordinal);

        public SemaphoreSlim ChunkGate { get; } = new(1, 1);

        public bool IsExpired(DateTimeOffset now) => now - UpdatedAt >= IdleTimeout;

        public DictationTurnState GetDictationTurn(string turnId)
        {
            if (!DictationTurns.TryGetValue(turnId, out var state))
            {
                state = new DictationTurnState();
                DictationTurns[turnId] = state;
            }
            if (state.IsFinal)
            {
                throw new InvalidOperationException("Der Diktierabschnitt wurde bereits abgeschlossen.");
            }
            return state;
        }

        public LiveCaptionSessionSnapshot ToSnapshot(string state)
        {
            var idleExpiry = UpdatedAt.Add(IdleTimeout);
            return new LiveCaptionSessionSnapshot(
                SessionId,
                state,
                Request.Mode,
                Request.Language,
                Request.SampleRate,
                Request.Channels,
                Request.WindowMilliseconds,
                Request.OverlapMilliseconds,
                NextSequence,
                Transcript,
                CreatedAt,
                UpdatedAt,
                idleExpiry,
                Request.Profile);
        }
    }

    private sealed class DictationTurnState
    {
        public long LastRevision { get; set; } = -1;

        public int ObservationCount { get; set; }

        public string? Language { get; set; }

        public double LastWindowStartSeconds { get; set; }

        public bool IsFinal { get; set; }

        public List<DictationWord> CommittedWords { get; } = [];

        public List<DictationWord> PreviousWords { get; set; } = [];
    }

    internal readonly record struct DictationWord(double Start, double End, string Text);

}

internal readonly record struct PcmWaveInfo(
    int SampleRate,
    int Channels,
    int BitsPerSample,
    int DataLength,
    double DurationMilliseconds);
