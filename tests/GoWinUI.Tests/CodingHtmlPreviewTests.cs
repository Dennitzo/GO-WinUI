using GoWinUI.App.Pages;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;

namespace GoWinUI.Tests;

public sealed class CodingHtmlPreviewTests
{
    [Fact]
    public async Task PreviewResolvesOnlyPersistedCompletedHtmlFromTheActiveSession()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Preview");
        var other = await chats.CreateSessionAsync("Other");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Vorschau", MessageStatus.Completed);
        const string html = "<button onclick=\"this.textContent='OK'\">Interaktiv</button>";
        await chats.SaveToolStepAsync(message.Id, new("html #1", "coding.renderHtml", "completed", PreviewHtml: html));
        var uri = $"https://go-coding-preview.local/coding/{message.Id:D}/html%20%231";
        Assert.Equal(html, await AssistantPage.GetCodingPreviewHtmlAsync(chats, session.Id, uri, CancellationToken.None));
        Assert.Null(await AssistantPage.GetCodingPreviewHtmlAsync(chats, other.Id, uri, CancellationToken.None));
        Assert.Null(await AssistantPage.GetCodingPreviewHtmlAsync(chats, Guid.Empty, uri, CancellationToken.None));
        Assert.Null(await AssistantPage.GetCodingPreviewHtmlAsync(chats, session.Id, uri + "missing", CancellationToken.None));
        foreach (var status in new[] { "running", "denied", "cancelled", "failed" })
        {
            await chats.SaveToolStepAsync(message.Id, new(status, "coding.renderHtml", status, PreviewHtml: html));
            Assert.Null(await AssistantPage.GetCodingPreviewHtmlAsync(chats, session.Id,
                $"https://go-coding-preview.local/coding/{message.Id:D}/{status}", CancellationToken.None));
        }
        await chats.SaveToolStepAsync(message.Id, new("read", "coding.read", "completed", PreviewHtml: html));
        Assert.Null(await AssistantPage.GetCodingPreviewHtmlAsync(chats, session.Id,
            $"https://go-coding-preview.local/coding/{message.Id:D}/read", CancellationToken.None));
    }

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("https://go.local/index.html")]
    [InlineData("https://go-preview.local/image.png")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("data:text/html,hello")]
    [InlineData("https://go-coding-preview.local:444/coding/ca5c9cae-7cf1-4e89-934c-2c0ba23f6253/html")]
    [InlineData("https://go-coding-preview.local/coding/ca5c9cae-7cf1-4e89-934c-2c0ba23f6253/html?leak=secret")]
    [InlineData("https://go-coding-preview.local/coding/ca5c9cae-7cf1-4e89-934c-2c0ba23f6253/%2foutside")]
    [InlineData("https://go-coding-preview.local/coding/invalid/html")]
    public void FrameNavigationRejectsNetworkFilesAppAndMalformedRoutes(string uri)
    {
        Assert.False(AssistantPage.TryParseCodingPreviewUri(uri, out _, out _));
    }

    [Fact]
    public void PreviewResponseAllowsInlineInteractionWithoutOriginNetworkOrEmbeddingPrivileges()
    {
        var headers = AssistantPage.CodingPreviewResponseHeaders;
        Assert.Contains("script-src 'unsafe-inline';", headers, StringComparison.Ordinal);
        Assert.Contains("style-src 'unsafe-inline';", headers, StringComparison.Ordinal);
        Assert.Contains("default-src 'none';", headers, StringComparison.Ordinal);
        Assert.Contains("img-src data: blob:;", headers, StringComparison.Ordinal);
        Assert.Contains("connect-src 'none';", headers, StringComparison.Ordinal);
        Assert.Contains("form-action 'none'; frame-src 'none';", headers, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors https://go.local; sandbox allow-scripts", headers, StringComparison.Ordinal);
        Assert.DoesNotContain("allow-same-origin", headers, StringComparison.Ordinal);
        Assert.Contains("Cache-Control: no-store", headers, StringComparison.Ordinal);
    }
}
