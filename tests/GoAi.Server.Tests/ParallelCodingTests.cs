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

namespace GoAi.Server.Tests;

public sealed class ParallelCodingTests
{
    private static readonly string[] WritePaths = ["src/file.cs"];
    private static readonly string[] DirectoryWritePaths = ["src/Component/"];
    private static readonly string[] TestArguments = ["test"];
    [Fact]
    public void LongSubagentTasksProduceValidCompactClientSummaries()
    {
        var task = "Mehrzeiliger Auftrag\r\n\t" + new string('ä', 16000) + "\0";
        var summary = CodingSubagentService.BuildToolSummary(task, "coding.read");
        Assert.StartsWith("Subagent GPU1 · coding.read · Mehrzeiliger Auftrag", summary);
        Assert.True(summary.Length <= 1000);
        Assert.DoesNotContain(summary, char.IsControl);
        Assert.EndsWith("…", summary);
        Assert.Contains("coding.command", CodingSubagentService.CapabilityPolicy);
        Assert.Contains("separaten Arbeitskopie", CodingSubagentService.CapabilityPolicy);
    }

    [Fact]
    public async Task IndependentInstancesEnterInferenceTogetherWithoutTimingAssertions()
    {
        using var context = new TestServerContext();
        context.Options.ModelRuntimeUri = new("http://native.test");
        using var handler = new PairHandler();
        using var http = new HttpClient(handler);
        using var runtime = new ModelRuntimeClient(http, context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance);
        var main = runtime.CompleteChatAsync(PairHandler.Main, [new("user", "Main task")], [], modelRole: "coding");
        var secondary = runtime.CompleteChatAsync(PairHandler.Secondary, [new("user", "Secondary task")], [], modelRole: "coding");
        await handler.BothEntered.Task;
        Assert.False(main.IsCompleted);
        Assert.False(secondary.IsCompleted);
        handler.Release.TrySetResult();
        var results = await Task.WhenAll(main, secondary);
        Assert.All(results, result => Assert.Equal("verified", result.Content));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("C:/outside")]
    [InlineData(".Git/config")]
    [InlineData("src/*.cs")]
    [InlineData("src/nul.cs")]
    [InlineData("src/com1")]
    [InlineData("src/file\0.cs")]
    [InlineData("src/file|name.cs")]
    public void AssignedPathsCannotEscapeWorkspace(string path) =>
        Assert.Throws<ArgumentException>(() => CodingSubagentTools.NormalizePath(path));

