using GoWinUI.Core.Chat;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using System.Text.Json;

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
            new string('x', 1_600), MessageStatus.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)).ToArray();
        var pages = new[] { new DocumentPage(Guid.NewGuid(), 1, "EINS"), new DocumentPage(Guid.NewGuid(), 2, "ZWEI"), new DocumentPage(Guid.NewGuid(), 3, "DREI") };

        var result = assembler.Build(new("Du bist hilfreich.", "Bitte Seite 2 auswerten", history, null, pages, 8_192));
        Assert.True(result.WasTruncated);
        Assert.Contains("ZWEI", result.Messages[^1].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("EINS", result.Messages[^1].Content, StringComparison.Ordinal);
        Assert.Equal(ChatRole.User, result.Messages[^1].Role);
        Assert.Contains("Markdown-Pipe-Tabellen", result.Messages[0].Content, StringComparison.Ordinal);
        Assert.Contains("Dokument-Policy", result.Messages[0].Content, StringComparison.Ordinal);
        Assert.Equal(1_024, result.MaxOutputTokens);

        using var envelope = JsonDocument.Parse(Assert.IsType<string>(result.RequestEnvelopeJson));
        var root = envelope.RootElement;
        Assert.Equal("barebone.general.markdown.request.v1", root.GetProperty("schema").GetString());
        Assert.Equal("document_qa", root.GetProperty("route").GetProperty("route").GetString());
        Assert.False(root.GetProperty("modePolicy").GetProperty("cadToolsAllowed").GetBoolean());
        Assert.Contains(root.GetProperty("policyRefs").EnumerateArray(), static value => value.GetString() == "documents");
        Assert.Contains("ZWEI", root.GetProperty("documentContext").GetProperty("selectedText").GetString(), StringComparison.Ordinal);

        static IContextAssembler environmentAssembler() => new GoWinUI.Core.Chat.ContextAssembler();
    }

    [Fact]
    public void GeneralChatEnvelopeUsesTgaPoliciesAndStructuredResponseContract()
    {
        var result = new GoWinUI.Core.Chat.ContextAssembler().Build(new(
            "GO Anwendungshinweis.",
            "Erstelle eine Vergleichstabelle.",
            Array.Empty<ChatMessage>(),
            null,
            Array.Empty<DocumentPage>(),
            131_072));

        using var envelope = JsonDocument.Parse(Assert.IsType<string>(result.RequestEnvelopeJson));
        var root = envelope.RootElement;
        Assert.Equal("general_chat", root.GetProperty("route").GetProperty("route").GetString());
        Assert.Equal("barebone-agent-json-message-with-session-title", root.GetProperty("expectedResponse").GetString());
        Assert.Equal(
            GeneralAgentResponseParser.ResponseSchema,
            root.GetProperty("responseContract").GetProperty("schema").GetString());
        Assert.True(root.GetProperty("responseContract").GetProperty("sessionTitle").GetProperty("refreshOnEveryRun").GetBoolean());
        Assert.Equal(6, root.GetProperty("responseContract").GetProperty("sessionTitle").GetProperty("maximumWords").GetInt32());
        Assert.True(root.GetProperty("responseContract").GetProperty("contextSummary").GetProperty("mustNotContainMarkdown").GetBoolean());
        Assert.Equal("TGA-Fachplanung", root.GetProperty("domainProfile").GetProperty("name").GetString());
        Assert.True(root.GetProperty("domainProfile").GetProperty("applicationNameIsNotProgrammingLanguage").GetBoolean());
        Assert.Collection(
            root.GetProperty("policyRefs").EnumerateArray(),
            static value => Assert.Equal("general", value.GetString()));
        Assert.Equal(8_192, result.MaxOutputTokens);
        Assert.Contains("|---|---|", result.Messages[0].Content, StringComparison.Ordinal);
        Assert.Contains("\\[...\\]", result.Messages[0].Content, StringComparison.Ordinal);
        Assert.Contains("Technischen Gebäudeausrüstung", result.Messages[0].Content, StringComparison.Ordinal);
        Assert.Contains("Programmiersprache Go", result.Messages[0].Content, StringComparison.Ordinal);
        Assert.Contains("Biete keine Go-Programmierung", result.Messages[0].Content, StringComparison.Ordinal);

        using var transmittedEnvelope = JsonDocument.Parse(result.Messages[^1].Content);
        Assert.Equal("Erstelle eine Vergleichstabelle.", transmittedEnvelope.RootElement.GetProperty("originalUserPrompt").GetString());
        Assert.Equal(
            "barebone-agent-json-message-with-session-title",
            transmittedEnvelope.RootElement.GetProperty("expectedResponse").GetString());
    }

    [Fact]
    public void StructuredAgentResponseSeparatesVisibleMarkdownAndSpecificSessionTitle()
    {
        const string raw = """
            ```json
            {"schema":"barebone.agent.response.v2","type":"message","message":"## Heizlast\n\nDie Berechnung ist vorbereitet.","sessionTitle":"Heizlast Bestand fachlich prüfen"}
            ```
            """;

        var response = GeneralAgentResponseParser.Parse(raw, "Hallo");

        Assert.True(response.IsStructured);
        Assert.Equal("## Heizlast\n\nDie Berechnung ist vorbereitet.", response.Message);
        Assert.Equal("Heizlast Bestand fachlich prüfen", response.SessionTitle);
        Assert.Equal("Heizlast Die Berechnung ist vorbereitet.", response.ContextSummary);
    }

    [Fact]
    public void LegacySessionTitleMarkerIsRemovedFromTheVisibleAnswer()
    {
        const string raw = "GO_SESSION_TITLE: Projektstart BricsCAD CATS\n\n## Projektstart\n\nRäume werden in BricsCAD vorbereitet.";

        var response = GeneralAgentResponseParser.Parse(raw, "Projekt starten");

        Assert.DoesNotContain("GO_SESSION_TITLE", response.Message, StringComparison.Ordinal);
        Assert.Equal("Projektstart BricsCAD CATS", response.SessionTitle);
        Assert.Equal("Projektstart Räume werden in BricsCAD vorbereitet.", response.ContextSummary);
    }

    [Fact]
    public void MarkdownFormattedEmptySessionTitleMarkerIsNeverRendered()
    {
        const string raw = "**GO_SESSION_TITLE:\u00A0**  \n\nDie unabhängige Prüfung wurde abgeschlossen.";

        var response = GeneralAgentResponseParser.Parse(raw, "Prüfung fortsetzen");

        Assert.Equal("Die unabhängige Prüfung wurde abgeschlossen.", response.Message);
        Assert.DoesNotContain("GO_SESSION_TITLE", response.ContextSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void EscapedMarkdownSessionTitleMarkerIsNeverRendered()
    {
        const string raw = "**GO\\_SESSION\\_TITLE:** Technischer Titel\n\n### Prozessbericht\nDie CodeÃ¤nderung wurde geprÃ¼ft.";

        var response = GeneralAgentResponseParser.Parse(raw, "Workflow fortsetzen");

        Assert.DoesNotContain("GO\\_SESSION", response.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("GO_SESSION_TITLE", response.Message, StringComparison.Ordinal);
        Assert.StartsWith("### Prozessbericht", response.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("## **GO_SESSION_TITLE:** Technischer Titel\n\nSichtbarer Text")]
    [InlineData("**Aktion:** **GO\\_SESSION\\_TITLE:** Arbeitsstand\n\nCode wurde geändert.")]
    [InlineData("Vorwort GO_SESSION_TITLE:\u00A0Zwischentitel\n\nErgebnis")]
    [InlineData("**Annahmen:** GO_SESSION_TITLE: A **GO_SESSION_TITLE:** B")]
    public void CentralChatBoundaryRemovesEveryLegacyTitleMarkerVariant(string content)
    {
        var sanitized = ChatContentSanitizer.Sanitize(content);

        Assert.False(ChatContentSanitizer.ContainsReservedMarker(sanitized));
        Assert.NotEmpty(sanitized);
    }

    [Fact]
    public void ProductionCSharpNeverEmitsTheReservedLegacyTitleToken()
    {
        var repositoryRoot = FindRepositoryRoot();
        var offenders = Directory
            .EnumerateFiles(Path.Combine(repositoryRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("GO_SESSION_TITLE", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(repositoryRoot, path))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void WorkflowMetadataConvertsMarkdownToShortPlainText()
    {
        const string markdown = "**Projektstart mit BricsCAD & C.A.T.S. – Raum-Erstellung**\n\n---\n\n## 1 Projektvorbereitung in BricsCAD\n\n| Schritt | Aktion | Hinweis |";

        Assert.Equal("Projektstart mit BricsCAD & C.A.T.S. – Raum-Erstellung", GeneralAgentResponseParser.CreateWorkflowTitle(markdown));
        var summary = GeneralAgentResponseParser.CreateContextSummary(null, markdown);
        Assert.DoesNotContain("**", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("|", summary, StringComparison.Ordinal);
        Assert.True(summary.Length <= 320);
    }

    [Fact]
    public void GenericGreetingNeverBecomesTheSessionTitleFallback()
    {
        var response = GeneralAgentResponseParser.Parse(
            "Hallo! Wobei kann ich dich in der TGA-Planung unterstützen?",
            "Hallo");

        Assert.False(response.IsStructured);
        Assert.Equal("Einstieg in die TGA-Fachplanung", response.SessionTitle);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GO.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Das GO-Repository wurde aus dem Testausgabeverzeichnis nicht gefunden.");
    }
}
