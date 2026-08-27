using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Models;
using Microsoft.Extensions.Options;

namespace GoAi.Server.Core.Status;

public sealed class CapabilityService
{
    private readonly GoAiServerOptions _options;
    private readonly ModelRuntimeClient? _modelRuntime;

    public CapabilityService(IOptions<GoAiServerOptions> options)
    {
        _options = options.Value;
    }

    public CapabilityService(IOptions<GoAiServerOptions> options, ModelRuntimeClient modelRuntime)
        : this(options)
    {
        _modelRuntime = modelRuntime;
    }

    public async Task<CapabilitySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (_modelRuntime is null)
        {
            return GetSnapshot();
        }

        var status = await _modelRuntime.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var dynamicModels = status.Models.Count > 0
            ? status.Models.Select(static model => new ModelCapability(
                model.Id,
                model.Role,
                model.ContextTokens,
                model.SupportsTools,
                model.SupportsVision,
                false,
                model.ReasoningEfforts,
                model.DefaultReasoningEffort)).ToArray()
            : null;
        return CreateSnapshot(dynamicModels);
    }

    public CapabilitySnapshot GetSnapshot() => CreateSnapshot(null);

    private CapabilitySnapshot CreateSnapshot(IReadOnlyList<ModelCapability>? dynamicModels) => new(
        GoAiProtocol.Version,
        typeof(CapabilityService).Assembly.GetName().Version?.ToString() ?? "1.0.0",
        dynamicModels ?? [
            CreateModelCapability(_options.GeneralModelId, "general", _options.GeneralContextLength, true, false),
            .. CodingModelCatalog.Models.Select(static profile =>
                CreateModelCapability(profile.Id, "code", profile.ContextLength, true, false)),
            CreateModelCapability(_options.VisionModelId, "vision", _options.VisionContextLength, true, true),
            CreateModelCapability(_options.EmbeddingModelId, "embedding", _options.EmbeddingContextLength, false, false),
        ],
        [
            "web.search", "web.fetch", "youtube.search", "media.inspect", "media.analyze",
            "image.generate", "math.evaluate", "context.embed", "context.retrieve",
        ],
        [
            ClientToolNames.DocumentRead,
            ClientToolNames.DocumentCreate,
            ClientToolNames.DocumentsList,
            ClientToolNames.DocumentsSearch,
            ClientToolNames.DocumentsReadPages,
            ClientToolNames.WorkspaceMap,
            ClientToolNames.FileSystemList,
            ClientToolNames.FileSystemStat,
            ClientToolNames.FileSystemFindFiles,
            ClientToolNames.FileSystemReadText,
            ClientToolNames.FileSystemReadMany,
            ClientToolNames.FileSystemSearch,
            ClientToolNames.FileSystemWriteText,
            ClientToolNames.FileSystemReplaceText,
            ClientToolNames.FileSystemMove,
            ClientToolNames.FileSystemProposePatch,
            ClientToolNames.FileSystemProposeCreate,
            ClientToolNames.FileSystemProposeDelete,
            ClientToolNames.ProcessRunPreset,
            ClientToolNames.ProcessRun,
            ClientToolNames.LeanProof,
            ClientToolNames.BricsCadGeometryQuery,
            ClientToolNames.BricsCadMeasure,
            ClientToolNames.BricsCadMove,
            ClientToolNames.BricsCadAction,
        ],
        new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["image"] = 25L * 1024 * 1024,
            ["audio"] = 100L * 1024 * 1024,
            ["video"] = 500L * 1024 * 1024,
            ["json"] = GoAiProtocol.MaximumJsonBytes,
            ["clientToolText"] = GoAiProtocol.MaximumToolResultTextBytes,
        },
        [
            "image/png", "image/jpeg", "image/webp", "audio/wav", "audio/mpeg", "audio/ogg",
            "video/mp4", "video/webm", "application/pdf", "text/plain",
        ],
        true,
        GoAiProtocol.UploadChunkSize,
        new LiveCaptionCapability(
            true,
            ["audio/wav; codecs=pcm_s16le"],
            [GoAiProtocol.LiveCaptionSampleRate],
            GoAiProtocol.MaximumLiveCaptionChunkBytes,
            1_000,
            10_000,
            true));

    private static ModelCapability CreateModelCapability(
        string modelId,
        string role,
        int contextTokens,
        bool supportsTools,
        bool supportsVision)
    {
        var reasoning = ModelReasoningProfiles.Resolve(modelId, role);
        return new ModelCapability(
            modelId,
            role,
            contextTokens,
            supportsTools,
            supportsVision,
            false,
            reasoning.SupportedEfforts,
            reasoning.DefaultEffort);
    }
}