    [Fact]
    public async Task AgentPersistsResultAndRejectsParentConflictingWrites()
    {
        using var context = new TestServerContext();
        context.Options.ModelRuntimeUri = new("http://native.test");
        using var handler = new PairHandler();
        using var http = new HttpClient(handler);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGoAiServerServices(context.Options, includeHostedServices: false);
        services.AddSingleton(context.Database);
        services.AddSingleton(new ModelRuntimeClient(http, context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance));
        using var provider = services.BuildServiceProvider();
        var repository = provider.GetRequiredService<RunRepository>();
        var agents = provider.GetRequiredService<CodingSubagentService>();
        var request = new RunRequest(GoAiProtocol.Version, RunMode.Coding, [new("user", [new("text", Text: "Task")])],
            ClientCapabilities: ["coding", CodingSubagentService.IsolatedWorkspaceCapability], CodingOptions: new(ParallelModelId: PairHandler.Base));
        var run = (await repository.CreateAsync(request, null)).Snapshot.RunId;
        var result = await agents.ExecuteAsync(CodingSubagentTools.Start, run, "operation-one",
            JsonSerializer.SerializeToElement(new { task = "Review file", writePaths = WritePaths }), request, CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.True(CodingWorkingStateReducer.IsSuccessfulResult(result.Result));
        Assert.True(result.Result.GetProperty("capabilities").GetProperty("terminal").GetBoolean());
        await handler.FirstEntered.Task;
        await Assert.ThrowsAsync<ArgumentException>(() => agents.ValidateParentMutationAsync(run,
            new("edit", "coding.edit", JsonSerializer.SerializeToElement(new { path = "src/file.cs" })), CancellationToken.None));
        await agents.ValidateParentMutationAsync(run,
            new("edit", "coding.edit", JsonSerializer.SerializeToElement(new { path = "src/other.cs" })), CancellationToken.None);
        var busy = await agents.ExecuteAsync(CodingSubagentTools.Start, run, "operation-two", JsonSerializer.SerializeToElement(new { task = "Other" }), request, CancellationToken.None);
        Assert.False(busy.Succeeded);
        handler.Release.TrySetResult();
        var waited = await agents.ExecuteAsync(CodingSubagentTools.Wait, run, "operation-wait",
            JsonSerializer.SerializeToElement(new { }), request, CancellationToken.None);
        Assert.True(waited.Succeeded);
        Assert.True(CodingWorkingStateReducer.IsSuccessfulResult(waited.Result));
        var states = await repository.GetAgentsAsync(run, CancellationToken.None);
        Assert.Equal("completed", Assert.Single(states).Status);
        Assert.Equal("verified", states[0].Result);
        Assert.DoesNotContain(await repository.GetEventsAfterAsync(run, 0), e => e.Type == RunEventTypes.ClientToolProposed);
        var duplicate = await agents.ExecuteAsync(CodingSubagentTools.Start, run, "operation-one", JsonSerializer.SerializeToElement(new { task = "Review file" }), request, CancellationToken.None);
        Assert.Equal(result.Result.GetProperty("agentId").GetString(), duplicate.Result.GetProperty("agentId").GetString());
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task RestoredChildCannotWriteOutsideItsAssignedFiles()
    {
        using var harness = new AgentHarness();
        var run = await harness.CreateAsync();
        var call = new LmToolCall("outside", "coding.write", JsonSerializer.SerializeToElement(new { path = "src/other.cs", content = "denied" }));
        await harness.Repository.SaveAgentAsync(new("child-denied", run, PairHandler.Secondary, "Write only assigned file", WritePaths,
            [new("user", "Task"), new("assistant", null, ToolCalls: [call])], PendingCalls: [call]), CancellationToken.None);
        await harness.Agents.RestoreAsync(run, harness.Request, CancellationToken.None);
        await harness.Agents.WaitAsync(run, CancellationToken.None);
        var state = Assert.Single(await harness.Repository.GetAgentsAsync(run, CancellationToken.None));
        Assert.Equal("failed", state.Status);
        Assert.Contains("Schreibzugriff", state.Result);
        Assert.DoesNotContain(await harness.Repository.GetEventsAfterAsync(run, 0), e => e.Type == RunEventTypes.ClientToolProposed);
        Assert.Equal(0, harness.Handler.Count);
    }

    [Fact]
    public async Task CancellationStopsActiveInferenceAndPersistsChildOutcome()
    {
        using var harness = new AgentHarness();
        var run = await harness.CreateAsync();
        await harness.Agents.ExecuteAsync(CodingSubagentTools.Start, run, "cancel-op",
            JsonSerializer.SerializeToElement(new { task = "Analyze assigned work" }), harness.Request, CancellationToken.None);
        await harness.Handler.FirstEntered.Task;
        harness.Agents.Cancel(run);
        await harness.Agents.WaitAsync(run, CancellationToken.None);
        Assert.Equal("cancelled", Assert.Single(await harness.Repository.GetAgentsAsync(run, CancellationToken.None)).Status);
        Assert.Contains(await harness.Repository.GetEventsAfterAsync(run, 0), e => e.Type == RunEventTypes.ServerToolCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoredPendingMutationConsumesPersistedResultWithoutReexecution(bool proposalAlreadyPublished)
    {
        using var harness = new AgentHarness();
        var run = await harness.CreateAsync();
        var call = new LmToolCall("write-once", "coding.write", JsonSerializer.SerializeToElement(new { path = "src/file.cs", content = "applied" }));
        var proposal = new ToolProposal("proposal-restored", run, call.Name, call.Arguments, ToolRiskClass.LocalMutation, "Assigned write", DateTimeOffset.MaxValue);
        await harness.Repository.SaveToolProposalAsync(proposal, CancellationToken.None);
        if (proposalAlreadyPublished)
            await harness.Repository.AppendEventAsync(run, RunEventTypes.ClientToolProposed, proposal, CancellationToken.None);
        await harness.Repository.SaveAgentAsync(new("child-restored", run, PairHandler.Secondary, "Finish assigned write", WritePaths,
            [new("user", "Task"), new("assistant", null, ToolCalls: [call])], PendingCalls: [call], ProposalId: proposal.ProposalId), CancellationToken.None);
        await harness.Repository.SaveClientToolResultAsync(run, new(proposal.ProposalId, "completed", JsonSerializer.SerializeToElement(new { applied = true, sha256 = "saved-hash" })), CancellationToken.None);
        harness.Handler.Release.TrySetResult();
        await harness.Agents.RestoreAsync(run, harness.Request, CancellationToken.None);
        await harness.Agents.WaitAsync(run, CancellationToken.None);
        var state = Assert.Single(await harness.Repository.GetAgentsAsync(run, CancellationToken.None));
        Assert.Equal("completed", state.Status);
        Assert.Null(state.PendingCalls);
        Assert.Null(state.ProposalId);
        var receipt = Assert.Single(state.Messages, m => m.Role == "tool");
        Assert.Equal(call.Id, receipt.ToolCallId);
        Assert.Contains("saved-hash", receipt.Content);
        var events = await harness.Repository.GetEventsAfterAsync(run, 0);
        if (proposalAlreadyPublished) Assert.Single(events, e => e.Type == RunEventTypes.ClientToolProposed);
        else Assert.DoesNotContain(events, e => e.Type == RunEventTypes.ClientToolProposed);
        Assert.Equal(1, harness.Handler.Count);
        await harness.Agents.RestoreAsync(run, harness.Request, CancellationToken.None);
        Assert.Equal(1, harness.Handler.Count);
    }

    [Fact]
    public async Task SameProviderCallIdInDifferentRunsDoesNotOverwriteChildState()
    {
        using var harness = new AgentHarness();
        harness.Handler.Release.TrySetResult();
        var first = await harness.CreateAsync();
        var second = await harness.CreateAsync();
        var args = JsonSerializer.SerializeToElement(new { task = "Independent task" });
        var firstResult = await harness.Agents.ExecuteAsync(CodingSubagentTools.Start, first, "reused-provider-call", args, harness.Request, CancellationToken.None);
        await harness.Agents.WaitAsync(first, CancellationToken.None);
        var secondResult = await harness.Agents.ExecuteAsync(CodingSubagentTools.Start, second, "reused-provider-call", args, harness.Request, CancellationToken.None);
        await harness.Agents.WaitAsync(second, CancellationToken.None);
        Assert.NotEqual(firstResult.Result.GetProperty("agentId").GetString(), secondResult.Result.GetProperty("agentId").GetString());
        Assert.Equal(first, Assert.Single(await harness.Repository.GetAgentsAsync(first, CancellationToken.None)).RunId);
        Assert.Equal(second, Assert.Single(await harness.Repository.GetAgentsAsync(second, CancellationToken.None)).RunId);
    }

    [Theory]
    [InlineData("Ich erstelle zunächst einen strukturierten Arbeitsplan.")]
    [InlineData("")]
    public async Task RestoredRepeatedAnnouncementCannotBecomeFalseCompletion(string response)
    {
        using var harness = new AgentHarness();
        harness.Handler.Response = response;
        harness.Handler.Release.TrySetResult();
        var run = await harness.CreateAsync();
        await harness.Repository.SaveAgentAsync(new("child-announcement", run, PairHandler.Secondary, "Implement assigned task", WritePaths,
            [new("user", "Implement assigned task")], IncompleteResponses: 2), CancellationToken.None);
        await harness.Agents.RestoreAsync(run, harness.Request, CancellationToken.None);
        await harness.Agents.WaitAsync(run, CancellationToken.None);
        var state = Assert.Single(await harness.Repository.GetAgentsAsync(run, CancellationToken.None));
        Assert.Equal("failed", state.Status);
        Assert.Equal(3, state.IncompleteResponses);
        Assert.Contains("nicht als erledigt", state.Result);
        Assert.Equal(1, harness.Handler.Count);
        Assert.DoesNotContain(await harness.Repository.GetEventsAfterAsync(run, 0), e => e.Type == RunEventTypes.ClientToolProposed);
    }

    [Fact]
    public async Task CancelledChildKeepsWriteReservationUntilDispatchedMutationResultArrives()
    {
        using var harness = new AgentHarness();
        var run = await harness.CreateAsync();
        var call = new LmToolCall("write-in-flight", "coding.write", JsonSerializer.SerializeToElement(new { path = "src/file.cs", content = "in flight" }));
        var proposal = new ToolProposal("proposal-in-flight", run, call.Name, call.Arguments, ToolRiskClass.LocalMutation, "Assigned write", DateTimeOffset.MaxValue);
        await harness.Repository.SaveToolProposalAsync(proposal, CancellationToken.None);
        await harness.Repository.AppendEventAsync(run, RunEventTypes.ClientToolProposed, proposal, CancellationToken.None);
        await harness.Repository.SaveAgentAsync(new("child-in-flight", run, PairHandler.Secondary, "Write assigned file", WritePaths,
            [new("user", "Task"), new("assistant", null, ToolCalls: [call])], PendingCalls: [call], ProposalId: proposal.ProposalId), CancellationToken.None);
        await harness.Agents.RestoreAsync(run, harness.Request, CancellationToken.None);
        harness.Agents.Cancel(run);
        var stopping = harness.Agents.WaitAsync(run, CancellationToken.None);
        Assert.Equal("running", Assert.Single(await harness.Repository.GetAgentsAsync(run, CancellationToken.None)).Status);
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Agents.ValidateParentMutationAsync(run,
            new("parent-edit", "coding.edit", JsonSerializer.SerializeToElement(new { path = "src/file.cs" })), CancellationToken.None));
        Assert.False(stopping.IsCompleted);
        await harness.Repository.SaveClientToolResultAsync(run, new(proposal.ProposalId, "completed",
            JsonSerializer.SerializeToElement(new { applied = true, sha256 = "final-write-hash" })), CancellationToken.None);
        await stopping;
        Assert.Equal("cancelled", Assert.Single(await harness.Repository.GetAgentsAsync(run, CancellationToken.None)).Status);
        await harness.Agents.ValidateParentMutationAsync(run,
            new("parent-edit", "coding.edit", JsonSerializer.SerializeToElement(new { path = "src/file.cs" })), CancellationToken.None);
        Assert.Equal(0, harness.Handler.Count);
        Assert.Single(await harness.Repository.GetEventsAfterAsync(run, 0), e => e.Type == RunEventTypes.ClientToolProposed);
    }

    [Fact]
    public async Task ConcurrentStartsAtomicallyReserveOnlyOneWorker()
    {
        using var harness = new AgentHarness();
        var run = await harness.CreateAsync();
        var args = JsonSerializer.SerializeToElement(new { task = "Implementiere den zugewiesenen Bereich", writePaths = DirectoryWritePaths });
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
            harness.Agents.ExecuteAsync(CodingSubagentTools.Start, run, "start-" + index, args, harness.Request, CancellationToken.None)));
        Assert.Single(results, result => result.Succeeded);
        Assert.Single(await harness.Repository.GetAgentsAsync(run, CancellationToken.None));
        await harness.Handler.FirstEntered.Task;
        Assert.Equal(1, harness.Handler.Count);
        harness.Handler.Release.TrySetResult();
        await harness.Agents.WaitAsync(run, CancellationToken.None);
    }

    [Fact]
    public async Task OldClientCannotLaunchUnisolatedChildTerminalByIgnoringNewProposalFields()
    {
        using var harness = new AgentHarness();
        var run = await harness.CreateAsync();
        var legacy = harness.Request with { ClientCapabilities = ["coding"] };
        var result = await harness.Agents.ExecuteAsync(CodingSubagentTools.Start, run, "legacy-start",
            JsonSerializer.SerializeToElement(new { task = "Führe Tests aus" }), legacy, CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Equal("agent.client_update_required", result.ErrorCode);
        Assert.Empty(await harness.Repository.GetAgentsAsync(run, CancellationToken.None));
        Assert.DoesNotContain(CodingSubagentService.DelegatableTools(new AgentToolCatalog(), legacy), tool => tool.Name == ClientToolNames.CodingCommand);
        Assert.Equal(0, harness.Handler.Count);
    }

    [Fact]
    public async Task DirectoryReservationAllowsParallelSiblingWriteButRejectsNestedParentWriteAndTerminal()
    {
        using var harness = new AgentHarness();
        var run = await harness.CreateAsync();
        await harness.Agents.ExecuteAsync(CodingSubagentTools.Start, run, "scoped-start",
            JsonSerializer.SerializeToElement(new { task = "Implementiere die Komponente", writePaths = DirectoryWritePaths }), harness.Request, CancellationToken.None);
        await harness.Handler.FirstEntered.Task;
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Agents.ValidateParentMutationAsync(run,
            new("write", "coding.write", JsonSerializer.SerializeToElement(new { path = "SRC/component/child/new.cs" })), CancellationToken.None));
        await harness.Agents.ValidateParentMutationAsync(run,
            new("write", "coding.write", JsonSerializer.SerializeToElement(new { path = "src/component-other/new.cs" })), CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Agents.ValidateParentMutationAsync(run,
            new("command", "coding.command", JsonSerializer.SerializeToElement(new { executable = "dotnet", arguments = TestArguments })), CancellationToken.None));
        harness.Handler.Release.TrySetResult();
        await harness.Agents.WaitAsync(run, CancellationToken.None);
    }

    [Theory]
    [InlineData("coding.command")]
    [InlineData("coding.write")]
    [InlineData("coding.gitDiff")]
    public async Task ChildClientToolsCarryPersistedIsolationScopeAndResultsAreNotReplayed(string tool)
    {
        using var harness = new AgentHarness();
        var run = await harness.CreateAsync();
        var args = tool switch
        {
            "coding.command" => JsonSerializer.SerializeToElement(new { executable = "dotnet", arguments = TestArguments }),
            "coding.write" => JsonSerializer.SerializeToElement(new { path = "src/component/new.cs", content = "test" }),
            _ => JsonSerializer.SerializeToElement(new { }),
        };
        var call = new LmToolCall("child-tool", tool, args);
        await harness.Repository.SaveAgentAsync(new("child-scope", run, PairHandler.Secondary, "Prüfe und ändere den Bereich", ["src/component/"],
            [new("user", "Auftrag"), new("assistant", null, ToolCalls: [call])], PendingCalls: [call]), CancellationToken.None);
        await harness.Agents.RestoreAsync(run, harness.Request, CancellationToken.None);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        ToolProposal? proposal = null;
        while (proposal is null)
        {
            var events = await harness.Repository.GetEventsAfterAsync(run, 0, cancellationToken: deadline.Token);
            var proposed = events.FirstOrDefault(item => item.Type == RunEventTypes.ClientToolProposed);
            if (proposed is not null) proposal = proposed.Data.Deserialize<ToolProposal>(GoAiProtocol.CreateJsonOptions());
            if (proposal is null) await Task.Delay(10, deadline.Token);
        }
        Assert.Equal("child-scope", proposal.ExecutionScope!.AgentId);
        Assert.Equal(["src/component/"], proposal.ExecutionScope.WritePaths);
        Assert.True(proposal.ExecutionScope.IsolatedWorkspace);
        var persisted = await harness.Repository.GetToolProposalAsync(proposal.ProposalId, run);
        Assert.Equal(proposal.ExecutionScope.AgentId, persisted!.ExecutionScope!.AgentId);
        harness.Handler.Release.TrySetResult();
        await harness.Repository.SaveClientToolResultAsync(run, new(proposal.ProposalId, "completed",
            JsonSerializer.SerializeToElement(new { exitCode = 0, applied = true, stdout = "Verified in isolated workspace" })));
        await harness.Agents.WaitAsync(run, deadline.Token);
        var state = Assert.Single(await harness.Repository.GetAgentsAsync(run, CancellationToken.None));
        Assert.Equal("completed", state.Status);
        Assert.Single(state.WorkingState!.Evidence);
        Assert.True(state.WorkingState.Evidence[0].Success);
        await harness.Agents.RestoreAsync(run, harness.Request, deadline.Token);
        Assert.Single(await harness.Repository.GetEventsAfterAsync(run, 0), item => item.Type == RunEventTypes.ClientToolProposed);
    }

    [Fact]
    public async Task ParentPrefixAndSharedContextCountSurvivePersistedChildRestart()
    {
        using var harness = new AgentHarness();
        var run = await harness.CreateAsync();
        var parent = ModelRuntimeClient.PrepareLanguageBoundMessages([new("system", "Gemeinsame stabile Richtlinie"), new("user", "Ursprünglicher Auftrag"),
            new("assistant", "Bereits geprüft", ReasoningContent: "Deutscher Denktext"), new("user", "Konkrete Folgeaufgabe")]).ToArray();
        await harness.Agents.ExecuteAsync(CodingSubagentTools.Start, run, "context-start",
            JsonSerializer.SerializeToElement(new { task = "Implementiere die Datei", writePaths = WritePaths }), harness.Request,
            CancellationToken.None, parentMessages: parent);
        await harness.Handler.FirstEntered.Task;
        var state = Assert.Single(await harness.Repository.GetAgentsAsync(run, CancellationToken.None));
        Assert.Equal(parent.Length, state.SharedContextMessages);
        Assert.Equal(parent, state.Messages.Take(parent.Length));
        Assert.Contains(state.Messages, message => message.Role == "system" && message.Content!.Contains(CodingAgentPolicy.ReasoningLanguagePrompt, StringComparison.Ordinal));
        harness.Handler.Release.TrySetResult();
        await harness.Agents.WaitAsync(run, CancellationToken.None);
        var reloaded = Assert.Single(await harness.Repository.GetAgentsAsync(run, CancellationToken.None));
        Assert.Equal(parent, reloaded.Messages.Take(parent.Length));
        Assert.Equal(32, reloaded.InputTokens);
    }

    [Fact]
    public async Task ChildNarrationContextAndMetricsRemainExplicitlyAttributedInTheRunJournal()
    {
        using var harness = new AgentHarness();
        var run = await harness.CreateAsync();
        await harness.Repository.AppendEventAsync(run, RunEventTypes.TextDelta, new TextDeltaEvent("Hauptagententext"));
        harness.Handler.Release.TrySetResult();
        var started = await harness.Agents.ExecuteAsync(CodingSubagentTools.Start, run, "attributed-start",
            JsonSerializer.SerializeToElement(new { task = "Prüfe den Teilauftrag" }), harness.Request, CancellationToken.None);
        var id = started.Result.GetProperty("agentId").GetString();
        await harness.Agents.WaitAsync(run, CancellationToken.None);
        var events = await harness.Repository.GetEventsAfterAsync(run, 0);
        var parent = Assert.Single(events, item => item.Type == RunEventTypes.TextDelta && !item.Data.TryGetProperty("agentId", out _));
        Assert.Equal("Hauptagententext", parent.Data.GetProperty("delta").GetString());
        var narration = Assert.Single(events, item => item.Type == RunEventTypes.TextDelta && item.Data.TryGetProperty("agentId", out _));
        Assert.Equal("verified", narration.Data.GetProperty("delta").GetString());
        Assert.Equal("completed", narration.Data.GetProperty("state").GetString());
        Assert.Equal(0, narration.Data.GetProperty("replaceFrom").GetInt32());
        Assert.Contains(events, item => item.Type == RunEventTypes.ContextChanged);
        var metrics = Assert.Single(events, item => item.Type == RunEventTypes.CodingMetrics);
        Assert.Equal(32, metrics.Data.GetProperty("metrics").GetProperty("inputTokens").GetInt32());
        Assert.All(events.Where(item => item != parent && item.Type is RunEventTypes.TextDelta or RunEventTypes.ModelGeneration
            or RunEventTypes.ContextChanged or RunEventTypes.CodingMetrics or RunEventTypes.ServerToolStarted or RunEventTypes.ServerToolCompleted),
            item => Assert.Equal(id, item.Data.GetProperty("agentId").GetString()));
    }

    [Fact]
    public async Task NativeChildStreamPublishesRealReasoningAndEvenShortNarrationSeparately()
    {
        using var harness = new AgentHarness();
        var run = await harness.CreateAsync();
        harness.Handler.StreamReasoning = true;
        harness.Handler.Release.TrySetResult();
        await harness.Agents.ExecuteAsync(CodingSubagentTools.Start, run, "streamed-child",
            JsonSerializer.SerializeToElement(new { task = "Prüfe den Teilauftrag" }), harness.Request, CancellationToken.None);
        await harness.Agents.WaitAsync(run, CancellationToken.None);
        var state = Assert.Single(await harness.Repository.GetAgentsAsync(run, CancellationToken.None));
        Assert.Equal("completed", state.Status);
        Assert.Equal("Belegtes Ergebnis.", state.Result);
        var events = await harness.Repository.GetEventsAfterAsync(run, 0);
        Assert.Contains(events, item => item.Type == RunEventTypes.ReasoningDelta
            && item.Data.GetProperty("state").GetString() == "running"
            && item.Data.GetProperty("delta").GetString() == "Ich prüfe die Datei.");
        Assert.Contains(events, item => item.Type == RunEventTypes.TextDelta
            && item.Data.GetProperty("state").GetString() == "running"
            && item.Data.GetProperty("delta").GetString() == "Belegtes Ergebnis.");
        Assert.All(events.Where(item => item.Type is RunEventTypes.ReasoningDelta or RunEventTypes.TextDelta),
            item => Assert.Equal(state.Id, item.Data.GetProperty("agentId").GetString()));
        Assert.Equal("Ich prüfe die Datei.", state.Messages[^1].ReasoningContent);
    }

    [Theory]
    [InlineData("subagent")]
    [InlineData("compaction")]
    [InlineData("research-4-0")]
    public async Task ChildReasoningAndNarrationRetriesReplaceOnlyTheirOwnRoundAndPhase(string phase)
    {
        using var harness = new AgentHarness();
        var run = await harness.CreateAsync();
        var state = new CodingSubagentState("agent-progress", run, PairHandler.Secondary, "Auftrag", [], [], Round: 4);
        var progress = new CodingSubagentService.ProgressPublisher(harness.Repository, state, phase);
        await progress.PublishAsync(new("promptProcessing", PromptTokens: 200, ProcessedPromptTokens: 150, CachedPromptTokens: 50), CancellationToken.None);
        await progress.PublishAsync(new("reasoningDelta", ReasoningDelta: "Erste Überlegung."), CancellationToken.None);
        await progress.PublishAsync(new("contentDelta", ContentDelta: "Erste Erklärung."), CancellationToken.None);
        await progress.PublishAsync(new("generationRetry", Attempt: 2), CancellationToken.None);
        await progress.PublishAsync(new("reasoningDelta", ReasoningDelta: "Korrigierte Überlegung."), CancellationToken.None);
        await progress.PublishAsync(new("contentDelta", ContentDelta: "Korrigierte Erklärung."), CancellationToken.None);
        await progress.CompleteAsync(new("Belegter Abschluss.", [], 200, 30, ReasoningContent: "Belegte deutsche Analyse.",
            Metrics: new(CachedPromptTokens: 50, InputTokens: 200, OutputTokens: 30)), "high", CancellationToken.None);
        var events = await harness.Repository.GetEventsAfterAsync(run, 0);
        Assert.All(events, item =>
        {
            Assert.Equal(state.Id, item.Data.GetProperty("agentId").GetString());
            Assert.Equal(5, item.Data.GetProperty("round").GetInt32());
            Assert.Equal(phase, item.Data.GetProperty("phase").GetString());
        });
        var reasoning = events.Where(item => item.Type == RunEventTypes.ReasoningDelta).ToArray();
        var narration = events.Where(item => item.Type == RunEventTypes.TextDelta).ToArray();
        Assert.Equal(3, reasoning.Length);
        Assert.Equal(3, narration.Length);
        Assert.All(reasoning.Concat(narration), item => Assert.Equal(0, item.Data.GetProperty("replaceFrom").GetInt32()));
        Assert.Equal("Belegte deutsche Analyse.", reasoning[^1].Data.GetProperty("delta").GetString());
        Assert.Equal("Belegter Abschluss.", narration[^1].Data.GetProperty("delta").GetString());
        Assert.Equal("completed", reasoning[^1].Data.GetProperty("state").GetString());
        Assert.DoesNotContain(narration, item => item.Data.GetProperty("delta").GetString()!.Contains("Überlegung", StringComparison.Ordinal));
        Assert.Equal(50, Assert.Single(events, item => item.Type == RunEventTypes.CodingMetrics)
            .Data.GetProperty("metrics").GetProperty("cachedPromptTokens").GetInt32());
    }

    [Fact]
    public async Task RetryWithoutReasoningClearsAbandonedChildThinkingAndCancellationClosesLiveText()
    {
        using var harness = new AgentHarness();
        var run = await harness.CreateAsync();
        var state = new CodingSubagentState("agent-progress", run, PairHandler.Secondary, "Auftrag", [], []);
        var progress = new CodingSubagentService.ProgressPublisher(harness.Repository, state, "subagent");
        await progress.PublishAsync(new("reasoningDelta", ReasoningDelta: "Abgebrochener Versuch"), CancellationToken.None);
        await progress.PublishAsync(new("generationRetry", Attempt: 2), CancellationToken.None);
        await progress.CompleteAsync(new("Endergebnis", [], 10, 4), null, CancellationToken.None);
        var events = await harness.Repository.GetEventsAfterAsync(run, 0);
        var finalReasoning = events.Last(item => item.Type == RunEventTypes.ReasoningDelta);
        Assert.Equal("", finalReasoning.Data.GetProperty("delta").GetString());
        Assert.Equal(0, finalReasoning.Data.GetProperty("replaceFrom").GetInt32());
        Assert.Equal("completed", finalReasoning.Data.GetProperty("state").GetString());

        var next = new CodingSubagentService.ProgressPublisher(harness.Repository, state with { Round = 1 }, "subagent");
        await next.PublishAsync(new("reasoningDelta", ReasoningDelta: "Unfertige deutsche Analyse"), CancellationToken.None);
        await next.StopAsync("cancelled", CancellationToken.None);
        var stopped = (await harness.Repository.GetEventsAfterAsync(run, 0)).Last(item => item.Type == RunEventTypes.ReasoningDelta);
        Assert.Equal("cancelled", stopped.Data.GetProperty("state").GetString());
        Assert.Equal(2, stopped.Data.GetProperty("round").GetInt32());
    }

    [Fact]
    public async Task SteeringInterruptsChildInferenceAndReleasesItsReservationAtASafeBoundary()
    {
        using var harness = new AgentHarness();
        var request = harness.Request with { SessionId = "child-steering-session" };
        var run = (await harness.Repository.CreateAsync(request, null)).Snapshot.RunId;
        await harness.Agents.ExecuteAsync(CodingSubagentTools.Start, run, "child-start",
            JsonSerializer.SerializeToElement(new { task = "Datei bearbeiten", writePaths = WritePaths }), request, CancellationToken.None);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await harness.Handler.FirstEntered.Task.WaitAsync(deadline.Token);
        await harness.Repository.AcceptSteeringAsync(run, new(request.SessionId!, "new-input", "Die Teilaufgabe entfällt."), deadline.Token);
        await harness.Agents.WaitAsync(run, deadline.Token);
        var state = Assert.Single(await harness.Repository.GetAgentsAsync(run, deadline.Token));
        Assert.Equal("steered", state.Status);
        Assert.Equal(1, harness.Handler.Count);
        await harness.Agents.ValidateParentMutationAsync(run, new("parent-write", "coding.write",
            JsonSerializer.SerializeToElement(new { path = "src/file.cs", content = "neues Ziel" })), deadline.Token);
    }

    [Fact]
    public async Task SteeringKeepsChildWriteReservationUntilDispatchedReceiptArrives()
    {
        using var harness = new AgentHarness();
        var request = harness.Request with { SessionId = "child-tool-session" };
        var run = (await harness.Repository.CreateAsync(request, null)).Snapshot.RunId;
        var first = new LmToolCall("child-first", "coding.write", JsonSerializer.SerializeToElement(new { path = "src/file.cs", content = "fertig" }));
        var stale = first with { Id = "child-stale" };
        var proposal = new ToolProposal("child-proposal", run, first.Name, first.Arguments, ToolRiskClass.LocalMutation,
            "Datei ändern", DateTimeOffset.MaxValue, new("child-safe", WritePaths));
        await harness.Repository.SaveToolProposalAsync(proposal);
        await harness.Repository.AppendEventAsync(run, RunEventTypes.ClientToolProposed, proposal);
        await harness.Repository.SaveAgentAsync(new("child-safe", run, PairHandler.Secondary, "Datei bearbeiten", WritePaths,
            [new("user", "Datei bearbeiten"), new("assistant", null, ToolCalls: [first, stale])],
            PendingCalls: [first, stale], ProposalId: proposal.ProposalId), CancellationToken.None);
        await harness.Agents.RestoreAsync(run, request, CancellationToken.None);
        await harness.Repository.AcceptSteeringAsync(run, new(request.SessionId!, "new-input", "Keine weiteren Änderungen."));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Agents.ValidateParentMutationAsync(run, first, CancellationToken.None));
        await harness.Repository.SaveClientToolResultAsync(run, new(proposal.ProposalId, "completed", JsonSerializer.SerializeToElement(new { applied = true })));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await harness.Agents.WaitAsync(run, deadline.Token);
        var state = Assert.Single(await harness.Repository.GetAgentsAsync(run, deadline.Token));
        Assert.Equal("steered", state.Status);
        Assert.Contains("completed", Assert.Single(state.Messages, message => message.ToolCallId == first.Id).Content);
        Assert.Contains("not_executed", Assert.Single(state.Messages, message => message.ToolCallId == stale.Id).Content);
        Assert.Single(await harness.Repository.GetEventsAfterAsync(run, 0), item => item.Type == RunEventTypes.ClientToolProposed);
        Assert.Equal(0, harness.Handler.Count);
        await harness.Agents.ValidateParentMutationAsync(run, first, deadline.Token);
    }

