using System.Text.Json;
using GoAi.Contracts;
using GoWinUI.App.Services;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;

namespace GoWinUI.Tests;

public sealed class CodingSessionToolTests
{
    [Fact]
    public async Task HistorySearchCannotReadAnotherSessionOrTheCurrentPromptAndKeepsChronology()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var a = await chats.CreateSessionAsync("A");
        var b = await chats.CreateSessionAsync("B");
        var first = await chats.AddMessageAsync(a.Id, ChatRole.User, "Architektur: MARKER zuerst", MessageStatus.Completed);
        var second = await chats.AddMessageAsync(a.Id, ChatRole.Assistant, "MARKER später", MessageStatus.Completed);
        await chats.AddMessageAsync(b.Id, ChatRole.User, "MARKER fremde Sitzung", MessageStatus.Completed);
        var turn = await chats.AddTurnAsync(a.Id, "Suche MARKER");
        var result = JsonSerializer.SerializeToElement(await CodingSessionTools.SearchHistoryAsync(chats, a.Id, turn.AssistantMessage.Id,
            "MARKER", 8, CancellationToken.None), JsonSerializerOptions.Web);
        var matches = result.GetProperty("matches").EnumerateArray().ToArray();
        Assert.Equal(2, matches.Length);
        Assert.Equal(first.Id, matches[0].GetProperty("messageId").GetGuid());
        Assert.Equal(second.Id, matches[1].GetProperty("messageId").GetGuid());
        Assert.DoesNotContain("fremde", result.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("Suche MARKER", result.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("coding.searchHistory")]
    [InlineData("coding.searchKnowledge")]
    public void ContextToolCannotChooseAForeignSessionOrExceedResultLimit(string tool)
    {
        var valid = Proposal(tool, new { query = "x", maximumResults = 8 });
        LocalToolBroker.ValidateProposal(valid);
        Assert.Throws<InvalidDataException>(() => LocalToolBroker.ValidateProposal(Proposal(tool, new { query = "x", sessionId = Guid.NewGuid() })));
        Assert.Throws<InvalidDataException>(() => LocalToolBroker.ValidateProposal(Proposal(tool, new { query = "x", maximumResults = 9 })));
    }

    [Fact]
    public async Task HtmlPreviewPersistsExactlyAndHasAReceiptWithoutEchoingCodeToTheModel()
    {
        const string html = "<!doctype html><button onclick=\"this.textContent='✓'\">Test</button>";
        var proposal = Proposal("coding.renderHtml", new { code = html, title = "Test" });
        LocalToolBroker.ValidateProposal(proposal);
        var receipt = JsonSerializer.SerializeToElement(CodingSessionTools.RenderReceipt(proposal.Arguments), JsonSerializerOptions.Web);
        Assert.False(receipt.GetProperty("networkAccess").GetBoolean());
        Assert.False(receipt.TryGetProperty("code", out _));
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Preview");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Vorschau", MessageStatus.Completed);
        await chats.SaveToolStepAsync(message.Id, new("html", "coding.renderHtml", "completed", "Vorschau bereit", GoAiAssistantService.GetToolPreviewHtml(proposal)));
        var saved = await environment.Get<IConversationSnapshotRepository>().GetAsync(session.Id);
        Assert.Equal(html, Assert.Single(Assert.Single(saved!.Messages).ToolSteps!).PreviewHtml);
        Assert.Null(GoAiAssistantService.GetToolPreviewHtml(Proposal("coding.read", new { code = html })));
        Assert.Throws<InvalidDataException>(() => LocalToolBroker.ValidateProposal(Proposal("coding.renderHtml", new { code = new string('x', 16_001) })));
    }

