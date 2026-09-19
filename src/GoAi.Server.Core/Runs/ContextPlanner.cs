using GoAi.Server.Core.Models;
using GoAi.Server.Core.Coding;

namespace GoAi.Server.Core.Runs;

public sealed record ContextPlan(
    IReadOnlyList<LmChatMessage> Messages,
    int EstimatedInputTokens,
    int InputTokenBudget,
    bool WasCompacted,
    string? Notice);

public static class ContextPlanner
{
    private const int CharactersPerEstimatedToken = 3;
    private const int PerMessageTokenOverhead = 32;

    public static ContextPlan Prepare(
        IReadOnlyList<LmChatMessage> source,
        int contextLength,
        int? maximumOutputTokens,
        bool allowLossyCompaction = true,
        bool preserveConversationPrefix = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        var budget = ComputeInputTokenBudget(contextLength, maximumOutputTokens);
        var conversationCompacted = false;
        var conversation = preserveConversationPrefix ? source.ToList()
            : CompactRepeatedConversationMessages(source, out conversationCompacted);
        var messages = preserveConversationPrefix && EstimateTokens(conversation) <= budget
            ? conversation.ToArray() : CodingEvidenceContext.CompactCompletedCalls(conversation).ToArray();
        var freshReadIds = FreshReadCallIds(messages);
        var compacted = conversationCompacted || !messages.SequenceEqual(conversation);
        var latestUserIndex = Array.FindLastIndex(messages, static message =>
            string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)
            && !ModelRuntimeClient.IsLanguageReminder(message)
            && message.Content?.StartsWith(CodingEvidenceContext.Marker, StringComparison.Ordinal) != true
            && message.Content?.StartsWith(CodingSessionContext.StateMarker, StringComparison.Ordinal) != true
            && message.Content?.StartsWith(CodingContextCompactor.MemoryMarker, StringComparison.Ordinal) != true);

        if (allowLossyCompaction && EstimateTokens(messages) > budget)
        {
            for (var index = 0; index < messages.Length && EstimateTokens(messages) > budget; index++)
            {
                if (index == latestUserIndex
                    || string.Equals(messages[index].Role, "system", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(messages[index].Role, "tool", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                messages[index] = TruncateContent(messages[index], 4_096);
                compacted = true;
            }
        }

        if (allowLossyCompaction && EstimateTokens(messages) > budget)
        {
            var toolIndices = messages
                .Select((message, index) => (message, index))
                .Where(static item => string.Equals(item.message.Role, "tool", StringComparison.OrdinalIgnoreCase))
                .Select(static item => item.index)
                .ToArray();
            foreach (var index in toolIndices.Take(Math.Max(0, toolIndices.Length - 12)))
            {
                if (freshReadIds.Contains(messages[index].ToolCallId ?? string.Empty)) continue;
                if (EstimateTokens(messages) <= budget)
                {
                    break;
                }
                messages[index] = TruncateContent(messages[index], 8_192);
                compacted = true;
            }
        }

        while (allowLossyCompaction && EstimateTokens(messages) > budget)
        {
            var candidate = messages
                .Select((message, index) => new
                {
                    Message = message,
                    Index = index,
                    Length = (message.Content?.Length ?? 0) + (message.ReasoningContent?.Length ?? 0),
                    Protected = freshReadIds.Contains(message.ToolCallId ?? string.Empty) || index == latestUserIndex
                        || string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase),
                })
                .Where(static item => !item.Protected && item.Length > 2_048)
                .OrderByDescending(static item => item.Length)
                .ThenBy(static item => item.Index)
                .FirstOrDefault();
            if (candidate is null)
            {
                break;
            }
            messages[candidate.Index] = TruncateContent(
                candidate.Message,
                Math.Max(2_048, candidate.Length / 2));
            compacted = true;
        }

        var estimated = EstimateTokens(messages);
        if (estimated > budget)
        {
            if (freshReadIds.Count > 0) throw new CodingReadContextBudgetException(estimated, budget);
            throw new ContextBudgetException(estimated, budget);
        }
        return new ContextPlan(
            messages,
            estimated,
            budget,
            compacted,
            compacted
                ? "Ältere Chat- und Werkzeugdaten wurden verdichtet; aktuelle Quellen und Tool-IDs bleiben erhalten."
                : null);
    }

    // A read is fresh until an assistant generation follows its result. Preserve every
    // read in that final tool batch, not merely the last file in a parallel batch.
    internal static HashSet<string> FreshReadCallIds(IReadOnlyList<LmChatMessage> messages)
    {
        var lastAssistant = -1;
        for (var index = messages.Count - 1; index >= 0; index--)
            if (messages[index].Role == "assistant") { lastAssistant = index; break; }
        if (lastAssistant < 0) return [];
        var readIds = (messages[lastAssistant].ToolCalls ?? [])
            .Where(call => call.Name == "coding.read").Select(call => call.Id).ToHashSet(StringComparer.Ordinal);
        return messages.Skip(lastAssistant + 1).Where(message => message.Role == "tool" && message.ToolCallId is not null
            && readIds.Contains(message.ToolCallId)).Select(message => message.ToolCallId!).ToHashSet(StringComparer.Ordinal);
    }

    public static int ComputeInputTokenBudget(int contextLength, int? maximumOutputTokens)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(contextLength, 2_048);
        if (maximumOutputTokens is { } explicitLimit) ArgumentOutOfRangeException.ThrowIfNegative(explicitLimit);
        var safetyTokens = Math.Min(8_192, Math.Max(2_048, contextLength / 16));
        // Automatic output consumes the remaining context after exact native token counting.
        // Do not reserve the whole window for output and evict the user's input.
        return Math.Max(1_024, contextLength - safetyTokens);
    }

