using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace GoWinUI.Core.Coding;

/// <summary>Automatically executed file and process tools with validated arguments and a user-selected project.</summary>
public sealed class LocalCodingToolExecutor
{
    public const int MaximumOutputCharacters = 12_000;
    private const int MaximumFileBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".venv", "venv", "node_modules", "bin", "obj", "__pycache__", ".next", "dist",
    };
    private readonly string _root;
    private readonly string _rootPrefix;
    private readonly Func<CodingCommandProgress, Task>? _commandProgress;
    private readonly StringComparison _pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public LocalCodingToolExecutor(string workspaceRoot, Func<CodingCommandProgress, Task>? commandProgress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        _rootPrefix = Path.EndsInDirectorySeparator(_root) ? _root : _root + Path.DirectorySeparatorChar;
        _commandProgress = commandProgress;
        if (!Directory.Exists(_root)) throw new DirectoryNotFoundException("Der Coding-Projektordner existiert nicht.");
        RejectReparsePoints(_root);
    }

    public async Task<JsonElement> ExecuteAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (arguments.ValueKind != JsonValueKind.Object) throw new ArgumentException("Werkzeugargumente müssen ein JSON-Objekt sein.");
        var result = toolName switch
        {
            "coding.list" => List(arguments, cancellationToken),
            "coding.search" => await SearchAsync(arguments, cancellationToken).ConfigureAwait(false),
            "coding.read" => await ReadAsync(arguments, cancellationToken).ConfigureAwait(false),
            "coding.write" => await WriteAsync(arguments, edit: false, cancellationToken).ConfigureAwait(false),
            "coding.edit" => await WriteAsync(arguments, edit: true, cancellationToken).ConfigureAwait(false),
            "coding.command" => await CommandAsync(arguments, cancellationToken).ConfigureAwait(false),
            "coding.gitDiff" => await GitDiffAsync(arguments, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentException($"Unbekanntes Coding-Werkzeug: {toolName}."),
        };
        if (result.GetRawText().Length <= MaximumOutputCharacters) return result;
        var raw = result.GetRawText();
        return Serialize(new
        {
            success = !result.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.False,
            truncated = true,
            originalCharacters = raw.Length,
            sha256 = result.TryGetProperty("sha256", out var hash) ? hash.GetString() : null,
            preview = raw[..1_200],
            message = "Ausgabe am JSON-Größenlimit gekürzt. Fordere einen kleineren Ausschnitt an.",
        });
    }

    private JsonElement List(JsonElement args, CancellationToken cancellationToken)
    {
        var directory = ResolvePath(OptionalString(args, "path") ?? ".");
        var maximum = Integer(args, "maximumEntries", 100, 1, 200);
        var entries = new List<object>();
        var totalCharacters = 0;
        var truncated = false;
        foreach (var path in EnumerateFiles(directory, cancellationToken))
        {
            var relative = Relative(path);
            if (entries.Count == maximum || totalCharacters + relative.Length > 8_000) { truncated = true; break; }
            entries.Add(new { path = relative, bytes = new FileInfo(path).Length });
            totalCharacters += relative.Length;
        }
        return Serialize(new { entries, truncated, ignoredDirectories = IgnoredDirectories.Order().ToArray() });
    }

    private async Task<JsonElement> SearchAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var query = RequiredString(args, "query", 1, 512);
        var root = ResolvePath(OptionalString(args, "path") ?? ".");
        var maximum = Integer(args, "maximumResults", 25, 1, 50);
        var matches = new List<object>();
        var outputSize = 0;
        var searchedFiles = 0;
        var truncated = false;
        foreach (var path in EnumerateFiles(root, cancellationToken))
        {
            if (++searchedFiles > 5_000) { truncated = true; break; }
            var bytes = await TryReadTextFileAsync(path, cancellationToken).ConfigureAwait(false);
            if (bytes is null) continue;
            string text;
            try { text = Decode(bytes); }
            catch (InvalidDataException) { continue; }
            var lines = text.Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var matchIndex = lines[index].IndexOf(query, StringComparison.OrdinalIgnoreCase);
                if (matchIndex < 0) continue;
                var start = Math.Max(0, matchIndex - 120);
                var preview = lines[index].Substring(start, Math.Min(300, lines[index].Length - start)).TrimEnd('\r');
                var relative = Relative(path);
                if (matches.Count == maximum || outputSize + preview.Length + relative.Length > 7_000) { truncated = true; break; }
                matches.Add(new { path = relative, line = index + 1, text = preview });
                outputSize += preview.Length + relative.Length;
            }
            if (truncated) break;
        }
        return Serialize(new { query, matches, searchedFiles, truncated });
    }

    private async Task<JsonElement> ReadAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var path = ResolvePath(RequiredString(args, "path", 1, 1_024));
        var bytes = await ReadTextFileAsync(path, cancellationToken).ConfigureAwait(false);
        var text = Decode(bytes);
        var lines = text.Split('\n');
        var start = Integer(args, "startLine", 1, 1, 1_000_000);
        var maximum = Integer(args, "maximumLines", 120, 1, 200);
        var content = new StringBuilder();
        var next = start - 1;
        while (next < lines.Length && next < start - 1 + maximum)
        {
            var line = $"{next + 1}: {lines[next].TrimEnd('\r')}";
            if (content.Length + line.Length > 8_000)
            {
                if (content.Length == 0)
                    throw new InvalidDataException("Diese Einzelzeile überschreitet das Leselimit. Nutze coding.search für Trefferfenster oder einen gezielten Prozessaufruf.");
                break;
            }
            content.AppendLine(line);
            next++;
        }
        return Serialize(new
        {
            path = Relative(path), sha256 = Hash(bytes), totalLines = lines.Length,
            startLine = start, nextLine = next < lines.Length ? (int?)next + 1 : null,
            truncated = next < lines.Length, content = content.ToString(),
        });
    }

    private async Task<JsonElement> WriteAsync(JsonElement args, bool edit, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        var path = ResolvePath(RequiredString(args, "path", 1, 1_024), mutation: true);
        var exists = File.Exists(path);
        var original = exists ? await ReadTextFileAsync(path, cancellationToken).ConfigureAwait(false) : null;
        var expected = OptionalString(args, "expectedSha256");
        if (exists && (expected is null || !string.Equals(expected, Hash(original!), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Die Datei wurde verändert oder expectedSha256 fehlt. Lies die aktuelle Datei vor der Änderung erneut.");
        if (!exists && (edit || expected is not null))
            throw new FileNotFoundException("Die erwartete vorhandene Datei existiert nicht.", path);
        var text = edit ? ApplyEdits(Decode(original!), args) : RequiredString(args, "content", 0, 16_000);
        var bytes = Encode(text, original);
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("Die Änderung überschreitet das Dateigrößenlimit.");
        var relative = Relative(path);
        var diff = CodingUnifiedDiff.Create(relative, original, bytes, cancellationToken);
        var originalHash = original is null ? null : Hash(original);
        var updatedHash = Hash(bytes);
        JsonElement Receipt(string phase, string diffText, bool diffTruncated, string? progressError = null) => Serialize(new
        {
            success = phase == "applied", phase, path = relative, sha256 = updatedHash, originalSha256 = originalHash,
            bytes = bytes.Length, created = !exists, applied = phase == "applied", diff = diffText,
            diffTruncated, addedLines = diff.AddedLines, removedLines = diff.RemovedLines, progressError,
        });
        if (_commandProgress is not null)
            await _commandProgress(new(string.Empty, string.Empty, diff.Truncated, (int)started.Elapsed.TotalMilliseconds,
                Receipt("preview", diff.Text, diff.Truncated).GetRawText())).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var parent = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent);
        _ = ResolvePath(Relative(path), mutation: true);
        var temporary = Path.Combine(parent, $".go-coding-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (exists && (!File.Exists(path) || !string.Equals(Hash(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false)), expected, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Die Datei wurde während der Änderung extern verändert; die Änderung wurde verworfen.");
            _ = ResolvePath(relative, mutation: true);
            File.Move(temporary, path, overwrite: exists);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        string? progressError = null;
        if (_commandProgress is not null)
        {
            try
            {
                await _commandProgress(new(string.Empty, string.Empty, diff.Truncated, (int)started.Elapsed.TotalMilliseconds,
                    Receipt("applied", diff.Text, diff.Truncated).GetRawText())).ConfigureAwait(false);
            }
            // The atomic write has committed. Reporting it as failed could cause a duplicate
            // mutation on retry; preserve the successful receipt and surface the delivery error.
            catch (Exception exception) when (exception is InvalidOperationException or IOException or OperationCanceledException
                or System.Data.Common.DbException or JsonException)
            {
                progressError = exception.Message[..Math.Min(exception.Message.Length, 512)];
            }
        }
        var receipt = Receipt("applied", diff.Text, diff.Truncated, progressError);
        // Full bounded UI output was delivered above. Keep the model receipt within its existing
        // JSON budget, with hashes and applied state intact, and explicitly mark omitted diff data.
        return receipt.GetRawText().Length <= MaximumOutputCharacters ? receipt
            : Receipt("applied", string.Empty, true, progressError);
    }

    private static string ApplyEdits(string original, JsonElement args)
    {
        var hasBatch = args.TryGetProperty("edits", out var batch);
        if (hasBatch && (args.TryGetProperty("oldText", out _) || args.TryGetProperty("newText", out _)))
            throw new ArgumentException("Verwende entweder edits oder oldText/newText, niemals beide Formen.");
        if (hasBatch && (batch.ValueKind != JsonValueKind.Array || batch.GetArrayLength() is < 1 or > 100))
            throw new ArgumentException("edits muss ein Array mit 1 bis 100 Ersetzungen sein.");
        JsonElement[] operations = hasBatch ? batch.EnumerateArray().ToArray() : [args];
        var replacements = new List<(int Start, int Length, string NewText)>(operations.Length);
        var totalCharacters = 0;
        // coding.read omits CR characters. Accept that representation for uniform
        // CRLF files while resolving every edit against this same original text.
        var newline = DetectUniformNewline(original);
        foreach (var operation in operations)
        {
            if (operation.ValueKind != JsonValueKind.Object
                || (hasBatch && (operation.EnumerateObject().Count() != 2
                    || operation.EnumerateObject().Any(property => property.Name is not ("oldText" or "newText")))))
                throw new ArgumentException("Jede Ersetzung muss ausschließlich oldText und newText enthalten.");
            var oldText = RequiredString(operation, "oldText", 1, 16_000);
            var newText = RequiredString(operation, "newText", 0, 16_000);
            totalCharacters += oldText.Length + newText.Length;
            if (totalCharacters > 32_000)
                throw new ArgumentException("Alle oldText/newText-Werte dürfen zusammen höchstens 32000 Zeichen enthalten.");
            oldText = NormalizeNewlines(oldText, newline);
            newText = NormalizeNewlines(newText, newline);
            var index = original.IndexOf(oldText, StringComparison.Ordinal);
            if (index < 0 || original.IndexOf(oldText, index + 1, StringComparison.Ordinal) >= 0)
                throw new InvalidOperationException("oldText muss exakt einmal im ursprünglichen Dateiinhalt vorkommen. Lies die Datei erneut und wähle eine eindeutige Fundstelle.");
            replacements.Add((index, oldText.Length, newText));
        }
        replacements.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        for (var index = 1; index < replacements.Count; index++)
            if (replacements[index].Start < replacements[index - 1].Start + replacements[index - 1].Length)
                throw new InvalidOperationException("Die Ersetzungen überlappen sich im ursprünglichen Dateiinhalt. Es wurde nichts geändert.");
        var result = new StringBuilder(original);
        // Apply from the end so replacement lengths cannot shift an earlier match.
        for (var index = replacements.Count - 1; index >= 0; index--)
        {
            var replacement = replacements[index];
            result.Remove(replacement.Start, replacement.Length).Insert(replacement.Start, replacement.NewText);
        }
        return result.ToString();
    }

    private async Task<JsonElement> CommandAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var executable = RequiredString(args, "executable", 1, 1_024);
        if (executable.Any(char.IsControl)) throw new ArgumentException("Der Programmname enthält ungültige Zeichen.");
        if (!args.TryGetProperty("arguments", out var values) || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > 64)
            throw new ArgumentException("arguments muss ein Array mit höchstens 64 Argumenten sein.");
        var arguments = values.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= 4_000
            ? value.GetString()! : throw new ArgumentException("Ungültiges Programmargument.")).ToArray();
        var directory = ResolvePath(OptionalString(args, "workingDirectory") ?? ".");
        var seconds = Integer(args, "timeoutSeconds", 0, 0, int.MaxValue);
        return Serialize(await RunProcessAsync(executable, arguments, directory, seconds, cancellationToken, _commandProgress).ConfigureAwait(false));
    }

    private async Task<JsonElement> GitDiffAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var directory = ResolvePath(OptionalString(args, "path") ?? ".");
        var pathspec = ".";
        if (File.Exists(directory))
        {
            pathspec = Path.GetFileName(directory);
            directory = Path.GetDirectoryName(directory)!;
        }
        Func<CodingCommandProgress, Task>? GitProgress(string phase) => _commandProgress is null ? null : progress => _commandProgress(progress with
        {
            OutputJson = Serialize(new { phase, path = Relative(directory), diff = phase == "git-status" ? null : progress.Stdout,
                stdout = progress.Stdout, stderr = progress.Stderr, diffTruncated = progress.Truncated }).GetRawText(),
        });
        var status = await RunProcessAsync("git", ["-c", "core.fsmonitor=false", "--literal-pathspecs", "--no-optional-locks", "status", "--short", "--untracked-files=normal", "--ignore-submodules=all", "--", pathspec], directory, 30, cancellationToken, GitProgress("git-status")).ConfigureAwait(false);
        if (status.ExitCode != 0) return Serialize(status);
        var diff = await RunProcessAsync("git", ["-c", "core.fsmonitor=false", "--literal-pathspecs", "--no-pager", "diff", "--no-ext-diff", "--no-textconv", "--ignore-submodules=all", "HEAD", "--", pathspec], directory, 30, cancellationToken, GitProgress("git-diff")).ConfigureAwait(false);
        CodingCommandResult? stagedDiff = null;
        // Before the first commit, HEAD does not exist. Report both staged additions
        // and subsequent working-tree edits instead of silently omitting the index.
        if (diff.ExitCode != 0)
        {
            stagedDiff = await RunProcessAsync("git", ["-c", "core.fsmonitor=false", "--literal-pathspecs", "--no-pager", "diff", "--cached", "--no-ext-diff", "--no-textconv", "--ignore-submodules=all", "--", pathspec], directory, 30, cancellationToken, GitProgress("git-diff-staged")).ConfigureAwait(false);
            diff = await RunProcessAsync("git", ["-c", "core.fsmonitor=false", "--literal-pathspecs", "--no-pager", "diff", "--no-ext-diff", "--no-textconv", "--ignore-submodules=all", "--", pathspec], directory, 30, cancellationToken, GitProgress("git-diff-working")).ConfigureAwait(false);
        }
        return Serialize(new { success = status.Success && diff.Success && stagedDiff?.Success != false, status, diff, stagedDiff, note = "Unversionierte Dateien erscheinen im Status; ihr Inhalt ist nicht Bestandteil von git diff." });
    }

    private static async Task<CodingCommandResult> RunProcessAsync(string executable, string[] arguments, string directory, int seconds,
        CancellationToken cancellationToken, Func<CodingCommandProgress, Task>? commandProgress = null)
    {
        var startInfo = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        var commandName = Path.GetFileNameWithoutExtension(executable);
        if (commandName.Equals("powershell", StringComparison.OrdinalIgnoreCase) || commandName.Equals("pwsh", StringComparison.OrdinalIgnoreCase))
        {
            // PowerShell scripts used by Coding should explicitly emit UTF-8 (Console.OutputEncoding).
            // Match that contract without changing decoding for arbitrary native executables.
            startInfo.StandardOutputEncoding = Encoding.UTF8;
            startInfo.StandardErrorEncoding = Encoding.UTF8;
        }
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["CI"] = "true";
        startInfo.Environment["NO_COLOR"] = "1";
        using var process = new Process { StartInfo = startInfo };
        using var processJob = OperatingSystem.IsWindows() ? WindowsProcessJob.Create() : null;
        var started = Stopwatch.StartNew();
        process.Start();
        try { processJob?.Assign(process); }
        catch
        {
            processJob?.Dispose();
            await StopProcessAsync(process).ConfigureAwait(false);
            throw;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var outputBuffer = new CommandOutputBuffer(4_000);
        var errorBuffer = new CommandOutputBuffer(2_000);
        var stdout = DrainAsync(process.StandardOutput, outputBuffer, timeout.Token);
        var stderr = DrainAsync(process.StandardError, errorBuffer, timeout.Token);
        using var progressCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var deadline = seconds == 0 ? Task.CompletedTask
            : CancelCommandAfterAsync(timeout, seconds, progressCancellation.Token);
        var progress = commandProgress is null ? Task.CompletedTask
            : PublishCommandProgressAsync(commandProgress, outputBuffer, errorBuffer, started, progressCancellation.Token);
        var timedOut = false;
        try
        {
            var completion = process.WaitForExitAsync(timeout.Token);
            if (commandProgress is not null)
            {
                // A failed awaited callback must stop the command immediately, not after its timeout.
                if (await Task.WhenAny(completion, progress).ConfigureAwait(false) == progress)
                    await progress.ConfigureAwait(false);
            }
            await completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
        }
        finally
        {
            await progressCancellation.CancelAsync().ConfigureAwait(false);
            if (processJob is not null)
            {
                // Closing the job terminates the parent and children, including on callback failure.
                processJob.Dispose();
                await WaitForStoppedProcessAsync(process).ConfigureAwait(false);
            }
            else await StopProcessAsync(process).ConfigureAwait(false);
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
            finally
            {
                try { await progress.ConfigureAwait(false); }
                catch (OperationCanceledException) when (progressCancellation.IsCancellationRequested) { }
                finally
                {
                    try { await deadline.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (progressCancellation.IsCancellationRequested) { }
                }
            }
        }
        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new CodingCommandResult(!timedOut && process.ExitCode == 0, process.ExitCode, timedOut, output.Text, error.Text,
            output.Truncated || error.Truncated, started.ElapsedMilliseconds);
    }

    private static async Task CancelCommandAfterAsync(CancellationTokenSource commandCancellation, int seconds, CancellationToken cancellationToken)
    {
        // Task.Delay and CancelAfter have a finite timer range. Chunk explicit
        // long deadlines while keeping elapsed time monotonic across clock changes.
        var budget = TimeSpan.FromSeconds(seconds);
        var started = Stopwatch.StartNew();
        var remaining = budget;
        while (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining < TimeSpan.FromDays(1) ? remaining : TimeSpan.FromDays(1), cancellationToken).ConfigureAwait(false);
            remaining = budget - started.Elapsed;
        }
        await commandCancellation.CancelAsync().ConfigureAwait(false);
    }

    private static async Task StopProcessAsync(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        await WaitForStoppedProcessAsync(process).ConfigureAwait(false);
    }

    private static Task WaitForStoppedProcessAsync(Process process) =>
        process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

    private static async Task PublishCommandProgressAsync(Func<CodingCommandProgress, Task> publish,
        CommandOutputBuffer outputBuffer, CommandOutputBuffer errorBuffer, Stopwatch elapsed, CancellationToken cancellationToken)
    {
        string? previousOutput = null;
        string? previousError = null;
        while (true)
        {
            // Delay after each awaited publication: even a slow callback cannot cause catch-up bursts.
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            var output = outputBuffer.Snapshot();
            var error = errorBuffer.Snapshot();
            if (output.Text == previousOutput && error.Text == previousError) continue;
            cancellationToken.ThrowIfCancellationRequested();
            await publish(new(output.Text, error.Text, output.Truncated || error.Truncated,
                elapsed.ElapsedMilliseconds)).ConfigureAwait(false);
            previousOutput = output.Text;
            previousError = error.Text;
        }
    }

    private static async Task<(string Text, bool Truncated)> DrainAsync(StreamReader reader, CommandOutputBuffer output, CancellationToken cancellationToken)
    {
        var buffer = new char[2_048];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
                output.Append(buffer.AsSpan(0, count));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        return output.Snapshot();
    }

    private sealed class CommandOutputBuffer(int limit)
    {
        private const string Marker = "\n...[Ausgabe gekürzt]...\n";
        private readonly object _gate = new();
        private readonly int _headLimit = (limit - Marker.Length) / 2;
        private readonly int _tailLimit = limit - Marker.Length - (limit - Marker.Length) / 2;
        private readonly StringBuilder _head = new();
        private readonly StringBuilder _tail = new();
        private bool _truncated;

        public void Append(ReadOnlySpan<char> characters)
        {
            lock (_gate)
            {
                var headCount = Math.Min(characters.Length, Math.Max(0, _headLimit - _head.Length));
                _head.Append(characters[..headCount]);
                _tail.Append(characters[headCount..]);
                if (_tail.Length <= _tailLimit) return;
                _tail.Remove(0, _tail.Length - _tailLimit);
                _truncated = true;
            }
        }

        public (string Text, bool Truncated) Snapshot()
        {
            lock (_gate) return (_head.ToString() + (_truncated ? Marker : string.Empty) + _tail, _truncated);
        }
    }

    private static IEnumerable<string> EnumerateFiles(string root, CancellationToken cancellationToken)
    {
        if (File.Exists(root)) { yield return root; yield break; }
        var pending = new Stack<string>();
        pending.Push(root);
        var visited = 0;
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++visited > 10_000) throw new InvalidDataException("Die Suche überschreitet 10.000 Verzeichnisse. Grenze path auf ein Unterverzeichnis ein.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    if (!IgnoredDirectories.Contains(Path.GetFileName(entry))) pending.Push(entry);
                }
                else yield return entry;
            }
        }
    }

    private string ResolvePath(string relative, bool mutation = false)
    {
        if (relative.Length > 1_024 || Path.IsPathRooted(relative) || relative.Any(char.IsControl) || relative.Contains(':'))
            throw new UnauthorizedAccessException("Coding-Dateipfade müssen relativ zum ausgewählten Projektordner sein.");
        var path = Path.GetFullPath(Path.Combine(_root, relative));
        if (!string.Equals(path, _root, _pathComparison) && !path.StartsWith(_rootPrefix, _pathComparison))
            throw new UnauthorizedAccessException("Der Pfad verlässt den ausgewählten Coding-Projektordner.");
        if (mutation && Path.GetRelativePath(_root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(static part => part.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException("Coding-Dateitools dürfen .git-Interna nicht verändern.");
        RejectReparsePoints(path);
        return path;
    }

    private static void RejectReparsePoints(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new UnauthorizedAccessException("Symlinks und Verzeichnisverknüpfungen sind in Coding-Dateitools nicht erlaubt.");
        }
    }

    private static async Task<byte[]?> TryReadTextFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (new FileInfo(path).Length > MaximumFileBytes) return null;
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (bytes.Length > MaximumFileBytes || IsBinary(bytes)) return null;
            return bytes;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static async Task<byte[]> ReadTextFileAsync(string path, CancellationToken cancellationToken) =>
        await TryReadTextFileAsync(path, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidDataException("Die Datei ist nicht lesbar, binär oder größer als 2 MiB.");

    private static bool IsBinary(byte[] bytes) => bytes.AsSpan(0, Math.Min(bytes.Length, 8_192)).Contains((byte)0);
    private static string Decode(byte[] bytes)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes.AsSpan(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0)); }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Die Datei enthält kein gültiges UTF-8. Konvertiere die Zeichencodierung vor einer Coding-Dateibearbeitung.", exception);
        }
    }
    private static byte[] Encode(string text, byte[]? original) => original is not null && original.AsSpan().StartsWith(Encoding.UTF8.Preamble)
        ? [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(text)] : Encoding.UTF8.GetBytes(text);
    private static string? DetectUniformNewline(string text)
    {
        if (!text.Contains('\n')) return null;
        var withoutCrLf = text.Replace("\r\n", string.Empty, StringComparison.Ordinal);
        if (withoutCrLf.Contains('\r')) return null;
        if (!text.Contains('\r')) return "\n";
        return withoutCrLf.Contains('\n') ? null : "\r\n";
    }
    private static string NormalizeNewlines(string text, string? newline) => newline is null ? text
        : text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", newline, StringComparison.Ordinal);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static JsonElement Serialize<T>(T value) => JsonSerializer.SerializeToElement(value, JsonOptions);
    private string Relative(string path) => Path.GetRelativePath(_root, path).Replace(Path.DirectorySeparatorChar, '/');
    private static string? OptionalString(JsonElement args, string name) => args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string RequiredString(JsonElement args, string name, int minimum, int maximum) =>
        OptionalString(args, name) is { } text && text.Length >= minimum && text.Length <= maximum
            ? text : throw new ArgumentException($"{name} muss zwischen {minimum} und {maximum} Zeichen enthalten.");
    private static int Integer(JsonElement args, string name, int fallback, int minimum, int maximum) =>
        !args.TryGetProperty(name, out var value) ? fallback
            : value.TryGetInt32(out var number) && number >= minimum && number <= maximum ? number
            : throw new ArgumentException($"{name} muss zwischen {minimum} und {maximum} liegen.");

    public sealed record CodingCommandResult(bool Success, int ExitCode, bool TimedOut, string Stdout, string Stderr, bool Truncated, long ElapsedMilliseconds);
}

public sealed record CodingCommandProgress(string Stdout, string Stderr, bool Truncated, long ElapsedMilliseconds, string? OutputJson = null);
