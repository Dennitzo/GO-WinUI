using GoAi.Contracts;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Workers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GoAi.Server.Core.Runs;

/// <summary>
/// Runs one visible coding request as a sequence of small, isolated model and
/// tool steps. Raw web pages, source files and process logs are only visible to
/// the step that consumes them; subsequent steps receive bounded briefs.
/// </summary>
internal sealed partial class CodingAgentV4Engine
{
    private const int CheckpointVersion = 8;
    private const int PublicAgentProtocolVersion = 4;
    private const int MaximumSessionBriefCharacters = 1_500;
    private const int MaximumLedgerCharacters = 3_000;
    private const int MaximumResearchBriefCharacters = 1_500;
    private const int MaximumResearchExcerptCharacters = 2_000;
    private const int MaximumFileExcerptCharacters = 8_000;
    private const int MaximumMutationCharacters = 8_000;
    private const int MaximumRecentResults = 6;
    private const int MaximumResearchSources = 3;
    private const int MaximumResearchPhrases = 3;
    private const int MaximumInspectionSteps = 8;
    private const int MaximumMutationAttempts = 4;
    private static readonly string[] CargoCheckArguments = ["check"];
    private static readonly string[] GoTestArguments = ["test", "./..."];
    private static readonly JsonSerializerOptions JsonOptions = GoAiProtocol.CreateJsonOptions();
    private readonly RunRepository repository;
    private readonly GpuLeaseScheduler scheduler;
    private readonly ModelRuntimeClient modelRuntime;
    private readonly WorkerOrchestrator workers;
    private readonly AgentToolCatalog toolCatalog;
    private readonly AgentToolExecutor toolExecutor;

    public CodingAgentV4Engine(
        RunRepository repository,
        GpuLeaseScheduler scheduler,
        ModelRuntimeClient modelRuntime,
        WorkerOrchestrator workers,
        AgentToolCatalog toolCatalog,
        AgentToolExecutor toolExecutor)
    {
        this.repository = repository;
        this.scheduler = scheduler;
        this.modelRuntime = modelRuntime;
        this.workers = workers;
        this.toolCatalog = toolCatalog;
        this.toolExecutor = toolExecutor;
    }

