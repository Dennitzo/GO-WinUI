using System.Text.Json;
using GoAi.Contracts;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using GoWinUI.Infrastructure.Repositories;
using GoWinUI.Infrastructure.Storage;

namespace GoWinUI.Tests;

public sealed class CodingToolJournalRecoveryTests
{
    [Fact]
    public async Task CompleteFileReadLargerThanProtocolDefaultSurvivesJournalRestart()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var run = await CreateRunAsync(environment, "server-large-read");
        var journal = environment.Get<IClientToolExecutionRepository>();
        await journal.BeginAsync(Claim("large-read", run.Id, "server-large-read", 1) with { ToolName = ClientToolNames.CodingRead });
        var receipt = JsonSerializer.Serialize(new ClientToolResult("large-read", "completed",
            JsonSerializer.SerializeToElement(new { content = new string('x', 5 * 1024 * 1024), truncated = false })), GoAiProtocol.CreateJsonOptions());
        await journal.CompleteAsync("large-read", receipt);
        var reopened = new SqliteClientToolExecutionRepository(environment.Get<SqliteDatabase>());
        Assert.Equal(receipt, (await reopened.GetAsync("large-read"))!.ResultJson);
        await journal.BeginAsync(Claim("large-command", run.Id, "server-large-read", 2));
        await Assert.ThrowsAsync<InvalidDataException>(() => journal.CompleteAsync("large-command", receipt));
    }

    [Fact]
    public async Task RestartFindsOnlyUnfinishedClaimsForExactLocalAndServerRunWithoutRewindingTheCursor()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var run = await CreateRunAsync(environment, "server-current");
        var other = await CreateRunAsync(environment, "server-other");
        var journal = environment.Get<IClientToolExecutionRepository>();
        await journal.BeginAsync(Claim("current-later", run.Id, "server-current", 12));
        await journal.BeginAsync(Claim("current-earlier", run.Id, "server-current", 8));
        await journal.BeginAsync(Claim("previous-attempt", run.Id, "server-previous", 7));
        await journal.BeginAsync(Claim("other-session", other.Id, "server-current", 6));
        await journal.BeginAsync(Claim("completed", run.Id, "server-current", 9));
        await journal.CompleteAsync("completed", Receipt("completed"));
        await journal.BeginAsync(Claim("submitted", run.Id, "server-current", 10));
        await journal.CompleteAsync("submitted", Receipt("submitted"));
        await journal.MarkSubmittedAsync("submitted");

        // A fresh repository opens independent SQLite connections. The event
        // cursor can already be beyond the claimed command when GO restarts.
        var reopened = new SqliteClientToolExecutionRepository(environment.Get<SqliteDatabase>());
        var unfinished = await reopened.ListIncompleteExecutionsAsync(run.Id, "server-current");

        Assert.Equal(2, unfinished.Count);
        Assert.Equal("current-earlier", unfinished[0].ProposalId);
        Assert.Equal("current-later", unfinished[1].ProposalId);
        Assert.All(unfinished, execution => { Assert.Equal("executing", execution.State); Assert.Null(execution.ResultJson); });
        Assert.Equal("previous-attempt", Assert.Single(await reopened.ListIncompleteExecutionsAsync(run.Id, "server-previous")).ProposalId);
        Assert.Equal("other-session", Assert.Single(await reopened.ListIncompleteExecutionsAsync(other.Id, "server-current")).ProposalId);
        Assert.Equal(99, (await environment.Get<IGoAiRunRepository>().GetAsync(run.Id))!.LastEventId);
        Assert.Equal("Already visible narration.", (await environment.Get<IChatRepository>().GetMessageAsync(run.AssistantMessageId))!.Content);
    }

    [Fact]
    public async Task UnknownOutcomeCanBeSubmittedOnceWithoutExecutingAgainAndDeletionPreservesOtherSessionClaims()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var run = await CreateRunAsync(environment, "server-recovery");
        var other = await CreateRunAsync(environment, "server-other");
        var journal = environment.Get<IClientToolExecutionRepository>();
        await journal.BeginAsync(Claim("interrupted-command", run.Id, "server-recovery", 8));
        await journal.BeginAsync(Claim("retained-command", other.Id, "server-other", 9));
        var interrupted = Assert.Single(await journal.ListIncompleteExecutionsAsync(run.Id, "server-recovery"));
        var unknown = new ClientToolResult(interrupted.ProposalId, "failed",
            JsonSerializer.SerializeToElement(new { outcomeUnknown = true }), "client.tool_outcome_unknown", "Nicht erneut ausgeführt.");

        await journal.CompleteAsync(interrupted.ProposalId, JsonSerializer.Serialize(unknown, GoAiProtocol.CreateJsonOptions()));

        Assert.Empty(await journal.ListIncompleteExecutionsAsync(run.Id, "server-recovery"));
        var pending = Assert.Single(await journal.ListPendingSubmissionsAsync(run.Id, "server-recovery"));
        var receipt = JsonSerializer.Deserialize<ClientToolResult>(pending.ResultJson!, GoAiProtocol.CreateJsonOptions())!;
        Assert.True(receipt.Result.GetProperty("outcomeUnknown").GetBoolean());
        Assert.Equal("client.tool_outcome_unknown", receipt.ErrorCode);
        await journal.MarkSubmittedAsync(interrupted.ProposalId);
        Assert.Empty(await journal.ListPendingSubmissionsAsync(run.Id, "server-recovery"));
        Assert.Empty(await journal.ListIncompleteExecutionsAsync(run.Id, "server-recovery"));
        await environment.Get<IChatRepository>().DeleteSessionAsync(run.SessionId);
        Assert.Null(await journal.GetAsync(interrupted.ProposalId));
        Assert.Equal("retained-command", Assert.Single(await journal.ListIncompleteExecutionsAsync(other.Id, "server-other")).ProposalId);
    }

    [Fact]
    public async Task IncompleteLookupRequiresAnExplicitValidRunScope()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var journal = environment.Get<IClientToolExecutionRepository>();
        await Assert.ThrowsAsync<ArgumentException>(() => journal.ListIncompleteExecutionsAsync(Guid.Empty, "server"));
        await Assert.ThrowsAsync<ArgumentException>(() => journal.ListIncompleteExecutionsAsync(Guid.NewGuid(), ""));
        await Assert.ThrowsAsync<ArgumentException>(() => journal.ListIncompleteExecutionsAsync(Guid.NewGuid(), "server\nother"));
    }

    private static ClientToolExecutionRecord Claim(string id, Guid run, string serverRun, long eventId) => new(
        id, run, serverRun, eventId, ClientToolNames.CodingCommand, "executing", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static string Receipt(string id) => JsonSerializer.Serialize(new ClientToolResult(id, "completed",
        JsonSerializer.SerializeToElement(new { success = true })), GoAiProtocol.CreateJsonOptions());

    private static async Task<GoAiRunRecord> CreateRunAsync(TestEnvironment environment, string serverRun)
    {
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Tool recovery fixture");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Already visible narration.", MessageStatus.Streaming);
        return await environment.Get<IGoAiRunRepository>().CreateAsync(new(Guid.NewGuid(), session.Id, message.Id,
            PromptTriggerAction.Coding, "idem-" + Guid.NewGuid().ToString("N"), serverRun, 99, "waitingForClient", null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
    }
}
