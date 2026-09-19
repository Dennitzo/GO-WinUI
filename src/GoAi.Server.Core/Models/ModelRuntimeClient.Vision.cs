using GoAi.Contracts;

namespace GoAi.Server.Core.Models;

public sealed partial class ModelRuntimeClient
{
    internal async Task<string?> ResolveIntegratedVisionAsync(string? selectedModel, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(selectedModel)) return null;
        var status = await GetStatusAsync(token).ConfigureAwait(false);
        if (!status.ProviderReachable)
            throw new HttpRequestException("Die Vision-Fähigkeit des ausgewählten Modells konnte nicht geprüft werden. Kein anderes Modell wurde ausgewählt.");
        return SelectIntegratedVision(status.Models, selectedModel);
    }

    // Exact instance identity matters: changing coding/ to vision/ would unload
    // the text model and lose the benefit of its already loaded projector.
    internal static string? SelectIntegratedVision(IReadOnlyList<ModelRuntimeStatus> models, string selectedModel)
    {
        var exact = models.FirstOrDefault(model => model.Downloaded && model.SupportsVision
            && model.Id.Equals(selectedModel, StringComparison.OrdinalIgnoreCase));
        if (exact is null || exact.Loaded) return exact?.Id;
        // A direct media button supplies the saved base ID, whereas a running
        // parallel main agent uses its already resident ~main instance.
        return models.FirstOrDefault(model => model.Downloaded && model.Loaded && model.SupportsVision
            && model.Id.Equals(selectedModel + "~main", StringComparison.OrdinalIgnoreCase))?.Id ?? exact.Id;
    }
}
