using GoAi.Contracts;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Policies;
using GoAi.Server.Core.Runtime;
using GoAi.Server.Core.Workers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using GoAi.Server.Core.Configuration;
using System.Text.Json;

namespace GoAi.Server.Core.Runs;

public sealed class RunProcessor : BackgroundService
{
    private readonly RunWorkChannel _queue;
    private readonly RunRepository _repository;
    private readonly ModelRouter _router;
    private readonly GpuLeaseScheduler _scheduler;
    private readonly LmStudioClient _lmStudio;
    private readonly WorkerOrchestrator _workers;
    private readonly AgentToolCatalog _toolCatalog;
    private readonly AgentToolExecutor _toolExecutor;
    private readonly RunEventNotifier _notifier;
    private readonly GoAiServerOptions _options;
    private readonly ServerRuntimeState _runtime;
    private readonly Dictionary<string, CancellationTokenSource> _activeRuns = new(StringComparer.Ordinal);
    private readonly object _activeGate = new();

    public RunProcessor(
        RunWorkChannel queue,
        RunRepository repository,
        ModelRouter router,
        GpuLeaseScheduler scheduler,
        LmStudioClient lmStudio,
        WorkerOrchestrator workers,
        AgentToolCatalog toolCatalog,
        AgentToolExecutor toolExecutor,
        RunEventNotifier notifier,
        IOptions<GoAiServerOptions> options,
        ServerRuntimeState runtime)
    {
        _queue = queue;
        _repository = repository;
        _router = router;
        _scheduler = scheduler;
        _lmStudio = lmStudio;
        _workers = workers;
        _toolCatalog = toolCatalog;
        _toolExecutor = toolExecutor;
        _notifier = notifier;
        _options = options.Value;
        _runtime = runtime;
    }

