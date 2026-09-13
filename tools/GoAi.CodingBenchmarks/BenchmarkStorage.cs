using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GoAi.CodingBenchmarks;

internal static class BenchmarkStorage
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    internal static readonly JsonSerializerOptions JsonLine = new(JsonSerializerDefaults.Web);

    internal static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static string FileDigest(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    internal static async Task SaveAsync<T>(string path, T value, CancellationToken token = default)
    {
        var target = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(target)!;
        RejectLinkedParents(directory);
        var temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
        var ownsTemporary = false;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, FileOptions.Asynchronous))
            {
                ownsTemporary = true;
                await JsonSerializer.SerializeAsync(stream, value, Json, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
            // A Windows reader without FileShare.Delete can briefly deny the atomic rename.
            // Keep the complete old file intact and retry only the same already-written replacement.
            var elapsed = Stopwatch.StartNew();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    File.Move(temporary, target, overwrite: true);
                    ownsTemporary = false;
                    return;
                }
                catch (Exception exception) when (IsTransientReplacementError(exception)
                    && !Directory.Exists(target) && elapsed.Elapsed < TimeSpan.FromSeconds(5))
                {
                    await Task.Delay(100, token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Cannot atomically save benchmark state '{target}' using '{temporary}': {exception.Message}", exception);
        }
        finally
        {
            if (ownsTemporary) await DeleteOwnedTemporaryAsync(temporary, directory).ConfigureAwait(false);
        }
    }

    internal static T? Read<T>(string path)
    {
        if (!File.Exists(path)) return default;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<T>(stream, Json);
    }

    private static bool IsTransientReplacementError(Exception exception) => OperatingSystem.IsWindows()
        && (exception is IOException or UnauthorizedAccessException) && (exception.HResult & 0xffff) is 5 or 32 or 33;

    private static async Task DeleteOwnedTemporaryAsync(string temporary, string directory)
    {
        // Only the exact CreateNew-owned sibling is eligible; never enumerate/delete other writers' files.
        if (!string.Equals(Path.GetDirectoryName(temporary), directory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Temporary benchmark state escaped its target directory.");
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                RejectLinkedParents(directory);
                File.Delete(temporary);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 9 || !IsTransientReplacementError(exception))
                {
                    Console.Error.WriteLine($"Could not remove owned temporary benchmark state '{temporary}': {exception.Message}");
                    return;
                }
                await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    internal static void RejectLinkedParents(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Benchmark paths must not traverse reparse points: " + current.FullName);
    }

    internal static async Task<string> PrepareFixtureAsync(string directory, BenchmarkTask task, bool reference, CancellationToken token)
    {
        if (Directory.Exists(directory)) throw new IOException("Fixture initialization refuses an existing directory: " + directory);
        RejectLinkedParents(directory);
        var workspace = Path.Combine(directory, "workspace");
        Directory.CreateDirectory(workspace);
        foreach (var (relative, content) in task.GetFiles(reference))
        {
            if (Path.IsPathRooted(relative) || relative.Split('/').Any(static component => component is ".." or "." or "")
                || relative.Contains('\\') || relative.Contains(':'))
                throw new InvalidDataException("Invalid benchmark fixture path: " + relative);
            var path = Path.Combine(workspace, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), token).ConfigureAwait(false);
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "independent_oracle.py"), task.Oracle, new UTF8Encoding(false), token).ConfigureAwait(false);
        return workspace;
    }
}
