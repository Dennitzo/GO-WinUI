using System.Text.Json;
using GoAi.Contracts;
using GoWinUI.App.Services;
using GoWinUI.Core.Models;

namespace GoWinUI.Tests;

public sealed class ChatRunBudgetTests
{
    private static readonly JsonSerializerOptions ProtocolJson = GoAiProtocol.CreateJsonOptions();

    [Fact]
    public void CodingRunHasNoWallClockDeadlineAndKeepsTheFullModelWindow()
    {
        var limits = GoAiAssistantService.CreateChatRunLimits(262_144, unlimitedDuration: true);
        var json = JsonSerializer.SerializeToElement(limits, ProtocolJson);
        Assert.Equal(0, json.GetProperty("timeoutSeconds").GetInt32());
        Assert.Equal(262_144, limits.MaximumContextTokens);
        Assert.Null(limits.MaximumOutputTokens);
        Assert.Equal(limits, json.Deserialize<RunLimits>(ProtocolJson));
    }

    [Theory]
    [InlineData(32_768)]
    [InlineData(49_152)]
    [InlineData(131_072)]
    [InlineData(262_144)]
    public void ChatLimitsPreserveTheActualModelWindowWithoutSerializingAnOutputCap(int actualContextLength)
    {
        var limits = GoAiAssistantService.CreateChatRunLimits(actualContextLength);

        Assert.Equal(actualContextLength, limits.MaximumContextTokens);
        Assert.Null(limits.MaximumOutputTokens);
        Assert.Equal(3_600, limits.TimeoutSeconds);
        var serialized = JsonSerializer.SerializeToElement(limits, ProtocolJson);
        Assert.Equal(actualContextLength, serialized.GetProperty("maximumContextTokens").GetInt32());
        Assert.False(serialized.TryGetProperty("maximumOutputTokens", out _));
        var roundTrip = serialized.Deserialize<RunLimits>(ProtocolJson)!;
        Assert.Equal(limits, roundTrip);
    }

    [Theory]
    [InlineData(1_200)]
    [InlineData(8_000)]
    [InlineData(50_000)]
    public void PreparationCharacterTargetsDoNotCutOffModelReasoningOrSetAnOutputTokenCap(int targetCharacters)
    {
        var sessionId = Guid.NewGuid();
        const string modelId = "coding/local-reasoning-model~runtime";
        const int actualContextLength = 131_072;
        ContentPart[] parts = [new("text", "Bewahre die Entscheidungen des bisherigen Gesprächs."),
            new("text", "Originalverlauf mit unveränderten Benutzeranforderungen.")];

        var request = SessionContextPreparationService.CreatePreparationRunRequest(
            sessionId, parts, modelId, actualContextLength, targetCharacters);

        Assert.Equal(RunMode.General, request.Mode);
        Assert.Equal(ConversationProfile.ContextPreparation, request.ConversationProfile);
        Assert.Equal(sessionId.ToString("D"), request.SessionId);
        Assert.Equal(modelId, request.PreferredGeneralModelId);
        Assert.Null(request.PreferredCodingModelId);
        Assert.Equal(parts, Assert.Single(request.Messages).Content);
        Assert.Empty(request.ClientCapabilities!);
        Assert.Empty(request.AllowedServerTools!);
        Assert.Null(request.ReasoningEffort);
        Assert.NotNull(request.Limits);
        Assert.Null(request.Limits.MaximumOutputTokens);
        Assert.Equal(actualContextLength, request.Limits.MaximumContextTokens);
        Assert.Equal(3_600, request.Limits.TimeoutSeconds);
        var serialized = JsonSerializer.SerializeToElement(request, ProtocolJson);
        Assert.False(serialized.GetProperty("limits").TryGetProperty("maximumOutputTokens", out _));
        Assert.False(serialized.TryGetProperty("reasoningEffort", out _));
    }