    public bool Cancel(string runId)
    {
        lock (_activeGate)
        {
            return _activeRuns.TryGetValue(runId, out var cancellation) && TryCancel(cancellation);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var recovered = await _repository.RecoverAsync(stoppingToken).ConfigureAwait(false);
        foreach (var runId in recovered)
        {
            await _queue.EnqueueAsync(runId, stoppingToken).ConfigureAwait(false);
        }

        await foreach (var runId in _queue.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            lock (_activeGate)
            {
                _activeRuns[runId] = linked;
            }

            try
            {
                await ProcessAsync(runId, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                await MarkInterruptedAsync(runId).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                await MarkCancelledAsync(runId).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                await MarkFailedAsync(runId, exception).ConfigureAwait(false);
            }
            finally
            {
                lock (_activeGate)
                {
                    _ = _activeRuns.Remove(runId);
                }
            }
        }
    }

    private async Task ProcessAsync(string runId, CancellationToken cancellationToken)
    {
        var request = await _repository.GetRequestAsync(runId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Run request disappeared from storage.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(request.Limits?.TimeoutSeconds ?? 1800));
        var runCancellationToken = timeout.Token;
        try
        {
            if (request.Workload?.Kind == RunWorkloadKind.ImageGeneration)
            {
                await ProcessImageGenerationAsync(runId, request.Workload, runCancellationToken).ConfigureAwait(false);
                return;
            }

            if (request.Workload?.Kind == RunWorkloadKind.MediaAnalysis)
            {
                await ProcessMediaAnalysisAsync(runId, request.Workload, runCancellationToken).ConfigureAwait(false);
                return;
            }

            await ProcessConversationAsync(runId, request, runCancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The configured run timeout expired.", exception);
        }
    }

    private async Task ProcessConversationAsync(
        string runId,
        RunRequest request,
        CancellationToken cancellationToken)
    {
        var selection = _router.Select(request);
        var contextLength = Math.Min(
            selection.ContextLength,
            request.Limits?.MaximumContextTokens ?? selection.ContextLength);
        var maximumOutputTokens = request.Limits?.MaximumOutputTokens ?? 8_192;
        var availableTools = _toolCatalog.GetAvailableTools(request);
        var checkpoint = await _repository.GetCheckpointAsync(runId, cancellationToken).ConfigureAwait(false);
        if (checkpoint is null)
        {
            checkpoint = new AgentRunCheckpoint(
                CreateInitialMessages(
                    request,
                    selection.Role,
                    availableTools.Select(static tool => tool.Name).ToArray()),
                0,
                0,
                0,
                0);
            await _repository.AppendEventAsync(
                runId,
                RunEventTypes.QueueChanged,
                new QueueChangedEvent(_scheduler.QueueLength + 1, _scheduler.QueueLength + 1),
                cancellationToken).ConfigureAwait(false);
            await _repository.UpdateStateAsync(runId, RunState.Running, selection.ModelId, cancellationToken: cancellationToken).ConfigureAwait(false);
            await _repository.AppendEventAsync(runId, RunEventTypes.RunStarted, new { protocolVersion = GoAiProtocol.Version }, cancellationToken).ConfigureAwait(false);
            await _repository.AppendEventAsync(
                runId,
                RunEventTypes.ModelSelected,
                new ModelSelectedEvent(selection.ModelId, selection.Role),
                cancellationToken).ConfigureAwait(false);
            _runtime.WriteLog("Information", "run.model.selected", $"Run {runId}: Modellrolle {selection.Role} gewählt.");
            await _repository.SaveCheckpointAsync(runId, checkpoint, cancellationToken).ConfigureAwait(false);
        }

        var messages = checkpoint.Messages.ToList();
        var roundCount = checkpoint.RoundCount;
        var toolCallCount = checkpoint.ToolCallCount;
        var inputTokens = checkpoint.InputTokens;
        var outputTokens = checkpoint.OutputTokens;
        var activeCalls = checkpoint.ActiveToolCalls?.ToArray();
        var nextToolIndex = checkpoint.NextToolIndex;
        var pendingProposalId = checkpoint.PendingProposalId;
        var pendingToolCallId = checkpoint.PendingToolCallId;

        while (roundCount < _options.MaximumModelRounds)
        {
            if (activeCalls is { Length: > 0 })
            {
                while (nextToolIndex < activeCalls.Length)
                {
                    var call = activeCalls[nextToolIndex];
                    var tool = _toolCatalog.Resolve(call.Name, availableTools);
                    if (!string.IsNullOrWhiteSpace(pendingProposalId))
                    {
                        if (!string.Equals(pendingToolCallId, call.Id, StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException("Persisted client tool checkpoint is inconsistent.");
                        }
                        var clientResult = await WaitForClientToolResultAsync(
                            runId,
                            pendingProposalId,
                            cancellationToken).ConfigureAwait(false);
                        messages.Add(new LmChatMessage("tool", SerializeClientToolResult(clientResult), ToolCallId: call.Id));
                        pendingProposalId = null;
                        pendingToolCallId = null;
                        nextToolIndex++;
                        await _repository.UpdateStateAsync(runId, RunState.Running, selection.ModelId, cancellationToken: cancellationToken).ConfigureAwait(false);
                        await SaveCheckpointAsync().ConfigureAwait(false);
                        continue;
                    }

                    _toolCatalog.Validate(tool, call.Arguments);
                    if (tool.ServerSide)
                    {
                        await _repository.AppendEventAsync(
                            runId,
                            RunEventTypes.ServerToolStarted,
                            new { tool = tool.Name, toolCallId = call.Id },
                            cancellationToken).ConfigureAwait(false);
                        var result = await _toolExecutor.ExecuteAsync(tool.Name, call.Arguments, runId, cancellationToken).ConfigureAwait(false);
                        if (result.IsFallback && result.ModelId is not null)
                        {
                            await _repository.AppendEventAsync(
                                runId,
                                RunEventTypes.ModelFallback,
                                new ModelSelectedEvent(result.ModelId, "vision-fallback", true),
                                cancellationToken).ConfigureAwait(false);
                        }
                        foreach (var artifact in result.Artifacts)
                        {
                            await _repository.AppendEventAsync(runId, RunEventTypes.ArtifactCreated, artifact, cancellationToken).ConfigureAwait(false);
                        }
                        await _repository.AppendEventAsync(
                            runId,
                            RunEventTypes.ServerToolCompleted,
                            new { tool = tool.Name, toolCallId = call.Id, result = result.Result },
                            cancellationToken).ConfigureAwait(false);
                        messages.Add(new LmChatMessage("tool", result.Result.GetRawText(), ToolCallId: call.Id));
                        nextToolIndex++;
                        await SaveCheckpointAsync().ConfigureAwait(false);
                        continue;
                    }

                    var proposal = new ToolProposal(
                        $"proposal-{Guid.NewGuid():N}",
                        runId,
                        tool.Name,
                        call.Arguments.Clone(),
                        tool.RiskClass,
                        CreateProposalSummary(tool, call.Arguments),
                        DateTimeOffset.UtcNow.AddHours(1));
                    await _repository.SaveToolProposalAsync(proposal, cancellationToken).ConfigureAwait(false);
                    pendingProposalId = proposal.ProposalId;
                    pendingToolCallId = call.Id;
                    await SaveCheckpointAsync().ConfigureAwait(false);
                    await _repository.AppendEventAsync(runId, RunEventTypes.ClientToolProposed, proposal, cancellationToken).ConfigureAwait(false);
                    await _repository.UpdateStateAsync(runId, RunState.WaitingForClient, selection.ModelId, cancellationToken: cancellationToken).ConfigureAwait(false);
                    await _repository.AppendEventAsync(
                        runId,
                        RunEventTypes.RunWaitingForClient,
                        new { proposalId = proposal.ProposalId, tool = proposal.Name, expiresAt = proposal.ExpiresAt },
                        cancellationToken).ConfigureAwait(false);
                }

                activeCalls = null;
                nextToolIndex = 0;
                await SaveCheckpointAsync().ConfigureAwait(false);
            }

            LmChatResult response;
            await _repository.AppendEventAsync(
                runId,
                RunEventTypes.ModelLoading,
                new { modelId = selection.ModelId, state = "loading" },
                cancellationToken).ConfigureAwait(false);
            var leaseMode = string.Equals(selection.Role, "general", StringComparison.Ordinal)
                ? GpuLeaseMode.Shared
                : GpuLeaseMode.Exclusive;
            await using (var lease = await _scheduler.AcquireAsync(
                $"llm-{selection.Role}",
                runId,
                leaseMode,
                cancellationToken).ConfigureAwait(false))
            {
                _ = await _workers.PrepareLmModelAsync(
                    selection.ModelId,
                    contextLength,
                    cancellationToken).ConfigureAwait(false);
                await _repository.AppendEventAsync(
                    runId,
                    RunEventTypes.ModelLoading,
                    new { modelId = selection.ModelId, state = "loaded" },
                    cancellationToken).ConfigureAwait(false);
                response = await _lmStudio.CompleteChatAsync(
                    selection.ModelId,
                    messages,
                    availableTools.Select(static tool => tool.ToLmDefinition()).ToArray(),
                    maximumOutputTokens,
                    cancellationToken).ConfigureAwait(false);
            }

            roundCount++;
            inputTokens += response.InputTokens;
            outputTokens += response.OutputTokens;
            if (response.ToolCalls.Count == 0)
            {
                if (string.IsNullOrWhiteSpace(response.Content))
                {
                    throw new InvalidOperationException("Model returned neither text nor a structured tool call.");
                }
                var finalResponse = ParseFinalResponse(response.Content, request);
                foreach (var delta in SplitDeltas(finalResponse.Message))
                {
                    await _repository.AppendEventAsync(
                        runId,
                        RunEventTypes.TextDelta,
                        new TextDeltaEvent(delta),
                        cancellationToken).ConfigureAwait(false);
                }

                var title = finalResponse.SessionTitle;
                await _repository.DeleteCheckpointAsync(runId, cancellationToken).ConfigureAwait(false);
                await _repository.AppendEventAsync(
                    runId,
                    RunEventTypes.RunCompleted,
                    new RunCompletedEvent(title, selection.ModelId, inputTokens, outputTokens),
                    cancellationToken).ConfigureAwait(false);
                await _repository.UpdateStateAsync(
                    runId,
                    RunState.Completed,
                    selection.ModelId,
                    title,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                _runtime.WriteLog("Information", "run.completed", $"Run {runId} erfolgreich beendet.");
                return;
            }

            toolCallCount += response.ToolCalls.Count;
            if (toolCallCount > _options.MaximumToolCalls)
            {
                throw new InvalidOperationException("Agent tool-call limit of 30 was exceeded.");
            }
            foreach (var call in response.ToolCalls)
            {
                var tool = _toolCatalog.Resolve(call.Name, availableTools);
                _toolCatalog.Validate(tool, call.Arguments);
            }
            messages.Add(new LmChatMessage("assistant", response.Content, response.ToolCalls));
            activeCalls = response.ToolCalls.ToArray();
            nextToolIndex = 0;
            await SaveCheckpointAsync().ConfigureAwait(false);
        }

        throw new InvalidOperationException($"Agent model-round limit of {_options.MaximumModelRounds} was exceeded.");

        Task SaveCheckpointAsync() => _repository.SaveCheckpointAsync(
            runId,
            new AgentRunCheckpoint(
                messages.ToArray(),
                roundCount,
                toolCallCount,
                inputTokens,
                outputTokens,
                activeCalls,
                nextToolIndex,
                pendingProposalId,
                pendingToolCallId),
            cancellationToken);
    }

    private async Task ProcessImageGenerationAsync(
        string runId,
        RunWorkload workload,
        CancellationToken cancellationToken)
    {
        var prompt = workload.Prompt ?? throw new InvalidOperationException("Image generation prompt is missing.");
        var request = new ImageGenerationRequest(
            prompt,
            workload.Width ?? 1024,
            workload.Height ?? 1024,
            workload.Seed,
            workload.Count ?? 1);
        await BeginWorkerRunAsync(runId, "image.generate", "Z-Image-Turbo Q4_K", cancellationToken).ConfigureAwait(false);
        var artifacts = await _workers.GenerateImagesAsync(request, runId, cancellationToken).ConfigureAwait(false);
        foreach (var artifact in artifacts)
        {
            await _repository.AppendEventAsync(runId, RunEventTypes.ArtifactCreated, artifact, cancellationToken).ConfigureAwait(false);
        }

        await CompleteWorkerRunAsync(runId, "Bildgenerierung", "Z-Image-Turbo Q4_K", artifacts, cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessMediaAnalysisAsync(
        string runId,
        RunWorkload workload,
        CancellationToken cancellationToken)
    {
        var uploadId = workload.UploadId ?? throw new InvalidOperationException("Media upload ID is missing.");
        if (workload.Options is null || !workload.Options.TryGetValue("mediaType", out var mediaType))
        {
            throw new InvalidOperationException("Media type is missing.");
        }

        await BeginWorkerRunAsync(runId, "media.analyze", "GO Media Pipeline", cancellationToken).ConfigureAwait(false);
        var arguments = JsonSerializer.SerializeToElement(
            new
            {
                uploadId,
                prompt = workload.Prompt ?? "Analysiere dieses Medium fachlich für die TGA-Planung und nenne Unsicherheiten.",
                detailWindows = workload.DetailWindows,
            },
            GoAiProtocol.CreateJsonOptions());
        var result = await _toolExecutor.ExecuteAsync(
            "media.analyze",
            arguments,
            runId,
            cancellationToken).ConfigureAwait(false);
        if (result.IsFallback && result.ModelId is not null)
        {
            await _repository.AppendEventAsync(
                runId,
                RunEventTypes.ModelFallback,
                new ModelSelectedEvent(result.ModelId, "vision-fallback", true),
                cancellationToken).ConfigureAwait(false);
        }
        await _repository.AppendEventAsync(
            runId,
            RunEventTypes.ServerToolCompleted,
            new { tool = "media.analyze", result = result.Result },
            cancellationToken).ConfigureAwait(false);
        foreach (var artifact in result.Artifacts)
        {
            await _repository.AppendEventAsync(runId, RunEventTypes.ArtifactCreated, artifact, cancellationToken).ConfigureAwait(false);
        }

        await CompleteWorkerRunAsync(
            runId,
            "Medienanalyse",
            result.ModelId ?? "GO Media Pipeline",
            result.Artifacts,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task BeginWorkerRunAsync(
        string runId,
        string tool,
        string provider,
        CancellationToken cancellationToken)
    {
        await _repository.AppendEventAsync(
            runId,
            RunEventTypes.QueueChanged,
            new QueueChangedEvent(_scheduler.QueueLength + 1, _scheduler.QueueLength + 1),
            cancellationToken).ConfigureAwait(false);
        await _repository.UpdateStateAsync(runId, RunState.Running, provider, cancellationToken: cancellationToken).ConfigureAwait(false);
        await _repository.AppendEventAsync(runId, RunEventTypes.RunStarted, new { protocolVersion = GoAiProtocol.Version }, cancellationToken).ConfigureAwait(false);
        await _repository.AppendEventAsync(runId, RunEventTypes.ServerToolStarted, new { tool }, cancellationToken).ConfigureAwait(false);
    }

    private async Task CompleteWorkerRunAsync(
        string runId,
        string title,
        string provider,
        IReadOnlyList<ArtifactDescriptor> artifacts,
        CancellationToken cancellationToken)
    {
        await _repository.AppendEventAsync(
            runId,
            RunEventTypes.ServerToolCompleted,
            new { provider, artifactCount = artifacts.Count },
            cancellationToken).ConfigureAwait(false);
        await _repository.AppendEventAsync(
            runId,
            RunEventTypes.RunCompleted,
            new RunCompletedEvent(title, provider, 0, 0, artifacts.Select(static item => item.ArtifactId).ToArray()),
            cancellationToken).ConfigureAwait(false);
        await _repository.UpdateStateAsync(runId, RunState.Completed, provider, title, cancellationToken: cancellationToken).ConfigureAwait(false);
        _runtime.WriteLog("Information", "run.completed", $"Worker-Run {runId} erfolgreich beendet.");
    }

    private static List<LmChatMessage> CreateInitialMessages(
        RunRequest request,
        string role,
        IReadOnlyList<string> effectiveTools)
    {
        var messages = new List<LmChatMessage>
        {
            new("system", TgaAgentPolicies.ForConversation(role, request, effectiveTools)),
        };
        foreach (var message in request.Messages)
        {
            var parts = new List<string>();
            foreach (var part in message.Content)
            {
                if (!string.IsNullOrWhiteSpace(part.Text))
                {
                    parts.Add(part.Text);
                }
                if (!string.IsNullOrWhiteSpace(part.UploadId))
                {
                    parts.Add($"[Temporärer Upload: {part.UploadId}; Datei: {part.FileName ?? "unbenannt"}; Medientyp: {part.MediaType ?? "unbekannt"}]");
                }
                if (!string.IsNullOrWhiteSpace(part.ArtifactId))
                {
                    parts.Add($"[Serverartefakt: {part.ArtifactId}; Datei: {part.FileName ?? "unbenannt"}]");
                }
            }
            if (parts.Count > 0)
            {
                var normalizedRole = string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                    ? "assistant"
                    : "user";
                messages.Add(new LmChatMessage(normalizedRole, string.Join(Environment.NewLine, parts)));
            }
        }
        return messages;
    }

    private async Task<ClientToolResult> WaitForClientToolResultAsync(
        string runId,
        string proposalId,
        CancellationToken cancellationToken)
    {
        var proposal = await _repository.GetToolProposalAsync(proposalId, runId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Persisted client tool proposal no longer exists.");
        using var subscription = _notifier.Subscribe(runId);
        while (true)
        {
            var result = await _repository.GetClientToolResultAsync(proposalId, cancellationToken).ConfigureAwait(false);
            if (result is not null)
            {
                return result;
            }
            if (DateTimeOffset.UtcNow >= proposal.ExpiresAt)
            {
                throw new TimeoutException("Client tool proposal expired before GO returned a result.");
            }

            using var delay = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, delay.Token);
            try
            {
                _ = await subscription.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (delay.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // Periodically recheck expiry and persisted state.
            }
        }
    }

    private static string SerializeClientToolResult(ClientToolResult result)
    {
        var payload = new
        {
            result.Status,
            result.Result,
            result.ErrorCode,
            result.Message,
        };
        return JsonSerializer.Serialize(payload, GoAiProtocol.CreateJsonOptions());
    }

    private static string CreateProposalSummary(AgentToolSpec tool, JsonElement arguments)
    {
        var target = arguments.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String
            ? path.GetString()
            : arguments.TryGetProperty("operation", out var operation) && operation.ValueKind == JsonValueKind.String
                ? operation.GetString()
                : arguments.TryGetProperty("preset", out var preset) && preset.ValueKind == JsonValueKind.String
                    ? preset.GetString()
                    : null;
        return string.IsNullOrWhiteSpace(target)
            ? $"GO soll {tool.Name} lokal ausführen."
            : $"GO soll {tool.Name} für „{target}“ lokal ausführen.";
    }

    private static IEnumerable<string> SplitDeltas(string content)
    {
        const int maximum = 512;
        var offset = 0;
        while (offset < content.Length)
        {
            var length = Math.Min(maximum, content.Length - offset);
            if (offset + length < content.Length && char.IsHighSurrogate(content[offset + length - 1]))
            {
                length--;
            }
            yield return content.Substring(offset, length);
            offset += length;
        }
    }

    private async Task MarkCancelledAsync(string runId)
    {
        await _repository.AppendEventAsync(runId, RunEventTypes.RunCancelled, new { reason = "client" }).ConfigureAwait(false);
        await _repository.UpdateStateAsync(runId, RunState.Cancelled, errorCode: "run.cancelled").ConfigureAwait(false);
        _runtime.WriteLog("Information", "run.cancelled", $"Run {runId} abgebrochen.");
    }

    private async Task MarkInterruptedAsync(string runId)
    {
        await _repository.UpdateStateAsync(runId, RunState.Interrupted, errorCode: "run.gateway_stopped").ConfigureAwait(false);
        _runtime.WriteLog("Warning", "run.interrupted", $"Run {runId} durch Gateway-Stopp unterbrochen.");
    }

    private async Task MarkFailedAsync(string runId, Exception exception)
    {
        var errorCode = exception switch
        {
            HttpRequestException => "provider.http_failed",
            JsonException => "provider.invalid_response",
            TimeoutException => "run.timeout",
            InvalidOperationException => "run.invalid_operation",
            _ => "run.failed",
        };
        await _repository.AppendEventAsync(
            runId,
            RunEventTypes.RunFailed,
            new RunFailedEvent(
                errorCode,
                "Der AI-Lauf konnte nicht abgeschlossen werden.",
                errorCode is "provider.http_failed" or "run.timeout")).ConfigureAwait(false);
        await _repository.UpdateStateAsync(runId, RunState.Failed, errorCode: errorCode).ConfigureAwait(false);
        _runtime.WriteLog("Error", errorCode, $"Run {runId} fehlgeschlagen ({exception.GetType().Name}).");
    }

    private static AgentFinalResponse ParseFinalResponse(string generated, RunRequest request)
    {
        var normalized = generated.Trim();
        const string titlePrefix = "GO_SESSION_TITLE:";
        var firstLineEnd = normalized.IndexOfAny(['\r', '\n']);
        var firstLine = firstLineEnd >= 0 ? normalized[..firstLineEnd] : normalized;
        if (firstLine.StartsWith(titlePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var title = firstLine[titlePrefix.Length..].Trim();
            var message = firstLineEnd >= 0
                ? normalized[firstLineEnd..].TrimStart('\r', '\n').Trim()
                : string.Empty;
            if (!string.IsNullOrWhiteSpace(message))
            {
                return new AgentFinalResponse(
                    message,
                    SanitizeTitle(title, GetLatestUserText(request)));
            }
        }

        if (normalized.StartsWith("```", StringComparison.Ordinal))
        {
            var firstFenceLineEnd = normalized.IndexOf('\n');
            var lastFence = normalized.LastIndexOf("```", StringComparison.Ordinal);
            if (firstFenceLineEnd >= 0 && lastFence > firstFenceLineEnd)
            {
                normalized = normalized[(firstFenceLineEnd + 1)..lastFence].Trim();
            }
        }

        try
        {
            using var document = JsonDocument.Parse(normalized);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("schema", out var schema)
                && string.Equals(schema.GetString(), "go.ai.agent.response.v1", StringComparison.Ordinal)
                && root.TryGetProperty("type", out var type)
                && string.Equals(type.GetString(), "message", StringComparison.Ordinal)
                && root.TryGetProperty("message", out var messageElement)
                && messageElement.ValueKind == JsonValueKind.String
                && root.TryGetProperty("sessionTitle", out var titleElement)
                && titleElement.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(messageElement.GetString()))
            {
                return new AgentFinalResponse(
                    messageElement.GetString()!,
                    SanitizeTitle(titleElement.GetString() ?? string.Empty, GetLatestUserText(request)));
            }
        }
        catch (JsonException)
        {
            // A bounded compatibility path keeps the answer visible while the strict contract is tested and logged by smokes.
        }

        return new AgentFinalResponse(
            generated,
            SanitizeTitle(string.Empty, GetLatestUserText(request)));
    }

    private static string GetLatestUserText(RunRequest request) => request.Messages
        .LastOrDefault(static message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))?
        .Content.FirstOrDefault(static part => !string.IsNullOrWhiteSpace(part.Text))?.Text
        ?? string.Empty;

    internal static string SanitizeTitle(string generated, string fallbackText)
    {
        var normalized = generated
            .Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace('"', ' ')
            .Replace('\'', ' ')
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim(' ', '.', ':', '-', '#');
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is > 0 and <= 6
            && normalized.Length <= 80
            && !IsGenericTitle(normalized))
        {
            return normalized;
        }

        var fallbackWords = fallbackText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(static word => word.Trim(' ', '.', ',', ':', ';', '!', '?', '-', '#', '"', '\''))
            .Where(static word => !TitleStopWords.Contains(word))
            .Take(6);
        var fallback = string.Join(' ', fallbackWords).Trim(' ', '.', ':', '-', '#');
        return string.IsNullOrWhiteSpace(fallback) ? "Neue TGA-Sitzung" : fallback;
    }

    private static bool IsGenericTitle(string value) => GenericTitles.Contains(value.Trim());

    private static readonly HashSet<string> GenericTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Hallo", "Hi", "Hey", "Frage", "Hilfe", "Neuer Chat", "Neue Sitzung", "Allgemeiner Chat",
        "Workflow", "GO AI bereit", "Test", "Testantwort",
    };

    private static readonly HashSet<string> TitleStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "hallo", "hi", "hey", "bitte", "kannst", "könntest", "du", "mir", "uns", "mal", "eine", "einen",
        "einer", "einem", "das", "die", "der", "den", "dem", "des", "dies", "diese", "dieser", "ist", "sind",
        "und", "oder", "für", "in", "im", "am", "an", "auf", "aus", "mit", "von", "zu", "zum", "zur",
        "beschreibe", "nenne", "erkläre", "erläutere", "zeige", "gib", "antworte", "fasse", "formuliere",
        "genau", "kurz", "kurze", "kurzen", "deutsch", "deutsche", "deutschen", "satz", "sätzen", "wie",
        "zuerst", "zunächst", "ich", "man", "wir", "ihr", "sie", "es",
    };

    private static bool TryCancel(CancellationTokenSource cancellation)
    {
        cancellation.Cancel();
        return true;
    }
}

internal sealed record AgentFinalResponse(string Message, string SessionTitle);
