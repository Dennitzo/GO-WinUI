using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Models;
using Microsoft.Extensions.Options;

namespace GoAi.Server.Core.Runs;

public sealed class ModelRouter
{
    private readonly GoAiServerOptions _options;
    private readonly ModelRuntimeClient? _modelRuntime;

    public ModelRouter(IOptions<GoAiServerOptions> options, ModelRuntimeClient? modelRuntime = null)
    {
        _options = options.Value;
        _modelRuntime = modelRuntime;
    }

    public async Task<ModelSelection> SelectAsync(
        RunRequest request,
        CancellationToken cancellationToken = default)
    {
        var selected = Select(request);
        if (_modelRuntime is null)
        {
            return selected;
        }

        var status = request.Mode == RunMode.Coding
            ? await _modelRuntime.GetCodingStatusAsync(cancellationToken).ConfigureAwait(false)
            : await _modelRuntime.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!status.ProviderReachable)
        {
            throw new HttpRequestException(request.Mode == RunMode.Coding
                ? "Der native llama Coding-Dienst ist nicht erreichbar."
                : "native llama model status is unavailable.");
        }
        if (request.Mode == RunMode.Coding && string.IsNullOrWhiteSpace(selected.ModelId))
        {
            selected = selected with { ModelId = status.Models.FirstOrDefault(static model => model.Downloaded && model.Role == "coding")?.Id
                ?? throw new InvalidOperationException("Kein lokales GGUF-Coding-Modell verfügbar.") };
        }
        var model = ModelRuntimeClient.ResolveModelStatus(status.Models, selected.ModelId, selected.Role)
            ?? throw new InvalidOperationException(
                $"The selected {selected.Role} model '{selected.ModelId}' is not available.");
        return selected with { ModelId = model.Id, ContextLength = Math.Max(2_048, model.ContextTokens) };
    }

    public ModelSelection Select(RunRequest request) => request.Mode == RunMode.Coding
        ? new ModelSelection(
            string.IsNullOrWhiteSpace(request.PreferredCodingModelId) ? _options.CodingModelId : request.PreferredCodingModelId.Trim(),
            "coding",
            _options.CodingContextLength)
        : SelectGeneral(request);

    private ModelSelection SelectGeneral(RunRequest request)
    {
        var modelId = string.IsNullOrWhiteSpace(request.PreferredGeneralModelId)
            ? _options.GeneralModelId
            : request.PreferredGeneralModelId.Trim();
        return new ModelSelection(modelId, "general", _options.GeneralContextLength);
    }

}

public sealed record ModelSelection(string ModelId, string Role, int ContextLength);