    [Fact]
    public void LargerActualCodingWindowsRetainMoreCompleteHistoryInChronologicalOrder()
    {
        var sessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        ChatMessage[] history =
        [
            new(Guid.NewGuid(), sessionId, ChatRole.User, new string('a', 400_000), MessageStatus.Completed, now, now),
            new(Guid.NewGuid(), sessionId, ChatRole.Assistant, new string('b', 180_000), MessageStatus.Completed, now, now),
            new(Guid.NewGuid(), sessionId, ChatRole.User, new string('c', 20_000), MessageStatus.Completed, now, now),
        ];
        const string prompt = "Berücksichtige die bisherigen Entscheidungen und prüfe die nächste Änderung.";

        var small = GoAiAssistantService.BuildHistoryMessages(history,
            GoAiAssistantService.CalculateCodingHistoryBudget(32_768, prompt));
        var medium = GoAiAssistantService.BuildHistoryMessages(history,
            GoAiAssistantService.CalculateCodingHistoryBudget(131_072, prompt));
        var large = GoAiAssistantService.BuildHistoryMessages(history,
            GoAiAssistantService.CalculateCodingHistoryBudget(262_144, prompt));

        Assert.Equal(history[2].Content, string.Concat(Assert.Single(small).Content.Select(part => part.Text)));
        Assert.Equal(2, medium.Count);
        Assert.Equal(history[1].Content, string.Concat(medium[0].Content.Select(part => part.Text)));
        Assert.Equal(history[2].Content, string.Concat(medium[1].Content.Select(part => part.Text)));
        Assert.Equal(3, large.Count);
        Assert.Equal(history.Select(message => message.Role == ChatRole.Assistant ? "assistant" : "user"), large.Select(message => message.Role));
        for (var index = 0; index < history.Length; index++)
            Assert.Equal(history[index].Content, string.Concat(large[index].Content.Select(part => part.Text)));
    }

    [Fact]
    public void LargeHistoryKeepsTheNewest499MessagesAndReservesOneProtocolSlotForTheCurrentPrompt()
    {
        var sessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var history = Enumerable.Range(0, 600)
            .Select(index => new ChatMessage(Guid.NewGuid(), sessionId,
                index % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                new string('x', index + 1), MessageStatus.Completed, now, now))
            .ToArray();
        const string prompt = "Führe den nächsten Schritt aus.";

        var messages = GoAiAssistantService.BuildHistoryMessages(history,
            GoAiAssistantService.CalculateCodingHistoryBudget(262_144, prompt)).ToList();

        Assert.Equal(499, messages.Count);
        Assert.Equal(history.Skip(101).Select(message => message.Content),
            messages.Select(message => string.Concat(message.Content.Select(part => part.Text))));
        messages.Add(new RunMessage("user", [new ContentPart("text", Text: prompt)]));
        Assert.Equal(500, messages.Count);
        Assert.Equal(prompt, Assert.Single(messages[^1].Content).Text);
    }

    [Fact]
    public void OversizedCurrentPromptLeavesNoHistoryAndBudgetArithmeticRemainsBounded()
    {
        const int actualContextLength = 32_768;
        var normal = GoAiAssistantService.CalculateCodingHistoryBudget(actualContextLength, "Kurzer Auftrag");
        var largerPrompt = GoAiAssistantService.CalculateCodingHistoryBudget(actualContextLength, new string('x', 30_000));
        var exhausted = GoAiAssistantService.CalculateCodingHistoryBudget(actualContextLength, new string('x', 100_000));

        Assert.True(normal > largerPrompt);
        Assert.True(largerPrompt > 0);
        Assert.Equal(0, exhausted);
        var now = DateTimeOffset.UtcNow;
        ChatMessage[] history = [new(Guid.NewGuid(), Guid.NewGuid(), ChatRole.User, "Vorherige Nachricht", MessageStatus.Completed, now, now)];
        Assert.Empty(GoAiAssistantService.BuildHistoryMessages(history, exhausted));
        Assert.Equal(0, GoAiAssistantService.CalculateCodingHistoryBudget(2_048, "Auftrag"));
        Assert.Equal(1_200_000, GoAiAssistantService.CalculateCodingHistoryBudget(int.MaxValue, "Auftrag"));
    }
}
