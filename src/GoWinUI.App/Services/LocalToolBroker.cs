using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GoAi.Contracts;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using GoWinUI.Core.Coding;

namespace GoWinUI.App.Services;

/// <summary>
/// Executes session document and Coding tools
/// bound to the active run's selected project. Valid tools execute automatically
/// under the user's standing authorization, including local processes and CAD mutations.
/// </summary>
public sealed class LocalToolBroker(
    GoAiConnectionService connection,
    IDocumentIngestor documents,
    LocalDocumentToolService documentTools,
    IChatRepository chats)
{
    private const int MaximumResultCharacters = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = GoAiProtocol.CreateJsonOptions();
    private static readonly SearchValues<char> EvidenceIdCharacters = SearchValues.Create("0123456789abcdef");

    public async Task<ClientToolResult> ExecuteAsync(
        ToolProposal proposal,
        Guid sessionId,
        Guid? assistantMessageId,
        string? codingWorkspacePath = null,
        Func<CodingCommandProgress, Task>? commandProgress = null,
        CodingRunEvidenceStore? evidenceStore = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateProposal(proposal);
            if (WorkspaceTools.IsLocal(proposal.Name))
            {
                var workspaceResult = await new WorkspaceToolService(connection).ExecuteAsync(proposal, codingWorkspacePath, commandProgress, cancellationToken).ConfigureAwait(false);
                var workspaceJson = JsonSerializer.SerializeToElement(workspaceResult, JsonOptions);
                var failed = workspaceJson.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False;
                return Result(proposal, failed ? "failed" : "completed", workspaceResult,
                    failed ? "client.workspace_tool_failed" : null, failed ? "Workspace-Werkzeug fehlgeschlagen; Belege beachten." : null);
            }
            var coding = proposal.Name.StartsWith("coding.", StringComparison.Ordinal);
            if (coding && string.IsNullOrWhiteSpace(codingWorkspacePath))
            {
                throw new InvalidOperationException("Dieser Lauf hat keinen ausgewählten Coding-Projektordner.");
            }

            if (coding)
            {
                if (evidenceStore is not null && (evidenceStore.SessionId != sessionId || evidenceStore.RootRunId != proposal.RunId))
                    throw new UnauthorizedAccessException("Der Werkzeugbelegspeicher gehört nicht zu dieser Sitzung und diesem Lauf.");
                if (proposal.Name == "coding.readOutput")
                {
                    var store = evidenceStore ?? throw new InvalidOperationException("Für diesen Lauf ist kein Werkzeugbelegspeicher verfügbar.");
                    var args = proposal.Arguments;
                    return Result(proposal, "completed", await store.ReadOutputAsync(args.GetProperty("evidenceId").GetString()!,
                        args.TryGetProperty("stream", out var stream) ? stream.GetString()! : "stdout",
                        args.TryGetProperty("offset", out var offset) ? offset.GetInt32() : 0,
                        args.TryGetProperty("maximumCharacters", out var characters) ? characters.GetInt32() : 16000, cancellationToken).ConfigureAwait(false));
                }
                if (proposal.Name == "coding.searchRunEvidence")
                {
                    var store = evidenceStore ?? throw new InvalidOperationException("Für diesen Lauf ist kein Werkzeugbelegspeicher verfügbar.");
                    return Result(proposal, "completed", await store.SearchRunEvidenceAsync(proposal.Arguments.GetProperty("query").GetString()!,
                        proposal.Arguments.TryGetProperty("maximumResults", out var maximum) ? maximum.GetInt32() : 8, cancellationToken).ConfigureAwait(false));
                }
                if (proposal.Name == "coding.searchHistory")
                    return Result(proposal, "completed", await CodingSessionTools.SearchHistoryAsync(chats, sessionId, assistantMessageId,
                        proposal.Arguments.GetProperty("query").GetString()!, CodingResultLimit(proposal.Arguments), cancellationToken).ConfigureAwait(false));
                if (proposal.Name == "coding.searchKnowledge")
                {
                    var query = proposal.Arguments.GetProperty("query").GetString()!;
                    var knowledge = await SearchDocumentsAsync(sessionId, JsonSerializer.SerializeToElement(new { query, maximumCharacters = 7_000 }),
                        cancellationToken).ConfigureAwait(false);
                    return Result(proposal, "completed", CodingSessionTools.BoundKnowledgeResult(
                        JsonSerializer.SerializeToElement(knowledge, JsonOptions), query, CodingResultLimit(proposal.Arguments)));
                }
                if (proposal.Name == "coding.renderHtml")
                    return Result(proposal, "completed", CodingSessionTools.RenderReceipt(proposal.Arguments));
                using var evidence = evidenceStore?.BeginStep(proposal.ProposalId, proposal.Name, proposal.Arguments);
                var codingResult = await new LocalCodingToolExecutor(codingWorkspacePath!, commandProgress, evidence)
                    .ExecuteAsync(proposal.Name, proposal.Arguments, cancellationToken).ConfigureAwait(false);
                if (evidence is not null)
                {
                    var enriched = JsonNode.Parse(codingResult.GetRawText())!.AsObject();
                    enriched["evidence"] = JsonSerializer.SerializeToNode(evidence.Reference, JsonOptions);
                    codingResult = JsonSerializer.SerializeToElement(enriched, JsonOptions);
                }
                if (codingResult.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
                {
                    return Result(proposal, "failed", codingResult, "client.coding_tool_failed",
                        "Das Coding-Werkzeug meldete einen Fehler. Details stehen im Werkzeugergebnis.");
                }
                return Result(proposal, "completed", codingResult);
            }
            var payload = proposal.Name switch
            {
                ClientToolNames.DocumentRead => await documentTools.ReadAsync(
                    proposal.Arguments,
                    sessionId,
                    cancellationToken).ConfigureAwait(false),
                ClientToolNames.DocumentCreate => await documentTools.CreateAsync(
                    proposal.Arguments,
                    sessionId,
                    assistantMessageId ?? throw new InvalidOperationException(
                        "Die AI-Nachricht für das Dokumentartefakt fehlt."),
                    cancellationToken).ConfigureAwait(false),
                ClientToolNames.DocumentsList => await ListDocumentsAsync(sessionId, cancellationToken).ConfigureAwait(false),
                ClientToolNames.DocumentsSearch => await SearchDocumentsAsync(
                    sessionId,
                    proposal.Arguments,
                    cancellationToken).ConfigureAwait(false),
                ClientToolNames.DocumentsReadPages => await ReadDocumentPagesAsync(
                    sessionId,
                    proposal.Arguments,
                    cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException(
                    $"Das Clientwerkzeug '{proposal.Name}' ist in GO nicht verfügbar."),
            };
            return Result(proposal, "completed", Bounded(payload));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FileNotFoundException exception) when (proposal.Name == ClientToolNames.CodingReadOutput)
        {
            return Result(proposal, "failed", new
            {
                evidenceId = proposal.Arguments.GetProperty("evidenceId").GetString(),
                available = false,
                retryable = false,
            }, "client.evidence_unavailable", exception.Message);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Result(
                proposal,
                "failed",
                new { failed = true },
                "client.tool_failed",
                exception.Message);
        }
    }

    private static int CodingResultLimit(JsonElement arguments) => arguments.TryGetProperty("maximumResults", out var maximum) ? maximum.GetInt32() : 5;

    internal static void ValidateProposal(ToolProposal proposal, DateTimeOffset? currentTime = null)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ValidateIdentifier(proposal.ProposalId, "proposalId");
        ValidateIdentifier(proposal.RunId, "runId");
        if (string.IsNullOrWhiteSpace(proposal.Summary)
            || proposal.Summary.Length > 1_000
            || proposal.Summary.Any(character => char.IsControl(character)
                && character is not '\r' and not '\n' and not '\t'))
        {
            throw new InvalidDataException("Die Zusammenfassung des Client-Toolvorschlags ist ungültig.");
        }
        if (proposal.ExpiresAt <= (currentTime ?? DateTimeOffset.UtcNow))
        {
            throw new InvalidDataException("Der Client-Toolvorschlag ist abgelaufen.");
        }
        if (proposal.Arguments.ValueKind != JsonValueKind.Object
            || proposal.Arguments.GetRawText().Length > MaximumResultCharacters)
        {
            throw new InvalidDataException("Die Client-Toolargumente sind ungültig oder zu groß.");
        }

        var expectedRisk = proposal.Name switch
        {
            "coding.list" or "coding.search" or "coding.read" or "coding.gitDiff"
                or "coding.searchHistory" or "coding.searchKnowledge" or "coding.renderHtml"
                or "coding.readOutput" or "coding.searchRunEvidence" => ToolRiskClass.ReadOnly,
            "coding.write" or "coding.edit" or "coding.undo" => ToolRiskClass.LocalMutation,
            "coding.command" or WorkspaceTools.Open => ToolRiskClass.Process,
            WorkspaceTools.ImageInput => ToolRiskClass.ReadOnly,
            ClientToolNames.DocumentRead or ClientToolNames.DocumentsList
                or ClientToolNames.DocumentsSearch or ClientToolNames.DocumentsReadPages => ToolRiskClass.ReadOnly,
            ClientToolNames.DocumentCreate => ToolRiskClass.LocalMutation,
            _ => throw new InvalidDataException($"Das Clientwerkzeug '{proposal.Name}' ist nicht freigegeben."),
        };
        if (proposal.RiskClass != expectedRisk)
        {
            throw new InvalidDataException(
                "Die Risikoklasse des Client-Toolvorschlags stimmt nicht mit dem lokalen Vertrag überein.");
        }

        var arguments = proposal.Arguments;
        if (WorkspaceTools.IsLocal(proposal.Name)) { WorkspaceTools.Validate(proposal.Name, arguments); return; }
        switch (proposal.Name)
        {
            case "coding.readOutput":
                ValidateProperties(arguments, ["evidenceId"], ["evidenceId", "stream", "offset", "maximumCharacters"]);
                var evidenceId = ValidateString(arguments, "evidenceId", 35, 35);
                if (!evidenceId.StartsWith("ev-", StringComparison.Ordinal) || evidenceId.AsSpan(3).ContainsAnyExcept(EvidenceIdCharacters))
                    throw new InvalidDataException("Die Belegkennung ist ungültig.");
                if (arguments.TryGetProperty("stream", out _) && ValidateString(arguments, "stream", 1, 6) is not ("stdout" or "stderr" or "input" or "result"))
                    throw new InvalidDataException("Der Belegkanal ist ungültig.");
                ValidateOptionalInteger(arguments, "offset", 0, int.MaxValue);
                ValidateOptionalInteger(arguments, "maximumCharacters", 1, 32000);
                break;
            case "coding.searchRunEvidence":
                ValidateProperties(arguments, ["query"], ["query", "maximumResults"]);
                ValidateString(arguments, "query", 1, 512);
                ValidateOptionalInteger(arguments, "maximumResults", 1, 20);
                break;
            case "coding.searchHistory":
            case "coding.searchKnowledge":
                ValidateProperties(arguments, ["query"], ["query", "maximumResults"]);
                ValidateString(arguments, "query", 1, 512);
                ValidateOptionalInteger(arguments, "maximumResults", 1, 8);
                break;
            case "coding.renderHtml":
                ValidateProperties(arguments, ["code"], ["code", "title"]);
                ValidateString(arguments, "code", 1, 16_000);
                ValidateOptionalString(arguments, "title", 1, 100);
                break;
            case ClientToolNames.DocumentRead:
                ValidateProperties(
                    arguments,
                    ["scope", "mode"],
                    ["scope", "mode", "reference", "query", "startUnit", "characterOffset", "maximumUnits", "maximumCharacters"]);
                if (ValidateString(arguments, "scope", 1, 16) != "session")
                {
                    throw new InvalidDataException("document.read ist nur für Sitzungsdokumente freigegeben.");
                }
                var readMode = ValidateString(arguments, "mode", 1, 16);
                if (readMode is not ("list" or "outline" or "read" or "search"))
                {
                    throw new InvalidDataException("Die document.read-Auswahl ist ungültig.");
                }
                ValidateOptionalString(arguments, "reference", 1, 1_024);
                ValidateOptionalString(arguments, "query", 1, 2_000);
                ValidateOptionalInteger(arguments, "startUnit", 1, 1_000_000);
                ValidateOptionalInteger(arguments, "characterOffset", 0, MaximumResultCharacters);
                ValidateOptionalInteger(arguments, "maximumUnits", 1, 30);
                ValidateOptionalInteger(arguments, "maximumCharacters", 1_000, 40_000);
                if (readMode != "list" && !arguments.TryGetProperty("reference", out _))
                {
                    throw new InvalidDataException("document.read benötigt außerhalb des list-Modus eine reference.");
                }
                if (readMode == "search" && !arguments.TryGetProperty("query", out _))
                {
                    throw new InvalidDataException("document.read search benötigt query.");
                }
                break;
            case ClientToolNames.DocumentCreate:
                ValidateProperties(
                    arguments,
                    ["operation", "reference", "format", "sectionId", "content"],
                    ["operation", "reference", "format", "sectionId", "heading", "content", "expectedSha256"]);
                var operation = ValidateString(arguments, "operation", 1, 32);
                if (operation is not ("create" or "appendSection" or "replaceSection"))
                {
                    throw new InvalidDataException("Die document.create-Operation ist ungültig.");
                }
                ValidateString(arguments, "reference", 1, 1_024);
                var format = ValidateString(arguments, "format", 1, 16);
                if (format is not ("markdown" or "text" or "docx" or "pdf"))
                {
                    throw new InvalidDataException("Das document.create-Format ist ungültig.");
                }
                ValidateString(arguments, "sectionId", 1, 128);
                ValidateOptionalString(arguments, "heading", 1, 500);
                ValidateString(arguments, "content", 0, 120_000);
                ValidateOptionalString(arguments, "expectedSha256", 64, 64);
                if (operation != "create" && !arguments.TryGetProperty("expectedSha256", out _))
                {
                    throw new InvalidDataException("document.create-Bearbeitungen benötigen expectedSha256.");
                }
                break;
            case ClientToolNames.DocumentsList:
                ValidateProperties(arguments, [], []);
                break;
            case ClientToolNames.DocumentsSearch:
                ValidateProperties(arguments, ["query"], ["query", "maximumCharacters"]);
                ValidateString(arguments, "query", 1, 20_000);
                ValidateOptionalInteger(arguments, "maximumCharacters", 1_000, 200_000);
                break;
            case ClientToolNames.DocumentsReadPages:
                ValidateProperties(
                    arguments,
                    ["documentId", "startPage", "endPage"],
                    ["documentId", "startPage", "endPage"]);
                _ = Guid.Parse(ValidateString(arguments, "documentId", 36, 36));
                ValidateInteger(arguments, "startPage", 1, 1_000_000);
                ValidateInteger(arguments, "endPage", 1, 1_000_000);
                break;
        }
    }

    private async Task<object> ListDocumentsAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var items = await documents.ListAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return new
        {
            documents = items.Where(static item => item.PreparationStatus == DocumentPreparationStatus.Ready)
                .Select(static item => new { documentId = item.Id, item.FileName, item.PageCount, item.Sha256 }),
        };
    }

    private async Task<object> SearchDocumentsAsync(
        Guid sessionId,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var query = arguments.GetProperty("query").GetString()!;
        var maximum = arguments.TryGetProperty("maximumCharacters", out var value) ? value.GetInt32() : 120_000;
        IReadOnlyList<DocumentContextHit> hits;
        var searchMode = "fulltext";
        try
        {
            using var client = await connection.CreateClientAsync(cancellationToken).ConfigureAwait(false);
            var modelStatus = await client.GetModelStatusAsync(cancellationToken).ConfigureAwait(false);
            var embeddingModel = modelStatus.Models.FirstOrDefault(static item => item.Downloaded && item.Role == "embedding");
            var indexed = embeddingModel is null
                ? []
                : await documents.ListIndexChunksAsync(sessionId, embeddingModel.Id, cancellationToken).ConfigureAwait(false);
            if (embeddingModel is not null
                && indexed.Count > 0
                && indexed.All(static item => item.Embedding is not null))
            {
                var response = await client.CreateEmbeddingsAsync(
                    new EmbeddingBatchRequest([new EmbeddingInput("query", query)]),
                    cancellationToken).ConfigureAwait(false);
                var vector = response.Vectors.Single(static item => item.Id == "query").Values;
                hits = await documents.SearchHybridAsync(
                    sessionId,
                    query,
                    embeddingModel.Id,
                    vector,
                    maximum,
                    cancellationToken).ConfigureAwait(false);
                searchMode = "hybrid";
            }
            else
            {
                hits = await documents.SearchAsync(sessionId, query, maximum, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            hits = await documents.SearchAsync(sessionId, query, maximum, cancellationToken).ConfigureAwait(false);
            searchMode = $"fulltext (semantisch nicht verfügbar: {exception.GetType().Name})";
        }
        return new
        {
            searchMode,
            evidence = hits.Select(static hit => new
            {
                hit.DocumentId,
                hit.FileName,
                hit.PageNumber,
                hit.Score,
                hit.Text,
                citation = $"[{hit.FileName}, S. {hit.PageNumber}]",
            }),
        };
    }

    private async Task<object> ReadDocumentPagesAsync(
        Guid sessionId,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var documentId = Guid.Parse(arguments.GetProperty("documentId").GetString()!);
        var start = arguments.GetProperty("startPage").GetInt32();
        var end = arguments.GetProperty("endPage").GetInt32();
        if (end < start || end - start > 100)
        {
            throw new InvalidDataException("Der angeforderte Seitenbereich ist ungültig oder zu groß.");
        }
        var document = (await documents.ListAsync(sessionId, cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(item => item.Id == documentId && item.PreparationStatus == DocumentPreparationStatus.Ready)
            ?? throw new FileNotFoundException("Das Dokument ist in dieser Sitzung nicht fertig aufbereitet.");
        var pages = (await documents.ReadPagesAsync(documentId, cancellationToken).ConfigureAwait(false))
            .Where(page => page.PageNumber >= start && page.PageNumber <= end)
            .Select(page => new
            {
                documentId,
                document.FileName,
                page.PageNumber,
                page.Text,
                citation = $"[{document.FileName}, S. {page.PageNumber}]",
            })
            .ToArray();
        return new { pages };
    }

    private static void ValidateIdentifier(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 256
            || value.Any(character => !(char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.' or ':')))
        {
            throw new InvalidDataException($"'{name}' ist ungültig.");
        }
    }

    private static void ValidateProperties(
        JsonElement arguments,
        IReadOnlyCollection<string> required,
        IReadOnlyCollection<string> allowed)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            if (!seen.Add(property.Name) || !allowed.Contains(property.Name))
            {
                throw new InvalidDataException($"Das Werkzeugargument '{property.Name}' ist nicht freigegeben.");
            }
        }
        foreach (var property in required)
        {
            if (!seen.Contains(property))
            {
                throw new InvalidDataException($"Das Werkzeugargument '{property}' fehlt.");
            }
        }
    }

    private static string ValidateString(
        JsonElement arguments,
        string name,
        int minimumLength,
        int maximumLength)
    {
        if (!arguments.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"Das Werkzeugargument '{name}' fehlt oder ist kein Text.");
        }
        var value = NormalizeUnicodeScalarText(property.GetString() ?? string.Empty);
        if (value.Length < minimumLength || value.Length > maximumLength)
        {
            throw new InvalidDataException($"Das Werkzeugargument '{name}' hat eine ungültige Länge.");
        }
        return value;
    }

    private static void ValidateOptionalString(
        JsonElement arguments,
        string name,
        int minimumLength,
        int maximumLength)
    {
        if (arguments.TryGetProperty(name, out _))
        {
            _ = ValidateString(arguments, name, minimumLength, maximumLength);
        }
    }

    private static int ValidateInteger(JsonElement arguments, string name, int minimum, int maximum)
    {
        if (!arguments.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out var value)
            || value < minimum
            || value > maximum)
        {
            throw new InvalidDataException($"Das Werkzeugargument '{name}' ist keine gültige Ganzzahl.");
        }
        return value;
    }

    private static void ValidateOptionalInteger(
        JsonElement arguments,
        string name,
        int minimum,
        int maximum)
    {
        if (arguments.TryGetProperty(name, out _))
        {
            _ = ValidateInteger(arguments, name, minimum, maximum);
        }
    }

    private static string RequiredString(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"Das Werkzeugargument '{name}' fehlt.");
        }
        var result = NormalizeUnicodeScalarText(property.GetString() ?? string.Empty);
        if (string.IsNullOrWhiteSpace(result))
        {
            throw new InvalidDataException($"Das Werkzeugargument '{name}' ist leer.");
        }
        return result;
    }

    internal static string NormalizeUnicodeScalarText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        StringBuilder? builder = null;
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (char.IsHighSurrogate(current)
                && index + 1 < value.Length
                && char.IsLowSurrogate(value[index + 1]))
            {
                if (builder is not null)
                {
                    builder.Append(current);
                    builder.Append(value[++index]);
                }
                else
                {
                    index++;
                }
                continue;
            }
            if (!char.IsSurrogate(current))
            {
                builder?.Append(current);
                continue;
            }

            builder ??= new StringBuilder(value.Length).Append(value, 0, index);
        }
        return builder?.ToString() ?? value;
    }

    private static object Bounded(object value)
    {
        if (JsonSerializer.Serialize(value, JsonOptions).Length > MaximumResultCharacters)
        {
            throw new InvalidOperationException("Das lokale Toolergebnis überschreitet das Größenlimit.");
        }
        return value;
    }

    private static ClientToolResult Result(
        ToolProposal proposal,
        string status,
        object value,
        string? errorCode = null,
        string? message = null) =>
        new(
            proposal.ProposalId,
            status,
            JsonSerializer.SerializeToElement(value, JsonOptions),
            errorCode,
            message);
}