    [Fact]
    public void KnowledgeResultsKeepSourceCoordinatesAndBoundExcerpts()
    {
        var id = Guid.NewGuid();
        var raw = JsonSerializer.SerializeToElement(new { searchMode = "hybrid", evidence = Enumerable.Range(1, 10).Select(page => new
        {
            documentId = id, fileName = "Spec.pdf", pageNumber = page, score = 0.5, text = new string('x', 3_000), citation = $"[Spec.pdf, S. {page}]",
        }) });
        var bounded = JsonSerializer.SerializeToElement(CodingSessionTools.BoundKnowledgeResult(raw, "Spec", 2), JsonSerializerOptions.Web);
        var evidence = bounded.GetProperty("evidence").EnumerateArray().ToArray();
        Assert.Equal(2, evidence.Length);
        Assert.Equal(id, evidence[0].GetProperty("documentId").GetGuid());
        Assert.Equal(1, evidence[0].GetProperty("pageNumber").GetInt32());
        Assert.True(evidence[0].GetProperty("text").GetString()!.Length < 850);
    }

    [Theory]
    [InlineData("Präzisionskapazität", "query-match")]
    [InlineData("Wie hoch ist die Präzisionskapazität?", "query-term")]
    public void KnowledgeExcerptKeepsTheActualMatchDeepInsideTheRetrievedChunk(string query, string kind)
    {
        var id = Guid.NewGuid();
        var text = new string('x', 2_500) + "\nPräzisionskapazität: 73 Aufträge. Beleg: UNIQUE-DEEP-SOURCE.\n" + new string('y', 900);
        var raw = JsonSerializer.SerializeToElement(new
        {
            searchMode = "fulltext", evidence = new[] { new
            {
                documentId = id, fileName = "Spec.txt", pageNumber = 3, score = 5.0, text, citation = "[Spec.txt, S. 3]",
            } },
        });
        var bounded = JsonSerializer.SerializeToElement(CodingSessionTools.BoundKnowledgeResult(raw, query, 2), JsonSerializerOptions.Web);
        var hit = Assert.Single(bounded.GetProperty("evidence").EnumerateArray());
        Assert.Equal(id, hit.GetProperty("documentId").GetGuid());
        Assert.Equal(3, hit.GetProperty("pageNumber").GetInt32());
        Assert.Equal("[Spec.txt, S. 3]", hit.GetProperty("citation").GetString());
        Assert.Contains("Präzisionskapazität: 73 Aufträge. Beleg: UNIQUE-DEEP-SOURCE.", hit.GetProperty("text").GetString()!, StringComparison.Ordinal);
        Assert.Equal(kind, hit.GetProperty("excerptKind").GetString());
        Assert.True(hit.GetProperty("excerptStart").GetInt32() > 2_000);
        Assert.True(hit.GetProperty("excerptTruncated").GetBoolean());
        Assert.True(hit.GetProperty("text").GetString()!.Length < 850);
    }

    [Fact]
    public void SemanticOnlyKnowledgeHitLabelsItsBoundedPrefixWithoutInventingALexicalMatch()
    {
        var text = "Actual source prefix: " + new string('x', 2_000);
        var raw = JsonSerializer.SerializeToElement(new
        {
            searchMode = "hybrid", evidence = new[] { new
            {
                documentId = Guid.NewGuid(), fileName = "Spec.txt", pageNumber = 1, score = 0.5, text, citation = "[Spec.txt, S. 1]",
            } },
        });
        var bounded = JsonSerializer.SerializeToElement(CodingSessionTools.BoundKnowledgeResult(raw, "semantic synonym", 1), JsonSerializerOptions.Web);
        var hit = Assert.Single(bounded.GetProperty("evidence").EnumerateArray());
        Assert.Equal("prefix-no-lexical-match", hit.GetProperty("excerptKind").GetString());
        Assert.Equal(0, hit.GetProperty("excerptStart").GetInt32());
        Assert.True(hit.GetProperty("excerptTruncated").GetBoolean());
        Assert.StartsWith("Actual source prefix:", hit.GetProperty("text").GetString()!, StringComparison.Ordinal);
        Assert.DoesNotContain("semantic synonym", hit.GetProperty("text").GetString()!, StringComparison.Ordinal);
    }

    private static ToolProposal Proposal(string name, object arguments) => new("p", "r", name,
        JsonSerializer.SerializeToElement(arguments), ToolRiskClass.ReadOnly, "Prüfung", DateTimeOffset.UtcNow.AddMinutes(1));
}
