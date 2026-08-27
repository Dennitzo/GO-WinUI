using GoAi.Contracts;
using GoAi.Server.Core.Models;
using GoAi.Server.Core.Runtime;
using GoAi.Server.Core.Workers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GoAi.Server.Core.Runs;

/// <summary>
/// Stable entry point for coding runs. All runtime behavior lives in the V4
/// step engine; this type only wires dependencies and retains the two shared
/// protocol guards used by the engine.
/// </summary>
public sealed class CodingAgentOrchestrator
{
    private static readonly Regex OutputPathPattern = new(
        @"(?<![\p{L}\p{N}_./:\\-])(?<path>(?:[\p{L}\p{N}_@.+-]+[\\/])*[\p{L}\p{N}_@.+-]+\.(?:py|pyw|cs|xaml|csproj|sln|fs|fsx|vb|cpp|cxx|cc|c|h|hpp|rs|go|java|kt|kts|js|jsx|ts|tsx|vue|svelte|html|css|scss|sql|sh|ps1|cmd|bat|lean|md|txt|json|ya?ml|toml|xml|csv|tex|ipynb))(?![\p{L}\p{N}_/])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex OutputRequestVerbPattern = new(
        @"\b(?:erstell(?:e|en|t)|schreib(?:e|en|t)|erzeug(?:e|en|t)|leg(?:e|en|t)\s+an|implementier(?:e|en|t)|bearbeit(?:e|en|et)|aender(?:e|n|t)|änder(?:e|n|t)|aktualisier(?:e|en|t)|speicher(?:e|n|t)|create|write|generate|implement|edit|update|save)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly RunRepository repository;
    private readonly GpuLeaseScheduler scheduler;
    private readonly ModelRuntimeClient modelRuntime;
    private readonly WorkerOrchestrator workers;
    private readonly AgentToolCatalog toolCatalog;
    private readonly AgentToolExecutor toolExecutor;

    public CodingAgentOrchestrator(
        RunRepository repository,
        GpuLeaseScheduler scheduler,
        ModelRuntimeClient modelRuntime,
        WorkerOrchestrator workers,
        AgentToolCatalog toolCatalog,
        AgentToolExecutor toolExecutor)
    {
        this.repository = repository;
        this.scheduler = scheduler;
        this.modelRuntime = modelRuntime;
        this.workers = workers;
        this.toolCatalog = toolCatalog;
        this.toolExecutor = toolExecutor;
    }

    public Task ProcessAsync(
        string runId,
        RunRequest request,
        ModelSelection selection,
        CancellationToken cancellationToken) => new CodingAgentV4Engine(
            repository,
            scheduler,
            modelRuntime,
            workers,
            toolCatalog,
            toolExecutor).ProcessAsync(runId, request, selection, cancellationToken);

    internal static IReadOnlyList<string> ExtractRequiredOutputPaths(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return [];
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in OutputPathPattern.Matches(prompt))
        {
            var prefixStart = Math.Max(0, match.Index - 220);
            if (!OutputRequestVerbPattern.IsMatch(prompt[prefixStart..match.Index]))
            {
                continue;
            }
            if (NormalizeRelativeToolPath(match.Groups["path"].Value) is { } normalized)
            {
                paths.Add(normalized);
            }
        }
        return paths.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static string? ValidateModelToolCalls(
        AgentToolCatalog catalog,
        IReadOnlyList<AgentToolSpec> availableTools,
        IReadOnlyList<LmToolCall> calls)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(availableTools);
        ArgumentNullException.ThrowIfNull(calls);
        if (calls.Count != 1)
        {
            return $"Pro Modellturn ist genau ein Werkzeugaufruf erlaubt; geliefert wurden {calls.Count}.";
        }

        try
        {
            var tool = catalog.Resolve(calls[0].Name, availableTools);
            catalog.Validate(tool, calls[0].Arguments);
            return null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or JsonException)
        {
            return exception.Message;
        }
    }

    private static string? NormalizeRelativeToolPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || Uri.TryCreate(value, UriKind.Absolute, out _)
            || Path.IsPathFullyQualified(value))
        {
            return null;
        }
        var normalized = value.Trim().Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }
        normalized = normalized.TrimStart('/');
        if (normalized.Length == 0
            || normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(static segment => segment == ".."))
        {
            return null;
        }
        return normalized;
    }
}

internal sealed class CodingAgentProtocolException : Exception
{
    public CodingAgentProtocolException(string message)
        : base(message)
    {
    }
}
