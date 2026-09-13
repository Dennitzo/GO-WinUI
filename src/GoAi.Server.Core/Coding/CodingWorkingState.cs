using GoAi.Server.Core.Models;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GoAi.Server.Core.Coding;

public sealed record CodingPlanItem(string Id, string Title, string Status, IReadOnlyList<string> EvidenceIds);
public sealed record CodingStateFact(string Text, IReadOnlyList<string> EvidenceIds);
public sealed record CodingToolEvidence(string Id, string Tool, bool Success, string? Path, string? Sha256,
    int? ExitCode, string Summary, long Sequence, string? OutputEvidenceId = null,
    string? SourceRunId = null, bool Historical = false);
public sealed record CodingActiveFile(string Path, string? Sha256, int StartLine, string Snippet,
    bool Truncated, bool NeedsRead, string EvidenceId, long Sequence, string? OutputEvidenceId = null);
public sealed record CodingFailureSignature(string Signature, string Tool, string ArgumentHash, string Error,
    int Count, long EnvironmentRevision, string EvidenceId);

/// <summary>Bounded, checkpoint-serializable working data; it does not replace the original run journal.</summary>
public sealed record CodingWorkingState(
    string OriginalTask,
    IReadOnlyList<CodingPlanItem> Plan,
    IReadOnlyList<CodingPlanItem> AcceptanceCriteria,
    IReadOnlyList<CodingStateFact> Facts,
    IReadOnlyList<CodingStateFact> RejectedHypotheses,
    IReadOnlyList<CodingToolEvidence> Evidence,
    IReadOnlyList<CodingToolEvidence> Tests,
    IReadOnlyList<CodingActiveFile> ActiveFiles,
    IReadOnlyList<CodingFailureSignature> Failures,
    string? NextStep,
    string Phase,
    long EnvironmentRevision,
    long Sequence,
    string? EnvironmentFingerprint = null)
{
    public static CodingWorkingState Create(string originalTask)
    {
        ArgumentNullException.ThrowIfNull(originalTask);
        return new(originalTask, [], [], [], [], [], [], [], [], null, "planning", 0, 0);
    }
}

public static class CodingWorkingStateReducer
{
    public const int MaximumEvidence = 224;
    public const int MaximumActiveFiles = 12;
    public const int MaximumFailures = 64;
    private static readonly SearchValues<char> EvidenceIdCharacters = SearchValues.Create("0123456789abcdef");

