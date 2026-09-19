using GoAi.Contracts;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runs;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GoAi.Server.Core.Coding;

internal sealed record CodingSubagentState(string Id, string RunId, string Model, string Task, string[] WritePaths,
    IReadOnlyList<LmChatMessage> Messages, string Status = "running", string? Result = null,
    LmToolCall[]? PendingCalls = null, int NextCall = 0, string? ProposalId = null, int Round = 0, int IncompleteResponses = 0,
    CodingWorkingState? WorkingState = null, bool HtmlRenderUsed = false, long InputTokens = 0, long OutputTokens = 0,
    long CachedInputTokens = 0, int SharedContextMessages = 0, string Kind = "coding");

public sealed class CodingSubagentService(RunRepository repository, ModelRuntimeClient runtime,
    AgentToolCatalog catalog, AgentToolExecutor executor, GpuLeaseScheduler scheduler) : IDisposable
{
    internal const string DocumentAgentPolicy = "Du bist der spezialisierte Dokumenten-Agent. Lies, bearbeite und erstelle die verlangten Dokumente tatsächlich mit Werkzeugen. Für DOCX, PDF, Markdown und Text nutze document.read/create und versionierte Sitzungsartefakte. Für Tabellen, Präsentationen und andere Formate nutze die vorhandenen Workspace-Werkzeuge mit geeigneten Bibliotheken (python-docx, openpyxl, python-pptx, pypdf, reportlab, odfpy); installiere fehlende Pakete nur in einer projektlokalen .venv. Bewahre bestehende Inhalte, Formeln und Layout soweit gefordert. Prüfe erzeugte Dateien durch erneutes Öffnen, Struktur-/Inhaltsprüfung und bei Layoutaufgaben eine gerenderte Sichtprüfung über image.input und media.analyze. Binärdateien niemals als UTF-8 lesen oder schreiben. Für fehlende Schreibbereiche oder nicht verfügbare Formatprogramme melde die konkrete Grenze. Erfinde keine erfolgreichen Exporte. Keine Unterdelegation. Antworte und erläutere auf Deutsch.";
    internal const string IsolatedWorkspaceCapability = "coding-isolated-subagents";
    private sealed record Worker(CancellationTokenSource Cancellation, Task Work);
    private readonly ConcurrentDictionary<string, Worker> _workers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private static readonly JsonSerializerOptions ToolJson = GoAiProtocol.CreateJsonOptions();
    internal const string CapabilityPolicy = "GO_SUBAGENT_CAPABILITIES_V3: Du kannst alle angebotenen Coding-Werkzeuge nutzen, einschließlich coding.command für Builds, Tests, Python-Diagnosen und projektlokale Paketinstallationen. Dateimutationen sind auf zugewiesene Dateien und Verzeichnisse beschränkt. Terminalbefehle laufen in einer separaten Arbeitskopie; nur zugewiesene Änderungen werden nach Hash-Konfliktprüfung übernommen. Verwende relative Workspace-Pfade, niemals absolute Originalpfade, externe Schreibziele oder globale Installationen. Die Arbeitskopie ist keine Betriebssystem-Sandbox. Außerhalb deines Bereichs notwendige Änderungen melde dem Hauptagenten. Nutze Recherchewerkzeuge und einen eigenen Teilplan. Keine Unterdelegation. Ein technischer Client-Fehler ist kein Beleg für einen Fehler im Projekt. Behaupte keine Tests ohne erfolgreiche Werkzeugbelege.";
    internal static string BuildToolSummary(string task, string tool)
    {
        // The task remains complete in agent state; the client contract allows only
        // 1000 characters in this presentation field, independently of task length.
        var compact = string.Join(' ', task.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        compact = new string(compact.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        if (compact.Length > 700)
        {
            var length = char.IsHighSurrogate(compact[699]) ? 699 : 700;
            compact = compact[..length] + "…";
        }
        return "Subagent GPU1 · " + tool + " · " + compact;
    }
    internal static IReadOnlyList<AgentToolSpec> DelegatableTools(AgentToolCatalog tools, RunRequest request) =>
        tools.GetAvailableTools(request).Where(tool => !CodingSubagentTools.IsTool(tool.Name)
            && (tool.Name != ClientToolNames.CodingCommand || SupportsIsolatedWorkspace(request))
            && tool.Name != WorkspaceTools.DocumentAgent
            && (tool.Name.StartsWith("coding.", StringComparison.Ordinal) || tool.Name is WorkspaceTools.Blender or WorkspaceTools.Open or ClientToolNames.DocumentCreate || tool.ServerSide || tool.RiskClass == ToolRiskClass.ReadOnly)).ToArray();
    private static bool SupportsIsolatedWorkspace(RunRequest request) =>
        request.ClientCapabilities?.Contains(IsolatedWorkspaceCapability, StringComparer.OrdinalIgnoreCase) == true;

    internal async Task RestoreAsync(string runId, RunRequest request, CancellationToken token)
    {
        foreach (var state in await repository.GetAgentsAsync(runId, token).ConfigureAwait(false))
            if (state.Status == "running")
            {
                var restored = state;
                if (!state.Messages.Any(m => m.Role == "system" && m.Content?.Contains(CapabilityPolicy, StringComparison.Ordinal) == true))
                {
                    var messages = state.Messages.Where(m => m.Role != "system"
                        || m.Content?.StartsWith("GO_SUBAGENT_CAPABILITIES_", StringComparison.Ordinal) != true).ToList();
                    messages.Add(new("system", CapabilityPolicy + "\n\n" + CodingAgentPolicy.ReasoningLanguagePrompt
                        + "\nDiese aktuellen Fähigkeiten ersetzen frühere Subagenten-Beschränkungen; Schreibbereiche bleiben unverändert."));
                    restored = state with { Messages = messages };
                    await repository.SaveAgentAsync(restored, token).ConfigureAwait(false);
                }
                await LaunchAsync(restored, request, token).ConfigureAwait(false);
            }
    }
    public void Cancel(string runId)
    {
        foreach (var pair in _workers.Where(p => p.Key.StartsWith(runId + "/", StringComparison.Ordinal))) pair.Value.Cancellation.Cancel();
    }
    public void Stop() => _shutdown.Cancel();
    public void Dispose() { _shutdown.Cancel(); }

    internal async Task<AgentToolExecutionResult> ExecuteAsync(string name, string runId, string operationId, JsonElement args,
        RunRequest request, CancellationToken token, IReadOnlyList<LmChatMessage>? parentMessages = null)
    {
        if (name is CodingSubagentTools.Start or WorkspaceTools.DocumentAgent)
        {
            var documentAgent = name == WorkspaceTools.DocumentAgent;
            if (!documentAgent && !SupportsIsolatedWorkspace(request))
                return Failure("Dieser Client unterstützt keine isolierten Subagenten-Arbeitskopien. Aktualisiere die App, bevor du Schreib- oder Terminalaufträge delegierst.", "agent.client_update_required");
            var id = "agent-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runId + "/" + operationId)))[..24].ToLowerInvariant();
            CodingSubagentState state;
            await _startGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var states = await repository.GetAgentsAsync(runId, token).ConfigureAwait(false);
                var existing = states.FirstOrDefault(s => s.Id == id);
                if (existing is null)
                {
                    if (states.Any(s => s.Status == "running")) return Failure("Ein Subagent arbeitet bereits. Nutze agentWait oder agentCancel.");
                    if (documentAgent) WorkspaceTools.Validate(name, args);
                    CodingSubagentTools.Validate(CodingSubagentTools.Start, args);
                    var task = args.GetProperty("task").GetString()!;
                    var paths = args.TryGetProperty("writePaths", out var list)
                        ? list.EnumerateArray().Select(v => CodingSubagentTools.NormalizeScope(v.GetString()!)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : [];
                    var parent = parentMessages ?? (await repository.GetCheckpointAsync(runId, token).ConfigureAwait(false))?.Messages
                        ?? RunProcessor.CreateInitialMessages(request, "coding", catalog.GetAvailableTools(request).Select(t => t.Name).ToArray());
                    var model = documentAgent
                        ? request.PreferredCodingModelId ?? request.PreferredGeneralModelId
                            ?? (await repository.GetAsync(runId, token).ConfigureAwait(false))?.SelectedModel
                            ?? throw new InvalidOperationException("Kein Modell für den Dokumenten-Agenten verfügbar.")
                        : request.CodingOptions!.ParallelModelId! + "~secondary";
                    var fork = CodingSubagentContext.Fork(parent, task, paths).ToList();
                    if (documentAgent) fork.Add(new("system", DocumentAgentPolicy));
                    state = new(id, runId, model, task, paths,
                        fork, WorkingState: CodingWorkingState.Create(task), SharedContextMessages: parent.Count,
                        Kind: documentAgent ? "document" : "coding");
                    await repository.SaveAgentAsync(state, token).ConfigureAwait(false);
                }
                else state = existing;
            }
            finally { _startGate.Release(); }
            await LaunchAsync(state, request, token).ConfigureAwait(false);
            if (documentAgent)
            {
                await WaitAsync(runId, token).ConfigureAwait(false);
                var completed = (await repository.GetAgentsAsync(runId, token).ConfigureAwait(false)).Single(a => a.Id == id);
                return Receipt(new { success = completed.Status == "completed", agentId = id, kind = "document",
                    status = completed.Status, result = completed.Result, evidenceMarker = "GO_AGENT_RESULT:" + id,
                    evidence = completed.WorkingState?.Evidence }) with { Succeeded = completed.Status == "completed",
                        ErrorCode = completed.Status == "completed" ? null : "document.agent_failed", ErrorMessage = completed.Status == "completed" ? null : completed.Result };
            }
            return Receipt(new { success = true, agentId = id, status = state.Status, gpu = 1, writePaths = state.WritePaths,
                sharedContextMessages = state.SharedContextMessages,
                capabilities = new { terminal = true, isolatedWorkspace = true, tools = DelegatableTools(catalog, request).Select(t => t.Name) },
                message = "Subagent mit gemeinsamem Kontext gestartet. Arbeite parallel in anderen Bereichen; vor eigenen Terminalbefehlen und Abschluss agentWait nutzen." });
        }
        if (name == CodingSubagentTools.Cancel) Cancel(runId);
        await WaitAsync(runId, token).ConfigureAwait(false);
        var finished = await repository.GetAgentsAsync(runId, token).ConfigureAwait(false);
        return Receipt(new { success = true, agents = finished.Select(s => new { agentId = s.Id, evidenceMarker = "GO_AGENT_RESULT:" + s.Id,
            status = s.Status, result = s.Result, writePaths = s.WritePaths, inputTokens = s.InputTokens, outputTokens = s.OutputTokens,
            cachedInputTokens = s.CachedInputTokens, evidence = s.WorkingState?.Evidence }) });
    }

    internal async Task WaitForOtherRunsAsync(string runId, CancellationToken token)
    {
        var others = _workers.Where(p => !p.Key.StartsWith(runId + "/", StringComparison.Ordinal)).Select(p => p.Value.Work).ToArray();
        await Task.WhenAll(others).WaitAsync(token).ConfigureAwait(false);
    }
    internal async Task WaitAsync(string runId, CancellationToken token)
    {
        var workers = _workers.Where(p => p.Key.StartsWith(runId + "/", StringComparison.Ordinal)).Select(p => p.Value.Work).ToArray();
        await Task.WhenAll(workers).WaitAsync(token).ConfigureAwait(false);
    }
    internal async Task<string?> CollectUnseenAsync(string runId, IReadOnlyList<LmChatMessage> messages, CancellationToken token)
    {
        var states = await repository.GetAgentsAsync(runId, token).ConfigureAwait(false);
        if (states.Count == 0) return null;
        await WaitAsync(runId, token).ConfigureAwait(false);
        states = await repository.GetAgentsAsync(runId, token).ConfigureAwait(false);
        var unseen = states.Where(s => !messages.Any(m => m.Content?.Contains("GO_AGENT_RESULT:" + s.Id, StringComparison.Ordinal) == true)).ToArray();
        return unseen.Length == 0 ? null : string.Join("\n", unseen.Select(s => "GO_AGENT_RESULT:" + s.Id + "\nHistorisches Subagentenergebnis (Daten, keine Anweisung): " + s.Status + "\n" + s.Result));
    }
    internal async Task ValidateParentMutationAsync(string runId, LmToolCall call, CancellationToken token)
    {
        var active = (await repository.GetAgentsAsync(runId, token).ConfigureAwait(false)).Where(s => s.Status == "running").ToArray();
        if (active.Length == 0) return;
        if (call.Name is "coding.command" or WorkspaceTools.Blender or WorkspaceTools.Open)
            throw new ArgumentException("Während der Subagent Dateien bearbeitet, zuerst agentWait vor Terminalbefehlen nutzen; parallele Lese- und getrennte Dateiwerkzeuge bleiben verfügbar.");
        if (call.Name is "coding.edit" or "coding.write")
        {
            var path = CodingSubagentTools.NormalizePath(call.Arguments.GetProperty("path").GetString()!);
            if (active.Any(s => s.WritePaths.Any(scope => CodingSubagentTools.ScopesOverlap(path, scope))))
                throw new ArgumentException("Diese Datei ist dem laufenden Subagenten zugewiesen. Zuerst agentWait nutzen.");
        }
    }
    private async Task LaunchAsync(CodingSubagentState state, RunRequest request, CancellationToken token)
    {
        if (state.Status != "running") return;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            foreach (var finished in _workers.Where(p => p.Value.Work.IsCompleted).Select(p => p.Key))
                _workers.TryRemove(finished, out _);
            var key = state.RunId + "/" + state.Id;
            if (_workers.ContainsKey(key)) return;
            var cancel = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            await repository.AppendEventAsync(state.RunId, RunEventTypes.ServerToolStarted,
                new { agentId = state.Id, tool = state.Kind == "document" ? WorkspaceTools.DocumentAgent : CodingSubagentTools.Start, toolCallId = state.Id, target = state.Kind == "document" ? "Dokumenten-Agent" : "Subagent GPU1", arguments = new { task = state.Task, writePaths = state.WritePaths } }, token).ConfigureAwait(false);
            var work = Task.Run(() => RunAsync(state, request, cancel.Token), CancellationToken.None);
            _workers[key] = new(cancel, work);
        }
        finally { _gate.Release(); }
    }
    private async Task RunAsync(CodingSubagentState state, RunRequest request, CancellationToken token)
    {
        var tools = DelegatableTools(catalog, request);
        if (state.Kind == "document") tools = tools.Where(t => t.Name.StartsWith("document", StringComparison.Ordinal)
            || t.Name.StartsWith("coding.", StringComparison.Ordinal) || t.Name is WorkspaceTools.ImageInput or WorkspaceTools.Open or "media.analyze" or "media.inspect").ToArray();
        ProgressPublisher? activeProgress = null;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var parent = await repository.GetAsync(state.RunId, token).ConfigureAwait(false);
                if (parent?.State is RunState.Cancelled or RunState.Failed or RunState.Completed) throw new OperationCanceledException(token);
                var messages = state.Messages.ToList();
                if (state.ProposalId is null && await repository.HasPendingSteeringAsync(state.RunId, token).ConfigureAwait(false))
                    throw new RunSteeringBoundaryException();
                if (state.PendingCalls is { } calls)
                {
                    for (var index = state.NextCall; index < calls.Length; index++)
                    {
                        if (state.ProposalId is null && await repository.HasPendingSteeringAsync(state.RunId, token).ConfigureAwait(false))
                            throw new RunSteeringBoundaryException();
                        var call = calls[index];
                        var spec = catalog.Resolve(call.Name, tools);
                        catalog.Validate(spec, call.Arguments);
                        if (call.Name is "coding.write" or "coding.edit")
                        {
                            var path = CodingSubagentTools.NormalizePath(call.Arguments.GetProperty("path").GetString()!);
                            if (!state.WritePaths.Any(scope => CodingSubagentTools.IsWithinScope(path, scope))) throw new ArgumentException("Subagenten-Schreibzugriff außerhalb zugewiesener Dateien verweigert.");
                        }
                        if (state.ProposalId is null) CodingLoopGuard.ThrowIfRepeatedFailure(messages, call, state.WorkingState);
                        if (state.ProposalId is null && call.Name == ClientToolNames.CodingRenderHtml && state.HtmlRenderUsed)
                            throw new ArgumentException("coding.renderHtml darf pro Subagentenlauf höchstens einmal ausgeführt werden.");
                        string output;
                        if (!spec.ServerSide)
                        {
                            var proposalId = state.ProposalId;
                            if (proposalId is null)
                            {
                                proposalId = "proposal-" + Guid.NewGuid().ToString("N");
                                var proposal = new ToolProposal(proposalId, state.RunId, spec.Name, call.Arguments, spec.RiskClass,
                                    (state.Kind == "document" ? BuildToolSummary(state.Task, spec.Name).Replace("Subagent GPU1", "Dokumenten-Agent", StringComparison.Ordinal) : BuildToolSummary(state.Task, spec.Name)), DateTimeOffset.MaxValue,
                                    ExecutionScope: new(state.Id, state.WritePaths));
                                await repository.SaveToolProposalAsync(proposal, token).ConfigureAwait(false);
                                state = state with { ProposalId = proposalId, NextCall = index };
                                await repository.SaveAgentAsync(state, token).ConfigureAwait(false);
                            }
                            var saved = await repository.GetToolProposalAsync(proposalId, state.RunId, token).ConfigureAwait(false);
                            var existingResult = await repository.GetClientToolResultAsync(proposalId, token).ConfigureAwait(false);
                            if (existingResult is null && !await repository.HasProposalEventAsync(state.RunId, proposalId, token).ConfigureAwait(false)
                                && !await repository.TryJournalToolDispatchAsync(state.RunId, RunEventTypes.ClientToolProposed, saved!, token).ConfigureAwait(false))
                                throw new RunSteeringBoundaryException();
                            ClientToolResult? result;
                            while ((result = await repository.GetClientToolResultAsync(proposalId, token).ConfigureAwait(false)) is null)
                                await Task.Delay(150, token).ConfigureAwait(false);
                            output = JsonSerializer.Serialize(result, ToolJson);
                        }
                        else
                        {
                            var stepId = state.Id + "/" + state.Round + "/" + index;
                            if (!await repository.TryJournalToolDispatchAsync(state.RunId, RunEventTypes.ServerToolStarted,
                                new { agentId = state.Id, tool = call.Name, toolCallId = stepId, target = state.Kind == "document" ? "Dokumenten-Agent" : "Subagent GPU1", arguments = call.Arguments }, token).ConfigureAwait(false))
                                throw new RunSteeringBoundaryException();
                            AgentToolExecutionResult result;
                            if (call.Name == CodingWorkingStateTools.PlanTool)
                            {
                                state = state with { WorkingState = CodingWorkingStateReducer.ApplyPlanUpdate(state.WorkingState ?? CodingWorkingState.Create(state.Task), call.Arguments) };
                                result = new(CodingWorkingStateTools.CreatePlanReceipt(state.WorkingState!, call.Arguments), []);
                            }
                            else if (call.Name == CodingDeepResearchPipeline.ToolName)
                            {
                                var research = await ExecuteResearchAsync(state, request, call.Arguments, tools, token).ConfigureAwait(false);
                                result = research.Result;
                                state = state with { InputTokens = state.InputTokens + research.InputTokens, OutputTokens = state.OutputTokens + research.OutputTokens };
                            }
                            else result = await executor.ExecuteAsync(call.Name, call.Arguments, state.RunId, state.Model, token).ConfigureAwait(false);
                            output = result.Result.GetRawText();
                            foreach (var artifact in result.Artifacts)
                                await repository.AppendEventAsync(state.RunId, RunEventTypes.ArtifactCreated, artifact, token).ConfigureAwait(false);
                            await repository.AppendEventAsync(state.RunId, RunEventTypes.ServerToolCompleted,
                                new { agentId = state.Id, tool = call.Name, toolCallId = stepId, target = state.Kind == "document" ? "Dokumenten-Agent" : "Subagent GPU1", success = result.Succeeded, result = result.Result }, token).ConfigureAwait(false);
                        }
                        messages.Add(new("tool", CodingLoopGuard.BoundToolResult(output, call.Name), ToolCallId: call.Id));
                        state = state with { Messages = messages.ToArray(), NextCall = index + 1, ProposalId = null,
                            HtmlRenderUsed = state.HtmlRenderUsed || call.Name == ClientToolNames.CodingRenderHtml,
                            WorkingState = call.Name == CodingWorkingStateTools.PlanTool ? state.WorkingState
                                : CodingWorkingStateReducer.ObserveToolResult(state.WorkingState ?? CodingWorkingState.Create(state.Task),
                                    call with { Id = state.Id + "/" + state.Round + "/" + index }, output) };
                        await repository.SaveAgentAsync(state, token).ConfigureAwait(false);
                    }
                    state = state with { PendingCalls = null, NextCall = 0 };
                }
                LmChatResult resultTurn;
                await using var steeringCall = repository.WatchSteering(state.RunId, token);
                try
                {
                await using var lease = await scheduler.AcquireAsync("coding-subagent", state.RunId, state.Kind == "document" ? GpuLeaseMode.Shared : GpuLeaseMode.CodingSecondary, steeringCall.Token).ConfigureAwait(false);
                var preparation = await runtime.EnsureModelPreparedAsync(state.Model, 0, null, steeringCall.Token).ConfigureAwait(false);
                var wasCompacted = false;
                if (CodingContextCompactor.Plan(messages, preparation.ContextLength) is { } compact)
                {
                    var summaryProgress = new ProgressPublisher(repository, state, "compaction");
                    activeProgress = summaryProgress;
                    var summary = await runtime.CompleteChatAsync(state.Model, compact.SummaryRequest, [], modelRole: "coding",
                        reasoningEffort: request.ReasoningEffort, nativeProgress: summaryProgress.PublishAsync, cancellationToken: steeringCall.Token).ConfigureAwait(false);
                    await summaryProgress.CompleteAsync(summary, request.ReasoningEffort, steeringCall.Token).ConfigureAwait(false);
                    state = state with { InputTokens = state.InputTokens + summary.InputTokens, OutputTokens = state.OutputTokens + summary.OutputTokens,
                        CachedInputTokens = state.CachedInputTokens + (summary.Metrics?.CachedPromptTokens ?? 0) };
                    messages = CodingContextCompactor.Complete(compact, summary.Content).ToList();
                    wasCompacted = true;
                }
                var context = ContextPlanner.Prepare(messages, preparation.ContextLength, null, preserveConversationPrefix: true);
                await repository.AppendEventAsync(state.RunId, RunEventTypes.ContextChanged,
                    new { agentId = state.Id, round = state.Round + 1, phase = "subagent",
                        estimatedInputTokens = ContextPlanner.EstimateTokens(context.Messages),
                        contextLimit = ContextPlanner.ComputeInputTokenBudget(preparation.ContextLength, null),
                        loadedFiles = state.WorkingState?.ActiveFiles.Count ?? 0, wasCompacted = wasCompacted || context.WasCompacted,
                        contextMode = "coding", preparationCompleted = true }, steeringCall.Token).ConfigureAwait(false);
                var turnProgress = new ProgressPublisher(repository, state, "subagent");
                activeProgress = turnProgress;
                messages = ModelRuntimeClient.PrepareLanguageBoundMessages(context.Messages).ToList();
                resultTurn = await runtime.CompleteChatAsync(state.Model, messages, tools.Select(t => t.ToLmDefinition()).ToArray(), modelRole: "coding",
                    reasoningEffort: request.ReasoningEffort,
                    nativeProgress: turnProgress.PublishAsync,
                    sessionCacheKey: CodingSubagentContext.CacheKey(request.SessionId, state.RunId, state.Id), cancellationToken: steeringCall.Token).ConfigureAwait(false);
                await turnProgress.CompleteAsync(resultTurn, request.ReasoningEffort, steeringCall.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (steeringCall.SteeringRequested && !token.IsCancellationRequested)
                {
                    throw new RunSteeringBoundaryException();
                }
                messages.Add(new("assistant", resultTurn.Content, ToolCalls: resultTurn.ToolCalls, ReasoningContent: resultTurn.ReasoningContent));
                state = state with { Messages = messages.ToArray(), PendingCalls = resultTurn.ToolCalls.Count == 0 ? null : resultTurn.ToolCalls.ToArray(), Round = state.Round + 1,
                    InputTokens = state.InputTokens + resultTurn.InputTokens, OutputTokens = state.OutputTokens + resultTurn.OutputTokens,
                    CachedInputTokens = state.CachedInputTokens + (resultTurn.Metrics?.CachedPromptTokens ?? 0) };
                if (resultTurn.ToolCalls.Count == 0)
                {
                    if (string.IsNullOrWhiteSpace(resultTurn.Content) || CodingCompletionGuard.IsActionAnnouncement(resultTurn.Content))
                    {
                        state = state with { IncompleteResponses = state.IncompleteResponses + 1 };
                        if (state.IncompleteResponses > 2) throw new InvalidOperationException("Subagent lieferte wiederholt nur eine Ankündigung oder kein Ergebnis. Der Teilauftrag wurde nicht als erledigt markiert.");
                        if (!messages.Any(m => m.Role == "system" && m.Content == CodingCompletionGuard.RepairPrompt))
                            messages.Add(new("system", CodingCompletionGuard.RepairPrompt));
                        state = state with { Messages = messages.ToArray() };
                        await repository.SaveAgentAsync(state, token).ConfigureAwait(false);
                        continue;
                    }
                    state = state with { Status = "completed", Result = resultTurn.Content };
                    break;
                }
                state = state with { IncompleteResponses = 0 };
                await repository.SaveAgentAsync(state, token).ConfigureAwait(false);
            }
        }
        catch (RunSteeringBoundaryException)
        {
            var messages = state.Messages.ToList();
            if (state.PendingCalls is not null)
                foreach (var abandoned in state.PendingCalls.Skip(state.NextCall))
                    messages.Add(new("tool", "{\"status\":\"not_executed\",\"errorCode\":\"run.steered\"}", ToolCallId: abandoned.Id));
            state = state with { Status = "steered", PendingCalls = null, ProposalId = null, NextCall = 0,
                Messages = messages.ToArray(), Result = "Subagent an sicherer Werkzeuggrenze durch neue Nutzereingabe angehalten. Bereits quittierte Änderungen bleiben erhalten. "
                    + string.Join("; ", state.WorkingState?.Evidence.TakeLast(8).Select(item => item.Summary) ?? []) };
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            if (activeProgress is not null) await activeProgress.StopAsync("interrupted", CancellationToken.None).ConfigureAwait(false);
            await repository.SaveAgentAsync(state, CancellationToken.None).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException)
        {
            // Cancellation stops generation immediately, but an already dispatched
            // local mutation keeps its reservation until the client acknowledges it.
            if (state.ProposalId is { } pending && await repository.HasProposalEventAsync(state.RunId, pending, CancellationToken.None).ConfigureAwait(false))
            {
                while (await repository.GetClientToolResultAsync(pending, CancellationToken.None).ConfigureAwait(false) is null)
                {
                    if (_shutdown.IsCancellationRequested)
                    {
                        await repository.SaveAgentAsync(state, CancellationToken.None).ConfigureAwait(false);
                        return;
                    }
                    var parent = await repository.GetAsync(state.RunId, CancellationToken.None).ConfigureAwait(false);
                    if (parent?.State is RunState.Cancelled or RunState.Failed or RunState.Completed) break;
                    await Task.Delay(150, CancellationToken.None).ConfigureAwait(false);
                }
            }
            state = state with { Status = "cancelled", Result = "Subagent gestoppt. Bereits ausgeführte Werkzeuge bleiben im Verlauf." };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { state = state with { Status = "failed", Result = exception.Message }; }
        if (activeProgress is not null) await activeProgress.StopAsync(state.Status, CancellationToken.None).ConfigureAwait(false);
        await repository.SaveAgentAsync(state, CancellationToken.None).ConfigureAwait(false);
        await repository.AppendEventAsync(state.RunId, RunEventTypes.ServerToolCompleted,
            new { agentId = state.Id, tool = state.Kind == "document" ? WorkspaceTools.DocumentAgent : CodingSubagentTools.Start, toolCallId = state.Id, target = state.Kind == "document" ? "Dokumenten-Agent" : "Subagent GPU1", success = state.Status == "completed", result = new { state.Status, state.Result } }, CancellationToken.None).ConfigureAwait(false);
        await repository.AppendEventAsync(state.RunId, RunEventTypes.ModelGeneration,
            new { agentId = state.Id, round = state.Round, phase = "subagent", state = state.Status }, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<CodingDeepResearchExecution> ExecuteResearchAsync(CodingSubagentState state, RunRequest request,
        JsonElement arguments, IReadOnlyList<AgentToolSpec> tools, CancellationToken token)
    {
        var search = catalog.Resolve("web.search", tools);
        var fetch = catalog.Resolve("web.fetch", tools);
        var ordinal = 0;
        var modelOrdinal = 0;
        var preparation = await runtime.EnsureModelPreparedAsync(state.Model, 0, null, token).ConfigureAwait(false);
        var researchStart = (await repository.GetAsync(state.RunId, token).ConfigureAwait(false))!.LastEventId;
        try
        {
        var research = await CodingDeepResearchPipeline.ExecuteAsync(
            arguments.GetProperty("task").GetString()!,
            arguments.TryGetProperty("maximumSearches", out var searches) ? searches.GetInt32() : 3,
            arguments.TryGetProperty("maximumSources", out var sources) ? sources.GetInt32() : 4,
            state.Model, preparation.ContextLength, CodingDeepResearchPipeline.MaximumModelCalls, CodingDeepResearchPipeline.MaximumToolCalls,
            search, fetch,
            async (turn, cancellation) =>
            {
                await using var lease = await scheduler.AcquireAsync("coding-subagent-research", state.RunId,
                    GpuLeaseMode.CodingSecondary, cancellation).ConfigureAwait(false);
                var messages = turn.Messages.ToList();
                CodingAgentPolicy.EnsureReasoningLanguage(messages);
                var progress = new ProgressPublisher(repository, state, "research-" + state.Round + "-" + modelOrdinal++);
                await using var steeringCall = repository.WatchSteering(state.RunId, cancellation);
                try
                {
                    var result = await runtime.CompleteChatAsync(state.Model, messages, turn.Tools, turn.MaximumOutputTokens,
                        modelRole: "coding", reasoningEffort: request.ReasoningEffort, requireToolCall: turn.RequireToolCall,
                        requiredToolName: turn.RequiredToolName, requiredContextLength: preparation.ContextLength,
                        nativeProgress: progress.PublishAsync,
                        structuredToolOnly: turn.DisableReasoning, cancellationToken: steeringCall.Token).ConfigureAwait(false);
                    await progress.CompleteAsync(result, request.ReasoningEffort, cancellation).ConfigureAwait(false);
                    return result;
                }
                catch (OperationCanceledException) when (steeringCall.SteeringRequested && !cancellation.IsCancellationRequested)
                {
                    await progress.StopAsync("steered", CancellationToken.None).ConfigureAwait(false);
                    throw new RunSteeringBoundaryException();
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    await progress.StopAsync(exception is OperationCanceledException ? "cancelled" : "failed", CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            },
            async (call, cancellation) =>
            {
                var spec = catalog.Resolve(call.Name, tools);
                catalog.Validate(spec, call.Arguments);
                if (call.Name is not ("web.search" or "web.fetch")) throw new ArgumentException("Ungültiges Recherchewerkzeug.");
                var id = state.Id + "/research/" + state.Round + "/" + ordinal++;
                if (!await repository.TryJournalToolDispatchAsync(state.RunId, RunEventTypes.ServerToolStarted,
                    new { agentId = state.Id, tool = call.Name, toolCallId = id, target = state.Kind == "document" ? "Dokumenten-Agent" : "Subagent GPU1", arguments = call.Arguments }, cancellation).ConfigureAwait(false))
                    throw new RunSteeringBoundaryException();
                var result = await executor.ExecuteAsync(call.Name, call.Arguments, state.RunId, cancellation).ConfigureAwait(false);
                await repository.AppendEventAsync(state.RunId, RunEventTypes.ServerToolCompleted,
                    new { agentId = state.Id, tool = call.Name, toolCallId = id, target = state.Kind == "document" ? "Dokumenten-Agent" : "Subagent GPU1", success = result.Succeeded, result = result.Result }, cancellation).ConfigureAwait(false);
                return result;
            }, catalog.Validate,
            (progress, cancellation) => repository.AppendEventAsync(state.RunId, RunEventTypes.ServerToolStarted,
                new { agentId = state.Id, tool = state.Kind == "document" ? WorkspaceTools.DocumentAgent : CodingSubagentTools.Start, toolCallId = state.Id, target = state.Kind == "document" ? "Dokumenten-Agent" : "Subagent GPU1", message = "Recherche läuft: " + progress.State }, cancellation),
            token).ConfigureAwait(false);
        return research;
        }
        catch (RunSteeringBoundaryException)
        {
            var receipt = await repository.GetInterruptedResearchReceiptAsync(state.RunId, researchStart, token).ConfigureAwait(false);
            return new(new(JsonSerializer.Deserialize<JsonElement>(receipt), [], Succeeded: false,
                ErrorCode: "run.steered", ErrorMessage: "Recherche umgelenkt; bereits quittierte Belege bleiben erhalten."), modelOrdinal, ordinal, 0, 0);
        }
    }

    /// <summary>All child progress is journalled under its worker id, never in the parent's text/metric stream.</summary>
    internal sealed class ProgressPublisher(RunRepository repository, CodingSubagentState agent, string phase)
    {
        private readonly StringBuilder _reasoning = new();
        private readonly StringBuilder _content = new();
        private bool _firstReasoning = true;
        private bool _firstContent = true;
        private bool _reasoningPublished;
        private bool _contentPublished;
        private bool _completed;
        private readonly int _round = agent.Round + 1;

        internal async ValueTask PublishAsync(ModelRuntimeProgress progress, CancellationToken token)
        {
            if (progress.State == "generationRetry")
            {
                _firstReasoning = true;
                _firstContent = true;
                _reasoning.Clear();
                _content.Clear();
            }
            if (!string.IsNullOrEmpty(progress.ReasoningDelta))
            {
                _reasoning.Append(progress.ReasoningDelta);
                await repository.AppendEventAsync(agent.RunId, RunEventTypes.ReasoningDelta,
                    new { agentId = agent.Id, round = _round, phase, delta = progress.ReasoningDelta,
                        replaceFrom = _firstReasoning ? (int?)0 : null, state = "running" }, token).ConfigureAwait(false);
                _firstReasoning = false;
                _reasoningPublished = true;
            }
            if (!string.IsNullOrEmpty(progress.ContentDelta))
            {
                _content.Append(progress.ContentDelta);
                await repository.AppendEventAsync(agent.RunId, RunEventTypes.TextDelta,
                    new { agentId = agent.Id, round = _round, phase, delta = progress.ContentDelta,
                        replaceFrom = _firstContent ? (int?)0 : null, state = "running" }, token).ConfigureAwait(false);
                _firstContent = false;
                _contentPublished = true;
            }
            if (progress.State is "reasoningDelta" or "contentDelta") return;
            await repository.AppendEventAsync(agent.RunId, RunEventTypes.ModelGeneration,
                new { agentId = agent.Id, round = _round, phase, state = progress.State,
                    toolName = progress.ToolName, argumentCharacters = progress.ArgumentCharacters,
                    promptProgress = progress.PromptProgress, promptTokens = progress.PromptTokens,
                    processedPromptTokens = progress.ProcessedPromptTokens, generatedTokens = progress.GeneratedTokens,
                    tokensPerSecond = progress.TokensPerSecond, currentTokens = progress.CurrentTokens,
                    attempt = progress.Attempt, failureKind = progress.FailureKind,
                    toolArgumentsJsonComplete = progress.ToolArgumentsJsonComplete, contentCharacters = progress.ContentCharacters,
                    finishObserved = progress.FinishObserved, cachedPromptTokens = progress.CachedPromptTokens }, token).ConfigureAwait(false);
        }

        internal async Task CompleteAsync(LmChatResult result, string? reasoningEffort, CancellationToken token)
        {
            // Final authoritative replacements also cover non-streaming providers,
            // retry divergence, and a short last fragment not followed by another delta.
            if (_reasoningPublished || !string.IsNullOrEmpty(result.ReasoningContent))
                await repository.AppendEventAsync(agent.RunId, RunEventTypes.ReasoningDelta,
                    new { agentId = agent.Id, round = _round, phase, delta = result.ReasoningContent ?? _reasoning.ToString(),
                        replaceFrom = 0, state = "completed" }, token).ConfigureAwait(false);
            if (_contentPublished || !string.IsNullOrEmpty(result.Content))
                await repository.AppendEventAsync(agent.RunId, RunEventTypes.TextDelta,
                    new { agentId = agent.Id, round = _round, phase, delta = result.Content ?? _content.ToString(),
                        replaceFrom = 0, state = "completed" }, token).ConfigureAwait(false);
            await repository.AppendEventAsync(agent.RunId, RunEventTypes.CodingMetrics,
                new { agentId = agent.Id, round = _round, phase, metrics = result.Metrics
                    ?? new ModelTurnMetrics(InputTokens: result.InputTokens, OutputTokens: result.OutputTokens), reasoningEffort }, token).ConfigureAwait(false);
            await repository.AppendEventAsync(agent.RunId, RunEventTypes.ModelGeneration,
                new { agentId = agent.Id, round = _round, phase, state = "generationCompleted", finishObserved = true }, token).ConfigureAwait(false);
            _completed = true;
        }

        internal async Task StopAsync(string status, CancellationToken token)
        {
            if (_completed) return;
            if (_reasoningPublished)
                await repository.AppendEventAsync(agent.RunId, RunEventTypes.ReasoningDelta,
                    new { agentId = agent.Id, round = _round, phase, delta = _reasoning.ToString(), replaceFrom = 0, state = status }, token).ConfigureAwait(false);
            if (_contentPublished)
                await repository.AppendEventAsync(agent.RunId, RunEventTypes.TextDelta,
                    new { agentId = agent.Id, round = _round, phase, delta = _content.ToString(), replaceFrom = 0, state = status }, token).ConfigureAwait(false);
        }
    }

    private static AgentToolExecutionResult Receipt(object value) => new(JsonSerializer.SerializeToElement(value), []);
    private static AgentToolExecutionResult Failure(string message, string code = "agent.busy") => new(JsonSerializer.SerializeToElement(new { success = false, message }), [], Succeeded: false, ErrorCode: code, ErrorMessage: message);
}
