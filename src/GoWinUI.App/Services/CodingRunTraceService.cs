using GoAi.Contracts;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using GoWinUI.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GoWinUI.App.Services;

internal sealed record CodingRunTraceRecord(
    Guid LocalRunId,
    string? ServerRunId,
    Guid SessionId,
    Guid MessageId,
    CodingRunTraceEntry Entry);

/// <summary>
/// Persists the execution trace for real coding runs in SQLite. Existing JSONL
/// files are accepted only by the one-time idempotent legacy importer.
/// </summary>
public sealed class CodingRunTraceService
{
    private const int MaximumConsoleCharacters = 96 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex AnsiEscapeRegex = new(
        "\\x1B(?:[@-_]|\\[[0-?]*[ -/]*[@-~])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BenignGitLineEndingWarningRegex = new(
        "warning:\\s+in the working copy of ['\"][^'\"\\r\\n]+['\"],\\s+(?:LF|CRLF) will be replaced by (?:LF|CRLF) the next time Git touches it\\.?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Action<ILogger, string, Exception?> TraceWriteFailed = LoggerMessage.Define<string>(
        LogLevel.Warning,
        new EventId(5350, nameof(TraceWriteFailed)),
        "Coding trace for message {MessageId} could not be persisted");
    private readonly string _traceDirectory;
    private readonly ICodingRunRepository _repository;
    private readonly ILogger<CodingRunTraceService> _logger;
    private readonly ConcurrentDictionary<Guid, long> _sequences = new();

    public CodingRunTraceService(
        GoInfrastructureOptions options,
        ICodingRunRepository repository,
        ILogger<CodingRunTraceService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _repository = repository;
        _logger = logger;
        _traceDirectory = Path.Combine(options.DataDirectory, "CodingRuns", "Traces");
    }

    public string TraceDirectory => _traceDirectory;

