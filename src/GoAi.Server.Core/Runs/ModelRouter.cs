using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
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

    public ModelRouter(IOptions<GoAiServerOptions> options)
    {
        _options = options.Value;
    }

    public ModelSelection Select(RunRequest request)
    {
        if (request.Mode == RunMode.General)
        {
            return new ModelSelection(_options.GeneralModelId, "general", _options.GeneralContextLength);
        }

        if (request.Mode == RunMode.Code)
        {
            return new ModelSelection(_options.CodeModelId, "code", _options.CodeContextLength);
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
            ? new ModelSelection(_options.CodeModelId, "code", _options.CodeContextLength)
            : new ModelSelection(_options.GeneralModelId, "general", _options.GeneralContextLength);
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
