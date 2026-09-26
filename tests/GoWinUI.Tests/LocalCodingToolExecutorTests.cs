using GoWinUI.Core.Coding;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GoWinUI.Tests;

public sealed class LocalCodingToolExecutorTests : IAsyncLifetime
{
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task MixedLineEndingsMatchReadRepresentationAndPreserveUntouchedBytes(string suppliedNewline)
    {
        var path = Path.Combine(_root, "mixed.cs");
        await File.WriteAllTextAsync(path, "prefix\nalpha\r\nbeta\ngamma\r\nsuffix\n", new UTF8Encoding(true));
        var read = await Execute("coding.read", new { path = "mixed.cs" });
        await Execute("coding.edit", new { path = "mixed.cs", expectedSha256 = read.GetProperty("sha256").GetString(),
            oldText = "alpha" + suppliedNewline + "beta" + suppliedNewline + "gamma", newText = "changed\nblock" });
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.True(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.Equal("prefix\nchanged\r\nblock\r\nsuffix\n", await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("absent", "text_not_found")]
    [InlineData("same\nline", "ambiguous_match")]
    public async Task FailedMixedNewlineEditExplainsRecoveryAndDoesNotWrite(string oldText, string error)
    {
        var path = Path.Combine(_root, "conflict.txt");
        const string original = "same\r\nline\nother\nsame\nline\n";
        await File.WriteAllTextAsync(path, original);
        var read = await Execute("coding.read", new { path = "conflict.txt" });
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Execute("coding.edit",
            new { path = "conflict.txt", expectedSha256 = read.GetProperty("sha256").GetString(), oldText, newText = "changed" }));
        Assert.Contains(error, failure.Message);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        var refreshed = await Execute("coding.read", new { path = "conflict.txt" });
        await Execute("coding.edit", new { path = "conflict.txt", expectedSha256 = refreshed.GetProperty("sha256").GetString(),
            oldText = "other\nsame\nline", newText = "other\nrecovered" });
        Assert.Equal("same\r\nline\nother\nrecovered\n", await File.ReadAllTextAsync(path));
    }

    private static readonly string[] OutputCommandArguments = ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Write(('x' * 20000) + 'FINAL_DIAGNOSTIC'); exit 7"];
    private static readonly string[] SleepCommandArguments = ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"];
    private static readonly string[] ChildScriptArguments = ["-NoProfile", "-NonInteractive", "-File", "spawn-child.ps1"];
    private static readonly string[] VerifyFileCommandArguments = ["-NoProfile", "-NonInteractive", "-Command", "if ([IO.File]::ReadAllText('design file.txt') -cne \"Header`nvalue = 2`nFooter`n\") { exit 7 }; [Console]::Write('FILE_VERIFIED')"];
    private static readonly string[] ProgressCommandArguments = ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Write('EARLY_STDOUT'); [Console]::Error.Write('EARLY_STDERR'); 1..8 | ForEach-Object { Start-Sleep -Milliseconds 200; [Console]::Write('tick') }; [Console]::Write('FINAL_STDOUT')"];
    private static readonly string[] LargeProgressCommandArguments = ["-NoProfile", "-NonInteractive", "-Command", "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); [Console]::Write('Grüße € 日本語' + ('x' * 16000) + 'STDOUT_TAIL'); [Console]::Error.Write(('y' * 9000) + 'STDERR_TAIL'); Start-Sleep -Milliseconds 1600"];
    private static readonly string[] RelativeExecutableArguments = ["/d", "/c", "echo RELATIVE_EXECUTABLE_OK & cd"];
    private static readonly string[] SourceFirstPaths = ["README.md", "freqai/engine.py", "freqai/__init__.py", "tests/test_engine.py"];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "go-coding-tests-" + Guid.NewGuid().ToString("N"));
    private readonly LocalCodingToolExecutor _executor;

    public LocalCodingToolExecutorTests()
    {
        Directory.CreateDirectory(_root);
        _executor = new LocalCodingToolExecutor(_root);
    }

