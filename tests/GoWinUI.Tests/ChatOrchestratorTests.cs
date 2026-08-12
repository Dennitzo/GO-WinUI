using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using GoWinUI.Infrastructure.AI;
using GoWinUI.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace GoWinUI.Tests;

public sealed class ChatOrchestratorTests
{
    [Fact]
    public async Task StreamIsPublishedPersistedAndRecordedAsRun()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var session = await environment.Get<IChatRepository>().CreateSessionAsync("Stream");
        var fakeLmStudio = new FakeLmStudio();
        using var orchestrator = new ChatOrchestrator(
            environment.Get<IChatRepository>(), environment.Get<IDocumentIngestor>(),
            fakeLmStudio, environment.Get<IContextAssembler>(), environment.Get<SqliteDatabase>());
        var updates = new List<ChatStreamUpdate>();
        orchestrator.StreamUpdated += (_, update) => updates.Add(update);

        var answer = await orchestrator.SendAsync(session.Id, "Frage", "test-model", "Hilf mir.");

        Assert.Equal("Hallo Welt", answer.Content);
        Assert.DoesNotContain(updates, static update => update.Delta.Contains('{', StringComparison.Ordinal));
        Assert.Contains(updates, static update => update.Status == MessageStatus.Streaming && update.Content.Length == 0);
        Assert.Contains(updates, static update => update.Status == MessageStatus.Completed && update.Content == "Hallo Welt");
        Assert.True(fakeLmStudio.LastRequest?.RequireJsonObject);
        var messages = await environment.Get<IChatRepository>().ListMessagesAsync(session.Id);
        Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant }, messages.Select(static message => message.Role));
        Assert.Equal(MessageStatus.Completed, messages[1].Status);
        Assert.Equal("Heizlast Bestand prüfen", (await environment.Get<IChatRepository>().GetSessionAsync(session.Id))?.Title);
        await using var connection = new SqliteConnection($"Data Source={environment.Get<IGoDatabase>().DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM chat_runs WHERE assistant_message_id=$id;";
        command.Parameters.AddWithValue("$id", answer.Id.ToString("D"));
        Assert.Equal("completed", await command.ExecuteScalarAsync());
    }

    private sealed class FakeLmStudio : ILmStudioClient
    {
        internal LmChatRequest? LastRequest { get; private set; }

        public Task<IReadOnlyList<LmModel>> ListModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LmModel>>([new("test-model", ContextLength: 8_192)]);
        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public async IAsyncEnumerable<LmDelta> StreamAsync(LmChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new("{\"schema\":\"barebone.agent.response.v2\",\"type\":\"message\",\"message\":\"Hallo ");
            yield return new("Welt\",\"sessionTitle\":\"Heizlast Bestand prüfen\"}");
            yield return new(string.Empty, IsCompleted: true);
        }
    }
}
