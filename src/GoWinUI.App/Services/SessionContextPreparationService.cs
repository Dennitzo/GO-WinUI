using GoAi.Client;
using GoAi.Contracts;
using GoWinUI.Core.Chat;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GoWinUI.App.Services;

public sealed record SessionContextProgress(int Percent, string Status, string Detail);

public sealed record SessionRunContext(
    IReadOnlyList<RunMessage> Messages,
    SessionContextDescriptor Descriptor,
    int ContextLength,
    bool CacheHit);

public sealed class SessionContextPreparationService(IChatRepository chats)
{
    private const int SchemaVersion = 3;
    private const int MaximumContentPartCharacters = 220_000;
    private const int MaximumPreparationInputCharacters = 600_000;
    private static readonly JsonSerializerOptions JsonOptions = GoAiProtocol.CreateJsonOptions();

    public async Task<SessionRunContext> PrepareAsync(
        GoAiClient client,
        Guid sessionId,
        IReadOnlyList<ChatMessage> history,
        string currentPrompt,
        string preferredGeneralModelId,
        bool coding,
        SessionContextProfile profile,
        int? knownContextLength,
        int? knownHistoryBudgetCharacters,
        Func<SessionContextProgress, Task> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentPrompt);
        ArgumentNullException.ThrowIfNull(progress);

        profile = coding ? SessionContextProfile.Code : profile;
        var eligible = SelectEligibleHistory(history, profile);
        var historyRevision = CreateHistoryRevision(eligible);
        var (modelId, contextLength) = await ResolveModelAsync(
            client,
            preferredGeneralModelId,
            coding,
            knownContextLength,
            cancellationToken).ConfigureAwait(false);
        var historyBudget = knownHistoryBudgetCharacters
            ?? CalculateHistoryBudget(contextLength, currentPrompt);
        historyBudget = Math.Clamp(historyBudget, 1_024, 1_200_000);

        if (eligible.Length == 0)
        {
            return new(
                [],
                new SessionContextDescriptor(historyRevision, 0, 0, 0),
                contextLength,
                CacheHit: false);
        }

        var exactMessages = BuildExactMessages(eligible);
        var exactCharacters = CountCharacters(exactMessages);
        if (exactCharacters <= historyBudget)
        {
            return new(
                exactMessages,
                new SessionContextDescriptor(
                    historyRevision,
                    eligible.Length,
                    eligible.Length,
                    EstimateTokens(exactMessages)),
                contextLength,
                CacheHit: false);
        }

        var summaryBudget = CalculateSummaryBudget(historyBudget);
        var fullCacheKey = CreateCacheKey(
            sessionId,
            historyRevision,
            modelId,
            historyBudget,
            summaryBudget,
            profile);
        var fullCached = await chats.GetSessionContextPreparationAsync(
            fullCacheKey,
            cancellationToken).ConfigureAwait(false);
        if (fullCached is not null && fullCached.MessageCount == eligible.Length)
        {
            var cachedMessages = new List<RunMessage>
            {
                new("user", SplitContentParts(BuildPreparedHistoryEnvelope(
                    fullCached.PreparedText,
                    eligible.Length,
                    eligible[^1],
                    profile))),
            };
            if (CountCharacters(cachedMessages) <= historyBudget)
            {
                await progress(new(
                    100,
                    "Sitzungsverlauf aufbereitet",
                    $"Gespeicherte vollständige Sitzungskomprimierung für {eligible.Length:N0} Nachrichten geladen.")).ConfigureAwait(false);
                return new(
                    cachedMessages,
                    new SessionContextDescriptor(
                        historyRevision,
                        eligible.Length,
                        eligible.Length,
                        EstimateTokens(cachedMessages),
                        PreparedByAi: true),
                    contextLength,
                    CacheHit: true);
            }
        }

