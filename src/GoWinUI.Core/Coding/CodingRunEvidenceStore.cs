using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GoWinUI.Core.Coding;

public sealed record CodingEvidenceReference(string EvidenceId, string Tool, long StoredBytes, bool Truncated, string? Notice);
public sealed record CodingEvidencePage(string EvidenceId, string Stream, string Text, int Offset, int NextOffset,
    bool HasMore, int StoredCharacters, bool Truncated, string? Notice, string? SourceRunId = null, bool Historical = false);
public sealed record CodingEvidenceMatch(string EvidenceId, string Tool, string Stream, int Offset, string Text);
public sealed record CodingEvidenceSearch(IReadOnlyList<CodingEvidenceMatch> Matches, bool Truncated, string? Notice);

/// <summary>Host-scoped, bounded evidence outside the project. Explicit historical references can be read
/// only within the host-selected session; model arguments never select a session, run or path.</summary>
public sealed class CodingRunEvidenceStore
{
    public const long MaximumStepBytes = 8L * 1024 * 1024;
    public const long MaximumRunBytes = 256L * 1024 * 1024;
    private const int MetadataReserve = 4096;
    private static readonly string[] Streams = ["stdout", "stderr", "input", "result"];
    private static readonly SearchValues<char> EvidenceIdCharacters = SearchValues.Create("0123456789abcdef");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<string, RunGate> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _directory;
    private readonly string _dataDirectory;
    private readonly RunGate _gate;
    private readonly long _stepLimit;
    private readonly long _runLimit;

    public CodingRunEvidenceStore(string dataDirectory, Guid sessionId, string rootRunId,
        long maximumStepBytes = MaximumStepBytes, long maximumRunBytes = MaximumRunBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ValidateRunId(rootRunId);
        if (sessionId == Guid.Empty) throw new ArgumentException("Die Sitzung für Werkzeugbelege fehlt.", nameof(sessionId));
        if (maximumStepBytes is < MetadataReserve + 1 or > MaximumStepBytes) throw new ArgumentOutOfRangeException(nameof(maximumStepBytes));
        if (maximumRunBytes < maximumStepBytes + MetadataReserve || maximumRunBytes > MaximumRunBytes) throw new ArgumentOutOfRangeException(nameof(maximumRunBytes));
        SessionId = sessionId;
        RootRunId = rootRunId;
        _stepLimit = maximumStepBytes;
        _runLimit = maximumRunBytes;
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _directory = Path.Combine(_dataDirectory, "Cache", "CodingRuns", rootRunId);
        _gate = Gates.GetOrAdd(_directory, static _ => new RunGate());
    }

    public Guid SessionId { get; }
    public string RootRunId { get; }

    public CodingEvidenceCapture BeginStep(string proposalId, string tool, JsonElement arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        if (tool.Length > 128) throw new ArgumentException("Der Werkzeugname ist zu lang.", nameof(tool));
        var id = "ev-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(RootRunId + "\n" + proposalId)))[..32];
        lock (_gate)
        {
            EnsureScope(create: true);
            var existing = LoadMetadata(id);
            if (existing is not null)
            {
                if (existing.Tool != tool) throw new InvalidDataException("Die Proposal-ID ist bereits einem anderen Werkzeugbeleg zugeordnet.");
                return new CodingEvidenceCapture(this, existing, writable: false);
            }
            // Recount when opening a step so independent store instances share the same disk quota.
            _gate.ReservedBytes = CalculateReservedBytes();
            if (_gate.ReservedBytes + MetadataReserve > _runLimit)
                return new CodingEvidenceCapture(this, new(id, tool, 0, true, "Das Beleglimit dieses Laufs ist erreicht; diese Ausgabe wurde nicht gespeichert.", true), writable: false);
            var metadata = new EvidenceMetadata(id, tool, 0, false, null, false);
            SaveMetadata(metadata);
            _gate.ReservedBytes += MetadataReserve;
            var capture = new CodingEvidenceCapture(this, metadata, writable: true);
            capture.Append("input", arguments.GetRawText());
            return capture;
        }
    }

