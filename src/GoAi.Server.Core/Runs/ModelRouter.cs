using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using GoAi.Server.Core.Models;
using Microsoft.Extensions.Options;

namespace GoAi.Server.Core.Runs;

public sealed class ModelRouter
{
    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".sln", ".slnx", ".xaml", ".js", ".ts", ".tsx", ".jsx", ".py", ".ps1",
        ".cpp", ".h", ".hpp", ".java", ".go", ".rs", ".sql", ".json", ".yaml", ".yml", ".toml",
    };
    private readonly GoAiServerOptions _options;
    private readonly LmStudioClient? _lmStudio;

    public ModelRouter(IOptions<GoAiServerOptions> options, LmStudioClient? lmStudio = null)
    {
        _options = options.Value;
        _lmStudio = lmStudio;
    }

    public async Task<ModelSelection> SelectAsync(
        RunRequest request,
        CancellationToken cancellationToken = default)
    {
        var selected = Select(request);
        if (_lmStudio is null)
        {
            return selected;
        }

        var status = await _lmStudio.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!status.ProviderReachable)
        {
            throw new HttpRequestException("LM Studio model status is unavailable.");
        }
        var model = status.Models.FirstOrDefault(candidate =>
            candidate.Downloaded
            && string.Equals(candidate.Id, selected.ModelId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"The selected {selected.Role} model '{selected.ModelId}' is not available.");
        return selected with { ContextLength = Math.Max(2_048, model.ContextTokens) };
    }

    public ModelSelection Select(RunRequest request)
    {
        if (request.Mode == RunMode.General)
        {
            return SelectGeneral(request);
        }

        if (request.Mode == RunMode.Code)
        {
            return SelectCode(request);
        }

        var codeAttachment = request.Messages
            .SelectMany(static message => message.Content)
            .Any(part => part.FileName is { } fileName && CodeExtensions.Contains(Path.GetExtension(fileName)));
        var codeCapability = request.ClientCapabilities?.Any(capability =>
            string.Equals(capability, "code", StringComparison.OrdinalIgnoreCase)) == true;
        var latestText = request.Messages
            .LastOrDefault(static message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))?
            .Content.FirstOrDefault(static part => !string.IsNullOrWhiteSpace(part.Text))?.Text;
        var explicitlyCode = latestText is not null && ContainsCodeIntent(latestText);
        return codeAttachment || (codeCapability && explicitlyCode)
            ? SelectCode(request)
            : SelectGeneral(request);
    }

    private ModelSelection SelectGeneral(RunRequest request)
    {
        var modelId = string.IsNullOrWhiteSpace(request.PreferredGeneralModelId)
            ? _options.GeneralModelId
            : request.PreferredGeneralModelId.Trim();
        return new ModelSelection(modelId, "general", _options.GeneralContextLength);
    }

    private ModelSelection SelectCode(RunRequest request)
    {
        var modelId = string.IsNullOrWhiteSpace(request.PreferredCodeModelId)
            ? _options.CodeModelId
            : request.PreferredCodeModelId.Trim();
        var profile = CodingModelCatalog.Get(modelId);
        return new ModelSelection(profile.Id, "code", profile.ContextLength);
    }

    private static bool ContainsCodeIntent(string text)
    {
        string[] indicators =
        [
            "code", "quellcode", "compiler", "build", "debug", "exception", "stacktrace", "csproj",
            "repository", "commit", "pull request", "funktion implementieren", "klasse implementieren",
        ];
        return indicators.Any(indicator => text.Contains(indicator, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record ModelSelection(string ModelId, string Role, int ContextLength);
