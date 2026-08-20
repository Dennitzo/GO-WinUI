using System.Text.Json;
using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using Microsoft.Extensions.Options;

namespace GoAi.Server.Core.Models;

public sealed class CodingModelSelectionService : IDisposable
{
    private readonly GoAiServerOptions _options;
    private readonly LmStudioClient _lmStudio;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string SelectionPath => Path.Combine(_options.DataDirectory, "Config", "coding-model.json");

    public CodingModelSelectionService(IOptions<GoAiServerOptions> options, LmStudioClient lmStudio)
    {
        _options = options.Value;
        _lmStudio = lmStudio;
    }

    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(SelectionPath))
        {
            Apply(CodingModelCatalog.Get(CodingModelCatalog.DefaultModelId));
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(SelectionPath, cancellationToken).ConfigureAwait(false);
            var saved = JsonSerializer.Deserialize<CodingModelSelection>(json);
            if (CodingModelCatalog.TryGet(saved?.ModelId, out var profile))
            {
                Apply(profile);
                return;
            }
        }
        catch (JsonException)
        {
            // Invalid state is replaced by the deterministic default below.
        }

        Apply(CodingModelCatalog.Get(CodingModelCatalog.DefaultModelId));
    }

    public async Task<CodingModelSelection> SelectAsync(
        string modelId,
        CancellationToken cancellationToken = default)
    {
        var profile = CodingModelCatalog.Get(modelId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = await _lmStudio.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            var model = status.Models.FirstOrDefault(candidate =>
                candidate.Downloaded
                && string.Equals(candidate.Role, "code", StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.Id, profile.Id, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"Das LM-Studio-Codingmodell '{profile.DisplayName}' ist nicht verfügbar.");

            Apply(profile with { ContextLength = Math.Min(profile.ContextLength, model.ContextTokens) });
            var selection = new CodingModelSelection(
                profile.Id,
                profile.DisplayName,
                _options.CodeContextLength,
                model.Loaded);
            Directory.CreateDirectory(Path.GetDirectoryName(SelectionPath)!);
            var temporary = SelectionPath + ".tmp";
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(selection),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporary, SelectionPath, true);
            return selection;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Apply(CodingModelProfile profile)
    {
        _options.CodeModelId = profile.Id;
        _options.CodeContextLength = profile.ContextLength;
    }

    public void Dispose() => _gate.Dispose();
}
