using System.Text;
using System.Text.Json;

namespace GoWinUI.App.Services;

internal sealed record CodingWorkflowJournalEntry(
    string Id,
    string Kind,
    string Title,
    string Content,
    DateTimeOffset CreatedAt);

internal static class CodingWorkflowMessageJournal
{
    private const int MaximumEntryLength = 750_000;
    private const int MaximumEntriesRead = 5_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim WriteGate = new(1, 1);

    public static string GetPath(string workspacePath) =>
        Path.Combine(Path.GetFullPath(workspacePath), ".go-workflow", "chat-messages.jsonl");

    public static async Task<CodingWorkflowJournalEntry> AppendAsync(
        string workspacePath,
        string kind,
        string title,
        string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        var normalizedKind = Normalize(kind, 80, "message");
        var normalizedTitle = Normalize(title, 240, "Coding-Workflow");
        var normalizedContent = (content ?? string.Empty).Trim();
        if (normalizedContent.Length > MaximumEntryLength)
        {
            normalizedContent = normalizedContent[..MaximumEntryLength]
                + "\n\n[Eintrag für die Chatdarstellung gekürzt.]";
        }
        var entry = new CodingWorkflowJournalEntry(
            Guid.NewGuid().ToString("N"),
            normalizedKind,
            normalizedTitle,
            normalizedContent,
            DateTimeOffset.UtcNow);
        var line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;
        var bytes = Encoding.UTF8.GetBytes(line);
        var path = GetPath(workspacePath);

        await WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var stream = new FileStream(
                path, FileMode.Append, FileAccess.Write, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            WriteGate.Release();
        }
        return entry;
    }

    public static async Task<IReadOnlyList<CodingWorkflowJournalEntry>> ReadAsync(
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        var path = GetPath(workspacePath);
        if (!File.Exists(path)) return [];
        var result = new List<CodingWorkflowJournalEntry>();
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (result.Count < MaximumEntriesRead)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                if (line.Length == 0 || line.Length > MaximumEntryLength + 2_048) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<CodingWorkflowJournalEntry>(line, JsonOptions);
                    if (entry is not null
                        && Guid.TryParseExact(entry.Id, "N", out _)
                        && !string.IsNullOrWhiteSpace(entry.Content))
                    {
                        result.Add(entry);
                    }
                }
                catch (JsonException)
                {
                    // A partially appended final line is ignored and can be retried after the next load.
                }
            }
        }
        catch (IOException)
        {
            return [];
        }
        return result;
    }

    private static string Normalize(string? value, int maximumLength, string fallback)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return fallback;
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }
}
