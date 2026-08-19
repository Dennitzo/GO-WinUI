using System.Text.Json;
using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using Microsoft.Extensions.Options;

namespace GoAi.Server.Core.Models;

public sealed class GeneralModelSelectionService : IDisposable
{
    private readonly GoAiServerOptions _options;
    private readonly LmStudioClient _lmStudio;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string SelectionPath => Path.Combine(_options.DataDirectory, "Config", "general-model.json");

    public GeneralModelSelectionService(IOptions<GoAiServerOptions> options, LmStudioClient lmStudio)
    {
        _options = options.Value;
        _lmStudio = lmStudio;
    }

    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(SelectionPath))
            return;
        try
        {
            var json = await File.ReadAllTextAsync(SelectionPath, cancellationToken).ConfigureAwait(false);
            var saved = JsonSerializer.Deserialize<GeneralModelSelection>(json);
            if (!string.IsNullOrWhiteSpace(saved?.ModelId))
            {
                _options.GeneralModelId = saved.ModelId.Trim();
                if (saved.ContextTokens >= 2_048)
                    _options.GeneralContextLength = saved.ContextTokens;
            }
        }
        catch (JsonException)
        {
            // Invalid legacy state is ignored; the configured default remains usable.
        }
    }

    public async Task<GeneralModelSelection> SelectAsync(string modelId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = await _lmStudio.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            var model = status.Models.FirstOrDefault(candidate =>
                candidate.Downloaded
                && candidate.Role == "general"
                && string.Equals(candidate.Id, modelId.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Das LM-Studio-Textmodell '{modelId}' ist nicht verfügbar.");
            _options.GeneralModelId = model.Id;
            _options.GeneralContextLength = Math.Max(2_048, model.ContextTokens);
            await _lmStudio.UnloadModelsExceptAsync([model.Id], cancellationToken).ConfigureAwait(false);
            _ = await _lmStudio.EnsureModelLoadedAsync(model.Id, _options.GeneralContextLength, cancellationToken).ConfigureAwait(false);
            var selection = new GeneralModelSelection(model.Id, _options.GeneralContextLength, true);
            Directory.CreateDirectory(Path.GetDirectoryName(SelectionPath)!);
            var temporary = SelectionPath + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(selection), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, SelectionPath, true);
            return selection;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
