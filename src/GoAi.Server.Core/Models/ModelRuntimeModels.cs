using System.Text.Json;

namespace GoAi.Server.Core.Models;

internal sealed record ModelPreparation(string InstanceId, bool WasAlreadyLoaded);

public sealed record ModelRuntimeProgress(
    string State,
    string? ToolName = null,
    int? ArgumentCharacters = null,
    double? PromptProgress = null,
    int? PromptTokens = null,
    int? ProcessedPromptTokens = null,
    int? GeneratedTokens = null,
    double? TokensPerSecond = null,
    int? CurrentTokens = null);

public sealed record LmChatMessage(
    string Role,
    string? Content = null,
    IReadOnlyList<LmToolCall>? ToolCalls = null,
    string? ToolCallId = null);

public sealed record LmToolCall(string Id, string Name, JsonElement Arguments);

public sealed record LmToolDefinition(string Name, string Description, JsonElement Parameters);

public sealed record LmChatResult(
    string? Content,
    IReadOnlyList<LmToolCall> ToolCalls,
    int InputTokens,
    int OutputTokens,
    bool HadReasoning = false,
    int ReasoningTokens = 0);

public sealed class ModelContextLengthException(
    string modelId,
    int requestedContextLength,
    int availableContextLength)
    : InvalidOperationException(
        $"Das Modell '{modelId}' stellt nur {availableContextLength:N0} statt der erforderlichen {requestedContextLength:N0} Kontexttoken bereit.")
{
    public string ModelId { get; } = modelId;
    public int RequestedContextLength { get; } = requestedContextLength;
    public int AvailableContextLength { get; } = availableContextLength;
}

public sealed class ModelGenerationTerminatedException(
    string providerCode,
    Exception? innerException = null)
    : HttpRequestException($"Die Modellgenerierung wurde beendet ({providerCode}).", innerException)
{
    public string ProviderCode { get; } = string.IsNullOrWhiteSpace(providerCode)
        ? "unknown"
        : providerCode.Trim();
}