    public async Task<CodingRunTraceEntry> StartAsync(
        Guid localRunId,
        Guid sessionId,
        Guid messageId,
        string? workspacePath,
        CancellationToken cancellationToken = default)
    {
        _sequences[messageId] = 0;
        return await AppendAsync(
            localRunId,
            null,
            sessionId,
            messageId,
            "run",
            "running",
            "Coding-Lauf gestartet",
            string.IsNullOrWhiteSpace(workspacePath)
                ? "Workspace wird gepr\u00FCft."
                : $"Workspace: {Path.GetFileName(Path.TrimEndingDirectorySeparator(workspacePath))}",
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<CodingRunTraceEntry> AppendAsync(
        Guid localRunId,
        string? serverRunId,
        Guid sessionId,
        Guid messageId,
        string stage,
        string status,
        string title,
        string? detail = null,
        string? tool = null,
        string? target = null,
        long? durationMilliseconds = null,
        long? serverEventId = null,
        CodingProcessConsole? processConsole = null,
        CancellationToken cancellationToken = default)
    {
        if (!_sequences.ContainsKey(messageId))
        {
            var existing = await _repository.ListForMessageAsync(messageId, cancellationToken).ConfigureAwait(false);
            _sequences.TryAdd(messageId, existing.Count == 0 ? 0 : existing.Max(static entry => entry.Sequence));
        }
        var sequence = _sequences.AddOrUpdate(messageId, 1, static (_, current) => current + 1);
        var entry = new CodingRunTraceEntry(
            sequence,
            DateTimeOffset.UtcNow,
            Limit(stage, 40),
            Limit(status, 40),
            Limit(title, 160),
            LimitNullable(detail, 320),
            LimitNullable(tool, 120),
            LimitNullable(target, 320),
            durationMilliseconds,
            serverEventId,
            processConsole);
        return await _repository.AppendAsync(
            localRunId, serverRunId, sessionId, messageId, entry, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<CodingRunTraceEntry>> GetForMessageAsync(
        Guid messageId,
        CancellationToken cancellationToken = default) =>
        _repository.ListForMessageAsync(messageId, cancellationToken);

    public Task<CodingRunSnapshot?> GetLatestForSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default) =>
        _repository.GetLatestForSessionAsync(sessionId, cancellationToken);

    public Task SetCodeDiffAsync(
        Guid localRunId,
        string? codeDiff,
        CancellationToken cancellationToken = default) =>
        _repository.SetCodeDiffAsync(localRunId, codeDiff, cancellationToken);

    internal static string? ExtractTarget(ToolProposal proposal)
    {
        var specialized = ExtractSpecializedTarget(proposal);
        if (!string.IsNullOrWhiteSpace(specialized))
        {
            return specialized;
        }

        foreach (var name in new[] { "path", "sourcePath", "target", "destination", "source", "directory", "workingDirectory" })
        {
            if (proposal.Arguments.ValueKind == JsonValueKind.Object
                && proposal.Arguments.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return NormalizeTarget(value.GetString()!);
            }
        }

        if (proposal.Arguments.ValueKind == JsonValueKind.Object
            && proposal.Arguments.TryGetProperty("paths", out var paths)
            && paths.ValueKind == JsonValueKind.Array)
        {
            var values = paths.EnumerateArray()
                .Where(static value => value.ValueKind == JsonValueKind.String)
                .Select(static value => value.GetString())
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Take(3)
                .Select(static value => NormalizeTarget(value!))
                .ToArray();
            return values.Length == 0 ? null : string.Join(", ", values);
        }

        return null;
    }

    private static string? ExtractSpecializedTarget(ToolProposal proposal)
    {
        if (proposal.Arguments.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (string.Equals(proposal.Name, ClientToolNames.FileSystemReadMany, StringComparison.Ordinal))
        {
            return ExtractReadManyTarget(proposal.Arguments);
        }

        if (string.Equals(proposal.Name, ClientToolNames.FileSystemFindFiles, StringComparison.Ordinal))
        {
            return ExtractFindFilesTarget(proposal.Arguments);
        }

        if (string.Equals(proposal.Name, ClientToolNames.FileSystemList, StringComparison.Ordinal))
        {
            return ExtractDirectoryTarget(proposal.Arguments);
        }

        if (string.Equals(proposal.Name, ClientToolNames.FileSystemSearch, StringComparison.Ordinal))
        {
            return ExtractSearchTarget(proposal.Arguments);
        }

        if (string.Equals(proposal.Name, ClientToolNames.FileSystemReadText, StringComparison.Ordinal)
            || string.Equals(proposal.Name, ClientToolNames.FileSystemStat, StringComparison.Ordinal)
            || string.Equals(proposal.Name, "fs.readFile", StringComparison.Ordinal))
        {
            var path = ReadString(proposal.Arguments, "path");
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }
            return NormalizeTarget(FormatPathRange(
                path,
                ReadInteger(proposal.Arguments, "startLine"),
                ReadInteger(proposal.Arguments, "endLine")));
        }

        if (proposal.Name.StartsWith("documents.", StringComparison.Ordinal))
        {
            return ExtractDocumentTarget(proposal.Arguments);
        }

        var compact = ExtractCompactArgumentSummary(proposal.Arguments);
        if (!string.IsNullOrWhiteSpace(compact))
        {
            return compact;
        }

        return null;
    }

    private static string? ExtractDirectoryTarget(JsonElement arguments)
    {
        var path = ReadString(arguments, "path")
            ?? ReadString(arguments, "directory")
            ?? ReadString(arguments, "root")
            ?? ".";
        return NormalizeTarget(path);
    }

    private static string? ExtractReadManyTarget(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var count = items.GetArrayLength();
        var values = items.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.Object)
            .Select(static item =>
            {
                var path = ReadString(item, "path");
                return string.IsNullOrWhiteSpace(path)
                    ? null
                    : FormatPathRange(path, ReadInteger(item, "startLine"), ReadInteger(item, "endLine"));
            })
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .Take(4)
            .ToArray();
        if (values.Length == 0)
        {
            return null;
        }

        var suffix = count > values.Length ? $" (+{count - values.Length} weitere)" : string.Empty;
        return Limit(string.Join(", ", values.Select(NormalizeTarget)) + suffix, 320);
    }

    private static string? ExtractFindFilesTarget(JsonElement arguments)
    {
        var root = ReadString(arguments, "path");
        root = string.IsNullOrWhiteSpace(root) ? "." : root;
        var patterns = ReadStringArray(arguments, "patterns");
        if (patterns.Length == 0)
        {
            patterns = ReadStringArray(arguments, "includeGlobs");
        }
        if (patterns.Length == 0)
        {
            return NormalizeTarget(root);
        }

        var displayed = patterns.Take(4).ToArray();
        var suffix = patterns.Length > displayed.Length ? $" (+{patterns.Length - displayed.Length} weitere)" : string.Empty;
        return Limit($"{NormalizeTarget(root)}: {string.Join(", ", displayed)}{suffix}", 320);
    }

    private static string? ExtractSearchTarget(JsonElement arguments)
    {
        var root = ReadString(arguments, "path")
            ?? ReadString(arguments, "directory")
            ?? ".";
        var queries = ReadStringArray(arguments, "queries");
        var query = ReadString(arguments, "query");
        if (!string.IsNullOrWhiteSpace(query))
        {
            queries = [query!, .. queries];
        }

        var includeGlobs = ReadStringArray(arguments, "includeGlobs");
        var excludeGlobs = ReadStringArray(arguments, "excludeGlobs");
        var parts = new List<string> { NormalizeTarget(root) };
        if (queries.Length > 0)
        {
            parts.Add("Suche: " + JoinLimited(queries, 3));
        }
        if (includeGlobs.Length > 0)
        {
            parts.Add("Include: " + JoinLimited(includeGlobs, 3));
        }
        if (excludeGlobs.Length > 0)
        {
            parts.Add("Exclude: " + JoinLimited(excludeGlobs, 2));
        }
        return Limit(string.Join(" · ", parts), 320);
    }

    private static string? ExtractDocumentTarget(JsonElement arguments)
    {
        var documentName = ReadString(arguments, "documentName")
            ?? ReadString(arguments, "fileName")
            ?? ReadString(arguments, "name");
        var query = ReadString(arguments, "query");
        var page = ReadInteger(arguments, "page");
        var startPage = ReadInteger(arguments, "startPage");
        var endPage = ReadInteger(arguments, "endPage");
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(documentName))
        {
            parts.Add(documentName!);
        }
        if (!string.IsNullOrWhiteSpace(query))
        {
            parts.Add("Suche: " + query);
        }
        if (page is { } singlePage)
        {
            parts.Add($"S. {singlePage}");
        }
        else if (startPage is not null || endPage is not null)
        {
            parts.Add($"S. {startPage?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}-{endPage?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}");
        }
        return parts.Count == 0 ? null : Limit(string.Join(" · ", parts), 320);
    }

    private static string? ExtractCompactArgumentSummary(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var arrayName in new[] { "paths", "files", "items", "queries", "patterns", "includeGlobs", "excludeGlobs" })
        {
            var values = ReadCompactArray(arguments, arrayName);
            if (values.Length > 0)
            {
                return Limit($"{arrayName}: {JoinLimited(values, 4)}", 320);
            }
        }

        return null;
    }