    [Fact]
    public async Task DocumentAgentWaitsForOwnWorkerAndPersistsItsKindWithoutSecondaryModel()
    {
        using var harness = new AgentHarness();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var request = harness.Request with { Mode = RunMode.General, CodingOptions = null,
            PreferredGeneralModelId = PairHandler.Main, ClientCapabilities = ["documentIo", "document-agent"] };
        var run = (await harness.Repository.CreateAsync(request, null)).Snapshot.RunId;
        LmChatMessage[] parent = [new("system", "Gemeinsamer Ausgangskontext"), new("user", "Erstelle ein Dokument")];
        var work = harness.Agents.ExecuteAsync(WorkspaceTools.DocumentAgent, run, "document-start",
            JsonSerializer.SerializeToElement(new { task = "Erstelle den Bericht." }), request, deadline.Token, parent);
        await harness.Handler.FirstEntered.Task.WaitAsync(deadline.Token);
        Assert.False(work.IsCompleted);
        var pending = Assert.Single(await harness.Repository.GetAgentsAsync(run, deadline.Token));
        Assert.Equal("document", pending.Kind);
        Assert.Equal(PairHandler.Main, pending.Model);
        Assert.Equal(parent, pending.Messages.Take(parent.Length));
        Assert.Contains(pending.Messages, message => message.Content == CodingSubagentService.DocumentAgentPolicy);
        harness.Handler.Release.TrySetResult();
        Assert.True((await work).Succeeded);
        var completed = Assert.Single(await harness.Repository.GetAgentsAsync(run, deadline.Token));
        Assert.Equal("document", completed.Kind);
        Assert.Equal("completed", completed.Status);
        Assert.Contains(await harness.Repository.GetEventsAfterAsync(run, 0), item => item.Type == RunEventTypes.ServerToolCompleted
            && item.Data.GetProperty("tool").GetString() == WorkspaceTools.DocumentAgent);
    }

