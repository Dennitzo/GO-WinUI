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

        var status = await _modelRuntime.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!status.ProviderReachable)
        {
            throw new HttpRequestException("LM Studio model status is unavailable.");
        }
        var model = status.Models.FirstOrDefault(candidate =>
            candidate.Downloaded
            && string.Equals(candidate.Id, selected.ModelId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.Role, selected.Role, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"The selected {selected.Role} model '{selected.ModelId}' is not available.");
        return selected with { ContextLength = Math.Max(2_048, model.ContextTokens) };
    }

    public ModelSelection Select(RunRequest request) => SelectGeneral(request);

    private ModelSelection SelectGeneral(RunRequest request)
    {
        var modelId = string.IsNullOrWhiteSpace(request.PreferredGeneralModelId)
            ? _options.GeneralModelId
            : request.PreferredGeneralModelId.Trim();
        return new ModelSelection(modelId, "general", _options.GeneralContextLength);
    }

}

public sealed record ModelSelection(string ModelId, string Role, int ContextLength);
