using GoAi.Contracts;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runs;
using System.Text.Json;

namespace GoAi.Server.Core.Coding;

public static class CodingLoopGuard
{
    public const int MaximumToolResultCharacters = 32_000;
    private static readonly string[] ReceiptMetadataFields =
    [
        "status", "success", "errorCode", "exitCode", "timedOut", "outcomeUnknown", "applied",
        "sha256", "originalSha256", "evidenceId", "stream", "offset", "nextOffset", "hasMore",
        "storedCharacters", "storedBytes", "truncated", "tool", "provider", "isFallback", "found",
        "path", "nextLine", "startLine", "endLine", "totalLines",
    ];
    private static readonly string[] ReceiptNestedFields = ["result", "evidence"];
    private static readonly string[] BlenderMetadataFields =
    [
        // Keep the full-report escape hatch and exact scene identity ahead of
        // optional display details when a receipt exhausts its metadata budget.
        "reportPath", "sourceSha256", "sceneSha256", "baseSceneSha256", "scriptSha256",
        "outputPath", "blendPath", "baseScene", "scriptPath", "operation", "stageId", "label",
        "valid", "inspectionCompleted", "issueCount", "truncatedObjects", "available", "toolkitVersion",
        "projectPath", "helperPath", "designPath", "modelingGuidePath", "readmePath", "legacyScriptPath",
        "state", "revision", "processId", "message", "error", "bytes",
    ];
    private static readonly string[] BlenderNestedFields = ["preview", "execution", "file"];
    private static readonly string[] BlenderImageFields = ["path", "sha256", "view", "bytes", "width", "height"];
    private static readonly string[] BlenderIssueFields = ["severity", "code", "object", "message"];

    public static string BoundToolResult(string value, string? toolName = null)
    {
        // Fresh file reads must reach context planning intact, including JSON escaping.
        if (toolName == "coding.read") return value;
        var maximum = toolName == "coding.readOutput" ? 256_000 : MaximumToolResultCharacters;
        if (value.Length <= maximum) return value;
        var metadataBudget = toolName == WorkspaceTools.Blender ? 9000 : 6000;
        var metadataTruncated = false;
        Dictionary<string, object?> bounded = [];
        try
        {
            using var document = JsonDocument.Parse(value);
            bounded = CopyReceiptMetadata(document.RootElement, 0, ref metadataBudget, ref metadataTruncated,
                blender: toolName == WorkspaceTools.Blender);
        }
        catch (JsonException) { }
        bounded["truncated"] = true;
        bounded["originalCharacters"] = value.Length;
        bounded["message"] = toolName == WorkspaceTools.Blender
            ? "Die Blender-Ausgabe wurde begrenzt. Lies den vollständigen reportPath mit coding.read; imagesTruncated oder issuesTruncated kennzeichnen fehlende Bilder oder Befunde. Erhaltene Pfade und Hashes sind unverändert. Textvorschau und Ende sind unvollständig."
            : "Die Ausgabe wurde begrenzt. Nutze vorhandene Beleg-IDs mit coding.readOutput oder lies gezielt kleinere Ausschnitte. Vorschau und Ende sind unvollständiger Ausgabetext.";
        if (metadataTruncated) bounded["metadataTruncated"] = true;
        // Blender's real preview object is an execution receipt, not a text
        // snippet. Wrapped receipts retain result.preview in the same way.
        var previewField = bounded.ContainsKey("preview") ? "textPreview" : "preview";
        var previewCharacters = 1000;
        var tailCharacters = 400;
        while (true)
        {
            bounded[previewField] = value[..previewCharacters];
            bounded["tail"] = value[(value.Length - tailCharacters)..];
            var serialized = JsonSerializer.Serialize(bounded);
            if (serialized.Length <= MaximumToolResultCharacters) return serialized;
            previewCharacters /= 2;
            tailCharacters /= 2;
        }
    }

