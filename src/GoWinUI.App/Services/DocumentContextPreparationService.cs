using GoAi.Client;
using GoAi.Contracts;
using GoWinUI.Core.Chat;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GoWinUI.App.Services;

public sealed record DocumentContextProgress(
    int Percent,
    string Status,
    string Detail,
    string? Model = null);

public sealed record DocumentRunContext(
    DocumentContextDescriptor Descriptor,
    IReadOnlyList<ContentPart> ContentParts,
    int ContextLength,
    int HistoryBudgetCharacters,
    IReadOnlyList<DocumentContextHit> Evidence,
    bool CacheHit);

public sealed class DocumentContextPreparationService(IDocumentIngestor documents)
{
    private const int SchemaVersion = 3;
    private const int MaximumContentPartCharacters = 220_000;
    private const int MaximumEmbeddingBatchCharacters = 400_000;
    private const int MaximumCandidateCharacters = 600_000;
    private static readonly JsonSerializerOptions JsonOptions = GoAiProtocol.CreateJsonOptions();

    public async Task<DocumentRunContext?> PrepareAsync(
        GoAiClient client,
        Guid sessionId,
        Guid assistantMessageId,
        string prompt,
        string modelId,
        int minimumHistoryReserveTokens,
        Func<DocumentContextProgress, Task> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumHistoryReserveTokens, 1_024);
        ArgumentNullException.ThrowIfNull(progress);

