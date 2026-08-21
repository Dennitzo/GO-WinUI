using System.Text.Json.Serialization;
using System.Text.Json;

namespace GoAi.Server.Core.Models;

internal sealed record LmStudioModelList(
    [property: JsonPropertyName("models")] IReadOnlyList<LmStudioModel> Models);

internal sealed record LmStudioModel(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("loaded_instances")] IReadOnlyList<LmStudioLoadedInstance>? LoadedInstances,
    [property: JsonPropertyName("max_context_length")] int MaximumContextLength,
    [property: JsonPropertyName("capabilities")] LmStudioCapabilities? Capabilities);

internal sealed record LmStudioLoadedInstance(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("model_instance_id")] string? ModelInstanceId,
    [property: JsonPropertyName("config")] LmStudioLoadConfig? Config);

internal sealed record LmStudioLoadConfig(
    [property: JsonPropertyName("context_length")] int ContextLength,
    [property: JsonPropertyName("parallel")] int? Parallel,
    [property: JsonPropertyName("flash_attention")] bool? FlashAttention,
    [property: JsonPropertyName("offload_kv_cache_to_gpu")] bool? OffloadKvCacheToGpu);

internal sealed record LmStudioCapabilities(
    [property: JsonPropertyName("vision")] bool Vision,
    [property: JsonPropertyName("trained_for_tool_use")] bool TrainedForToolUse);

internal sealed record LmStudioLoadResponse(
    [property: JsonPropertyName("instance_id")] string? InstanceId,
    [property: JsonPropertyName("model_instance_id")] string? ModelInstanceId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("load_time_seconds")] double LoadTimeSeconds,
    [property: JsonPropertyName("load_config")] LmStudioLoadConfig? LoadConfig);

internal sealed record LmStudioModelPreparation(
    string InstanceId,
    bool WasAlreadyLoaded);

public sealed record LmChatMessage(
    string Role,
    string? Content = null,
    IReadOnlyList<LmToolCall>? ToolCalls = null,
    string? ToolCallId = null);

public sealed record LmToolCall(
    string Id,
    string Name,
    JsonElement Arguments);

public sealed record LmToolDefinition(
    string Name,
    string Description,
    JsonElement Parameters);

public sealed record LmChatResult(
    string? Content,
    IReadOnlyList<LmToolCall> ToolCalls,
    int InputTokens,
    int OutputTokens);

public sealed class LmStudioContextLengthException(
    string modelId,
    int requestedContextLength,
    int availableContextLength)
    : InvalidOperationException(
        $"Das Coding-Modell '{modelId}' stellt nur {availableContextLength:N0} statt der erforderlichen {requestedContextLength:N0} Kontexttoken bereit.")
{
    public string ModelId { get; } = modelId;

    public int RequestedContextLength { get; } = requestedContextLength;

    public int AvailableContextLength { get; } = availableContextLength;
}
