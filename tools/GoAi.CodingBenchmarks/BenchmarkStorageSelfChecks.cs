using System.Text.Json;

namespace GoAi.CodingBenchmarks;

/// <summary>Isolated real file-system checks; never model measurements.</summary>
internal static class BenchmarkStorageSelfChecks
{
    private sealed record StoredState(int Revision, string Text);

    internal static async Task<IReadOnlyList<string>> RunAsync(string root, CancellationToken token)
    {
        var directory = Path.GetFullPath(Path.Combine(root, "storage-unit-checks"));
        BenchmarkStorage.RejectLinkedParents(directory);
        if (Directory.Exists(directory)) throw new IOException("Storage checks require a fresh directory: " + directory);
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Storage self-check failed: " + name);
            checks.Add(name);
        }

        var oldState = new StoredState(1, "Complete original: Grüße 🌍");
        var replacement = new StoredState(2, string.Concat(Enumerable.Repeat("Complete replacement: Grüße 🌍\n", 2000)));
        var path = Path.Combine(directory, "job.json");
        await BenchmarkStorage.SaveAsync(path, oldState, token).ConfigureAwait(false);
        var unrelated = path + ".tmp-unrelated";
        await File.WriteAllTextAsync(unrelated, "belongs to another writer", token).ConfigureAwait(false);

        if (OperatingSystem.IsWindows())
        {
            Task pending;
            // Deliberately reproduce an external reader that permits reads but denies deletion/rename.
            using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                pending = BenchmarkStorage.SaveAsync(path, replacement, token);
                await WaitForPreparedReplacementAsync(directory, "job.json", pending, token).ConfigureAwait(false);
                await Task.Delay(150, token).ConfigureAwait(false);
                Check(!pending.IsCompleted && BenchmarkStorage.Read<StoredState>(path) == oldState,
                    "blocked Windows replacement keeps the complete old checkpoint until its reader releases it");
            }
            await pending.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            Check(BenchmarkStorage.Read<StoredState>(path) == replacement
                && Directory.EnumerateFiles(directory, "job.json.tmp-*").SequenceEqual([unrelated]),
                "released Windows reader permits a complete atomic replacement without partial or owned temporary files");

            using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                var cancelledSave = BenchmarkStorage.SaveAsync(path, oldState, cancellation.Token);
                await WaitForPreparedReplacementAsync(directory, "job.json", cancelledSave, token).ConfigureAwait(false);
                await cancellation.CancelAsync().ConfigureAwait(false);
                var cancelled = false;
                try { await cancelledSave.ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { cancelled = true; }
                Check(cancelled && BenchmarkStorage.Read<StoredState>(path) == replacement
                    && Directory.EnumerateFiles(directory, "job.json.tmp-*").SequenceEqual([unrelated]),
                    "cancellation keeps the original checkpoint and removes only this save's exact temporary file");
            }
        }
        else
        {
            await BenchmarkStorage.SaveAsync(path, replacement, token).ConfigureAwait(false);
            Check(BenchmarkStorage.Read<StoredState>(path) == replacement,
                "complete UTF-8 checkpoint replacement succeeds; Windows-only sharing-lock checks not applicable");
        }

        var deniedTarget = Path.Combine(directory, "directory-instead-of-file.json");
        Directory.CreateDirectory(deniedTarget);
        IOException? failure = null;
        try { await BenchmarkStorage.SaveAsync(deniedTarget, oldState, token).ConfigureAwait(false); }
        catch (IOException exception) { failure = exception; }
        Check(failure is not null && failure.Message.Contains(deniedTarget, StringComparison.Ordinal)
            && failure.InnerException is IOException or UnauthorizedAccessException
            && !Directory.EnumerateFiles(directory, "directory-instead-of-file.json.tmp-*").Any(),
            "permanent replacement failure names the full destination and preserves its original exception without temporary leftovers");
        Check(await File.ReadAllTextAsync(unrelated, token).ConfigureAwait(false) == "belongs to another writer",
            "cleanup never deletes a different writer's temporary file");
        return checks;
    }

    private static async Task WaitForPreparedReplacementAsync(string directory, string fileName, Task save, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (save.IsCompleted)
            {
                await save.ConfigureAwait(false);
                throw new InvalidOperationException("Expected a Windows reader to block atomic checkpoint replacement.");
            }
            foreach (var temporary in Directory.EnumerateFiles(directory, fileName + ".tmp-*"))
            {
                if (temporary.EndsWith(".tmp-unrelated", StringComparison.Ordinal)) continue;
                try
                {
                    if (BenchmarkStorage.Read<StoredState>(temporary) is not null) return;
                }
                catch (IOException) { }
                catch (JsonException) { }
            }
            await Task.Delay(10, timeout.Token).ConfigureAwait(false);
        }
    }
}
