using GoAi.Contracts;
using GoWinUI.Infrastructure;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GoWinUI.App.Services;

public sealed record CodingProcessConsole(
    string OperationId,
    string Command,
    string WorkingDirectory,
    string Purpose,
    string Status,
    int? ExitCode = null,
    string? StandardOutput = null,
    string? StandardError = null);

public sealed record CodingRunTraceEntry(
    long Sequence,
    DateTimeOffset Timestamp,
    string Stage,
    string Status,
    string Title,
    string? Detail = null,
    string? Tool = null,
    string? Target = null,
    long? DurationMilliseconds = null,
    long? ServerEventId = null,
    CodingProcessConsole? ProcessConsole = null);

internal sealed record CodingRunTraceRecord(
    Guid LocalRunId,
    string? ServerRunId,
    Guid SessionId,
    Guid MessageId,
    CodingRunTraceEntry Entry);

/// <summary>
/// Persists the execution trace for real coding runs. Process entries include
/// bounded command output for the PowerShell panel. Failures here must never
/// interrupt the agent.
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
    private readonly ILogger<CodingRunTraceService> _logger;
    private readonly ConcurrentDictionary<Guid, TraceState> _states = new();

    public CodingRunTraceService(GoInfrastructureOptions options, ILogger<CodingRunTraceService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
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
        var state = new TraceState(localRunId, sessionId, messageId);
        _states[messageId] = state;
        try
        {
            Directory.CreateDirectory(_traceDirectory);
            var path = TracePath(messageId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (IsDiagnosticIoFailure(exception))
        {
            TraceWriteFailed(_logger, messageId.ToString("D"), exception);
        }

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

    public Task<CodingRunTraceEntry> AppendAsync(
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
        var state = _states.GetOrAdd(messageId, _ => LoadState(localRunId, sessionId, messageId));
        return AppendCoreAsync(
            state,
            serverRunId,
            stage,
            status,
            title,
            detail,
            tool,
            target,
            durationMilliseconds,
            serverEventId,
            processConsole,
            cancellationToken);
    }

    public IReadOnlyList<CodingRunTraceEntry> GetForMessage(Guid messageId)
    {
        if (_states.TryGetValue(messageId, out var existing))
        {
            lock (existing.SyncRoot)
            {
                return existing.Entries.ToArray();
            }
        }

        var loaded = LoadState(Guid.Empty, Guid.Empty, messageId);
        if (loaded.Entries.Count == 0)
        {
            return Array.Empty<CodingRunTraceEntry>();
        }

        _states.TryAdd(messageId, loaded);
        lock (loaded.SyncRoot)
        {
            return loaded.Entries.ToArray();
        }
    }

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

    private async Task<CodingRunTraceEntry> AppendCoreAsync(
        TraceState state,
        string? serverRunId,
        string stage,
        string status,
        string title,
        string? detail,
        string? tool,
        string? target,
        long? durationMilliseconds,
        long? serverEventId,
        CodingProcessConsole? processConsole,
        CancellationToken cancellationToken)
    {
        CodingRunTraceEntry entry;
        lock (state.SyncRoot)
        {
            entry = new CodingRunTraceEntry(
                ++state.Sequence,
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
            state.Entries.Add(entry);
        }

        try
        {
            Directory.CreateDirectory(_traceDirectory);
            var record = new CodingRunTraceRecord(
                state.LocalRunId,
                serverRunId,
                state.SessionId,
                state.MessageId,
                entry);
            var line = JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine;
            await state.FileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await File.AppendAllTextAsync(
                    TracePath(state.MessageId),
                    line,
                    new UTF8Encoding(false),
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                state.FileGate.Release();
            }
        }
        catch (Exception exception) when (IsDiagnosticIoFailure(exception))
        {
            TraceWriteFailed(_logger, state.MessageId.ToString("D"), exception);
        }

        return entry;
    }

    private TraceState LoadState(Guid localRunId, Guid sessionId, Guid messageId)
    {
        var entries = new List<CodingRunTraceEntry>();
        var resolvedLocalRunId = localRunId;
        var resolvedSessionId = sessionId;
        try
        {
            var path = TracePath(messageId);
            if (File.Exists(path))
            {
                foreach (var line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }
                    var record = JsonSerializer.Deserialize<CodingRunTraceRecord>(line, JsonOptions);
                    if (record is null || record.MessageId != messageId)
                    {
                        continue;
                    }
                    resolvedLocalRunId = record.LocalRunId;
                    resolvedSessionId = record.SessionId;
                    entries.Add(record.Entry);
                }
            }
        }
        catch (Exception exception) when (IsDiagnosticIoFailure(exception) || exception is JsonException)
        {
            TraceWriteFailed(_logger, messageId.ToString("D"), exception);
        }

        return new TraceState(resolvedLocalRunId, resolvedSessionId, messageId, entries);
    }

    private string TracePath(Guid messageId) => Path.Combine(_traceDirectory, $"{messageId:N}.jsonl");

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

    private static bool IsDiagnosticIoFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or NotSupportedException;

    private sealed class TraceState
    {
        public TraceState(Guid localRunId, Guid sessionId, Guid messageId, List<CodingRunTraceEntry>? entries = null)
        {
            LocalRunId = localRunId;
            SessionId = sessionId;
            MessageId = messageId;
            Entries = entries ?? [];
            Sequence = Entries.Count == 0 ? 0 : Entries.Max(static entry => entry.Sequence);
        }

        public Guid LocalRunId { get; }
        public Guid SessionId { get; }
        public Guid MessageId { get; }
        public object SyncRoot { get; } = new();
        public SemaphoreSlim FileGate { get; } = new(1, 1);
        public List<CodingRunTraceEntry> Entries { get; }
        public long Sequence { get; set; }
    }
}
