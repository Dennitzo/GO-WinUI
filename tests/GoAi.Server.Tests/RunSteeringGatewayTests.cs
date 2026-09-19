using System.Text.Json;
using GoAi.Contracts;
using GoAi.Server.Core.Gateway;
using GoAi.Server.Core.Runs;
using GoAi.Server.Core.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace GoAi.Server.Tests;

public sealed class RunSteeringGatewayTests
{
    [Fact]
    public async Task MappedRouteAcceptsInSameRunAndReturnsIdempotentReceiptAfterTerminalState()
    {
        await using var gateway = new GatewayFixture();
        var run = await gateway.CreateRunAsync();
        var request = new RunSteeringRequest("session-fixture", "input-one", "Bitte zuerst die Sidebar.");

        var first = await gateway.SubmitAsync(run, request);
        Assert.Equal(StatusCodes.Status202Accepted, first.Status);
        var accepted = first.Body.Deserialize<RunSteeringAccepted>(GoAiProtocol.CreateJsonOptions())!;
        Assert.Equal(run, accepted.RunId);
        Assert.False(accepted.Duplicate);
        Assert.Equal("accepted", accepted.State);
        Assert.Equal(RunState.Running, (await gateway.Repository.GetAsync(run))!.State);
        await gateway.Repository.UpdateStateAsync(run, RunState.Cancelled);

        var repeated = await gateway.SubmitAsync(run, request);

        Assert.Equal(StatusCodes.Status200OK, repeated.Status);
        var duplicate = repeated.Body.Deserialize<RunSteeringAccepted>(GoAiProtocol.CreateJsonOptions())!;
        Assert.True(duplicate.Duplicate);
        Assert.Equal(accepted.Sequence, duplicate.Sequence);
        Assert.Equal(run, duplicate.RunId);
        Assert.Single(await gateway.Repository.GetEventsAfterAsync(run, 0), item => item.Type == RunSteeringEventTypes.Accepted);
    }

    [Fact]
    public async Task RouteReturnsConflictForWrongSessionIdReuseAndNewInputAfterCompletion()
    {
        await using var gateway = new GatewayFixture();
        var run = await gateway.CreateRunAsync();
        var request = new RunSteeringRequest("session-fixture", "input-one", "Erste Anweisung");
        Assert.Equal(StatusCodes.Status202Accepted, (await gateway.SubmitAsync(run, request)).Status);

        var wrongSession = await gateway.SubmitAsync(run, request with { SessionId = "foreign-session" });
        var changedText = await gateway.SubmitAsync(run, request with { Text = "Andere Anweisung" });
        await gateway.Repository.UpdateStateAsync(run, RunState.Completed);
        var terminal = await gateway.SubmitAsync(run, request with { InputId = "new-input" });

        foreach (var response in new[] { wrongSession, changedText, terminal })
        {
            Assert.Equal(StatusCodes.Status409Conflict, response.Status);
            Assert.Equal("operation.invalid_state", response.Body.Deserialize<GoAiProblem>(GoAiProtocol.CreateJsonOptions())!.ErrorCode);
        }
        Assert.Single(await gateway.Repository.GetPendingSteeringAsync(run));
        Assert.Equal(RunState.Completed, (await gateway.Repository.GetAsync(run))!.State);
    }

    [Fact]
    public async Task UnknownRunReturnsNotFoundWithoutInventingAConversation()
    {
        await using var gateway = new GatewayFixture();

        var response = await gateway.SubmitAsync("run-does-not-exist", new("session-fixture", "input", "Neue Priorität"));

        Assert.Equal(StatusCodes.Status404NotFound, response.Status);
        Assert.Equal("resource.not_found", response.Body.Deserialize<GoAiProblem>(GoAiProtocol.CreateJsonOptions())!.ErrorCode);
        Assert.Null(await gateway.Repository.GetAsync("run-does-not-exist"));
    }

