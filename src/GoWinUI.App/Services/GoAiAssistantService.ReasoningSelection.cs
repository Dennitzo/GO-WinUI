using GoAi.Client;
using GoAi.Contracts;
using GoWinUI.Core.Models;

namespace GoWinUI.App.Services;

public sealed record ComposerReasoningOptions(string ModelId, string Role, string? Selected,
    IReadOnlyList<string> Levels, string? DefaultLevel, bool Available, string? Detail = null);

public sealed partial class GoAiAssistantService
{
    internal static string ReasoningKey(string modelId, string role) => role.ToLowerInvariant() + ":" + modelId.ToLowerInvariant();

    private static string? ExplicitReasoningLevel(string? value)
    {
        var level = value?.Trim().ToLowerInvariant();
        return level is { Length: > 0 and <= 32 } && level != "auto" && char.IsAsciiLetterLower(level[0])
            && level.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '_' or '-')
            ? level : null;
    }

    internal static string? StoredReasoning(AppSettings current, string modelId, string role) =>
        current.ReasoningEffortsByModel.TryGetValue(ReasoningKey(modelId, role), out var value) ? ExplicitReasoningLevel(value) : null;

    internal static ComposerReasoningOptions ResolveComposerReasoning(AppSettings current, string modelId,
        string role, ModelStatusSnapshot snapshot)
    {
        var model = snapshot.Models.FirstOrDefault(item => item.Id == modelId && item.Role == role);
        var levels = snapshot.ProviderReachable
            ? (model?.ReasoningEfforts ?? []).Select(ExplicitReasoningLevel).OfType<string>().Distinct(StringComparer.Ordinal).ToArray()
            : [];
        var stored = StoredReasoning(current, modelId, role);
        var modelDefault = ExplicitReasoningLevel(model?.DefaultReasoningEffort);
        var selected = stored is not null && levels.Contains(stored) ? stored
            : modelDefault is not null && levels.Contains(modelDefault) ? modelDefault : levels.FirstOrDefault();
        return new(modelId, role, selected, levels,
            modelDefault is not null && levels.Contains(modelDefault) ? modelDefault : null, selected is not null,
            !snapshot.ProviderReachable ? "Modellinformationen momentan nicht erreichbar."
                : levels.Length == 0 ? "Das Modell bietet keine wählbaren Reasoning-Stufen." : null);
    }

    public async Task<ComposerReasoningOptions> GetReasoningOptionsAsync(string modelId, string role, CancellationToken cancellationToken)
    {
        using var client = await connection.CreateClientAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await client.GetModelStatusAsync(cancellationToken).ConfigureAwait(false);
        return ResolveComposerReasoning(settings.Current, modelId, role, snapshot);
    }

    internal async Task<string?> ResolveRequestedReasoningAsync(GoAiClient client, string modelId, string role, CancellationToken cancellationToken)
    {
        var status = await client.GetModelStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!status.ProviderReachable) throw new InvalidOperationException(status.ErrorMessage ?? "Reasoning-Kompatibilität des Modells ist nicht erreichbar.");
        return ResolveComposerReasoning(settings.Current, modelId, role, status).Selected;
    }
}
