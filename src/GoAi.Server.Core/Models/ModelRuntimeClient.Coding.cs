using GoAi.Contracts;
using System.Text.Json;

namespace GoAi.Server.Core.Models;

public sealed partial class ModelRuntimeClient
{
    internal static bool IsCodingModel(string modelId) => modelId.StartsWith("coding/", StringComparison.Ordinal);

    public async Task<ModelStatusSnapshot> GetCodingStatusAsync(CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return status with { Models = status.Models.Where(static model => model.Role == "coding").ToArray() };
    }

    public async Task<CodingModelCatalogResponse> GetCodingModelsAsync(CancellationToken cancellationToken = default)
    {
        var status = await GetCodingStatusAsync(cancellationToken).ConfigureAwait(false);
        return new CodingModelCatalogResponse(status.Models, _options.CodingModelRoot, status.ProviderReachable,
            !status.ProviderReachable ? status.ErrorMessage ?? "Windows llama.cpp ist nicht erreichbar. Nativen GO-Llama-Prozess und Modellordner prüfen."
            : status.Models.Count == 0 ? "Keine vollständigen lokalen GGUF-Sprachmodelle im Unsloth-Modellordner gefunden."
            : null, status.CheckedAt);
    }

    internal static IReadOnlyList<ModelRuntimeStatus> ReadCodingModels(JsonElement root, int contextLength) =>
        BuildRuntimeStatuses(ReadRuntimeModels(root, contextLength)).Where(static model => model.Role == "coding").ToArray();
}