    [Theory]
    [InlineData("session", "", "Text")]
    [InlineData("", "input", "Text")]
    [InlineData("session", "input", " ")]
    public async Task MissingRequiredInputIsBadRequestAndCreatesNoSteeringEvent(string session, string input, string text)
    {
        await using var gateway = new GatewayFixture();
        var run = await gateway.CreateRunAsync();

        var response = await gateway.SubmitAsync(run, new(session, input, text));

        Assert.Equal(StatusCodes.Status400BadRequest, response.Status);
        Assert.Empty(await gateway.Repository.GetPendingSteeringAsync(run));
        Assert.Empty(await gateway.Repository.GetEventsAfterAsync(run, 0));
    }

    [Fact]
    public async Task ExcessiveInputAndModelOrWorkspaceMutationAreNotAcceptedByTheSteeringContract()
    {
        await using var gateway = new GatewayFixture();
        var run = await gateway.CreateRunAsync();
        var excessive = await gateway.SubmitAsync(run, new("session-fixture", "input", new string('x', 128_001)));
        Assert.Equal(StatusCodes.Status400BadRequest, excessive.Status);
        var alteredScope = await gateway.SubmitRawAsync(run, JsonSerializer.SerializeToUtf8Bytes(new
        {
            sessionId = "session-fixture", inputId = "input", text = "Andere Aufgabe",
            preferredCodingModelId = "different-model", workspacePath = "C:/different-project",
        }));
        Assert.Equal(StatusCodes.Status400BadRequest, alteredScope.Status);
        Assert.Empty(await gateway.Repository.GetPendingSteeringAsync(run));
        var request = (await gateway.Repository.GetRequestAsync(run))!;
        Assert.Equal("coding/fixture", request.PreferredCodingModelId);
        Assert.Equal("C:/fixture/project", request.CodingOptions!.WorkspacePath);
    }

    private sealed class GatewayFixture : IAsyncDisposable
    {
        private readonly TestServerContext _context = new();
        private readonly ServiceProvider _services;
        private readonly RequestDelegate _route;
        public RunRepository Repository { get; }

        public GatewayFixture()
        {
            Repository = new RunRepository(_context.Database, new RunEventNotifier());
            _services = new ServiceCollection().AddLogging().AddRouting()
                .AddSingleton(Repository).AddSingleton<RunWorkChannel>()
                .AddSingleton(new ServerRuntimeState(_context.WrappedOptions)).BuildServiceProvider();
            var routes = new RouteBuilder(_services);
            GatewayEndpoints.Map(routes);
            var endpoint = Assert.Single(routes.DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>(),
                endpoint => endpoint.RoutePattern.RawText == "/v1/runs/{runId}/steer");
            _route = endpoint.RequestDelegate!;
        }

        public async Task<string> CreateRunAsync()
        {
            var created = await Repository.CreateAsync(new RunRequest(GoAiProtocol.Version, RunMode.Coding,
                [new RunMessage("user", [new ContentPart("text", "Originale Aufgabe")])], SessionId: "session-fixture",
                PreferredCodingModelId: "coding/fixture", CodingOptions: new(WorkspacePath: "C:/fixture/project")), null);
            await Repository.UpdateStateAsync(created.Snapshot.RunId, RunState.Running);
            return created.Snapshot.RunId;
        }

        public Task<(int Status, JsonElement Body)> SubmitAsync(string run, RunSteeringRequest input) =>
            SubmitRawAsync(run, JsonSerializer.SerializeToUtf8Bytes(input, GoAiProtocol.CreateJsonOptions()));

        public async Task<(int Status, JsonElement Body)> SubmitRawAsync(string run, byte[] body)
        {
            var http = new DefaultHttpContext { RequestServices = _services };
            http.Request.Method = "POST";
            http.Request.Path = $"/v1/runs/{run}/steer";
            http.Request.RouteValues["runId"] = run;
            http.Request.ContentType = "application/json";
            http.Request.ContentLength = body.Length;
            await using var request = new MemoryStream(body);
            await using var response = new MemoryStream();
            http.Request.Body = request;
            http.Response.Body = response;
            await new ProblemDetailsMiddleware(_route).InvokeAsync(http, _services.GetRequiredService<ServerRuntimeState>());
            return (http.Response.StatusCode, JsonSerializer.Deserialize<JsonElement>(response.ToArray()));
        }

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            _context.Dispose();
        }
    }

    private sealed class RouteBuilder(IServiceProvider services) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = services;
        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();
        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }
}
