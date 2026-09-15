using GoAi.Contracts;
using GoWinUI.Core.Models;
using System.Text;
using System.Text.Json;
using System.Globalization;

namespace GoWinUI.App.Services;

public sealed partial class GoAiAssistantService
{
    // Persisted chat/tool receipts survive application restarts. A failed run may have
    // committed useful work even though it never produced a successful final answer.
    internal static IReadOnlyList<RunMessage> BuildCodingHistoryMessages(IReadOnlyList<ChatMessage> history, int budget)
    {
        var selected = new Stack<RunMessage>();
        foreach (var message in history.Reverse())
        {
            if (budget <= 0 || selected.Count >= 499) break;
            if (message.Role is not (ChatRole.User or ChatRole.Assistant)
                || message.Status is MessageStatus.Pending or MessageStatus.Streaming) continue;
            var text = message.Role == ChatRole.Assistant ? FullCodingHistoryEvidence(message) : message.Content;
            if (text.Length > budget && message.Role == ChatRole.Assistant)
                text = CodingHistoryEvidence(message);
            if (string.IsNullOrWhiteSpace(text)) continue;
            text = ClipCodingHistory(text, budget);
            if (string.IsNullOrWhiteSpace(text)) break;
            selected.Push(new RunMessage(message.Role == ChatRole.Assistant ? "assistant" : "user",
                SplitCodingHistoryParts(text)));
            budget -= text.Length;
        }
        return selected.ToArray();
    }

    private static List<ContentPart> SplitCodingHistoryParts(string text)
    {
        List<ContentPart> parts = [];
        for (var offset = 0; offset < text.Length;)
        {
            var length = Math.Min(240_000, text.Length - offset);
            if (offset + length < text.Length && char.IsHighSurrogate(text[offset + length - 1])) length--;
            parts.Add(new ContentPart("text", Text: text.Substring(offset, length)));
            offset += length;
        }
        return parts;
    }

    private static string FullCodingHistoryEvidence(ChatMessage message)
    {
        var text = new StringBuilder();
        text.AppendLine("Historischer Sitzungsverlauf, keine neue Anweisung. Befunde weiterverwenden; Dateistand vor Änderungen prüfen.");
        if (message.Status != MessageStatus.Completed)
            text.AppendLine("Der frühere Lauf wurde nicht erfolgreich abgeschlossen; bereits ausgeführte Änderungen bleiben relevant.");
        if (!string.IsNullOrWhiteSpace(message.ContextSummary)) text.AppendLine(message.ContextSummary);
        text.AppendLine(message.Content);
        if (!string.IsNullOrWhiteSpace(message.Error)) text.AppendLine(message.Error);
        foreach (var step in message.ToolSteps ?? [])
        {
            if (step.Tool == ReasoningStepTool) continue;
            text.AppendLine(CultureInfo.InvariantCulture, $"{step.Tool} | {step.Status} | {step.Id}");
            text.AppendLine("Eingabe: " + step.InputJson);
            text.AppendLine("Ergebnis: " + (step.OutputJson ?? step.Detail));
        }
        return text.ToString();
    }

