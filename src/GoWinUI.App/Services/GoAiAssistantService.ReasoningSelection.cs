using GoAi.Client;
using GoWinUI.Core.Models;

namespace GoWinUI.App.Services;

public sealed record ComposerReasoningOptions(string ModelId, string Role, string Selected,
    IReadOnlyList<string> Levels, string? DefaultLevel, bool Available, string? Detail = null);

public sealed partial class GoAiAssistantService
{
    internal static string ReasoningKey(string modelId, string role) => role.ToLowerInvariant() + ":" + modelId.ToLowerInvariant();

    internal static string? StoredReasoning(AppSettings current, string modelId, string role) =>
        current.ReasoningEffortsByModel.TryGetValue(ReasoningKey(modelId, role), out var value) && value != "auto" ? value : null;

    public async Task<ComposerReasoningOptions> GetReasoningOptionsAsync(string modelId, string role, CancellationToken cancellationToken)
    {
        using var client = await connection.CreateClientAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await client.GetModelStatusAsync(cancellationToken).ConfigureAwait(false);
        var model = snapshot.Models.FirstOrDefault(item => item.Id == modelId && item.Role == role);
        var levels = snapshot.ProviderReachable ? model?.ReasoningEfforts ?? [] : [];
        var stored = StoredReasoning(settings.Current, modelId, role);
        return new(modelId, role, stored is not null && levels.Contains(stored) ? stored : "auto", levels,
            model?.DefaultReasoningEffort, snapshot.ProviderReachable && model is not null,
            !snapshot.ProviderReachable ? "Modellinformationen momentan nicht erreichbar."
                : levels.Count == 0 ? "Das Modell liefert keine verlässlich erkannten steuerbaren Stufen. Automatisch nutzt seine Vorgaben." : null);
    }

    private async Task<string?> ResolveRequestedReasoningAsync(GoAiClient client, string modelId, string role, CancellationToken cancellationToken)
    {
        var requested = StoredReasoning(settings.Current, modelId, role);
        if (requested is null) return null;
        var status = await client.GetModelStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!status.ProviderReachable) throw new InvalidOperationException(status.ErrorMessage ?? "Reasoning-Kompatibilität des Modells ist nicht erreichbar.");
        var model = status.Models.FirstOrDefault(item => item.Id == modelId && item.Role == role);
        return model?.ReasoningEfforts?.Contains(requested) == true ? requested : null;
    }
}
