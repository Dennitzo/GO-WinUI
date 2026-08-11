using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;

namespace GoWinUI.Tests;

public sealed class ChatAndContextTests
{
    [Fact]
    public async Task ChatStatePersistsAndStreamingMessagesRecoverAsInterrupted()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Neue Sitzung");
        await chats.SaveDraftAsync(session.Id, "Entwurf");
        _ = await chats.AddMessageAsync(session.Id, ChatRole.User, "Hallo", MessageStatus.Completed);
        _ = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Teil", MessageStatus.Streaming);

        Assert.Equal(1, await chats.MarkStreamingMessagesInterruptedAsync());
        var restored = await chats.GetSessionAsync(session.Id);
        var messages = await chats.ListMessagesAsync(session.Id);
        Assert.Single(await chats.ListSessionsAsync("Neue"));
        Assert.Equal("Entwurf", restored?.Draft);
        Assert.Collection(messages,
            message => Assert.Equal(MessageStatus.Completed, message.Status),
            message => Assert.Equal(MessageStatus.Interrupted, message.Status));
    }

    [Fact]
    public void ContextAssemblerHonorsExplicitPageRangeAndDropsOldHistoryAtBudget()
    {
        var assembler = environmentAssembler();
        var session = Guid.NewGuid();
        var history = Enumerable.Range(1, 20).Select(index => new ChatMessage(
            Guid.NewGuid(), session, index % 2 == 0 ? ChatRole.Assistant : ChatRole.User,
            new string('x', 800), MessageStatus.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)).ToArray();
        var pages = new[] { new DocumentPage(Guid.NewGuid(), 1, "EINS"), new DocumentPage(Guid.NewGuid(), 2, "ZWEI"), new DocumentPage(Guid.NewGuid(), 3, "DREI") };

        var result = assembler.Build(new("Du bist hilfreich.", "Bitte Seite 2 auswerten", history, null, pages, 2_048));
        Assert.True(result.WasTruncated);
        Assert.Contains("ZWEI", result.Messages[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("EINS", result.Messages[0].Content, StringComparison.Ordinal);
        Assert.Equal(ChatRole.User, result.Messages[^1].Role);

        static IContextAssembler environmentAssembler() => new GoWinUI.Core.Chat.ContextAssembler();
    }
}
