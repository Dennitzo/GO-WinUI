using GoAi.Contracts;

namespace GoWinUI.App.Services;

/// <summary>
/// Holds the last model catalog committed by the gateway. LM Studio remains
/// authoritative; the registry only lets WebView and request creation use the
/// same capability snapshot without another network roundtrip.
/// </summary>
public sealed class ModelCapabilityRegistry
{
    private readonly object _sync = new();
    private Dictionary<(string Id, string Role), ModelCapability> _models = new();

    public void Update(CapabilitySnapshot snapshot) => Update(snapshot.Models);

    public void Update(ModelStatusSnapshot snapshot) => Update(snapshot.Models.Select(static model =>
        new ModelCapability(
            model.Id,
            model.Role,
            model.ContextTokens,
            model.SupportsTools,
            model.SupportsVision,
            false,
            model.ReasoningEfforts,
            model.DefaultReasoningEffort)));

    public ModelReasoningProfile ResolveReasoning(string? modelId, string role)
    {
        var id = modelId?.Trim() ?? string.Empty;
        lock (_sync)
        {
            if (_models.TryGetValue((id.ToLowerInvariant(), role.ToLowerInvariant()), out var model))
            {
                var efforts = model.ReasoningEfforts ?? [];
                return new ModelReasoningProfile("lmstudio-native", efforts, model.DefaultReasoningEffort);
            }
        }
        return ModelReasoningProfiles.Resolve(modelId, role);
    }

    public int ResolveContext(string? modelId, string role, int fallback)
    {
        var id = modelId?.Trim() ?? string.Empty;
        lock (_sync)
        {
            return _models.TryGetValue((id.ToLowerInvariant(), role.ToLowerInvariant()), out var model)
                ? model.ContextTokens
                : fallback;
        }
    }

    private void Update(IEnumerable<ModelCapability> models)
    {
        var updated = models
            .Where(static model => !string.IsNullOrWhiteSpace(model.Id) && !string.IsNullOrWhiteSpace(model.Role))
            .GroupBy(static model => (model.Id.ToLowerInvariant(), model.Role.ToLowerInvariant()))
            .ToDictionary(static group => group.Key, static group => group.Last());
        lock (_sync)
        {
            _models = updated;
        }
    }
}
