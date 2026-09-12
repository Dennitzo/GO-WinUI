using GoAi.Contracts;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text.Json;

namespace GoAi.Server.Core.Models;

public sealed partial class ModelRuntimeClient
{
    private static readonly Action<ILogger, string, int, int, int, string, Exception?> LogModelRequestBudget =
        LoggerMessage.Define<string, int, int, int, string>(LogLevel.Information,
            new EventId(4104, "ModelRequestBudget"),
            "Native model request {ModelId}: input_tokens={InputTokens}, context_tokens={ContextTokens}, max_tokens={MaximumOutputTokens}, reasoning_effort={ReasoningEffort}.");

    private async Task<Dictionary<string, object?>> ApplyTokenBudgetAsync(
        IReadOnlyDictionary<string, object?> body,
        int contextLength,
        int? maximumOutputTokens,
        Func<ModelRuntimeProgress, CancellationToken, ValueTask>? progress,
        CancellationToken cancellationToken)
    {
        if (progress is not null)
            await progress(new ModelRuntimeProgress("tokenCounting"), cancellationToken).ConfigureAwait(false);
        // Native llama applies the identical chat template, tools, reasoning
        // kwargs and multimodal preprocessing without running model generation.
        using var response = await SendJsonAsync(HttpMethod.Post,
            "v1/chat/completions/input_tokens", body, cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("input_tokens", out var count)
            || !count.TryGetInt32(out var inputTokens) || inputTokens < 1)
            throw new JsonException("Der native Tokenzähler lieferte keine gültige Promptlänge.");

        var outputTokens = ResolveMaximumOutputTokens(contextLength, inputTokens, maximumOutputTokens);
        var request = new Dictionary<string, object?>(body, StringComparer.Ordinal)
        {
            ["max_tokens"] = outputTokens,
        };
        var effort = body.TryGetValue("reasoning_effort", out var direct) ? direct?.ToString() : null;
        if (body.TryGetValue("chat_template_kwargs", out var kwargs)
            && kwargs is IReadOnlyDictionary<string, object?> template
            && template.TryGetValue("reasoning_effort", out var templateEffort))
            effort = templateEffort?.ToString();
        if (effort is not (null or "none" or "off"))
            request["reasoning_budget_tokens"] = outputTokens;
        LogModelRequestBudget(_logger, body["model"]?.ToString() ?? "", inputTokens,
            contextLength, outputTokens, effort ?? "none", null);
        return request;
    }

    internal static int ResolveMaximumOutputTokens(int contextLength, int inputTokens, int? requestedMaximum)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(contextLength, 2);
        ArgumentOutOfRangeException.ThrowIfNegative(inputTokens);
        if (requestedMaximum is { } explicitMaximum) ArgumentOutOfRangeException.ThrowIfLessThan(explicitMaximum, 1);
        // llama stops when prompt + generated + 1 reaches the slot capacity.
        // Reasoning and the visible answer share this single output allowance.
        var available = (long)contextLength - inputTokens - 1;
        if (available < 1)
            throw new InvalidOperationException($"Der exakt tokenisierte Prompt benötigt {inputTokens:N0} Token; im geladenen Kontext von {contextLength:N0} Token bleibt kein Platz für eine Antwort.");
        return (int)Math.Min(available, requestedMaximum ?? int.MaxValue);
    }

    internal static int ReadLoadedContextLength(JsonElement properties)
    {
        if (properties.TryGetProperty("default_generation_settings", out var settings)
            && settings.TryGetProperty("n_ctx", out var context)
            && context.TryGetInt32(out var length) && length >= 2_048)
            return length;
        throw new JsonException("Der native Modellprozess meldet keinen gültigen geladenen Kontext (props.n_ctx).");
    }

    private static int ReadNominalContextLength(JsonElement item, string modelId, int fallback)
    {
        const string tagPrefix = "go-context-train:";
        if (item.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
        {
            foreach (var tag in tags.EnumerateArray())
            {
                var value = tag.ValueKind == JsonValueKind.String ? tag.GetString() : null;
                if (value?.StartsWith(tagPrefix, StringComparison.Ordinal) == true
                    && int.TryParse(value.AsSpan(tagPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var maximum)
                    && maximum >= 2_048)
                    return maximum;
            }
        }
        if (item.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object
            && meta.TryGetProperty("n_ctx_train", out var context) && context.TryGetInt32(out var trainingMaximum)
            && trainingMaximum >= 2_048)
            return trainingMaximum;
        var familyMaximum = ModelContextProfiles.ResolveMaximum(modelId, null);
        return familyMaximum == ModelContextProfiles.NativeRuntimeDefault ? fallback : familyMaximum;
    }
}
