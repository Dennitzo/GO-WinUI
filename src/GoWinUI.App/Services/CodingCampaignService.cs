using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GoAi.Contracts;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using Microsoft.Extensions.Logging;

namespace GoWinUI.App.Services;

public sealed record CodingCampaignView(
    string Id,
    string SessionId,
    string DefinitionId,
    string Title,
    string Description,
    string WorkspaceName,
    string ModelId,
    string Status,
    string Phase,
    int Iteration,
    string? Challenge,
    string? Error,
    int RestartCount,
    IReadOnlyList<string> ValidationIssues,
    IReadOnlyList<CodingProofVerificationResult> Proofs,
    DateTimeOffset UpdatedAt);

public sealed record CodingCampaignUiSnapshot(
    IReadOnlyList<CodingCampaignDescriptor> Definitions,
    CodingCampaignView? ActiveCampaign);

public sealed class CodingCampaignService(
    ICodingCampaignRepository repository,
    IChatRepository chats,
    IChatArtifactRepository chatArtifacts,
    CodingCampaignCatalog catalog,
    ICodingCampaignAgent assistant,
    SettingsCoordinator settings,
    ILogger<CodingCampaignService> logger,
    CodingSolutionPdfExporter? solutionPdfExporter = null) : IDisposable
{
    private const int MaximumPublishedTextLength = 750_000;
    private const long MaximumPlotLength = 50L * 1024 * 1024;
    private const string ProcessReportDiffRule = """
        Behaupte unter **Aktion** ausschließlich Änderungen, die du in genau diesem Lauf selbst vorgenommen hast.
        Bereits zuvor vorhandene Dirty-Worktree-Änderungen, erneut ausgeführte Prüfungen sowie neu gerenderte Plots,
        Bilder, PDFs, Logs und andere generierte Ausgaben sind kein Codefortschritt. GO veröffentlicht den Bericht nur,
        wenn der isolierte Lauf-Diff tatsächlich hinzugefügte oder entfernte Zeilen in einer Quell- oder Testdatei enthält.
        Rein kosmetische Umbenennungen, Formatierung, Kommentare oder bedeutungslose Variablenextraktionen sind kein
        fachlicher Fortschritt. Verändere den echten Git-Index niemals; insbesondere sind git add, reset, restore,
        checkout und commit verboten. GO ermittelt den Lauf-Diff selbst.
        """;
    private const string AutonomousChallenge = "Autonom gewählter nächster Arbeitsschritt";
    private const string ProcessReportInstruction = """
        Verfasse für diesen AI-Lauf eine kurze fachliche Prozessmeldung als sichtbare Abschlussantwort. Beginne exakt
        mit dieser Struktur und füge keine technische Titel- oder Metadatenzeile davor ein:

        ### Prozessbericht
        **Gegenstand:** Konkretes untersuchtes Modell, Bauteil, Programmteil oder Problem.
        **Aktion:** Konkrete gerade ausgeführte Änderung, Berechnung, Analyse oder Verifikation.
        **Annahmen:** Die für diesen Schritt maßgeblichen fachlichen Annahmen.
        **Annahmenänderung:** `Unverändert` oder `Geändert: <bisher> → <neu>; Grund: <belegbare Erkenntnis>`.
        **Prüfung:** Tatsächlich ausgeführte Tests, Beweise, Builds oder Laufzeitprüfungen.

        Schreibe keine interne Gedankenkette. Bei einem neuen Lauf entsteht eine neue Prozessmeldung. Vergleiche die
        Annahmen mit dem vorherigen Prozessbericht und kennzeichne jede Änderung ausdrücklich.
        """;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> SolutionExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt", ".tex", ".json",
    };
    private static readonly HashSet<string> PlotExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg",
    };
    private static readonly Action<ILogger, string, int, double, Exception?> WorkflowRetryScheduled =
        LoggerMessage.Define<string, int, double>(
            LogLevel.Warning,
            new EventId(5800, nameof(WorkflowRetryScheduled)),
            "Coding workflow {WorkflowId} hit a recoverable failure. Retry {RetryCount} starts in {DelaySeconds} seconds.");
    private static readonly Action<ILogger, string, Exception?> UiUpdateDetached = LoggerMessage.Define<string>(
        LogLevel.Debug, new EventId(5801, nameof(UiUpdateDetached)), "Coding workflow UI update detached at {Stage}.");
    private static readonly Action<ILogger, string, Exception?> OutputPublicationFailed = LoggerMessage.Define<string>(
        LogLevel.Warning, new EventId(5802, nameof(OutputPublicationFailed)), "Coding workflow output {Output} could not be published.");

    private readonly SemaphoreSlim _controlGate = new(1, 1);
    private readonly object _sinkLock = new();
    private CancellationTokenSource? _loopCancellation;
    private Task? _loopTask;
    private Guid? _activeCampaignId;
    private long _loopGeneration;
    private Func<GoAiAssistantUpdate, Task>? _assistantSink;
    private Func<CodingCampaignUiSnapshot, Task>? _campaignSink;
    private int _clientStartPrepared;
    private int _disposed;

    public bool IsRunning => _loopTask is { IsCompleted: false };

    public void AttachSinks(
        Func<GoAiAssistantUpdate, Task> assistantSink,
        Func<CodingCampaignUiSnapshot, Task> campaignSink)
    {
        lock (_sinkLock)
        {
            _assistantSink = assistantSink;
            _campaignSink = campaignSink;
        }
    }

    public void DetachSinks()
    {
        lock (_sinkLock)
        {
            _assistantSink = null;
            _campaignSink = null;
        }
    }

    public async Task<CodingCampaignUiSnapshot> GetSnapshotAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var state = await repository.GetForSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return new(catalog.List(), state is null ? null : BuildView(state));
    }

    public async Task PrepareForClientStartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.CompareExchange(ref _clientStartPrepared, 1, 0) != 0)
        {
            return;
        }

        await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopLoopCoreAsync(markStopped: false, cancellationToken).ConfigureAwait(false);
            foreach (var state in await repository.ListAsync(cancellationToken).ConfigureAwait(false))
            {
                await MarkStoppedAsync(
                    state,
                    "Der Client wurde neu gestartet; geladene Workflows starten immer gestoppt.",
                    cancellationToken).ConfigureAwait(false);
                await PublishPlotsAsync(
                    state,
                    "Geladener Workflow-Stand",
                    cancellationToken).ConfigureAwait(false);
            }
            _ = await chats.DeleteInternalMessagesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Exchange(ref _clientStartPrepared, 0);
            throw;
        }
        finally
        {
            _controlGate.Release();
        }
    }

    public async Task<CodingCampaignUiSnapshot> SelectAsync(
        Guid sessionId,
        string definitionId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopLoopCoreAsync(markStopped: true, cancellationToken).ConfigureAwait(false);
            var session = await RequireCodingSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            var definition = catalog.GetRequired(definitionId);
            var existing = await repository.GetForSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            var workspace = Path.GetFullPath(session.WorkspacePath!);
            var canReuse = existing is not null
                && existing.DefinitionId.Equals(definitionId, StringComparison.OrdinalIgnoreCase)
                && Path.GetFullPath(existing.WorkspacePath).Equals(workspace, StringComparison.OrdinalIgnoreCase);
            if (existing is not null && !canReuse)
            {
                await repository.DeleteForSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
                existing = null;
            }

            var now = DateTimeOffset.UtcNow;
            var iteration = Math.Max(canReuse ? existing!.Iteration : 0, definition.ReadIteration(workspace));
            var hasFoundation = definition.HasFoundation(workspace);
            var state = canReuse
                ? existing! with
                {
                    Title = definition.Descriptor.Title,
                    WorkspacePath = workspace,
                    WorkspaceFingerprint = session.WorkspaceFingerprint ?? string.Empty,
                    ModelId = settings.Current.SelectedCodingModel,
                    Status = CodingCampaignStatus.Stopped,
                    LastError = null,
                    UpdatedAt = now,
                }
                : new CodingCampaignState(
                    Guid.NewGuid(), session.Id, definition.Descriptor.Id, definition.Descriptor.Title,
                    workspace, session.WorkspaceFingerprint ?? string.Empty, settings.Current.SelectedCodingModel,
                    CodingCampaignStatus.Stopped,
                    hasFoundation ? CodingCampaignPhase.Iteration : CodingCampaignPhase.Bootstrap,
                    iteration,
                    hasFoundation ? AutonomousChallenge : "Projektgrundlage erstellen",
                    null, "[]", 0, now, now);

            await repository.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            await PublishJournalMessagesAsync(state, cancellationToken).ConfigureAwait(false);
            await PublishExistingSolutionsAsync(state, definition, cancellationToken).ConfigureAwait(false);
            await PublishPlotsAsync(state, "Geladener Workflow-Stand", cancellationToken).ConfigureAwait(false);
            await PublishSnapshotAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return await GetSnapshotAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _controlGate.Release();
        }
    }

    public async Task<CodingCampaignUiSnapshot> RunAsync(
        Guid sessionId,
        string? instruction = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var normalizedInstruction = NormalizeInstruction(instruction);
            if (IsRunning)
            {
                if (normalizedInstruction is null)
                {
                    return await GetSnapshotAsync(sessionId, cancellationToken).ConfigureAwait(false);
                }

                // A new composer or voice instruction replaces the currently
                // executing workflow step. It must never escape into General AI.
                await StopLoopCoreAsync(markStopped: false, cancellationToken).ConfigureAwait(false);
            }

            var state = await repository.GetForSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Lade zuerst einen Coding-Workflow über Workflows.");
            var session = await RequireCodingSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (!Path.GetFullPath(session.WorkspacePath!).Equals(Path.GetFullPath(state.WorkspacePath), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Der Workspace wurde geändert. Lade den Coding-Workflow für diesen Workspace erneut.");
            }

            var definition = catalog.GetRequired(state.DefinitionId);
            var hasFoundation = definition.HasFoundation(state.WorkspacePath);
            var phase = hasFoundation
                ? state.Phase == CodingCampaignPhase.Bootstrap ? CodingCampaignPhase.Iteration : state.Phase
                : CodingCampaignPhase.Bootstrap;
            state = state with
            {
                ModelId = settings.Current.SelectedCodingModel,
                Status = CodingCampaignStatus.Running,
                Phase = phase,
                CurrentChallenge = phase switch
                {
                    CodingCampaignPhase.Bootstrap => "Projektgrundlage erstellen",
                    CodingCampaignPhase.Correction => "Validierungsfehler selbstständig beheben",
                    _ => AutonomousChallenge,
                },
                LastError = null,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await repository.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            if (normalizedInstruction is not null)
            {
                await PublishUserInstructionAsync(
                    state.SessionId,
                    normalizedInstruction,
                    cancellationToken).ConfigureAwait(false);
            }
            LaunchLoop(state.Id, normalizedInstruction);
            await PublishSnapshotAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return await GetSnapshotAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _controlGate.Release();
        }
    }

    public async Task<CodingCampaignUiSnapshot> StopAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await repository.GetForSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            var hadLocalLoop = IsRunning;
            await StopLoopCoreAsync(markStopped: false, cancellationToken).ConfigureAwait(false);
            if (!hadLocalLoop && state?.Status == CodingCampaignStatus.Running)
            {
                // A restored server run can be active even though this process has
                // no local workflow task or active campaign id yet.
                await assistant.CancelCurrentAndWaitAsync(cancellationToken).ConfigureAwait(false);
            }
            state = await repository.GetForSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (state is not null)
            {
                await MarkStoppedAsync(state, "Der Workflow wurde vom Nutzer gestoppt.", cancellationToken).ConfigureAwait(false);
            }
            return await GetSnapshotAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _controlGate.Release();
        }
    }

    public async Task<bool> StopForNewPromptAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var state = await repository.GetForSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (state?.Status != CodingCampaignStatus.Running && !IsRunning)
        {
            return false;
        }
        _ = await StopAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private void LaunchLoop(Guid campaignId, string? initialInstruction)
    {
        _loopCancellation?.Dispose();
        _loopCancellation = new CancellationTokenSource();
        _activeCampaignId = campaignId;
        var generation = Interlocked.Increment(ref _loopGeneration);
        _loopTask = Task.Run(
            () => RunLoopAsync(campaignId, generation, initialInstruction, _loopCancellation.Token),
            CancellationToken.None);
    }

    private async Task RunLoopAsync(
        Guid campaignId,
        long generation,
        string? initialInstruction,
        CancellationToken cancellationToken)
    {
        var pendingInstruction = initialInstruction;
        var supervisorFailureCount = 0;
        var consecutiveNoProgressSteps = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!IsCurrentLoop(campaignId, generation)) return;
                try
                {
                    var state = await repository.GetAsync(campaignId, cancellationToken).ConfigureAwait(false);
                    if (state is null)
                    {
                        // The workflow was explicitly removed. There is no authoritative state to continue.
                        return;
                    }
                    if (state.Status != CodingCampaignStatus.Running)
                    {
                        // A user action persisted the stop before cancellation reached this task.
                        return;
                    }

                    var definition = catalog.GetRequired(state.DefinitionId);
                    var session = await chats.GetSessionAsync(state.SessionId, cancellationToken).ConfigureAwait(false);
                    if (session is null)
                    {
                        await MarkStoppedAsync(
                            state,
                            "Die zugehörige Coding-Sitzung wurde gelöscht.",
                            cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    ValidateWorkspaceBinding(session, state);

                    if (state.Phase == CodingCampaignPhase.Validation)
                    {
                        await ValidateAndAdvanceAsync(state, definition, cancellationToken).ConfigureAwait(false);
                        supervisorFailureCount = 0;
                        continue;
                    }

                    var challenge = state.Phase switch
                    {
                        CodingCampaignPhase.Bootstrap => "Projektgrundlage erstellen",
                        CodingCampaignPhase.Correction => "Validierungsfehler selbstständig beheben",
                        _ => AutonomousChallenge,
                    };
                    state = state with
                    {
                        ModelId = settings.Current.SelectedCodingModel,
                        CurrentChallenge = challenge,
                        Status = CodingCampaignStatus.Running,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    };
                    await repository.SaveAsync(state, cancellationToken).ConfigureAwait(false);
                    await PublishSnapshotAsync(state.SessionId, cancellationToken).ConfigureAwait(false);

                    var prompt = ResolvePrompt(
                        definition,
                        state,
                        challenge,
                        pendingInstruction);
                    var response = await ExecuteAgentRunAsync(
                        state, definition, prompt, challenge, generation, cancellationToken).ConfigureAwait(false);
                    if (!IsCurrentLoop(campaignId, generation)) return;
                    if (response.Status != MessageStatus.Completed)
                    {
                        await chats.DeleteMessageAsync(response.Id, CancellationToken.None).ConfigureAwait(false);
                        throw new InvalidOperationException(
                            response.Error ?? "Der Coding-Agent hat den Workflow-Schritt nicht abgeschlossen.");
                    }

                    if (!HasChangedCodeLines(response.CodeDiff))
                    {
                        consecutiveNoProgressSteps++;
                        await chats.DeleteMessageAsync(response.Id, cancellationToken).ConfigureAwait(false);

                        state = await repository.GetAsync(campaignId, cancellationToken).ConfigureAwait(false)
                            ?? throw new InvalidOperationException("Der gespeicherte Coding-Workflow wurde während des Laufs entfernt.");
                        state = state with
                        {
                            CurrentChallenge = AutonomousChallenge,
                            LastError = null,
                            UpdatedAt = DateTimeOffset.UtcNow,
                        };
                        await repository.SaveAsync(state, cancellationToken).ConfigureAwait(false);
                        await PublishSnapshotAsync(state.SessionId, cancellationToken).ConfigureAwait(false);
                        supervisorFailureCount = 0;
                        await Task.Delay(NoProgressDelay(consecutiveNoProgressSteps), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    await chats.SetMessageVisibilityAsync(
                        response.Id,
                        ChatMessageVisibility.Visible,
                        cancellationToken).ConfigureAwait(false);
                    response = response with { Visibility = ChatMessageVisibility.Visible };
                    await PublishAssistantUpdateAsync(new(
                        GoAiAssistantUpdateKind.Completed,
                        response,
                        await chatArtifacts.ListForMessageAsync(response.Id, cancellationToken).ConfigureAwait(false),
                        session,
                        "Fertig")).ConfigureAwait(false);

                    // Consume a user instruction only after a completed model run. A transient error therefore retries
                    // the same instruction instead of silently replacing it with an autonomous continuation.
                    pendingInstruction = null;
                    consecutiveNoProgressSteps = 0;
                    state = await repository.GetAsync(campaignId, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("Der gespeicherte Coding-Workflow wurde während des Laufs entfernt.");
                    state = state with
                    {
                        Phase = CodingCampaignPhase.Validation,
                        LastError = null,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    };
                    await repository.SaveAsync(state, cancellationToken).ConfigureAwait(false);
                    await PublishSnapshotAsync(state.SessionId, cancellationToken).ConfigureAwait(false);
                    await PublishPlotsAsync(state, "Aktueller Teststand", cancellationToken).ConfigureAwait(false);
                    supervisorFailureCount = 0;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    supervisorFailureCount = await RecordRecoverableFailureAsync(
                        campaignId,
                        exception,
                        supervisorFailureCount,
                        cancellationToken).ConfigureAwait(false);
                    var delay = RetryDelay(supervisorFailureCount);
                    WorkflowRetryScheduled(
                        logger,
                        campaignId.ToString("D"),
                        supervisorFailureCount,
                        delay.TotalSeconds,
                        exception);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stop via composer, replacement prompt or application shutdown.
        }
        finally
        {
            if (IsCurrentLoop(campaignId, generation))
            {
                _activeCampaignId = null;
            }
        }
    }

    private async Task ValidateAndAdvanceAsync(
        CodingCampaignState state,
        ICodingCampaignDefinition definition,
        CancellationToken cancellationToken)
    {
        CodingCampaignValidationResult validation;
        try
        {
            validation = await definition.ValidateAsync(state.WorkspacePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            // Model-authored workspace artifacts may temporarily be malformed. Convert those defects into explicit
            // correction input; a validator exception must never terminate the continuous supervisor.
            validation = new CodingCampaignValidationResult(
                false,
                [$"Die unabhängige Workflow-Abnahme konnte den aktuellen Workspace-Stand nicht vollständig prüfen: {VisibleError(exception)}"],
                []);
        }

        state = state with
        {
            ValidationJson = SerializeValidation(validation),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await repository.SaveAsync(state, cancellationToken).ConfigureAwait(false);

        if (!validation.IsValid)
        {
            state = state with
            {
                Phase = CodingCampaignPhase.Correction,
                CurrentChallenge = "Validierungsfehler selbstständig beheben",
                LastError = null,
                RestartCount = 0,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await repository.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            await PublishSnapshotAsync(state.SessionId, cancellationToken).ConfigureAwait(false);
            return;
        }

        await PublishVerifiedSolutionsAsync(state, definition, cancellationToken).ConfigureAwait(false);
        await PublishPlotsAsync(state, "Verifizierter Teststand", cancellationToken).ConfigureAwait(false);
        await PublishValidationMessageAsync(
            state,
            string.IsNullOrWhiteSpace(state.CurrentChallenge) ? AutonomousChallenge : state.CurrentChallenge,
            validation,
            cancellationToken).ConfigureAwait(false);

        var nextIteration = Math.Max(state.Iteration + 1, definition.ReadIteration(state.WorkspacePath));
        state = state with
        {
            Iteration = nextIteration,
            Phase = CodingCampaignPhase.Iteration,
            CurrentChallenge = AutonomousChallenge,
            Status = CodingCampaignStatus.Running,
            LastError = null,
            RestartCount = 0,
            ValidationJson = SerializeValidation(validation),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await repository.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        await PublishSnapshotAsync(state.SessionId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> RecordRecoverableFailureAsync(
        Guid campaignId,
        Exception exception,
        int localFailureCount,
        CancellationToken cancellationToken)
    {
        var retryCount = Math.Max(1, localFailureCount + 1);
        try
        {
            _ = await chats.DeleteInternalMessagesAsync(CancellationToken.None).ConfigureAwait(false);
            var state = await repository.GetAsync(campaignId, cancellationToken).ConfigureAwait(false);
            if (state is null || state.Status != CodingCampaignStatus.Running)
            {
                return retryCount;
            }

            retryCount = Math.Max(retryCount, state.RestartCount + 1);
            state = state with
            {
                Status = CodingCampaignStatus.Running,
                LastError = VisibleError(exception),
                RestartCount = retryCount,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await repository.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            await PublishSnapshotAsync(state.SessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception persistenceException) when (persistenceException is not OutOfMemoryException)
        {
            // A transient database/UI failure must not tear down the in-memory supervisor. The next cycle retries
            // loading and persisting the authoritative workflow state.
            WorkflowRetryScheduled(
                logger,
                campaignId.ToString("D"),
                retryCount,
                RetryDelay(retryCount).TotalSeconds,
                persistenceException);
        }
        return retryCount;
    }

    private async Task<ChatMessage> ExecuteAgentRunAsync(
        CodingCampaignState workflow,
        ICodingCampaignDefinition definition,
        string prompt,
        string challenge,
        long generation,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var iteration = new CodingCampaignIteration(
            Guid.NewGuid(), workflow.Id, workflow.Iteration, workflow.Phase, challenge, null,
            "running", null, workflow.ValidationJson, now, now);
        await repository.SaveIterationAsync(iteration, cancellationToken).ConfigureAwait(false);
        try
        {
            async Task HandleAgentUpdateAsync(GoAiAssistantUpdate update)
            {
                if (!IsCurrentLoop(workflow.Id, generation)) return;
                // Keep the model-authored process report invisible until the completed run has an isolated
                // source/test-line diff. Coding traces and diff panels remain live while the model works.
                if (update.Kind is not (GoAiAssistantUpdateKind.CodingTraceChanged
                    or GoAiAssistantUpdateKind.CodeDiffChanged))
                {
                    return;
                }
                await PublishAssistantUpdateAsync(update).ConfigureAwait(false);
                if (!definition.PublishSolutionsOnlyAfterValidation && MayHaveProducedSolution(update))
                {
                    await PublishSolutionsAsync(
                        workflow,
                        definition,
                        "Lösung aus Workflow",
                        cancellationToken).ConfigureAwait(false);
                }
            }

            var message = await assistant.SendWorkflowStepAsync(
                workflow.SessionId,
                prompt,
                AssistantCoordinator.CreateToolMatch("code", prompt),
                HandleAgentUpdateAsync,
                cancellationToken).ConfigureAwait(false);
            iteration = iteration with
            {
                AssistantMessageId = message.Id,
                Status = message.Status.ToString().ToLowerInvariant(),
                Error = message.Error,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await repository.SaveIterationAsync(iteration, CancellationToken.None).ConfigureAwait(false);
            return message;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            iteration = iteration with
            {
                Status = exception is OperationCanceledException ? "cancelled" : "failed",
                Error = exception.Message,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await repository.SaveIterationAsync(iteration, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task PublishValidationMessageAsync(
        CodingCampaignState state,
        string challenge,
        CodingCampaignValidationResult validation,
        CancellationToken cancellationToken)
    {
        var proofCount = validation.Proofs.Count(static proof => proof.Passed);
        var proofText = proofCount == 0 ? "Keine neuen maschinengeprüften Beweise." : $"{proofCount} Beweisprüfungen bestanden.";
        var content = $"**Workflow-Schritt verifiziert**\n\nVersuch {state.Iteration + 1}\n\n{proofText}\n\nDer Workflow setzt die Untersuchung autonom fort.";
        await PublishStaticMessageAsync(
            state.SessionId,
            content,
            new ToolExecutionInfo("coding.workflow.validation", state.Title, "completed", $"Versuch {state.Iteration + 1}"),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishExistingSolutionsAsync(
        CodingCampaignState state,
        ICodingCampaignDefinition definition,
        CancellationToken cancellationToken)
    {
        if (definition.PublishSolutionsOnlyAfterValidation)
        {
            CodingCampaignValidationResult validation;
            try
            {
                validation = await definition.ValidateAsync(state.WorkspacePath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                validation = new CodingCampaignValidationResult(
                    false,
                    [$"Die unabhängige Prüfung vorhandener Lösungen ist fehlgeschlagen: {VisibleError(exception)}"],
                    []);
            }

            state = state with
            {
                ValidationJson = SerializeValidation(validation),
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await repository.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            if (!validation.IsValid)
            {
                return;
            }
        }
        await PublishSolutionsAsync(state, definition, "Vorhandene Lösung", cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishVerifiedSolutionsAsync(
        CodingCampaignState state,
        ICodingCampaignDefinition definition,
        CancellationToken cancellationToken) =>
        await PublishSolutionsAsync(state, definition, "Verifizierte Lösung", cancellationToken).ConfigureAwait(false);

    private async Task PublishJournalMessagesAsync(
        CodingCampaignState state,
        CancellationToken cancellationToken)
    {
        var entries = await CodingWorkflowMessageJournal.ReadAsync(state.WorkspacePath, cancellationToken).ConfigureAwait(false);
        foreach (var entry in entries.OrderBy(static item => item.CreatedAt))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var publicationKey = "journal/" + entry.Id;
            var sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                entry.Kind + "\n" + entry.Title + "\n" + entry.Content)));
            if (await repository.IsSolutionPublishedAsync(state.Id, publicationKey, sha256, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }
            var message = await PublishStaticMessageAsync(
                state.SessionId,
                $"**{entry.Title}**\n\n{entry.Content}",
                new ToolExecutionInfo(
                    "coding.workflow.history",
                    entry.Kind,
                    "completed",
                    entry.CreatedAt.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture)),
                cancellationToken).ConfigureAwait(false);
            await repository.SaveSolutionPublicationAsync(
                state.Id, publicationKey, sha256, message.Id, message.CreatedAt, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PublishSolutionsAsync(
        CodingCampaignState state,
        ICodingCampaignDefinition definition,
        string heading,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(state.WorkspacePath, "solutions");
        if (!Directory.Exists(root)) return;
        var publishableDocuments = definition.GetPublishableSolutionDocuments(state.WorkspacePath);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(path => SolutionExtensions.Contains(Path.GetExtension(path)))
                     .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var relativePath = NormalizeRelativePath(state.WorkspacePath, path);
                if (publishableDocuments is not null && !publishableDocuments.Contains(relativePath))
                {
                    continue;
                }
                var snapshot = await ReadTextSnapshotAsync(path, cancellationToken).ConfigureAwait(false);
                if (snapshot is null || snapshot.Text.Length == 0) continue;
                var publicationKey = "solution/" + relativePath;
                var alreadyPublished = await repository.IsSolutionPublishedAsync(
                    state.Id,
                    publicationKey,
                    snapshot.Sha256,
                    cancellationToken).ConfigureAwait(false);
                if (!alreadyPublished)
                {
                    var text = snapshot.Text.Length <= MaximumPublishedTextLength
                        ? snapshot.Text
                        : snapshot.Text[..MaximumPublishedTextLength] + "\n\n[Darstellung im Chat gekürzt; die vollständige Lösung liegt im Workspace.]";
                    var documentHeading = definition.GetSolutionPublicationHeading(
                        state.WorkspacePath,
                        relativePath,
                        heading);
                    var message = await PublishStaticMessageAsync(
                        state.SessionId,
                        $"### {documentHeading}: {Path.GetFileName(path)}\n\n{text}",
                        new ToolExecutionInfo("coding.workflow.solution", relativePath, "completed", documentHeading),
                        cancellationToken).ConfigureAwait(false);
                    await repository.SaveSolutionPublicationAsync(
                        state.Id, publicationKey, snapshot.Sha256, message.Id, message.CreatedAt, cancellationToken).ConfigureAwait(false);
                }

                if (solutionPdfExporter is not null)
                {
                    _ = await solutionPdfExporter.EnsureCurrentAsync(
                        path,
                        sourceChanged: !alreadyPublished,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                OutputPublicationFailed(logger, path, exception);
            }
        }
    }

    internal static bool MayHaveProducedSolution(GoAiAssistantUpdate update)
    {
        if (update.Kind != GoAiAssistantUpdateKind.CodingTraceChanged
            || update.CodingTrace is not { Stage: "tool", Status: "completed", Tool: { } tool })
        {
            return false;
        }

        return tool is ClientToolNames.FileSystemWriteText
            or ClientToolNames.FileSystemReplaceText
            or ClientToolNames.FileSystemMove
            or ClientToolNames.FileSystemProposePatch
            or ClientToolNames.FileSystemProposeCreate
            or ClientToolNames.FileSystemProposeDelete
            or ClientToolNames.ProcessRun
            or ClientToolNames.ProcessRunPreset;
    }

    private async Task PublishPlotsAsync(
        CodingCampaignState state,
        string heading,
        CancellationToken cancellationToken)
    {
        foreach (var plotPath in FindPlots(state.WorkspacePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var snapshot = await ReadBinarySnapshotAsync(plotPath, cancellationToken).ConfigureAwait(false);
                if (snapshot is null) continue;
                var relativePath = NormalizeRelativePath(state.WorkspacePath, plotPath);
                var publicationKey = "plot/" + relativePath;
                if (await repository.IsSolutionPublishedAsync(
                        state.Id, publicationKey, snapshot.Sha256, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                var tool = new ToolExecutionInfo(
                    "coding.workflow.plot", relativePath, "completed", $"Versuch {state.Iteration + 1}");
                var message = await chats.AddMessageAsync(
                    state.SessionId,
                    ChatRole.Assistant,
                    $"### {heading}: {Path.GetFileName(plotPath)}\n\n{state.Title} · Versuch {state.Iteration + 1}",
                    MessageStatus.Completed,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                await chats.SetToolExecutionAsync(message.Id, tool, cancellationToken).ConfigureAwait(false);
                message = message with { ToolExecution = tool };

                var artifactIdentity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    relativePath + "\n" + snapshot.Sha256)));
                await using var content = new MemoryStream(snapshot.Bytes, writable: false);
                var artifact = await chatArtifacts.ImportAsync(
                    message.Id,
                    $"coding-workflow-plot-{state.Id:N}-{artifactIdentity[..16].ToLowerInvariant()}",
                    Path.GetFileName(plotPath),
                    ContentTypeForPlot(plotPath),
                    snapshot.Sha256,
                    snapshot.Bytes.LongLength,
                    "coding-workflow",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["source"] = "coding.workflow.plot",
                        ["workflowId"] = state.DefinitionId,
                        ["workflowRunId"] = state.Id.ToString("D"),
                        ["relativePath"] = relativePath,
                        ["iteration"] = state.Iteration.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    },
                    content,
                    cancellationToken).ConfigureAwait(false);
                await repository.SaveSolutionPublicationAsync(
                    state.Id, publicationKey, snapshot.Sha256, message.Id, message.CreatedAt, cancellationToken).ConfigureAwait(false);

                // Emit only after the artifact is durably associated with the message. The
                // WebView can therefore request its preview immediately without a stale event.
                await EmitStaticMessageAsync(message, [artifact]).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                OutputPublicationFailed(logger, plotPath, exception);
            }
        }
    }

    private async Task PublishUserInstructionAsync(
        Guid sessionId,
        string instruction,
        CancellationToken cancellationToken)
    {
        var message = await chats.AddMessageAsync(
            sessionId,
            ChatRole.User,
            instruction,
            MessageStatus.Completed,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await EmitStaticMessageAsync(message, []).ConfigureAwait(false);
    }

    private async Task<ChatMessage> PublishStaticMessageAsync(
        Guid sessionId,
        string content,
        ToolExecutionInfo tool,
        CancellationToken cancellationToken)
    {
        var message = await chats.AddMessageAsync(
            sessionId, ChatRole.Assistant, content, MessageStatus.Completed,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await chats.SetToolExecutionAsync(message.Id, tool, cancellationToken).ConfigureAwait(false);
        message = message with { ToolExecution = tool };
        await EmitStaticMessageAsync(message, []).ConfigureAwait(false);
        return message;
    }

    private Task EmitStaticMessageAsync(ChatMessage message, IReadOnlyList<ChatArtifact> artifacts) =>
        PublishAssistantUpdateAsync(new(GoAiAssistantUpdateKind.MessageAdded, message, artifacts));

    private async Task PublishAssistantUpdateAsync(GoAiAssistantUpdate update)
    {
        Func<GoAiAssistantUpdate, Task>? sink;
        lock (_sinkLock) sink = _assistantSink;
        if (sink is not null)
        {
            try { await sink(update).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                UiUpdateDetached(logger, "assistant update", exception);
            }
        }
    }

    private async Task PublishSnapshotAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        Func<CodingCampaignUiSnapshot, Task>? sink;
        lock (_sinkLock) sink = _campaignSink;
        if (sink is null) return;
        try
        {
            await sink(await GetSnapshotAsync(sessionId, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            UiUpdateDetached(logger, "workflow snapshot", exception);
        }
    }

    private CodingCampaignView BuildView(CodingCampaignState state)
    {
        var validation = ParseValidation(state.ValidationJson);
        var description = catalog.GetRequired(state.DefinitionId).Descriptor.Description;
        return new(
            state.Id.ToString("D"), state.SessionId.ToString("D"), state.DefinitionId, state.Title,
            description, Path.GetFileName(Path.TrimEndingDirectorySeparator(state.WorkspacePath)),
            state.ModelId, state.Status.ToString().ToLowerInvariant(), state.Phase.ToString().ToLowerInvariant(),
            state.Iteration, state.CurrentChallenge, state.LastError, state.RestartCount,
            validation.Issues, validation.Proofs, state.UpdatedAt);
    }

    private static string ResolvePrompt(
        ICodingCampaignDefinition definition,
        CodingCampaignState state,
        string challenge,
        string? instruction)
    {
        var prompt = state.Phase switch
        {
            CodingCampaignPhase.Bootstrap => definition.BuildBootstrapPrompt(),
            CodingCampaignPhase.Correction when ParseValidationProblems(state.ValidationJson) is { Length: > 0 } issues =>
                definition.BuildCorrectionPrompt(state.Iteration, challenge, issues),
            _ => BuildAutonomousRunPrompt(definition, state),
        };
        prompt += "\n\n" + ProcessReportInstruction + "\n\n" + ProcessReportDiffRule;
        return string.IsNullOrWhiteSpace(instruction)
            ? prompt
            : prompt + "\n\nZusätzliche aktuelle Nutzeranweisung:\n" + instruction;
    }

    internal static bool HasChangedCodeLines(string? diff)
    {
        if (string.IsNullOrWhiteSpace(diff)) return false;

        var currentPathIsCode = false;
        foreach (var line in diff.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                currentPathIsCode = IsCodePath(ExtractDiffTargetPath(line));
                continue;
            }

            if (!currentPathIsCode
                || line.StartsWith("+++", StringComparison.Ordinal)
                || line.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith('+') || line.StartsWith('-')) return true;
        }
        return false;
    }

    private static string ExtractDiffTargetPath(string header)
    {
        const string marker = " b/";
        var markerIndex = header.LastIndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0) return string.Empty;
        return header[(markerIndex + marker.Length)..].Trim().Trim('"').Replace('\\', '/');
    }

    private static bool IsCodePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(static segment => segment is ".git" or ".vs" or ".venv" or "bin" or "obj"
                or "artifacts" or "coverage" or "dist" or "node_modules" or "__pycache__" or ".pytest_cache"))
        {
            return false;
        }

        return Path.GetExtension(path).ToLowerInvariant() is
            ".c" or ".cc" or ".cpp" or ".cxx" or ".h" or ".hh" or ".hpp" or ".hxx"
            or ".cs" or ".csx" or ".fs" or ".fsx" or ".vb"
            or ".py" or ".pyi" or ".pyx" or ".r" or ".jl"
            or ".js" or ".mjs" or ".cjs" or ".jsx" or ".ts" or ".mts" or ".cts" or ".tsx"
            or ".java" or ".kt" or ".kts" or ".scala" or ".groovy"
            or ".go" or ".rs" or ".swift" or ".m" or ".mm"
            or ".rb" or ".php" or ".lua" or ".dart"
            or ".ps1" or ".psm1" or ".psd1" or ".sh" or ".bash" or ".zsh" or ".fish" or ".bat" or ".cmd"
            or ".sql" or ".html" or ".htm" or ".css" or ".scss" or ".sass" or ".less"
            or ".vue" or ".svelte" or ".razor" or ".xaml";
    }

    private static string BuildAutonomousRunPrompt(
        ICodingCampaignDefinition definition,
        CodingCampaignState state) => $$"""
        Arbeite im geladenen Coding-Workflow „{{definition.Descriptor.Title}}“ im vorhandenen Workspace autonom und
        ohne Rückfrage. Analysiere zuerst Code, Daten, Lösungen, Beweise, Testresultate und dokumentierte
        Fehlschläge. Wähle danach selbst den fachlich sinnvollsten noch offenen, nicht redundanten Arbeitsschritt.
        Eine erfolgreich verifizierte Lösung beendet den Workflow nicht: Erschließe anschließend selbstständig die
        nächste belastbare Fragestellung innerhalb des Workflow-Themas. Du entscheidest anhand des Erkenntnisgewinns,
        ob du einen bestehenden Ansatz vertiefst, eine Inkonsistenz behebst, ein anderes Verfahren erprobst oder eine
        neue zulässige Modellreduktion implementierst. Die Themenliste ist ein Möglichkeitsraum und keine Reihenfolge.
        Verändere keine Abnahmekriterien, erfinde keine Ergebnisse und führe alle passenden Tests,
        Verifikationen, Generatoren und Darstellungsprüfungen aus. Berichte im Ergebnis konkret über den tatsächlich
        gewählten Schritt, die Änderungen und die unabhängige Prüfung.

        Verbindliche workflow-spezifische Arbeits- und Abnahmeregeln:

        {{definition.BuildIterationPrompt(state.Iteration, "Wähle Ziel und Verfahren selbst anhand des Workspace-Stands.")}}
        """;

    private async Task<ChatSession> RequireCodingSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die Coding-Sitzung wurde nicht gefunden.");
        if (string.IsNullOrWhiteSpace(session.WorkspacePath) || !Directory.Exists(session.WorkspacePath))
        {
            throw new DirectoryNotFoundException("Wähle zuerst einen verfügbaren Workspace für die Coding-Sitzung aus.");
        }
        if (session.PersistentToolAction != PersistentToolAction.Code || session.AssistantMode != AssistantMode.Code)
        {
            await chats.SetPersistentToolActionAsync(session.Id, PersistentToolAction.Code, cancellationToken).ConfigureAwait(false);
            await chats.SetAssistantContextAsync(
                session.Id, AssistantMode.Code, session.WorkspacePath, session.WorkspaceFingerprint, cancellationToken).ConfigureAwait(false);
            session = session with { PersistentToolAction = PersistentToolAction.Code, AssistantMode = AssistantMode.Code };
        }
        return session;
    }

    private static void ValidateWorkspaceBinding(ChatSession session, CodingCampaignState state)
    {
        if (string.IsNullOrWhiteSpace(session.WorkspacePath) || !Directory.Exists(session.WorkspacePath))
        {
            throw new DirectoryNotFoundException("Der Workflow-Workspace ist nicht verfügbar.");
        }
        if (!Path.GetFullPath(session.WorkspacePath).Equals(Path.GetFullPath(state.WorkspacePath), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Der Sitzungs-Workspace wurde geändert. Lade den Workflow für den neuen Workspace erneut.");
        }
    }

    private async Task StopLoopCoreAsync(bool markStopped, CancellationToken cancellationToken)
    {
        var campaignId = _activeCampaignId;
        var loopTask = _loopTask;
        Interlocked.Increment(ref _loopGeneration);
        if (loopTask is { IsCompleted: false })
        {
            _loopCancellation?.Cancel();
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(TimeSpan.FromMinutes(2));
            await assistant.CancelCurrentAndWaitAsync(wait.Token).ConfigureAwait(false);
            try { await loopTask.WaitAsync(wait.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Der laufende Coding-Auftrag konnte nicht innerhalb von zwei Minuten beendet werden.");
            }
        }
        _loopCancellation?.Dispose();
        _loopCancellation = null;
        _loopTask = null;
        _activeCampaignId = null;
        _ = await chats.DeleteInternalMessagesAsync(CancellationToken.None).ConfigureAwait(false);
        if (markStopped && campaignId is { } id)
        {
            var state = await repository.GetAsync(id, CancellationToken.None).ConfigureAwait(false);
            if (state is not null)
            {
                state = state with { Status = CodingCampaignStatus.Stopped, LastError = null, UpdatedAt = DateTimeOffset.UtcNow };
                await repository.SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
                await PublishSnapshotAsync(state.SessionId, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private bool IsCurrentLoop(Guid campaignId, long generation) =>
        Volatile.Read(ref _loopGeneration) == generation
        && _activeCampaignId == campaignId;

    private async Task MarkStoppedAsync(
        CodingCampaignState state,
        string iterationReason,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var stopped = state with
        {
            Status = CodingCampaignStatus.Stopped,
            LastError = null,
            UpdatedAt = now,
        };
        await repository.SaveAsync(stopped, cancellationToken).ConfigureAwait(false);

        foreach (var iteration in await repository.ListIterationsAsync(state.Id, cancellationToken).ConfigureAwait(false))
        {
            if (!iteration.Status.Equals("running", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            await repository.SaveIterationAsync(
                iteration with
                {
                    Status = "cancelled",
                    Error = iterationReason,
                    UpdatedAt = now,
                },
                cancellationToken).ConfigureAwait(false);
        }
        await PublishSnapshotAsync(state.SessionId, cancellationToken).ConfigureAwait(false);
    }

    private static string[] FindPlots(string workspacePath)
    {
        var directory = Path.Combine(workspacePath, "visualizations");
        if (!Directory.Exists(directory)) return [];
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(path => PlotExtensions.Contains(Path.GetExtension(path)))
                .Select(path => new FileInfo(path))
                .Where(static file => file.Exists && file.Length > 0 && file.Length <= MaximumPlotLength)
                .OrderBy(static file => file.LastWriteTimeUtc)
                .ThenBy(static file => file.FullName, StringComparer.OrdinalIgnoreCase)
                .Select(static file => file.FullName)
                .ToArray();
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static async Task<BinarySnapshot?> ReadBinarySnapshotAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var before = new FileInfo(path);
                if (!before.Exists || before.Length <= 0 || before.Length > MaximumPlotLength) return null;
                await using var source = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var destination = new MemoryStream((int)Math.Min(before.Length, int.MaxValue));
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                var after = new FileInfo(path);
                if (before.Length == after.Length && before.LastWriteTimeUtc == after.LastWriteTimeUtc)
                {
                    var bytes = destination.ToArray();
                    return new(bytes, Convert.ToHexString(SHA256.HashData(bytes)));
                }
            }
            catch (IOException) when (attempt < 3) { }
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private static async Task<TextSnapshot?> ReadTextSnapshotAsync(string path, CancellationToken cancellationToken)
    {
        var binary = await ReadFileSnapshotAsync(path, MaximumPublishedTextLength * 4L, cancellationToken).ConfigureAwait(false);
        if (binary is null) return null;
        var text = Encoding.UTF8.GetString(binary.Bytes).Trim();
        return new(text, binary.Sha256);
    }

    private static async Task<BinarySnapshot?> ReadFileSnapshotAsync(
        string path,
        long maximumLength,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var before = new FileInfo(path);
                if (!before.Exists || before.Length <= 0 || before.Length > maximumLength) return null;
                await using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var memory = new MemoryStream((int)Math.Min(before.Length, int.MaxValue));
                await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
                var after = new FileInfo(path);
                if (before.Length == after.Length && before.LastWriteTimeUtc == after.LastWriteTimeUtc)
                {
                    var bytes = memory.ToArray();
                    return new(bytes, Convert.ToHexString(SHA256.HashData(bytes)));
                }
            }
            catch (IOException) when (attempt < 2) { }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private static string NormalizeRelativePath(string workspacePath, string path) =>
        Path.GetRelativePath(workspacePath, path).Replace('\\', '/');

    private static string ContentTypeForPlot(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        _ => "image/png",
    };

    private static string? NormalizeInstruction(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized.Length <= 100_000 ? normalized : normalized[..100_000];
    }

    private static TimeSpan RetryDelay(int restartCount) =>
        TimeSpan.FromSeconds(Math.Min(30, Math.Max(2, restartCount * 2)));

    private static TimeSpan NoProgressDelay(int consecutiveSteps) =>
        TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Clamp(consecutiveSteps, 1, 5))));

    private static string SerializeValidation(CodingCampaignValidationResult validation) =>
        JsonSerializer.Serialize(new PersistedValidation(validation.Issues, validation.Proofs), JsonOptions);

    internal static string[] ParseValidationProblems(string json)
    {
        var validation = ParseValidation(json);
        return validation.Issues
            .Concat(validation.Proofs
                .Where(static proof => !proof.Passed)
                .Select(static proof =>
                    $"Beweisprüfung {proof.ManifestPath}: {proof.Detail}"))
            .Where(static problem => !string.IsNullOrWhiteSpace(problem))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static PersistedValidation ParseValidation(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                return new(JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? [], []);
            }
            return JsonSerializer.Deserialize<PersistedValidation>(json, JsonOptions) ?? new([], []);
        }
        catch (JsonException)
        {
            return new(["Gespeicherte Validierungsdaten sind beschädigt."], []);
        }
    }

    private static string VisibleError(Exception exception) => exception switch
    {
        DirectoryNotFoundException => exception.Message,
        TimeoutException => exception.Message,
        _ => string.IsNullOrWhiteSpace(exception.Message) ? "Der Coding-Workflow ist fehlgeschlagen." : exception.Message,
    };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _loopCancellation?.Cancel();
        _loopCancellation?.Dispose();
        _controlGate.Dispose();
    }

    private sealed record PersistedValidation(
        IReadOnlyList<string> Issues,
        IReadOnlyList<CodingProofVerificationResult> Proofs);

    private sealed record BinarySnapshot(byte[] Bytes, string Sha256);
    private sealed record TextSnapshot(string Text, string Sha256);
}
