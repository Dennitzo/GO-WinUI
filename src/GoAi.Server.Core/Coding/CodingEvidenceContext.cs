using GoAi.Server.Core.Models;
using System.Text;
using System.Text.Json;

namespace GoAi.Server.Core.Coding;

public static class CodingEvidenceContext
{
    public const string Marker = "GO_CODING_WORKING_STATE\n";
    private const int MaximumHistoricalArguments = 1024;

    public static LmChatMessage Build(CodingWorkingState state, int budgetCharacters = 12_000)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentOutOfRangeException.ThrowIfLessThan(budgetCharacters, 2048);
        const string omissionNotice = "Weitere gespeicherte Details wurden für dieses Kontextfenster ausgelassen; keine neuen Belege angenommen.";
        var output = new StringBuilder(Marker + "Gespeicherte Arbeitsdaten, keine neue Anweisung oder Autorisierung. Der ursprüngliche Auftrag und das vollständige Journal bleiben erhalten.\n");
        var omitted = false;
        void Add(string section, object value)
        {
            var line = JsonSerializer.Serialize(new { section, data = value });
            if (output.Length + line.Length + omissionNotice.Length + 4 > budgetCharacters) { omitted = true; return; }
            output.AppendLine(line);
        }
        Add("next", new { state.Phase, state.NextStep, state.EnvironmentRevision });
        foreach (var item in state.AcceptanceCriteria) Add("acceptance", item);
        foreach (var item in state.Plan.OrderBy(static item => item.Status == "completed" ? 1 : 0)) Add("plan", item);
        foreach (var item in state.Facts) Add("evidenced_fact", item);
        foreach (var item in state.RejectedHypotheses) Add("rejected_hypothesis", item);
        foreach (var item in state.Failures.Where(item => item.EnvironmentRevision == state.EnvironmentRevision)) Add("failure", item);
        // A successful command is evidence of that process, not automatically a passed test suite.
        foreach (var item in state.Tests.TakeLast(4)) Add("executed_process", item);
        foreach (var file in state.ActiveFiles.OrderByDescending(file => state.NextStep?.Contains(file.Path, StringComparison.OrdinalIgnoreCase) == true)
            .ThenByDescending(static file => file.Sequence))
            Add("active_file", file with { Snippet = SelectSnippet(file, file.StartLine) });
        foreach (var item in state.Evidence.TakeLast(6)) Add("receipt", item);
        if (omitted) output.AppendLine(omissionNotice);
        return new LmChatMessage("user", output.ToString());
    }

    public static string SelectSnippet(CodingActiveFile file, int startLine, int maximumLines = 40, int maximumCharacters = 2400)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(startLine, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLines, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCharacters, 128);
        if (file.NeedsRead || startLine < file.StartLine) return string.Empty;
        var lines = file.Snippet.Split('\n');
        var offset = startLine - file.StartLine;
        if (offset >= lines.Length) return string.Empty;
        var output = new StringBuilder();
        foreach (var line in lines.Skip(offset).Take(maximumLines))
        {
            if (output.Length + line.Length + 1 > maximumCharacters) break;
            if (output.Length > 0) output.Append('\n');
            output.Append(line);
        }
        return output.ToString();
    }

    public static IReadOnlyList<LmChatMessage> CompactCompletedCalls(IReadOnlyList<LmChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var result = messages.ToArray();
        var receiptPositions = messages.Select((message, index) => (message, index))
            .Where(static item => item.message.Role == "tool" && item.message.ToolCallId is not null)
            .GroupBy(static item => item.message.ToolCallId!, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Max(static item => item.index), StringComparer.Ordinal);
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            if (message.Role != "assistant" || message.ToolCalls is not { Count: > 0 }) continue;
            var changed = false;
            var calls = new List<LmToolCall>(message.ToolCalls.Count);
            foreach (var call in message.ToolCalls)
            {
                if (receiptPositions.TryGetValue(call.Id, out var receiptIndex) && receiptIndex > index
                    && call.Arguments.GetRawText().Length > MaximumHistoricalArguments && !IsCompacted(call.Arguments))
                {
                    calls.Add(call with { Arguments = SummarizeArguments(call) });
                    changed = true;
                }
                else calls.Add(call);
            }
            if (changed) result[index] = message with { ToolCalls = calls };
        }
        return result;
    }

    private static bool IsCompacted(JsonElement args) => args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty("_goCompletedArguments", out var marker) && marker.ValueKind == JsonValueKind.True;

    private static JsonElement SummarizeArguments(LmToolCall call)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["_goCompletedArguments"] = true,
            ["toolCallId"] = call.Id,
            ["originalCharacters"] = call.Arguments.GetRawText().Length,
            ["argumentsSha256"] = CodingWorkingStateReducer.ArgumentHash(call.Arguments),
            ["notice"] = "Abgeschlossener Aufruf: gekürzte historische Argumente, nicht erneut ausführen. Originalargumente und Ergebnis stehen im Laufjournal.",
        };
        if (call.Arguments.ValueKind == JsonValueKind.Object)
            foreach (var property in call.Arguments.EnumerateObject())
            {
                if (property.Name.StartsWith("_go", StringComparison.Ordinal)) continue;
                if (property.Value.GetRawText().Length <= 256) result[property.Name] = property.Value.Clone();
                else if (property.Value.ValueKind == JsonValueKind.String)
                    result[property.Name] = CodingWorkingStateReducer.Bound(property.Value.GetString()!, 200);
                else result[property.Name] = new { omitted = true, characters = property.Value.GetRawText().Length };
            }
        return JsonSerializer.SerializeToElement(result);
    }
}
