using GoAi.Contracts;
using GoAi.Server.Core.Coding;
using GoAi.Server.Core.Gateway;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GoAi.Server.Tests;

public sealed class CodingWorkingStateIntegrationTests
{
    private static readonly string[] Capabilities = ["coding", "coding.evidence"];
    private static readonly string[] ForbiddenArguments = ["delete"];
    private static readonly string[] WebTools = ["web.search", "web.fetch"];
    private static RunRequest Request(string text = "Implementiere die Aufgabe mit zwei unabhängigen lesenden Prüfungen.") => new(
        GoAiProtocol.Version, RunMode.Coding, [new("user", [new("text", text)])], ClientCapabilities: Capabilities,
        AllowedServerTools: WebTools, PreferredCodingModelId: NativeHandler.ModelId,
        CodingOptions: new());

    [Fact]
    public void SingleAgentCatalogKeepsWorkingStateAndEvidenceWithoutRetiredAgentTools()
    {
        var catalog = new AgentToolCatalog();
        RunRequestValidator.Validate(Request());
        var tools = catalog.GetAvailableTools(Request());
        Assert.Contains(tools, tool => tool.Name == CodingWorkingStateTools.PlanTool);
        Assert.Contains(tools, tool => tool.Name == ClientToolNames.CodingReadOutput);
        Assert.DoesNotContain(tools, tool => tool.Name.StartsWith("agent.", StringComparison.Ordinal));
        var reference = catalog.GetAvailableTools(Request() with { CodingOptions = new(UseWorkingState: false) });
        Assert.Contains(reference, tool => tool.Name == ClientToolNames.CodingReadOutput);
        Assert.Contains(reference, tool => tool.Name == CodingWorkingStateTools.PlanTool);
        var legacy = catalog.GetAvailableTools(Request() with { CodingOptions = null, ClientCapabilities = ["coding"] });
        Assert.Contains(legacy, tool => tool.Name == CodingWorkingStateTools.PlanTool);
        Assert.DoesNotContain(legacy, tool => tool.Name == ClientToolNames.CodingReadOutput);
        Assert.Throws<ArgumentException>(() => RunRequestValidator.Validate(Request() with { ClientCapabilities = ["coding", "coding.agents"] }));
    }

    [Theory]
    [InlineData("agent.start")]
    [InlineData("agent.wait")]
    [InlineData("agent.cancel")]
    public void RemovedAgentToolsCannotBeSelectedOrAuthorized(string name)
    {
        var catalog = new AgentToolCatalog();
        Assert.Throws<InvalidOperationException>(() => catalog.Resolve(name, catalog.GetAvailableTools(Request())));
        Assert.Throws<ArgumentException>(() => catalog.GetAvailableTools(Request() with { AllowedServerTools = [name] }));
        Assert.Throws<ArgumentException>(() => RunRequestValidator.Validate(Request() with { AllowedServerTools = [name] }));
    }

    [Fact]
    public async Task HistoricalChildCheckpointFieldsAreIgnoredWhileTheSingleAgentResumesItsOwnCall()
    {
        using var harness = new Harness();
        var run = await harness.CreateAsync();
        var call = new LmToolCall("own-read", "coding.read", JsonSerializer.SerializeToElement(new { path = "own.cs" }));
        var checkpoint = new AgentRunCheckpoint([new("user", "Preserved task"), new("assistant", null, ToolCalls: [call])],
            4, 8, 21, 12, ActiveToolCalls: [call], WorkingState: CodingWorkingState.Create("Preserved task"));
        await harness.Repository.SaveCheckpointAsync(run, checkpoint);
        var storedRequest = JsonSerializer.SerializeToNode(Request(), GoAiProtocol.CreateJsonOptions())!.AsObject();
        storedRequest["codingOptions"]!["specialists"] = "optional";
        storedRequest["codingOptions"]!["maximumSpecialists"] = 2;
        storedRequest["clientCapabilities"]!.AsArray().Add("coding.agents");
        var storedCheckpoint = JsonSerializer.SerializeToNode(checkpoint, GoAiProtocol.CreateJsonOptions())!.AsObject();
        storedCheckpoint["specialists"] = JsonNode.Parse("""[{"id":"historical-child","state":"waitingForClient","context":{"pendingProposalId":"never-requeue"}}]""");
        storedCheckpoint["nextSpecialistIndex"] = 1;
        storedCheckpoint["specialistTurnPending"] = true;
        storedCheckpoint["awaitingSpecialistCompletion"] = true;
        await using (var connection = await harness.Context.Database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE runs SET request_json=$request WHERE run_id=$run; UPDATE run_checkpoints SET checkpoint_json=$checkpoint WHERE run_id=$run;";
            command.Parameters.AddWithValue("$run", run);
            command.Parameters.AddWithValue("$request", storedRequest.ToJsonString());
            command.Parameters.AddWithValue("$checkpoint", storedCheckpoint.ToJsonString());
            _ = await command.ExecuteNonQueryAsync();
        }
        var request = (await harness.Repository.GetRequestAsync(run))!;
        Assert.True(request.CodingOptions!.UseWorkingState);
        Assert.DoesNotContain("coding.agents", request.ClientCapabilities!);
        var restored = (await harness.Repository.GetCheckpointAsync(run))!;
        Assert.Equal("Preserved task", restored.WorkingState!.OriginalTask);
        Assert.DoesNotContain("specialists", JsonSerializer.Serialize(restored), StringComparison.OrdinalIgnoreCase);
        await harness.TickAsync(run);
        var events = await harness.Repository.GetEventsAfterAsync(run, 0);
        var proposal = Assert.Single(events, item => item.Type == RunEventTypes.ClientToolProposed);
        Assert.Equal("own.cs", proposal.Data.GetProperty("arguments").GetProperty("path").GetString());
        Assert.DoesNotContain(events, item => item.Type == "agent.status");
        Assert.Empty(harness.Handler.Models);
    }

    [Fact]
    public async Task RestoredRetiredToolCallProducesFailureReceiptWithoutStartingAnyChild()
    {
        using var harness = new Harness();
        var run = await harness.CreateAsync();
        LmToolCall[] calls = [new("retired", "agent.start", JsonSerializer.SerializeToElement(new { task = "Old request", kind = "review" })),
            new("read", "coding.read", JsonSerializer.SerializeToElement(new { path = "source.cs" }))];
        await harness.Repository.SaveCheckpointAsync(run, new([new("user", "Task"), new("assistant", null, ToolCalls: calls)],
            1, 2, 0, 0, ActiveToolCalls: calls, WorkingState: CodingWorkingState.Create("Task")));
        await harness.TickAsync(run);
        var restored = (await harness.Repository.GetCheckpointAsync(run))!;
        Assert.Contains(restored.Messages, message => message.ToolCallId == "retired" && message.Content!.Contains("agent.invalid_tool_call", StringComparison.Ordinal));
        var proposal = Assert.Single(await harness.Repository.GetEventsAfterAsync(run, 0), item => item.Type == RunEventTypes.ClientToolProposed);
        Assert.Equal("coding.read", proposal.Data.GetProperty("name").GetString());
        Assert.Empty(harness.Handler.Models);
    }

