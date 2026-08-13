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
            if (Volatile.Read(ref _liveCaptionResourcesHeld) == 0)
            {
                await TryReleaseAsync("speech").ConfigureAwait(false);
            }
        }
    }

    public async Task<TranscriptionResponse> TranscribeLiveCaptionAsync(
        ReadOnlyMemory<byte> waveAudio,
        string? language,
        LiveCaptionMode mode,
        string sessionId,
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
            cancellationToken).ConfigureAwait(false);
    }

    public async Task ReleaseLiveCaptionResourcesAsync()
    {
        Interlocked.Exchange(ref _liveCaptionResourcesHeld, 0);
        await TryReleaseAsync("speech").ConfigureAwait(false);
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
            _runtime.WriteLog("Warning", "provider.fallback", "Qwen3-TTS nicht erreichbar; sichtbarer Windows-TTS-Fallback verwendet.");
        }
        finally
        {
            if (Volatile.Read(ref _liveCaptionResourcesHeld) == 0)
            {
                await TryReleaseAsync("speech").ConfigureAwait(false);
            }
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
            await TryReleaseAsync("image").ConfigureAwait(false);
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
            await TryReleaseAsync("media").ConfigureAwait(false);
        }
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
            if (!sharedModel)
            {
                Volatile.Write(ref _sharedRuntimeReady, 0);
            }

            await _workers.ReleaseAllAsync(sharedModel ? "speech" : null, cancellationToken).ConfigureAwait(false);
            var instanceId = await _lmStudio.EnsureModelLoadedAsync(
                modelId,
                contextLength,
                cancellationToken).ConfigureAwait(false);
            if (sharedModel)
            {
                Volatile.Write(ref _sharedRuntimeReady, 1);
            }

            return instanceId;
        }
        finally
        {
            _resourceTransitionGate.Release();
        }
    }

    private async Task PrepareWorkerAsync(string workerName, CancellationToken cancellationToken)
    {
        if (string.Equals(workerName, "speech", StringComparison.Ordinal)
            && Volatile.Read(ref _sharedRuntimeReady) == 1)
        {
            return;
        }

        await _resourceTransitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.Equals(workerName, "speech", StringComparison.Ordinal))
            {
                if (Volatile.Read(ref _sharedRuntimeReady) == 1)
                {
                    return;
                }

                try
                {
                    await _lmStudio.UnloadModelsExceptAsync(
                        [_options.GeneralModelId, _options.EmbeddingModelId],
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                {
                    _runtime.WriteLog("Warning", "lmstudio.release.unavailable", "LM Studio war beim GPU-Wechsel nicht erreichbar.");
                }

                await _workers.ReleaseAllAsync("speech", cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _sharedRuntimeReady, 1);
            }
            else
            {
                Volatile.Write(ref _sharedRuntimeReady, 0);
                try
                {
                    await _lmStudio.UnloadAllModelsAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                {
                    _runtime.WriteLog("Warning", "lmstudio.release.unavailable", "LM Studio war beim GPU-Wechsel nicht erreichbar.");
                }

                await _workers.ReleaseAllAsync(workerName, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _resourceTransitionGate.Release();
        }
    }

    private bool IsSharedLmModel(string modelId) =>
        string.Equals(modelId, _options.GeneralModelId, StringComparison.OrdinalIgnoreCase)
        || string.Equals(modelId, _options.EmbeddingModelId, StringComparison.OrdinalIgnoreCase);

    public void Dispose() => _resourceTransitionGate.Dispose();

    private async Task TryReleaseAsync(string workerName)
    {
        try
        {
            await _workers.ReleaseAsync(workerName, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            _runtime.WriteLog("Warning", "worker.release.failed", $"Worker {workerName} konnte nach dem Lauf nicht freigegeben werden.");
        }
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
}

public sealed record ProcessedMediaResult(
    string Kind,
    JsonElement Metadata,
    IReadOnlyList<ArtifactDescriptor> Artifacts);
