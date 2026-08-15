using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runtime;
using GoAi.Server.Core.Storage;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace GoAi.Server.Core.Workers;

public sealed class WorkerOrchestrator : IDisposable
{
    private readonly WorkerApiClient _workers;
    private readonly WindowsSpeechService _windowsSpeech;
    private readonly LmStudioClient _lmStudio;
    private readonly GpuLeaseScheduler _scheduler;
    private readonly ArtifactService _artifacts;
    private readonly GoAiServerOptions _options;
    private readonly ServerRuntimeState _runtime;
    private readonly SemaphoreSlim _resourceTransitionGate = new(1, 1);
    private int _liveCaptionResourcesHeld;
    private int _sharedRuntimeReady;

    public WorkerOrchestrator(
        WorkerApiClient workers,
        WindowsSpeechService windowsSpeech,
        LmStudioClient lmStudio,
        GpuLeaseScheduler scheduler,
        ArtifactService artifacts,
        IOptions<GoAiServerOptions> options,
        ServerRuntimeState runtime)
    {
        _workers = workers;
        _windowsSpeech = windowsSpeech;
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
            GpuLeaseMode.Shared,
            cancellationToken).ConfigureAwait(false);
        await PrepareWorkerAsync("speech", cancellationToken).ConfigureAwait(false);
        try
        {
            return await _workers.TranscribeAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Speechmodelle bleiben für Folgeaufträge vorgewärmt.
        }
    }

    public async Task<TranscriptionResponse> TranscribeLiveCaptionAsync(
        ReadOnlyMemory<byte> waveAudio,
        string? language,
        LiveCaptionMode mode,
        string sessionId,
        string? previousContext,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref _liveCaptionResourcesHeld, 1);
        await using var lease = await _scheduler.AcquireAsync(
            "live-caption",
            sessionId,
            GpuLeaseMode.Shared,
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
        Interlocked.Exchange(ref _liveCaptionResourcesHeld, 1);
        try
        {
            await using var lease = await _scheduler.AcquireAsync(
                "live-caption-warmup",
                sessionId,
                GpuLeaseMode.Shared,
                cancellationToken).ConfigureAwait(false);
            await PrepareWorkerAsync("speech", cancellationToken).ConfigureAwait(false);
            _ = await _workers.LoadSpeechComponentAsync("stt", cancellationToken).ConfigureAwait(false);
            _ = await _workers.LoadSpeechComponentAsync("speaker", cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Exchange(ref _liveCaptionResourcesHeld, 0);
            throw;
        }
    }

    public async Task<IReadOnlyList<TranscriptionSegment>> TranslateCaptionSegmentsAsync(
        IReadOnlyList<TranscriptionSegment> segments,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (segments.Count == 0)
        {
            return segments;
        }
        await using var lease = await _scheduler.AcquireAsync(
            "caption-translation",
            sessionId,
            GpuLeaseMode.Shared,
            cancellationToken).ConfigureAwait(false);
        _ = await _lmStudio.EnsureModelLoadedAsync(
            _options.GeneralModelId,
            _options.GeneralContextLength,
            cancellationToken).ConfigureAwait(false);
        var input = JsonSerializer.Serialize(segments.Select(static segment => segment.Text));
        var result = await _lmStudio.CompleteChatAsync(
            _options.GeneralModelId,
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
        return segments.Select((segment, index) => segment with { Text = translated[index]!.Trim() }).ToArray();
    }

    public Task ReleaseLiveCaptionResourcesAsync()
    {
        Interlocked.Exchange(ref _liveCaptionResourcesHeld, 0);
        // Whisper and Piper TTS intentionally remain warm beside gpt-oss-20b. Laguna
        // still obtains an exclusive lease and releases every worker before loading.
        return Task.CompletedTask;
    }

    public async Task WarmSpeechResourcesAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await _scheduler.AcquireAsync("speech-startup-warmup", "server-startup", GpuLeaseMode.Shared, cancellationToken).ConfigureAwait(false);
        await PrepareWorkerAsync("speech", cancellationToken).ConfigureAwait(false);
        _ = await _workers.LoadSpeechComponentAsync("stt", cancellationToken).ConfigureAwait(false);
        _ = await _workers.LoadSpeechComponentAsync("tts", cancellationToken).ConfigureAwait(false);
        _ = await _workers.LoadSpeechComponentAsync("speaker", cancellationToken).ConfigureAwait(false);
        _runtime.WriteLog("Information", "speech.warm", "Whisper, Piper-TTS und ECAPA wurden für Sprachdienste vorgewärmt.");
    }

    public async Task WarmSharedLmModelsAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await _scheduler.AcquireAsync(
            "shared-model-startup-warmup",
            "server-startup",
            GpuLeaseMode.Shared,
            cancellationToken).ConfigureAwait(false);
        _ = await PrepareLmModelAsync(
            _options.GeneralModelId,
            _options.GeneralContextLength,
            cancellationToken).ConfigureAwait(false);
        _runtime.WriteLog(
            "Information",
            "models.shared.warm",
            "General-, Vision- und Embeddingmodell wurden für die gemeinsame GPU-Lane vorgewärmt.");
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
        await WarmSharedLmModelsAsync(cancellationToken).ConfigureAwait(false);
        await WarmSpeechResourcesAsync(cancellationToken).ConfigureAwait(false);
        await WarmAuxiliaryWorkersAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SpeechResponse> SynthesizeAsync(
        SpeechRequest request,
        string? runId = null,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _scheduler.AcquireAsync(
            "text-to-speech",
            runId,
            GpuLeaseMode.Shared,
            cancellationToken).ConfigureAwait(false);
        await PrepareWorkerAsync("speech", cancellationToken).ConfigureAwait(false);
        WorkerSpeechResult result;
        try
        {
            result = await _workers.SynthesizeAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            result = await _windowsSpeech.SynthesizeAsync(request, cancellationToken).ConfigureAwait(false);
            _runtime.WriteLog("Warning", "provider.fallback", "Piper-TTS nicht erreichbar; sichtbarer Windows-TTS-Fallback verwendet.");
        }
        finally
        {
            // Whisper und Piper-TTS bleiben neben dem gemeinsamen Generalmodell resident.
        }

        var artifact = await ImportAsync(
            result.RelativePath,
            result.FileName,
            result.MediaType,
            result.Metadata,
            cancellationToken).ConfigureAwait(false);
        return new SpeechResponse(artifact, result.Provider, result.IsFallback);
    }

    public async Task<IReadOnlyList<ArtifactDescriptor>> GenerateImagesAsync(
        ImageGenerationRequest request,
        string runId,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _scheduler.AcquireAsync("image-generation", runId, cancellationToken).ConfigureAwait(false);
        await PrepareWorkerAsync("image", cancellationToken).ConfigureAwait(false);
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

    public async Task<ProcessedMediaResult> InspectMediaAsync(
        WorkerMediaRequest request,
        string runId,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _scheduler.AcquireAsync("media-analysis", runId, cancellationToken).ConfigureAwait(false);
        await PrepareWorkerAsync("media", cancellationToken).ConfigureAwait(false);
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

    public async Task<string> PrepareLmModelAsync(
        string modelId,
        int contextLength,
        CancellationToken cancellationToken = default)
    {
        var sharedModel = IsSharedLmModel(modelId);
        await _resourceTransitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (sharedModel)
            {
                var instances = await EnsureSharedLmModelsLoadedAsync(cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _sharedRuntimeReady, 1);
                if (string.Equals(modelId, _options.GeneralModelId, StringComparison.OrdinalIgnoreCase))
                {
                    return instances.General;
                }
                if (string.Equals(modelId, _options.VisionModelId, StringComparison.OrdinalIgnoreCase))
                {
                    return instances.Vision;
                }
                return instances.Embedding;
            }

            Volatile.Write(ref _sharedRuntimeReady, 0);
            await _workers.ReleaseAllAsync(exceptWorker: null, cancellationToken).ConfigureAwait(false);
            return await _lmStudio.EnsureModelLoadedAsync(
                modelId,
                contextLength,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _resourceTransitionGate.Release();
        }
    }

    private async Task PrepareWorkerAsync(string workerName, CancellationToken cancellationToken)
    {
        _ = workerName switch
        {
            "speech" or "media" or "image" => workerName,
            _ => throw new ArgumentOutOfRangeException(nameof(workerName)),
        };
        if (Volatile.Read(ref _sharedRuntimeReady) == 1)
        {
            return;
        }

        await _resourceTransitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _sharedRuntimeReady) == 1)
            {
                return;
            }

            try
            {
                _ = await EnsureSharedLmModelsLoadedAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                !cancellationToken.IsCancellationRequested
                && (exception is HttpRequestException or TaskCanceledException))
            {
                _runtime.WriteLog("Warning", "lmstudio.shared.unavailable", "Die gemeinsamen LM-Studio-Modelle konnten beim GPU-Wechsel nicht geladen werden.");
                return;
            }
            Volatile.Write(ref _sharedRuntimeReady, 1);
        }
        finally
        {
            _resourceTransitionGate.Release();
        }
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

    private bool IsSharedLmModel(string modelId) =>
        string.Equals(modelId, _options.GeneralModelId, StringComparison.OrdinalIgnoreCase)
        || string.Equals(modelId, _options.VisionModelId, StringComparison.OrdinalIgnoreCase)
        || string.Equals(modelId, _options.EmbeddingModelId, StringComparison.OrdinalIgnoreCase);

    private async Task<(string General, string Vision, string Embedding)> EnsureSharedLmModelsLoadedAsync(
        CancellationToken cancellationToken)
    {
        var general = await _lmStudio.EnsureModelLoadedAsync(
            _options.GeneralModelId,
            _options.GeneralContextLength,
            cancellationToken).ConfigureAwait(false);
        var vision = await _lmStudio.EnsureModelLoadedAsync(
            _options.VisionModelId,
            65_536,
            cancellationToken).ConfigureAwait(false);
        var embedding = await _lmStudio.EnsureModelLoadedAsync(
            _options.EmbeddingModelId,
            8_192,
            cancellationToken).ConfigureAwait(false);
        return (general, vision, embedding);
    }

    public void Dispose() => _resourceTransitionGate.Dispose();

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
}

public sealed record ProcessedMediaResult(
    string Kind,
    JsonElement Metadata,
    IReadOnlyList<ArtifactDescriptor> Artifacts);