    [Fact]
    public void EphemeralWorkingDataPreservesEveryPersistedPrefixMessage()
    {
        LmChatMessage[] history = [new("system", "Policy"), new("user", "Original task"), new("assistant", "Verified narration")];
        var state = CodingWorkingState.Create("Original task");
        var first = RunProcessor.WithWorkingState(history, state);
        var next = RunProcessor.WithWorkingState(history, state with { NextStep = "New narrow question" });
        Assert.Equal(3, history.Length);
        Assert.Equal(4, first.Count);
        for (var index = 0; index < history.Length; index++)
        {
            Assert.Same(history[index], first[index]);
            Assert.Same(first[index], next[index]);
        }
        Assert.StartsWith(CodingEvidenceContext.Marker, first[^1].Content, StringComparison.Ordinal);
        Assert.NotEqual(first[^1].Content, next[^1].Content);
    }

    [Fact]
    public void ContextPressureNeverProtectsEphemeralWorkingDataInsteadOfTheRealUserTask()
    {
        var original = new string('u', 9000);
        LmChatMessage[] history = [new("system", "Policy"), new("user", original), new("assistant", new string('a', 15000)),
            new("user", CodingEvidenceContext.Marker + new string('w', 12000))];
        var plan = ContextPlanner.Prepare(history, 8192, null);
        Assert.True(plan.WasCompacted);
        Assert.Equal(original, plan.Messages[1].Content);
        Assert.True(plan.Messages[^1].Content!.Length < history[^1].Content!.Length);
    }