    public static int EstimateTokens(IReadOnlyList<LmChatMessage> messages)
    {
        long characters = 0;
        foreach (var message in messages)
        {
            characters += message.Content?.Length ?? 0;
            characters += message.ReasoningContent?.Length ?? 0;
            characters += message.ToolCallId?.Length ?? 0;
            foreach (var call in message.ToolCalls ?? [])
            {
                characters += call.Id.Length + call.Name.Length + call.Arguments.GetRawText().Length;
            }
        }
        var estimated = (characters + CharactersPerEstimatedToken - 1) / CharactersPerEstimatedToken
            + (long)messages.Count * PerMessageTokenOverhead;
        return estimated >= int.MaxValue ? int.MaxValue : (int)estimated;
    }

    private static List<LmChatMessage> CompactRepeatedConversationMessages(
        IReadOnlyList<LmChatMessage> source,
        out bool compacted)
    {
        var lastPlainUserMessage = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < source.Count; index++)
        {
            var message = source[index];
            if (IsPlainMessage(message, "user")
                && NormalizeConversationContent(message.Content) is { Length: > 0 } content)
            {
                lastPlainUserMessage[content] = index;
            }
        }

        var result = new List<LmChatMessage>(source.Count);
        compacted = false;
        for (var index = 0; index < source.Count; index++)
        {
            var message = source[index];
            if (IsTransientWorkflowStatus(message))
            {
                compacted = true;
                continue;
            }
            if (IsPlainMessage(message, "user")
                && NormalizeConversationContent(message.Content) is { Length: > 0 } content
                && lastPlainUserMessage.TryGetValue(content, out var lastIndex)
                && lastIndex != index)
            {
                compacted = true;
                continue;
            }
            result.Add(message);
        }
        return result;
    }

    private static bool IsPlainMessage(LmChatMessage message, string role) =>
        string.Equals(message.Role, role, StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrWhiteSpace(message.ToolCallId)
        && message.ToolCalls is not { Count: > 0 };

    private static bool IsTransientWorkflowStatus(LmChatMessage message) =>
        IsPlainMessage(message, "assistant")
        && NormalizeConversationContent(message.Content).StartsWith(
            "**Workflow-Schritt verifiziert**",
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizeConversationContent(string? content) =>
        string.IsNullOrWhiteSpace(content)
            ? string.Empty
            : string.Join(' ', content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static LmChatMessage TruncateContent(LmChatMessage message, int maximumCharacters)
    {
        // Reasoning is part of the cached native transcript and of the context budget.
        // When lossy reduction is actually required, keep the visible answer first.
        if ((long)(message.Content?.Length ?? 0) + (message.ReasoningContent?.Length ?? 0) > maximumCharacters)
            message = message with { ReasoningContent = null };
        if (message.Content is not { } content || content.Length <= maximumCharacters)
        {
            return message;
        }
        const string marker = "\n...[Kontext verdichtet]...\n";
        var available = Math.Max(0, maximumCharacters - marker.Length);
        var head = available * 2 / 3;
        var tail = available - head;
        return message with
        {
            Content = content[..head] + marker + content[^tail..],
        };
    }
}

public sealed class ContextBudgetException(int estimatedTokens, int budgetTokens)
    : InvalidOperationException(
        $"Der Modellkontext benötigt geschätzt {estimatedTokens:N0} Token und überschreitet das sichere Budget von {budgetTokens:N0} Token.")
{
    public int EstimatedTokens { get; } = estimatedTokens;

    public int BudgetTokens { get; } = budgetTokens;
}

public sealed class DocumentContextBudgetException(
    int estimatedTokens,
    int budgetTokens,
    GoAi.Contracts.DocumentContextMode mode)
    : InvalidOperationException(
        $"Der Dokumentkontext im Modus {mode} benötigt geschätzt {estimatedTokens:N0} Token und überschreitet das sichere Budget von {budgetTokens:N0} Token.")
{
    public int EstimatedTokens { get; } = estimatedTokens;

    public int BudgetTokens { get; } = budgetTokens;

    public GoAi.Contracts.DocumentContextMode Mode { get; } = mode;
}

public sealed class SessionContextBudgetException(int estimatedTokens, int budgetTokens)
    : InvalidOperationException(
        $"Der aufbereitete Sitzungsverlauf benötigt geschätzt {estimatedTokens:N0} Token und überschreitet das sichere Budget von {budgetTokens:N0} Token.")
{
    public int EstimatedTokens { get; } = estimatedTokens;

    public int BudgetTokens { get; } = budgetTokens;
}

public sealed class GeneralContextBudgetException(int estimatedTokens, int budgetTokens)
    : InvalidOperationException(
        $"Der vorbereitete General-AI-Kontext benötigt geschätzt {estimatedTokens:N0} Token und überschreitet das sichere Budget von {budgetTokens:N0} Token.")
{
    public int EstimatedTokens { get; } = estimatedTokens;

    public int BudgetTokens { get; } = budgetTokens;
}

public sealed class CodingReadContextBudgetException(int estimatedTokens, int budgetTokens)
    : InvalidOperationException($"Der vollständige gelesene Dateiinhalt passt auch nach Verdichtung älterer Daten nicht in den Modellkontext ({estimatedTokens:N0} von {budgetTokens:N0} Token). Die Datei wurde nicht gekürzt. Wähle ausdrücklich einen Zeilenbereich oder ein Modell mit größerem Kontext.")
{
    public int EstimatedTokens { get; } = estimatedTokens;
    public int BudgetTokens { get; } = budgetTokens;
}
