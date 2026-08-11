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
        using var orchestrator = new ChatOrchestrator(
            environment.Get<IChatRepository>(), environment.Get<IWorkflowRepository>(), environment.Get<IDocumentIngestor>(),
            new FakeLmStudio(), environment.Get<IContextAssembler>(), environment.Get<SqliteDatabase>());
        var updates = new List<ChatStreamUpdate>();
        orchestrator.StreamUpdated += (_, update) => updates.Add(update);

        var answer = await orchestrator.SendAsync(session.Id, "Frage", "test-model", "Hilf mir.");

        Assert.Equal("Hallo Welt", answer.Content);
        Assert.Contains(updates, static update => update.Delta == "Hallo ");
        var messages = await environment.Get<IChatRepository>().ListMessagesAsync(session.Id);
        Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant }, messages.Select(static message => message.Role));
        Assert.Equal(MessageStatus.Completed, messages[1].Status);
        await using var connection = new SqliteConnection($"Data Source={environment.Get<IGoDatabase>().DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM chat_runs WHERE assistant_message_id=$id;";
        command.Parameters.AddWithValue("$id", answer.Id.ToString("D"));
        Assert.Equal("completed", await command.ExecuteScalarAsync());
    }

    private sealed class FakeLmStudio : ILmStudioClient
    {
        public Task<IReadOnlyList<LmModel>> ListModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LmModel>>([new("test-model", ContextLength: 8_192)]);
        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public async IAsyncEnumerable<LmDelta> StreamAsync(LmChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new("Hallo ");
            yield return new("Welt");
            yield return new(string.Empty, IsCompleted: true);
        }
    }
}