        // A transient error belongs to the previous run, not to the reusable local index.
        // A new prompt therefore retries from the persisted ready document objects.
        await documents.SetContextPreparationStateAsync(
            sessionId,
            assistantMessageId,
            null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var readyDocuments = (await documents.ListAsync(sessionId, cancellationToken).ConfigureAwait(false))
            .Where(static item => item.PreparationStatus == DocumentPreparationStatus.Ready)
            .OrderBy(static item => item.CreatedAt)
            .ToArray();
        if (readyDocuments.Length == 0)
        {
            return null;
        }

        var modelStatus = await client.GetModelStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!modelStatus.ProviderReachable)
        {
            throw new InvalidOperationException("Die Kontextlänge des ausgewählten General-AI-Modells konnte nicht ermittelt werden.");
        }
        var selectedModel = modelStatus.Models.FirstOrDefault(item =>
            item.Downloaded && string.Equals(item.Id, modelId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Das ausgewählte General-AI-Modell '{modelId}' ist nicht verfügbar.");
        var contextLength = Math.Max(2_048, selectedModel.ContextTokens);

        var pages = new List<DocumentPage>();
        foreach (var document in readyDocuments)
        {
            pages.AddRange(await documents.ReadPagesAsync(document.Id, cancellationToken).ConfigureAwait(false));
        }
        pages = pages.OrderBy(page => Array.FindIndex(readyDocuments, document => document.Id == page.DocumentId))
            .ThenBy(static page => page.PageNumber)
            .ToList();
        var corpusRevision = CreateCorpusRevision(readyDocuments);
        var fullText = BuildPageBlocks(pages);
        var fullDocumentTokens = EstimateTokens(fullText);
        var safetyTokens = Math.Min(8_192, Math.Max(2_048, contextLength / 16));
        const int outputTokens = 8_192;
        const int envelopeAndToolReserve = 6_144;
        var minimumHistoryReserve = Math.Clamp(minimumHistoryReserveTokens, 1_024, 16_384);
        var promptTokens = EstimateTokens(prompt) + 256;
        var documentBudget = Math.Max(
            4_096,
            contextLength - outputTokens - safetyTokens - envelopeAndToolReserve - promptTokens - minimumHistoryReserve);

        if (fullDocumentTokens <= documentBudget)
        {
            var shaByDocument = readyDocuments.ToDictionary(static document => document.Id, static document => document.Sha256);
            var evidence = pages.Select(page => new DocumentContextHit(
                page.DocumentId,
                shaByDocument[page.DocumentId],
                page.FileName ?? page.DocumentId.ToString("D"),
                page.PageNumber,
                page.Text,
                1)).ToArray();
            await documents.SaveEvidenceAsync(assistantMessageId, evidence, cancellationToken).ConfigureAwait(false);
            var remainingTokens = Math.Max(
                1_024,
                contextLength - outputTokens - safetyTokens - envelopeAndToolReserve - promptTokens - fullDocumentTokens);
            return new(
                new DocumentContextDescriptor(
                    DocumentContextMode.Full,
                    corpusRevision,
                    readyDocuments.Length,
                    pages.Count,
                    fullDocumentTokens,
                    pages.Count),
                SplitContentParts(fullText),
                contextLength,
                Math.Min(1_200_000, remainingTokens * 3),
                evidence,
                CacheHit: false);
        }

        var promptFingerprint = Hash(NormalizePrompt(prompt));
        var cacheKey = Hash(string.Join('|',
            SchemaVersion,
            sessionId.ToString("D"),
            corpusRevision,
            promptFingerprint,
            modelId.Trim().ToLowerInvariant(),
            documentBudget));
        var cached = await documents.GetContextPreparationAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            var cachedPreparedText = cached.PreparedText;
            if (cachedPreparedText.Length > CalculatePreparedDossierLimit(documentBudget))
            {
                await progress(new(
                    85,
                    "Dokumentkontext wird weiter komprimiert",
                    "Die gespeicherte Aufbereitung überschreitet das aktuelle Restbudget und wird erneut durch AI verdichtet.",
                    modelId)).ConfigureAwait(false);
                cachedPreparedText = await CompressPreparedTextToBudgetAsync(
                    client,
                    sessionId,
                    cachedPreparedText,
                    modelId,
                    contextLength,
                    documentBudget,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                cached = cached with
                {
                    PreparedText = cachedPreparedText,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                await documents.SaveContextPreparationAsync(cached, cancellationToken).ConfigureAwait(false);
            }
            var cachedContext = BuildPreparedContext(cachedPreparedText, cached.Evidence, documentBudget);
            await documents.SaveEvidenceAsync(assistantMessageId, cachedContext.Evidence, cancellationToken).ConfigureAwait(false);
            await progress(new(
                100,
                "Dokumentkontext aufbereitet",
                $"Gespeichertes Evidenzdossier mit {cachedContext.Evidence.Count:N0} Originalbelegen geladen.")).ConfigureAwait(false);
            return CreatePreparedResult(
                readyDocuments.Length,
                pages.Count,
                corpusRevision,
                contextLength,
                promptTokens,
                safetyTokens,
                cachedContext,
                CacheHit: true);
        }

        await documents.SetContextPreparationStateAsync(
            sessionId,
            assistantMessageId,
            DocumentPreparationStatus.Preparing,
            1,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        string? activeModel = null;
        try
        {
            var embeddingModel = modelStatus.Models.FirstOrDefault(static item => item.Downloaded && item.Role == "embedding")
                ?? throw new InvalidOperationException("Das konfigurierte BGE-M3-Embeddingmodell ist nicht verfügbar.");
            activeModel = embeddingModel.Id;
            await progress(new(
                1,
                "Dokumentkontext wird aufbereitet",
                $"{readyDocuments.Length:N0} Dokumente werden semantisch indiziert.",
                embeddingModel.Id)).ConfigureAwait(false);

            int indexedChunks;
            IReadOnlyList<DocumentContextHit> evidence;
            try
            {
                indexedChunks = await EnsureEmbeddingsAsync(
                    client,
                    sessionId,
                    assistantMessageId,
                    embeddingModel.Id,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                var queryResponse = await client.CreateEmbeddingsAsync(
                    new EmbeddingBatchRequest(
                        [new EmbeddingInput("query", prompt)],
                        KeepModelLoaded: true),
                    cancellationToken).ConfigureAwait(false);
                var queryVector = queryResponse.Vectors.Single(static item => item.Id == "query").Values;
                var candidateCharacters = CalculatePreparationCandidateCharacters(contextLength, prompt);
                evidence = await documents.SearchHybridAsync(
                    sessionId,
                    prompt,
                    embeddingModel.Id,
                    queryVector,
                    candidateCharacters,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    await client.ReleaseEmbeddingModelAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception releaseException) when (releaseException is not OutOfMemoryException)
                {
                    _ = releaseException;
                }
                throw;
            }
            await client.ReleaseEmbeddingModelAsync(cancellationToken).ConfigureAwait(false);
            if (evidence.Count == 0)
            {
                throw new InvalidOperationException("Der Dokumentindex lieferte keine aufbereitbaren Originalbelege.");
            }

            await documents.SetContextPreparationStateAsync(
                sessionId,
                assistantMessageId,
                DocumentPreparationStatus.Preparing,
                70,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            activeModel = modelId;
            await progress(new(
                70,
                "Evidenzdossier wird verdichtet",
                $"{modelId} verdichtet relevante Originalbelege.",
                modelId)).ConfigureAwait(false);
            var preparedText = await RunPreparationAsync(
                client,
                sessionId,
                prompt,
                modelId,
                contextLength,
                documentBudget,
                evidence,
                cacheKey,
                cancellationToken).ConfigureAwait(false);
            preparedText = await CompressPreparedTextToBudgetAsync(
                client,
                sessionId,
                preparedText,
                modelId,
                contextLength,
                documentBudget,
                progress,
                cancellationToken).ConfigureAwait(false);
            var preparedContext = BuildPreparedContext(preparedText, evidence, documentBudget);
            var preparation = new DocumentContextPreparation(
                cacheKey,
                sessionId,
                corpusRevision,
                promptFingerprint,
                modelId,
                documentBudget,
                preparedText,
                preparedContext.Evidence,
                DateTimeOffset.UtcNow);
            await documents.SaveContextPreparationAsync(preparation, cancellationToken).ConfigureAwait(false);
            await documents.SaveEvidenceAsync(assistantMessageId, preparedContext.Evidence, cancellationToken).ConfigureAwait(false);
            await documents.SetContextPreparationStateAsync(
                sessionId,
                assistantMessageId,
                null,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await progress(new(
                100,
                "Dokumentkontext aufbereitet",
                $"{preparedContext.Evidence.Count:N0} Originalbelege geladen · {indexedChunks:N0} Abschnitte indiziert.",
                modelId)).ConfigureAwait(false);
            return CreatePreparedResult(
                readyDocuments.Length,
                pages.Count,
                corpusRevision,
                contextLength,
                promptTokens,
                safetyTokens,
                preparedContext,
                CacheHit: false);
        }
        catch (OperationCanceledException)
        {
            await documents.SetContextPreparationStateAsync(
                sessionId,
                assistantMessageId,
                null,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var failureMessage = string.IsNullOrWhiteSpace(exception.Message)
                ? "Der Dokumentkontext konnte nicht vollständig und sicher aufbereitet werden."
                : $"Der Dokumentkontext konnte nicht vollständig und sicher aufbereitet werden. Ursache: {exception.Message}";
            await documents.SetContextPreparationStateAsync(
                sessionId,
                assistantMessageId,
                DocumentPreparationStatus.Failed,
                100,
                failureMessage,
                CancellationToken.None).ConfigureAwait(false);
            await progress(new(
                100,
                "Dokumentaufbereitung fehlgeschlagen",
                exception.Message,
                activeModel)).ConfigureAwait(false);
            throw new InvalidOperationException(failureMessage, exception);
        }
    }

    private async Task<int> EnsureEmbeddingsAsync(
        GoAiClient client,
        Guid sessionId,
        Guid assistantMessageId,
        string modelId,
        Func<DocumentContextProgress, Task> progress,
        CancellationToken cancellationToken)
    {
        var chunks = await documents.ListIndexChunksAsync(sessionId, modelId, cancellationToken).ConfigureAwait(false);
        var missing = chunks.Where(static item => item.Embedding is null).ToArray();
        if (missing.Length == 0)
        {
            return chunks.Count;
        }

        var completed = 0;
        foreach (var batch in BatchChunks(missing))
        {
            var response = await client.CreateEmbeddingsAsync(
                new EmbeddingBatchRequest(
                    batch.Select(static item => new EmbeddingInput(item.Id, item.Text)).ToArray(),
                    KeepModelLoaded: true),
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(response.ModelId, modelId, StringComparison.OrdinalIgnoreCase)
                || response.Dimensions <= 0
                || response.Vectors.Count != batch.Count
                || response.Vectors.Any(item => item.Values.Count != response.Dimensions))
            {
                throw new InvalidDataException("Der Embedding-Dienst lieferte ein inkonsistentes Ergebnis.");
            }
            await documents.SaveEmbeddingsAsync(
                response.Vectors.Select(item => new DocumentChunkEmbedding(item.Id, response.ModelId, item.Values)).ToArray(),
                cancellationToken).ConfigureAwait(false);
            completed += batch.Count;
            var percent = 5 + (int)Math.Round(completed * 55d / missing.Length);
            await documents.SetContextPreparationStateAsync(
                sessionId,
                assistantMessageId,
                DocumentPreparationStatus.Preparing,
                percent,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await progress(new(
                percent,
                "Dokumentkontext wird aufbereitet",
                $"{completed:N0}/{missing.Length:N0} Abschnitte semantisch indiziert.",
                modelId)).ConfigureAwait(false);
        }
        return chunks.Count;
    }

    private static async Task<string> RunPreparationAsync(
        GoAiClient client,
        Guid sessionId,
        string prompt,
        string modelId,
        int contextLength,
        int documentBudget,
        IReadOnlyList<DocumentContextHit> evidence,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        var instruction = """
            Erstelle ausschließlich ein kompaktes, promptbezogenes Evidenzdossier für einen nachfolgenden TGA-Assistenzlauf.
            Bewahre technische Zahlen, Einheiten, Normbezeichnungen, Einschränkungen und widersprüchliche Angaben.
            Jede Aussage muss unmittelbar eine Quelle im Format [Dateiname, S. 12] tragen.
            Erfinde nichts und gib keine allgemeine Begrüßung aus. Gliedere nach relevanten Teilfragen und nenne Informationslücken.
            """;
        // SearchHybridAsync limits the raw chunk text. The prompt also contains a
        // document/page header for every hit, so enforce the model-aware character
        // budget again on the final serialized evidence block.
        var evidenceText = BuildEvidenceBlocks(
            evidence,
            CalculatePreparationCandidateCharacters(contextLength, prompt));
        var parts = new List<ContentPart>
        {
            new("text", Text: $"{instruction}\n\nNutzerfrage:\n{prompt}"),
        };
        parts.AddRange(SplitContentParts(evidenceText));
        var request = new RunRequest(
            GoAiProtocol.Version,
            RunMode.General,
            [new RunMessage("user", parts)],
            ClientCapabilities: [],
            Limits: new RunLimits(
                MaximumOutputTokens: Math.Clamp(documentBudget / 4, 2_048, 8_192),
                MaximumContextTokens: contextLength,
                TimeoutSeconds: 3_600),
            SessionId: sessionId.ToString("D"),
            AllowedServerTools: [],
            PreferredGeneralModelId: modelId);
        var accepted = await client.CreateRunAsync(
            request,
            $"docprep-{cacheKey}",
            cancellationToken).ConfigureAwait(false);
        var content = new StringBuilder();
        try
        {
            await foreach (var item in client.StreamRunEventsAsync(accepted.RunId, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                switch (item.Type)
                {
                    case RunEventTypes.TextDelta:
                        content.Append(item.Data.Deserialize<TextDeltaEvent>(JsonOptions)?.Delta);
                        break;
                    case RunEventTypes.RunFailed:
                        var failure = item.Data.Deserialize<RunFailedEvent>(JsonOptions);
                        throw new InvalidOperationException(failure?.Message ?? "Der Dokument-Aufbereitungslauf ist fehlgeschlagen.");
                    case RunEventTypes.RunCancelled:
                        throw new OperationCanceledException("Der Dokument-Aufbereitungslauf wurde abgebrochen.", cancellationToken);
                    case RunEventTypes.RunCompleted:
                        var parsed = GeneralAgentResponseParser.Parse(content.ToString(), prompt);
                        if (string.IsNullOrWhiteSpace(parsed.Message))
                        {
                            throw new InvalidDataException("General AI hat kein Evidenzdossier erzeugt.");
                        }
                        return parsed.Message;
                }
            }
            throw new InvalidDataException("Der Dokument-Aufbereitungslauf endete ohne Abschlussereignis.");
        }
        catch (OperationCanceledException)
        {
            try { await client.CancelRunAsync(accepted.RunId, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
            throw;
        }
    }

    private static async Task<string> CompressPreparedTextToBudgetAsync(
        GoAiClient client,
        Guid sessionId,
        string preparedText,
        string modelId,
        int contextLength,
        int documentBudget,
        Func<DocumentContextProgress, Task> progress,
        CancellationToken cancellationToken)
    {
        var targetCharacters = CalculatePreparedDossierLimit(documentBudget);
        var current = preparedText.Trim();
        var pass = 0;
        while (current.Length > targetCharacters && pass < 24)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pass++;
            await progress(new(
                Math.Min(99, 84 + pass),
                "Dokumentkontext wird weiter komprimiert",
                $"Komprimierungslauf {pass:N0} reduziert das Evidenzdossier auf das verbleibende Budget ({current.Length:N0}/{targetCharacters:N0} Zeichen).",
                modelId)).ConfigureAwait(false);

            var instruction = $"""
                Verdichte das folgende bereits belegte Dokument-Evidenzdossier auf höchstens {targetCharacters:N0} Zeichen.
                Bewahre alle für die Nutzerfrage relevanten technischen Aussagen, Zahlen, Einheiten, Einschränkungen,
                Widersprüche und vorhandenen Quellen im exakten Format [Dateiname, S. 12]. Erfinde nichts.
                Entferne Wiederholungen und schwächer relevante Erläuterungen. Antworte ausschließlich mit dem
                weiter verdichteten Evidenzdossier ohne Einleitung oder Kommentar zu dieser Bearbeitung.
                """;
            var parts = new List<ContentPart> { new("text", Text: instruction) };
            parts.AddRange(SplitContentParts(current));
            var request = new RunRequest(
                GoAiProtocol.Version,
                RunMode.General,
                [new RunMessage("user", parts)],
                ClientCapabilities: [],
                Limits: new RunLimits(
                    MaximumOutputTokens: Math.Clamp((targetCharacters + 5) / 6, 64, 8_192),
                    MaximumContextTokens: contextLength,
                    TimeoutSeconds: 3_600),
                SessionId: sessionId.ToString("D"),
                AllowedServerTools: [],
                PreferredGeneralModelId: modelId);
            var accepted = await client.CreateRunAsync(
                request,
                $"docprep-reduce-{Guid.NewGuid():N}",
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
                                failure?.Message ?? "Der zusätzliche Dokument-Komprimierungslauf ist fehlgeschlagen.");
                        case RunEventTypes.RunCancelled:
                            throw new OperationCanceledException(
                                "Der zusätzliche Dokument-Komprimierungslauf wurde abgebrochen.",
                                cancellationToken);
                        case RunEventTypes.RunCompleted:
                            var parsed = GeneralAgentResponseParser.Parse(content.ToString(), "Dokumentkontext verdichten");
                            if (string.IsNullOrWhiteSpace(parsed.Message))
                            {
                                throw new InvalidDataException("General AI hat keine weitere Dokumentverdichtung erzeugt.");
                            }
                            current = parsed.Message.Trim();
                            break;
                    }
                }
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

        if (string.IsNullOrWhiteSpace(current) || current.Length > targetCharacters)
        {
            throw new InvalidDataException(
                $"General AI hat das Dokument-Komprimierungsziel nach {pass:N0} aufeinanderfolgenden Läufen nicht eingehalten ({current.Length:N0}/{targetCharacters:N0} Zeichen).");
        }
        return current;
    }

    private static DocumentRunContext CreatePreparedResult(
        int documentCount,
        int totalPageCount,
        string corpusRevision,
        int contextLength,
        int promptTokens,
        int safetyTokens,
        PreparedContext prepared,
        bool CacheHit)
    {
        var documentTokens = EstimateTokens(prepared.Text);
        const int outputTokens = 8_192;
        const int envelopeAndToolReserve = 6_144;
        var remainingTokens = Math.Max(
            1_024,
            contextLength - outputTokens - safetyTokens - envelopeAndToolReserve - promptTokens - documentTokens);
        return new(
            new DocumentContextDescriptor(
                DocumentContextMode.Prepared,
                corpusRevision,
                documentCount,
                totalPageCount,
                documentTokens,
                prepared.Evidence.Select(static item => (item.DocumentId, item.PageNumber)).Distinct().Count(),
                PreparedByAi: true),
            SplitContentParts(prepared.Text),
            contextLength,
            Math.Min(1_200_000, remainingTokens * 3),
            prepared.Evidence,
            CacheHit);
    }

    private static PreparedContext BuildPreparedContext(
        string preparedText,
        IReadOnlyList<DocumentContextHit> evidence,
        int documentBudget)
    {
        var maximumCharacters = Math.Max(12_000, documentBudget * 3);
        var header = "[GO_DOCUMENT_CORPUS_PREPARED]\nPromptbezogenes Evidenzdossier:\n";
        var dossierLimit = CalculatePreparedDossierLimit(documentBudget);
        if (preparedText.Length > dossierLimit)
        {
            throw new InvalidDataException(
                "Das AI-Evidenzdossier überschreitet sein reserviertes Kontextbudget und wird nicht still gekürzt.");
        }
        var builder = new StringBuilder(header).AppendLine(preparedText).AppendLine("\n[ORIGINALBELEGE]");
        var selected = new List<DocumentContextHit>();
        foreach (var hit in evidence)
        {
            var block = $"\n--- Dokument: {hit.FileName} | Seite: {hit.PageNumber} ---\n{hit.Text}\n";
            if (builder.Length + block.Length + 32 > maximumCharacters)
            {
                break;
            }
            builder.Append(block);
            selected.Add(hit);
        }
        if (selected.Count == 0)
        {
            throw new InvalidOperationException("Das aufbereitete Evidenzdossier enthält keinen zitierfähigen Originalbeleg.");
        }
        builder.AppendLine("[ENDE_GO_DOCUMENT_CORPUS]");
        return new(builder.ToString(), selected);
    }

    internal static int CalculatePreparedDossierLimit(int documentBudget) =>
        Math.Max(8_000, Math.Max(12_000, documentBudget * 3) / 3);

    private static string BuildPageBlocks(IReadOnlyList<DocumentPage> pages)
    {
        var builder = new StringBuilder("[GO_DOCUMENT_CORPUS_FULL]\nAlle gebundenen Dokumentseiten folgen vollständig.\n");
        foreach (var page in pages)
        {
            builder.Append("\n--- Dokument: ")
                .Append(page.FileName ?? page.DocumentId.ToString("D"))
                .Append(" | Seite: ")
                .Append(page.PageNumber)
                .AppendLine(" ---")
                .AppendLine(page.Text);
        }
        builder.AppendLine("[ENDE_GO_DOCUMENT_CORPUS]");
        return builder.ToString();
    }

    internal static string BuildEvidenceBlocks(IReadOnlyList<DocumentContextHit> evidence, int maximumCharacters)
    {
        const string header = "[GO_DOCUMENT_EVIDENCE_CANDIDATES]\n";
        const string footer = "[ENDE_GO_DOCUMENT_EVIDENCE_CANDIDATES]\n";
        if (maximumCharacters < header.Length + footer.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCharacters),
                maximumCharacters,
                "Das Evidenzbudget ist zu klein für einen gültigen Dokumentblock.");
        }

        var builder = new StringBuilder(header);
        foreach (var hit in evidence)
        {
            var block = $"\n--- Dokument: {hit.FileName} | Seite: {hit.PageNumber} | Relevanz: {hit.Score:0.###} ---\n{hit.Text}\n";
            if (builder.Length + block.Length + footer.Length > maximumCharacters)
            {
                break;
            }
            builder.Append(block);
        }
        builder.Append(footer);
        return builder.ToString();
    }

    private static List<ContentPart> SplitContentParts(string text)
    {
        var result = new List<ContentPart>();
        for (var offset = 0; offset < text.Length;)
        {
            var length = Math.Min(MaximumContentPartCharacters, text.Length - offset);
            if (offset + length < text.Length && char.IsHighSurrogate(text[offset + length - 1]))
            {
                length--;
            }
            result.Add(new ContentPart("document", Text: text.Substring(offset, length)));
            offset += length;
        }
        return result;
    }

    private static IEnumerable<IReadOnlyList<DocumentIndexChunk>> BatchChunks(IReadOnlyList<DocumentIndexChunk> chunks)
    {
        var batch = new List<DocumentIndexChunk>(64);
        var characters = 0;
        foreach (var chunk in chunks)
        {
            if (batch.Count > 0
                && (batch.Count == 64 || characters + chunk.Text.Length > MaximumEmbeddingBatchCharacters))
            {
                yield return batch.ToArray();
                batch.Clear();
                characters = 0;
            }
            batch.Add(chunk);
            characters += chunk.Text.Length;
        }
        if (batch.Count > 0)
        {
            yield return batch.ToArray();
        }
    }

    private static string CreateCorpusRevision(IReadOnlyList<StoredDocument> source) => Hash(string.Join(
        '\n',
        source.Select(static item => $"{item.Id:D}|{item.Sha256}|{item.FileName}|{item.PageCount}")));

    private static string NormalizePrompt(string prompt) => Regex.Replace(
        prompt.Trim().ToLowerInvariant(),
        @"\s+",
        " ",
        RegexOptions.CultureInvariant);

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static int EstimateTokens(string value) => Math.Max(1, (value.Length + 2) / 3);

    internal static int CalculatePreparationCandidateCharacters(int contextLength, string prompt)
    {
        const int outputTokens = 8_192;
        const int policyReserveTokens = 6_144;
        var safetyTokens = Math.Min(8_192, Math.Max(2_048, contextLength / 16));
        var promptAndInstructionTokens = EstimateTokens(prompt) + 1_024;
        var availableTokens = Math.Max(
            16_384,
            contextLength - outputTokens - policyReserveTokens - safetyTokens - promptAndInstructionTokens);
        return Math.Min(MaximumCandidateCharacters, availableTokens * 3);
    }

    private sealed record PreparedContext(string Text, IReadOnlyList<DocumentContextHit> Evidence);
}