    private static string[] ReadCompactArray(JsonElement owner, string name)
    {
        if (owner.ValueKind != JsonValueKind.Object
            || !owner.TryGetProperty(name, out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return array.EnumerateArray()
            .Select(static item => item.ValueKind switch
            {
                JsonValueKind.String => item.GetString(),
                JsonValueKind.Object => ReadString(item, "path")
                    ?? ReadString(item, "target")
                    ?? ReadString(item, "query")
                    ?? ReadString(item, "pattern")
                    ?? ReadString(item, "documentName")
                    ?? ReadString(item, "fileName"),
                _ => null,
            })
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => NormalizeTarget(value!))
            .ToArray();
    }

    private static string JoinLimited(string[] values, int maximum)
    {
        var displayed = values.Take(maximum).ToArray();
        var suffix = values.Length > displayed.Length ? $" (+{values.Length - displayed.Length} weitere)" : string.Empty;
        return string.Join(", ", displayed) + suffix;
    }

    private static string FormatPathRange(string path, int? startLine, int? endLine)
    {
        if (startLine is null && endLine is null)
        {
            return path;
        }
        if (startLine is { } start && endLine is { } end)
        {
            return $"{path}:{start}-{end}";
        }
        if (startLine is { } onlyStart)
        {
            return $"{path}:{onlyStart}";
        }
        return $"{path}:1-{endLine}";
    }

    internal static string DescribeResult(ClientToolResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            return Limit(result.Message.ReplaceLineEndings(" ").Trim(), 240);
        }

        if (result.Result.ValueKind != JsonValueKind.Object)
        {
            return string.Equals(result.Status, "completed", StringComparison.OrdinalIgnoreCase)
                ? "Aktion erfolgreich abgeschlossen."
                : "Aktion beendet.";
        }

        var parts = new List<string>();
        AddScalar("exitCode", "Exit-Code");
        AddScalar("fileCount", "Dateien");
        AddScalar("matchCount", "Treffer");
        AddScalar("changed", "Ge\u00E4ndert");
        AddScalar("created", "Erstellt");
        AddScalar("deleted", "Gel\u00F6scht");
        return parts.Count > 0
            ? string.Join(" \u00B7 ", parts)
            : string.Equals(result.Status, "completed", StringComparison.OrdinalIgnoreCase)
                ? "Aktion erfolgreich abgeschlossen."
                : result.ErrorCode ?? "Aktion beendet.";

        void AddScalar(string property, string label)
        {
            if (!result.Result.TryGetProperty(property, out var value)
                || value.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null)
            {
                return;
            }
            parts.Add($"{label}: {Limit(value.ToString(), 80)}");
        }
    }

