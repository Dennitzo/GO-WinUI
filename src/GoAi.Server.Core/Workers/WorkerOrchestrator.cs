using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runtime;
using GoAi.Server.Core.Storage;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace GoAi.Server.Core.Workers;

public sealed class WorkerOrchestrator : IDisposable
{
    internal const string ResidentSpeechWorkerName = "speech";

    private readonly WorkerApiClient _workers;
    private readonly LmStudioClient _lmStudio;
    private readonly GpuLeaseScheduler _scheduler;
    private readonly ArtifactService _artifacts;
    private readonly GoAiServerOptions _options;
    private readonly ServerRuntimeState _runtime;
    private readonly SemaphoreSlim _resourceTransitionGate = new(1, 1);
    private readonly ConcurrentDictionary<string, SpeechSessionState> _speechSessions = new(StringComparer.Ordinal);

    public WorkerOrchestrator(
        WorkerApiClient workers,
        LmStudioClient lmStudio,
        GpuLeaseScheduler scheduler,
        ArtifactService artifacts,
        IOptions<GoAiServerOptions> options,
        ServerRuntimeState runtime)
    {
        _workers = workers;
        _lmStudio = lmStudio;
        _scheduler = scheduler;
        _artifacts = artifacts;
        _options = options.Value;
        _runtime = runtime;
    }

