using GoAi.Contracts;
using GoAi.Server.Core.Audio;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Research;
using GoAi.Server.Core.Runs;
using GoAi.Server.Core.Runtime;
using GoAi.Server.Core.Status;
using GoAi.Server.Core.Storage;
using GoAi.Server.Core.Workers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using System.Text.Json;

namespace GoAi.Server.Core.Gateway;

internal static class GatewayEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = GoAiProtocol.CreateJsonOptions();

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/health/live", WriteLiveHealthAsync);
        endpoints.MapGet("/v1/health/ready", WriteReadyHealthAsync);
        endpoints.MapGet("/v1/capabilities", WriteCapabilitiesAsync);
        endpoints.MapGet("/v1/models/status", WriteModelStatusAsync);
        endpoints.MapPost("/v1/models/general", SelectGeneralModelAsync);
        endpoints.MapPost("/v1/models/code", SelectCodingModelAsync);
        endpoints.MapGet("/v1/gpu/status", WriteGpuStatusAsync);
        endpoints.MapGet("/v1/services/status", WriteServiceStatusAsync);
        endpoints.MapPost("/v1/context/embeddings", CreateEmbeddingsAsync);
        endpoints.MapPost("/v1/context/embeddings/release", ReleaseEmbeddingsAsync);

        endpoints.MapPost("/v1/runs", CreateRunAsync);
        endpoints.MapGet("/v1/runs/{runId}", GetRunAsync);
        endpoints.MapGet("/v1/runs/{runId}/events", StreamRunEventsAsync);
        endpoints.MapPost("/v1/runs/{runId}/client-tool-results", SubmitClientToolResultAsync);
        endpoints.MapPost("/v1/runs/{runId}/cancel", CancelRunAsync);

        endpoints.MapPost("/v1/uploads", CreateUploadAsync);
        endpoints.MapGet("/v1/uploads/{uploadId}", GetUploadAsync);
        endpoints.MapPut("/v1/uploads/{uploadId}/chunks/{index:int}", PutUploadChunkAsync);
        endpoints.MapPost("/v1/uploads/{uploadId}/complete", CompleteUploadAsync);
        endpoints.MapDelete("/v1/uploads/{uploadId}", DeleteUploadAsync);
        endpoints.MapGet("/v1/artifacts/{artifactId}", GetArtifactAsync);

        endpoints.MapPost("/v1/research/web", SearchWebAsync);
        endpoints.MapPost("/v1/research/youtube", SearchYouTubeAsync);
        endpoints.MapPost("/v1/research/fetch", FetchWebAsync);

        endpoints.MapPost("/v1/audio/transcriptions", TranscribeAudioAsync);
        endpoints.MapPost("/v1/audio/speech", SynthesizeSpeechAsync);
        endpoints.MapPost("/v1/audio/speech/sessions", CreateSpeechSessionAsync);
        endpoints.MapPost("/v1/audio/speech/sessions/{sessionId}/paragraphs", SynthesizeSpeechParagraphAsync);
        endpoints.MapPost("/v1/audio/speech/sessions/{sessionId}/end", EndSpeechSessionAsync);
        endpoints.MapPost("/v1/audio/speech/sessions/{sessionId}/cancel", CancelSpeechSessionAsync);
        endpoints.MapPost("/v1/audio/utterance-intent", ClassifyUtteranceIntentAsync);
        endpoints.MapPost("/v1/audio/live-captions/sessions", CreateLiveCaptionSessionAsync);
        endpoints.MapGet("/v1/audio/live-captions/sessions/{sessionId}", GetLiveCaptionSessionAsync);
        endpoints.MapPost("/v1/audio/live-captions/sessions/{sessionId}/heartbeat", KeepLiveCaptionSessionAliveAsync);
        endpoints.MapPut("/v1/audio/live-captions/sessions/{sessionId}/chunks/{sequence:long}", PutLiveCaptionChunkAsync);
        endpoints.MapPost("/v1/audio/live-captions/sessions/{sessionId}/stop", StopLiveCaptionSessionAsync);
        endpoints.MapPost("/v1/images/generations", GenerateImageAsync);
        endpoints.MapPost("/v1/media/analyze", AnalyzeMediaAsync);
    }

    private static Task WriteLiveHealthAsync(HttpContext context) =>
        WriteJsonAsync(context, ServerRuntimeState.CreateLiveSnapshot());

    private static async Task WriteReadyHealthAsync(HttpContext context)
    {
        var readiness = context.RequestServices.GetRequiredService<ReadinessService>();
        var result = await readiness.GetSnapshotAsync(context.RequestAborted).ConfigureAwait(false);
        if (!string.Equals(result.Status, "ready", StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        }

        await WriteJsonAsync(context, result).ConfigureAwait(false);
    }

    private static Task WriteCapabilitiesAsync(HttpContext context)
    {
        var capabilities = context.RequestServices.GetRequiredService<CapabilityService>();
        return WriteJsonAsync(context, capabilities.GetSnapshot());
    }

    private static async Task WriteModelStatusAsync(HttpContext context)
    {
        var lmStudio = context.RequestServices.GetRequiredService<LmStudioClient>();
        await WriteJsonAsync(context, await lmStudio.GetStatusAsync(context.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task SelectGeneralModelAsync(HttpContext context)
    {
        var request = await ReadJsonAsync<GeneralModelSelection>(context).ConfigureAwait(false);
        var selection = context.RequestServices.GetRequiredService<GeneralModelSelectionService>();
        await WriteJsonAsync(
            context,
            await selection.SelectAsync(request.ModelId, context.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task SelectCodingModelAsync(HttpContext context)
    {
        var request = await ReadJsonAsync<CodingModelSelection>(context).ConfigureAwait(false);
        var selection = context.RequestServices.GetRequiredService<CodingModelSelectionService>();
        await WriteJsonAsync(
            context,
            await selection.SelectAsync(request.ModelId, context.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task WriteGpuStatusAsync(HttpContext context)
    {
        var gpu = context.RequestServices.GetRequiredService<GpuStatusService>();
        await WriteJsonAsync(context, await gpu.GetStatusAsync(context.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task WriteServiceStatusAsync(HttpContext context)
    {
        var probes = context.RequestServices.GetRequiredService<ServiceProbeService>();
        await WriteJsonAsync(context, await probes.GetStatusesAsync(context.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task CreateEmbeddingsAsync(HttpContext context)
    {
        var request = await ReadJsonAsync<EmbeddingBatchRequest>(context).ConfigureAwait(false);
        if (request.Inputs is null || request.Inputs.Count is < 1 or > 64)
        {
            throw new ArgumentException("Embedding batches require between 1 and 64 inputs.");
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        long totalCharacters = 0;
        foreach (var input in request.Inputs)
        {
            if (input is null
                || string.IsNullOrWhiteSpace(input.Id)
                || input.Id.Length > 128
                || !ids.Add(input.Id)
                || string.IsNullOrWhiteSpace(input.Text)
                || input.Text.Length > 32_768)
            {
                throw new ArgumentException("Embedding inputs require unique bounded IDs and text up to 32768 characters.");
            }
            totalCharacters += input.Text.Length;
        }
        if (totalCharacters > 512_000)
        {
            throw new ArgumentException("The embedding batch exceeds 512000 characters.");
        }

        var executor = context.RequestServices.GetRequiredService<AgentToolExecutor>();
        var result = await executor.CreateEmbeddingBatchAsync(
            request,
            $"embedding-{Guid.NewGuid():N}",
            context.RequestAborted).ConfigureAwait(false);
        await WriteJsonAsync(context, result).ConfigureAwait(false);
    }

    private static async Task ReleaseEmbeddingsAsync(HttpContext context)
    {
        await AgentToolExecutor.ReleaseEmbeddingModelAsync(context.RequestAborted).ConfigureAwait(false);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private static async Task CreateRunAsync(HttpContext context)
    {
        var request = await ReadJsonAsync<RunRequest>(context).ConfigureAwait(false);
        RunRequestValidator.Validate(request);
        var idempotencyKey = context.Request.Headers[GoAiHeaders.IdempotencyKey].FirstOrDefault();
        if (idempotencyKey?.Length > 128)
        {
            throw new ArgumentException("Idempotency-Key may contain at most 128 characters.");
        }

        var repository = context.RequestServices.GetRequiredService<RunRepository>();
        var queue = context.RequestServices.GetRequiredService<RunWorkChannel>();
        var (snapshot, created) = await repository.CreateAsync(request, idempotencyKey, context.RequestAborted).ConfigureAwait(false);
        if (created)
        {
            await queue.EnqueueAsync(snapshot.RunId, context.RequestAborted).ConfigureAwait(false);
        }

        context.Response.StatusCode = created ? StatusCodes.Status202Accepted : StatusCodes.Status200OK;
        await WriteJsonAsync(
            context,
            new RunAccepted(
                snapshot.RunId,
                snapshot.State,
                snapshot.CreatedAt,
                $"/v1/runs/{Uri.EscapeDataString(snapshot.RunId)}/events")).ConfigureAwait(false);
    }

    private static async Task GetRunAsync(HttpContext context)
    {
        var runId = GetRouteString(context, "runId");
        var repository = context.RequestServices.GetRequiredService<RunRepository>();
        var snapshot = await repository.GetAsync(runId, context.RequestAborted).ConfigureAwait(false);
        if (snapshot is null)
        {
            throw new KeyNotFoundException("Run not found.");
        }

        await WriteJsonAsync(context, snapshot).ConfigureAwait(false);
    }

    private static async Task StreamRunEventsAsync(HttpContext context)
    {
        var runId = GetRouteString(context, "runId");
        var repository = context.RequestServices.GetRequiredService<RunRepository>();
        var snapshot = await repository.GetAsync(runId, context.RequestAborted).ConfigureAwait(false);
        if (snapshot is null)
        {
            throw new KeyNotFoundException("Run not found.");
        }

        var cursor = ParseLastEventId(context);
        var notifier = context.RequestServices.GetRequiredService<RunEventNotifier>();
        using var subscription = notifier.Subscribe(runId);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache, no-store";
        context.Response.Headers.Append("X-Accel-Buffering", "no");

        while (!context.RequestAborted.IsCancellationRequested)
        {
            var events = await repository.GetEventsAfterAsync(runId, cursor, context.RequestAborted).ConfigureAwait(false);
            foreach (var runEvent in events)
            {
                cursor = runEvent.Id;
                var json = JsonSerializer.Serialize(runEvent, JsonOptions);
                await context.Response.WriteAsync($"id: {runEvent.Id.ToString(CultureInfo.InvariantCulture)}\n", context.RequestAborted).ConfigureAwait(false);
                await context.Response.WriteAsync($"event: {runEvent.Type}\n", context.RequestAborted).ConfigureAwait(false);
                await context.Response.WriteAsync($"data: {json}\n\n", context.RequestAborted).ConfigureAwait(false);
            }

            if (events.Count > 0)
            {
                await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
            }

            snapshot = await repository.GetAsync(runId, context.RequestAborted).ConfigureAwait(false);
            if (snapshot is null || IsTerminal(snapshot.State))
            {
                var trailing = await repository.GetEventsAfterAsync(runId, cursor, context.RequestAborted).ConfigureAwait(false);
                if (trailing.Count == 0)
                {
                    break;
                }

                continue;
            }

            using var heartbeat = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                context.RequestAborted,
                heartbeat.Token);
            try
            {
                _ = await subscription.Reader.ReadAsync(waitCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (heartbeat.IsCancellationRequested && !context.RequestAborted.IsCancellationRequested)
            {
                await context.Response.WriteAsync(": keep-alive\n\n", context.RequestAborted).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
            }
        }
    }

    private static async Task SubmitClientToolResultAsync(HttpContext context)
    {
        var runId = GetRouteString(context, "runId");
        var result = await ReadClientToolResultAsync(context).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result.ProposalId))
        {
            throw new ArgumentException("proposalId is required.");
        }
        if (result.Status is not ("completed" or "rejected" or "failed"))
        {
            throw new ArgumentException("Client tool result status must be completed, rejected, or failed.");
        }

        var repository = context.RequestServices.GetRequiredService<RunRepository>();
        if (await repository.GetAsync(runId, context.RequestAborted).ConfigureAwait(false) is null)
        {
            throw new KeyNotFoundException("Run not found.");
        }

        _ = await repository.SaveClientToolResultAsync(runId, result, context.RequestAborted).ConfigureAwait(false);
        // The proposal event can reach a fast local client before RunProcessor has finished
        // switching Running -> WaitingForClient. Queueing is therefore based atomically on the
        // persisted checkpoint and result, rather than on a racy state snapshot. Re-submitting an
        // idempotent result also repairs a continuation that was missed by an older gateway build.
        if (await repository.TryQueueClientToolContinuationAsync(
                runId,
                result.ProposalId,
                context.RequestAborted).ConfigureAwait(false))
        {
            var queue = context.RequestServices.GetRequiredService<RunWorkChannel>();
            await queue.EnqueueAsync(runId, context.RequestAborted).ConfigureAwait(false);
        }
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private static async Task CancelRunAsync(HttpContext context)
    {
        var runId = GetRouteString(context, "runId");
        var repository = context.RequestServices.GetRequiredService<RunRepository>();
        var snapshot = await repository.GetAsync(runId, context.RequestAborted).ConfigureAwait(false);
        if (snapshot is null)
        {
            throw new KeyNotFoundException("Run not found.");
        }

        if (IsTerminal(snapshot.State))
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        var processor = context.RequestServices.GetRequiredService<RunProcessor>();
        if (!processor.Cancel(runId))
        {
            await repository.AppendEventAsync(runId, RunEventTypes.RunCancelled, new { reason = "client" }, context.RequestAborted).ConfigureAwait(false);
            await repository.UpdateStateAsync(runId, RunState.Cancelled, errorCode: "run.cancelled", cancellationToken: context.RequestAborted).ConfigureAwait(false);
        }

        context.Response.StatusCode = StatusCodes.Status202Accepted;
    }

    private static async Task CreateUploadAsync(HttpContext context)
    {
        var manifest = await ReadJsonAsync<UploadManifest>(context).ConfigureAwait(false);
        var uploads = context.RequestServices.GetRequiredService<UploadService>();
        var result = await uploads.CreateAsync(manifest, context.RequestAborted).ConfigureAwait(false);
        context.Response.StatusCode = StatusCodes.Status201Created;
        await WriteJsonAsync(context, result).ConfigureAwait(false);
    }

    private static async Task GetUploadAsync(HttpContext context)
    {
        var uploads = context.RequestServices.GetRequiredService<UploadService>();
        var result = await uploads.GetAsync(GetRouteString(context, "uploadId"), context.RequestAborted).ConfigureAwait(false);
        if (result is null)
        {
            throw new KeyNotFoundException("Upload not found.");
        }

        await WriteJsonAsync(context, result).ConfigureAwait(false);
    }

    private static async Task PutUploadChunkAsync(HttpContext context)
    {
        var expectedSha = context.Request.Headers["X-Chunk-SHA256"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(expectedSha))
        {
            throw new ArgumentException("X-Chunk-SHA256 is required.");
        }

        var indexText = GetRouteString(context, "index");
        if (!int.TryParse(indexText, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            throw new ArgumentException("Chunk index is invalid.");
        }

        var uploads = context.RequestServices.GetRequiredService<UploadService>();
        var result = await uploads.PutChunkAsync(
            GetRouteString(context, "uploadId"),
            index,
            context.Request.Body,
            expectedSha,
            context.RequestAborted).ConfigureAwait(false);
        await WriteJsonAsync(context, result).ConfigureAwait(false);
    }

    private static async Task CompleteUploadAsync(HttpContext context)
    {
        var uploads = context.RequestServices.GetRequiredService<UploadService>();
        var result = await uploads.CompleteAsync(GetRouteString(context, "uploadId"), context.RequestAborted).ConfigureAwait(false);
        await WriteJsonAsync(context, result).ConfigureAwait(false);
    }

    private static async Task DeleteUploadAsync(HttpContext context)
    {
        var uploads = context.RequestServices.GetRequiredService<UploadService>();
        await uploads.DeleteAsync(GetRouteString(context, "uploadId"), context.RequestAborted).ConfigureAwait(false);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private static async Task GetArtifactAsync(HttpContext context)
    {
        var artifacts = context.RequestServices.GetRequiredService<ArtifactService>();
        var artifact = await artifacts.ResolveAsync(GetRouteString(context, "artifactId"), context.RequestAborted).ConfigureAwait(false);
        if (artifact is null)
        {
            throw new KeyNotFoundException("Artifact not found.");
        }

        context.Response.Headers.ETag = $"\"{artifact.Descriptor.Sha256}\"";
        await Results.File(
            artifact.Path,
            artifact.Descriptor.MediaType,
            artifact.Descriptor.FileName,
            enableRangeProcessing: true).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task SearchWebAsync(HttpContext context)
    {
        var request = await ReadJsonAsync<WebSearchRequest>(context).ConfigureAwait(false);
        var research = context.RequestServices.GetRequiredService<WebResearchService>();
        await WriteJsonAsync(context, await research.SearchAsync(request, youtubeFallback: false, context.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task SearchYouTubeAsync(HttpContext context)
    {
        var request = await ReadJsonAsync<WebSearchRequest>(context).ConfigureAwait(false);
        var research = context.RequestServices.GetRequiredService<WebResearchService>();
        await WriteJsonAsync(context, await research.SearchAsync(request, youtubeFallback: true, context.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task FetchWebAsync(HttpContext context)
    {
        var request = await ReadJsonAsync<WebFetchRequest>(context).ConfigureAwait(false);
        await WriteJsonAsync(context, await WebResearchService.FetchAsync(request, context.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task TranscribeAudioAsync(HttpContext context)
    {
        var request = await ReadJsonAsync<TranscriptionRequest>(context).ConfigureAwait(false);
        if (request.Language?.Length > 16)
        {
            throw new ArgumentException("Transcription language may contain at most 16 characters.");
        }
        var uploads = context.RequestServices.GetRequiredService<UploadService>();
        var upload = await uploads.GetCompletedAsync(request.UploadId, context.RequestAborted).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Completed audio upload not found.");
        if (!upload.MediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            && !upload.MediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only completed audio or video uploads can be transcribed.");
        }

        var workers = context.RequestServices.GetRequiredService<WorkerOrchestrator>();
        await WriteJsonAsync(context, await workers.TranscribeAsync(request, cancellationToken: context.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task SynthesizeSpeechAsync(HttpContext context)
    {
        var request = await ReadJsonAsync<SpeechRequest>(context).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(request.Text)
            || request.Text.Length > 10_000
            || request.Speed is < 0.5 or > 2.0
            || string.IsNullOrWhiteSpace(request.Voice)
            || request.Voice?.Length > 64
            || !string.Equals(request.Format, "wav", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Speech text, voice, format, or speed is outside the protocol limits.");
        }

        request = request with { Text = GermanSpeechTextNormalizer.Normalize(request.Text) };
        if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 10_000)
        {
            throw new ArgumentException("Speech text is empty or too long after mathematical notation was normalized.");
        }

        var workers = context.RequestServices.GetRequiredService<WorkerOrchestrator>();
        await WriteJsonAsync(context, await workers.SynthesizeAsync(request, cancellationToken: context.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task CreateSpeechSessionAsync(HttpContext context)
    {
        var request = await ReadJsonAsync<SpeechSessionRequest>(context).ConfigureAwait(false);
        if (!string.Equals(request.Language, "de", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(request.Language, "de-DE", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Vorlesesitzungen unterstützen ausschließlich deutsche Sprachausgabe.");
        }
        var workers = context.RequestServices.GetRequiredService<WorkerOrchestrator>();
        var result = await workers.BeginSpeechSessionAsync(request, context.RequestAborted).ConfigureAwait(false);
        context.Response.StatusCode = StatusCodes.Status201Created;
        context.Response.Headers.Location = $"/v1/audio/speech/sessions/{Uri.EscapeDataString(result.SessionId)}";
        await WriteJsonAsync(context, result).ConfigureAwait(false);
    }

    private static async Task SynthesizeSpeechParagraphAsync(HttpContext context)
    {
        var sessionId = GetRouteString(context, "sessionId");
        var request = await ReadJsonAsync<SpeechParagraphRequest>(context).ConfigureAwait(false);
        if (request.ParagraphIndex < 0
            || request.Speed is < 0.5 or > 2.0
            || string.IsNullOrWhiteSpace(request.Text)
            || request.Text.Length > 3_000)
        {
            throw new ArgumentException("Absatzindex, Text oder Geschwindigkeit liegt außerhalb der Vorlesegrenzen.");
        }
        IReadOnlyList<SpeechParagraphPart>? parts = null;
        if (request.Parts is { Count: > 0 })
        {
            if (request.Parts.Count > 256)
            {
                throw new ArgumentException("Ein Vorleseabsatz enthält zu viele Satzteile.");
            }
            var normalizedParts = new List<SpeechParagraphPart>(request.Parts.Count);
            var seenIndexes = new HashSet<int>();
            foreach (var part in request.Parts)
            {
                var text = GermanSpeechTextNormalizer.Normalize(part.Text);
                if (part.SegmentIndex < 0
                    || !seenIndexes.Add(part.SegmentIndex)
                    || part.Speed is < 0.5 or > 2.0
                    || part.PauseBeforeMilliseconds is < 0 or > 1_500
                    || part.PauseAfterMilliseconds is < 0 or > 1_500
                    || string.IsNullOrWhiteSpace(text)
                    || text.Length > 3_000)
                {
                    throw new ArgumentException("Ein Satzteil des Vorleseabsatzes ist ungültig.");
                }
                normalizedParts.Add(part with { Text = text });
            }
            var combined = string.Join(' ', normalizedParts.Select(static part => part.Text));
            if (combined.Length > 3_000)
            {
                throw new ArgumentException("Die Satzteile überschreiten gemeinsam die Vorlesegrenze des Absatzes.");
            }
            parts = normalizedParts;
            request = request with { Text = combined, Parts = parts };
        }
        else
        {
            request = request with { Text = GermanSpeechTextNormalizer.Normalize(request.Text), Parts = null };
        }
        if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 3_000)
        {
            throw new ArgumentException("Der Absatz ist nach der mathematischen Normalisierung leer oder zu lang.");
        }
        var workers = context.RequestServices.GetRequiredService<WorkerOrchestrator>();
        await WriteJsonAsync(
            context,
            await workers.SynthesizeSpeechParagraphAsync(
                sessionId,
                request,
                context.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static Task EndSpeechSessionAsync(HttpContext context) =>
        CompleteSpeechSessionAsync(context, cancelled: false);

    private static Task CancelSpeechSessionAsync(HttpContext context) =>
        CompleteSpeechSessionAsync(context, cancelled: true);

    private static async Task CompleteSpeechSessionAsync(HttpContext context, bool cancelled)
    {
        var sessionId = GetRouteString(context, "sessionId");
        var workers = context.RequestServices.GetRequiredService<WorkerOrchestrator>();
        await WriteJsonAsync(
            context,
            await workers.EndSpeechSessionAsync(
                sessionId,
                cancelled,
                context.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task ClassifyUtteranceIntentAsync(HttpContext context)
    {
        var request = await ReadJsonAsync<UtteranceIntentRequest>(context).ConfigureAwait(false);
        var service = context.RequestServices.GetRequiredService<UtteranceIntentService>();
        await WriteJsonAsync(
            context,
            await service.ClassifyAsync(request, context.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task CreateLiveCaptionSessionAsync(HttpContext context)
    {
        var request = await ReadJsonAsync<LiveCaptionSessionRequest>(context).ConfigureAwait(false);
        var captions = context.RequestServices.GetRequiredService<LiveCaptionService>();
        var snapshot = await captions.CreateAsync(request, context.RequestAborted).ConfigureAwait(false);
        context.Response.StatusCode = StatusCodes.Status201Created;
        context.Response.Headers.Location = $"/v1/audio/live-captions/sessions/{Uri.EscapeDataString(snapshot.SessionId)}";
        await WriteJsonAsync(context, snapshot).ConfigureAwait(false);
    }

    private static async Task GetLiveCaptionSessionAsync(HttpContext context)
    {
        var captions = context.RequestServices.GetRequiredService<LiveCaptionService>();
        await WriteJsonAsync(
            context,
            await captions.GetAsync(
                GetRouteString(context, "sessionId"),
                context.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task KeepLiveCaptionSessionAliveAsync(HttpContext context)
    {
        var captions = context.RequestServices.GetRequiredService<LiveCaptionService>();
        await WriteJsonAsync(
            context,
            await captions.KeepAliveAsync(
                GetRouteString(context, "sessionId"),
                context.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task PutLiveCaptionChunkAsync(HttpContext context)
    {
        if (context.Request.ContentType is not { } contentType
            || !contentType.StartsWith("audio/wav", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Live-Untertitel-Audio muss audio/wav verwenden.");
        }
        if (!long.TryParse(
            GetRouteString(context, "sequence"),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var sequence)
            || sequence < 0)
        {
            throw new ArgumentException("Live-Untertitel-Sequenz ist ungültig.");
        }

        var audio = await GatewayRequestReader.ReadBinaryAsync(
            context,
            GoAiProtocol.MaximumLiveCaptionChunkBytes).ConfigureAwait(false);
        LiveCaptionChunkMetadata? metadata = null;
        var turnId = context.Request.Headers[GoAiHeaders.CaptionTurnId].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(turnId))
        {
            if (!long.TryParse(
                    context.Request.Headers[GoAiHeaders.CaptionRevision].FirstOrDefault(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var revision)
                || revision < 0
                || !int.TryParse(
                    context.Request.Headers[GoAiHeaders.CaptionWindowStartMilliseconds].FirstOrDefault(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var windowStartMilliseconds)
                || windowStartMilliseconds < 0
                || !bool.TryParse(
                    context.Request.Headers[GoAiHeaders.CaptionFinal].FirstOrDefault(),
                    out var isFinal))
            {
                throw new ArgumentException("Diktierfenster-Metadaten sind ungültig.");
            }
            metadata = new LiveCaptionChunkMetadata(
                turnId,
                revision,
                windowStartMilliseconds,
                isFinal);
        }
        var captions = context.RequestServices.GetRequiredService<LiveCaptionService>();
        await WriteJsonAsync(
            context,
            await captions.ProcessChunkAsync(
                GetRouteString(context, "sessionId"),
                sequence,
                audio,
                metadata,
                context.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task StopLiveCaptionSessionAsync(HttpContext context)
    {
        var captions = context.RequestServices.GetRequiredService<LiveCaptionService>();
        await WriteJsonAsync(
            context,
            await captions.StopSessionAsync(
                GetRouteString(context, "sessionId"),
                context.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task GenerateImageAsync(HttpContext context)
    {
        var request = await ReadJsonAsync<ImageGenerationRequest>(context).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(request.Prompt)
            || request.Prompt.Length > 10_000
            || request.Width is < 256 or > 1536
            || request.Height is < 256 or > 1536
            || request.Width % 64 != 0
            || request.Height % 64 != 0
            || request.Count is < 1 or > 4)
        {
            throw new ArgumentException("Image generation parameters are outside the protocol limits.");
        }

        var runRequest = new RunRequest(
            GoAiProtocol.Version,
            RunMode.General,
            [new RunMessage("user", [new ContentPart("text", request.Prompt)])],
            Workload: new RunWorkload(
                RunWorkloadKind.ImageGeneration,
                Prompt: request.Prompt,
                Width: request.Width,
                Height: request.Height,
                Seed: request.Seed,
                Count: request.Count));
        await AcceptSpecialRunAsync(context, runRequest).ConfigureAwait(false);
    }

    private static async Task AnalyzeMediaAsync(HttpContext context)
    {
        var request = await ReadJsonAsync<MediaJobRequest>(context).ConfigureAwait(false);
        if (request.Prompt?.Length > 10_000
            || request.Options?.Count > 32
            || request.Options?.Any(static option =>
                string.IsNullOrWhiteSpace(option.Key)
                || option.Key.Length > 64
                || option.Value is null
                || option.Value.Length > 1_024) == true)
        {
            throw new ArgumentException("Media prompt or options exceed the protocol limits.");
        }
        var uploads = context.RequestServices.GetRequiredService<UploadService>();
        var upload = await uploads.GetCompletedAsync(request.UploadId, context.RequestAborted).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Completed media upload not found.");
        if (!upload.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            && !upload.MediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            && !upload.MediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The uploaded media type cannot be analyzed.");
        }
        ValidateDetailWindows(request.DetailWindows);

        var options = new Dictionary<string, string>(request.Options ?? new Dictionary<string, string>(), StringComparer.Ordinal)
        {
            ["mediaType"] = upload.MediaType,
        };
        var prompt = string.IsNullOrWhiteSpace(request.Prompt) ? "Analysiere dieses Medium fachlich." : request.Prompt;
        var runRequest = new RunRequest(
            GoAiProtocol.Version,
            RunMode.General,
            [new RunMessage("user", [new ContentPart("text", prompt, UploadId: request.UploadId, MediaType: upload.MediaType, FileName: upload.FileName)])],
            UploadIds: [request.UploadId],
            Workload: new RunWorkload(
                RunWorkloadKind.MediaAnalysis,
                UploadId: request.UploadId,
                Prompt: prompt,
                Options: options,
                DetailWindows: request.DetailWindows));
        await AcceptSpecialRunAsync(context, runRequest).ConfigureAwait(false);
    }

    private static void ValidateDetailWindows(IReadOnlyList<MediaTimeWindow>? windows)
    {
        if (windows is null)
        {
            return;
        }
        if (windows.Count > 3 || windows.Any(static window =>
            !double.IsFinite(window.Start)
            || !double.IsFinite(window.End)
            || window.Start < 0
            || window.End <= window.Start
            || window.End > 3_600))
        {
            throw new ArgumentException("Media detail windows must contain at most three valid ranges within 60 minutes.");
        }
    }

    private static async Task AcceptSpecialRunAsync(HttpContext context, RunRequest request)
    {
        var idempotencyKey = context.Request.Headers[GoAiHeaders.IdempotencyKey].FirstOrDefault();
        if (idempotencyKey?.Length > 128)
        {
            throw new ArgumentException("Idempotency-Key may contain at most 128 characters.");
        }

        var repository = context.RequestServices.GetRequiredService<RunRepository>();
        var queue = context.RequestServices.GetRequiredService<RunWorkChannel>();
        var (snapshot, created) = await repository.CreateAsync(request, idempotencyKey, context.RequestAborted).ConfigureAwait(false);
        if (created)
        {
            await queue.EnqueueAsync(snapshot.RunId, context.RequestAborted).ConfigureAwait(false);
        }

        context.Response.StatusCode = created ? StatusCodes.Status202Accepted : StatusCodes.Status200OK;
        await WriteJsonAsync(
            context,
            new RunAccepted(snapshot.RunId, snapshot.State, snapshot.CreatedAt, $"/v1/runs/{Uri.EscapeDataString(snapshot.RunId)}/events")).ConfigureAwait(false);
    }

    private static Task<T> ReadJsonAsync<T>(HttpContext context) =>
        GatewayRequestReader.ReadJsonAsync<T>(context, JsonOptions);

    private static async Task<ClientToolResult> ReadClientToolResultAsync(HttpContext context)
    {
        var maximum = GoAiProtocol.MaximumToolResultTextBytes + (64 * 1024);
        if (context.Request.ContentLength is > 0 && context.Request.ContentLength > maximum)
        {
            throw new ArgumentException("Client tool result exceeds the 4 MiB protocol limit.");
        }

        using var buffer = new MemoryStream();
        var bytes = new byte[64 * 1024];
        while (true)
        {
            var read = await context.Request.Body.ReadAsync(bytes, context.RequestAborted).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            if (buffer.Length + read > maximum)
            {
                throw new ArgumentException("Client tool result exceeds the 4 MiB protocol limit.");
            }
            buffer.Write(bytes, 0, read);
        }
        buffer.Position = 0;
        return await JsonSerializer.DeserializeAsync<ClientToolResult>(buffer, JsonOptions, context.RequestAborted).ConfigureAwait(false)
            ?? throw new JsonException("Request body is required.");
    }

    private static Task WriteJsonAsync<T>(HttpContext context, T value) =>
        context.Response.WriteAsJsonAsync(value, JsonOptions, context.RequestAborted);

    private static string GetRouteString(HttpContext context, string name) =>
        Convert.ToString(context.Request.RouteValues[name], CultureInfo.InvariantCulture)
        ?? throw new ArgumentException($"Route value {name} is required.");

    private static long ParseLastEventId(HttpContext context)
    {
        var value = context.Request.Headers[GoAiHeaders.LastEventId].FirstOrDefault()
            ?? context.Request.Query["after"].FirstOrDefault();
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id >= 0
            ? id
            : 0;
    }

    private static bool IsTerminal(RunState state) => state is
        RunState.Completed or RunState.Failed or RunState.Cancelled or RunState.Interrupted;

}
