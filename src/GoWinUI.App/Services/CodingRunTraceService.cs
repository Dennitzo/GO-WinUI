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
        foreach (var name in new[] { "path", "target", "destination", "source", "directory", "workingDirectory" })
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
        var status = result is null
            ? "running"
            : !string.Equals(result.Status, "completed", StringComparison.OrdinalIgnoreCase)
                || exitCode is not null and not 0
                    ? "failed"
                    : "completed";
        var standardOutput = result is null ? null : FilterConsoleNoise(ReadString(result.Result, "standardOutput"));
        var standardError = result is null ? null : FilterConsoleNoise(ReadString(result.Result, "standardError"));
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
