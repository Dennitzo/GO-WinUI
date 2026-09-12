using System.Text.Json;
using System.Text.RegularExpressions;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;

namespace GoWinUI.App.Services;

/// <summary>Read-only context tools. Session and branch boundaries are supplied by GO, never by the model.</summary>
internal static partial class CodingSessionTools
{
    internal static async Task<object> SearchHistoryAsync(IChatRepository chats, Guid sessionId, Guid? assistantMessageId,
        string query, int maximumResults, CancellationToken cancellationToken)
    {
        var messages = await chats.ListMessagesAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var currentIndex = assistantMessageId is null ? -1 : messages.ToList().FindIndex(message => message.Id == assistantMessageId);
        // Exclude the current turn's prompt as well as the streaming answer: neither is recalled history.
        var history = currentIndex > 0 ? messages.Take(currentIndex - 1) : messages;
        var matches = history.Where(message => message.Status == MessageStatus.Completed
                && message.Content.Contains(query, StringComparison.OrdinalIgnoreCase))
            .TakeLast(Math.Clamp(maximumResults, 1, 8))
            .Select(message => new
            {
                messageId = message.Id, role = message.Role.ToString().ToLowerInvariant(), message.CreatedAt,
                text = MatchWindow(message.Content, query), citation = $"Nachricht {message.Id:D}",
            }).ToArray();
        return new { scope = "current-session", isUntrusted = true, query, matches };
    }

    internal static object BoundKnowledgeResult(JsonElement result, string query, int maximumResults)
    {
        var evidence = result.GetProperty("evidence").EnumerateArray().Take(Math.Clamp(maximumResults, 1, 8))
            .Select(hit =>
            {
                var excerpt = KnowledgeExcerpt(hit.GetProperty("text").GetString() ?? "", query, 800);
                return new
                {
                    documentId = hit.GetProperty("documentId").GetString(),
                    fileName = Bound(hit.GetProperty("fileName").GetString() ?? "", 160),
                    pageNumber = hit.GetProperty("pageNumber").GetInt32(),
                    text = excerpt.Text, excerptStart = excerpt.Start, excerptTruncated = excerpt.Truncated,
                    excerptKind = excerpt.Kind,
                    score = hit.GetProperty("score"), citation = Bound(hit.GetProperty("citation").GetString() ?? "", 220),
                };
            }).ToArray();
        return new { scope = "current-session-documents", isUntrusted = true, query,
            searchMode = result.GetProperty("searchMode").GetString(), evidence };
    }

    internal static object RenderReceipt(JsonElement arguments) => new
    {
        success = true,
        title = arguments.TryGetProperty("title", out var title) ? title.GetString() : "HTML-Vorschau",
        previewAvailable = true, networkAccess = false,
        message = "Die HTML-Vorschau ist beim Werkzeugschritt verfügbar. Sie besitzt keine Netzwerk-, Datei- oder GO-Bridge-Rechte.",
    };

    private static string MatchWindow(string content, string query)
    {
        var index = content.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        var start = Math.Max(0, index - 200);
        var end = Math.Min(content.Length, start + 650);
        return (start > 0 ? "… " : "") + content[start..end] + (end < content.Length ? " …" : "");
    }

    private static (string Text, int Start, bool Truncated, string Kind) KnowledgeExcerpt(string content, string query, int maximum)
    {
        var needle = query.Trim();
        var index = needle.Length > 0 ? content.IndexOf(needle, StringComparison.OrdinalIgnoreCase) : -1;
        var kind = "query-match";
        if (index < 0)
        {
            // Fulltext/hybrid retrieval accepts natural-language queries. Prefer the longest
            // matching query term when the complete phrase does not occur in the source chunk.
            foreach (var term in QueryTermsRegex().Matches(query)
                .Select(match => match.Value).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(term => term.Length))
            {
                index = content.IndexOf(term, StringComparison.OrdinalIgnoreCase);
                if (index < 0) continue;
                needle = term;
                kind = "query-term";
                break;
            }
        }
        // A semantic-only hit has no lexical coordinate; label its bounded prefix honestly.
        if (index < 0) { index = 0; kind = "prefix-no-lexical-match"; }
        var leadingContext = Math.Min(200, Math.Max(0, (maximum - needle.Length) / 2));
        var start = Math.Max(0, index - leadingContext);
        var end = Math.Min(content.Length, start + maximum);
        var truncated = start > 0 || end < content.Length;
        return ((start > 0 ? "… " : "") + content[start..end] + (end < content.Length ? " …" : ""), start, truncated, kind);
    }

    private static string Bound(string value, int maximum) => value.Length <= maximum ? value : value[..maximum] + " …";

    [GeneratedRegex(@"[\p{L}\p{N}_-]{2,}", RegexOptions.CultureInvariant, 100)]
    private static partial Regex QueryTermsRegex();
}