    public Task<CodingEvidenceReference> RecordAsync(string proposalId, string tool, JsonElement arguments,
        JsonElement result, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            using var capture = BeginStep(proposalId, tool, arguments);
            var existing = LoadMetadata(capture.Reference.EvidenceId);
            if (!capture.IsWritable && existing is not null && !File.Exists(Path.Combine(_directory, existing.EvidenceId + ".result.txt")))
            {
                _gate.ReservedBytes = CalculateReservedBytes();
                using var completion = new CodingEvidenceCapture(this, existing, writable: true);
                completion.SetResult(result);
                return Task.FromResult(completion.Reference);
            }
            capture.SetResult(result);
            return Task.FromResult(capture.Reference);
        }
    }

    public Task<CodingEvidencePage> ReadOutputAsync(string evidenceId, string stream = "stdout", int offset = 0,
        int maximumCharacters = 16000, CancellationToken cancellationToken = default)
    {
        ValidateEvidenceId(evidenceId);
        ValidateStream(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (maximumCharacters is < 1 or > 32000) throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        cancellationToken.ThrowIfCancellationRequested();
        var page = TryReadOutput(evidenceId, stream, offset, maximumCharacters, cancellationToken);
        if (page is not null) return Task.FromResult(page);

        // A subsequent user turn gets a new run ID, while its restored chat history
        // still contains exact ev-references. Resolve only that requested reference,
        // without copying old outputs into the new run or broadening run searches.
        var root = Path.GetDirectoryName(_directory)!;
        RejectLinks(root);
        if (Directory.Exists(root))
        {
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(directory, _directory, StringComparison.OrdinalIgnoreCase)) continue;
                var runId = Path.GetFileName(directory);
                try { ValidateRunId(runId); }
                catch (ArgumentException) { continue; }
                var source = new CodingRunEvidenceStore(_dataDirectory, SessionId, runId);
                lock (source._gate)
                {
                    // Check scope before reading any evidence contents. Unrelated,
                    // corrupt and linked stores are not searchable from this session.
                    try { source.EnsureScope(create: false); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
                    { continue; }
                    page = source.TryReadOutput(evidenceId, stream, offset, maximumCharacters, cancellationToken);
                }
                if (page is null) continue;
                return Task.FromResult(page with
                {
                    Historical = true,
                    Notice = string.Join(" ", "Gespeicherter Originalbeleg eines früheren Laufs derselben Sitzung; kein Nachweis des aktuellen Dateistands.", page.Notice).Trim(),
                });
            }
        }
        throw new FileNotFoundException("Dieser Beleg ist weder im aktuellen Lauf noch in den gespeicherten Läufen dieser Sitzung verfügbar. Nutze coding.searchRunEvidence für aktuelle Belege oder coding.searchHistory für den gespeicherten Sitzungsstand; erfinde keine Beleg-ID.");
    }

    private CodingEvidencePage? TryReadOutput(string evidenceId, string stream, int offset, int maximumCharacters,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(_directory)) return null;
            EnsureScope(create: false);
            var metadata = LoadMetadata(evidenceId);
            if (metadata is null) return null;
            var text = ReadStream(evidenceId, stream);
            var start = Math.Min(offset, text.Length);
            // Offsets are UTF-16, as in the model's JSON; do not split a surrogate pair.
            if (start > 0 && start < text.Length && char.IsLowSurrogate(text[start]) && char.IsHighSurrogate(text[start - 1])) start--;
            var end = Math.Min(text.Length, start + maximumCharacters);
            if (end < text.Length && end > start && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end])) end--;
            if (end == start && end < text.Length) end = Math.Min(text.Length, end + 2);
            return new CodingEvidencePage(evidenceId, stream, text[start..end], start, end, end < text.Length,
                text.Length, metadata.Truncated, metadata.Notice, RootRunId);
        }
    }

    public Task<CodingEvidenceSearch> SearchRunEvidenceAsync(string query, int maximumResults = 8, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 512) throw new ArgumentException("Die Suchphrase muss 1 bis 512 Zeichen enthalten.", nameof(query));
        if (maximumResults is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(maximumResults));
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(_directory)) return Task.FromResult(new CodingEvidenceSearch([], false, null));
            EnsureScope(create: false);
            var matches = new List<CodingEvidenceMatch>();
            var truncated = false;
            foreach (var path in Directory.EnumerateFiles(_directory, "ev-*.json").Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = Path.GetFileNameWithoutExtension(path);
                var metadata = LoadMetadata(id)!;
                truncated |= metadata.Truncated;
                foreach (var stream in Streams)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var text = ReadStream(id, stream);
                    for (var from = 0; from < text.Length;)
                    {
                        var index = text.IndexOf(query, from, StringComparison.OrdinalIgnoreCase);
                        if (index < 0) break;
                        if (matches.Count == maximumResults)
                            return Task.FromResult(new CodingEvidenceSearch(matches, true, "Weitere Treffer oder gespeicherte Ausgaben sind begrenzt. Lies die gefundenen Belege gezielt nach."));
                        var start = Math.Max(0, index - 100);
                        var end = Math.Min(text.Length, index + query.Length + 200);
                        matches.Add(new(id, metadata.Tool, stream, start, text[start..end]));
                        from = index + query.Length;
                    }
                }
            }
            return Task.FromResult(new CodingEvidenceSearch(matches, truncated, truncated ? "Mindestens ein Beleg wurde am Speicherlimit gekürzt." : null));
        }
    }

    public static Task DeleteSessionAsync(string dataDirectory, Guid sessionId, CancellationToken cancellationToken = default)
    {
        var root = Path.Combine(Path.GetFullPath(dataDirectory), "Cache", "CodingRuns");
        if (!Directory.Exists(root)) return Task.CompletedTask;
        RejectLinks(root);
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (Gates.GetOrAdd(directory, static _ => new RunGate()))
            {
                RejectLinks(directory);
                var manifest = Path.Combine(directory, "scope.json");
                if (!File.Exists(manifest)) continue;
                RejectLinks(manifest);
                if (new FileInfo(manifest).Length > MetadataReserve) continue;
                var scope = JsonSerializer.Deserialize<EvidenceScope>(File.ReadAllText(manifest), Json);
                if (scope?.SessionId != sessionId || scope.RootRunId != Path.GetFileName(directory)) continue;
                // Only flat store-owned files are removable. Never follow a link or remove a nested project.
                var files = Directory.EnumerateFiles(directory).ToArray();
                if (Directory.EnumerateDirectories(directory).Any()) continue;
                if (files.Any(file => !IsOwnedFile(Path.GetFileName(file)))) continue;
                foreach (var file in files) RejectLinks(file);
                foreach (var file in files) File.Delete(file);
                Directory.Delete(directory);
            }
        }
        return Task.CompletedTask;
    }

    private void EnsureScope(bool create)
    {
        RejectLinks(_directory);
        if (create) Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "scope.json");
        RejectLinks(path);
        if (!File.Exists(path))
        {
            if (!create) throw new FileNotFoundException("Für diesen Lauf wurden noch keine Werkzeugbelege gespeichert.");
            File.WriteAllText(path, JsonSerializer.Serialize(new EvidenceScope(SessionId, RootRunId), Json));
        }
        if (new FileInfo(path).Length > MetadataReserve) throw new InvalidDataException("Die Sitzung des Werkzeugbelegspeichers ist beschädigt.");
        var scope = JsonSerializer.Deserialize<EvidenceScope>(File.ReadAllText(path), Json);
        if (scope?.SessionId != SessionId || scope.RootRunId != RootRunId)
            throw new UnauthorizedAccessException("Der Belegspeicher gehört nicht zu dieser Sitzung und diesem Lauf.");
    }

    private long CalculateReservedBytes() => MetadataReserve + Directory.EnumerateFiles(_directory).Sum(path =>
    {
        RejectLinks(path);
        return Path.GetExtension(path) == ".json" ? path.EndsWith("scope.json", StringComparison.Ordinal) ? 0 : MetadataReserve : new FileInfo(path).Length;
    });

    private EvidenceMetadata? LoadMetadata(string id)
    {
        ValidateEvidenceId(id);
        var path = Path.Combine(_directory, id + ".json");
        RejectLinks(path);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > MetadataReserve) throw new InvalidDataException("Der Werkzeugbeleg ist beschädigt.");
        var result = JsonSerializer.Deserialize<EvidenceMetadata>(File.ReadAllText(path), Json);
        if (result is null || result.EvidenceId != id || result.StoredBytes < 0 || result.StoredBytes > MaximumStepBytes)
            throw new InvalidDataException("Der Werkzeugbeleg ist beschädigt.");
        return result;
    }

    private void SaveMetadata(EvidenceMetadata metadata)
    {
        var path = Path.Combine(_directory, metadata.EvidenceId + ".json");
        RejectLinks(path);
        var temporary = path + ".tmp";
        RejectLinks(temporary);
        File.WriteAllText(temporary, JsonSerializer.Serialize(metadata, Json));
        File.Move(temporary, path, overwrite: true);
    }

    private string ReadStream(string id, string stream)
    {
        var path = Path.Combine(_directory, id + "." + stream + ".txt");
        RejectLinks(path);
        if (!File.Exists(path)) return string.Empty;
        if (new FileInfo(path).Length > MaximumStepBytes) throw new InvalidDataException("Die gespeicherte Ausgabe überschreitet das Beleglimit.");
        return File.ReadAllText(path, Encoding.UTF8);
    }

    private static void ValidateRunId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException("Die Laufkennung ist ungültig.", nameof(id));
    }

    private static void ValidateEvidenceId(string id)
    {
        if (id is null || id.Length != 35 || !id.StartsWith("ev-", StringComparison.Ordinal) || id.AsSpan(3).ContainsAnyExcept(EvidenceIdCharacters))
            throw new ArgumentException("Die Belegkennung ist ungültig.", nameof(id));
    }

    private static void ValidateStream(string stream)
    {
        if (!Streams.Contains(stream, StringComparer.Ordinal)) throw new ArgumentException("Der Ausgabekanal ist ungültig.", nameof(stream));
    }

    private static bool IsOwnedFile(string name)
    {
        if (name == "scope.json") return true;
        if (name.Length < 40) return false;
        try { ValidateEvidenceId(name[..35]); }
        catch (ArgumentException) { return false; }
        var suffix = name[35..];
        return suffix is ".json" or ".json.tmp" || Streams.Any(stream => suffix == "." + stream + ".txt");
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Verknüpfungen sind im Werkzeugbelegspeicher nicht zulässig.");
    }

    private sealed class RunGate { public long ReservedBytes { get; set; } }

    private sealed record EvidenceScope(Guid SessionId, string RootRunId);
    internal sealed record EvidenceMetadata(string EvidenceId, string Tool, long StoredBytes, bool Truncated, string? Notice, bool Completed);

    public sealed class CodingEvidenceCapture : IDisposable
    {
        private readonly CodingRunEvidenceStore _store;
        private readonly bool _writable;
        private readonly Dictionary<string, string> _pending = new(StringComparer.Ordinal);
        private EvidenceMetadata _metadata;
        private bool _resultWritten;
        private bool _disposed;
        private bool _failed;
        private bool _capacityReached;

        internal CodingEvidenceCapture(CodingRunEvidenceStore store, EvidenceMetadata metadata, bool writable)
        { _store = store; _metadata = metadata; _writable = writable; }

        public CodingEvidenceReference Reference => new(_metadata.EvidenceId, _metadata.Tool, _metadata.StoredBytes, _metadata.Truncated, _metadata.Notice);
        internal bool IsWritable => _writable;

        public void Append(string stream, string text)
        {
            ValidateStream(stream);
            if (!_writable || _disposed || _failed || _capacityReached || text.Length == 0) return;
            try
            {
            lock (_store._gate)
            {
                text = _pending.GetValueOrDefault(stream, string.Empty) + text;
                _pending.Remove(stream);
                if (char.IsHighSurrogate(text[^1])) { _pending[stream] = text[^1..]; text = text[..^1]; }
                if (text.Length == 0) return;
                var room = Math.Min(_store._stepLimit - MetadataReserve - _metadata.StoredBytes, _store._runLimit - _store._gate.ReservedBytes);
                var bytes = new byte[(int)Math.Max(0, Math.Min(room, Encoding.UTF8.GetMaxByteCount(text.Length)))];
                var charsUsed = 0;
                var bytesUsed = 0;
                if (bytes.Length >= 4)
                    Encoding.UTF8.GetEncoder().Convert(text.AsSpan(), bytes.AsSpan(), true, out charsUsed, out bytesUsed, out _);
                if (bytesUsed > 0)
                {
                    var path = Path.Combine(_store._directory, _metadata.EvidenceId + "." + stream + ".txt");
                    RejectLinks(path);
                    using var output = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                    output.Write(bytes.AsSpan(0, bytesUsed));
                    _store._gate.ReservedBytes += bytesUsed;
                    _metadata = _metadata with { StoredBytes = _metadata.StoredBytes + bytesUsed };
                }
                if (charsUsed < text.Length)
                {
                    _capacityReached = true;
                    _metadata = _metadata with { Truncated = true, Notice = "Die Ausgabe wurde am Beleglimit gekürzt (8 MiB pro Schritt, 256 MiB pro Lauf). Gespeichert ist nur der Anfang." };
                }
                _store.SaveMetadata(_metadata);
            }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A logging failure must never turn an already-applied write into a retryable tool failure.
                _failed = true;
                _metadata = _metadata with { Truncated = true, Notice = "Der Werkzeugbeleg konnte nicht vollständig gespeichert werden (Speicherzugriff fehlgeschlagen)." };
            }
        }

        public void SetResult(JsonElement result)
        {
            if (_resultWritten) return;
            _resultWritten = true;
            Append("result", result.GetRawText());
        }

        public void Dispose()
        {
            if (_disposed) return;
            lock (_store._gate)
            {
                foreach (var stream in _pending.Keys.ToArray()) { _pending.Remove(stream); Append(stream, "\uFFFD"); }
                if (_writable && !_failed)
                {
                    _metadata = _metadata with { Completed = true };
                    try { _store.SaveMetadata(_metadata); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    { _metadata = _metadata with { Truncated = true, Notice = "Der Abschluss des Werkzeugbelegs konnte nicht gespeichert werden." }; }
                }
                _disposed = true;
            }
        }
    }
}