    public async Task<TranscriptionResponse> TranscribeAsync(
        TranscriptionRequest request,
        string? runId = null,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _scheduler.AcquireAsync(
            "speech-to-text",
            runId,
            GpuLeaseMode.Speech,
            cancellationToken).ConfigureAwait(false);
        await PrepareWorkerAsync("speech", cancellationToken).ConfigureAwait(false);
        return await _workers.TranscribeAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TranscriptionResponse> TranscribeLiveCaptionAsync(
        ReadOnlyMemory<byte> waveAudio,
        string? language,
        LiveCaptionMode mode,
        string sessionId,
        string? previousContext,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _scheduler.AcquireAsync(
            "live-caption",
            sessionId,
            GpuLeaseMode.Speech,
            cancellationToken).ConfigureAwait(false);
        await PrepareWorkerAsync("speech", cancellationToken).ConfigureAwait(false);
        return await _workers.TranscribeLiveCaptionAsync(
            waveAudio,
            language,
            mode,
            sessionId,
            previousContext,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task PrepareLiveCaptionResourcesAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _scheduler.AcquireAsync(
            "live-caption-warmup",
            sessionId,
            GpuLeaseMode.Speech,
            cancellationToken).ConfigureAwait(false);
        await PrepareWorkerAsync("speech", cancellationToken).ConfigureAwait(false);
        _ = await _workers.LoadSpeechComponentAsync("stt", cancellationToken).ConfigureAwait(false);
        _ = await _workers.LoadSpeechComponentAsync("speaker", cancellationToken).ConfigureAwait(false);
    }

    public async Task<(IReadOnlyList<TranscriptionSegment> Segments, string ModelId)> TranslateCaptionSegmentsAsync(
        IReadOnlyList<TranscriptionSegment> segments,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var modelId = _options.GeneralModelId;
        var contextLength = _options.GeneralContextLength;
        if (segments.Count == 0)
        {
            return (segments, modelId);
        }
        await using var lease = await _scheduler.AcquireAsync(
            "caption-translation",
            sessionId,
            GpuLeaseMode.Shared,
            cancellationToken).ConfigureAwait(false);
        _ = await _lmStudio.EnsureModelLoadedAsync(
            modelId,
            contextLength,
            cancellationToken).ConfigureAwait(false);
        var input = JsonSerializer.Serialize(segments.Select(static segment => segment.Text));
        var result = await _lmStudio.CompleteChatAsync(
            modelId,
            [
                new LmChatMessage(
                    "system",
                    "Gib jeden Textabschnitt ausschließlich auf Deutsch aus. Übersetze alle nichtdeutschen Sprachen vollständig ins natürliche Deutsche; lasse bereits deutsche Wörter sowie Namen, Zahlen, Einheiten und TGA-Fachbegriffe unverändert. Gemischtsprachige Abschnitte müssen ebenfalls vollständig deutsch werden. Antworte ausschließlich als JSON-Array aus Zeichenketten, in derselben Reihenfolge und mit exakt derselben Anzahl Elemente wie die Eingabe."),
                new LmChatMessage("user", input),
            ],
            [],
            maximumOutputTokens: 1_024,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var content = result.Content
            ?? throw new JsonException("Caption translation did not return content.");
        var firstBracket = content.IndexOf('[', StringComparison.Ordinal);
        var lastBracket = content.LastIndexOf(']');
        if (firstBracket < 0 || lastBracket <= firstBracket)
        {
            throw new JsonException("Caption translation did not return a JSON array.");
        }
        using var document = JsonDocument.Parse(content[firstBracket..(lastBracket + 1)]);
        if (document.RootElement.ValueKind != JsonValueKind.Array
            || document.RootElement.GetArrayLength() != segments.Count)
        {
            throw new JsonException("Caption translation returned an unexpected number of segments.");
        }
        var translated = document.RootElement.EnumerateArray().Select(static item => item.GetString()).ToArray();
        if (translated.Any(static item => string.IsNullOrWhiteSpace(item)))
        {
            throw new JsonException("Caption translation returned an empty segment.");
        }
        return (
            segments.Select((segment, index) => segment with { Text = translated[index]!.Trim() }).ToArray(),
            modelId);
    }

    public async Task WarmSpeechResourcesAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await _scheduler.AcquireAsync("speech-startup-warmup", "server-startup", GpuLeaseMode.Speech, cancellationToken).ConfigureAwait(false);
        await PrepareWorkerAsync("speech", cancellationToken).ConfigureAwait(false);
        _ = await _workers.LoadSpeechComponentAsync("stt", cancellationToken).ConfigureAwait(false);
        _ = await _workers.LoadSpeechComponentAsync("speaker", cancellationToken).ConfigureAwait(false);
        _ = await _workers.LoadSpeechComponentAsync("tts", cancellationToken).ConfigureAwait(false);
        _runtime.WriteLog(
            "Information",
            "speech.warm",
            "Whisper, ECAPA und Supertonic F5 Ultra wurden dauerhaft vorgewärmt.");
    }

    public async Task WarmAuxiliaryWorkersAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await _scheduler.AcquireAsync(
            "worker-startup-warmup",
            "server-startup",
            GpuLeaseMode.Shared,
            cancellationToken).ConfigureAwait(false);
        await PrepareWorkerAsync("media", cancellationToken).ConfigureAwait(false);
        _ = await _workers.LoadWorkerAsync("media", cancellationToken).ConfigureAwait(false);
        _ = await _workers.LoadWorkerAsync("image", cancellationToken).ConfigureAwait(false);
        _runtime.WriteLog(
            "Information",
            "workers.shared.warm",
            "Media- und Z-Image-Dienst wurden vorgewärmt.");
    }

    public async Task WarmAllStartupResourcesAsync(CancellationToken cancellationToken = default)
    {
        await WarmSpeechResourcesAsync(cancellationToken).ConfigureAwait(false);
        // Only speech input, speaker separation and TTS are resident for the
        // GO AI Server lifetime. Startup never selects an LM Studio model: an
        // already loaded model is preserved and the next AI run chooses its target.
        _runtime.WriteLog(
            "Information",
            "models.startup.on_demand",
            "LM-Studio-Modelle werden ausschließlich durch konkrete AI-Läufe geladen; der vorhandene Modellzustand bleibt unverändert.");
    }

    public async Task<SpeechSessionSnapshot> BeginSpeechSessionAsync(
        SpeechSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        await PrepareWorkerAsync("speech", cancellationToken).ConfigureAwait(false);
        var sessionId = $"speech-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var worker = await _workers.BeginSpeechSessionAsync(
            sessionId,
            request.Profile,
            cancellationToken).ConfigureAwait(false);
        var state = new SpeechSessionState(
            sessionId,
            request.Profile,
            worker.Provider,
            now);
        if (!_speechSessions.TryAdd(sessionId, state))
        {
            throw new InvalidOperationException("Die Vorlesesitzung konnte nicht registriert werden.");
        }
        return state.Snapshot();
    }