    private sealed class AgentHarness : IDisposable
    {
        private readonly TestServerContext _context = new();
        private readonly HttpClient _http;
        private readonly ServiceProvider _provider;
        internal PairHandler Handler { get; } = new();
        internal RunRequest Request { get; } = new(GoAiProtocol.Version, RunMode.Coding,
            [new("user", [new("text", Text: "Task")])], ClientCapabilities: ["coding", CodingSubagentService.IsolatedWorkspaceCapability], CodingOptions: new(ParallelModelId: PairHandler.Base));
        internal RunRepository Repository => _provider.GetRequiredService<RunRepository>();
        internal CodingSubagentService Agents => _provider.GetRequiredService<CodingSubagentService>();
        internal AgentHarness()
        {
            _context.Options.ModelRuntimeUri = new("http://native.test");
            _http = new HttpClient(Handler);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddGoAiServerServices(_context.Options, includeHostedServices: false);
            services.AddSingleton(_context.Database);
            services.AddSingleton(new ModelRuntimeClient(_http, _context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance));
            _provider = services.BuildServiceProvider();
        }
        internal async Task<string> CreateAsync() => (await Repository.CreateAsync(Request, null)).Snapshot.RunId;
        public void Dispose() { _provider.Dispose(); _http.Dispose(); Handler.Dispose(); _context.Dispose(); }
    }

