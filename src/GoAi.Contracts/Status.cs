namespace GoAi.Contracts;

public sealed record HealthSnapshot(
    string Status,
    string ProtocolVersion,
    DateTimeOffset Timestamp,
    string? Reason = null,
    string? Repair = null);

public sealed record CapabilitySnapshot(
    string ProtocolVersion,
    string ServerVersion,
    IReadOnlyList<ModelCapability> Models,
    IReadOnlyList<string> ServerTools,
    IReadOnlyList<string> ClientTools,
    IReadOnlyDictionary<string, long> UploadLimits,
    IReadOnlyList<string> MediaTypes,
    bool SupportsSseResume,
    int UploadChunkSize,
    LiveCaptionCapability? LiveCaptions = null);

public sealed record LiveCaptionCapability(
    bool Available,
    IReadOnlyList<string> Formats,
    IReadOnlyList<int> SampleRates,
    int MaximumChunkBytes,
    int MinimumWindowMilliseconds,
    int MaximumWindowMilliseconds,
    bool SupportsEnglishTranslation);

public sealed record ModelCapability(
    string Id,
    string Role,
    int ContextTokens,
    bool SupportsTools,
    bool SupportsVision,
    bool IsFallback);

public sealed record ModelStatusSnapshot(
    bool ProviderReachable,
    string ProviderUrl,
    IReadOnlyList<ModelRuntimeStatus> Models,
    DateTimeOffset CheckedAt,
    string? ErrorCode = null);

public sealed record ModelRuntimeStatus(
    string Id,
    string Role,
    bool Downloaded,
    bool Loaded,
    string State,
    int ContextTokens);

public sealed record GpuStatusSnapshot(
    bool Available,
    int QueueLength,
    string? ActiveLease,
    IReadOnlyList<GpuDeviceStatus> Devices,
    DateTimeOffset CheckedAt,
    string? ErrorCode = null,
    IReadOnlyList<ActiveAiWorkload>? ActiveWorkloads = null);

public sealed record ActiveAiWorkload(
    string LeaseId,
    string Workload,
    string DisplayName,
    string Runtime,
    string? RunId,
    DateTimeOffset StartedAt);

public sealed record GpuDeviceStatus(
    int Index,
    string Name,
    long MemoryTotalMiB,
    long MemoryUsedMiB,
    int UtilizationPercent,
    int TemperatureCelsius);

public sealed record ServiceStatusSnapshot(
    string Name,
    string State,
    string Endpoint,
    bool Reachable,
    DateTimeOffset CheckedAt,
    string? Detail = null,
    bool? Loaded = null,
    IReadOnlyList<string>? LoadedComponents = null);