    [Fact]
    public void ReusedProviderIdsRemainDistinctOperationsWhileReplayRemainsIdempotent()
    {
        var call = new LmToolCall("reused-provider-id", "coding.read", JsonSerializer.SerializeToElement(new { path = "missing.cs" }));
        var first = RunProcessor.WithOperationIdentity("root", "main", 1, 0, call);
        var second = RunProcessor.WithOperationIdentity("root", "main", 2, 0, call);
        const string failure = "{\"status\":\"failed\",\"errorCode\":\"not_found\"}";
        var state = CodingWorkingStateReducer.ObserveToolResult(CodingWorkingState.Create("Task"), first, failure);
        var replay = CodingWorkingStateReducer.ObserveToolResult(state, RunProcessor.WithOperationIdentity("root", "main", 1, 0, call), failure);
        Assert.Same(state, replay);
        state = CodingWorkingStateReducer.ObserveToolResult(state, second, failure);
        Assert.Equal(2, state.Evidence.Count);
        Assert.Equal(2, Assert.Single(state.Failures).Count);
        Assert.Throws<AgentRunLimitException>(() => CodingLoopGuard.ThrowIfRepeatedFailure([], call, state));
        Assert.Equal("reused-provider-id", call.Id);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void PlanSchemaExposesCriteriaAndEvidenceBackedFactsWithoutRequiringAnUnchangedPlan()
    {
        var catalog = new AgentToolCatalog();
        var tool = catalog.Resolve(CodingWorkingStateTools.PlanTool, catalog.GetAvailableTools(Request()));
        var fields = tool.Schema.GetProperty("properties");
        Assert.Equal(16, fields.GetProperty("acceptanceCriteria").GetProperty("maxItems").GetInt32());
        Assert.Equal(12, fields.GetProperty("facts").GetProperty("maxItems").GetInt32());
        Assert.True(fields.TryGetProperty("rejectedHypotheses", out _));
        Assert.Equal("id", Assert.Single(fields.GetProperty("steps").GetProperty("items").GetProperty("required").EnumerateArray()).GetString());
        Assert.Equal("id", Assert.Single(fields.GetProperty("acceptanceCriteria").GetProperty("items").GetProperty("required").EnumerateArray()).GetString());
        var call = new LmToolCall("read-a", "coding.read", JsonSerializer.SerializeToElement(new { path = "target.cs" }));
        var state = CodingWorkingStateReducer.ObserveToolResult(CodingWorkingState.Create("Keep the source correct"), call,
            "{\"success\":true,\"content\":\"verified target\"}");
        var args = JsonSerializer.Deserialize<JsonElement>("""
            {"acceptanceCriteria":[{"id":"correct","title":"Source verified","status":"completed","evidenceIds":["read-a"]}],
             "facts":[{"text":"target.cs was read","evidenceIds":["read-a"]}],
             "rejectedHypotheses":[{"text":"No unread-source claim is necessary","evidenceIds":["read-a"]}],
             "nextStep":"Review the targeted change","phase":"review"}
            """);
        catalog.Validate(tool, args);
        var updated = CodingWorkingStateReducer.ApplyPlanUpdate(state, args);
        Assert.Single(updated.AcceptanceCriteria);
        Assert.Single(updated.Facts);
        Assert.Single(updated.RejectedHypotheses);
        Assert.Empty(updated.Plan);
    }

    [Fact]
    public void IncrementalPlanSchemaAcceptsStatusOnlyButReducerStillRequiresNewItemFieldsAndCompletionEvidence()
    {
        var catalog = new AgentToolCatalog();
        var tool = catalog.Resolve(CodingWorkingStateTools.PlanTool, catalog.GetAvailableTools(Request()));
        var state = CodingWorkingState.Create("Keep criteria") with
        {
            Plan = [new("change", "Apply the targeted change", "pending", [])],
            AcceptanceCriteria = [new("correct", "The targeted behavior remains correct", "pending", [])],
        };
        var statusOnly = JsonSerializer.Deserialize<JsonElement>("""{"steps":[{"id":"change","status":"in_progress"}]}""");
        catalog.Validate(tool, statusOnly);
        var updated = CodingWorkingStateReducer.ApplyPlanUpdate(state, statusOnly);
        Assert.Equal("Apply the targeted change", Assert.Single(updated.Plan).Title);
        Assert.Equal("in_progress", updated.Plan[0].Status);
        Assert.Equal(state.AcceptanceCriteria, updated.AcceptanceCriteria);

        var missingFields = JsonSerializer.Deserialize<JsonElement>("""{"steps":[{"id":"new","status":"pending"}]}""");
        catalog.Validate(tool, missingFields);
        Assert.Throws<ArgumentException>(() => CodingWorkingStateReducer.ApplyPlanUpdate(state, missingFields));
        var missingEvidence = JsonSerializer.Deserialize<JsonElement>("""{"steps":[{"id":"change","status":"completed"}]}""");
        catalog.Validate(tool, missingEvidence);
        Assert.Throws<ArgumentException>(() => CodingWorkingStateReducer.ApplyPlanUpdate(state, missingEvidence));
    }

    [Fact]
    public async Task IncrementalPlanReceiptIsCompactWhileCheckpointKeepsTheFullWorkingState()
    {
        using var harness = new Harness();
        var run = await harness.CreateAsync();
        var state = CodingWorkingStateReducer.ObserveToolResult(CodingWorkingState.Create("Preserve the user's complete task"),
            new("proof-read", "coding.read", JsonSerializer.SerializeToElement(new { path = "source.cs" })),
            JsonSerializer.Serialize(new { success = true, content = "FULL_CODE_EVIDENCE_" + new string('x', 1500) })) with
        {
            Plan = [new("change", "Apply the targeted change", "in_progress", []), new("next", "Retain this untouched step", "pending", [])],
            AcceptanceCriteria = [new("correct", "Retain this acceptance criterion", "pending", [])],
            Facts = [new("Retain a verified fact", ["proof-read"])],
        };
        var arguments = JsonSerializer.Deserialize<JsonElement>("""
            {"steps":[{"id":"change","status":"completed","evidenceIds":["proof-read"]}],"phase":"review","nextStep":"Inspect source.cs"}
            """);
        LmToolCall[] calls = [new("plan-small", "coding.updatePlan", arguments),
            new("read-next", "coding.read", JsonSerializer.SerializeToElement(new { path = "source.cs" }))];
        await harness.Repository.SaveCheckpointAsync(run, new([new("user", "Task"), new("assistant", null, ToolCalls: calls)],
            1, 2, 1, 1, ActiveToolCalls: calls, WorkingState: state));
        await harness.TickAsync(run);

        var checkpoint = (await harness.Repository.GetCheckpointAsync(run))!;
        Assert.Equal(2, checkpoint.WorkingState!.Plan.Count);
        Assert.Equal("Retain this untouched step", checkpoint.WorkingState.Plan[1].Title);
        Assert.Equal(JsonSerializer.Serialize(state.AcceptanceCriteria), JsonSerializer.Serialize(checkpoint.WorkingState.AcceptanceCriteria));
        Assert.Equal(JsonSerializer.Serialize(state.Facts), JsonSerializer.Serialize(checkpoint.WorkingState.Facts));
        Assert.Contains("FULL_CODE_EVIDENCE_", Assert.Single(checkpoint.WorkingState.ActiveFiles).Snippet, StringComparison.Ordinal);
        var receiptText = Assert.Single(checkpoint.Messages, message => message.ToolCallId == "plan-small").Content!;
        Assert.True(receiptText.Length < 600, receiptText);
        var receipt = JsonSerializer.Deserialize<JsonElement>(receiptText);
        Assert.False(receipt.TryGetProperty("workingState", out _));
        Assert.Equal("review", receipt.GetProperty("phase").GetString());
        Assert.Equal("Inspect source.cs", receipt.GetProperty("nextStep").GetString());
        var changed = Assert.Single(receipt.GetProperty("updatedSteps").EnumerateArray());
        Assert.Equal("change", changed.GetProperty("id").GetString());
        Assert.Equal("completed", changed.GetProperty("status").GetString());
        Assert.False(changed.TryGetProperty("title", out _));
        Assert.False(receipt.TryGetProperty("updatedAcceptanceCriteria", out _));
        var completed = Assert.Single(await harness.Repository.GetEventsAfterAsync(run, 0), item => item.Type == RunEventTypes.ServerToolCompleted);
        Assert.Equal(receipt.GetRawText(), completed.Data.GetProperty("result").GetRawText());
        Assert.Empty(harness.Handler.Models);
    }

    [Fact]
    public void PhaseOnlyPlanReceiptDoesNotRepeatExistingNextStepOrState()
    {
        var state = CodingWorkingState.Create("A long task") with { Phase = "review", NextStep = new string('x', 1000) };
        var receipt = CodingWorkingStateTools.CreatePlanReceipt(state, JsonSerializer.Deserialize<JsonElement>("""{"phase":"review"}"""));
        Assert.Equal("{\"success\":true,\"phase\":\"review\"}", receipt.GetRawText());
    }

    [Theory]
    [InlineData("coding.updatePlan", "{}")]
    [InlineData("coding.updatePlan", "{\"steps\":[{}]}")]
    [InlineData("coding.updatePlan", "{\"steps\":[{\"id\":\"a\",\"status\":\"invented\"}]}")]
    [InlineData("coding.updatePlan", "{\"facts\":[{\"text\":\"unverified\"}]}")]
    [InlineData("coding.updatePlan", "{\"rejectedHypotheses\":[{\"text\":\"x\",\"evidenceIds\":[\"1\",\"2\",\"3\",\"4\"]}]}")]
    [InlineData("coding.readOutput", "{\"evidenceId\":\"../../outside\"}")]
    [InlineData("coding.readOutput", "{\"evidenceId\":\"ev-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"maximumCharacters\":32001}")]
    [InlineData("coding.searchRunEvidence", "{\"query\":\"q\",\"maximumResults\":21}")]
    public void MalformedStateAndEvidenceArgumentsAreRejected(string name, string json)
    {
        var catalog = new AgentToolCatalog();
        Assert.Throws<ArgumentException>(() => catalog.Validate(catalog.Resolve(name, catalog.GetAvailableTools(Request())), JsonSerializer.Deserialize<JsonElement>(json)));
    }

    [Fact]
    public async Task RepeatedInvalidArgumentsRemainVisibleToWorkingStateFailureGuard()
    {
        using var harness = new Harness();
        var run = await harness.CreateAsync();
        var args = JsonSerializer.SerializeToElement(new { });
        LmToolCall[] invalidCalls = [new("bad-a", "coding.read", args), new("bad-b", "coding.read", args), new("bad-c", "coding.read", args), new("valid", "coding.read", JsonSerializer.SerializeToElement(new { path = "source.cs" }))];
        await harness.Repository.SaveCheckpointAsync(run, new([new("user", "Task"), new("assistant", null, ToolCalls: invalidCalls)],
            1, 3, 1, 1, ActiveToolCalls: invalidCalls, WorkingState: CodingWorkingState.Create("Task")));
        await harness.TickAsync(run);
        var saved = (await harness.Repository.GetCheckpointAsync(run))!;
        Assert.Contains(saved.WorkingState!.Failures, failure => failure.Count >= 2);
        Assert.Contains(saved.Messages, message => message.Role == "tool" && message.Content!.Contains("agent.repeated_tool_failure", StringComparison.Ordinal));
        Assert.Equal(RunState.WaitingForClient, (await harness.Repository.GetAsync(run))!.State);

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyCheckpointWithoutWorkingStateGetsPersistentState(bool hasLegacyOptions)
    {
        using var harness = new Harness();
        var request = Request() with { CodingOptions = hasLegacyOptions ? new(UseWorkingState: false, ReasoningPolicy: "adaptive") : null };
        var run = (await harness.Repository.CreateAsync(request, null)).Snapshot.RunId;
        LmToolCall[] calls = [new("read", "coding.read", JsonSerializer.SerializeToElement(new { path = "source.cs" }))];
        await harness.Repository.SaveCheckpointAsync(run, new([new("user", "Task"), new("assistant", null, ToolCalls: calls)],
            1, 1, 1, 1, ActiveToolCalls: calls));
        await harness.TickAsync(run);
        var saved = (await harness.Repository.GetCheckpointAsync(run))!;
        Assert.NotNull(saved.WorkingState);
        Assert.False(string.IsNullOrWhiteSpace(saved.WorkingState.OriginalTask));
        Assert.Equal(RunState.WaitingForClient, (await harness.Repository.GetAsync(run))!.State);
    }

    [Fact]
    public async Task LegacyAdaptiveRequestUsesMaximumImmediately()
    {
        using var harness = new Harness();
        harness.Handler.AdaptiveScenario = true;
        var request = Request() with { CodingOptions = new(ReasoningPolicy: "adaptive") };
        var run = (await harness.Repository.CreateAsync(request, null)).Snapshot.RunId;
        await harness.Repository.SaveCheckpointAsync(run, new([new("system", "Main policy"), new("user", "Task")], 0, 0, 0, 0,
            WorkingState: CodingWorkingState.Create("Task") with { Phase = "exploration" }));
        await harness.DriveUntilCompletedAsync(run);
        Assert.Equal("xhigh", Assert.Single(harness.Handler.Efforts));
        var visible = CodingTextReconciler.Project(await harness.Repository.GetEventsAfterAsync(run, 0));
        Assert.DoesNotContain("DRAFT_ANSWER", visible, StringComparison.Ordinal);
        Assert.Contains("MAXIMUM_VERIFIED", visible, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitManualReasoningSurvivesWorkingStateProcessing()
    {
        using var harness = new Harness();
        harness.Handler.AdaptiveScenario = true;
        var request = Request() with { ReasoningEffort = "low" };
        var run = (await harness.Repository.CreateAsync(request, null)).Snapshot.RunId;
        await harness.Repository.SaveCheckpointAsync(run, new([new("system", "Main policy"), new("user", "Task")], 0, 0, 0, 0,
            WorkingState: CodingWorkingState.Create("Task") with { Phase = "exploration" }));
        await harness.DriveUntilCompletedAsync(run);
        Assert.NotEmpty(harness.Handler.Efforts);
        Assert.All(harness.Handler.Efforts, effort => Assert.Equal("low", effort));
    }

    [Theory]
    [InlineData("new")]
    [InlineData("legacy")]
    [InlineData("without-system")]
    [InlineData("updated")]
    public async Task CodingModelRequestAlwaysContainsOneGermanReasoningInstruction(string checkpointKind)
    {
        using var harness = new Harness();
        var run = await harness.CreateAsync("Explain src/Parser.cs and parse_value without changing identifiers.");
        if (checkpointKind != "new")
        {
            var history = new List<LmChatMessage>();
            if (checkpointKind != "without-system")
                history.Add(new("system", checkpointKind == "updated"
                    ? CodingAgentPolicy.ForWorkingState(true) : "Legacy coding policy. Preserve existing rules."));
            // Even a complete quotation in user data must not suppress the actual system instruction.
            history.Add(new("user", "Source identifier: parse_value; path: src/Parser.cs\n" + CodingAgentPolicy.ReasoningLanguagePrompt));
            await harness.Repository.SaveCheckpointAsync(run, new(history, 0, 0, 0, 0));
        }
        await harness.DriveUntilCompletedAsync(run);
        var sent = Assert.Single(harness.Handler.Requests);
        AssertGermanReasoningInstruction(sent);
        var allContent = string.Join("\n", sent.GetProperty("messages").EnumerateArray()
            .Select(message => message.GetProperty("content").GetString()));
        Assert.Contains("src/Parser.cs", allContent, StringComparison.Ordinal);
        Assert.Contains("parse_value", allContent, StringComparison.Ordinal);
        if (checkpointKind == "legacy")
            Assert.Contains("Legacy coding policy. Preserve existing rules.",
                sent.GetProperty("messages")[0].GetProperty("content").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatedWaitingCheckpointRecoveryPersistsGermanReasoningInstructionOnlyOnce()
    {
        using var harness = new Harness();
        var run = await harness.CreateAsync();
        LmToolCall[] calls = [new("read", "coding.read", JsonSerializer.SerializeToElement(new { path = "source.cs" }))];
        await harness.Repository.SaveCheckpointAsync(run, new([new("system", "Legacy policy"), new("user", "Task"),
            new("assistant", null, ToolCalls: calls)], 1, 1, 1, 1, ActiveToolCalls: calls));
        await harness.TickAsync(run);
        var first = (await harness.Repository.GetCheckpointAsync(run))!;
        await harness.TickAsync(run);
        var second = (await harness.Repository.GetCheckpointAsync(run))!;
        var system = Assert.Single(second.Messages, message => message.Role == "system").Content!;
        Assert.Equal("Legacy policy\n\n" + CodingAgentPolicy.ReasoningLanguagePrompt
            + "\n\n" + CodingAgentPolicy.StagedExecutionAndNarrationPrompt
            + "\n\n" + CodingAgentPolicy.WorkspaceDependenciesPrompt, system);
        Assert.Equal(first.Messages[0], second.Messages[0]);
        Assert.Empty(harness.Handler.Requests);
    }

    private static void AssertGermanReasoningInstruction(JsonElement request)
    {
        var systemText = string.Join("\n", request.GetProperty("messages").EnumerateArray()
            .Where(message => message.GetProperty("role").GetString() == "system")
            .Select(message => message.GetProperty("content").GetString()));
        Assert.Contains(CodingAgentPolicy.ReasoningLanguagePrompt, systemText, StringComparison.Ordinal);
        Assert.Equal(2, systemText.Split(CodingAgentPolicy.ReasoningLanguagePrompt, StringSplitOptions.None).Length);
    }

    [Fact]
    public async Task LegacyReadonlyCheckpointStillAllowsWriting()
    {
        using var harness = new Harness();
        var run = await harness.CreateAsync();
        LmToolCall[] calls = [new("write", "coding.write", JsonSerializer.SerializeToElement(new { path = "source.cs", content = "changed" }))];
        await harness.Repository.SaveCheckpointAsync(run, new([new("user", "Task"), new("assistant", null, ToolCalls: calls)],
            1, 1, 1, 1, ActiveToolCalls: calls, ActiveCallsReadOnly: true,
            WorkingState: CodingWorkingState.Create("Task") with { Phase = "exploration" }));
        await harness.TickAsync(run);
        var proposal = Assert.Single((await harness.Repository.GetEventsAfterAsync(run, 0)), item => item.Type == RunEventTypes.ClientToolProposed);
        Assert.Equal("coding.write", proposal.Data.GetProperty("name").GetString());
    }

    [Fact]
    public async Task AdaptiveBudgetClosureUsesMaximumReasoning()
    {
        using var harness = new Harness();
        harness.Context.Options.CodingMaximumModelRounds = 2;
        harness.Handler.AdaptiveScenario = true;
        var run = (await harness.Repository.CreateAsync(Request() with { CodingOptions = new(ReasoningPolicy: "adaptive") }, null)).Snapshot.RunId;
        await harness.Repository.SaveCheckpointAsync(run, new([new("system", "Main policy"), new("user", "Task")], 1, 0, 0, 0,
            WorkingState: CodingWorkingState.Create("Task") with { Phase = "exploration" }));
        await Assert.ThrowsAsync<AgentRunLimitException>(() => harness.Processor.ProcessAsync(run, CancellationToken.None));
        Assert.Equal("xhigh", Assert.Single(harness.Handler.Efforts));
    }

    [Theory]
    [InlineData(-1, "xhigh")]
    [InlineData(0, "xhigh")]
    [InlineData(1, "xhigh")]
    public async Task CompactionEnteringTheReservedClosureUpgradesAdaptiveReasoningToMaximum(int failureRevision, string summaryEffort)
    {
        using var harness = new Harness();
        harness.Context.Options.CodingMaximumModelRounds = 3;
        harness.Handler.AdaptiveScenario = true;
        harness.Handler.EmitReasoning = true;
        var run = (await harness.Repository.CreateAsync(Request() with { CodingOptions = new(ReasoningPolicy: "adaptive") }, null)).Snapshot.RunId;
        var history = new List<LmChatMessage> { new("system", "Main policy"), new("user", "Task") };
        for (var index = 0; index < 64; index++)
        {
            var call = new LmToolCall("past-" + index, "coding.read", JsonSerializer.SerializeToElement(new { path = "source.cs" }));
            history.Add(new("assistant", null, ToolCalls: [call]));
            history.Add(new("tool", JsonSerializer.Serialize(new { content = new string('x', 1400) }), ToolCallId: call.Id));
        }
        await harness.Repository.SaveCheckpointAsync(run, new(history, 1, 64, 0, 0,
            WorkingState: CodingWorkingState.Create("Task") with { Phase = "exploration",
                Failures = failureRevision < 0 ? [] : [new("signature", "coding.command", "args", "failed", 1, failureRevision, "receipt")] }));
        await Assert.ThrowsAsync<AgentRunLimitException>(() => harness.Processor.ProcessAsync(run, CancellationToken.None));
        Assert.Equal(2, harness.Handler.Efforts.Count);
        Assert.Equal(summaryEffort, harness.Handler.Efforts[0]);
        Assert.Equal("xhigh", harness.Handler.Efforts[1]);
        Assert.All(harness.Handler.Requests, AssertGermanReasoningInstruction);
        Assert.Contains(CodingContextCompactor.SummaryInstruction.Replace(CodingAgentPolicy.ReasoningLanguagePrompt, "", StringComparison.Ordinal).Trim(),
            harness.Handler.Requests[0].GetProperty("messages")[0].GetProperty("content").GetString());
        var metrics = (await harness.Repository.GetEventsAfterAsync(run, 0)).Where(item => item.Type == RunEventTypes.CodingMetrics).ToArray();
        Assert.Equal(2, metrics.Length);
        Assert.Equal("summarization", metrics[0].Data.GetProperty("phase").GetString());
        Assert.True(metrics[0].Data.GetProperty("queueMilliseconds").GetDouble() >= 0);
        Assert.Equal(0, metrics[1].Data.GetProperty("queueMilliseconds").GetDouble());
        var reasoning = (await harness.Repository.GetEventsAfterAsync(run, 0))
            .Where(item => item.Type == RunEventTypes.ReasoningDelta).ToArray();
        Assert.Equal(4, reasoning.Length);
        Assert.Equal("compaction", reasoning[0].Data.GetProperty("phase").GetString());
        Assert.Equal(2, reasoning[0].Data.GetProperty("round").GetInt32());
        Assert.Equal("completed", reasoning[1].Data.GetProperty("state").GetString());
        Assert.Equal("main", reasoning[2].Data.GetProperty("phase").GetString());
        Assert.Equal(3, reasoning[2].Data.GetProperty("round").GetInt32());
        Assert.Equal("completed", reasoning[3].Data.GetProperty("state").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReasoningOnlyTurnAfterSavedEditContinuesWithoutReapplyingTheEdit(bool streaming)
    {
        using var harness = new Harness();
        var run = await harness.CreateAsync("Apply and verify the change.");
        var edit = new LmToolCall("edit-once", "coding.edit", JsonSerializer.SerializeToElement(new
        {
            path = "source.cs", expectedSha256 = new string('a', 64),
            edits = new[] { new { oldText = "old", newText = "new" } },
        }));
        await harness.Repository.SaveCheckpointAsync(run, new(
            [new("user", "Apply and verify the change."), new("assistant", null, ToolCalls: [edit])],
            4, 1, 100, 20, ActiveToolCalls: [edit], WorkingState: CodingWorkingState.Create("Apply and verify the change.")));
        await harness.TickAsync(run);
        var pending = (await harness.Repository.GetCheckpointAsync(run))!;
        await harness.Repository.SaveClientToolResultAsync(run, new(pending.PendingProposalId!, "completed",
            JsonSerializer.SerializeToElement(new { path = "source.cs", applied = true, sha256 = new string('b', 64) })));
        harness.Handler.CompletionOverride = (attempt, _) => Task.FromResult(attempt == 1
            ? NativeHandler.EmptyResponse(reasoning: true, streaming)
            : harness.Handler.ReadResponse("verify-after-edit"));

        await harness.TickAsync(run);

        var checkpoint = (await harness.Repository.GetCheckpointAsync(run))!;
        Assert.Equal(6, checkpoint.RoundCount);
        Assert.Equal(228, checkpoint.InputTokens);
        Assert.Equal(36, checkpoint.OutputTokens);
        Assert.Equal(0, checkpoint.EmptyResponseRetryCount);
        Assert.Equal(2, harness.Handler.Requests.Count);
        var events = await harness.Repository.GetEventsAfterAsync(run, 0);
        var proposals = events.Where(item => item.Type == RunEventTypes.ClientToolProposed).ToArray();
        Assert.Equal(2, proposals.Length);
        Assert.Single(proposals, item => item.Data.GetProperty("name").GetString() == "coding.edit");
        Assert.Equal("coding.read", proposals[^1].Data.GetProperty("name").GetString());
        Assert.Contains(checkpoint.Messages, item => item.Role == "tool" && item.Content!.Contains("applied", StringComparison.Ordinal));
        Assert.DoesNotContain(events, item => item.Type == RunEventTypes.TextDelta || item.Type == RunEventTypes.RunFailed);
        Assert.Contains(events, item => item.Type == RunEventTypes.ReasoningDelta
            && item.Data.GetProperty("delta").GetString() == NativeHandler.EmptyTurnReasoning);
        Assert.DoesNotContain("REASONING_ONLY_NOT_AN_EXECUTABLE_CALL", harness.Handler.Requests[1].GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConsecutiveCompletedEmptyTurnsStopWithSpecificErrorAndPreserveAccounting(bool reasoning)
    {
        using var harness = new Harness();
        var run = await harness.CreateAsync();
        harness.Handler.CompletionOverride = (_, _) => Task.FromResult(NativeHandler.EmptyResponse(reasoning));

        await Assert.ThrowsAsync<ModelEmptyResponseException>(() => harness.Processor.ProcessAsync(run, CancellationToken.None));

        Assert.Equal(3, harness.Handler.Requests.Count);
        var checkpoint = (await harness.Repository.GetCheckpointAsync(run))!;
        Assert.Equal(3, checkpoint.EmptyResponseRetryCount);
        Assert.Equal(3, checkpoint.RoundCount);
        Assert.Equal(192, checkpoint.InputTokens);
        Assert.Equal(24, checkpoint.OutputTokens);
        Assert.DoesNotContain(checkpoint.Messages, item => item.Role == "assistant" && string.IsNullOrWhiteSpace(item.Content));
        Assert.DoesNotContain(await harness.Repository.GetEventsAfterAsync(run, 0),
            item => item.Type == RunEventTypes.TextDelta || item.Type == RunEventTypes.ClientToolProposed);
    }

    [Fact]
    public async Task RestoredEmptyTurnRetryCounterDoesNotRestartItsRecoveryBudget()
    {
        using var harness = new Harness();
        var run = await harness.CreateAsync();
        await harness.Repository.SaveCheckpointAsync(run, new([new("user", "Continue the saved work.")],
            5, 2, 123, 45, WorkingState: CodingWorkingState.Create("Continue the saved work."), EmptyResponseRetryCount: 2));
        harness.Handler.CompletionOverride = (_, _) => Task.FromResult(NativeHandler.EmptyResponse(reasoning: true));

        await Assert.ThrowsAsync<ModelEmptyResponseException>(() => harness.Processor.ProcessAsync(run, CancellationToken.None));

        Assert.Single(harness.Handler.Requests);
        var checkpoint = (await harness.Repository.GetCheckpointAsync(run))!;
        Assert.Equal(3, checkpoint.EmptyResponseRetryCount);
        Assert.Equal(6, checkpoint.RoundCount);
        Assert.Equal(187, checkpoint.InputTokens);
        Assert.Equal(53, checkpoint.OutputTokens);
    }

    [Fact]
    public async Task BackgroundProcessorPublishesSpecificRetryableFailureAfterReasoningOnlyRecoveryExhaustion()
    {
        using var harness = new Harness();
        var run = await harness.CreateAsync();
        harness.Handler.CompletionOverride = (_, _) => Task.FromResult(NativeHandler.EmptyResponse(reasoning: true, streaming: true));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var subscription = harness.Notifier.Subscribe(run);
        await harness.Processor.StartAsync(deadline.Token);
        try
        {
            while (!(await harness.Repository.GetEventsAfterAsync(run, 0, deadline.Token))
                .Any(item => item.Type == RunEventTypes.RunFailed))
                await subscription.Reader.ReadAsync(deadline.Token);
        }
        finally
        {
            await harness.Processor.StopAsync(CancellationToken.None);
        }

        Assert.Equal(3, harness.Handler.Requests.Count);
        var snapshot = (await harness.Repository.GetAsync(run))!;
        Assert.Equal(RunState.Failed, snapshot.State);
        Assert.Equal("provider.empty_response", snapshot.ErrorCode);
        var events = await harness.Repository.GetEventsAfterAsync(run, 0);
        var failure = Assert.Single(events, item => item.Type == RunEventTypes.RunFailed);
        Assert.Equal("provider.empty_response", failure.Data.GetProperty("errorCode").GetString());
        Assert.True(failure.Data.GetProperty("retryable").GetBoolean());
        var message = failure.Data.GetProperty("message").GetString();
        Assert.Contains("3 aufeinanderfolgenden Anfragen", message, StringComparison.Ordinal);
        Assert.Contains("nur Denktext", message, StringComparison.Ordinal);
        Assert.Contains("bleiben gespeichert", message, StringComparison.Ordinal);
        Assert.DoesNotContain("erforderliche Operation", message, StringComparison.Ordinal);
        Assert.DoesNotContain(events, item => item.Type == RunEventTypes.RunCompleted || item.Type == RunEventTypes.ClientToolProposed);
        Assert.Equal(3, (await harness.Repository.GetCheckpointAsync(run))!.EmptyResponseRetryCount);
    }

    [Fact]
    public async Task ValidToolCallResetsOnlyTheConsecutiveEmptyTurnCounter()
    {
        using var harness = new Harness();
        var run = await harness.CreateAsync();
        harness.Handler.CompletionOverride = (attempt, _) => Task.FromResult(attempt switch
        {
            3 => harness.Handler.ReadResponse("actual-progress"),
            6 => harness.Handler.CompleteResponse(),
            _ => NativeHandler.EmptyResponse(reasoning: true),
        });
        await harness.TickAsync(run);
        var checkpoint = (await harness.Repository.GetCheckpointAsync(run))!;
        Assert.Equal(3, checkpoint.RoundCount);
        Assert.Equal(0, checkpoint.EmptyResponseRetryCount);
        await harness.Repository.SaveClientToolResultAsync(run, new(checkpoint.PendingProposalId!, "completed",
            JsonSerializer.SerializeToElement(new { path = "source.cs", content = "new", sha256 = new string('b', 64) })));

        await harness.TickAsync(run);

        Assert.Equal(RunState.Completed, (await harness.Repository.GetAsync(run))!.State);
        Assert.Equal(6, harness.Handler.Requests.Count);
        var events = await harness.Repository.GetEventsAfterAsync(run, 0);
        Assert.Single(events, item => item.Type == RunEventTypes.ClientToolProposed);
        Assert.Equal(6, events.Count(item => item.Type == RunEventTypes.CodingMetrics));
        var completed = Assert.Single(events, item => item.Type == RunEventTypes.RunCompleted);
        Assert.Equal(384, completed.Data.GetProperty("inputTokens").GetInt32());
        Assert.Equal(48, completed.Data.GetProperty("outputTokens").GetInt32());
    }

    [Fact]
    public async Task CancellationAfterEmptyTurnKeepsItsRepairCheckpointForResume()
    {
        using var harness = new Harness();
        using var stop = new CancellationTokenSource();
        var run = await harness.CreateAsync();
        var secondRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Handler.CompletionOverride = async (attempt, token) =>
        {
            if (attempt == 2)
            {
                secondRequest.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return NativeHandler.EmptyResponse(reasoning: true);
        };
        var processing = harness.Processor.ProcessAsync(run, stop.Token);
        await secondRequest.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
        var interrupted = (await harness.Repository.GetCheckpointAsync(run))!;
        Assert.Equal(1, interrupted.EmptyResponseRetryCount);
        Assert.Equal(1, interrupted.RoundCount);
        Assert.Equal(64, interrupted.InputTokens);
        Assert.Equal(8, interrupted.OutputTokens);
        harness.Handler.CompletionOverride = (attempt, _) => Task.FromResult(attempt == 3
            ? NativeHandler.EmptyResponse(reasoning: true)
            : harness.Handler.ReadResponse("resumed-verification"));

        await harness.TickAsync(run);

        var restored = (await harness.Repository.GetCheckpointAsync(run))!;
        Assert.Equal(3, restored.RoundCount);
        Assert.Equal(192, restored.InputTokens);
        Assert.Equal(24, restored.OutputTokens);
        Assert.Equal(0, restored.EmptyResponseRetryCount);
        Assert.Single(await harness.Repository.GetEventsAfterAsync(run, 0), item => item.Type == RunEventTypes.ClientToolProposed);
    }

    [Fact]
    public void LegacyCheckpointDefaultsToNoEmptyResponseRetries()
    {
        var checkpoint = JsonSerializer.Deserialize<AgentRunCheckpoint>(
            """{"messages":[],"roundCount":4,"toolCallCount":2,"inputTokens":100,"outputTokens":20}""",
            GoAiProtocol.CreateJsonOptions());
        Assert.Equal(0, checkpoint!.EmptyResponseRetryCount);
    }

    [Fact]
    public async Task AnnouncedPlanContinuesToActualToolInsteadOfCompleting()
    {
        using var harness = new Harness();
        var run = await harness.CreateAsync("Implement and verify the requested change.");
        harness.Handler.CompletionOverride = (attempt, _) => Task.FromResult(attempt switch
        {
            1 => harness.Handler.AnnouncementResponse(),
            2 => harness.Handler.ReadResponse("next-real-step"),
            _ => harness.Handler.CompleteResponse(),
        });
        await harness.TickAsync(run);
        var saved = (await harness.Repository.GetCheckpointAsync(run))!;
        Assert.Equal(RunState.WaitingForClient, (await harness.Repository.GetAsync(run))!.State);
        Assert.Equal(2, saved.RoundCount);
        Assert.Equal(0, saved.IncompleteResponseRetryCount);
        Assert.DoesNotContain(await harness.Repository.GetEventsAfterAsync(run, 0), item => item.Type == RunEventTypes.RunCompleted);
        await harness.Repository.SaveClientToolResultAsync(run, new(saved.PendingProposalId!, "completed",
            JsonSerializer.SerializeToElement(new { content = "verified source" })));
        await harness.TickAsync(run);
        Assert.Equal(RunState.Completed, (await harness.Repository.GetAsync(run))!.State);
        Assert.Equal(3, harness.Handler.Requests.Count);
    }

    [Fact]
    public async Task PersistentAnnouncementsCannotBecomeFalseSuccessAfterRestart()
    {
        using var harness = new Harness();
        var run = await harness.CreateAsync("Implement the requested change.");
        await harness.Repository.SaveCheckpointAsync(run, new([new("user", "Implement the requested change.")],
            2, 0, 100, 10, IncompleteResponseRetryCount: 2));
        harness.Handler.CompletionOverride = (_, _) => Task.FromResult(harness.Handler.AnnouncementResponse());
        await Assert.ThrowsAsync<AgentRunLimitException>(() => harness.Processor.ProcessAsync(run, CancellationToken.None));
        Assert.Equal(3, (await harness.Repository.GetCheckpointAsync(run))!.IncompleteResponseRetryCount);
        Assert.DoesNotContain(await harness.Repository.GetEventsAfterAsync(run, 0), item => item.Type == RunEventTypes.RunCompleted);
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Cancelled")]
    public async Task NewRunRestoresSessionToolsWithoutReplayingThem(string terminal)
    {
        using var harness = new Harness();
        var request = Request() with { SessionId = "session-continuity", CodingOptions = new(WorkspacePath: "C:/project", ContinueSessionContext: true) };
        var first = (await harness.Repository.CreateAsync(request, null)).Snapshot.RunId;
        var call = new LmToolCall("prior-read", "coding.read", JsonSerializer.SerializeToElement(new { path = "source.cs" }));
        await harness.Repository.SaveCheckpointAsync(first, new(
            [new("system", "Old policy"), new("user", "Original task"), new("assistant", null, ToolCalls: [call]),
             new("tool", "FULL_PREVIOUS_FILE_CONTENT", ToolCallId: call.Id), new("assistant", "Previous conclusion")],
            8, 4, 100, 20, WorkingState: CodingWorkingState.Create("Original task")));
        if (terminal == "Completed") await harness.Repository.FinalizeConversationAsync(first, new(null, "qwen", 100, 20));
        else await harness.Repository.UpdateStateAsync(first, RunState.Cancelled);
        await harness.Repository.DeleteCheckpointAsync(first);
        Assert.Null(await harness.Repository.GetCheckpointAsync(first));
        var next = (await harness.Repository.CreateAsync(request, null)).Snapshot.RunId;
        harness.Handler.CompletionOverride = (_, _) => Task.FromResult(harness.Handler.ReadResponse("new-read"));
        await harness.TickAsync(next);
        var checkpoint = (await harness.Repository.GetCheckpointAsync(next))!;
        Assert.Contains(checkpoint.Messages, m => m.Role == "tool" && m.Content == "FULL_PREVIOUS_FILE_CONTENT");
        Assert.Contains(checkpoint.Messages, m => m.Content == "Previous conclusion");
        Assert.Equal(1, checkpoint.RoundCount);
        var proposals = (await harness.Repository.GetEventsAfterAsync(next, 0)).Where(e => e.Type == RunEventTypes.ClientToolProposed);
        Assert.Single(proposals);
        Assert.DoesNotContain(checkpoint.Messages, m => m.Role == "system" && m.Content == "Old policy");
        Assert.Null(await harness.Repository.GetSessionContextAsync(next, request with { SessionId = "other-session" }));
        Assert.Null(await harness.Repository.GetSessionContextAsync(next, request with { CodingOptions = new(WorkspacePath: "C:/other", ContinueSessionContext: true) }));
        Assert.Null(await harness.Repository.GetSessionContextAsync(next, request with { CodingOptions = new(WorkspacePath: "C:/project", ContinueSessionContext: false) }));
        await harness.Repository.SaveClientToolResultAsync(next, new(checkpoint.PendingProposalId!, "completed",
            JsonSerializer.SerializeToElement(new { content = "NEW_READ_CONTENT" })));
        harness.Handler.CompletionOverride = (_, _) => Task.FromResult(harness.Handler.CompleteResponse());
        await harness.TickAsync(next);
        Assert.Equal(RunState.Completed, (await harness.Repository.GetAsync(next))!.State);
        Assert.Null(await harness.Repository.GetCheckpointAsync(next));
        var third = (await harness.Repository.CreateAsync(request, null)).Snapshot.RunId;
        // Reopen the repository to exercise durable storage, not an in-memory cache.
        var reopened = new RunRepository(harness.Context.Database, harness.Notifier);
        var retained = (await reopened.GetSessionContextAsync(third, request))!;
        Assert.Contains(retained.Messages, m => m.Content == "Verified result.");
        Assert.Contains(retained.Messages, m => m.Content == "FULL_PREVIOUS_FILE_CONTENT");
        Assert.Contains(retained.Messages, m => m.Role == "tool" && m.Content!.Contains("NEW_READ_CONTENT", StringComparison.Ordinal));
    }

    private sealed class Harness : IDisposable
    {
        internal TestServerContext Context { get; } = new();
        private readonly ServiceProvider _services;
        private readonly HttpClient _http;
        internal NativeHandler Handler { get; } = new();
        internal RunRepository Repository { get; }
        internal RunProcessor Processor { get; }
        internal RunWorkChannel Queue { get; }
        internal RunEventNotifier Notifier { get; }
        internal Harness()
        {
            Context.Options.ModelRuntimeUri = new("http://native.test");
            _http = new(Handler);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddGoAiServerServices(Context.Options, includeHostedServices: false);
            services.AddSingleton(Context.Database);
            services.AddSingleton(new ModelRuntimeClient(_http, Context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance));
            _services = services.BuildServiceProvider();
            Repository = _services.GetRequiredService<RunRepository>();
            Processor = _services.GetRequiredService<RunProcessor>();
            Queue = _services.GetRequiredService<RunWorkChannel>();
            Notifier = _services.GetRequiredService<RunEventNotifier>();
        }
        internal async Task<string> CreateAsync(string? prompt = null) =>
            (await Repository.CreateAsync(prompt is null ? Request() : Request(prompt), null)).Snapshot.RunId;
        internal async Task TickAsync(string run)
        {
            try { await Processor.ProcessAsync(run, CancellationToken.None); }
            catch (RunWaitingForClientException) { }
        }
        internal async Task DriveUntilCompletedAsync(string run)
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                await TickAsync(run);
                if ((await Repository.GetAsync(run))!.State == RunState.Completed) return;
            }
            Assert.Fail("The single-agent run did not complete.");
        }
        public void Dispose() { _services.Dispose(); _http.Dispose(); Context.Dispose(); }
    }

    private sealed class NativeHandler : HttpMessageHandler
    {
        internal const string ModelId = "coding/Qwen3.8-WorkingStateFixture-Q4~abc123";
        private static readonly string[] Tags = ["go-context-train:32768"];
        private int _concurrent;
        internal int MaximumConcurrent { get; private set; }
        internal bool AdaptiveScenario { get; set; }
        internal Func<int, CancellationToken, Task<HttpResponseMessage>>? CompletionOverride { get; set; }
        internal List<string?> Efforts { get; } = [];
        internal List<string> Models { get; } = [];
        internal List<JsonElement> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/models")
            {
                return Json(new { data = new[] { new { id = ModelId, tags = Tags, status = new { value = "loaded" } } } });
            }
            if (path == "/props") return Json(new { default_generation_settings = new { n_ctx = 32768 } });
            if (path == "/v1/chat/completions/input_tokens") return Json(new { input_tokens = 64 });
            if (path != "/v1/chat/completions") throw new InvalidOperationException("Unexpected endpoint " + path);
            var concurrent = Interlocked.Increment(ref _concurrent);
            MaximumConcurrent = Math.Max(MaximumConcurrent, concurrent);
            try
            {
                await Task.Delay(2, cancellationToken);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Requests.Add(body.RootElement.Clone());
                Models.Add(body.RootElement.GetProperty("model").GetString()!);
                if (CompletionOverride is not null)
                    return await CompletionOverride(Requests.Count, cancellationToken);
                if (AdaptiveScenario)
                {
                    var effort = body.RootElement.GetProperty("chat_template_kwargs").GetProperty("reasoning_effort").GetString();
                    Efforts.Add(effort);
                    if (effort == "medium")
                    {
                        if (!body.RootElement.TryGetProperty("tools", out var offered) || offered.GetArrayLength() == 0)
                            return Answer("COMPACTED_NOT_FINAL");
                        var names = offered.EnumerateArray().Select(tool => tool.GetProperty("function").GetProperty("name").GetString()).ToArray();
                        Assert.DoesNotContain(ModelRuntimeClient.ToTransportToolName("coding.write"), names);
                        Assert.DoesNotContain(ModelRuntimeClient.ToTransportToolName("coding.edit"), names);
                        Assert.DoesNotContain(ModelRuntimeClient.ToTransportToolName("coding.command"), names);
                        Assert.Contains(ModelRuntimeClient.ToTransportToolName("coding.updatePlan"), names);
                        return Answer("DRAFT_ANSWER");
                    }
                    return Answer("MAXIMUM_VERIFIED");
                }
                return Answer("Single-agent completed.");
            }
            finally { Interlocked.Decrement(ref _concurrent); }
        }
        internal bool EmitReasoning { get; set; }
        internal const string EmptyTurnReasoning = "REASONING_ONLY_NOT_AN_EXECUTABLE_CALL: Die gespeicherte Änderung als Nächstes prüfen.";
        internal HttpResponseMessage CompleteResponse() => Answer("Verified result.");
        internal HttpResponseMessage AnnouncementResponse() => Answer("Ich erstelle zunächst einen strukturierten Arbeitsplan für diese mehrstufige Aufgabe, um die verschiedenen Teilschritte nachvollziehbar zu halten.");
        internal HttpResponseMessage ReadResponse(string id) => Answer(null, new
        {
            id, type = "function", function = new
            {
                name = ModelRuntimeClient.ToTransportToolName("coding.read"),
                arguments = JsonSerializer.Serialize(new { path = "source.cs" }),
            },
        });
        internal static HttpResponseMessage EmptyResponse(bool reasoning, bool streaming = false)
        {
            var message = new { role = "assistant", content = (string?)null, reasoning_content = reasoning ? EmptyTurnReasoning : null };
            if (!streaming)
                return Json(new
                {
                    choices = new[] { new { message, finish_reason = "stop" } },
                    usage = new { prompt_tokens = 64, completion_tokens = 8 },
                });
            var delta = JsonSerializer.Serialize(new { choices = new[] { new { delta = message } } });
            var finish = JsonSerializer.Serialize(new
            {
                choices = new[] { new { delta = new { }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 64, completion_tokens = 8 },
            });
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent($"data: {delta}\n\ndata: {finish}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
            };
        }
        private HttpResponseMessage Answer(string? content, params object[] calls) => Json(new
        {
            choices = new[] { new { message = new { role = "assistant", content, reasoning_content = EmitReasoning ? "Prüfplan für diesen Modellturn." : null, tool_calls = calls }, finish_reason = calls.Length > 0 ? "tool_calls" : "stop" } },
            usage = new { prompt_tokens = 64, completion_tokens = 8 },
        });
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