    private static Dictionary<string, object?> CopyReceiptMetadata(JsonElement source, int depth, ref int budget,
        ref bool truncated, bool blender = false)
    {
        Dictionary<string, object?> result = [];
        if (source.ValueKind != JsonValueKind.Object) return result;
        if (blender) CopyFields(source, result, BlenderMetadataFields, ref budget, ref truncated);
        CopyFields(source, result, ReceiptMetadataFields, ref budget, ref truncated);
        if (depth < 2)
        {
            foreach (var name in ReceiptNestedFields)
            {
                if (!source.TryGetProperty(name, out var nested) || nested.ValueKind != JsonValueKind.Object) continue;
                result[name] = CopyReceiptMetadata(nested, depth + 1, ref budget, ref truncated, blender);
            }
            if (blender)
            {
                foreach (var name in BlenderNestedFields)
                {
                    if (!source.TryGetProperty(name, out var nested) || nested.ValueKind != JsonValueKind.Object) continue;
                    result[name] = CopyReceiptMetadata(nested, depth + 1, ref budget, ref truncated, blender: true);
                }
                CopyBlenderImages(source, result, ref budget, ref truncated);
                CopyBlenderSection(source, result, "counts", 1500, ref budget, ref truncated);
                CopyBlenderSection(source, result, "bounds", 700, ref budget, ref truncated);
                CopyBlenderSection(source, result, "units", 512, ref budget, ref truncated);
                CopyBlenderIssues(source, result, ref budget, ref truncated);
            }
        }
        return result;
    }

    private static void CopyFields(JsonElement source, Dictionary<string, object?> result, string[] names,
        ref int budget, ref bool truncated)
    {
        foreach (var name in names)
        {
            if (!source.TryGetProperty(name, out var field) || field.ValueKind is not
                (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)) continue;
            var serialized = JsonSerializer.Serialize(field);
            var cost = serialized.Length + name.Length + 8;
            var pathField = name is "path" or "baseScene" || name.EndsWith("Path", StringComparison.Ordinal);
            if (serialized.Length > (pathField ? 2048 : 512) || cost > budget) { truncated = true; continue; }
            result[name] = field.Clone();
            budget -= cost;
        }
    }

    private static void CopyBlenderImages(JsonElement source, Dictionary<string, object?> result,
        ref int budget, ref bool truncated)
    {
        if (!source.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array) return;
        List<Dictionary<string, object?>> retained = [];
        foreach (var image in images.EnumerateArray().Take(6))
        {
            if (image.ValueKind != JsonValueKind.Object) { truncated = true; continue; }
            var imageBudget = budget;
            Dictionary<string, object?> metadata = [];
            CopyFields(image, metadata, BlenderImageFields, ref imageBudget, ref truncated);
            if (!metadata.ContainsKey("path") || !metadata.ContainsKey("sha256") || imageBudget < 8)
            {
                truncated = true;
                continue;
            }
            retained.Add(metadata);
            budget = imageBudget - 8;
        }
        result["images"] = retained;
        if (retained.Count < images.GetArrayLength())
        {
            result["imagesTruncated"] = true;
            truncated = true;
        }
    }

