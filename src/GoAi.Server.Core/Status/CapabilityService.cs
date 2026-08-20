using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using Microsoft.Extensions.Options;

namespace GoAi.Server.Core.Status;

public sealed class CapabilityService
{
    private readonly GoAiServerOptions _options;

    public CapabilityService(IOptions<GoAiServerOptions> options)
    {
        _options = options.Value;
    }

    public CapabilitySnapshot GetSnapshot() => new(
        GoAiProtocol.Version,
        typeof(CapabilityService).Assembly.GetName().Version?.ToString() ?? "1.0.0",
        [
            new ModelCapability(_options.GeneralModelId, "general", _options.GeneralContextLength, true, false, false),
            .. CodingModelCatalog.Models.Select(static profile =>
                new ModelCapability(profile.Id, "code", profile.ContextLength, true, false, false)),
            new ModelCapability(_options.VisionModelId, "vision", 65536, true, true, false),
            new ModelCapability(_options.EmbeddingModelId, "embedding", 8192, false, false, false),
        ],
        [
            "web.search", "web.fetch", "youtube.search", "media.inspect", "media.analyze",
            "image.generate", "math.evaluate", "context.embed", "context.retrieve",
        ],
        [
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
}