    [Fact]
    public async Task ReadLargerPagesPreservesStructuredContentAndContinuation()
    {
        var lines = Enumerable.Range(1, 700).Select(i => $"value_{i} = \"some source text\";").ToArray();
        await File.WriteAllTextAsync(Path.Combine(_root, "large.cs"), string.Join("\n", lines));
        var first = await Execute("coding.read", new { path = "large.cs", maximumLines = 300 });
        Assert.Equal(301, first.GetProperty("nextLine").GetInt32());
        Assert.Contains("300: " + lines[299], first.GetProperty("content").GetString());
        var all = await Execute("coding.read", new { path = "large.cs" });
        Assert.False(all.GetProperty("truncated").GetBoolean());
        Assert.Contains("700: " + lines[699], all.GetProperty("content").GetString());
        Assert.Equal(first.GetProperty("sha256").GetString(), all.GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task ReadCharacterBoundaryPreservesWholeLinesAndJsonEscaping()
    {
        var line = new string('\t', 1000);
        await File.WriteAllTextAsync(Path.Combine(_root, "escaped.txt"), string.Join("\n", Enumerable.Repeat(line, 40)));
        var first = await Execute("coding.read", new { path = "escaped.txt", maximumLines = 1000 });
        Assert.False(first.GetProperty("truncated").GetBoolean());
        Assert.Contains("40: " + line, first.GetProperty("content").GetString());
        var second = await Execute("coding.read", new { path = "escaped.txt", startLine = 32 });
        Assert.StartsWith("32: " + line, second.GetProperty("content").GetString());
        Assert.False(second.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task ReadWholeFileBeyondFormerFileAndReceiptLimitsWithoutTruncation()
    {
        var line = new string('x', 3000);
        await File.WriteAllTextAsync(Path.Combine(_root, "complete.txt"), string.Join("\n", Enumerable.Repeat(line, 1100)));
        var result = await Execute("coding.read", new { path = "complete.txt" });
        Assert.False(result.GetProperty("truncated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("nextLine").ValueKind);
        Assert.Contains("1100: " + line, result.GetProperty("content").GetString());
        var selected = await Execute("coding.read", new { path = "complete.txt", startLine = 1099, maximumLines = int.MaxValue });
        Assert.StartsWith("1099: ", selected.GetProperty("content").GetString());
        Assert.False(selected.GetProperty("truncated").GetBoolean());
    }

    [Theory]
    [InlineData("new file café.txt", "first\nsecond")]
    [InlineData("empty.txt", "")]
    public async Task WriteDiffIsPreviewedBeforeMutationAndAppliedOnlyAfterSuccessfulWrite(string relative, string content)
    {
        var observed = new List<JsonElement>();
        var path = Path.Combine(_root, relative);
        var executor = new LocalCodingToolExecutor(_root, async progress =>
        {
            var output = JsonSerializer.Deserialize<JsonElement>(progress.OutputJson!);
            observed.Add(output);
            if (output.GetProperty("phase").GetString() == "preview")
            {
                Assert.False(File.Exists(path));
                Assert.False(output.GetProperty("applied").GetBoolean());
            }
            else
            {
                Assert.Equal(content, await File.ReadAllTextAsync(path));
                Assert.True(output.GetProperty("applied").GetBoolean());
            }
        });
        var result = await executor.ExecuteAsync("coding.write", JsonSerializer.SerializeToElement(new { path = relative, content }));
        Assert.Equal(2, observed.Count);
        Assert.Equal("applied", result.GetProperty("phase").GetString());
        var diff = result.GetProperty("diff").GetString()!;
        Assert.Equal(observed[0].GetProperty("diff").GetString(), diff);
        Assert.Equal(observed[1].GetProperty("diff").GetString(), diff);
        Assert.Contains("new file mode 100644", diff, StringComparison.Ordinal);
        if (content.Length > 0)
        {
            Assert.Contains("@@ -0,0 +1,2 @@", diff, StringComparison.Ordinal);
            Assert.Contains("+second\n\\ No newline at end of file", diff, StringComparison.Ordinal);
        }
        await VerifyDiffAppliesAsync(relative, null, Encoding.UTF8.GetBytes(content), diff);
    }

    [Theory]
    [InlineData("\n", false)]
    [InlineData("\r\n", true)]
    public async Task BatchDiffHasRealSeparateHunksAndRecreatesExactBytesIncludingBomAndEndOfFile(string newline, bool withBom)
    {
        var originalText = string.Join(newline, Enumerable.Range(1, 30).Select(index => $"line {index:D2}"));
        var original = Encoding.UTF8.GetBytes(originalText);
        if (withBom) original = [.. Encoding.UTF8.Preamble, .. original];
        await File.WriteAllBytesAsync(Path.Combine(_root, "hunks.txt"), original);
        var result = await Execute("coding.edit", new
        {
            path = "hunks.txt", expectedSha256 = Convert.ToHexStringLower(SHA256.HashData(original)),
            edits = new[]
            {
                new { oldText = "line 05", newText = "inserted one\ninserted two" },
                new { oldText = "line 25", newText = "changed 25" },
                new { oldText = "line 30", newText = "last line" },
            },
        });
        var diff = result.GetProperty("diff").GetString()!;
        Assert.Contains("@@ -2,7 +2,8 @@", diff, StringComparison.Ordinal);
        Assert.Contains("@@ -22,9 +23,9 @@", diff, StringComparison.Ordinal);
        Assert.Contains("-line 30\n\\ No newline at end of file", diff, StringComparison.Ordinal);
        Assert.Contains("+last line\n\\ No newline at end of file", diff, StringComparison.Ordinal);
        Assert.Equal(4, result.GetProperty("addedLines").GetInt32());
        Assert.Equal(3, result.GetProperty("removedLines").GetInt32());
        var updated = await File.ReadAllBytesAsync(Path.Combine(_root, "hunks.txt"));
        await VerifyDiffAppliesAsync("hunks.txt", original, updated, diff);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedPreviewCannotMutateOrPublishAnAppliedDiff(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var seen = new List<string>();
        var executor = new LocalCodingToolExecutor(_root, progress =>
        {
            seen.Add(JsonSerializer.Deserialize<JsonElement>(progress.OutputJson!).GetProperty("phase").GetString()!);
            if (cancel) { cancellation.Cancel(); return Task.CompletedTask; }
            throw new InvalidOperationException("Preview delivery failed.");
        });
        var operation = executor.ExecuteAsync("coding.write", JsonSerializer.SerializeToElement(new { path = "new/sub/file.txt", content = "never written" }), cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => operation);
        Assert.Equal("preview", Assert.Single(seen));
        Assert.False(Directory.Exists(Path.Combine(_root, "new")));
    }

    [Fact]
    public async Task ExternalChangeAfterPreviewDiscardsPatchAndNeverPublishesApplied()
    {
        var path = Path.Combine(_root, "conflict-preview.txt");
        await File.WriteAllTextAsync(path, "original\n");
        var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path)));
        var events = new List<string>();
        var executor = new LocalCodingToolExecutor(_root, async progress =>
        {
            events.Add(JsonSerializer.Deserialize<JsonElement>(progress.OutputJson!).GetProperty("phase").GetString()!);
            await File.WriteAllTextAsync(path, "external update\n");
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync("coding.edit", JsonSerializer.SerializeToElement(new
        {
            path = "conflict-preview.txt", expectedSha256 = hash, oldText = "original", newText = "proposed",
        })));
        Assert.Equal("preview", Assert.Single(events));
        Assert.Equal("external update\n", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(_root, ".go-coding-*.tmp"));
    }

    [Fact]
    public async Task AppliedCallbackFailureKeepsCommittedMutationAndSuccessfulReceipt()
    {
        var executor = new LocalCodingToolExecutor(_root, progress =>
        {
            if (JsonSerializer.Deserialize<JsonElement>(progress.OutputJson!).GetProperty("phase").GetString() == "applied")
                throw new InvalidOperationException("Applied delivery failed.");
            return Task.CompletedTask;
        });
        var result = await executor.ExecuteAsync("coding.write", JsonSerializer.SerializeToElement(new { path = "committed.txt", content = "committed\n" }));
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.True(result.GetProperty("applied").GetBoolean());
        Assert.Equal("Applied delivery failed.", result.GetProperty("progressError").GetString());
        Assert.Equal("committed\n", await File.ReadAllTextAsync(Path.Combine(_root, "committed.txt")));
    }

    [Fact]
    public async Task LargeDiffStaysAvailableToUiWhileModelReceiptRetainsHashesAndAppliedState()
    {
        JsonElement applied = default;
        var executor = new LocalCodingToolExecutor(_root, progress =>
        {
            var output = JsonSerializer.Deserialize<JsonElement>(progress.OutputJson!);
            if (output.GetProperty("phase").GetString() == "applied") applied = output.Clone();
            return Task.CompletedTask;
        });
        var content = new string('x', 15_000) + "\n";
        var result = await executor.ExecuteAsync("coding.write", JsonSerializer.SerializeToElement(new { path = "long.txt", content }));
        Assert.True(applied.GetProperty("diff").GetString()!.Length > LocalCodingToolExecutor.MaximumOutputCharacters);
        Assert.False(applied.GetProperty("diffTruncated").GetBoolean());
        Assert.InRange(result.GetRawText().Length, 1, LocalCodingToolExecutor.MaximumOutputCharacters);
        Assert.True(result.GetProperty("diffTruncated").GetBoolean());
        Assert.True(result.GetProperty("applied").GetBoolean());
        Assert.Equal(applied.GetProperty("sha256").GetString(), result.GetProperty("sha256").GetString());
        await VerifyDiffAppliesAsync("long.txt", null, Encoding.UTF8.GetBytes(content), applied.GetProperty("diff").GetString()!);
    }

    [Fact]
    public async Task HighlyDifferentFileUsesBoundedSearchAndStillProducesAnApplicablePatch()
    {
        var original = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(1, 6_000).Select(index => $"old {index}\n")));
        var content = string.Concat(Enumerable.Range(1, 1_000).Select(index => $"new {index}\n"));
        await File.WriteAllBytesAsync(Path.Combine(_root, "different.txt"), original);
        string? patch = null;
        var executor = new LocalCodingToolExecutor(_root, progress =>
        {
            var output = JsonSerializer.Deserialize<JsonElement>(progress.OutputJson!);
            if (output.GetProperty("phase").GetString() == "applied") patch = output.GetProperty("diff").GetString();
            return Task.CompletedTask;
        });
        var result = await executor.ExecuteAsync("coding.write", JsonSerializer.SerializeToElement(new
        {
            path = "different.txt", content, expectedSha256 = Convert.ToHexStringLower(SHA256.HashData(original)),
        }));
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.NotNull(patch);
        Assert.Contains("@@ -1,6000 +1,1000 @@", patch, StringComparison.Ordinal);
        await VerifyDiffAppliesAsync("different.txt", original, Encoding.UTF8.GetBytes(content), patch);
    }

    [Fact]
    public async Task NoOpEditReportsEmptyActualDiff()
    {
        var written = await Execute("coding.write", new { path = "same.txt", content = "same\n" });
        var edited = await Execute("coding.edit", new { path = "same.txt", oldText = "same", newText = "same", expectedSha256 = written.GetProperty("sha256").GetString() });
        Assert.Equal(string.Empty, edited.GetProperty("diff").GetString());
        Assert.Equal(0, edited.GetProperty("addedLines").GetInt32());
        Assert.Equal(0, edited.GetProperty("removedLines").GetInt32());
        Assert.Equal(written.GetProperty("sha256").GetString(), edited.GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task OversizedSingleHunkIsExplicitlyOmittedWithoutPublishingABrokenPatchHunk()
    {
        var original = Encoding.UTF8.GetBytes(new string('a', 300_000) + "\n");
        await File.WriteAllBytesAsync(Path.Combine(_root, "oversized.txt"), original);
        JsonElement applied = default;
        var executor = new LocalCodingToolExecutor(_root, progress =>
        {
            var output = JsonSerializer.Deserialize<JsonElement>(progress.OutputJson!);
            if (output.GetProperty("phase").GetString() == "applied") applied = output.Clone();
            return Task.CompletedTask;
        });
        var result = await executor.ExecuteAsync("coding.write", JsonSerializer.SerializeToElement(new
        {
            path = "oversized.txt", content = "replacement\n", expectedSha256 = Convert.ToHexStringLower(SHA256.HashData(original)),
        }));
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.True(applied.GetProperty("diffTruncated").GetBoolean());
        Assert.DoesNotContain("@@", applied.GetProperty("diff").GetString()!, StringComparison.Ordinal);
        Assert.Equal(1, applied.GetProperty("addedLines").GetInt32());
        Assert.Equal(1, applied.GetProperty("removedLines").GetInt32());
        Assert.InRange(applied.GetRawText().Length, 1, 2_097_152);
        Assert.Equal("replacement\n", await File.ReadAllTextAsync(Path.Combine(_root, "oversized.txt")));
    }

    private async Task VerifyDiffAppliesAsync(string relative, byte[]? original, byte[] expected, string patch)
    {
        var path = Path.Combine(_root, relative);
        if (original is null) File.Delete(path);
        else await File.WriteAllBytesAsync(path, original);
        var patchName = $"patch-{Guid.NewGuid():N}.diff";
        await File.WriteAllTextAsync(Path.Combine(_root, patchName), patch, new UTF8Encoding(false));
        await Git("-c", "core.autocrlf=false", "apply", "--check", "--", patchName);
        await Git("-c", "core.autocrlf=false", "apply", "--", patchName);
        Assert.Equal(expected, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ReadEditAndStaleHashPreserveExternalChanges()
    {
        await Execute("coding.write", new { path = "sample.cs", content = "before\nafter\n" });
        var read = await Execute("coding.read", new { path = "sample.cs", startLine = 1, maximumLines = 1 });
        Assert.Equal(2, read.GetProperty("nextLine").GetInt32());
        var hash = read.GetProperty("sha256").GetString();
        await Execute("coding.edit", new { path = "sample.cs", oldText = "before", newText = "changed", expectedSha256 = hash });
        Assert.Equal("changed\nafter\n", await File.ReadAllTextAsync(Path.Combine(_root, "sample.cs")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Execute("coding.write", new { path = "sample.cs", content = "overwrite", expectedSha256 = hash }));
        Assert.Equal("changed\nafter\n", await File.ReadAllTextAsync(Path.Combine(_root, "sample.cs")));
    }

    [Fact]
    public async Task NewFileCanBeListedSearchedReadReplacedAndVerifiedByCommand()
    {
        const string relative = "notes/design file.txt";
        var written = await Execute("coding.write", new { path = relative, content = "Header\nvalue = 1\nFooter\n" });
        Assert.True(written.GetProperty("created").GetBoolean());
        var listed = await Execute("coding.list", new { path = "notes" });
        Assert.Equal(relative, Assert.Single(listed.GetProperty("entries").EnumerateArray()).GetProperty("path").GetString());
        var searched = await Execute("coding.search", new { path = "notes", query = "value = 1" });
        var match = Assert.Single(searched.GetProperty("matches").EnumerateArray());
        Assert.Equal(2, match.GetProperty("line").GetInt32());
        var read = await Execute("coding.read", new { path = relative });
        Assert.Equal(written.GetProperty("sha256").GetString(), read.GetProperty("sha256").GetString());
        Assert.Contains("2: value = 1", read.GetProperty("content").GetString()!, StringComparison.Ordinal);
        var edited = await Execute("coding.edit", new
        {
            path = relative, oldText = "value = 1", newText = "value = 2", expectedSha256 = read.GetProperty("sha256").GetString(),
        });
        var verified = await Execute("coding.read", new { path = relative });
        Assert.Equal(edited.GetProperty("sha256").GetString(), verified.GetProperty("sha256").GetString());
        Assert.NotEqual(read.GetProperty("sha256").GetString(), verified.GetProperty("sha256").GetString());
        Assert.Contains("2: value = 2", verified.GetProperty("content").GetString()!, StringComparison.Ordinal);
        var command = await Execute("coding.command", new
        {
            executable = "powershell.exe", workingDirectory = "notes", timeoutSeconds = 15,
            arguments = VerifyFileCommandArguments,
        });
        Assert.True(command.GetProperty("success").GetBoolean(), command.GetRawText());
        Assert.Equal("FILE_VERIFIED", command.GetProperty("stdout").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(_root, relative)))), verified.GetProperty("sha256").GetString());
    }

    [Theory]
    [InlineData("\r\n", true)]
    [InlineData("\r\n", false)]
    [InlineData("\n", true)]
    public async Task MultilineReplacementPreservesNewlinesBomAndUnchangedSections(string newline, bool withBom)
    {
        var original = $"prefix{newline}begin{newline}  old one{newline}  old two{newline}end{newline}suffix{newline}";
        var originalBytes = Encoding.UTF8.GetBytes(original);
        if (withBom) originalBytes = [.. Encoding.UTF8.Preamble, .. originalBytes];
        var path = Path.Combine(_root, "multiline.txt");
        await File.WriteAllBytesAsync(path, originalBytes);
        var read = await Execute("coding.read", new { path = "multiline.txt" });
        await Execute("coding.edit", new
        {
            path = "multiline.txt", oldText = "begin\n  old one\n  old two\nend", newText = "begin\n  replacement café\nend",
            expectedSha256 = read.GetProperty("sha256").GetString(),
        });
        var expected = Encoding.UTF8.GetBytes($"prefix{newline}begin{newline}  replacement café{newline}end{newline}suffix{newline}");
        if (withBom) expected = [.. Encoding.UTF8.Preamble, .. expected];
        Assert.Equal(expected, await File.ReadAllBytesAsync(path));
        var reread = await Execute("coding.read", new { path = "multiline.txt" });
        Assert.Contains("3:   replacement café", reread.GetProperty("content").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaleHashCanBeCorrectedByRereadingWithoutLosingExternalChanges()
    {
        const string relative = "conflict.txt";
        await Execute("coding.write", new { path = relative, content = "keep\nvalue = old\n" });
        var stale = await Execute("coding.read", new { path = relative });
        var path = Path.Combine(_root, relative);
        var external = Encoding.UTF8.GetBytes("external addition\nkeep\nvalue = old\n");
        await File.WriteAllBytesAsync(path, external);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Execute("coding.edit", new
        {
            path = relative, oldText = "value = old", newText = "value = new", expectedSha256 = stale.GetProperty("sha256").GetString(),
        }));
        Assert.Equal(external, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.EnumerateFiles(_root, ".go-coding-*.tmp", SearchOption.AllDirectories));
        var current = await Execute("coding.read", new { path = relative });
        var updated = await Execute("coding.edit", new
        {
            path = relative, oldText = "value = old", newText = "value = new", expectedSha256 = current.GetProperty("sha256").GetString(),
        });
        Assert.Equal("external addition\nkeep\nvalue = new\n", await File.ReadAllTextAsync(path));
        Assert.Equal(updated.GetProperty("sha256").GetString(), (await Execute("coding.read", new { path = relative })).GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task OverlappingMatchesAreRejectedAndUniqueCorrectionCanDeleteText()
    {
        await Execute("coding.write", new { path = "overlap.txt", content = "ababa tail" });
        var read = await Execute("coding.read", new { path = "overlap.txt" });
        var hash = read.GetProperty("sha256").GetString();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Execute("coding.edit", new { path = "overlap.txt", oldText = "aba", newText = "wrong", expectedSha256 = hash }));
        Assert.Equal("ababa tail", await File.ReadAllTextAsync(Path.Combine(_root, "overlap.txt")));
        await Execute("coding.edit", new { path = "overlap.txt", oldText = "ababa ", newText = "", expectedSha256 = hash });
        Assert.Equal("tail", await File.ReadAllTextAsync(Path.Combine(_root, "overlap.txt")));
    }

    [Fact]
    public async Task WholeFileOverwriteRequiresCurrentHashAndReturnsUpdatedHash()
    {
        await Execute("coding.write", new { path = "replace.txt", content = "original" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Execute("coding.write", new { path = "replace.txt", content = "unapproved overwrite" }));
        var current = await Execute("coding.read", new { path = "replace.txt" });
        var written = await Execute("coding.write", new { path = "replace.txt", content = "entirely replaced\n", expectedSha256 = current.GetProperty("sha256").GetString() });
        Assert.False(written.GetProperty("created").GetBoolean());
        Assert.Equal("entirely replaced\n", await File.ReadAllTextAsync(Path.Combine(_root, "replace.txt")));
        Assert.Equal(written.GetProperty("sha256").GetString(), (await Execute("coding.read", new { path = "replace.txt" })).GetProperty("sha256").GetString());
    }

    [Theory]
    [InlineData("\n", false)]
    [InlineData("\r\n", true)]
    public async Task BatchEditsUseOriginalMatchesAndPreserveMultilineEncoding(string newline, bool withBom)
    {
        var path = Path.Combine(_root, "batch.txt");
        var original = Encoding.UTF8.GetBytes($"prefix{newline}alpha{newline}beta{newline}separator{newline}gamma{newline}delta{newline}suffix{newline}");
        if (withBom) original = [.. Encoding.UTF8.Preamble, .. original];
        await File.WriteAllBytesAsync(path, original);
        var read = await Execute("coding.read", new { path = "batch.txt" });
        var edited = await Execute("coding.edit", new
        {
            path = "batch.txt", expectedSha256 = read.GetProperty("sha256").GetString(),
            edits = new[]
            {
                new { oldText = "gamma\ndelta", newText = "result café" },
                new { oldText = "prefix", newText = "expanded prefix" },
                // These introduced strings must not be consumed by the first edit.
                new { oldText = "alpha\nbeta", newText = "gamma\ndelta\nnew line" },
                new { oldText = "suffix", newText = "" },
            },
        });
        var expected = Encoding.UTF8.GetBytes($"expanded prefix{newline}gamma{newline}delta{newline}new line{newline}separator{newline}result café{newline}{newline}");
        if (withBom) expected = [.. Encoding.UTF8.Preamble, .. expected];
        Assert.Equal(expected, await File.ReadAllBytesAsync(path));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(expected)), edited.GetProperty("sha256").GetString());
        Assert.Equal(edited.GetProperty("sha256").GetString(), (await Execute("coding.read", new { path = "batch.txt" })).GetProperty("sha256").GetString());
        Assert.Empty(Directory.EnumerateFiles(_root, ".go-coding-*.tmp"));
    }

    [Theory]
    [InlineData("abc", "bcd")]
    [InlineData("abc", "absent")]
    [InlineData("abc", "introduced")]
    [InlineData("abc", "abc")]
    [InlineData("abc", "same")]
    public async Task InvalidBatchMatchLeavesEveryOriginalByteUntouched(string first, string second)
    {
        const string original = "abcdef same same tail\n";
        var created = await Execute("coding.write", new { path = "batch-invalid.txt", content = original });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Execute("coding.edit", new
        {
            path = "batch-invalid.txt", expectedSha256 = created.GetProperty("sha256").GetString(),
            edits = new[] { new { oldText = first, newText = "introduced" }, new { oldText = second, newText = "replacement" } },
        }));
        Assert.Equal(Encoding.UTF8.GetBytes(original), await File.ReadAllBytesAsync(Path.Combine(_root, "batch-invalid.txt")));
        Assert.Empty(Directory.EnumerateFiles(_root, ".go-coding-*.tmp"));
    }

    [Fact]
    public async Task StaleBatchHashRejectsAllEditsAndRereadRetainsExternalChanges()
    {
        var created = await Execute("coding.write", new { path = "batch-stale.txt", content = "first\nsecond\n" });
        var path = Path.Combine(_root, "batch-stale.txt");
        var external = Encoding.UTF8.GetBytes("external addition\nfirst\nsecond\n");
        await File.WriteAllBytesAsync(path, external);
        var edits = new[] { new { oldText = "first", newText = "updated first" }, new { oldText = "second", newText = "updated second" } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Execute("coding.edit", new
        {
            path = "batch-stale.txt", expectedSha256 = created.GetProperty("sha256").GetString(), edits,
        }));
        Assert.Equal(external, await File.ReadAllBytesAsync(path));
        var current = await Execute("coding.read", new { path = "batch-stale.txt" });
        await Execute("coding.edit", new { path = "batch-stale.txt", expectedSha256 = current.GetProperty("sha256").GetString(), edits });
        Assert.Equal("external addition\nupdated first\nupdated second\n", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(_root, ".go-coding-*.tmp"));
    }

    [Fact]
    public async Task BatchShapeAndCombinedTextLimitsRejectWithoutMutation()
    {
        var path = Path.Combine(_root, "batch-limits.txt");
        var original = Encoding.UTF8.GetBytes(new string('a', 16_000) + new string('b', 16_000));
        await File.WriteAllBytesAsync(path, original);
        var hash = Convert.ToHexStringLower(SHA256.HashData(original));
        var validEdit = new { oldText = "ab", newText = "updated" };
        object[] invalidArguments =
        [
            new { path = "batch-limits.txt", expectedSha256 = hash, edits = Array.Empty<object>() },
            new { path = "batch-limits.txt", expectedSha256 = hash, edits = Enumerable.Repeat(validEdit, 101).ToArray() },
            new { path = "batch-limits.txt", expectedSha256 = hash, edits = new[] { validEdit }, oldText = "ab", newText = "updated" },
            new { path = "batch-limits.txt", expectedSha256 = hash, edits = new[] { validEdit }, oldText = (string?)null },
            new { path = "batch-limits.txt", expectedSha256 = hash, edits = new[] { new { oldText = "", newText = "updated" } } },
            new { path = "batch-limits.txt", expectedSha256 = hash, edits = new[] { new { oldText = "ab", newText = new string('x', 16_001) } } },
            new { path = "batch-limits.txt", expectedSha256 = hash, edits = new[] { new { oldText = "ab", newText = "updated", extra = true } } },
            new { path = "batch-limits.txt", expectedSha256 = hash, edits = new[]
            {
                new { oldText = new string('a', 16_000), newText = "x" },
                new { oldText = new string('b', 16_000), newText = "y" },
            } },
        ];
        foreach (var arguments in invalidArguments)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => Execute("coding.edit", arguments));
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        }
        Assert.Empty(Directory.EnumerateFiles(_root, ".go-coding-*.tmp"));
    }

    [Fact]
    public async Task GitDiffForOneFileIncludesItsChangesButExcludesSiblingChanges()
    {
        await Git("init", "--quiet");
        await Execute("coding.write", new { path = "target[1].txt", content = "target before\n" });
        await Execute("coding.write", new { path = "target1.txt", content = "sibling before\n" });
        await Git("add", "--all");
        await Git("-c", "user.name=GO Coding Tests", "-c", "user.email=coding-tests@example.invalid", "-c", "commit.gpgsign=false", "-c", "core.hooksPath=NUL", "commit", "--quiet", "-m", "fixture");
        var read = await Execute("coding.read", new { path = "target[1].txt" });
        await Execute("coding.edit", new { path = "target[1].txt", oldText = "target before", newText = "target after", expectedSha256 = read.GetProperty("sha256").GetString() });
        await File.WriteAllTextAsync(Path.Combine(_root, "target1.txt"), "sibling after\n");
        var result = await Execute("coding.gitDiff", new { path = "target[1].txt" });
        Assert.True(result.GetProperty("success").GetBoolean(), result.GetRawText());
        Assert.Contains("+target after", result.GetProperty("diff").GetProperty("stdout").GetString()!, StringComparison.Ordinal);
        Assert.DoesNotContain("sibling", result.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("target1.txt", result.GetProperty("status").GetProperty("stdout").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitDiffBeforeFirstCommitIncludesStagedAdditionsAndLaterEdits()
    {
        await Git("init", "--quiet");
        await Execute("coding.write", new { path = "first.txt", content = "staged initial\n" });
        await Git("add", "first.txt");
        var read = await Execute("coding.read", new { path = "first.txt" });
        await Execute("coding.edit", new { path = "first.txt", oldText = "staged initial", newText = "working update", expectedSha256 = read.GetProperty("sha256").GetString() });
        var result = await Execute("coding.gitDiff", new { });
        Assert.True(result.GetProperty("success").GetBoolean(), result.GetRawText());
        Assert.Contains("+staged initial", result.GetProperty("stagedDiff").GetProperty("stdout").GetString()!, StringComparison.Ordinal);
        Assert.Contains("+working update", result.GetProperty("diff").GetProperty("stdout").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TraversalGitMutationAndAmbiguousEditAreRejected()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Execute("coding.write", new { path = "../escape.txt", content = "no" }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Execute("coding.write", new { path = ".git/config", content = "no" }));
        await Execute("coding.write", new { path = "a.txt", content = "same same" });
        var read = await Execute("coding.read", new { path = "a.txt" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Execute("coding.edit", new { path = "a.txt", oldText = "same", newText = "different", expectedSha256 = read.GetProperty("sha256").GetString() }));
        Assert.Equal("same same", await File.ReadAllTextAsync(Path.Combine(_root, "a.txt")));
    }

    [Fact]
    public async Task SearchAndListSkipDependenciesAndReturnBoundedResults()
    {
        Directory.CreateDirectory(Path.Combine(_root, "node_modules"));
        await File.WriteAllTextAsync(Path.Combine(_root, "node_modules", "ignore.js"), "needle");
        await File.WriteAllTextAsync(Path.Combine(_root, "found.cs"), string.Join('\n', Enumerable.Repeat("needle", 100)));
        var list = await Execute("coding.list", new { });
        Assert.Single(list.GetProperty("entries").EnumerateArray());
        var search = await Execute("coding.search", new { query = "needle", maximumResults = 3 });
        Assert.Equal(3, search.GetProperty("matches").GetArrayLength());
        Assert.True(search.GetProperty("truncated").GetBoolean());
        Assert.True(search.GetRawText().Length <= LocalCodingToolExecutor.MaximumOutputCharacters);
    }

    [Fact]
    public async Task SourcePackagesPrecedeBulkDataWhenTheStructuredListHitsItsOutputBudget()
    {
        foreach (var path in SourceFirstPaths)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_root, path))!);
            await File.WriteAllTextAsync(Path.Combine(_root, path), "SOURCE_NEEDLE");
        }
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        Directory.CreateDirectory(Path.Combine(_root, "unsloth-tmp", "pytest-of-AMD", "pytest-9"));
        for (var index = 0; index < 180; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(_root, "data", $"{index:D3}-{new string('d', 32)}.txt"), "DATA_NEEDLE");
            await File.WriteAllTextAsync(Path.Combine(_root, "unsloth-tmp", "pytest-of-AMD", "pytest-9", $"cache-{index:D3}.txt"), "CACHE_NEEDLE");
        }

        var list = await Execute("coding.list", new { maximumEntries = 200 });
        var paths = list.GetProperty("entries").EnumerateArray().Select(entry => entry.GetProperty("path").GetString()!).ToArray();
        Assert.Equal(SourceFirstPaths, paths.Take(SourceFirstPaths.Length));
        Assert.True(paths.Length > SourceFirstPaths.Length);
        Assert.True(list.GetProperty("truncated").GetBoolean());
        Assert.True(list.GetRawText().Length <= LocalCodingToolExecutor.MaximumOutputCharacters);
        Assert.Equal("source-first", list.GetProperty("ordering").GetString());
        Assert.DoesNotContain(paths, path => path.StartsWith("unsloth-tmp/", StringComparison.Ordinal));
        Assert.Contains(list.GetProperty("ignoredDirectories").EnumerateArray(), value => value.GetString() == "unsloth-tmp");
        Assert.Contains(list.GetProperty("ignoredDirectoryPatterns").EnumerateArray(), value => value.GetString() == "pytest-<digits>");

        var search = await Execute("coding.search", new { query = "NEEDLE", maximumResults = 4 });
        Assert.Equal(SourceFirstPaths, search.GetProperty("matches").EnumerateArray().Select(match => match.GetProperty("path").GetString()));
        Assert.True(search.GetProperty("truncated").GetBoolean());
    }

    [Theory]
    [InlineData("unsloth-tmp/pytest-of-AMD/pytest-9")]
    [InlineData(".pytest_cache/v/cache")]
    [InlineData(".venv/Scripts")]
    [InlineData("freqai_wave_memory.egg-info")]
    [InlineData("pytest-of-AMD/pytest-9")]
    [InlineData("pytest-9")]
    public async Task GeneratedPathsAreExcludedByDefaultButExplicitListSearchAndReadRemainAvailable(string generatedPath)
    {
        var path = generatedPath + "/evidence.txt";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_root, path))!);
        await File.WriteAllTextAsync(Path.Combine(_root, path), "ACTUAL_CACHE_EVIDENCE");
        Assert.Empty((await Execute("coding.list", new { })).GetProperty("entries").EnumerateArray());
        Assert.Empty((await Execute("coding.search", new { query = "ACTUAL_CACHE_EVIDENCE" })).GetProperty("matches").EnumerateArray());

        var targetedList = await Execute("coding.list", new { path = generatedPath.Split('/')[0] });
        Assert.Equal(path, Assert.Single(targetedList.GetProperty("entries").EnumerateArray()).GetProperty("path").GetString());
        Assert.True(targetedList.GetProperty("includedGeneratedPath").GetBoolean());
        Assert.Empty(targetedList.GetProperty("ignoredDirectories").EnumerateArray());
        Assert.Empty(targetedList.GetProperty("ignoredDirectoryPatterns").EnumerateArray());
        var targetedFile = await Execute("coding.list", new { path });
        Assert.Equal(path, Assert.Single(targetedFile.GetProperty("entries").EnumerateArray()).GetProperty("path").GetString());
        var targetedSearch = await Execute("coding.search", new { path = generatedPath, query = "ACTUAL_CACHE_EVIDENCE" });
        Assert.Equal(path, Assert.Single(targetedSearch.GetProperty("matches").EnumerateArray()).GetProperty("path").GetString());
        var read = await Execute("coding.read", new { path });
        Assert.Contains("ACTUAL_CACHE_EVIDENCE", read.GetProperty("content").GetString()!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pytest-plugin")]
    [InlineData("results")]
    [InlineData("runtime")]
    public async Task UserArtifactsAndNonCachePytestDirectoriesAreStillListed(string directory)
    {
        Directory.CreateDirectory(Path.Combine(_root, directory));
        await File.WriteAllTextAsync(Path.Combine(_root, directory, "evidence.txt"), "retained");
        var result = await Execute("coding.list", new { });
        Assert.Equal(directory + "/evidence.txt", Assert.Single(result.GetProperty("entries").EnumerateArray()).GetProperty("path").GetString());
    }

    [Fact]
    public async Task EnvironmentReportsAMissingVenvInterpreterAndCommandNamesItsExactMissingPath()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".venv", "Scripts"));
        var listing = await Execute("coding.list", new { });
        var environment = listing.GetProperty("environment");
        Assert.Equal("Windows", environment.GetProperty("operatingSystem").GetString());
        Assert.True(environment.GetProperty("virtualEnvironmentDirectoryExists").GetBoolean());
        Assert.False(environment.GetProperty("virtualEnvironmentPythonExists").GetBoolean());
        Assert.False(environment.GetProperty("gitMetadataAtProjectRoot").GetBoolean());
        Assert.Equal(".venv/Scripts/python.exe", environment.GetProperty("virtualEnvironmentPython").GetString());
        var error = await Assert.ThrowsAsync<FileNotFoundException>(() => Execute("coding.command", new
        {
            executable = ".venv/Scripts/python.exe", arguments = Array.Empty<string>(),
        }));
        Assert.Equal(Path.Combine(_root, ".venv", "Scripts", "python.exe"), error.FileName);
        Assert.Contains("Geprüfter absoluter Pfad", error.Message, StringComparison.Ordinal);
        Assert.Contains(".venv-Ordner allein", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tools/fixture-cmd.exe", "project")]
    [InlineData("../tools/fixture-cmd.exe", "project/nested")]
    [InlineData("../../shared/fixture-cmd.exe", "project/nested")]
    public async Task RelativeExecutableUsesTheExplicitWorkingDirectoryAndCanReachSharedPrograms(string executable, string workingDirectory)
    {
        // The selected project is nested inside the owned fixture. Shared programs
        // are allowed outside that project, just like absolute executable paths.
        var project = Path.Combine(_root, "project");
        var directory = Path.GetFullPath(Path.Combine(_root, workingDirectory));
        Directory.CreateDirectory(directory);
        var fullExecutable = Path.GetFullPath(executable, directory);
        Directory.CreateDirectory(Path.GetDirectoryName(fullExecutable)!);
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), fullExecutable);
        var executor = new LocalCodingToolExecutor(project);
        var result = await executor.ExecuteAsync("coding.command", JsonSerializer.SerializeToElement(new
        {
            executable, workingDirectory = Path.GetRelativePath(project, directory),
            arguments = RelativeExecutableArguments, timeoutSeconds = 15,
        }));
        Assert.True(result.GetProperty("success").GetBoolean(), result.GetRawText());
        Assert.Equal(0, result.GetProperty("exitCode").GetInt32());
        Assert.Contains("RELATIVE_EXECUTABLE_OK", result.GetProperty("stdout").GetString()!, StringComparison.Ordinal);
        Assert.Contains(directory, result.GetProperty("stdout").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(fullExecutable, result.GetProperty("executable").GetString());
        Assert.Equal(directory, result.GetProperty("workingDirectory").GetString());
        Assert.Equal("absolute-path", result.GetProperty("executableResolution").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("nested folder")]
    [InlineData("nested folder/../nested folder")]
    public async Task AbsoluteCommandWorkingDirectoryUsesTheSelectedWorkspaceOrItsChild(string relative)
    {
        var absolute = Path.Combine(_root, relative);
        var expected = Path.GetFullPath(absolute);
        Directory.CreateDirectory(expected);
        var result = await Execute("coding.command", new
        {
            executable = "cmd.exe", workingDirectory = absolute,
            arguments = RelativeExecutableArguments, timeoutSeconds = 15,
        });
        Assert.True(result.GetProperty("success").GetBoolean(), result.GetRawText());
        Assert.Equal(expected, result.GetProperty("workingDirectory").GetString());
        Assert.Contains(expected, result.GetProperty("stdout").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RELATIVE_EXECUTABLE_OK", result.GetProperty("stdout").GetString()!, StringComparison.Ordinal);
        var file = Path.Combine(expected, "relative-only.txt");
        await File.WriteAllTextAsync(file, "file tools keep their relative-path contract");
        // Read-only tools may now read absolute paths anywhere on the host.
        var read = await Execute("coding.read", new { path = file });
        Assert.Contains("file tools keep their relative-path contract", read.GetProperty("content").GetString());
    }

    [Theory]
    [InlineData(".")]
    [InlineData("project-neighbor")]
    [InlineData("outside")]
    [InlineData("project/../project-neighbor")]
    public async Task AbsoluteCommandWorkingDirectoryCannotEscapeToAParentOrSibling(string relative)
    {
        var project = Path.Combine(_root, "project");
        Directory.CreateDirectory(project);
        var directory = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetFullPath(directory));
        var executor = new LocalCodingToolExecutor(project);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => executor.ExecuteAsync("coding.command",
            JsonSerializer.SerializeToElement(new
            {
                executable = "must-not-start-missing-program.exe", arguments = Array.Empty<string>(), workingDirectory = directory,
            })));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbsoluteCommandWorkingDirectoryStillRejectsJunctions(bool targetOutsideProject)
    {
        var project = Path.Combine(_root, "project");
        Directory.CreateDirectory(project);
        var target = Path.Combine(targetOutsideProject ? _root : project, "target");
        Directory.CreateDirectory(target);
        var link = Path.Combine(project, "linked");
        // These two paths are generated inside this test's exact private fixture.
        var start = new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var process = Process.Start(start)!;
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(process.ExitCode == 0, await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync());
            var executor = new LocalCodingToolExecutor(project);
            var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => executor.ExecuteAsync("coding.command",
                JsonSerializer.SerializeToElement(new
                {
                    executable = "must-not-start-missing-program.exe", arguments = Array.Empty<string>(), workingDirectory = link,
                })));
            Assert.Contains("Symlinks", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            if (Directory.Exists(link) && File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint))
                Directory.Delete(link, recursive: false);
        }
    }

    [Fact]
    public async Task GitDiffWithoutARepositoryKeepsTheRealErrorAndExplainsAvailableFileTools()
    {
        var result = await Execute("coding.gitDiff", new { });
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.NotEqual(0, result.GetProperty("exitCode").GetInt32());
        Assert.Equal("coding.git_not_repository", result.GetProperty("errorCode").GetString());
        Assert.Contains("not a git repository", result.GetProperty("stderr").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("coding.write/edit", result.GetProperty("message").GetString()!, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_root, ".git")));
    }

    [Fact]
    public async Task SearchSkipsLegacyEncodingAndDirectReadExplainsIt()
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "a-legacy.txt"), [0x63, 0x61, 0x66, 0xe9]);
        await File.WriteAllTextAsync(Path.Combine(_root, "z-valid.txt"), "needle café");
        var search = await Execute("coding.search", new { query = "needle" });
        var match = Assert.Single(search.GetProperty("matches").EnumerateArray());
        Assert.Equal("z-valid.txt", match.GetProperty("path").GetString());
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Execute("coding.read", new { path = "a-legacy.txt" }));
        Assert.Contains("UTF-8", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DriveRootAllowsItsExistingChildWithoutDoubleSeparator()
    {
        var path = Path.Combine(_root, "drive-root.txt");
        await File.WriteAllTextAsync(path, "root-prefix-ok");
        var drive = Path.GetPathRoot(_root)!;
        var executor = new LocalCodingToolExecutor(drive);
        var read = await executor.ExecuteAsync("coding.read", JsonSerializer.SerializeToElement(new { path = Path.GetRelativePath(drive, path) }));
        Assert.Contains("root-prefix-ok", read.GetProperty("content").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommandReturnsExitCodeAndDrainsBoundedOutput()
    {
        var result = await Execute("coding.command", new
        {
            executable = "powershell.exe",
            arguments = OutputCommandArguments,
            timeoutSeconds = 15,
        });
        Assert.Equal(7, result.GetProperty("exitCode").GetInt32());
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.True(result.GetProperty("truncated").GetBoolean());
        Assert.Contains("FINAL_DIAGNOSTIC", result.GetProperty("stdout").GetString()!, StringComparison.Ordinal);
        Assert.Equal("powershell.exe", result.GetProperty("executable").GetString());
        Assert.Equal("native-PATH", result.GetProperty("executableResolution").GetString());
        Assert.True(result.GetRawText().Length <= LocalCodingToolExecutor.MaximumOutputCharacters);
    }

    [Fact]
    public async Task CommandPublishesRealOutputBeforeExitAndAwaitsThrottledCallbacks()
    {
        using var cancellation = new CancellationTokenSource();
        var firstOutput = new TaskCompletionSource<CodingCommandProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshots = new List<CodingCommandProgress>();
        var executor = new LocalCodingToolExecutor(_root, async progress =>
        {
            await Task.Delay(30);
            snapshots.Add(progress);
            if (progress.Stdout.Contains("EARLY_STDOUT", StringComparison.Ordinal) && progress.Stderr.Contains("EARLY_STDERR", StringComparison.Ordinal))
                firstOutput.TrySetResult(progress);
        });
        var command = executor.ExecuteAsync("coding.command", JsonSerializer.SerializeToElement(new
        {
            executable = "powershell.exe", arguments = ProgressCommandArguments, timeoutSeconds = 15,
        }), cancellation.Token);
        try
        {
            var live = await firstOutput.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(command.IsCompleted, "The first output must arrive while the actual command is still running.");
            Assert.Contains("EARLY_STDERR", live.Stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("FINAL_STDOUT", live.Stdout, StringComparison.Ordinal);
            var result = await command;
            Assert.True(result.GetProperty("success").GetBoolean(), result.GetRawText());
            Assert.Contains("FINAL_STDOUT", result.GetProperty("stdout").GetString()!, StringComparison.Ordinal);
            Assert.True(snapshots.Count >= 2, "The real process must produce several live output snapshots.");
            for (var index = 1; index < snapshots.Count; index++)
                Assert.True(snapshots[index].ElapsedMilliseconds - snapshots[index - 1].ElapsedMilliseconds >= 450,
                    "Progress publications must be at least approximately half a second apart.");
        }
        finally
        {
            cancellation.Cancel();
            try { await command; }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task LiveCommandSnapshotsPreserveUtf8AndBoundBothHeadAndTail()
    {
        using var cancellation = new CancellationTokenSource();
        var captured = new TaskCompletionSource<CodingCommandProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new LocalCodingToolExecutor(_root, progress =>
        {
            if (progress.Stdout.Contains("STDOUT_TAIL", StringComparison.Ordinal) && progress.Stderr.Contains("STDERR_TAIL", StringComparison.Ordinal))
                captured.TrySetResult(progress);
            return Task.CompletedTask;
        });
        var command = executor.ExecuteAsync("coding.command", JsonSerializer.SerializeToElement(new
        {
            executable = "powershell.exe", arguments = LargeProgressCommandArguments, timeoutSeconds = 15,
        }), cancellation.Token);
        try
        {
            var live = await captured.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(command.IsCompleted);
            Assert.True(live.Truncated);
            Assert.InRange(live.Stdout.Length, 1, 4_000);
            Assert.InRange(live.Stderr.Length, 1, 2_000);
            Assert.StartsWith("Grüße € 日本語", live.Stdout, StringComparison.Ordinal);
            Assert.EndsWith("STDOUT_TAIL", live.Stdout, StringComparison.Ordinal);
            Assert.EndsWith("STDERR_TAIL", live.Stderr, StringComparison.Ordinal);
            var result = await command;
            Assert.True(result.GetProperty("success").GetBoolean());
            Assert.Equal(live.Stdout, result.GetProperty("stdout").GetString());
            Assert.Equal(live.Stderr, result.GetProperty("stderr").GetString());
        }
        finally
        {
            cancellation.Cancel();
            try { await command; }
            catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProgressCallbackFailureOrCancellationStopsTheOwnedProcessTree(bool cancel)
    {
        await CreateChildScriptAsync(30);
        using var cancellation = new CancellationTokenSource();
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pidFile = Path.Combine(_root, "child.pid");
        var executor = new LocalCodingToolExecutor(_root, progress =>
        {
            if (!File.Exists(pidFile)) return Task.CompletedTask;
            observed.TrySetResult();
            if (cancel) { cancellation.Cancel(); return Task.CompletedTask; }
            return Task.FromException(new InvalidOperationException("Progress callback failed deliberately."));
        });
        var command = executor.ExecuteAsync("coding.command", JsonSerializer.SerializeToElement(new
        {
            executable = "powershell.exe", arguments = ChildScriptArguments, timeoutSeconds = 30,
        }), cancellation.Token);
        try
        {
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command);
            else
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => command);
                Assert.Contains("Progress callback failed deliberately", error.Message, StringComparison.Ordinal);
            }
            var childId = int.Parse(await File.ReadAllTextAsync(pidFile), System.Globalization.CultureInfo.InvariantCulture);
            await AssertProcessExitedAsync(childId);
        }
        finally
        {
            cancellation.Cancel();
            try { await command; }
            catch (OperationCanceledException) { }
            catch (InvalidOperationException error) when (error.Message == "Progress callback failed deliberately.") { }
        }
    }

    [Fact]
    public async Task CommandTimeoutTerminatesProcessAndReportsFailure()
    {
        var result = await Execute("coding.command", new
        {
            executable = "powershell.exe",
            arguments = SleepCommandArguments,
            timeoutSeconds = 1,
        });
        Assert.True(result.GetProperty("timedOut").GetBoolean());
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.InRange(result.GetProperty("elapsedMilliseconds").GetInt32(), 500, 10_000);
    }

    [Fact]
    public async Task CancellationStopsChildProcess()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _executor.ExecuteAsync("coding.command", JsonSerializer.SerializeToElement(new
        {
            executable = "powershell.exe",
            arguments = SleepCommandArguments,
            timeoutSeconds = 30,
        }), cancellation.Token));
    }

    [Theory]
    [InlineData(0, 15, false)]
    [InlineData(30, 3, true)]
    public async Task WindowsJobClosesChildrenAfterParentExitOrTimeout(int parentSleep, int timeoutSeconds, bool expectedTimeout)
    {
        await CreateChildScriptAsync(parentSleep);
        var result = await Execute("coding.command", new { executable = "powershell.exe", arguments = ChildScriptArguments, timeoutSeconds });
        Assert.Equal(expectedTimeout, result.GetProperty("timedOut").GetBoolean());
        var childId = int.Parse(await File.ReadAllTextAsync(Path.Combine(_root, "child.pid")), System.Globalization.CultureInfo.InvariantCulture);
        await AssertProcessExitedAsync(childId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    public async Task WindowsJobClosesChildrenWhenUnboundedOrVeryLongCommandIsCancelled(int? timeoutSeconds)
    {
        await CreateChildScriptAsync(30);
        using var cancellation = new CancellationTokenSource();
        var arguments = new Dictionary<string, object>
        {
            ["executable"] = "powershell.exe", ["arguments"] = ChildScriptArguments,
        };
        if (timeoutSeconds.HasValue) arguments["timeoutSeconds"] = timeoutSeconds.Value;
        var command = _executor.ExecuteAsync("coding.command", JsonSerializer.SerializeToElement(arguments), cancellation.Token);
        var pidFile = Path.Combine(_root, "child.pid");
        using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while (!File.Exists(pidFile))
            {
                if (command.IsCompleted) await command;
                await Task.Delay(50, startupTimeout.Token);
            }
            var childId = int.Parse(await File.ReadAllTextAsync(pidFile, startupTimeout.Token), System.Globalization.CultureInfo.InvariantCulture);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command);
            await AssertProcessExitedAsync(childId);
        }
        finally
        {
            cancellation.Cancel();
            try { await command; }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task NegativeCommandDeadlineIsRejectedBeforeStartingAProcess()
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() => Execute("coding.command", new
        {
            executable = "powershell.exe", arguments = SleepCommandArguments, timeoutSeconds = -1,
        }));
        Assert.Contains("timeoutSeconds", error.Message, StringComparison.Ordinal);
    }

    private Task CreateChildScriptAsync(int parentSleep) => File.WriteAllTextAsync(Path.Combine(_root, "spawn-child.ps1"),
        """
        $start = New-Object System.Diagnostics.ProcessStartInfo
        $start.FileName = 'powershell.exe'
        $start.Arguments = '-NoProfile -NonInteractive -Command "Start-Sleep -Seconds 30"'
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $child = [Diagnostics.Process]::Start($start)
        [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'child.pid.tmp'), $child.Id.ToString())
        [IO.File]::Move((Join-Path $PSScriptRoot 'child.pid.tmp'), (Join-Path $PSScriptRoot 'child.pid'))
        [Console]::Write('CHILD_READY')
        """ + $"\nStart-Sleep -Seconds {parentSleep}\nexit 0\n");

    private static async Task AssertProcessExitedAsync(int processId)
    {
        Process child;
        try { child = Process.GetProcessById(processId); }
        catch (ArgumentException) { return; }
        using (child)
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(child.HasExited);
        }
    }

    private Task<JsonElement> Execute(string tool, object arguments) => _executor.ExecuteAsync(tool, JsonSerializer.SerializeToElement(arguments));

    private async Task Git(params string[] arguments)
    {
        var result = await Execute("coding.command", new { executable = "git", arguments, timeoutSeconds = 30 });
        Assert.True(result.GetProperty("success").GetBoolean(), result.GetRawText());
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // Exit assertions stay in the tests. Windows can briefly retain a terminated job's working-directory
        // handle during teardown; retry only this exact fixture root, and keep persistent cleanup failures visible.
        var fixtureRoot = Path.GetFullPath(_root);
        var temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var fixtureName = Path.GetFileName(fixtureRoot);
        const string fixturePrefix = "go-coding-tests-";
        if (!string.Equals(Path.GetDirectoryName(fixtureRoot), temporaryRoot, StringComparison.OrdinalIgnoreCase)
            || !fixtureName.StartsWith(fixturePrefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(fixtureName[fixturePrefix.Length..], "N", out _))
            throw new InvalidOperationException("Refusing cleanup outside the exact Coding test fixture directory.");
        var elapsed = Stopwatch.StartNew();
        while (Directory.Exists(_root))
        {
            try
            {
                if (File.GetAttributes(fixtureRoot).HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidOperationException("Refusing cleanup of a linked Coding test fixture directory.");
                // Git deliberately makes object files read-only. Clear that bit only on
                // ordinary files in this fixture; never enumerate through directory links.
                foreach (var file in Directory.EnumerateFiles(fixtureRoot, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false,
                }))
                {
                    var attributes = File.GetAttributes(file);
                    if (attributes.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
                Directory.Delete(fixtureRoot, recursive: true);
            }
            catch (IOException) when (elapsed.Elapsed < TimeSpan.FromSeconds(5)) { await Task.Delay(100); }
            catch (UnauthorizedAccessException) when (elapsed.Elapsed < TimeSpan.FromSeconds(5)) { await Task.Delay(100); }
        }
    }
}
