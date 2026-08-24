using GoAi.Server.Core.Models;
using System.Text.Json;

namespace GoAi.Server.Core.Runs;

public sealed record CodingContextPlan(
    IReadOnlyList<LmChatMessage> Messages,
    int EstimatedInputTokens,
    int InputTokenBudget,
    bool WasCompacted,
    string? Notice);

public static class CodingContextPlanner
{
    private const int CharactersPerEstimatedToken = 3;
    private const int PerMessageTokenOverhead = 32;
    private const int MaximumHistoricalToolArgumentCharacters = 16_384;

    public static CodingContextPlan Prepare(
        IReadOnlyList<LmChatMessage> source,
        int contextLength,
        int maximumOutputTokens)
    {
        ArgumentNullException.ThrowIfNull(source);
        var safetyTokens = Math.Min(8_192, Math.Max(2_048, contextLength / 16));
        var budget = Math.Max(1_024, contextLength - maximumOutputTokens - safetyTokens);
        var conversation = CompactRepeatedConversationMessages(source, out var conversationCompacted);
        var messages = conversation.Select(CompactHistoricalToolArguments).ToArray();
        var compacted = conversationCompacted || !messages.SequenceEqual(conversation);
        var latestUserIndex = Array.FindLastIndex(messages, static message =>
            string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase));

        if (EstimateTokens(messages) > budget)
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

        if (EstimateTokens(messages) > budget)
        {
            var toolIndices = messages
                .Select((message, index) => (message, index))
                .Where(static item => string.Equals(item.message.Role, "tool", StringComparison.OrdinalIgnoreCase))
                .Select(static item => item.index)
                .ToArray();
            foreach (var index in toolIndices.Take(Math.Max(0, toolIndices.Length - 12)))
            {
                if (EstimateTokens(messages) <= budget)
                {
                    break;
                }
                messages[index] = TruncateContent(messages[index], 8_192);
                compacted = true;
            }
        }

        while (EstimateTokens(messages) > budget)
        {
            var candidate = messages
                .Select((message, index) => new
                {
                    Message = message,
                    Index = index,
                    Length = message.Content?.Length ?? 0,
                    Protected = index == latestUserIndex
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
            throw new CodingContextBudgetException(estimated, budget);
        }
        return new CodingContextPlan(
            messages,
            estimated,
            budget,
            compacted,
            compacted
                ? "Ältere Chat- und Werkzeugdaten wurden verdichtet; aktuelle Quellen und Tool-IDs bleiben erhalten."
                : null);
    }

    public static int EstimateTokens(IReadOnlyList<LmChatMessage> messages)
    {
        long characters = 0;
        foreach (var message in messages)
        {
            characters += message.Content?.Length ?? 0;
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

    private static LmChatMessage CompactHistoricalToolArguments(LmChatMessage message)
    {
        if (message.ToolCalls is not { Count: > 0 }
            || message.ToolCalls.All(static call => call.Arguments.GetRawText().Length <= MaximumHistoricalToolArgumentCharacters))
        {
            return message;
        }
        var calls = message.ToolCalls.Select(call => call.Arguments.GetRawText().Length <= MaximumHistoricalToolArgumentCharacters
            ? call
            : call with
            {
                Arguments = JsonSerializer.SerializeToElement(new
                {
                    compacted = true,
                    originalCharacters = call.Arguments.GetRawText().Length,
                }),
            }).ToArray();
        return message with { ToolCalls = calls };
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

public sealed class CodingContextBudgetException(int estimatedTokens, int budgetTokens)
    : InvalidOperationException(
        $"Der Coding-Kontext benötigt geschätzt {estimatedTokens:N0} Token und überschreitet das sichere Budget von {budgetTokens:N0} Token.")
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