    internal static CodingProcessConsole? CreateProcessConsole(
        ToolProposal proposal,
        ClientToolResult? result = null)
    {
        if (!TryDescribePowerShellCommand(proposal, out var command, out var workingDirectory, out var purpose))
        {
            return null;
        }

        var exitCode = result is null ? null : ReadInteger(result.Result, "exitCode");
        var passed = result is null ? null : ReadBoolean(result.Result, "passed");
        var status = result is null
            ? "running"
            : !string.Equals(result.Status, "completed", StringComparison.OrdinalIgnoreCase)
                || exitCode is not null and not 0
                || passed == false
                    ? "failed"
                    : "completed";
        var standardOutput = result is null
            ? null
            : proposal.Name == ClientToolNames.LeanProof
                ? FilterConsoleNoise(FormatLeanConsoleOutput(result.Result))
                : FilterConsoleNoise(ReadString(result.Result, "standardOutput"));
        var standardError = result is null
            ? null
            : proposal.Name == ClientToolNames.LeanProof
                ? FilterConsoleNoise(FormatLeanConsoleError(result.Result, result.Message, status))
                : FilterConsoleNoise(ReadString(result.Result, "standardError"));
        if (result is not null
            && !string.Equals(status, "completed", StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(standardError)
            && !string.IsNullOrWhiteSpace(result.Message))
        {
            standardError = result.Message;
        }

        return new CodingProcessConsole(
            proposal.ProposalId,
            LimitConsole(command),
            LimitConsole(workingDirectory),
            purpose,
            status,
            exitCode,
            LimitConsoleNullable(standardOutput),
            LimitConsoleNullable(standardError));
    }

    private static bool TryDescribePowerShellCommand(
        ToolProposal proposal,
        out string command,
        out string workingDirectory,
        out string purpose)
    {
        command = string.Empty;
        workingDirectory = ".";
        purpose = "start";
        if (proposal.Arguments.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (proposal.Name == ClientToolNames.ProcessRun)
        {
            var executable = ReadString(proposal.Arguments, "executable");
            if (string.IsNullOrWhiteSpace(executable))
            {
                return false;
            }
            var arguments = proposal.Arguments.TryGetProperty("arguments", out var argumentArray)
                && argumentArray.ValueKind == JsonValueKind.Array
                    ? argumentArray.EnumerateArray()
                        .Where(static value => value.ValueKind == JsonValueKind.String)
                        .Select(static value => value.GetString() ?? string.Empty)
                    : [];
            command = string.Join(' ', new[] { executable }.Concat(arguments).Select(QuotePowerShellArgument));
            workingDirectory = ReadString(proposal.Arguments, "workingDirectory") ?? ".";
            purpose = ReadString(proposal.Arguments, "purpose")?.ToLowerInvariant() ?? "inspect";
            return true;
        }

        if (proposal.Name == ClientToolNames.LeanProof)
        {
            var operation = ReadString(proposal.Arguments, "operation");
            if (string.IsNullOrWhiteSpace(operation))
            {
                return false;
            }

            var path = ReadString(proposal.Arguments, "path");
            var leanTarget = ReadString(proposal.Arguments, "target");
            var theoremName = ReadString(proposal.Arguments, "theoremName");
            var timeoutSeconds = ReadInteger(proposal.Arguments, "timeoutSeconds");
            var arguments = new List<string> { "proof.lean", operation };
            if (!string.IsNullOrWhiteSpace(path))
            {
                arguments.Add(path);
            }
            if (!string.IsNullOrWhiteSpace(leanTarget))
            {
                arguments.Add("-Target");
                arguments.Add(leanTarget);
            }
            if (!string.IsNullOrWhiteSpace(theoremName))
            {
                arguments.Add("-TheoremName");
                arguments.Add(theoremName);
            }
            if (timeoutSeconds is { } timeout)
            {
                arguments.Add("-TimeoutSeconds");
                arguments.Add(timeout.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            command = string.Join(' ', arguments.Select(QuotePowerShellArgument));
            workingDirectory = ".";
            purpose = operation switch
            {
                "check" => "test",
                "verify" or "axioms" => "verify",
                "build" => "build",
                _ => "inspect",
            };
            return true;
        }

        if (proposal.Name != ClientToolNames.ProcessRunPreset)
        {
            return false;
        }

        var preset = ReadString(proposal.Arguments, "preset");
        if (string.IsNullOrWhiteSpace(preset))
        {
            return false;
        }

        var target = ReadString(proposal.Arguments, "target");
        command = DescribePresetCommand(preset, target);
        workingDirectory = ReadString(proposal.Arguments, "workspace") ?? ".";
        purpose = preset switch
        {
            "dotnet.test" or "code.test" => "test",
            "dotnet.build" or "repository.build" => "build",
            "repository.start" or "code.run" => "start",
            "repository.verify" => "verify",
            _ => "inspect",
        };
        return true;
    }

    private static string DescribePresetCommand(string preset, string? target)
    {
        var targetArgument = string.IsNullOrWhiteSpace(target)
            ? string.Empty
            : $" {QuotePowerShellArgument(target)}";
        return preset switch
        {
            "git.status" => "git status --short",
            "git.diff" => "git diff --no-ext-diff",
            "dotnet.build" => $"dotnet build{targetArgument} --nologo",
            "dotnet.test" => $"dotnet test{targetArgument} --nologo",
            _ => $"GO-Preset {QuotePowerShellArgument(preset)}{targetArgument}",
        };
    }

    private static string QuotePowerShellArgument(string value)
    {
        if (value.Length > 0
            && value.All(static character => char.IsLetterOrDigit(character)
                || character is '_' or '-' or '.' or '/' or '\\' or ':'))
        {
            return value;
        }
        return $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
    }

    private static string? ReadString(JsonElement owner, string name)
    {
        if (owner.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        foreach (var property in owner.EnumerateObject())
        {
            if (property.NameEquals(name)
                || string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : null;
            }
        }
        return null;
    }

    private static int? ReadInteger(JsonElement owner, string name)
    {
        if (owner.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        foreach (var property in owner.EnumerateObject())
        {
            if ((property.NameEquals(name)
                    || string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                && property.Value.TryGetInt32(out var value))
            {
                return value;
            }
        }
        return null;
    }

    private static bool? ReadBoolean(JsonElement owner, string name)
    {
        if (owner.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        foreach (var property in owner.EnumerateObject())
        {
            if (property.NameEquals(name)
                || string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null,
                };
            }
        }
        return null;
    }

    private static string? FormatLeanConsoleOutput(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var lines = new List<string>();
        AddLine("Operation", ReadString(result, "operation"));
        AddLine("Projekt", ReadString(result, "project"));
        AddLine("Pfad", ReadString(result, "path"));
        AddLine("Target", ReadString(result, "target"));
        AddLine("Theorem", ReadString(result, "theoremName"));
        AddLine("Lean", ReadString(result, "leanVersion"));
        AddLine("Lake", ReadString(result, "lakeVersion"));
        if (ReadInteger(result, "durationMilliseconds") is { } duration)
        {
            lines.Add($"Dauer: {duration} ms");
        }
        if (ReadBoolean(result, "available") is { } available)
        {
            lines.Add($"Toolchain verfÃ¼gbar: {(available ? "ja" : "nein")}");
        }
        if (ReadBoolean(result, "passed") is { } passed)
        {
            lines.Add($"Bestanden: {(passed ? "ja" : "nein")}");
        }

        var axioms = ReadStringArray(result, "axioms");
        if (axioms.Length > 0)
        {
            lines.Add("Axiome: " + string.Join(", ", axioms));
        }

        var diagnostics = FormatLeanDiagnostics(result);
        if (diagnostics.Length > 0)
        {
            lines.Add("Diagnosen:");
            lines.AddRange(diagnostics);
        }

        AddLine("Meldung", ReadString(result, "message"));
        return lines.Count == 0 ? null : string.Join('\n', lines);

        void AddLine(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                lines.Add($"{label}: {value}");
            }
        }
    }

    private static string? FormatLeanConsoleError(JsonElement result, string? resultMessage, string status)
    {
        if (!string.Equals(status, "failed", StringComparison.Ordinal))
        {
            return null;
        }

        var lines = new List<string>();
        var message = ReadString(result, "message") ?? resultMessage;
        if (!string.IsNullOrWhiteSpace(message))
        {
            lines.Add(message);
        }

        var forbidden = ReadStringArray(result, "forbiddenConstructs");
        if (forbidden.Length > 0)
        {
            lines.Add("Verbotene Konstrukte: " + string.Join(", ", forbidden));
        }

        var diagnostics = FormatLeanDiagnostics(result);
        if (diagnostics.Length > 0)
        {
            lines.Add("Diagnosen:");
            lines.AddRange(diagnostics);
        }

        return lines.Count == 0 ? null : string.Join('\n', lines);
    }

    private static string[] FormatLeanDiagnostics(JsonElement owner)
    {
        if (owner.ValueKind != JsonValueKind.Object
            || !owner.TryGetProperty("diagnostics", out var diagnostics)
            || diagnostics.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var lines = new List<string>();
        foreach (var diagnostic in diagnostics.EnumerateArray())
        {
            if (diagnostic.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var severity = ReadString(diagnostic, "severity") ?? "diagnostic";
            var file = ReadString(diagnostic, "file") ?? ".";
            var line = ReadInteger(diagnostic, "line");
            var column = ReadInteger(diagnostic, "column");
            var message = ReadString(diagnostic, "message") ?? string.Empty;
            var location = line is { } lineNumber && column is { } columnNumber
                ? $"{file}:{lineNumber}:{columnNumber}"
                : file;
            lines.Add($"{location}: {severity}: {message}".TrimEnd());
        }
        return lines.ToArray();
    }

    private static string[] ReadStringArray(JsonElement owner, string name)
    {
        if (owner.ValueKind != JsonValueKind.Object
            || !owner.TryGetProperty(name, out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return array.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString())
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item!)
            .ToArray();
    }

    public async Task<int> ImportLegacyAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_traceDirectory))
        {
            return 0;
        }

        var imported = 0;
        foreach (var path in Directory.EnumerateFiles(_traceDirectory, "*.jsonl", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var records = new List<CodingRunTraceRecord>();
                foreach (var line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }
                    var record = JsonSerializer.Deserialize<CodingRunTraceRecord>(line, JsonOptions);
                    if (record is not null)
                    {
                        records.Add(record);
                    }
                }

                foreach (var group in records.GroupBy(static record => new
                         {
                             record.LocalRunId,
                             record.ServerRunId,
                             record.SessionId,
                             record.MessageId,
                         }))
                {
                    var entries = group.Select(static record => record.Entry)
                        .GroupBy(static entry => entry.Sequence)
                        .Select(static values => values.Last())
                        .OrderBy(static entry => entry.Sequence)
                        .ToArray();
                    if (entries.Length == 0)
                    {
                        continue;
                    }
                    await _repository.ImportAsync(
                        group.Key.LocalRunId,
                        group.Key.ServerRunId,
                        group.Key.SessionId,
                        group.Key.MessageId,
                        entries,
                        cancellationToken).ConfigureAwait(false);
                    _sequences[group.Key.MessageId] = entries[^1].Sequence;
                    imported++;
                }

                File.Move(path, path + ".imported", overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or SqliteException)
            {
                TraceWriteFailed(_logger, Path.GetFileNameWithoutExtension(path), exception);
            }
        }
        return imported;
    }

    private static string NormalizeTarget(string value)
    {
        var normalized = value.Replace('\\', '/').Trim();
        if (!Path.IsPathRooted(normalized))
        {
            return Limit(normalized, 320);
        }

        // Absolute workspace locations are unnecessary in the UI trace. Keep
        // only the final path portion while the full path remains authoritative
        // inside the broker.
        return Limit(Path.GetFileName(normalized.TrimEnd('/')), 320);
    }

    private static string Limit(string value, int maximum) =>
        value.Length <= maximum ? value : value[..(maximum - 1)] + "\u2026";

    private static string? LimitNullable(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value) ? null : Limit(value.ReplaceLineEndings(" ").Trim(), maximum);

    private static string LimitConsole(string value)
    {
        var normalized = AnsiEscapeRegex.Replace(value, string.Empty).ReplaceLineEndings("\n").TrimEnd();
        if (normalized.Length <= MaximumConsoleCharacters)
        {
            return normalized;
        }

        const int tailCharacters = 24 * 1024;
        var headCharacters = MaximumConsoleCharacters - tailCharacters;
        return normalized[..headCharacters]
            + $"\n\n[... {normalized.Length - MaximumConsoleCharacters:N0} Zeichen ausgeblendet ...]\n\n"
            + normalized[^tailCharacters..];
    }

    private static string? LimitConsoleNullable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : LimitConsole(value);

    internal static string? FilterConsoleNoise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var filtered = BenignGitLineEndingWarningRegex.Replace(value, string.Empty)
            .ReplaceLineEndings("\n");
        var lines = filtered.Split('\n')
            .Select(static line => line.TrimEnd())
            .Where(static line => line.Length > 0)
            .ToArray();
        return lines.Length == 0 ? null : string.Join('\n', lines);
    }

}