    public async Task<SpeechParagraphResponse> SynthesizeSpeechParagraphAsync(
        string sessionId,
        SpeechParagraphRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_speechSessions.TryGetValue(sessionId, out var state))
        {
            throw new KeyNotFoundException("Die Vorlesesitzung wurde nicht gefunden.");
        }

        await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var synthesis = await SynthesizeDeterministicallyAsync(
                state,
                request,
                cancellationToken).ConfigureAwait(false);
            var result = synthesis.Result;
            state.UpdatedAt = DateTimeOffset.UtcNow;
            var artifact = await ImportAsync(
                result.RelativePath,
                result.FileName,
                result.MediaType,
                result.Metadata,
                cancellationToken).ConfigureAwait(false);
            return new SpeechParagraphResponse(
                artifact,
                state.Provider,
                request.ParagraphIndex,
                ReadMetadataDouble(result.Metadata, "durationSeconds"),
                ReadMetadataInt(result.Metadata, "sampleRate"),
                synthesis.Timings,
                synthesis.AlignmentStatus,
                synthesis.AlignmentConfidence);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async Task<SpeechSessionSnapshot> EndSpeechSessionAsync(
        string sessionId,
        bool cancelled,
        CancellationToken cancellationToken = default)
    {
        if (!_speechSessions.TryRemove(sessionId, out var state))
        {
            throw new KeyNotFoundException("Die Vorlesesitzung wurde nicht gefunden.");
        }

        // Signal the worker before waiting for an in-flight paragraph so the
        // Supertonic chunk loop can stop promptly.
        _ = await _workers.EndSpeechSessionAsync(
            sessionId,
            cancellationToken).ConfigureAwait(false);

        await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            state.State = cancelled ? "cancelled" : "completed";
            state.UpdatedAt = DateTimeOffset.UtcNow;
            var snapshot = state.Snapshot();
            return snapshot;
        }
        finally
        {
            state.Gate.Release();
            state.Gate.Dispose();
        }
    }

    public async Task<SpeechResponse> SynthesizeAsync(
        SpeechRequest request,
        string? runId = null,
        CancellationToken cancellationToken = default)
    {
        var session = await BeginSpeechSessionAsync(
            new SpeechSessionRequest(),
            cancellationToken).ConfigureAwait(false);
        var cancelled = false;
        try
        {
            var paragraph = await SynthesizeSpeechParagraphAsync(
                session.SessionId,
                new SpeechParagraphRequest(request.Text, 0, request.Speed),
                cancellationToken).ConfigureAwait(false);
            return new SpeechResponse(paragraph.Artifact, paragraph.Provider);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            throw;
        }
        finally
        {
            try
            {
                _ = await EndSpeechSessionAsync(
                    session.SessionId,
                    cancelled,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _runtime.WriteLog("Warning", "speech.session.cleanup_failed", "Eine kurzlebige Vorlesesitzung konnte nicht vollständig bereinigt werden.");
            }
        }
    }

    private async Task<SpeechSynthesisOutcome> SynthesizeDeterministicallyAsync(
        SpeechSessionState state,
        SpeechParagraphRequest request,
        CancellationToken cancellationToken)
    {
        var parts = request.Parts is { Count: > 0 }
            ? request.Parts
            : [new SpeechParagraphPart(request.ParagraphIndex, request.Text)];
        var result = await SynthesizeProviderParagraphAsync(
            state,
            request,
            true,
            cancellationToken).ConfigureAwait(false);
        if (result.Timings is not { Count: > 0 }
            || result.Timings.Count != parts.Count
            || !result.Timings.Select(static timing => timing.SegmentIndex)
                .SequenceEqual(parts.Select(static part => part.SegmentIndex)))
        {
            DeleteWorkerArtifact(result.RelativePath);
            throw new InvalidDataException(
                "Die deterministische Satzsynthese lieferte keine vollständigen Zeitmarken.");
        }
        return new(
            result,
            result.Timings,
            SpeechAlignmentStatus.Deterministic,
            1.0);
    }

    private async Task<WorkerSpeechResult> SynthesizeProviderParagraphAsync(
        SpeechSessionState state,
        SpeechParagraphRequest request,
        bool forceSegmentSynthesis,
        CancellationToken cancellationToken)
    {
        await using var lease = await _scheduler.AcquireAsync(
            "text-to-speech",
            state.SessionId,
            GpuLeaseMode.Speech,
            cancellationToken).ConfigureAwait(false);
        return await _workers.SynthesizeParagraphAsync(
            state.SessionId,
            request,
            forceSegmentSynthesis,
            cancellationToken).ConfigureAwait(false);
    }

    private void DeleteWorkerArtifact(string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(_options.ResolvedWorkerDataDirectory, normalized));
        var root = Path.GetFullPath(_options.WorkerArtifactDirectory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        try
        {
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public async Task<IReadOnlyList<ArtifactDescriptor>> GenerateImagesAsync(
        ImageGenerationRequest request,
        string runId,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _scheduler.AcquireAsync("image-generation", runId, cancellationToken).ConfigureAwait(false);
        await PrepareWorkerAsync("image", cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await _workers.GenerateImageAsync(request, cancellationToken).ConfigureAwait(false);
            var artifacts = new List<ArtifactDescriptor>(result.Artifacts.Count);
            foreach (var item in result.Artifacts)
            {
                artifacts.Add(await ImportAsync(
                    item.RelativePath,
                    item.FileName,
                    item.MediaType,
                    item.Metadata,
                    cancellationToken).ConfigureAwait(false));
            }

            return artifacts;
        }
        finally
        {
            await TryReleaseWorkerAsync("image", CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task<ProcessedMediaResult> InspectMediaAsync(
        WorkerMediaRequest request,
        string runId,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _scheduler.AcquireAsync("media-analysis", runId, cancellationToken).ConfigureAwait(false);
        await PrepareWorkerAsync("media", cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await _workers.InspectMediaAsync(request, cancellationToken).ConfigureAwait(false);
            var artifacts = new List<ArtifactDescriptor>();
            foreach (var item in result.Artifacts.Concat(result.Frames))
            {
                var metadata = new Dictionary<string, string>(item.Metadata ?? new Dictionary<string, string>(), StringComparer.Ordinal);
                metadata["visibility"] = "internal";
                if (!string.IsNullOrWhiteSpace(item.Role))
                {
                    metadata["role"] = item.Role;
                }

                if (!string.IsNullOrWhiteSpace(item.Group))
                {
                    metadata["group"] = item.Group;
                }

                if (item.TimecodeSeconds is { } timecode)
                {
                    metadata["timecodeSeconds"] = timecode.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                artifacts.Add(await ImportAsync(
                    item.RelativePath,
                    item.FileName,
                    item.MediaType,
                    metadata,
                    cancellationToken).ConfigureAwait(false));
            }

            return new ProcessedMediaResult(result.Kind, result.Metadata.Clone(), artifacts);
        }
        finally
        {
            await TryReleaseWorkerAsync("media", CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task<string> PrepareLmModelAsync(
        string modelId,
        int contextLength,
        CancellationToken cancellationToken = default) =>
        (await PrepareLmModelWithStatusAsync(
            modelId,
            contextLength,
            loadingStarted: null,
            cancellationToken).ConfigureAwait(false)).InstanceId;

    internal async Task<LmStudioModelPreparation> PrepareLmModelWithStatusAsync(
        string modelId,
        int contextLength,
        Func<CancellationToken, Task>? loadingStarted,
        CancellationToken cancellationToken = default)
    {
        await _resourceTransitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (CodingModelCatalog.TryGet(modelId, out _)
                || string.Equals(modelId, _options.VisionModelId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(modelId, _options.EmbeddingModelId, StringComparison.OrdinalIgnoreCase))
            {
                // Heavy LM Studio targets replace optional worker allocations,
                // while the resident speech stack remains available.
                await _workers.ReleaseAllAsync(
                    exceptWorker: ResidentSpeechWorkerName,
                    cancellationToken).ConfigureAwait(false);
            }

            return await _lmStudio.EnsureModelPreparedAsync(
                modelId,
                contextLength,
                loadingStarted,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _resourceTransitionGate.Release();
        }
    }

    private static async Task PrepareWorkerAsync(string workerName, CancellationToken cancellationToken)
    {
        _ = workerName switch
        {
            "speech" or "media" or "image" => workerName,
            _ => throw new ArgumentOutOfRangeException(nameof(workerName)),
        };
        // Worker preparation is intentionally independent of LM Studio. Media,
        // image and speech work must not cause an implicit General-AI transition.
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task TryReleaseWorkerAsync(string workerName, CancellationToken cancellationToken)
    {
        try
        {
            await _workers.ReleaseAsync(workerName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or IOException)
        {
            // An unavailable optional worker cannot hold GPU memory.
        }
    }

    private static double ReadMetadataDouble(
        IReadOnlyDictionary<string, string>? metadata,
        string key) =>
        metadata is not null
        && metadata.TryGetValue(key, out var value)
        && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

    private static int ReadMetadataInt(
        IReadOnlyDictionary<string, string>? metadata,
        string key) =>
        metadata is not null
        && metadata.TryGetValue(key, out var value)
        && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

    public void Dispose()
    {
        foreach (var session in _speechSessions.Values)
        {
            session.Gate.Dispose();
        }
        _speechSessions.Clear();
        _resourceTransitionGate.Dispose();
    }

    private async Task<ArtifactDescriptor> ImportAsync(
        string relativePath,
        string fileName,
        string mediaType,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken)
    {
        var normalizedRelative = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var source = Path.GetFullPath(Path.Combine(_options.ResolvedWorkerDataDirectory, normalizedRelative));
        var allowedRoot = Path.GetFullPath(_options.WorkerArtifactDirectory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!source.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Worker artifact path escaped the configured data scope.");
        }

        var artifact = await _artifacts.ImportAsync(
            source,
            fileName,
            mediaType,
            metadata,
            cancellationToken).ConfigureAwait(false);
        try
        {
            File.Delete(source);
        }
        catch (IOException)
        {
            _runtime.WriteLog("Warning", "worker.artifact.cleanup_failed", "Worker-Zwischendatei konnte nicht entfernt werden.");
        }
        catch (UnauthorizedAccessException)
        {
            _runtime.WriteLog("Warning", "worker.artifact.cleanup_failed", "Worker-Zwischendatei konnte nicht entfernt werden.");
        }

        return artifact;
    }

    private sealed class SpeechSessionState
    {
        public SpeechSessionState(
            string sessionId,
            SpeechContentProfile profile,
            string provider,
            DateTimeOffset createdAt)
        {
            SessionId = sessionId;
            Profile = profile;
            Provider = provider;
            CreatedAt = createdAt;
            UpdatedAt = createdAt;
        }

        public string SessionId { get; }
        public SpeechContentProfile Profile { get; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public string State { get; set; } = "active";
        public string Provider { get; }
        public bool GeneralModelEjected { get; set; }
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset UpdatedAt { get; set; }

        public SpeechSessionSnapshot Snapshot() => new(
            SessionId,
            State,
            Profile,
            Provider,
            GeneralModelEjected,
            CreatedAt,
            UpdatedAt);
    }

    private sealed record SpeechSynthesisOutcome(
        WorkerSpeechResult Result,
        IReadOnlyList<SpeechParagraphTiming> Timings,
        SpeechAlignmentStatus AlignmentStatus,
        double? AlignmentConfidence);
}

public sealed record ProcessedMediaResult(
    string Kind,
    JsonElement Metadata,
    IReadOnlyList<ArtifactDescriptor> Artifacts);
