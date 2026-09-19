using System.Text.Json;
using GoAi.Contracts;
using GoWinUI.App.Services;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;

namespace GoWinUI.Tests;

public sealed class SubagentDisplayTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 6, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task InterleavedChildStreamsKeepIdentityTextAndReplayCursorAfterRepositoryReload()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Getrennte Agent-Verläufe");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Hauptantwort", MessageStatus.Streaming);
        var first = Event(1, RunEventTypes.ReasoningDelta, new { agentId = "child-A", round = 1, phase = "subagent", delta = "Ich prüfe ", state = "running" });
        var second = Event(2, RunEventTypes.ReasoningDelta, new { agentId = "child-B", round = 1, phase = "subagent", delta = "Andere Aufgabe.", state = "completed" });
        var third = Event(3, RunEventTypes.TextDelta, new { agentId = "child-A", round = 1, phase = "subagent", delta = "Datei erstellt.", state = "completed" });
        foreach (var item in new[] { first, second, third })
            await chats.SaveToolStepAsync(message.Id, Apply(null, item));
        var stored = (await chats.GetMessageAsync(message.Id))!;
        var firstStep = stored.ToolSteps!.Single(step => step.Id == GoAiAssistantService.SubagentDisplayStepId(first));
        var final = Event(4, RunEventTypes.ReasoningDelta, new { agentId = "child-A", round = 1, phase = "subagent", delta = "die Datei.", state = "completed" });
        await chats.SaveToolStepAsync(message.Id, Apply(firstStep, final));
        var snapshot = (await environment.Get<IConversationSnapshotRepository>().GetAsync(session.Id))!;
        stored = Assert.Single(snapshot.Messages);
        Assert.Equal("Hauptantwort", stored.Content);
        Assert.Equal(3, stored.ToolSteps!.Count);
        var reasoning = stored.ToolSteps.Single(step => step.AgentId == "child-A" && step.Tool == "assistant.reasoning");
        Assert.Equal("Ich prüfe die Datei.", reasoning.Detail);
        Assert.Equal("Andere Aufgabe.", stored.ToolSteps.Single(step => step.AgentId == "child-B").Detail);
        Assert.Equal("Datei erstellt.", stored.ToolSteps.Single(step => step.Tool == "assistant.narration").Detail);
        Assert.Null(GoAiAssistantService.ApplySubagentDisplayEvent(reasoning, first, reasoning.Id, 0, Now));
        Assert.Null(GoAiAssistantService.ApplySubagentDisplayEvent(reasoning, final, reasoning.Id, 0, Now));
        var lateToolResult = AssistantToolStep.Merge(new("write-A", "coding.write", "running", AgentId: "child-A"),
            new("write-A", "coding.write", "completed", "OK"));
        Assert.Equal("child-A", lateToolResult.AgentId);
    }

    [Fact]
    public async Task SteeredChildDisplaysKeepTheirTerminalReasonAfterPersistenceAndReplay()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Umgeleiteter Subagent");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Hauptantwort", MessageStatus.Streaming);
        string[] eventTypes = [RunEventTypes.ReasoningDelta, RunEventTypes.TextDelta, RunEventTypes.ModelGeneration];
        foreach (var type in eventTypes)
        {
            object firstPayload = type switch
            {
                RunEventTypes.ReasoningDelta => new ReasoningDeltaEvent("Empfangener Zwischenstand.", 1,
                    Phase: "subagent", ReplaceFrom: 0, State: "running", AgentId: "child-A"),
                RunEventTypes.TextDelta => new TextDeltaEvent("Empfangener Zwischenstand.", ReplaceFrom: 0,
                    AgentId: "child-A", Round: 1, Phase: "subagent", State: "running"),
                _ => new { agentId = "child-A", round = 1, phase = "subagent", state = "codingWaiting", generatedTokens = 25 },
            };
            var first = Event(1, type, firstPayload);
            var running = Apply(null, first);
            object finalPayload = type switch
            {
                RunEventTypes.ReasoningDelta => new ReasoningDeltaEvent("Empfangener Zwischenstand.", 1,
                    Phase: "subagent", ReplaceFrom: 0, State: "steered", AgentId: "child-A"),
                RunEventTypes.TextDelta => new TextDeltaEvent("Empfangener Zwischenstand.", ReplaceFrom: 0,
                    AgentId: "child-A", Round: 1, Phase: "subagent", State: "steered"),
                _ => new { agentId = "child-A", round = 1, phase = "subagent", state = "steered" },
            };
            var final = Event(2, type, finalPayload);
            var steered = Apply(running, final);
            await chats.SaveToolStepAsync(message.Id, running);
            await chats.SaveToolStepAsync(message.Id, steered);
            await chats.SaveToolStepAsync(message.Id, running);
            var stored = (await chats.GetMessageAsync(message.Id))!.ToolSteps!.Single(step => step.Id == running.Id);
            Assert.Equal("interrupted", stored.Status);
            Assert.NotNull(stored.CompletedAt);
            Assert.Equal("child-A", stored.AgentId);
            using var metadata = JsonDocument.Parse(stored.OutputJson!);
            Assert.Equal(2, metadata.RootElement.GetProperty("lastEventId").GetInt64());
            if (type == RunEventTypes.ModelGeneration)
            {
                Assert.Equal("Umgeleitet", stored.Detail);
                Assert.Equal("steered", metadata.RootElement.GetProperty("generation").GetProperty("state").GetString());
                Assert.Equal(25, metadata.RootElement.GetProperty("generation").GetProperty("generatedTokens").GetInt32());
            }
            else
            {
                Assert.Equal("Empfangener Zwischenstand.", stored.Detail);
                Assert.Equal("steered", metadata.RootElement.GetProperty("state").GetString());
            }
            Assert.Null(GoAiAssistantService.ApplySubagentDisplayEvent(stored, first, stored.Id, 0, Now));
            Assert.Null(GoAiAssistantService.ApplySubagentDisplayEvent(stored, final, stored.Id, 0, Now));
        }
    }

    [Fact]
    public void ChildProgressCombinesActualMeasurementsWithoutLosingEarlierContext()
    {
        var context = Event(10, RunEventTypes.ContextChanged, new { agentId = "child-A", round = 2, phase = "subagent", estimatedInputTokens = 823, contextLimit = 65536 });
        var generation = Event(11, RunEventTypes.ModelGeneration, new { agentId = "child-A", round = 2, phase = "subagent", state = "codingWaiting", cachedPromptTokens = 700, generatedTokens = 44 });
        var metrics = Event(12, RunEventTypes.CodingMetrics, new { agentId = "child-A", round = 2, phase = "subagent", metrics = new { cachedPromptTokens = 700 } });
        var finished = Event(13, RunEventTypes.ModelGeneration, new { agentId = "child-A", round = 2, phase = "subagent", state = "generationCompleted", finishObserved = true });
        var step = Apply(Apply(Apply(Apply(null, context), generation), metrics), finished);
        Assert.Equal("assistant.progress", step.Tool);
        Assert.Equal("completed", step.Status);
        using var json = JsonDocument.Parse(step.OutputJson!);
        Assert.Equal(823, json.RootElement.GetProperty("context").GetProperty("estimatedInputTokens").GetInt32());
        Assert.Equal(700, json.RootElement.GetProperty("generation").GetProperty("cachedPromptTokens").GetInt32());
        Assert.True(json.RootElement.TryGetProperty("metrics", out _));
        Assert.Null(GoAiAssistantService.ApplySubagentDisplayEvent(step, generation, step.Id, 0, Now));
        Assert.Null(GoAiAssistantService.SubagentDisplayStepId(Event(13, RunEventTypes.ReasoningDelta, new { round = 2, delta = "Hauptagent" })));
    }

    [Fact]
    public void ChildRetryReplacesOnlyItsRoundAndDoesNotReopenActualTools()
    {
        var first = Event(1, RunEventTypes.TextDelta, new { agentId = "child-A", round = 1, phase = "subagent", delta = "Verworfen", state = "completed" });
        var retry = Event(2, RunEventTypes.TextDelta, new { agentId = "child-A", round = 1, phase = "subagent", delta = "Neu", replaceFrom = 0, state = "running" });
        var previous = Apply(null, first);
        var updated = AssistantToolStep.Merge(previous, Apply(previous, retry));
        Assert.Equal("Neu", updated.Detail);
        Assert.Equal("running", updated.Status);
        Assert.Equal("child-A", updated.AgentId);
        var actualTool = previous with { Tool = "coding.write" };
        Assert.Equal(actualTool, AssistantToolStep.Merge(actualTool, updated with { Tool = actualTool.Tool }));
    }

    private static RunEvent Event(long id, string type, object data) => new(id, "run-display", type,
        Now.AddSeconds(id), JsonSerializer.SerializeToElement(data, Json));

    private static AssistantToolStep Apply(AssistantToolStep? previous, RunEvent item) =>
        GoAiAssistantService.ApplySubagentDisplayEvent(previous, item,
            GoAiAssistantService.SubagentDisplayStepId(item)!, 10, item.CreatedAt)!;
}