    private sealed class PairHandler : HttpMessageHandler
    {
        private static readonly string[] ModelIds = [Main, Secondary];
        private static readonly string[] Tags = ["go-context-train:32768"];
        internal const string Base = "coding/Qwen3.8-27B-fixture", Main = Base + "~main", Secondary = Base + "~secondary";
        internal TaskCompletionSource BothEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;
        internal int Count => _count;
        internal string Response { get; set; } = "verified";
        internal bool StreamReasoning { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath is "/sessions/prepare" or "/sessions/save")
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/v1/models": return Json(new { data = ModelIds.Select(id => new { id, tags = Tags, status = new { value = "loaded" } }) });
                case "/props": return Json(new { default_generation_settings = new { n_ctx = 32768 } });
                case "/v1/chat/completions/input_tokens": return Json(new { input_tokens = 64 });
                case "/v1/chat/completions":
                    if (Interlocked.Increment(ref _count) == 2) BothEntered.TrySetResult();
                    FirstEntered.TrySetResult();
                    await Release.Task.WaitAsync(token);
                    if (StreamReasoning)
                    {
                        var reasoning = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { reasoning_content = "Ich prüfe die Datei." } } } });
                        var narration = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = "Belegtes Ergebnis." }, finish_reason = "stop" } },
                            usage = new { prompt_tokens = 32, completion_tokens = 8 } });
                        return new(HttpStatusCode.OK) { Content = new StringContent("data: " + reasoning + "\n\ndata: " + narration + "\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
                    }
                    return Json(new { choices = new[] { new { message = new { role = "assistant", content = Response }, finish_reason = "stop" } }, usage = new { prompt_tokens = 32, completion_tokens = 8 } });
                default: throw new InvalidOperationException(request.RequestUri.AbsolutePath);
            }
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
