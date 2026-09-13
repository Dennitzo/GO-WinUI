using System.Text.Json;
using GoAi.Contracts;

namespace GoAi.Server.Core.Models;

internal sealed record ModelPreparation(string InstanceId, bool WasAlreadyLoaded, int ContextLength = 0);

public sealed record ModelRuntimeProgress(
    string State,
    string? ToolName = null,
    int? ArgumentCharacters = null,
    double? PromptProgress = null,
    int? PromptTokens = null,
    int? ProcessedPromptTokens = null,
    int? GeneratedTokens = null,
    double? TokensPerSecond = null,
    int? CurrentTokens = null,
    int? Attempt = null,
    string? FailureKind = null,
    bool? ToolArgumentsJsonComplete = null,
    int? ContentCharacters = null,
    bool? FinishObserved = null,
    string? ContentDelta = null,
    string? ReasoningDelta = null);

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
    int ReasoningTokens = 0,
    ModelTurnMetrics? Metrics = null);

public sealed class ModelEmptyResponseException(bool hadReasoning, int attempts)
    : InvalidOperationException(hadReasoning
        ? $"Das lokale Modell hat nach {attempts} aufeinanderfolgenden Anfragen nur Denktext geliefert, aber keinen Antworttext oder vollständigen Werkzeugaufruf. Bereits ausgeführte Änderungen und der Arbeitsstand bleiben gespeichert. Der Lauf kann fortgesetzt werden."
        : $"Das lokale Modell hat nach {attempts} aufeinanderfolgenden Anfragen keinen Antworttext oder vollständigen Werkzeugaufruf geliefert. Bereits ausgeführte Änderungen und der Arbeitsstand bleiben gespeichert. Der Lauf kann fortgesetzt werden.")
{
    public bool HadReasoning { get; } = hadReasoning;
    public int Attempts { get; } = attempts;
}

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

public sealed class ModelProviderRequestException(string phase, int attempts, Exception innerException)
    : HttpRequestException(CreateMessage(phase, attempts, innerException), innerException,
        (innerException as HttpRequestException)?.StatusCode)
{
    public string Phase { get; } = phase;

    private static string CreateMessage(string phase, int attempts, Exception exception)
    {
        var operation = phase switch { "token_counting" => "Prompt-Tokenzählung", "model_selection" => "Modellerkennung", "model_loading" => "Modellvorbereitung", _ => "Modellanfrage" };
        var detail = exception.Message[..Math.Min(exception.Message.Length, 1000)];
        detail = string.Concat(detail.Select(static character => char.IsControl(character) ? ' ' : character)).Trim();
        return $"Die Verbindung zum nativen Windows-llama-Modell ist bei der {operation} nach {attempts} Versuchen fehlgeschlagen. {detail}";
    }
}