    public async Task ProcessAsync(
        string runId,
        RunRequest request,
        ModelSelection selection,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(selection);
        var workspace = request.Workspace
            ?? throw new InvalidOperationException("Für einen Coding-Lauf ist ein gebundener Workspace erforderlich.");
        var availableTools = toolCatalog.GetAvailableTools(request);
        var contextLength = Math.Min(
            selection.ContextLength,
            request.Limits?.MaximumContextTokens ?? selection.ContextLength);
        var goal = GetLatestUserText(request);
        var requiredPaths = CodingAgentOrchestrator.ExtractRequiredOutputPaths(goal);
        var checkpoint = await repository.GetCheckpointAsync(runId, cancellationToken).ConfigureAwait(false);
        if (checkpoint is not null
            && (checkpoint.AgentProtocolVersion != CheckpointVersion || checkpoint.V4State is null))
        {
            await repository.DeleteCheckpointAsync(runId, cancellationToken).ConfigureAwait(false);
            checkpoint = null;
        }

        if (checkpoint is null)
        {
            var sessionHistory = BuildSessionHistorySource(request);
            var initialBrief = sessionHistory.Length <= MaximumSessionBriefCharacters
                ? sessionHistory
                : string.Empty;
            var mutationRequested = requiredPaths.Count > 0
                || RunProcessor.ClassifyCodingRequest(request) == CodingRequestIntent.Mutation;
            var directUrls = ExtractPublicUrls(goal);
            var researchRequested = directUrls.Length > 0 || RequestsResearch(goal);
            var initialStep = NewStep(1, CodingStepKind.Capture, "Auftrag und begrenzten Arbeitsstand erfassen");
            var initialState = new CodingAgentV4State(
                goal,
                initialBrief,
                initialStep,
                requiredPaths,
                [],
                [],
                [],
                mutationRequested,
                researchRequested,
                researchRequested,
                directUrls,
                [],
                []);
            checkpoint = new AgentRunCheckpoint(
                Messages: [],
                RoundCount: 0,
                ToolCallCount: 0,
                InputTokens: 0,
                OutputTokens: 0,
                AgentProtocolVersion: CheckpointVersion,
                V4State: initialState);
            await InitializeRunAsync(runId, selection, initialState, contextLength, cancellationToken).ConfigureAwait(false);
            await repository.SaveCheckpointAsync(runId, checkpoint, cancellationToken).ConfigureAwait(false);
        }

        var state = checkpoint.V4State!;
        var roundCount = checkpoint.RoundCount;
        var toolCallCount = checkpoint.ToolCallCount;
        var inputTokens = checkpoint.InputTokens;
        var outputTokens = checkpoint.OutputTokens;
        var activeCalls = checkpoint.ActiveToolCalls?.ToArray();
        var pendingProposalId = checkpoint.PendingProposalId;
        var pendingToolCallId = checkpoint.PendingToolCallId;
        string? ephemeralEvidence = null;

        if (!string.IsNullOrWhiteSpace(pendingProposalId))
        {
            var clientResult = await repository.GetClientToolResultAsync(
                pendingProposalId,
                cancellationToken).ConfigureAwait(false);
            if (clientResult is null)
            {
                await repository.UpdateStateAsync(
                    runId,
                    RunState.WaitingForClient,
                    selection.ModelId,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                throw new RunWaitingForClientException();
            }
            if (activeCalls is not { Length: 1 } || string.IsNullOrWhiteSpace(pendingToolCallId))
            {
                throw new InvalidDataException("Der gespeicherte V4-Clientschritt ist unvollständig.");
            }

            var completedCall = activeCalls[0];
            await repository.AppendEventAsync(
                runId,
                RunEventTypes.AgentObservationCommitted,
                new AgentObservationCommittedEvent(
                    completedCall.Id,
                    IsSuccessful(clientResult),
                    clientResult.ErrorCode,
                    clientResult.Message),
                cancellationToken).ConfigureAwait(false);
            await repository.DeleteClientToolExchangeAsync(
                runId,
                pendingProposalId,
                cancellationToken).ConfigureAwait(false);
            pendingProposalId = null;
            pendingToolCallId = null;
            activeCalls = null;
            await repository.UpdateStateAsync(
                runId,
                RunState.Running,
                selection.ModelId,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await HandleClientResultAsync(completedCall, clientResult).ConfigureAwait(false);
            await SaveCheckpointAsync().ConfigureAwait(false);
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (state.Step.Kind)
            {
                case CodingStepKind.Capture:
                    await CaptureAsync().ConfigureAwait(false);
                    break;
                case CodingStepKind.ResearchSearch:
                    await RunResearchSearchAsync().ConfigureAwait(false);
                    break;
                case CodingStepKind.ResearchFetch:
                    await RunResearchFetchAsync().ConfigureAwait(false);
                    break;
                case CodingStepKind.ResearchSummarize:
                    // Raw source windows are deliberately not checkpointed. A
                    // restart repeats the bounded read instead of persisting
                    // page content in the active context.
                    await TransitionAsync(
                        CodingStepKind.ResearchFetch,
                        "Begrenztes Trefferfenster nach Neustart erneut laden",
                        "Der flüchtige Webausschnitt wurde nicht persistiert.").ConfigureAwait(false);
                    break;
                case CodingStepKind.WorkspaceInspect:
                    await RunWorkspaceInspectionAsync().ConfigureAwait(false);
                    break;
                case CodingStepKind.Mutation:
                    await RunMutationAsync().ConfigureAwait(false);
                    break;
                case CodingStepKind.Verification:
                    await RunVerificationAsync().ConfigureAwait(false);
                    break;
                case CodingStepKind.Finish:
                    await FinishAsync().ConfigureAwait(false);
                    return;
                default:
                    throw new CodingAgentProtocolException($"Unbekannter Coding-Schritt: {state.Step.Kind}.");
            }
        }

        async Task CaptureAsync()
        {
            var source = BuildSessionHistorySource(request);
            if (state.SessionBrief.Length == 0 && source.Length > MaximumSessionBriefCharacters)
            {
                var summary = await InvokeVisibleTextAsync(
                    "Verdichte den vorherigen Sitzungsverlauf für genau diesen Coding-Auftrag. Bewahre nur Nutzerentscheidungen, relevante bestehende Ergebnisse und offene Einschränkungen. Keine Dateiinhalte erfinden. Maximal 1.500 Zeichen.",
                    Limit(source, 12_000),
                    1_024,
                    4_000,
                    allowHostFallback: true).ConfigureAwait(false);
                state = state with { SessionBrief = Limit(summary, MaximumSessionBriefCharacters) };
            }

            if (state.ResearchRequested)
            {
                await TransitionAsync(
                    state.ResearchSources.Count > 0 ? CodingStepKind.ResearchFetch : CodingStepKind.ResearchSearch,
                    state.ResearchSources.Count > 0
                        ? "Vorgegebene Quelle gezielt nach einer brauchbaren Phrase durchsuchen"
                        : "Höchstens fünf passende Webquellen ermitteln",
                    state.ResearchSources.Count > 0 ? "Direkte URL erkannt; Websuche wird übersprungen." : null).ConfigureAwait(false);
                return;
            }
            await TransitionAfterResearchAsync().ConfigureAwait(false);
        }

        async Task RunResearchSearchAsync()
        {
            var searchTool = ResolveTool("web.search", availableTools);
            var messages = BuildStepMessages(
                state,
                workspace,
                "Formuliere genau eine kurze Websuchanfrage in der Sprache des Nutzerauftrags. Liefere ausschließlich einen web.search-Aufruf. GO begrenzt das Ergebnis auf fünf Treffer.",
                currentData: null);
            var response = await InvokeRequiredToolAsync(
                messages,
                [CreateSearchDefinition(searchTool)],
                "web.search",
                512,
                2_000).ConfigureAwait(false);
            var call = NormalizeSearchCall(response.ToolCalls[0], goal);
            var validation = CodingAgentOrchestrator.ValidateModelToolCalls(toolCatalog, availableTools, [call]);
            if (validation is not null)
            {
                call = await RepairToolCallAsync(call, searchTool, validation, messages, 512).ConfigureAwait(false);
                call = NormalizeSearchCall(call, goal);
            }
            var result = await ExecuteServerToolAsync(searchTool, call).ConfigureAwait(false);
            var search = result.Result.Deserialize<WebSearchResponse>(JsonOptions)
                ?? throw new InvalidDataException("web.search lieferte keine lesbare Trefferliste.");
            var usable = search.Results
                .Where(static item => Uri.TryCreate(item.Url, UriKind.Absolute, out var uri)
                    && uri.Scheme is "http" or "https")
                .DistinctBy(static item => item.Url, StringComparer.OrdinalIgnoreCase)
                .Take(MaximumResearchSources)
                .ToArray();
            if (usable.Length == 0)
            {
                state = state with { BlockedReason = "Die ausdrücklich angeforderte Websuche lieferte keine aufrufbare Quelle." };
                await TransitionAsync(CodingStepKind.Finish, "Rechercheblocker sichtbar abschließen", state.BlockedReason).ConfigureAwait(false);
                return;
            }
            state = state with
            {
                ResearchSources = usable.Select(static item => item.Url).ToArray(),
                SearchSummary = Limit(string.Join('\n', usable.Select(static item =>
                    $"{item.Title} | {item.Url} | {item.Snippet}")), MaximumResearchBriefCharacters),
                RecentResults = AddRecent(state.RecentResults, $"Websuche: {usable.Length} brauchbare Quelle(n)."),
            };
            await TransitionAsync(
                CodingStepKind.ResearchFetch,
                "Erste brauchbare Quelle mit einer konkreten Phrase abrufen",
                $"{usable.Length} Quelle(n) stehen zur gezielten Prüfung bereit.").ConfigureAwait(false);
        }

        async Task RunResearchFetchAsync()
        {
            if (state.ResearchSources.Count == 0)
            {
                state = state with { BlockedReason = "Für die Recherche ist keine aufrufbare Quelle verfügbar." };
                await TransitionAsync(CodingStepKind.Finish, "Rechercheblocker sichtbar abschließen", state.BlockedReason).ConfigureAwait(false);
                return;
            }
            if (state.ResearchPhrases.Count == 0)
            {
                var phraseText = await InvokeVisibleTextAsync(
                    "Bestimme für den gezielten Quellenabruf höchstens drei konkrete, kurze Suchphrasen. Eine Phrase pro Zeile, keine Nummerierung, keine Erklärung. Verwende Fachbegriffe aus dem Nutzerziel und gegebenenfalls aus Seitentitel oder URL.",
                    Limit($"Nutzerziel:\n{goal}\n\nQuellenhinweise:\n{state.SearchSummary}\n{string.Join('\n', state.ResearchSources)}", 4_000),
                    512,
                    2_000,
                    allowHostFallback: true).ConfigureAwait(false);
                var phrases = ParseResearchPhrases(phraseText, goal, state.ResearchSources[0]);
                state = state with { ResearchPhrases = phrases };
                await SaveCheckpointAsync().ConfigureAwait(false);
            }

            var pair = NextResearchPair(state);
            if (pair is null)
            {
                if (state.ResearchExplicitlyRequired)
                {
                    state = state with { BlockedReason = "In höchstens drei Quellen und drei konkreten Phrasen wurde kein brauchbarer Inhaltsausschnitt gefunden." };
                    await TransitionAsync(CodingStepKind.Finish, "Rechercheblocker sichtbar abschließen", state.BlockedReason).ConfigureAwait(false);
                }
                else
                {
                    state = state with { ResearchRequested = false };
                    await TransitionAfterResearchAsync().ConfigureAwait(false);
                }
                return;
            }

            var fetchTool = ResolveTool("web.fetch", availableTools);
            var fetchMessages = BuildStepMessages(
                state,
                workspace,
                $"Rufe ausschließlich die festgelegte Quelle auf und suche genau eine konkrete Phrase. URL: {pair.Value.Url}. Bevorzugte Phrase: {pair.Value.Query}. Liefere nur web.fetch.",
                state.SearchSummary);
            var response = await InvokeRequiredToolAsync(
                fetchMessages,
                [CreateFetchDefinition(fetchTool)],
                "web.fetch",
                512,
                2_000).ConfigureAwait(false);
            var call = NormalizeFetchCall(response.ToolCalls[0], pair.Value.Url, pair.Value.Query);
            var validation = CodingAgentOrchestrator.ValidateModelToolCalls(toolCatalog, availableTools, [call]);
            if (validation is not null)
            {
                call = await RepairToolCallAsync(call, fetchTool, validation, fetchMessages, 512).ConfigureAwait(false);
                call = NormalizeFetchCall(call, pair.Value.Url, pair.Value.Query);
            }
            state = state with
            {
                AttemptedResearchKeys = state.AttemptedResearchKeys
                    .Append(ResearchKey(pair.Value.Url, pair.Value.Query))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
            };
            var result = await ExecuteServerToolAsync(fetchTool, call).ConfigureAwait(false);
            var targeted = result.Result.Deserialize<TargetedWebFetchResult>(JsonOptions)
                ?? throw new InvalidDataException("web.fetch lieferte kein lesbares Trefferfenster.");
            var excerpt = targeted.Matches
                .Select(static match => match.Text)
                .FirstOrDefault(IsUsableResearchExcerpt);
            if (excerpt is null)
            {
                state = state with
                {
                    RecentResults = AddRecent(
                        state.RecentResults,
                        $"Kein brauchbarer Treffer für '{pair.Value.Query}' in {pair.Value.Url}."),
                };
                await TransitionAsync(
                    CodingStepKind.ResearchFetch,
                    "Nächste Phrase oder Quelle gezielt prüfen",
                    "Der vorherige Treffer war leer oder bestand überwiegend aus Navigation.").ConfigureAwait(false);
                return;
            }

            excerpt = Limit(excerpt, MaximumResearchExcerptCharacters);
            var summarizeStep = NewStep(
                state.Step.Sequence + 1,
                CodingStepKind.ResearchSummarize,
                "Gefundenes Trefferfenster isoliert verdichten") with
            {
                Detail = $"Ein brauchbares Fenster mit {excerpt.Length:N0} Zeichen wurde gefunden; weitere Abrufe enden.",
            };
            await EmitStepAsync(
                summarizeStep,
                $"{state.Step.Kind} → {CodingStepKind.ResearchSummarize}").ConfigureAwait(false);
            var briefText = await InvokeVisibleTextAsync(
                "Verdichte ausschließlich das folgende Trefferfenster für die anschließende Coding-Aufgabe. Nenne überprüfbare Fakten, Formeln, Einheiten, Einschränkungen und die Quelle. Keine Ergänzungen aus Modellwissen. Maximal 1.500 Zeichen.",
                $"Nutzerziel:\n{goal}\n\nQuelle: {pair.Value.Url}\nPhrase: {pair.Value.Query}\n\nTrefferfenster:\n{excerpt}",
                1_024,
                4_000,
                allowHostFallback: false).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(briefText))
            {
                state = state with { BlockedReason = "Das ausgewählte Coding-Modell konnte den gefundenen Webbeleg nicht sichtbar verdichten." };
                await TransitionAsync(CodingStepKind.Finish, "Rechercheblocker sichtbar abschließen", state.BlockedReason).ConfigureAwait(false);
                return;
            }
            var brief = new ResearchBrief(
                pair.Value.Url,
                pair.Value.Query,
                Limit(briefText.Trim(), MaximumResearchBriefCharacters));
            state = state with
            {
                ResearchBrief = brief,
                ResearchRequested = false,
                SearchSummary = string.Empty,
                RecentResults = AddRecent(state.RecentResults, $"Recherchebrief aus {pair.Value.Url} erstellt."),
            };
            await AppendMilestoneAsync(
                $"Die Webrecherche wurde nach dem ersten brauchbaren Treffer abgeschlossen. Die Quelle {pair.Value.Url} wurde gezielt nach „{pair.Value.Query}“ ausgewertet; für die Umsetzung bleibt nur ein kompakter Recherchebrief im Kontext.").ConfigureAwait(false);
            await TransitionAfterResearchAsync().ConfigureAwait(false);
        }

        async Task RunWorkspaceInspectionAsync()
        {
            var target = NextMutationTarget(state);
            if (state.MutationRequested && target is not null && !WorkspaceLikelyContains(workspace, target)
                && !state.MutatedPaths.Contains(target, StringComparer.OrdinalIgnoreCase))
            {
                state = state with { CurrentFilePath = target, CurrentFileKnownToExist = false };
                await TransitionAsync(CodingStepKind.Mutation, $"Neue Datei {target} erstellen", "Der Zielpfad ist im aktuellen Workspace nicht vorhanden.").ConfigureAwait(false);
                return;
            }
            if (state.InspectionCount >= MaximumInspectionSteps)
            {
                state = state with { BlockedReason = "Der Workspace konnte nach acht kleinen, gezielten Untersuchungsschritten nicht ausreichend eingegrenzt werden." };
                await TransitionAsync(CodingStepKind.Finish, "Untersuchungsblocker sichtbar abschließen", state.BlockedReason).ConfigureAwait(false);
                return;
            }

            var inspectTools = new[]
            {
                ResolveTool(ClientToolNames.FileSystemList, availableTools),
                ResolveTool(ClientToolNames.FileSystemSearch, availableTools),
                ResolveTool(ClientToolNames.FileSystemReadText, availableTools),
            };
            var instruction = target is null
                ? "Untersuche den Workspace mit genau einem gezielten Aufruf. Nutze fs.list für einen Ordner, fs.search für eine konkrete Funktion oder Phrase und fs.readText nur für einen bereits bekannten begrenzten Bereich."
                : $"Untersuche ausschließlich den für den nächsten Schritt relevanten Zielpfad {target}. Suche zuerst nach einer konkreten Funktion oder Textphrase; lade danach nur den benötigten Block. Wiederhole keine Suche, die bereits found=false ergeben hat.";
            var messages = BuildStepMessages(state, workspace, instruction, state.LastObservation ?? state.LastError);
            var definitions = inspectTools.Select(CreateInspectionDefinition).ToArray();
            var response = state.MutationRequested
                ? await InvokeRequiredToolAsync(
                    messages,
                    definitions,
                    requiredToolName: null,
                    maximumOutputTokens: 1_024,
                    maximumInputTokens: 12_000).ConfigureAwait(false)
                : await InvokeModelAsync(
                    messages,
                    definitions,
                    1_024,
                    12_000,
                    requireToolCall: false,
                    requiredToolName: null).ConfigureAwait(false);
            if (response.ToolCalls.Count == 0)
            {
                if (!state.MutationRequested)
                {
                    state = state with { LastObservation = Limit(response.Content ?? string.Empty, MaximumLedgerCharacters) };
                    await TransitionAsync(CodingStepKind.Finish, "Analyseergebnis abschließen", null).ConfigureAwait(false);
                    return;
                }
                throw new CodingEmptyResponseException("Der Untersuchungsschritt lieferte keinen Workspace-Aufruf.");
            }
            var call = NormalizeInspectionCall(response.ToolCalls[0], target);
            var tool = ResolveTool(call.Name, availableTools);
            var validation = CodingAgentOrchestrator.ValidateModelToolCalls(toolCatalog, availableTools, [call]);
            if (validation is not null)
            {
                call = await RepairToolCallAsync(call, tool, validation, messages, 1_024).ConfigureAwait(false);
                call = NormalizeInspectionCall(call, target);
            }
            state = state with { InspectionCount = state.InspectionCount + 1 };
            await ProposeClientToolAsync(tool, call).ConfigureAwait(false);
        }

        async Task RunMutationAsync()
        {
            if (!state.MutationRequested)
            {
                await TransitionAsync(CodingStepKind.Finish, "Analyse abschließen", null).ConfigureAwait(false);
                return;
            }
            if (state.MutationCount >= MaximumMutationAttempts)
            {
                state = state with { BlockedReason = "Vier kleine Mutationsschritte konnten den Auftrag nicht in einen prüfbaren Stand überführen." };
                await TransitionAsync(CodingStepKind.Finish, "Mutationsblocker sichtbar abschließen", state.BlockedReason).ConfigureAwait(false);
                return;
            }
            var target = NextMutationTarget(state) ?? state.CurrentFilePath;
            var existing = target is not null
                && (state.CurrentFileKnownToExist
                    || WorkspaceLikelyContains(workspace, target)
                    || state.MutatedPaths.Contains(target, StringComparer.OrdinalIgnoreCase));
            if (existing && string.IsNullOrWhiteSpace(ephemeralEvidence))
            {
                await TransitionAsync(
                    CodingStepKind.WorkspaceInspect,
                    $"Aktuellen Zielblock von {target} unmittelbar vor der Änderung lesen",
                    "Bestehende Dateien werden ohne aktuellen Textblock nicht verändert.").ConfigureAwait(false);
                return;
            }

            var toolName = existing
                ? ClientToolNames.FileSystemReplaceText
                : ClientToolNames.FileSystemProposeCreate;
            var tool = ResolveTool(toolName, availableTools);
            var instruction = existing
                ? $"Ändere genau einen zusammenhängenden Block in {target}. oldText muss unverändert und eindeutig aus dem aktuellen Dateiausschnitt stammen; newText darf höchstens 8.000 Unicode-Zeichen enthalten. Liefere ausschließlich fs.replaceText."
                : $"Erstelle {(target is null ? "genau eine passende neue Datei" : target)} als syntaktisch vollständige Arbeitsversion mit höchstens 8.000 Unicode-Zeichen. Liefere ausschließlich fs.proposeCreate.";
            var currentData = existing
                ? $"Aktueller, unmittelbar gelesener Dateiausschnitt:\n{Limit(ephemeralEvidence!, MaximumFileExcerptCharacters)}"
                : state.ResearchBrief?.Summary;
            var messages = BuildStepMessages(state, workspace, instruction, currentData);
            var response = await InvokeRequiredToolAsync(
                messages,
                [CreateMutationDefinition(tool)],
                toolName,
                16_384,
                12_000).ConfigureAwait(false);
            var call = NormalizeMutationCall(response.ToolCalls[0], toolName, target, ephemeralEvidence);
            var validation = CodingAgentOrchestrator.ValidateModelToolCalls(toolCatalog, availableTools, [call]);
            if (validation is not null || !MutationWithinLimit(call))
            {
                var diagnostic = validation ?? "Der Änderungsinhalt überschreitet 8.000 Unicode-Zeichen.";
                call = await RepairToolCallAsync(call, tool, diagnostic, messages, 16_384).ConfigureAwait(false);
                call = NormalizeMutationCall(call, toolName, target, ephemeralEvidence);
                if (!MutationWithinLimit(call))
                {
                    throw new CodingAgentProtocolException("Das Coding-Modell hat auch im Reparaturturn mehr als 8.000 Änderungszeichen geliefert.");
                }
            }
            state = state with { MutationCount = state.MutationCount + 1 };
            ephemeralEvidence = null;
            await ProposeClientToolAsync(tool, call).ConfigureAwait(false);
        }

        async Task RunVerificationAsync()
        {
            var target = state.MutatedPaths
                .FirstOrDefault(path => !state.VerifiedPaths.Contains(path, StringComparer.OrdinalIgnoreCase));
            if (target is null)
            {
                await TransitionAsync(CodingStepKind.Finish, "Geprüften Arbeitsstand abschließen", null).ConfigureAwait(false);
                return;
            }
            var verification = CreateVerificationCall(target, workspace, availableTools, state.Step);
            if (verification is null)
            {
                state = state with
                {
                    VerifiedPaths = state.VerifiedPaths.Append(target).ToArray(),
                    RecentResults = AddRecent(state.RecentResults, $"Für {target} ist keine universelle automatische Prüfung verfügbar."),
                };
                await TransitionAsync(CodingStepKind.Finish, "Arbeitsstand ohne universellen Prüfbefehl abschließen", null).ConfigureAwait(false);
                return;
            }
            state = state with { VerificationCount = state.VerificationCount + 1 };
            await ProposeClientToolAsync(verification.Value.Tool, verification.Value.Call).ConfigureAwait(false);
        }

        async Task FinishAsync()
        {
            var missing = state.RequiredOutputPaths
                .Where(path => !state.MutatedPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            if (state.BlockedReason is null && missing.Length > 0 && state.MutationCount < MaximumMutationAttempts)
            {
                await TransitionAsync(CodingStepKind.Mutation, $"Fehlenden Ausgabepfad {missing[0]} erstellen", null).ConfigureAwait(false);
                return;
            }
            var instruction = state.BlockedReason is null
                ? "Formuliere eine kurze sichtbare Abschlussantwort: umgesetzte Änderung, betroffene Pfade und tatsächlich bestandene Prüfungen. Behaupte nichts, das im Arbeitsstand nicht belegt ist."
                : "Formuliere eine kurze sichtbare Blockerantwort mit dem konkreten belegten Grund und der verbleibenden Arbeit.";
            var final = await InvokeVisibleTextAsync(
                instruction,
                BuildLedger(state),
                1_024,
                4_000,
                allowHostFallback: true).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(final))
            {
                final = state.BlockedReason is not null
                    ? $"Der Coding-Lauf wurde kontrolliert beendet: {state.BlockedReason}"
                    : $"Der Coding-Auftrag wurde abgeschlossen. Geändert: {string.Join(", ", state.MutatedPaths)}. Geprüft: {string.Join(", ", state.VerifiedPaths)}.";
            }
            await AppendFinalAsync(final.Trim()).ConfigureAwait(false);
            if (state.BlockedReason is not null)
            {
                await repository.AppendEventAsync(
                    runId,
                    RunEventTypes.AgentPhaseChanged,
                    new AgentPhaseChangedEvent(
                        CodingAgentPhase.Blocked,
                        state.BlockedReason,
                        state.Step.Sequence),
                    cancellationToken).ConfigureAwait(false);
            }
            var title = RunProcessor.SanitizeTitle(string.Empty, goal);
            await repository.DeleteCheckpointAsync(runId, cancellationToken).ConfigureAwait(false);
            await repository.AppendEventAsync(
                runId,
                RunEventTypes.RunCompleted,
                new RunCompletedEvent(title, selection.ModelId, inputTokens, outputTokens),
                cancellationToken).ConfigureAwait(false);
            await repository.UpdateStateAsync(
                runId,
                RunState.Completed,
                selection.ModelId,
                title,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        async Task HandleClientResultAsync(LmToolCall call, ClientToolResult result)
        {
            var success = IsSuccessful(result);
            var compact = CompactClientResult(call.Name, result);
            if (!success)
            {
                state = state with
                {
                    LastError = Limit(compact, MaximumLedgerCharacters),
                    RecentResults = AddRecent(state.RecentResults, $"{call.Name} fehlgeschlagen: {result.ErrorCode ?? result.Message}"),
                };
                if (state.Step.Kind == CodingStepKind.Verification)
                {
                    await TransitionAsync(
                        CodingStepKind.WorkspaceInspect,
                        "Fehlerursache mit aktuellem Quelltext eingrenzen",
                        state.LastError).ConfigureAwait(false);
                }
                else if (state.Step.Kind == CodingStepKind.Mutation)
                {
                    var target = FindTarget(call.Arguments) ?? state.CurrentFilePath;
                    var rejectedCreateForMissingTarget = call.Name == ClientToolNames.FileSystemProposeCreate
                        && target is not null
                        && !WorkspaceLikelyContains(workspace, target);
                    await TransitionAsync(
                        rejectedCreateForMissingTarget ? CodingStepKind.Mutation : CodingStepKind.WorkspaceInspect,
                        rejectedCreateForMissingTarget
                            ? $"Abgewiesene neue Datei {target} anhand der konkreten Diagnose korrigieren"
                            : "Nach abgewiesener Änderung den aktuellen Zielblock neu lesen",
                        state.LastError).ConfigureAwait(false);
                }
                else
                {
                    await TransitionAsync(
                        CodingStepKind.WorkspaceInspect,
                        "Pfad oder Suchansatz anhand der konkreten Diagnose korrigieren",
                        state.LastError).ConfigureAwait(false);
                }
                return;
            }

            switch (state.Step.Kind)
            {
                case CodingStepKind.WorkspaceInspect:
                    state = state with
                    {
                        LastObservation = Limit(compact, MaximumLedgerCharacters),
                        LastError = null,
                        RecentResults = AddRecent(state.RecentResults, CompactResultLabel(call.Name, result.Result)),
                    };
                    if (call.Name == ClientToolNames.FileSystemReadText
                        && TryReadString(result.Result, "text") is { Length: > 0 } text)
                    {
                        ephemeralEvidence = Limit(text, MaximumFileExcerptCharacters);
                        state = state with
                        {
                            CurrentFilePath = TryReadString(result.Result, "path") ?? FindTarget(call.Arguments),
                            CurrentFileKnownToExist = true,
                        };
                        await TransitionAsync(
                            state.MutationRequested ? CodingStepKind.Mutation : CodingStepKind.Finish,
                            state.MutationRequested
                                ? "Gelesenen Zielblock in einem kleinen Schritt ändern"
                                : "Gelesenen Quelltext analysiert abschließen",
                            null).ConfigureAwait(false);
                        return;
                    }
                    if (call.Name == ClientToolNames.FileSystemSearch
                        && TryReadBoolean(result.Result, "found") == false)
                    {
                        state = state with { LastObservation = Limit(compact + "\nfound=false ist autoritativ; dieselbe Suche wird nicht wiederholt.", MaximumLedgerCharacters) };
                    }
                    await TransitionAsync(CodingStepKind.WorkspaceInspect, "Nächsten gezielten Workspace-Schritt ausführen", null).ConfigureAwait(false);
                    break;
                case CodingStepKind.Mutation:
                {
                    var path = FindTarget(call.Arguments) ?? state.CurrentFilePath;
                    var changed = state.MutatedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (path is not null)
                    {
                        changed.Add(path);
                    }
                    state = state with
                    {
                        MutatedPaths = changed.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                        CurrentFilePath = path,
                        CurrentFileKnownToExist = true,
                        LastError = null,
                        LastObservation = Limit(compact, MaximumLedgerCharacters),
                        RecentResults = AddRecent(
                            ClearRecoveredFailures(state.RecentResults),
                            $"Datei geändert: {path ?? "Workspace"}."),
                    };
                    await AppendMilestoneAsync(
                        path is null
                            ? "Eine zusammenhängende Workspace-Änderung wurde erfolgreich gespeichert."
                            : $"Die Datei `{path}` wurde in einem begrenzten, zusammenhängenden Schritt erfolgreich geändert.").ConfigureAwait(false);
                    await TransitionAsync(CodingStepKind.Verification, $"Kleinste passende Prüfung für {path ?? "die Änderung"} ausführen", null).ConfigureAwait(false);
                    break;
                }
                case CodingStepKind.Verification:
                {
                    var path = state.CurrentFilePath
                        ?? state.MutatedPaths.FirstOrDefault(candidate => !state.VerifiedPaths.Contains(candidate, StringComparer.OrdinalIgnoreCase));
                    var verified = state.VerifiedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (path is not null)
                    {
                        verified.Add(path);
                    }
                    state = state with
                    {
                        VerifiedPaths = verified.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                        LastError = null,
                        LastObservation = Limit(compact, MaximumLedgerCharacters),
                        RecentResults = AddRecent(
                            ClearRecoveredFailures(state.RecentResults),
                            $"Prüfung bestanden: {path ?? call.Name}."),
                    };
                    await AppendMilestoneAsync(
                        path is null
                            ? "Die kleinste passende Prüfung wurde erfolgreich abgeschlossen."
                            : $"Die Prüfung für `{path}` wurde erfolgreich abgeschlossen.").ConfigureAwait(false);
                    await TransitionAsync(CodingStepKind.Finish, "Geprüften Auftrag abschließen", null).ConfigureAwait(false);
                    break;
                }
                default:
                    state = state with { LastObservation = Limit(compact, MaximumLedgerCharacters) };
                    break;
            }
        }

        async Task TransitionAfterResearchAsync()
        {
            var target = NextMutationTarget(state);
            if (state.MutationRequested
                && target is not null
                && !WorkspaceLikelyContains(workspace, target)
                && !state.MutatedPaths.Contains(target, StringComparer.OrdinalIgnoreCase))
            {
                state = state with { CurrentFilePath = target, CurrentFileKnownToExist = false };
                await TransitionAsync(CodingStepKind.Mutation, $"Neue Datei {target} erstellen", null).ConfigureAwait(false);
                return;
            }
            await TransitionAsync(
                CodingStepKind.WorkspaceInspect,
                state.MutationRequested ? "Relevanten Workspace-Bereich gezielt untersuchen" : "Workspace für die angeforderte Analyse untersuchen",
                null).ConfigureAwait(false);
        }

        async Task<LmChatResult> InvokeRequiredToolAsync(
            IReadOnlyList<LmChatMessage> messages,
            IReadOnlyList<LmToolDefinition> tools,
            string? requiredToolName,
            int maximumOutputTokens,
            int maximumInputTokens)
        {
            var response = await InvokeModelAsync(
                messages,
                tools,
                maximumOutputTokens,
                maximumInputTokens,
                requireToolCall: true,
                requiredToolName).ConfigureAwait(false);
            if (response.ToolCalls.Count == 1)
            {
                return response;
            }
            var repairMessages = messages.Concat([
                new LmChatMessage(
                    "system",
                    requiredToolName is null
                        ? "Der Schritt benötigt jetzt genau einen vollständigen Aufruf aus den angebotenen Werkzeugen. Kein freier Text."
                        : $"Der Schritt benötigt jetzt genau einen vollständigen Aufruf von {requiredToolName}. Kein freier Text.")
            ]).ToArray();
            response = await InvokeModelAsync(
                repairMessages,
                tools,
                maximumOutputTokens,
                maximumInputTokens,
                requireToolCall: true,
                requiredToolName).ConfigureAwait(false);
            if (response.ToolCalls.Count != 1)
            {
                throw new CodingEmptyResponseException("Der isolierte Coding-Schritt hat auch nach einer kontrollierten Reparatur keinen vollständigen Werkzeugaufruf geliefert.");
            }
            return response;
        }

        async Task<string> InvokeVisibleTextAsync(
            string instruction,
            string data,
            int maximumOutputTokens,
            int maximumInputTokens,
            bool allowHostFallback)
        {
            var messages = new[]
            {
                new LmChatMessage("system", "Du bearbeitest einen isolierten internen Schritt desselben Coding-Laufs. Antworte sichtbar und knapp; verwende keine Werkzeuge und keine erfundenen Fakten."),
                new LmChatMessage("user", $"{instruction}\n\n{Limit(data, Math.Max(2_000, maximumInputTokens * 3 - 1_000))}"),
            };
            var response = await InvokeModelAsync(
                messages,
                [],
                maximumOutputTokens,
                maximumInputTokens,
                requireToolCall: false,
                requiredToolName: null).ConfigureAwait(false);
            var text = (response.Content ?? string.Empty).Trim();
            if (text.Length > 0)
            {
                return text;
            }
            var retry = await InvokeModelAsync(
                messages.Concat([new LmChatMessage("system", "Der vorige Turn enthielt nur internes Reasoning. Gib jetzt ausschließlich den sichtbaren Ergebnistext aus.")]).ToArray(),
                [],
                maximumOutputTokens,
                maximumInputTokens,
                requireToolCall: false,
                requiredToolName: null).ConfigureAwait(false);
            text = (retry.Content ?? string.Empty).Trim();
            return text.Length > 0 || !allowHostFallback ? text : Limit(data, maximumOutputTokens * 3);
        }

        async Task<LmChatResult> InvokeModelAsync(
            IReadOnlyList<LmChatMessage> messages,
            IReadOnlyList<LmToolDefinition> tools,
            int maximumOutputTokens,
            int maximumInputTokens,
            bool requireToolCall,
            string? requiredToolName)
        {
            var estimatedInput = CodingContextPlanner.EstimateTokens(messages);
            if (estimatedInput > maximumInputTokens)
            {
                throw new CodingContextBudgetException(estimatedInput, maximumInputTokens);
            }
            state = state with
            {
                Step = state.Step with
                {
                    Context = new StepContextMetrics(estimatedInput, maximumInputTokens, 0, maximumOutputTokens),
                },
            };
            await EmitStepAsync(state.Step, null).ConfigureAwait(false);
            await SaveCheckpointAsync().ConfigureAwait(false);

            for (var transportAttempt = 0; ; transportAttempt++)
            {
                try
                {
                    LmChatResult response;
                    await using (var lease = await scheduler.AcquireAsync(
                        "llm-code",
                        runId,
                        GpuLeaseMode.Exclusive,
                        cancellationToken).ConfigureAwait(false))
                    {
                        var preparation = await workers.PrepareLmModelWithStatusAsync(
                            selection.ModelId,
                            contextLength,
                            async token => await repository.AppendEventAsync(
                                runId,
                                RunEventTypes.ModelLoading,
                                new ModelLoadingEvent(selection.ModelId, "loading", contextLength, contextLength),
                                token).ConfigureAwait(false),
                            cancellationToken).ConfigureAwait(false);
                        if (!preparation.WasAlreadyLoaded)
                        {
                            await repository.AppendEventAsync(
                                runId,
                                RunEventTypes.ModelLoading,
                                new ModelLoadingEvent(selection.ModelId, "loaded", contextLength, contextLength),
                                cancellationToken).ConfigureAwait(false);
                        }
                        response = await modelRuntime.CompleteChatAsync(
                            selection.ModelId,
                            messages,
                            tools,
                            maximumOutputTokens,
                            modelRole: "code",
                            reasoningEffort: null,
                            requireToolCall,
                            requiredToolName,
                            requiredContextLength: contextLength,
                            nativeProgress: async (progress, token) => await repository.AppendEventAsync(
                                runId,
                                RunEventTypes.ModelGeneration,
                                new ModelGenerationEvent(
                                    progress.State,
                                    progress.ToolName,
                                    progress.ArgumentCharacters,
                                    progress.PromptProgress,
                                    progress.PromptTokens,
                                    progress.ProcessedPromptTokens,
                                    progress.GeneratedTokens,
                                    progress.TokensPerSecond,
                                    progress.CurrentTokens,
                                    progress.Attempt,
                                    progress.FailureKind,
                                    progress.ToolArgumentsJsonComplete,
                                    progress.ContentCharacters,
                                    progress.FinishObserved),
                                token).ConfigureAwait(false),
                            structuredToolOnly: tools.Count > 0,
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                    roundCount++;
                    inputTokens += response.InputTokens;
                    outputTokens += response.OutputTokens;
                    state = state with
                    {
                        Step = state.Step with
                        {
                            Context = new StepContextMetrics(
                                response.InputTokens > 0 ? response.InputTokens : estimatedInput,
                                maximumInputTokens,
                                response.OutputTokens,
                                maximumOutputTokens),
                            Attempt = state.Step.Attempt + transportAttempt,
                        },
                    };
                    await EmitStepAsync(state.Step, null).ConfigureAwait(false);
                    await SaveCheckpointAsync().ConfigureAwait(false);
                    return response;
                }
                catch (Exception exception) when (
                    exception is ModelGenerationTerminatedException or HttpRequestException
                    && transportAttempt < 2
                    && !cancellationToken.IsCancellationRequested)
                {
                    await repository.AppendEventAsync(
                        runId,
                        RunEventTypes.ModelGeneration,
                        new ModelGenerationEvent(
                            "transportRetry",
                            state.Step.Tool,
                            Attempt: transportAttempt + 1,
                            FailureKind: exception.GetType().Name),
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }

        async Task<LmToolCall> RepairToolCallAsync(
            LmToolCall rejected,
            AgentToolSpec tool,
            string diagnostic,
            IReadOnlyList<LmChatMessage> originalMessages,
            int maximumOutputTokens)
        {
            await repository.AppendEventAsync(
                runId,
                RunEventTypes.ModelGeneration,
                new ModelGenerationEvent(
                    "toolCallRetry",
                    tool.Name,
                    rejected.Arguments.GetRawText().Length,
                    Attempt: 1,
                    FailureKind: "invalid_tool_arguments"),
                cancellationToken).ConfigureAwait(false);
            var messages = originalMessages.Concat([
                new LmChatMessage("system", $"Der Werkzeugaufruf war ungültig: {Limit(diagnostic, 1_000)} Korrigiere genau einen Aufruf von {tool.Name} anhand des einzigen angebotenen Schemas. Kein freier Text.")
            ]).ToArray();
            var response = await InvokeModelAsync(
                messages,
                [ToolDefinitionForStep(tool, state.Step.Kind)],
                maximumOutputTokens,
                state.Step.Kind == CodingStepKind.Mutation ? 12_000 : 4_000,
                requireToolCall: true,
                requiredToolName: tool.Name).ConfigureAwait(false);
            if (response.ToolCalls.Count != 1 || response.ToolCalls[0].Name != tool.Name)
            {
                throw new CodingAgentProtocolException($"{tool.Name} konnte nach der konkreten Schemadiagnose nicht repariert werden.");
            }
            return response.ToolCalls[0];
        }

        async Task<AgentToolExecutionResult> ExecuteServerToolAsync(AgentToolSpec tool, LmToolCall call)
        {
            toolCallCount++;
            await EmitActionAsync(call, tool).ConfigureAwait(false);
            await repository.AppendEventAsync(
                runId,
                RunEventTypes.ServerToolStarted,
                new { tool = tool.Name, toolCallId = call.Id, target = FindTarget(call.Arguments) },
                cancellationToken).ConfigureAwait(false);
            var result = await toolExecutor.ExecuteAsync(tool.Name, call.Arguments, runId, cancellationToken).ConfigureAwait(false);
            foreach (var artifact in result.Artifacts)
            {
                await repository.AppendEventAsync(runId, RunEventTypes.ArtifactCreated, artifact, cancellationToken).ConfigureAwait(false);
            }
            await repository.AppendEventAsync(
                runId,
                RunEventTypes.ServerToolCompleted,
                new
                {
                    tool = tool.Name,
                    toolCallId = call.Id,
                    target = FindTarget(call.Arguments),
                    success = result.Succeeded,
                    result.ErrorCode,
                    result.ErrorMessage,
                    result = result.Result,
                },
                cancellationToken).ConfigureAwait(false);
            await repository.AppendEventAsync(
                runId,
                RunEventTypes.AgentObservationCommitted,
                new AgentObservationCommittedEvent(call.Id, result.Succeeded, result.ErrorCode, result.ErrorMessage),
                cancellationToken).ConfigureAwait(false);
            await SaveCheckpointAsync().ConfigureAwait(false);
            return result;
        }

        async Task ProposeClientToolAsync(AgentToolSpec tool, LmToolCall call)
        {
            toolCallCount++;
            await EmitActionAsync(call, tool).ConfigureAwait(false);
            var proposalId = $"proposal-{runId}-{state.Step.StepId}-{state.Step.Attempt}";
            var proposal = new ToolProposal(
                proposalId,
                runId,
                tool.Name,
                call.Arguments.Clone(),
                tool.RiskClass,
                CreateProposalSummary(tool.Name, call.Arguments),
                DateTimeOffset.UtcNow.AddHours(1));
            await repository.SaveToolProposalAsync(proposal, cancellationToken).ConfigureAwait(false);
            activeCalls = [call];
            pendingProposalId = proposal.ProposalId;
            pendingToolCallId = call.Id;
            state = state with { Step = state.Step with { Status = CodingStepStatus.WaitingForClient, Tool = tool.Name, Target = FindTarget(call.Arguments) } };
            await EmitStepAsync(state.Step, null).ConfigureAwait(false);
            await SaveCheckpointAsync().ConfigureAwait(false);
            await repository.AppendEventAsync(runId, RunEventTypes.ClientToolProposed, proposal, cancellationToken).ConfigureAwait(false);
            await repository.UpdateStateAsync(
                runId,
                RunState.WaitingForClient,
                selection.ModelId,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await repository.AppendEventAsync(
                runId,
                RunEventTypes.RunWaitingForClient,
                new { proposalId = proposal.ProposalId, tool = proposal.Name, expiresAt = proposal.ExpiresAt },
                cancellationToken).ConfigureAwait(false);
            throw new RunWaitingForClientException();
        }

        async Task TransitionAsync(CodingStepKind kind, string stepGoal, string? detail)
        {
            var previous = state.Step;
            var completed = previous with
            {
                Status = previous.Status == CodingStepStatus.Blocked ? CodingStepStatus.Blocked : CodingStepStatus.Completed,
                Detail = detail ?? previous.Detail,
            };
            state = state with { Step = completed };
            await EmitStepAsync(completed, $"{previous.Kind} → {kind}").ConfigureAwait(false);
            var next = NewStep(previous.Sequence + 1, kind, stepGoal) with { Detail = detail };
            state = state with { Step = next };
            await EmitStepAsync(next, $"{previous.Kind} → {kind}").ConfigureAwait(false);
            await EmitPhaseAsync(kind, stepGoal).ConfigureAwait(false);
            await SaveCheckpointAsync().ConfigureAwait(false);
        }

        Task SaveCheckpointAsync() => repository.SaveCheckpointAsync(
            runId,
            new AgentRunCheckpoint(
                Messages: [],
                RoundCount: roundCount,
                ToolCallCount: toolCallCount,
                InputTokens: inputTokens,
                OutputTokens: outputTokens,
                ActiveToolCalls: activeCalls,
                NextToolIndex: 0,
                PendingProposalId: pendingProposalId,
                PendingToolCallId: pendingToolCallId,
                MutatedPaths: state.MutatedPaths,
                VerificationStages: state.VerifiedPaths,
                AgentProtocolVersion: CheckpointVersion,
                V4State: state),
            cancellationToken);

        async Task AppendMilestoneAsync(string text)
        {
            var normalized = text.Trim();
            if (normalized.Length == 0)
            {
                return;
            }
            if (state.VisibleMessage.Length > 0)
            {
                normalized = "\n\n" + normalized;
            }
            var itemId = AssistantItemId(runId);
            foreach (var delta in SplitDeltas(normalized))
            {
                await repository.AppendEventAsync(
                    runId,
                    RunEventTypes.AgentMessageDelta,
                    new AgentMessageDeltaEvent(
                        itemId,
                        runId,
                        state.Step.Sequence,
                        state.VisibleDeltaCount,
                        delta,
                        AgentMessagePhase.FinalAnswer,
                        AgentMessageOrigin.Host,
                        $"step-{state.Step.Sequence}"),
                    cancellationToken).ConfigureAwait(false);
                state = state with
                {
                    VisibleMessage = state.VisibleMessage + delta,
                    VisibleDeltaCount = state.VisibleDeltaCount + 1,
                };
            }
            await SaveCheckpointAsync().ConfigureAwait(false);
        }

        async Task AppendFinalAsync(string text)
        {
            await AppendMilestoneAsync(text).ConfigureAwait(false);
            await repository.AppendEventAsync(
                runId,
                RunEventTypes.AgentMessageCompleted,
                new AgentMessageCompletedEvent(
                    AssistantItemId(runId),
                    runId,
                    state.Step.Sequence,
                    AgentMessagePhase.FinalAnswer,
                    AgentMessageOrigin.Model,
                    state.VisibleMessage,
                    "final-answer",
                    state.VisibleDeltaCount),
                cancellationToken).ConfigureAwait(false);
        }

        Task EmitStepAsync(CodingStepSnapshot step, string? transition) => repository.AppendEventAsync(
            runId,
            RunEventTypes.CodingStepChanged,
            new CodingStepChangedEvent(step, transition),
            cancellationToken);

        Task EmitActionAsync(LmToolCall call, AgentToolSpec tool) => repository.AppendEventAsync(
            runId,
            RunEventTypes.AgentActionStarted,
            new AgentActionStartedEvent(
                call.Id,
                state.Step.Sequence,
                tool.Name,
                ReadOperation(call),
                FindTarget(call.Arguments)),
            cancellationToken);

        Task EmitPhaseAsync(CodingStepKind kind, string detail) => repository.AppendEventAsync(
            runId,
            RunEventTypes.AgentPhaseChanged,
            new AgentPhaseChangedEvent(MapPhase(kind), detail, state.Step.Sequence),
            cancellationToken);
    }

    private async Task InitializeRunAsync(
        string runId,
        ModelSelection selection,
        CodingAgentV4State state,
        int contextLength,
        CancellationToken cancellationToken)
    {
        await repository.AppendEventAsync(
            runId,
            RunEventTypes.QueueChanged,
            new QueueChangedEvent(scheduler.QueueLength + 1, scheduler.QueueLength + 1),
            cancellationToken).ConfigureAwait(false);
        await repository.UpdateStateAsync(
            runId,
            RunState.Running,
            selection.ModelId,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await repository.AppendEventAsync(
            runId,
            RunEventTypes.RunStarted,
            new { protocolVersion = GoAiProtocol.Version, agentProtocolVersion = PublicAgentProtocolVersion },
            cancellationToken).ConfigureAwait(false);
        await repository.AppendEventAsync(
            runId,
            RunEventTypes.ModelSelected,
            new ModelSelectedEvent(selection.ModelId, selection.Role),
            cancellationToken).ConfigureAwait(false);
        await repository.AppendEventAsync(
            runId,
            RunEventTypes.AgentMessageStarted,
            new AgentMessageStartedEvent(
                AssistantItemId(runId),
                runId,
                0,
                AgentMessagePhase.FinalAnswer,
                AgentMessageOrigin.Model,
                "final-answer"),
            cancellationToken).ConfigureAwait(false);
        await repository.AppendEventAsync(
            runId,
            RunEventTypes.CodingStepChanged,
            new CodingStepChangedEvent(state.Step),
            cancellationToken).ConfigureAwait(false);
        await repository.AppendEventAsync(
            runId,
            RunEventTypes.ContextChanged,
            new ContextChangedEvent(
                0,
                contextLength,
                0,
                false,
                "Coding V4 verwendet pro Arbeitsschritt einen neuen begrenzten Kontext; Rohdaten werden nicht in Folgeschritte übernommen.",
                "coding-v4-steps"),
            cancellationToken).ConfigureAwait(false);
    }

    private static CodingStepSnapshot NewStep(int sequence, CodingStepKind kind, string goal) => new(
        $"step-{sequence:D4}",
        sequence,
        kind,
        CodingStepStatus.Running,
        goal);

    internal static IReadOnlyList<LmChatMessage> BuildStepMessages(
        CodingAgentV4State state,
        WorkspaceDescriptor workspace,
        string instruction,
        string? currentData)
    {
        var system = $"""
            Du bearbeitest genau einen isolierten Schritt eines Coding-Laufs im gebundenen Workspace „{workspace.Name}“.
            Lokale Datei- und Prozesspfade müssen relativ innerhalb dieses Workspace liegen.
            Verwende ausschließlich die in diesem Schritt angebotenen Werkzeuge und höchstens einen Werkzeugaufruf.
            {instruction}
            """;
        var user = new StringBuilder();
        user.AppendLine("Nutzerziel:");
        user.AppendLine(state.Goal);
        if (state.SessionBrief.Length > 0)
        {
            user.AppendLine().AppendLine("Sitzungsbrief:").AppendLine(Limit(state.SessionBrief, MaximumSessionBriefCharacters));
        }
        user.AppendLine().AppendLine("Arbeitsstand:").AppendLine(BuildLedger(state));
        user.AppendLine().AppendLine("Begrenzter Dateibaum:").AppendLine(Limit(workspace.FileTree, MaximumLedgerCharacters));
        if (state.ResearchBrief is not null)
        {
            user.AppendLine().AppendLine("Recherchebrief:").AppendLine(Limit(state.ResearchBrief.Summary, MaximumResearchBriefCharacters));
            user.Append("Quelle: ").AppendLine(state.ResearchBrief.Url);
        }
        if (!string.IsNullOrWhiteSpace(currentData))
        {
            user.AppendLine().AppendLine("Nur für diesen Schritt relevante Daten:").AppendLine(Limit(currentData, MaximumFileExcerptCharacters));
        }
        return [new LmChatMessage("system", system), new LmChatMessage("user", user.ToString())];
    }

    internal static string BuildLedger(CodingAgentV4State state)
    {
        var builder = new StringBuilder();
        if (state.RequiredOutputPaths.Count > 0)
        {
            builder.Append("Verlangte Pfade: ").AppendLine(string.Join(", ", state.RequiredOutputPaths));
        }
        builder.Append("Geändert: ").AppendLine(state.MutatedPaths.Count == 0 ? "keine" : string.Join(", ", state.MutatedPaths));
        builder.Append("Geprüft: ").AppendLine(state.VerifiedPaths.Count == 0 ? "keine" : string.Join(", ", state.VerifiedPaths));
        foreach (var result in state.RecentResults.TakeLast(MaximumRecentResults))
        {
            builder.Append("- ").AppendLine(Limit(result, 420));
        }
        if (!string.IsNullOrWhiteSpace(state.LastError))
        {
            builder.Append("Letzter Fehler: ").AppendLine(Limit(state.LastError, 900));
        }
        if (!string.IsNullOrWhiteSpace(state.BlockedReason))
        {
            builder.Append("Blocker: ").AppendLine(Limit(state.BlockedReason, 900));
        }
        return Limit(builder.ToString().Trim(), MaximumLedgerCharacters);
    }

    internal static LmToolDefinition CreateSearchDefinition(AgentToolSpec tool) => new(
        tool.Name,
        "Suche genau einmal nach höchstens fünf passenden Quellen in der Sprache des Nutzerziels.",
        ParseSchema("""{"type":"object","properties":{"query":{"type":"string","minLength":1,"maxLength":512},"language":{"type":"string","maxLength":32}},"required":["query"],"additionalProperties":false}"""));

    internal static LmToolDefinition CreateFetchDefinition(AgentToolSpec tool) => new(
        tool.Name,
        "Rufe genau eine festgelegte URL auf und suche darin genau eine konkrete Phrase. GO liefert höchstens ein Trefferfenster mit 2.000 Zeichen.",
        ParseSchema("""{"type":"object","properties":{"url":{"type":"string","maxLength":2048},"query":{"type":"string","minLength":1,"maxLength":512}},"required":["url","query"],"additionalProperties":false}"""));

    internal static LmToolDefinition CreateInspectionDefinition(AgentToolSpec tool) => tool.Name switch
    {
        ClientToolNames.FileSystemSearch => new(
            tool.Name,
            "Suche gezielt nach einer Funktion, einem Symbol oder einer Textphrase. Höchstens fünf Treffer mit zwei Kontextzeilen.",
            ParseSchema("""{"type":"object","properties":{"path":{"type":"string"},"query":{"type":"string","minLength":1,"maxLength":512},"matchMode":{"type":"string","enum":["literal","regex"]},"includeGlobs":{"type":"array","maxItems":16,"items":{"type":"string"}},"excludeGlobs":{"type":"array","maxItems":16,"items":{"type":"string"}}},"required":["path","query"],"additionalProperties":false}""")),
        ClientToolNames.FileSystemReadText => new(
            tool.Name,
            "Lade nur einen bekannten Bereich mit höchstens 200 Zeilen und 8.000 Zeichen.",
            ParseSchema("""{"type":"object","properties":{"path":{"type":"string"},"startLine":{"type":"integer","minimum":1},"endLine":{"type":"integer","minimum":1},"matchText":{"type":"string","maxLength":4096}},"required":["path"],"additionalProperties":false}""")),
        _ => tool.ToLmDefinition(),
    };

    internal static LmToolDefinition CreateMutationDefinition(AgentToolSpec tool) => tool.Name switch
    {
        ClientToolNames.FileSystemProposeCreate => new(
            tool.Name,
            "Erstelle eine syntaktisch vollständige Datei. content darf höchstens 8.000 Unicode-Zeichen enthalten.",
            ParseSchema("""{"type":"object","properties":{"path":{"type":"string","maxLength":1024},"content":{"type":"string","maxLength":8000}},"required":["path","content"],"additionalProperties":false}""")),
        ClientToolNames.FileSystemReplaceText => new(
            tool.Name,
            "Ersetze genau einen unmittelbar gelesenen Block. GO ergänzt den gelesenen Referenztext selbst; newText darf höchstens 8.000 Zeichen enthalten.",
            ParseSchema("""{"type":"object","properties":{"path":{"type":"string","maxLength":1024},"oldText":{"type":"string","minLength":1,"maxLength":8000},"newText":{"type":"string","maxLength":8000}},"required":["path","oldText","newText"],"additionalProperties":false}""")),
        _ => tool.ToLmDefinition(),
    };

    private static LmToolDefinition ToolDefinitionForStep(AgentToolSpec tool, CodingStepKind kind) => kind switch
    {
        CodingStepKind.ResearchSearch => CreateSearchDefinition(tool),
        CodingStepKind.ResearchFetch => CreateFetchDefinition(tool),
        CodingStepKind.WorkspaceInspect => CreateInspectionDefinition(tool),
        CodingStepKind.Mutation => CreateMutationDefinition(tool),
        _ => tool.ToLmDefinition(),
    };

    private static LmToolCall NormalizeSearchCall(LmToolCall call, string goal)
    {
        var query = TryReadString(call.Arguments, "query") ?? Limit(goal, 300);
        var language = TryReadString(call.Arguments, "language") ?? InferSearchLanguage(goal);
        return NewCall(call.Id, "web.search", new { query, maximumResults = 5, language });
    }

    internal static LmToolCall NormalizeFetchCall(LmToolCall call, string url, string fallbackQuery)
    {
        var query = fallbackQuery;
        return NewCall(call.Id, "web.fetch", new
        {
            url,
            query,
            maximumResults = 1,
            contextCharacters = 800,
            maximumCharacters = MaximumResearchExcerptCharacters,
        });
    }

    private static LmToolCall NormalizeInspectionCall(LmToolCall call, string? target)
    {
        if (call.Name == ClientToolNames.FileSystemList)
        {
            var path = TryReadString(call.Arguments, "path");
            return NewCall(call.Id, call.Name, new { path = NormalizeWorkspacePath(path) });
        }
        if (call.Name == ClientToolNames.FileSystemSearch)
        {
            var path = NormalizeWorkspacePath(TryReadString(call.Arguments, "path"));
            var query = TryReadString(call.Arguments, "query") ?? Path.GetFileNameWithoutExtension(target ?? string.Empty);
            if (string.IsNullOrWhiteSpace(query))
            {
                query = "class|def|function";
            }
            return NewCall(call.Id, call.Name, new
            {
                path,
                query,
                matchMode = TryReadString(call.Arguments, "matchMode") ?? "literal",
                maximumResults = 5,
                contextLines = 2,
            });
        }
        if (call.Name == ClientToolNames.FileSystemReadText)
        {
            var path = TryReadString(call.Arguments, "path") ?? target ?? string.Empty;
            var start = TryReadInt(call.Arguments, "startLine");
            var end = TryReadInt(call.Arguments, "endLine");
            if (start is not null && end is not null && end - start > 199)
            {
                end = start + 199;
            }
            var matchText = TryReadString(call.Arguments, "matchText");
            var payload = new Dictionary<string, object?>
            {
                ["path"] = path,
                ["maximumCharacters"] = MaximumFileExcerptCharacters,
            };
            if (start is not null) payload["startLine"] = start;
            if (end is not null) payload["endLine"] = end;
            if (!string.IsNullOrWhiteSpace(matchText)) payload["matchText"] = matchText;
            return NewCall(call.Id, call.Name, payload);
        }
        return call;
    }

    private static LmToolCall NormalizeMutationCall(
        LmToolCall call,
        string toolName,
        string? target,
        string? expectedContent)
    {
        if (toolName == ClientToolNames.FileSystemProposeCreate)
        {
            return NewCall(call.Id, toolName, new
            {
                path = target ?? TryReadString(call.Arguments, "path") ?? "output.txt",
                content = TryReadString(call.Arguments, "content") ?? string.Empty,
            });
        }
        return NewCall(call.Id, toolName, new
        {
            path = target ?? TryReadString(call.Arguments, "path") ?? string.Empty,
            oldText = TryReadString(call.Arguments, "oldText") ?? string.Empty,
            newText = TryReadString(call.Arguments, "newText") ?? string.Empty,
            expectedContent = expectedContent ?? string.Empty,
            expectedContentMode = "fragment",
        });
    }

    internal static bool MutationWithinLimit(LmToolCall call)
    {
        var content = call.Name == ClientToolNames.FileSystemProposeCreate
            ? TryReadString(call.Arguments, "content")
            : TryReadString(call.Arguments, "newText");
        return content is not null && content.Length <= MaximumMutationCharacters;
    }

    internal static IReadOnlyList<string> ParseResearchPhrases(string modelText, string goal, string url)
    {
        // A direct URL usually carries the most reliable page-local wording.
        // Put that deterministic phrase first so a plausible but over-specific
        // model phrase cannot consume all three bounded fetch attempts.
        var phrases = ExtractUrlPhraseFallbacks(url).ToList();
        var modelPhrases = modelText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static value => Regex.Replace(value, @"^[-*\d.)\s]+", string.Empty).Trim('"', '\'', '`'))
            .Where(static value => value.Length is >= 3 and <= 160)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var phrase in modelPhrases)
        {
            if (phrases.Count >= MaximumResearchPhrases)
            {
                break;
            }
            if (!phrases.Contains(phrase, StringComparer.OrdinalIgnoreCase))
            {
                phrases.Add(phrase);
            }
        }
        foreach (var fallback in ExtractPhraseFallbacks(goal, url))
        {
            if (phrases.Count >= MaximumResearchPhrases)
            {
                break;
            }
            if (!phrases.Contains(fallback, StringComparer.OrdinalIgnoreCase))
            {
                phrases.Add(fallback);
            }
        }
        return phrases;
    }

    internal static (string Url, string Query)? NextResearchPair(CodingAgentV4State state)
    {
        foreach (var url in state.ResearchSources.Take(MaximumResearchSources))
        {
            foreach (var phrase in state.ResearchPhrases.Take(MaximumResearchPhrases))
            {
                if (!state.AttemptedResearchKeys.Contains(ResearchKey(url, phrase), StringComparer.OrdinalIgnoreCase))
                {
                    return (url, phrase);
                }
            }
        }
        return null;
    }

    internal static bool IsUsableResearchExcerpt(string? excerpt)
    {
        if (string.IsNullOrWhiteSpace(excerpt) || excerpt.Length < 120)
        {
            return false;
        }
        var normalized = Regex.Replace(excerpt, @"\s+", " ").Trim();
        var words = ResearchWordRegex().Count(normalized);
        var navigation = ResearchNavigationRegex().Count(normalized);
        var punctuation = normalized.Count(static character => character is '.' or ';' or ':' or '=' or '∑' or '∫');
        return words >= 18 && (punctuation >= 2 || words >= 35) && navigation * 5 < words;
    }

    internal static (AgentToolSpec Tool, LmToolCall Call)? CreateVerificationCall(
        string path,
        WorkspaceDescriptor workspace,
        IReadOnlyList<AgentToolSpec> tools,
        CodingStepSnapshot step)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var callId = $"verify-{step.StepId}-{step.Attempt}";
        if (extension == ".lean")
        {
            var tool = tools.FirstOrDefault(static item => item.Name == ClientToolNames.LeanProof);
            return tool is null
                ? null
                : (tool, NewCall(callId, tool.Name, new { operation = "check", path, timeoutSeconds = 300 }));
        }
        var process = tools.FirstOrDefault(static item => item.Name == ClientToolNames.ProcessRun);
        var preset = tools.FirstOrDefault(static item => item.Name == ClientToolNames.ProcessRunPreset);
        return extension switch
        {
            ".py" or ".pyw" when process is not null => (process, NewCall(callId, process.Name, new
            {
                executable = "py",
                arguments = new[] { "-3", "-m", "py_compile", path },
                workingDirectory = ".",
                timeoutSeconds = 180,
                purpose = "test",
                startMode = "wait",
            })),
            ".js" when process is not null => (process, NewCall(callId, process.Name, new
            {
                executable = "node",
                arguments = new[] { "--check", path },
                workingDirectory = ".",
                timeoutSeconds = 180,
                purpose = "test",
                startMode = "wait",
            })),
            ".cs" or ".xaml" or ".csproj" when preset is not null => (preset, NewCall(callId, preset.Name, new
            {
                preset = "dotnet.build",
                target = FindFirstTreePath(workspace.FileTree, ".sln")
                    ?? FindFirstTreePath(workspace.FileTree, ".csproj")
                    ?? path,
            })),
            ".rs" when process is not null && workspace.FileTree.Contains("Cargo.toml", StringComparison.OrdinalIgnoreCase) => (process, NewCall(callId, process.Name, new
            {
                executable = "cargo",
                arguments = CargoCheckArguments,
                workingDirectory = ".",
                timeoutSeconds = 600,
                purpose = "test",
                startMode = "wait",
            })),
            ".go" when process is not null => (process, NewCall(callId, process.Name, new
            {
                executable = "go",
                arguments = GoTestArguments,
                workingDirectory = ".",
                timeoutSeconds = 600,
                purpose = "test",
                startMode = "wait",
            })),
            _ => null,
        };
    }

    private static string? NextMutationTarget(CodingAgentV4State state) => state.RequiredOutputPaths
        .FirstOrDefault(path => !state.MutatedPaths.Contains(path, StringComparer.OrdinalIgnoreCase));

    private static bool WorkspaceLikelyContains(WorkspaceDescriptor workspace, string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('.', '/');
        return workspace.FileTree.Replace('\\', '/').Contains(normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static AgentToolSpec ResolveTool(string name, IReadOnlyList<AgentToolSpec> available) =>
        available.FirstOrDefault(tool => tool.Name == name)
        ?? throw new CodingAgentProtocolException($"Der für den V4-Schritt benötigte Toolvertrag '{name}' ist nicht verfügbar.");

    private static string[] AddRecent(IReadOnlyList<string> values, string value) => values
        .Append(Limit(value, 500))
        .TakeLast(MaximumRecentResults)
        .ToArray();

    private static string[] ClearRecoveredFailures(IReadOnlyList<string> values) => values
        .Where(static value => !value.Contains("fehlgeschlagen", StringComparison.OrdinalIgnoreCase))
        .ToArray();

    private static string CompactClientResult(string toolName, ClientToolResult result)
    {
        if (!IsSuccessful(result))
        {
            return Limit($"{toolName}: {result.ErrorCode} {result.Message} {result.Result.GetRawText()}", MaximumLedgerCharacters);
        }
        if (toolName == ClientToolNames.FileSystemReadText)
        {
            return $"{TryReadString(result.Result, "path")}: Zeilen {TryReadInt(result.Result, "startLine")}-{TryReadInt(result.Result, "endLine")}, vollständig={TryReadBoolean(result.Result, "completeFile")}.";
        }
        if (toolName == ClientToolNames.FileSystemSearch)
        {
            return Limit(result.Result.GetRawText(), MaximumLedgerCharacters);
        }
        if (toolName is ClientToolNames.ProcessRun or ClientToolNames.ProcessRunPreset or ClientToolNames.LeanProof)
        {
            return LimitProcessResult(result.Result);
        }
        return Limit(result.Result.GetRawText(), MaximumLedgerCharacters);
    }

    private static string CompactResultLabel(string toolName, JsonElement result) => toolName switch
    {
        ClientToolNames.FileSystemReadText => $"Dateiblock gelesen: {TryReadString(result, "path")}",
        ClientToolNames.FileSystemSearch => TryReadBoolean(result, "found") == true
            ? "Gezielte Quelltextsuche lieferte Treffer."
            : "Gezielte Quelltextsuche: found=false.",
        ClientToolNames.FileSystemList => "Workspace-Ordner begrenzt aufgelistet.",
        _ => $"{toolName} erfolgreich.",
    };

    private static string LimitProcessResult(JsonElement result)
    {
        var exitCode = TryReadInt(result, "exitCode");
        var stdout = TryReadString(result, "standardOutput") ?? TryReadString(result, "stdout") ?? string.Empty;
        var stderr = TryReadString(result, "standardError") ?? TryReadString(result, "stderr") ?? string.Empty;
        return Limit($"ExitCode={exitCode}\n{stdout}\n{stderr}", MaximumLedgerCharacters);
    }

    private static LmChatMessage[] BuildSessionMessages(RunRequest request) => request.Messages
        .Select(message => new LmChatMessage(
            string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user",
            string.Join("\n", message.Content.Where(static part => part.Type == "text").Select(static part => part.Text).Where(static text => !string.IsNullOrWhiteSpace(text)))))
        .Where(static message => !string.IsNullOrWhiteSpace(message.Content))
        .ToArray();

    private static string BuildSessionHistorySource(RunRequest request)
    {
        var messages = BuildSessionMessages(request);
        if (messages.Length <= 1)
        {
            return string.Empty;
        }
        return Limit(string.Join("\n\n", messages.Take(messages.Length - 1).Select(static message => $"{message.Role}: {message.Content}")), 12_000);
    }

    private static string GetLatestUserText(RunRequest request) => request.Messages
        .LastOrDefault(static message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))?
        .Content.Where(static part => part.Type == "text")
        .Select(static part => part.Text)
        .LastOrDefault(static text => !string.IsNullOrWhiteSpace(text))?
        .Trim() ?? string.Empty;

    private static string[] ExtractPublicUrls(string value)
    {
        var urls = new List<string>();
        foreach (Match match in PublicUrlRegex().Matches(value.Replace("\\_", "_", StringComparison.Ordinal)))
        {
            var candidate = match.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '"', '\'');
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                urls.Add(uri.AbsoluteUri);
            }
        }
        return urls.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaximumResearchSources).ToArray();
    }

    private static bool RequestsResearch(string value) => ResearchRequestRegex().IsMatch(value);

    private static string[] ExtractPhraseFallbacks(string goal, string url)
    {
        var decoded = Uri.UnescapeDataString(url.Replace('_', ' '));
        var candidates = Regex.Matches($"{goal} {decoded}", @"[\p{L}\p{N}][\p{L}\p{N}\-]{2,}")
            .Select(static match => match.Value)
            .Where(static value => !StopWords.Contains(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var phrases = new List<string>();
        if (candidates.Length >= 3)
        {
            phrases.Add(string.Join(' ', candidates.Take(3)));
        }
        phrases.AddRange(candidates.Take(3));
        return phrases.Where(static value => value.Length >= 3).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToArray();
    }

    private static string[] ExtractUrlPhraseFallbacks(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return [];
        }

        var segment = Uri.UnescapeDataString(uri.Segments.LastOrDefault() ?? string.Empty)
            .Replace('_', ' ')
            .Trim('/', ' ', '\t', '\r', '\n');
        if (segment.Length == 0)
        {
            return [];
        }

        var specific = segment.Contains(':', StringComparison.Ordinal)
            ? segment[(segment.LastIndexOf(':') + 1)..]
            : segment;
        specific = Regex.Replace(specific, @"[^\p{L}\p{N}\-]+", " ").Trim();
        if (specific.Length < 3)
        {
            return [];
        }
        return [Limit(specific, 160)];
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "https", "http", "wiki", "org", "www", "durch", "führe", "fuehre", "erstelle", "schreibe",
        "dafür", "dafuer", "eine", "einen", "dieses", "thema", "datei", "namens", "content", "vor",
    };

    private static string InferSearchLanguage(string goal) => Regex.IsMatch(goal, @"\b(?:the|and|create|write|search)\b", RegexOptions.IgnoreCase)
        ? "en-US"
        : "de-DE";

    private static string ResearchKey(string url, string query) => $"{url}\n{query}";

    private static string NormalizeWorkspacePath(string? path) => string.IsNullOrWhiteSpace(path) || path is "/" or "\\"
        ? "."
        : path.Replace('\\', '/').TrimStart('/');

    private static string? FindFirstTreePath(string tree, string suffix) => tree
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(static line => line.TrimStart('-', ' ', '├', '└', '│'))
        .FirstOrDefault(line => line.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    private static string ReadOperation(LmToolCall call) => TryReadString(call.Arguments, "operation")
        ?? TryReadString(call.Arguments, "preset")
        ?? TryReadString(call.Arguments, "purpose")
        ?? call.Name;

    private static string? FindTarget(JsonElement arguments)
    {
        foreach (var name in new[] { "path", "url", "query", "source", "destination", "target", "workingDirectory" })
        {
            if (TryReadString(arguments, name) is { Length: > 0 } value)
            {
                return Limit(value, 512);
            }
        }
        return null;
    }

    private static string CreateProposalSummary(string tool, JsonElement arguments) => FindTarget(arguments) is { } target
        ? $"GO soll {tool} für „{target}“ lokal ausführen."
        : $"GO soll {tool} lokal ausführen.";

    private static bool IsSuccessful(ClientToolResult result) =>
        result.Status.Equals("completed", StringComparison.OrdinalIgnoreCase)
        || result.Status.Equals("succeeded", StringComparison.OrdinalIgnoreCase)
        || result.Status.Equals("success", StringComparison.OrdinalIgnoreCase);

    private static string? TryReadString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
            ? NormalizeModelText(property.GetString())
            : null;

    /// <summary>
    /// Removes isolated UTF-16 surrogate code units produced by malformed
    /// provider output while preserving valid surrogate pairs. Tool payloads
    /// must contain Unicode scalar values before they cross the client bridge.
    /// </summary>
    internal static string? NormalizeModelText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

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

            builder ??= new StringBuilder(value.Length)
                .Append(value, 0, index);
        }
        return builder?.ToString() ?? value;
    }

    private static int? TryReadInt(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property)
        && property.TryGetInt32(out var number)
            ? number
            : null;

    private static bool? TryReadBoolean(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property)
        && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : null;

    private static LmToolCall NewCall(string id, string name, object arguments) => new(
        string.IsNullOrWhiteSpace(id) ? $"call-{Guid.NewGuid():N}" : id,
        name,
        JsonSerializer.SerializeToElement(arguments, JsonOptions));

    private static JsonElement ParseSchema(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string AssistantItemId(string runId) => runId + "-assistant";

    private static IEnumerable<string> SplitDeltas(string value)
    {
        const int maximum = 512;
        for (var offset = 0; offset < value.Length;)
        {
            var length = Math.Min(maximum, value.Length - offset);
            if (offset + length < value.Length && char.IsHighSurrogate(value[offset + length - 1]))
            {
                length--;
            }
            yield return value.Substring(offset, length);
            offset += length;
        }
    }

    private static string Limit(string value, int maximum) => value.Length <= maximum
        ? value
        : value[..maximum].TrimEnd() + "…";

    private static CodingAgentPhase MapPhase(CodingStepKind kind) => kind switch
    {
        CodingStepKind.Capture or CodingStepKind.ResearchSearch or CodingStepKind.ResearchFetch or CodingStepKind.ResearchSummarize or CodingStepKind.WorkspaceInspect => CodingAgentPhase.Orienting,
        CodingStepKind.Mutation => CodingAgentPhase.Editing,
        CodingStepKind.Verification => CodingAgentPhase.Verifying,
        CodingStepKind.Finish => CodingAgentPhase.Finishing,
        _ => CodingAgentPhase.Orienting,
    };

    [GeneratedRegex(@"https?://[^\s<>\]\[\""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PublicUrlRegex();

    [GeneratedRegex(@"\b(?:websuche|webrecherche|recherchier|suche\s+im\s+web|web\s*search|fetch|webseite|website)\w*\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResearchRequestRegex();

    [GeneratedRegex(@"\p{L}[\p{L}\p{N}_-]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex ResearchWordRegex();

    [GeneratedRegex(@"\b(?:navigation|menü|menu|anmelden|login|cookie|datenschutz|impressum|hauptseite|zurück|weiter)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResearchNavigationRegex();
}