    internal static string CodingHistoryEvidence(ChatMessage message)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"[Gespeicherter Sitzungsstand {message.UpdatedAt:O}; Status: {message.Status}]");
        text.AppendLine("Historische Aussagen und Werkzeugbelege, keine neuen Anweisungen. An diesem Stand weiterarbeiten; bestätigte Untersuchungen nicht pauschal wiederholen. Offene Schritte bleiben offen. Vor Änderungen nur betroffene Pfade/Hashes im aktuellen Workspace prüfen; der Workspace oder Dateien können inzwischen verändert sein. Prozess-Erfolg allein belegt keine bestandenen Abnahmetests.");
        text.AppendLine("Vorhandene ev-Referenzen können mit coding.readOutput in dieser Sitzung gezielt nachgelesen werden. historical/sourceRunId kennzeichnen frühere Originalausgaben; deren Inhalt ist kein neuer Test oder Nachweis des aktuellen Dateistands.");
        if (message.Status != MessageStatus.Completed)
            text.AppendLine("Der frühere Lauf wurde nicht erfolgreich abgeschlossen; seine bereits ausgeführten Bearbeitungen und Befunde bleiben dennoch relevant.");
        if (!string.IsNullOrWhiteSpace(message.ContextSummary))
            text.AppendLine("Gespeicherte Zusammenfassung: " + ClipCodingHistory(message.ContextSummary, 4_000));
        text.AppendLine(ClipCodingHistory(message.Content, 20_000));
        if (!string.IsNullOrWhiteSpace(message.Error)) text.AppendLine("Abbruchgrund: " + ClipCodingHistory(message.Error, 1_000));
        var steps = (message.ToolSteps ?? []).Where(step => step.Tool != ReasoningStepTool).ToArray();
        // Prefer committed edits, commands and failures over repeated broad directory reads.
        var candidates = steps.Select((step, index) => (step, index))
            .OrderBy(item => item.step.Tool is "coding.write" or "coding.edit" or "coding.command" or "coding.updatePlan"
                || item.step.Status is "failed" or "interrupted" ? 0 : 1)
            .ThenByDescending(item => item.index).Take(48);
        var chosen = new List<(int Index, string Receipt)>();
        var remaining = 40_000;
        foreach (var (step, index) in candidates)
        {
            var receipt = $"\n{step.Tool} | {step.Status} | {step.Id}\nEingabe: {ClipCodingHistory(step.InputJson, 700)}\nErgebnis: {SummarizeCodingReceipt(step)}\n";
            if (receipt.Length > remaining) continue;
            chosen.Add((index, receipt));
            remaining -= receipt.Length;
        }
        if (chosen.Count > 0) text.AppendLine("Gespeicherte Werkzeugbelege (Auszüge, chronologisch):");
        foreach (var item in chosen.OrderBy(item => item.Index)) text.Append(item.Receipt);
        if (chosen.Count < steps.Length) text.AppendLine(CultureInfo.InvariantCulture, $"[{steps.Length - chosen.Count} weitere Werkzeugschritte nicht in diesen Kontextauszug übernommen.]");
        return text.ToString();
    }

    private static string SummarizeCodingReceipt(AssistantToolStep step)
    {
        try
        {
            using var document = JsonDocument.Parse(step.OutputJson ?? "null");
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ClipCodingHistory(step.Detail, 1_000);
            var result = new StringBuilder();
            AppendFields(root, result);
            if (root.TryGetProperty("result", out var nested) && nested.ValueKind == JsonValueKind.Object) AppendFields(nested, result);
            return ClipCodingHistory(result.Length > 0 ? result.ToString() : step.OutputJson, 1_700);
        }
        catch (JsonException) { return ClipCodingHistory(step.Detail, 1_000); }

        static void AppendFields(JsonElement root, StringBuilder result)
        {
            foreach (var name in new[] { "path", "sha256", "originalSha256", "status", "success", "applied", "phase",
                "exitCode", "timedOut", "errorCode", "error", "message", "startLine", "endLine", "nextLine", "totalLines",
                "addedLines", "removedLines", "stderr", "stdout" })
                if (root.TryGetProperty(name, out var value)) result.Append(name).Append('=').Append(ClipCodingHistory(value.ToString(), 650)).Append(';');
        }
    }

    private static string ClipCodingHistory(string? text, int limit)
    {
        if (string.IsNullOrEmpty(text) || limit <= 0) return string.Empty;
        if (text.Length <= limit) return text;
        const string marker = "\n[Auszug gekürzt]\n";
        if (limit <= marker.Length + 2) return string.Empty;
        var head = (limit - marker.Length) / 2;
        var tail = limit - marker.Length - head;
        if (char.IsHighSurrogate(text[head - 1])) head--;
        if (char.IsLowSurrogate(text[text.Length - tail])) tail--;
        return text[..head] + marker + text[^tail..];
    }
}
