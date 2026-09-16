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
    [Fact]
    public void LongSubagentTasksProduceValidCompactClientSummaries()
    {
        var task = "Mehrzeiliger Auftrag\r\n\t" + new string('ä', 16000) + "\0";
        var summary = CodingSubagentService.BuildToolSummary(task, "coding.read");
        Assert.StartsWith("Subagent GPU1 · coding.read · Mehrzeiliger Auftrag", summary);
        Assert.True(summary.Length <= 1000);
        Assert.DoesNotContain(summary, char.IsControl);
        Assert.EndsWith("…", summary);
        Assert.Contains("keinen Terminalzugriff", CodingSubagentService.CapabilityPolicy);
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
            ClientCapabilities: ["coding"], CodingOptions: new(ParallelModelId: PairHandler.Base));
        var run = (await repository.CreateAsync(request, null)).Snapshot.RunId;
        var result = await agents.ExecuteAsync(CodingSubagentTools.Start, run, "operation-one",
            JsonSerializer.SerializeToElement(new { task = "Review file", writePaths = WritePaths }), request, CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.True(CodingWorkingStateReducer.IsSuccessfulResult(result.Result));
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

    private sealed class AgentHarness : IDisposable
    {
        private readonly TestServerContext _context = new();
        private readonly HttpClient _http;
        private readonly ServiceProvider _provider;
        internal PairHandler Handler { get; } = new();
        internal RunRequest Request { get; } = new(GoAiProtocol.Version, RunMode.Coding,
            [new("user", [new("text", Text: "Task")])], ClientCapabilities: ["coding"], CodingOptions: new(ParallelModelId: PairHandler.Base));
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
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/v1/models": return Json(new { data = ModelIds.Select(id => new { id, tags = Tags, status = new { value = "loaded" } }) });
                case "/props": return Json(new { default_generation_settings = new { n_ctx = 32768 } });
                case "/v1/chat/completions/input_tokens": return Json(new { input_tokens = 64 });
                case "/v1/chat/completions":
                    if (Interlocked.Increment(ref _count) == 2) BothEntered.TrySetResult();
                    FirstEntered.TrySetResult();
                    await Release.Task.WaitAsync(token);
                    return Json(new { choices = new[] { new { message = new { role = "assistant", content = Response }, finish_reason = "stop" } }, usage = new { prompt_tokens = 32, completion_tokens = 8 } });
                default: throw new InvalidOperationException(request.RequestUri.AbsolutePath);
            }
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