    public static CodingWorkingState ObserveToolResult(CodingWorkingState state, LmToolCall call, string resultJson)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(resultJson);
        // Replayed receipts must not increment failure counters or invalidate newer file evidence.
        if (state.Evidence.Any(item => item.Id == call.Id)) return state;
        using var document = Parse(resultJson);
        var outer = document?.RootElement ?? default;
        var result = Unwrap(outer);
        var success = IsSuccessfulResult(outer);
        var failed = IsFailedResult(outer);
        var path = Text(result, "path") ?? Text(call.Arguments, "path");
        if (path is not null) path = Bound(path.Replace('\\', '/'), 1024);
        var sha = Text(result, "sha256");
        if (sha is not null && (sha.Length != 64 || sha.Any(static value => !char.IsAsciiHexDigit(value)))) sha = null;
        var exit = Integer(result, "exitCode");
        var outputEvidenceId = Text(result, "evidenceId") ?? Text(outer, "evidenceId");
        if (outputEvidenceId is null && result.ValueKind == JsonValueKind.Object && result.TryGetProperty("evidence", out var outputReference))
            outputEvidenceId = Text(outputReference, "evidenceId");
        if (outputEvidenceId is not null && (outputEvidenceId.Length != 35 || !outputEvidenceId.StartsWith("ev-", StringComparison.Ordinal)
            || outputEvidenceId.AsSpan(3).ContainsAnyExcept(EvidenceIdCharacters))) outputEvidenceId = null;
        var sequence = state.Sequence + 1;
        var summary = Bound(Text(result, "stderr") ?? Text(result, "content") ?? Text(result, "stdout")
            ?? Text(outer, "message") ?? Text(result, "message") ?? resultJson, 1600);
        // Prefer actual combined process output; an empty stderr is not a useful success receipt.
        if (exit is not null) summary = Bound("exitCode=" + exit + "\nstdout:\n" + Text(result, "stdout") + "\nstderr:\n" + Text(result, "stderr"), 1600);
        // Output provenance must survive bounded summaries and checkpoints: the text
        // may be much longer than the working-state excerpt and is still historical.
        var sourceRunId = call.Name == "coding.readOutput" ? Text(result, "sourceRunId") : null;
        var historical = call.Name == "coding.readOutput" && Boolean(result, "historical");
        var evidence = new CodingToolEvidence(call.Id, call.Name, success, path, sha, exit, summary, sequence, outputEvidenceId,
            sourceRunId, historical);
        var files = state.ActiveFiles.ToList();
        var environment = state.EnvironmentFingerprint;
        var environmentChanged = false;
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("environment", out var observedEnvironment))
        {
            var fingerprint = Hash(observedEnvironment.GetRawText());
            environmentChanged = environment is not null && environment != fingerprint;
            environment = fingerprint;
        }
        var previousFile = path is null ? null : files.FirstOrDefault(item => SamePath(item.Path, path));
        var contentChanged = sha is not null && previousFile?.Sha256 is { } previousSha && !string.Equals(sha, previousSha, StringComparison.OrdinalIgnoreCase);
        var mutation = success && call.Name is "coding.write" or "coding.edit";
        var processRan = call.Name == "coding.command" && exit is not null;
        // Failed/aborted processes can already have changed the project even
        // when no exit code survived. Only explicit non-execution receipts are safe.
        var commandNotExecuted = Text(outer, "status") is "rejected" or "not_executed"
            || (Text(outer, "errorCode") ?? Text(result, "errorCode")) is
                "agent.invalid_tool_call" or "agent.tool_budget";
        var processMayHaveRun = call.Name == "coding.command" && (processRan
            || Boolean(result, "outcomeUnknown") || Boolean(outer, "outcomeUnknown") || failed && !commandNotExecuted);
        // A successful probe/test is not proof that unrelated failure conditions
        // changed. Its matching signature is removed below; only observed project
        // or environment changes invalidate the broader failure epoch.
        var invalidateFailures = environmentChanged || contentChanged || mutation;
        var epoch = state.EnvironmentRevision + (invalidateFailures ? 1 : 0);
        var failures = invalidateFailures ? new List<CodingFailureSignature>() : state.Failures.ToList();
        if (success)
        {
            var argumentHash = ArgumentHash(call.Arguments);
            failures.RemoveAll(item => item.Tool == call.Name && item.ArgumentHash == argumentHash);
        }
        if (processMayHaveRun)
            files = files.Select(static file => file with { Snippet = string.Empty, NeedsRead = true }).ToList();
        if (path is not null && (success && call.Name == "coding.read" || mutation))
        {
            files.RemoveAll(item => SamePath(item.Path, path));
            var content = mutation ? string.Empty : Text(result, "content") ?? string.Empty;
            files.Add(new(path, sha, Integer(result, "startLine") ?? Integer(call.Arguments, "startLine") ?? 1,
                content[..Math.Min(content.Length, 2400)], content.Length > 2400 || Boolean(result, "truncated"), mutation, call.Id, sequence, outputEvidenceId));
        }
        if (failed)
        {
            var argumentHash = ArgumentHash(call.Arguments);
            var error = Bound(Text(outer, "errorCode") ?? Text(result, "errorCode") ?? Text(result, "stderr")
                ?? Text(outer, "message") ?? Text(result, "message") ?? "tool.failed", 500);
            var signature = Hash(call.Name + "\n" + argumentHash + "\n" + error);
            var previous = failures.FirstOrDefault(item => item.Signature == signature && item.EnvironmentRevision == epoch);
            failures.RemoveAll(item => item.Signature == signature);
            failures.Add(new(signature, call.Name, argumentHash, error, Math.Min(int.MaxValue - 1, previous?.Count ?? 0) + 1, epoch, call.Id));
        }
        return state with
        {
            Evidence = KeepEvidence(state, state.Evidence.Append(evidence)),
            Tests = processRan ? state.Tests.Append(evidence).TakeLast(16).ToArray() : state.Tests,
            ActiveFiles = files.TakeLast(MaximumActiveFiles).ToArray(),
            Failures = failures.TakeLast(MaximumFailures).ToArray(),
            EnvironmentRevision = epoch, EnvironmentFingerprint = environment, Sequence = sequence,
            Phase = failed ? "error_recovery" : success ? mutation ? "editing" : state.Phase == "error_recovery" ? "exploration" : state.Phase : state.Phase,
        };
    }

    public static CodingWorkingState ApplyPlanUpdate(CodingWorkingState state, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) throw new ArgumentException("updatePlan benötigt ein Argumentobjekt.");
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "steps", "acceptanceCriteria", "facts", "rejectedHypotheses", "nextStep", "phase", "explanation" };
        if (arguments.EnumerateObject().Any(property => !allowed.Contains(property.Name))) throw new ArgumentException("Unbekanntes updatePlan-Feld.");
        var plan = arguments.TryGetProperty("steps", out var steps) ? ReadPlan(state, steps, state.Plan) : state.Plan;
        var criteria = arguments.TryGetProperty("acceptanceCriteria", out var acceptance) ? ReadPlan(state, acceptance, state.AcceptanceCriteria) : state.AcceptanceCriteria;
        if (state.AcceptanceCriteria.Any(previous => !criteria.Any(item => item.Id == previous.Id && item.Title == previous.Title)))
            throw new ArgumentException("Vorhandene Akzeptanzkriterien dürfen nicht entfernt oder umgedeutet werden; nur Status und Belege dürfen sich ändern.");
        var facts = arguments.TryGetProperty("facts", out var suppliedFacts) ? ReadFacts(state, suppliedFacts, state.Facts) : state.Facts;
        var rejected = arguments.TryGetProperty("rejectedHypotheses", out var suppliedRejected) ? ReadFacts(state, suppliedRejected, state.RejectedHypotheses) : state.RejectedHypotheses;
        var phase = Text(arguments, "phase") ?? state.Phase;
        if (phase is not ("planning" or "exploration" or "editing" or "review" or "final" or "error_recovery" or "unknown"))
            throw new ArgumentException("Unbekannte Arbeitsphase.");
        var next = arguments.TryGetProperty("nextStep", out _) ? RequireText(arguments, "nextStep", 1000) : state.NextStep;
        if (arguments.TryGetProperty("explanation", out _)) _ = RequireText(arguments, "explanation", 2000);
        return state with { Plan = plan, AcceptanceCriteria = criteria, Facts = facts, RejectedHypotheses = rejected,
            NextStep = next, Phase = phase, Sequence = state.Sequence + 1 };
    }

    private static List<CodingPlanItem> ReadPlan(CodingWorkingState state, JsonElement value, IReadOnlyList<CodingPlanItem> existing)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 1 or > 16) throw new ArgumentException("Ein Plan benötigt 1 bis 16 Schritte.");
        var items = existing.ToList();
        var updatedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateArray())
        {
            var id = RequireText(item, "id", 80);
            var previousIndex = items.FindIndex(previous => previous.Id == id);
            var previous = previousIndex >= 0 ? items[previousIndex] : null;
            var title = item.TryGetProperty("title", out _) || previous is null ? RequireText(item, "title", 400) : previous.Title;
            var status = item.TryGetProperty("status", out _) || previous is null ? RequireText(item, "status", 20) : previous.Status;
            if (status is not ("pending" or "in_progress" or "completed") || !updatedIds.Add(id))
                throw new ArgumentException("Planschritte benötigen eindeutige IDs und einen gültigen Status.");
            IReadOnlyList<string> evidence;
            if (item.TryGetProperty("evidenceIds", out _))
                evidence = ReadEvidenceIds(state, item, status == "completed", requireSuccess: status == "completed");
            else
            {
                evidence = previous?.EvidenceIds ?? [];
                if (status == "completed" && (previous?.Status != "completed" || evidence.Count == 0
                    || evidence.Any(evidenceId => ResolveEvidence(state, evidenceId) is not { Success: true, Tool: not "coding.updatePlan" })))
                    throw new ArgumentException("Ein neuer Abschluss benötigt tatsächliche erfolgreiche evidenceIds.");
            }
            var updated = new CodingPlanItem(id, title, status, evidence);
            if (previousIndex >= 0) items[previousIndex] = updated;
            else items.Add(updated);
        }
        if (items.Count > 16) throw new ArgumentException("Ein gespeicherter Plan darf insgesamt höchstens 16 Schritte enthalten.");
        if (items.Count(static item => item.Status == "in_progress") > 1) throw new ArgumentException("Nur ein Planschritt darf gleichzeitig in_progress sein.");
        return items;
    }

    private static CodingStateFact[] ReadFacts(CodingWorkingState state, JsonElement values, IReadOnlyList<CodingStateFact> existing)
    {
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > 12) throw new ArgumentException("Höchstens 12 belegte Erkenntnisse sind erlaubt.");
        var facts = existing.ToList();
        foreach (var item in values.EnumerateArray())
        {
            var updated = new CodingStateFact(RequireText(item, "text", 600), ReadEvidenceIds(state, item, required: true, requireSuccess: false));
            facts.RemoveAll(previous => previous.Text == updated.Text);
            facts.Add(updated);
        }
        // Only the compact working window rolls; all submitted facts remain in the run journal.
        return facts.TakeLast(12).ToArray();
    }

    private static List<string> ReadEvidenceIds(CodingWorkingState state, JsonElement item, bool required, bool requireSuccess)
    {
        if (!item.TryGetProperty("evidenceIds", out var ids))
            return required ? throw new ArgumentException("Abgeschlossene Schritte und Erkenntnisse benötigen tatsächliche evidenceIds.") : [];
        if (ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() > 3 || required && ids.GetArrayLength() == 0)
            throw new ArgumentException("evidenceIds muss 1 bis 3 tatsächliche Werkzeugbelege nennen.");
        var result = new List<string>();
        foreach (var id in ids.EnumerateArray())
        {
            var evidenceId = id.ValueKind == JsonValueKind.String ? id.GetString() : null;
            var evidence = ResolveEvidence(state, evidenceId);
            if (evidence is null || requireSuccess && (!evidence.Success || evidence.Tool == "coding.updatePlan"))
            {
                var shownId = evidenceId is null ? "null (keine Text-ID)" : JsonSerializer.Serialize(
                    evidenceId.Length <= 160 ? evidenceId : evidenceId[..160] + "… [gekürzt]");
                var reason = evidence is null ? "ist unbekannt oder keinem eindeutigen Werkzeugbeleg zuordenbar"
                    : evidence.Tool == "coding.updatePlan" ? "ist ein Plan-Receipt und belegt keine ausgeführte Arbeit"
                    : "bestätigt keinen erfolgreichen Werkzeugabschluss";
                throw new ArgumentException($"Beleg-ID {shownId} {reason}. Verwende unverändert Evidence.Id oder OutputEvidenceId aus dem Arbeitsstand; eine Datei-SHA ist keine Beleg-ID.");
            }
            if (!result.Contains(evidence.Id, StringComparer.Ordinal)) result.Add(evidence.Id);
        }
        return result;
    }

    private static CodingToolEvidence? ResolveEvidence(CodingWorkingState state, string? evidenceId)
    {
        if (evidenceId is null) return null;
        var canonical = state.Evidence.FirstOrDefault(value => value.Id == evidenceId);
        if (canonical is not null) return canonical;
        var aliases = state.Evidence.Where(value => value.OutputEvidenceId == evidenceId).ToArray();
        // readOutput can refer to a prior command's output. Its successful read
        // must not turn that command's failed receipt into successful execution.
        var originals = aliases.Where(static value => value.Tool is not ("coding.readOutput" or "coding.searchRunEvidence")).ToArray();
        var candidates = originals.Length > 0 ? originals : aliases;
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static CodingToolEvidence[] KeepEvidence(CodingWorkingState state, IEnumerable<CodingToolEvidence> evidence)
    {
        var retainedIds = state.Plan.Concat(state.AcceptanceCriteria).SelectMany(static item => item.EvidenceIds)
            .Concat(state.Facts.Concat(state.RejectedHypotheses).SelectMany(static item => item.EvidenceIds)).ToHashSet(StringComparer.Ordinal);
        var all = evidence.ToArray();
        var pinned = all.Where(item => retainedIds.Contains(item.Id)).ToArray();
        return pinned.Concat(all.Where(item => !retainedIds.Contains(item.Id)).TakeLast(Math.Max(0, MaximumEvidence - pinned.Length)))
            .OrderBy(static item => item.Sequence).ToArray();
    }

    public static bool IsSuccessfulResult(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object || IsFailedResult(result)) return false;
        var status = Text(result, "status");
        if (status is "completed" or "success") return true;
        if (result.TryGetProperty("result", out var nested) && nested.ValueKind == JsonValueKind.Object && IsSuccessfulResult(nested)) return true;
        return status is "completed" or "success" || Boolean(result, "success") || Integer(result, "exitCode") == 0
            || result.TryGetProperty("content", out _) || result.TryGetProperty("entries", out _) || result.TryGetProperty("matches", out _)
            || result.TryGetProperty("results", out _) || result.TryGetProperty("sha256", out _);
    }

    internal static bool IsFailedResult(JsonElement result) => result.ValueKind == JsonValueKind.Object
        && (Text(result, "status") is "failed" or "rejected" or "cancelled"
            || result.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False
            || Integer(result, "exitCode") is { } exit && exit != 0
            || result.TryGetProperty("result", out var nested) && IsFailedResult(nested));

    internal static JsonElement Unwrap(JsonElement value) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty("result", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : value;
    internal static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static string RequireText(JsonElement value, string name, int maximum)
    {
        var text = Text(value, name);
        return text is not null && !string.IsNullOrWhiteSpace(text) && text.Length <= maximum ? text
            : throw new ArgumentException($"{name} muss ein nichtleerer Text mit höchstens {maximum} Zeichen sein.");
    }
    internal static int? Integer(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var number) && number.ValueKind == JsonValueKind.Number && number.TryGetInt32(out var result) ? result : null;
    private static bool Boolean(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var boolean) && boolean.ValueKind == JsonValueKind.True;
    internal static string Bound(string value, int maximum) => value.Length <= maximum ? value : value[..(maximum - 36)] + "\n[gekürzt; Beleg im Laufjournal]";
    internal static bool SamePath(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static JsonDocument? Parse(string value) { try { return JsonDocument.Parse(value); } catch (JsonException) { return null; } }
    internal static string ArgumentHash(JsonElement args)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, args);
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }
    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(static item => item.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        { writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item); writer.WriteEndArray(); }
        else if (value.ValueKind == JsonValueKind.Undefined) writer.WriteNullValue();
        else value.WriteTo(writer);
    }
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
