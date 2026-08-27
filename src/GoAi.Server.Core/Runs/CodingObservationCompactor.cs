using GoAi.Contracts;
using System.Text;
using System.Text.Json;

namespace GoAi.Server.Core.Runs;

/// <summary>
/// Deterministically reduces tool payloads while preserving the information
/// needed to choose the next action. Raw source and mutation payloads are sent
/// to the model at most once and are never persisted in the gateway ledger.
/// </summary>
public static class CodingObservationCompactor
{
    private const int MaximumDepth = 8;
    private const int MaximumObjectProperties = 96;
    private const int MaximumDefaultArrayItems = 24;
    private const int MaximumDiagnosticItems = 48;
    private const int MaximumShortStringCharacters = 2_048;
    private const int MaximumDiagnosticCharacters = 8_192;
    private const int MaximumProcessCharacters = 12_000;
    private const int MaximumModelSourceCharacters = 8_192;

    private static readonly HashSet<string> SourcePayloadNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "text", "content", "oldText", "newText", "patch", "body", "markdown", "html",
    };

    private static readonly HashSet<string> ProcessPayloadNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "standardOutput", "standardError", "stdout", "stderr", "output",
    };

    private static readonly HashSet<string> DiagnosticNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "diagnostics", "errors", "warnings", "suggestedPaths", "matches", "results", "files",
        "hits", "axioms", "evidenceIds", "changedPaths", "verificationIds",
    };

    public static JsonElement CompactResult(AgentActionEnvelope action, AgentObservation observation)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(observation);

        var state = new CompactionState();
        // Public web extracts are evidence, not private workspace source. Keep one
        // bounded extract in the persisted run observation so a following search
        // or tool turn cannot make the already prepared page disappear from the
        // model context. Local source and mutation payloads remain hash-only.
        var persistenceMode = string.Equals(action.Tool, CodingAgentToolFacade.ResearchQuery, StringComparison.Ordinal)
            && string.Equals(action.Operation, "webFetch", StringComparison.Ordinal)
                ? CompactionMode.ModelObservation
                : CompactionMode.Observation;
        var compacted = CompactValue(
            observation.Result,
            propertyName: null,
            depth: 0,
            mode: persistenceMode,
            state);
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["version"] = 1,
            ["tool"] = action.Tool,
            ["operation"] = action.Operation,
            ["originalHash"] = observation.ResultHash,
            ["originalCharacters"] = observation.Result.GetRawText().Length,
            ["truncated"] = state.Truncated,
        };

        if (compacted is Dictionary<string, object?> objectResult)
        {
            objectResult["_goCompaction"] = metadata;
            return JsonSerializer.SerializeToElement(objectResult, GoAiProtocol.CreateJsonOptions());
        }

        return JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["value"] = compacted,
                ["_goCompaction"] = metadata,
            },
            GoAiProtocol.CreateJsonOptions());
    }

    public static JsonElement CompactArguments(AgentActionEnvelope action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var state = new CompactionState();
        return JsonSerializer.SerializeToElement(
            CompactValue(
                action.Arguments,
                propertyName: null,
                depth: 0,
                mode: CompactionMode.ActionArguments,
                state),
            GoAiProtocol.CreateJsonOptions());
    }

    public static JsonElement CompactResultForModel(AgentObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var state = new CompactionState();
        return JsonSerializer.SerializeToElement(
            CompactValue(
                observation.Result,
                propertyName: null,
                depth: 0,
                mode: CompactionMode.ModelObservation,
                state),
            GoAiProtocol.CreateJsonOptions());
    }

    private static object? CompactValue(
        JsonElement element,
        string? propertyName,
        int depth,
        CompactionMode mode,
        CompactionState state)
    {
        if (depth >= MaximumDepth)
        {
            state.Truncated = true;
            return new Dictionary<string, object?>
            {
                ["omitted"] = true,
                ["reason"] = "maximum_depth",
                ["sha256"] = CodingAgentHash.Sha256(element.GetRawText()),
            };
        }

        return element.ValueKind switch
        {
            JsonValueKind.Object => CompactObject(element, depth, mode, state),
            JsonValueKind.Array => CompactArray(element, propertyName, depth, mode, state),
            JsonValueKind.String => CompactString(element.GetString() ?? string.Empty, propertyName, mode, state),
            JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static Dictionary<string, object?> CompactObject(
        JsonElement element,
        int depth,
        CompactionMode mode,
        CompactionState state)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        var properties = element.EnumerateObject().ToArray();
        foreach (var property in properties.Take(MaximumObjectProperties))
        {
            result[property.Name] = CompactValue(property.Value, property.Name, depth + 1, mode, state);
        }
        if (properties.Length > MaximumObjectProperties)
        {
            state.Truncated = true;
            result["_omittedPropertyCount"] = properties.Length - MaximumObjectProperties;
        }
        return result;
    }

    private static object?[] CompactArray(
        JsonElement element,
        string? propertyName,
        int depth,
        CompactionMode mode,
        CompactionState state)
    {
        var values = element.EnumerateArray().ToArray();
        var maximum = propertyName is not null && DiagnosticNames.Contains(propertyName)
            ? MaximumDiagnosticItems
            : MaximumDefaultArrayItems;
        if (values.Length > maximum)
        {
            state.Truncated = true;
        }
        return values
            .Take(maximum)
            .Select(value => CompactValue(value, propertyName, depth + 1, mode, state))
            .ToArray();
    }

    private static object CompactString(
        string value,
        string? propertyName,
        CompactionMode mode,
        CompactionState state)
    {
        if (propertyName is not null && SourcePayloadNames.Contains(propertyName))
        {
            if (mode is CompactionMode.Observation or CompactionMode.ActionArguments)
            {
                state.Truncated = true;
                return new Dictionary<string, object?>
                {
                    ["omitted"] = true,
                    ["characters"] = value.Length,
                    ["sha256"] = CodingAgentHash.Sha256(value),
                };
            }
            if (mode == CompactionMode.ModelObservation && value.Length > MaximumModelSourceCharacters)
            {
                state.Truncated = true;
                return PreserveHeadAndTail(value, MaximumModelSourceCharacters);
            }
        }

        var maximum = propertyName is not null && ProcessPayloadNames.Contains(propertyName)
            ? MaximumProcessCharacters
            : propertyName is not null && IsDiagnosticProperty(propertyName)
                ? MaximumDiagnosticCharacters
                : MaximumShortStringCharacters;
        if (value.Length <= maximum)
        {
            return value;
        }

        state.Truncated = true;
        return PreserveHeadAndTail(value, maximum);
    }

    private static bool IsDiagnosticProperty(string propertyName) =>
        propertyName.Contains("error", StringComparison.OrdinalIgnoreCase)
        || propertyName.Contains("message", StringComparison.OrdinalIgnoreCase)
        || propertyName.Contains("diagnostic", StringComparison.OrdinalIgnoreCase)
        || propertyName.Contains("warning", StringComparison.OrdinalIgnoreCase)
        || propertyName.Contains("snippet", StringComparison.OrdinalIgnoreCase)
        || propertyName.Contains("summary", StringComparison.OrdinalIgnoreCase);

    private static string PreserveHeadAndTail(string value, int maximum)
    {
        var marker = $"\n… {value.Length - maximum:N0} Zeichen deterministisch ausgelassen …\n";
        var available = Math.Max(64, maximum - marker.Length);
        var head = available / 3;
        var tail = available - head;
        var builder = new StringBuilder(maximum + marker.Length);
        builder.Append(value.AsSpan(0, head));
        builder.Append(marker);
        builder.Append(value.AsSpan(value.Length - tail, tail));
        return builder.ToString();
    }

    private sealed class CompactionState
    {
        public bool Truncated { get; set; }
    }

    private enum CompactionMode
    {
        Observation,
        ActionArguments,
        ModelObservation,
    }
}

internal static class CodingAgentHash
{
    public static string Sha256(string value) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)))
        .ToLowerInvariant();
}
