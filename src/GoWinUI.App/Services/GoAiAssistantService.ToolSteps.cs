using GoAi.Contracts;
using GoWinUI.Core.Chat;
using GoWinUI.Core.Coding;
using GoWinUI.Core.Models;
using System.Text.Json;

namespace GoWinUI.App.Services;

public sealed partial class GoAiAssistantService
{
    private static readonly string[] GitProgressOnlyProperties =
        ["stdout", "stderr", "elapsedMilliseconds", "truncated", "phase", "partial", "diffTruncated"];

    internal static DateTimeOffset NextToolStepUpdate(AssistantToolStep? previous)
    {
        var now = DateTimeOffset.UtcNow;
        return previous?.UpdatedAt is { } last && now <= last.AddMilliseconds(1) ? last.AddMilliseconds(1) : now;
    }

    internal static string NormalizeCodingNarration(string content) =>
        ChatContentSanitizer.Sanitize(content);

    internal static int RebaseToolContentOffset(string source, int offset, string visibleContent)
    {
        var prefix = NormalizeCodingNarration(source[..Math.Clamp(offset, 0, source.Length)]);
        if (visibleContent.StartsWith(prefix, StringComparison.Ordinal)) return prefix.Length;
        // Removing a metadata line can also trim leading/trailing whitespace.
        var trimmed = prefix.Trim();
        if (visibleContent.StartsWith(trimmed, StringComparison.Ordinal)) return trimmed.Length;
        var common = 0;
        while (common < prefix.Length && common < visibleContent.Length && prefix[common] == visibleContent[common]) common++;
        return common;
    }

    internal static string SerializeToolProgress(CodingCommandProgress progress) => progress.OutputJson
        ?? JsonSerializer.Serialize(new
        {
            stdout = progress.Stdout, stderr = progress.Stderr, elapsedMilliseconds = progress.ElapsedMilliseconds,
            truncated = progress.Truncated, phase = "running", partial = true,
        }, JsonOptions);

    internal static string SerializeClientToolOutput(ClientToolResult result, string? previousOutputJson = null,
        CodingCommandProgress? finalProgress = null)
    {
        // The callback captures progress before attempting persistence. Its applied diff
        // remains authoritative even when that intermediate database write failed.
        var merged = ReadToolOutputObject(finalProgress is null ? previousOutputJson : SerializeToolProgress(finalProgress));
        var priorDiff = merged.GetValueOrDefault("diff");
        var priorPhase = merged.GetValueOrDefault("phase");
        var priorTruncated = merged.GetValueOrDefault("diffTruncated");
        if (result.Result.ValueKind == JsonValueKind.Object
            && result.Result.TryGetProperty("status", out var gitStatus) && gitStatus.ValueKind == JsonValueKind.Object
            && result.Result.TryGetProperty("diff", out var gitDiff) && gitDiff.ValueKind == JsonValueKind.Object)
        {
            // A finished Git receipt contains all process outputs in status/diff/stagedDiff.
            // Flat fields belonged to one earlier subprocess and can include a recovered HEAD error.
            foreach (var propertyName in GitProgressOnlyProperties) merged.Remove(propertyName);
        }
        if (result.Result.ValueKind == JsonValueKind.Object)
            foreach (var property in result.Result.EnumerateObject()) merged[property.Name] = property.Value.Clone();
        else if (result.Result.ValueKind != JsonValueKind.Undefined) merged["result"] = result.Result.Clone();
        var clippedDiff = result.Result.ValueKind == JsonValueKind.Object
            && result.Result.TryGetProperty("diffTruncated", out var clipped) && clipped.ValueKind == JsonValueKind.True;
        if (clippedDiff && priorDiff.ValueKind == JsonValueKind.String
            && priorPhase.ValueKind == JsonValueKind.String && priorPhase.GetString() is "applied" or "git-diff")
        {
            merged["diff"] = priorDiff;
            merged["diffTruncated"] = priorTruncated.ValueKind == JsonValueKind.Undefined ? JsonSerializer.SerializeToElement(false) : priorTruncated;
        }
        merged["toolStatus"] = JsonSerializer.SerializeToElement(GetToolResultStatus(result));
        if (result.ErrorCode is not null) merged["errorCode"] = JsonSerializer.SerializeToElement(result.ErrorCode);
        if (result.Message is not null) merged["message"] = JsonSerializer.SerializeToElement(result.Message);
        if (merged.TryGetValue("exitCode", out _) || GetToolResultStatus(result) == "completed")
            merged["partial"] = JsonSerializer.SerializeToElement(false);
        else if (merged.TryGetValue("phase", out var phase) && phase.ValueKind == JsonValueKind.String && phase.GetString() == "running")
            merged["phase"] = JsonSerializer.SerializeToElement("interrupted");
        return JsonSerializer.Serialize(merged, JsonOptions);
    }

    private static string? CompleteToolOutput(AssistantToolStep step, string status, CodingCommandProgress? progress)
    {
        if (step.Tool == ReasoningStepTool) return step.OutputJson;
        var json = progress is null ? step.OutputJson : SerializeToolProgress(progress);
        if (json is null) return null;
        var values = ReadToolOutputObject(json);
        values["toolStatus"] = JsonSerializer.SerializeToElement(status);
        if (!values.ContainsKey("exitCode") && !values.ContainsKey("applied"))
        {
            values["partial"] = JsonSerializer.SerializeToElement(true);
            values["phase"] = JsonSerializer.SerializeToElement("interrupted");
        }
        return JsonSerializer.Serialize(values, JsonOptions);
    }

    private static string FormatStoredToolInput(AssistantToolStep step)
    {
        using var document = JsonDocument.Parse(step.InputJson!);
        return FormatToolInputDetail(new(step.Id, "stored", step.Tool, document.RootElement,
            ToolRiskClass.ReadOnly, step.Explanation ?? "", DateTimeOffset.MaxValue));
    }

    private static string FormatProgressOutputDetail(CodingCommandProgress progress)
    {
        if (progress.OutputJson is null) return FormatCommandProgressDetail(progress);
        using var document = JsonDocument.Parse(progress.OutputJson);
        return FormatToolResultDetail(document.RootElement);
    }

    private static Dictionary<string, JsonElement> ReadToolOutputObject(string? json)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (json is null) return values;
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Object)
            foreach (var property in document.RootElement.EnumerateObject()) values[property.Name] = property.Value.Clone();
        return values;
    }

    internal static string DescribeServerTool(string tool, string? target) => tool switch
    {
        "web.search" => "Ich suche passende Quellen" + (target is null ? "." : ": " + target),
        "web.fetch" => "Ich lese die Quelle" + (target is null ? "." : ": " + target),
        "web.deepResearch" => "Ich recherchiere mehrere Quellen und prüfe ihre Belege.",
        _ => "Ich führe " + tool + (target is null ? " aus." : " für „" + target + "“ aus."),
    };
}