    private static void CopyBlenderSection(JsonElement source, Dictionary<string, object?> result,
        string name, int maximumCharacters, ref int budget, ref bool truncated)
    {
        if (!source.TryGetProperty(name, out var section)) return;
        if (section.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null)) { truncated = true; return; }
        var size = JsonSerializer.Serialize(section).Length;
        var cost = size + name.Length + 8;
        if (size > maximumCharacters || cost > budget) { truncated = true; return; }
        result[name] = section.Clone();
        budget -= cost;
    }

    private static void CopyBlenderIssues(JsonElement source, Dictionary<string, object?> result,
        ref int budget, ref bool truncated)
    {
        if (!source.TryGetProperty("issues", out var issues) || issues.ValueKind != JsonValueKind.Array) return;
        // A failed mesh near the end of a long warning list must remain actionable.
        // Keep the original code/object/message, rather than inventing a diagnosis.
        var issueBudget = Math.Min(budget, 3000);
        var initialBudget = issueBudget;
        var omitted = false;
        List<Dictionary<string, object?>> retained = [];
        foreach (var issue in issues.EnumerateArray().Where(static value => value.ValueKind == JsonValueKind.Object)
            .OrderBy(static value => IssuePriority(value)).Take(8))
        {
            var candidateBudget = issueBudget;
            Dictionary<string, object?> metadata = [];
            CopyFields(issue, metadata, BlenderIssueFields, ref candidateBudget, ref omitted);
            if (!metadata.ContainsKey("severity") || !metadata.ContainsKey("code") || candidateBudget < 8)
            {
                omitted = true;
                continue;
            }
            retained.Add(metadata);
            issueBudget = candidateBudget - 8;
        }
        budget -= initialBudget - issueBudget;
        result["issues"] = retained;
        if (omitted || retained.Count < issues.GetArrayLength()
            || source.TryGetProperty("issueCount", out var count) && count.ValueKind == JsonValueKind.Number
                && count.TryGetInt32(out var total) && total > retained.Count)
        {
            result["issuesTruncated"] = true;
            truncated = true;
        }
    }

    private static int IssuePriority(JsonElement issue) =>
        issue.TryGetProperty("severity", out var severity) && severity.ValueKind == JsonValueKind.String
            ? severity.GetString() switch { "error" => 0, "warning" => 1, _ => 2 } : 2;

    public static void ThrowIfRepeatedFailure(IReadOnlyList<LmChatMessage> messages, LmToolCall candidate, CodingWorkingState? workingState = null)
    {
        if (workingState is not null)
        {
            var argumentHash = CodingWorkingStateReducer.ArgumentHash(candidate.Arguments);
            if (workingState.Failures.Any(failure => failure.Tool == candidate.Name && failure.ArgumentHash == argumentHash
                && failure.EnvironmentRevision == workingState.EnvironmentRevision && failure.Count >= 2))
                throw new AgentRunLimitException($"'{candidate.Name}' wurde im unveränderten Arbeitszustand bereits zweimal mit denselben Argumenten erfolglos aufgerufen. Dieser Aufruf wurde nicht erneut ausgeführt. Prüfe den vorhandenen Fehlerbeleg mit coding.readOutput oder coding.searchRunEvidence, korrigiere Befehl, Pfad oder Umgebung und setze den Nutzerauftrag mit einem anderen Ansatz fort. Der Lauf bleibt aktiv.");
            return;
        }
        var matchingIds = messages.SelectMany(static message => message.ToolCalls ?? [])
            .Where(call => call.Name == candidate.Name && JsonElement.DeepEquals(call.Arguments, candidate.Arguments))
            .Select(static call => call.Id).ToHashSet(StringComparer.Ordinal);
        var failures = messages.Count(message => message.Role == "tool"
            && message.ToolCallId is not null && matchingIds.Contains(message.ToolCallId)
            && IsFailure(message.Content));
        if (failures >= 2)
            throw new AgentRunLimitException($"'{candidate.Name}' wurde bereits zweimal mit identischen Argumenten erfolglos aufgerufen. Dieser Aufruf wurde nicht erneut ausgeführt. Lies den vorhandenen Fehlerbeleg, korrigiere die Argumente oder wähle ein anderes Werkzeug und setze den Nutzerauftrag fort. Der Lauf bleibt aktiv.");
    }

    public static void ThrowIfRenderAlreadyUsed(IReadOnlyList<LmChatMessage> messages, LmToolCall candidate)
    {
        if (candidate.Name != ClientToolNames.CodingRenderHtml) return;
        var renderCalls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            foreach (var call in message.ToolCalls ?? [])
                if (call.Name == ClientToolNames.CodingRenderHtml) renderCalls.Add(call.Id);
            if (message.Role == "tool" && message.ToolCallId is { } completedId && renderCalls.Contains(completedId))
                throw new ArgumentException("coding.renderHtml darf höchstens einmal pro Lauf ausgeführt werden; die vorhandene Vorschau bleibt erhalten.");
        }
    }

    private static bool IsFailure(string? content)
    {
        if (string.IsNullOrEmpty(content)) return false;
        try
        {
            using var document = JsonDocument.Parse(content);
            return IsFailure(document.RootElement);
        }
        catch (JsonException) { return false; }
    }

    private static bool IsFailure(JsonElement value) => CodingWorkingStateReducer.IsFailedResult(value);
}