        var recentBudget = Math.Max(0, historyBudget - summaryBudget - 512);
        var recent = SelectRecentMessages(eligible, recentBudget);
        var olderCount = eligible.Length - recent.Length;
        if (olderCount <= 0)
        {
            // A single oversized recent message is summarized instead of being cut.
            recent = [];
            olderCount = eligible.Length;
        }
        var older = eligible[..olderCount];
        var olderRevision = CreateHistoryRevision(older);
        var cacheKey = CreateCacheKey(
            sessionId,
            olderRevision,
            modelId,
            historyBudget,
            summaryBudget,
            profile);
        var cached = await chats.GetSessionContextPreparationAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        var reusable = cached is null
            ? await FindReusablePreparationAsync(
                sessionId,
                modelId,
                older,
                profile,
                cancellationToken).ConfigureAwait(false)
            : null;
        var reusedPersistentHistory = cached is not null || reusable is not null;
        string preparedText;
        if (cached is not null)
        {
            preparedText = cached.PreparedText;
            await progress(new(
                100,
                "Sitzungsverlauf aufbereitet",
                $"Gespeicherte Verdichtung für {cached.MessageCount:N0} Nachrichten geladen.")).ConfigureAwait(false);
        }
        else
        {
            try
            {
                if (reusable is not null)
                {
                    var additional = older[reusable.MessageCount..];
                    await progress(new(
                        1,
                        "Sitzungsverlauf wird fortgeschrieben",
                        additional.Length == 0
                            ? $"Gespeicherte Verdichtung für {reusable.MessageCount:N0} Nachrichten wird an das aktuelle Kontextbudget angepasst."
                            : $"Gespeicherte Verdichtung für {reusable.MessageCount:N0} Nachrichten geladen; nur {additional.Length:N0} neu herausgefallene Nachrichten werden ergänzt.")).ConfigureAwait(false);
                    preparedText = additional.Length == 0 && reusable.PreparedText.Length <= summaryBudget
                        ? reusable.PreparedText
                        : await ExtendPreparedHistoryAsync(
                            client,
                            sessionId,
                            reusable.PreparedText,
                            additional,
                            modelId,
                            coding,
                            profile,
                            contextLength,
                            summaryBudget,
                            progress,
                            cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await progress(new(
                        1,
                        "Sitzungsverlauf wird aufbereitet",
                        $"{older.Length:N0} ältere Nachrichten überschreiten das Modellfenster von {contextLength:N0} Token.")).ConfigureAwait(false);
                    preparedText = await PrepareHistoryAsync(
                        client,
                        sessionId,
                        older,
                        modelId,
                        coding,
                        profile,
                        contextLength,
                        summaryBudget,
                        progress,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                throw new InvalidOperationException(
                    $"Der Sitzungsverlauf konnte nicht vollständig und sicher aufbereitet werden. Ursache: {exception.Message}",
                    exception);
            }
            await chats.SaveSessionContextPreparationAsync(
                new SessionContextPreparation(
                    cacheKey,
                    sessionId,
                    olderRevision,
                    modelId,
                    historyBudget,
                    older[^1].Id,
                    older.Length,
                    preparedText,
                    DateTimeOffset.UtcNow,
                    profile),
                cancellationToken).ConfigureAwait(false);
            await progress(new(
                100,
                "Sitzungsverlauf aufbereitet",
                reusable is null
                    ? $"{older.Length:N0} Nachrichten persistent verdichtet; {recent.Length:N0} aktuelle Nachrichten bleiben unverändert."
                    : $"Persistente Sitzungschronik bis {older.Length:N0} Nachrichten fortgeschrieben; {recent.Length:N0} aktuelle Nachrichten bleiben unverändert.")).ConfigureAwait(false);
        }

        var messages = new List<RunMessage>
        {
            new("user", SplitContentParts(BuildPreparedHistoryEnvelope(preparedText, older.Length, older[^1], profile))),
        };
        messages.AddRange(BuildExactMessages(recent));
        var finalCharacters = CountCharacters(messages);
        if (finalCharacters > historyBudget)
        {
            await progress(new(
                96,
                "Sitzungsverlauf wird weiter komprimiert",
                $"Das Restbudget reicht für Verdichtung und aktuelle Nachrichten noch nicht aus ({finalCharacters:N0}/{historyBudget:N0} Zeichen). Ein weiterer Komprimierungslauf wird gestartet.")).ConfigureAwait(false);

            var combinedHistory = new StringBuilder()
                .AppendLine("[BISHERIGE_AI_VERDICHTUNG]")
                .AppendLine(preparedText.Trim())
                .AppendLine("[ENDE_BISHERIGE_AI_VERDICHTUNG]")
                .AppendLine()
                .AppendLine("[BISHER_UNVERAENDERTE_AKTUELLE_NACHRICHTEN]");
            foreach (var message in recent)
            {
                combinedHistory.Append(FormatMessage(message));
            }
            combinedHistory.AppendLine("[ENDE_BISHER_UNVERAENDERTE_AKTUELLE_NACHRICHTEN]");

            var finalTarget = Math.Max(384, historyBudget - 640);
            preparedText = await PrepareBlocksAsync(
                client,
                sessionId,
                SplitText(combinedHistory.ToString(), CalculatePreparationInputCharacters(contextLength)),
                modelId,
                coding,
                profile,
                contextLength,
                finalTarget,
                progress,
                cancellationToken).ConfigureAwait(false);

            messages =
            [
                new("user", SplitContentParts(BuildPreparedHistoryEnvelope(
                    preparedText,
                    eligible.Length,
                    eligible[^1],
                    profile))),
            ];
            finalCharacters = CountCharacters(messages);
            if (finalCharacters > historyBudget)
            {
                // The envelope itself consumes a small part of the budget. Start another
                // model-backed reduction instead of cutting text or failing on budget alone.
                preparedText = await PrepareBlocksAsync(
                    client,
                    sessionId,
                    SplitText(preparedText, CalculatePreparationInputCharacters(contextLength)),
                    modelId,
                    coding,
                    profile,
                    contextLength,
                    Math.Max(256, historyBudget - 768),
                    progress,
                    cancellationToken).ConfigureAwait(false);
                messages =
                [
                    new("user", SplitContentParts(BuildPreparedHistoryEnvelope(
                        preparedText,
                        eligible.Length,
                        eligible[^1],
                        profile))),
                ];
            }

            await chats.SaveSessionContextPreparationAsync(
                new SessionContextPreparation(
                    fullCacheKey,
                    sessionId,
                    historyRevision,
                    modelId,
                    historyBudget,
                    eligible[^1].Id,
                    eligible.Length,
                    preparedText,
                    DateTimeOffset.UtcNow,
                    profile),
                cancellationToken).ConfigureAwait(false);

            await progress(new(
                100,
                "Sitzungsverlauf aufbereitet",
                $"Der gesamte relevante Sitzungsverlauf wurde auf das verbleibende Budget von {historyBudget:N0} Zeichen komprimiert und persistent gespeichert.")).ConfigureAwait(false);
        }
        return new(
            messages,
            new SessionContextDescriptor(
                historyRevision,
                eligible.Length,
                eligible.Length,
                EstimateTokens(messages),
                PreparedByAi: true),
            contextLength,
            CacheHit: reusedPersistentHistory);
    }

    private async Task<SessionContextPreparation?> FindReusablePreparationAsync(
        Guid sessionId,
        string modelId,
        ChatMessage[] older,
        SessionContextProfile profile,
        CancellationToken cancellationToken)
    {
        var candidates = await chats.ListSessionContextPreparationsAsync(
            sessionId,
            modelId,
            older.Length,
            profile,
            cancellationToken).ConfigureAwait(false);
        foreach (var candidate in candidates)
        {
            if (candidate.MessageCount <= 0 || candidate.MessageCount > older.Length)
            {
                continue;
            }
            var prefix = older[..candidate.MessageCount];
            if (candidate.ThroughMessageId != prefix[^1].Id)
            {
                continue;
            }
            var prefixRevision = CreateHistoryRevision(prefix);
            if (!string.Equals(candidate.HistoryRevision, prefixRevision, StringComparison.Ordinal))
            {
                continue;
            }
            var expectedKey = CreateCacheKey(
                sessionId,
                prefixRevision,
                modelId,
                candidate.ContextBudget,
                CalculateSummaryBudget(candidate.ContextBudget),
                profile);
            if (string.Equals(candidate.CacheKey, expectedKey, StringComparison.Ordinal))
            {
                return candidate;
            }
        }
        return null;
    }

    private static async Task<string> PrepareHistoryAsync(
        GoAiClient client,
        Guid sessionId,
        ChatMessage[] messages,
        string modelId,
        bool coding,
        SessionContextProfile profile,
        int contextLength,
        int targetCharacters,
        Func<SessionContextProgress, Task> progress,
        CancellationToken cancellationToken)
    {
        var maximumInputCharacters = CalculatePreparationInputCharacters(contextLength);
        var blocks = BuildHistoryBlocks(messages, maximumInputCharacters);
        return await PrepareBlocksAsync(
            client,
            sessionId,
            blocks,
            modelId,
            coding,
            profile,
            contextLength,
            targetCharacters,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ExtendPreparedHistoryAsync(
        GoAiClient client,
        Guid sessionId,
        string preparedHistory,
        ChatMessage[] additionalMessages,
        string modelId,
        bool coding,
        SessionContextProfile profile,
        int contextLength,
        int targetCharacters,
        Func<SessionContextProgress, Task> progress,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder()
            .AppendLine("[BESTEHENDE_PERSISTENTE_SITZUNGSCHRONIK]")
            .AppendLine(preparedHistory.Trim())
            .AppendLine("[ENDE_BESTEHENDE_PERSISTENTE_SITZUNGSCHRONIK]")
            .AppendLine()
            .AppendLine("[NEU_HINZUGEKOMMENE_AELTERE_ORIGINALNACHRICHTEN]");
        foreach (var message in additionalMessages)
        {
            builder.Append(FormatMessage(message));
        }
        builder.AppendLine("[ENDE_NEU_HINZUGEKOMMENE_AELTERE_ORIGINALNACHRICHTEN]");
        var blocks = SplitText(builder.ToString(), CalculatePreparationInputCharacters(contextLength));
        return await PrepareBlocksAsync(
            client,
            sessionId,
            blocks,
            modelId,
            coding,
            profile,
            contextLength,
            targetCharacters,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> PrepareBlocksAsync(
        GoAiClient client,
        Guid sessionId,
        List<string> blocks,
        string modelId,
        bool coding,
        SessionContextProfile profile,
        int contextLength,
        int targetCharacters,
        Func<SessionContextProgress, Task> progress,
        CancellationToken cancellationToken)
    {
        if (blocks.Count == 0)
        {
            throw new InvalidDataException("Für die Sitzungsverdichtung wurde kein Inhalt bereitgestellt.");
        }

        targetCharacters = Math.Max(256, targetCharacters);
        var maximumInputCharacters = CalculatePreparationInputCharacters(contextLength);
        var summaries = new List<string>(blocks.Count);
        var initialBlockTarget = CalculateBlockSummaryTarget(targetCharacters, blocks.Count);
        for (var index = 0; index < blocks.Count; index++)
        {
            var percent = 5 + (int)Math.Round(index * 60d / Math.Max(1, blocks.Count));
            await progress(new(
                percent,
                "Sitzungsverlauf wird aufbereitet",
                $"Verlaufsblock {index + 1:N0}/{blocks.Count:N0} wird durch {modelId} strukturiert.")).ConfigureAwait(false);
            summaries.Add(await RunSummaryAsync(
                client,
                sessionId,
                blocks[index],
                modelId,
                coding,
                profile,
                contextLength,
                initialBlockTarget,
                cancellationToken).ConfigureAwait(false));
        }

        var combined = string.Join("\n\n", summaries);
        var pass = 0;
        var previousLength = int.MaxValue;
        while (combined.Length > targetCharacters && pass < 24)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pass++;
            var reductionBlocks = SplitText(combined, maximumInputCharacters);
            var stagnating = combined.Length >= previousLength - Math.Max(64, previousLength / 50);
            previousLength = combined.Length;
            var requestedTotal = stagnating
                ? Math.Max(256, (int)Math.Floor(targetCharacters * 0.70d))
                : targetCharacters;
            var reductionTarget = CalculateBlockSummaryTarget(requestedTotal, reductionBlocks.Count);
            var reduced = new List<string>(reductionBlocks.Count);
            for (var index = 0; index < reductionBlocks.Count; index++)
            {
                reduced.Add(await RunSummaryAsync(
                    client,
                    sessionId,
                    reductionBlocks[index],
                    modelId,
                    coding,
                    profile,
                    contextLength,
                    reductionTarget,
                    cancellationToken).ConfigureAwait(false));
            }
            combined = string.Join("\n\n", reduced);
            await progress(new(
                Math.Min(99, 68 + (pass * 2)),
                "Sitzungsverlauf wird weiter komprimiert",
                $"Komprimierungslauf {pass:N0} reduziert die Sitzung auf das verbleibende Kontextbudget ({combined.Length:N0}/{targetCharacters:N0} Zeichen).")).ConfigureAwait(false);
        }
        if (string.IsNullOrWhiteSpace(combined) || combined.Length > targetCharacters)
        {
            throw new InvalidDataException(
                $"General AI hat das angeforderte Komprimierungsziel nach {pass:N0} aufeinanderfolgenden Läufen nicht eingehalten ({combined.Length:N0}/{targetCharacters:N0} Zeichen)." );
        }
        return combined.Trim();
    }

    private static async Task<string> RunSummaryAsync(
        GoAiClient client,
        Guid sessionId,
        string historyBlock,
        string modelId,
        bool coding,
        SessionContextProfile profile,
        int contextLength,
        int targetCharacters,
        CancellationToken cancellationToken)
    {
        var instruction = profile == SessionContextProfile.Audiobook
            ? $"""
                Erzeuge aus dem folgenden älteren Hörbuch-Sitzungsverlauf eine verlustarme, persistente Story-Chronik.
                Zielumfang: höchstens {targetCharacters:N0} Zeichen. Schreibe keine neue Szene und antworte nicht auf alte Nutzerprompts.
                Bewahre strukturiert und widerspruchsfrei:
                - Szenario, Genre, Ton, Stil, Perspektive und Zeitform;
                - jede Figur mit Aussehen, Stimme, Eigenschaften, Beziehungen, Wissen, Zielen und aktuellem Zustand;
                - Weltregeln, Orte, Gegenstände und die vollständige Chronologie wichtiger Ereignisse;
                - offene Konflikte, Versprechen, Geheimnisse und Handlungsfäden;
                - mindestens eine Hauptfigur samt verbindlicher Erzählperspektive und bisheriger Entwicklung;
                - vorhandene Kapitelüberschriften, die laufende ausgeschriebene Kapitelnummer sowie den belastbaren Zustand,
                  ob der aktuelle Kapitelbogen noch offen oder narrativ abgeschlossen ist;
                - die letzten Richtungsangaben des Nutzers sowie langfristig geplante, noch nicht eingetretene Serienhandlungen;
                - als CONTINUATION_ANCHOR die letzten zusammenhängenden Absätze der neuesten erzählten Szene möglichst wörtlich.
                Trenne bereits geschehene Ereignisse eindeutig von zukünftigen Serienvorgaben. Kürze Wiederholungen, aber erfinde,
                löse oder verändere keine Handlung. Der Szenenanker darf niemals linear abgeschnitten werden.
                """
            : $"""
                Verdichte den folgenden älteren GO-Sitzungsverlauf für die verlustarme Weiterverwendung in derselben Sitzung.
                Zielumfang: höchstens {targetCharacters:N0} Zeichen.
                Bewahre konkrete Entscheidungen, Nutzerpräferenzen, bereits ausgeführte Aktionen, offene Aufgaben,
                Fehlermeldungen, relevante Zahlen und Einheiten, Datei- oder Modellnamen sowie Dokumentquellen exakt.
                Trenne sicher feststehende Ergebnisse von offenen oder unsicheren Punkten. Erfinde nichts.
                Entferne nur Wiederholungen, Höflichkeitsfloskeln und nicht mehr relevante Zwischenformulierungen.
                Schreibe eine strukturierte deutsche Sitzungschronik, keine Antwort auf eine alte Nutzerfrage.
                """;
        var parts = new List<ContentPart> { new("text", Text: instruction) };
        parts.AddRange(SplitContentParts(historyBlock));
        var request = new RunRequest(
            GoAiProtocol.Version,
            coding ? RunMode.Code : RunMode.General,
            [new RunMessage("user", parts)],
            ClientCapabilities: [],
            Limits: new RunLimits(
                MaximumOutputTokens: Math.Clamp((targetCharacters + 5) / 6, 64, 8_192),
                MaximumContextTokens: Math.Clamp(contextLength, 1_024, 262_144),
                TimeoutSeconds: 3_600),
            SessionId: sessionId.ToString("D"),
            AllowedServerTools: [],
            PreferredGeneralModelId: coding ? null : modelId,
            ConversationProfile: profile == SessionContextProfile.Audiobook
                ? ConversationProfile.Audiobook
                : ConversationProfile.General);
        var accepted = await client.CreateRunAsync(
            request,
            $"sessionprep-{Guid.NewGuid():N}",
            cancellationToken).ConfigureAwait(false);
        var content = new StringBuilder();
        try
        {
            await foreach (var item in client.StreamRunEventsAsync(
                accepted.RunId,
                cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                switch (item.Type)
                {
                    case RunEventTypes.TextDelta:
                        content.Append(item.Data.Deserialize<TextDeltaEvent>(JsonOptions)?.Delta);
                        break;
                    case RunEventTypes.RunFailed:
                        var failure = item.Data.Deserialize<RunFailedEvent>(JsonOptions);
                        throw new InvalidOperationException(
                            failure?.Message ?? "Der interne Lauf zur Sitzungsverdichtung ist fehlgeschlagen.");
                    case RunEventTypes.RunCancelled:
                        throw new OperationCanceledException(
                            "Der interne Lauf zur Sitzungsverdichtung wurde abgebrochen.",
                            cancellationToken);
                    case RunEventTypes.RunCompleted:
                        var parsed = GeneralAgentResponseParser.Parse(content.ToString(), "Sitzungsverlauf verdichten");
                        if (string.IsNullOrWhiteSpace(parsed.Message))
                        {
                            throw new InvalidDataException("General AI hat keine Sitzungsverdichtung erzeugt.");
                        }
                        return parsed.Message.Trim();
                }
            }
            throw new InvalidDataException("Der interne Lauf zur Sitzungsverdichtung endete ohne Abschlussereignis.");
        }
        catch (OperationCanceledException)
        {
            try
            {
                await client.CancelRunAsync(accepted.RunId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _ = exception;
            }
            throw;
        }
    }

    private static async Task<(string ModelId, int ContextLength)> ResolveModelAsync(
        GoAiClient client,
        string preferredGeneralModelId,
        bool coding,
        int? knownContextLength,
        CancellationToken cancellationToken)
    {
        if (!coding && knownContextLength is >= 2_048)
        {
            return (preferredGeneralModelId, knownContextLength.Value);
        }
        var status = await client.GetModelStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!status.ProviderReachable)
        {
            throw new InvalidOperationException("Die Modellkontextlänge für den Sitzungsverlauf konnte nicht ermittelt werden.");
        }
        var model = coding
            ? status.Models.FirstOrDefault(static item => item.Downloaded && item.Role == "code")
            : status.Models.FirstOrDefault(item => item.Downloaded
                && string.Equals(item.Id, preferredGeneralModelId, StringComparison.OrdinalIgnoreCase));
        if (model is null)
        {
            throw new InvalidOperationException(coding
                ? "Das konfigurierte Laguna-Codingmodell ist nicht verfügbar."
                : $"Das ausgewählte General-AI-Modell '{preferredGeneralModelId}' ist nicht verfügbar.");
        }
        return (model.Id, Math.Max(2_048, knownContextLength ?? model.ContextTokens));
    }

    private static int CalculateHistoryBudget(int contextLength, string prompt)
    {
        const int outputTokens = 8_192;
        const int envelopeAndToolReserve = 6_144;
        var safetyTokens = Math.Min(8_192, Math.Max(2_048, contextLength / 16));
        var promptTokens = EstimateTokens(prompt) + 256;
        var available = Math.Max(
            1_024,
            contextLength - outputTokens - envelopeAndToolReserve - safetyTokens - promptTokens);
        return Math.Min(1_200_000, available * 3);
    }

    private static int CalculateSummaryBudget(int historyBudget)
    {
        var summaryBudget = Math.Clamp(
            (int)Math.Floor((historyBudget - 512) * 0.45d),
            1_200,
            24_000);
        return Math.Min(summaryBudget, Math.Max(1_024, historyBudget - 768));
    }

    private static int CalculatePreparationInputCharacters(int contextLength)
    {
        const int maximumOutputTokens = 8_192;
        const int policyReserveTokens = 6_144;
        var safetyTokens = Math.Min(8_192, Math.Max(2_048, contextLength / 16));
        var inputTokens = Math.Max(
            8_192,
            contextLength - maximumOutputTokens - policyReserveTokens - safetyTokens);
        return Math.Min(MaximumPreparationInputCharacters, inputTokens * 3);
    }

    private static ChatMessage[] SelectRecentMessages(
        ChatMessage[] messages,
        int budget)
    {
        var selected = new Stack<ChatMessage>();
        var characters = 0;
        for (var index = messages.Length - 1; index >= 0; index--)
        {
            var cost = MessageCharacterCost(messages[index]);
            if (characters + cost > budget)
            {
                break;
            }
            selected.Push(messages[index]);
            characters += cost;
        }
        return selected.ToArray();
    }

    internal static ChatMessage[] SelectEligibleHistory(
        IReadOnlyList<ChatMessage> history,
        SessionContextProfile profile = SessionContextProfile.General)
    {
        var ordered = history
            .Where(static message => message.Status is MessageStatus.Completed
                or MessageStatus.Cancelled
                or MessageStatus.Failed
                or MessageStatus.Interrupted)
            .OrderBy(static message => message.CreatedAt)
            .ThenBy(static message => message.Id)
            .ToArray();
        var excluded = new HashSet<Guid>();
        for (var index = 0; index < ordered.Length; index++)
        {
            var message = ordered[index];
            if (message.Role != ChatRole.Assistant
                || !string.Equals(message.ToolExecution?.Tool, "Vorlesen", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            excluded.Add(message.Id);
            for (var previousIndex = index - 1; previousIndex >= 0; previousIndex--)
            {
                if (ordered[previousIndex].Role == ChatRole.User)
                {
                    excluded.Add(ordered[previousIndex].Id);
                    break;
                }
                if (ordered[previousIndex].Role == ChatRole.Assistant)
                {
                    break;
                }
            }
        }

        var candidates = ordered
            .Where(message => !excluded.Contains(message.Id)
                && message.Role is ChatRole.User or ChatRole.Assistant
                && !string.IsNullOrWhiteSpace(message.Content))
            .ToArray();
        if (profile != SessionContextProfile.Audiobook)
        {
            return candidates
                .Where(static message => message.Status == MessageStatus.Completed)
                .ToArray();
        }

        var firstAudiobookAssistant = Array.FindIndex(candidates, static message =>
            message.Role == ChatRole.Assistant
            && message.ContentProfile == MessageContentProfile.Audiobook
            && message.Status is MessageStatus.Completed or MessageStatus.Cancelled or MessageStatus.Interrupted);
        if (firstAudiobookAssistant < 0)
        {
            return [];
        }
        var firstStoryMessage = firstAudiobookAssistant;
        for (var index = firstAudiobookAssistant - 1; index >= 0; index--)
        {
            if (candidates[index].Role == ChatRole.User && candidates[index].Status == MessageStatus.Completed)
            {
                firstStoryMessage = index;
                break;
            }
        }
        return candidates[firstStoryMessage..]
            .Where(static message => message.Role == ChatRole.User
                ? message.Status == MessageStatus.Completed
                : message.ContentProfile == MessageContentProfile.Audiobook
                    && message.Status is MessageStatus.Completed or MessageStatus.Cancelled or MessageStatus.Interrupted)
            .ToArray();
    }

    internal static int CalculateBlockSummaryTarget(int totalTargetCharacters, int blockCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(blockCount, 1);
        var separatorReserve = Math.Max(0, blockCount - 1) * 2;
        return Math.Max(128, (Math.Max(256, totalTargetCharacters) - separatorReserve) / blockCount);
    }

    private static RunMessage[] BuildExactMessages(ChatMessage[] source) => source
        .Select(static message => new RunMessage(
            message.Role == ChatRole.Assistant ? "assistant" : "user",
            SplitContentParts(message.Content)))
        .ToArray();

    private static List<string> BuildHistoryBlocks(
        ChatMessage[] messages,
        int maximumCharacters)
    {
        var blocks = new List<string>();
        var builder = new StringBuilder();
        foreach (var message in messages)
        {
            var formatted = FormatMessage(message);
            if (formatted.Length > maximumCharacters)
            {
                if (builder.Length > 0)
                {
                    blocks.Add(builder.ToString());
                    builder.Clear();
                }
                blocks.AddRange(SplitText(formatted, maximumCharacters));
                continue;
            }
            if (builder.Length > 0 && builder.Length + formatted.Length > maximumCharacters)
            {
                blocks.Add(builder.ToString());
                builder.Clear();
            }
            builder.Append(formatted);
        }
        if (builder.Length > 0)
        {
            blocks.Add(builder.ToString());
        }
        return blocks;
    }

    private static string FormatMessage(ChatMessage message)
    {
        var role = message.Role == ChatRole.Assistant ? "AI" : "Nutzer";
        var builder = new StringBuilder()
            .Append("\n--- ").Append(role)
            .Append(" | ").Append(message.CreatedAt.ToString("O"))
            .Append(" | Nachricht ").Append(message.Id.ToString("D"))
            .AppendLine(" ---")
            .AppendLine(message.Content);
        if (!string.IsNullOrWhiteSpace(message.ContextSummary))
        {
            builder.Append("Kontextnotiz: ").AppendLine(message.ContextSummary);
        }
        return builder.ToString();
    }

    private static string BuildPreparedHistoryEnvelope(
        string preparedText,
        int messageCount,
        ChatMessage throughMessage,
        SessionContextProfile profile)
    {
        var marker = profile == SessionContextProfile.Audiobook
            ? "GO_AUDIOBOOK_STORY_CHRONICLE"
            : "GO_SESSION_HISTORY_PREPARED";
        var description = profile == SessionContextProfile.Audiobook
            ? "persistente Story-Chronik mit verbindlichem Szenenanker"
            : "persistente Sitzungschronik";
        return $"""
            [{marker}]
            Der folgende ältere Verlauf von {messageCount:N0} Nachrichten wurde wegen des Modellfensters intern durch AI verdichtet.
            Er reicht einschließlich Nachricht {throughMessage.Id:D} vom {throughMessage.CreatedAt:O}.
            Behandle ihn als {description}; neuere Originalnachrichten folgen danach unverändert.

            {preparedText}
            [ENDE_{marker}]
            """;
    }

    private static ContentPart[] SplitContentParts(string text) => SplitText(
            text,
            MaximumContentPartCharacters)
        .Select(static part => new ContentPart("text", Text: part))
        .ToArray();

    private static List<string> SplitText(string text, int maximumCharacters)
    {
        var result = new List<string>();
        for (var offset = 0; offset < text.Length;)
        {
            var length = Math.Min(maximumCharacters, text.Length - offset);
            if (offset + length < text.Length && char.IsHighSurrogate(text[offset + length - 1]))
            {
                length--;
            }
            result.Add(text.Substring(offset, length));
            offset += length;
        }
        return result;
    }

    private static int CountCharacters(IEnumerable<RunMessage> messages) => messages
        .SelectMany(static message => message.Content)
        .Sum(static part => part.Text?.Length ?? 0);

    private static int MessageCharacterCost(ChatMessage message) => message.Content.Length + 96;

    private static int EstimateTokens(IEnumerable<RunMessage> messages) => Math.Max(
        0,
        (CountCharacters(messages) + 2) / 3);

    private static int EstimateTokens(string value) => Math.Max(1, (value.Length + 2) / 3);

    private static int EstimateTokensByCharacters(int characters) => Math.Max(1, (characters + 2) / 3);

    private static string CreateHistoryRevision(ChatMessage[] source)
    {
        if (source.Length == 0)
        {
            return Hash("empty-session-history-v1");
        }
        var builder = new StringBuilder();
        foreach (var message in source)
        {
            builder.Append(message.Id.ToString("D")).Append('|')
                .Append(message.UpdatedAt.ToString("O")).Append('|')
                .Append(message.Role).Append('|')
                .Append(message.Status).Append('|')
                .Append(message.ContentProfile).Append('|')
                .Append(Hash(message.Content)).Append('|')
                .Append(Hash(message.ContextSummary ?? string.Empty)).Append('\n');
        }
        return Hash(builder.ToString());
    }

    private static string CreateCacheKey(
        Guid sessionId,
        string historyRevision,
        string modelId,
        int historyBudget,
        int summaryBudget,
        SessionContextProfile profile) => Hash(string.Join('|',
            SchemaVersion,
            sessionId.ToString("D"),
            historyRevision,
            modelId.Trim().ToLowerInvariant(),
            historyBudget,
            summaryBudget,
            profile));

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
