using GoWinUI.Core.Coding;
using System.Text.Json;

namespace GoWinUI.Tests;

public sealed class CodingDelegatedWorkspaceTests
{
    private static readonly string[] RolledBackFixturePaths = ["a-edited.txt", "b-deleted.txt", "c-new.txt"];
    private static readonly string[] PublishedFixturePaths = ["a.txt", "b.txt"];
    [Theory]
    [InlineData("../file")]
    [InlineData("C:/project/file")]
    [InlineData(".git/config")]
    [InlineData("src./file")]
    [InlineData("/")]
    [InlineData("src/NUL.txt")]
    [InlineData("src/com1")]
    public void RejectsInvalidOwnership(string path) => Assert.Throws<ArgumentException>(() => CodingDelegatedWorkspace.NormalizeScope(path));

    [Fact]
    public void DirectoryOwnershipDoesNotIncludeSiblingPrefixes()
    {
        Assert.True(CodingDelegatedWorkspace.IsOwned("src/Feature/Code.cs", ["src/feature/"]));
        Assert.False(CodingDelegatedWorkspace.IsOwned("src/feature-other/Code.cs", ["src/feature/"]));
        Assert.False(CodingDelegatedWorkspace.IsOwned("src/main.cs", ["src/child.cs"]));
        Assert.False(CodingDelegatedWorkspace.IsOwned("src/child.cs", []));
    }

    [Fact]
    public async Task RealCommandReadsSharedSourceButPublishesOnlyOwnedChangesAndPreservesParent()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var source = Path.Combine(environment.Directory, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "parent.txt"), "parent");
        await File.WriteAllTextAsync(Path.Combine(source, "shared.txt"), "shared-context");
        var delegated = new CodingDelegatedWorkspace(source, "run-1", "agent-1", ["child/"], Path.Combine(environment.Directory, "copies"));
        var result = await delegated.ExecuteCommandAsync(Command("New-Item -ItemType Directory child -Force | Out-Null; [IO.File]::WriteAllText((Join-Path $PWD 'child/result.txt'), (Get-Content shared.txt -Raw)); [IO.File]::WriteAllText((Join-Path $PWD 'parent.txt'), 'unowned'); Write-Output 'child-test-passed'"));
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal("parent", await File.ReadAllTextAsync(Path.Combine(source, "parent.txt")));
        Assert.Equal("shared-context", await File.ReadAllTextAsync(Path.Combine(source, "child", "result.txt")));
        Assert.Contains("child-test-passed", result.GetProperty("stdout").GetString());
        Assert.Contains("parent.txt", result.GetProperty("delegation").GetProperty("withheldPaths").EnumerateArray().Select(item => item.GetString()));
        // A following command sees current parent changes and keeps the same private copy.
        await File.WriteAllTextAsync(Path.Combine(source, "shared.txt"), "fresh-context");
        await delegated.ExecuteCommandAsync(Command("[IO.File]::WriteAllText((Join-Path $PWD 'child/result.txt'), (Get-Content shared.txt -Raw))"));
        Assert.Equal("fresh-context", await File.ReadAllTextAsync(Path.Combine(source, "child", "result.txt")));
    }

    [Fact]
    public async Task ConcurrentOriginalEditPreventsPublishingTheEntireCommandResult()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var source = Path.Combine(environment.Directory, "source");
        Directory.CreateDirectory(source);
        var file = Path.Combine(source, "owned.txt");
        await File.WriteAllTextAsync(file, "before");
        var delegated = new CodingDelegatedWorkspace(source, "run-1", "agent-1", ["owned.txt", "new.txt"], Path.Combine(environment.Directory, "copies"));
        var observed = false;
        await Assert.ThrowsAsync<IOException>(() => delegated.ExecuteCommandAsync(Command("Write-Output 'ready'; Start-Sleep -Seconds 2; [IO.File]::WriteAllText((Join-Path $PWD 'owned.txt'), 'child'); [IO.File]::WriteAllText((Join-Path $PWD 'new.txt'), 'new')"), async _ =>
        {
            if (observed) return;
            observed = true;
            await File.WriteAllTextAsync(file, "parent-later");
        }));
        Assert.True(observed);
        Assert.Equal("parent-later", await File.ReadAllTextAsync(file));
        Assert.False(File.Exists(Path.Combine(source, "new.txt")));
    }

    [Fact]
    public async Task ExplicitOriginalWorkspaceReferencesAreRejected()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var source = Path.Combine(environment.Directory, "source");
        Directory.CreateDirectory(source);
        var delegated = new CodingDelegatedWorkspace(source, "run-1", "agent-1", [], Path.Combine(environment.Directory, "copies"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => delegated.ExecuteCommandAsync(Command("Get-Content '" + source + "\\file.txt'")));
    }

    [Fact]
    public async Task FailedCommandNeverPublishesPartialEditsOrDeletes()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var source = Path.Combine(environment.Directory, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "owned.txt"), "original");
        var delegated = new CodingDelegatedWorkspace(source, "run", "agent", ["owned.txt", "new.txt"], Path.Combine(environment.Directory, "copies"));
        var result = await delegated.ExecuteCommandAsync(Command("Remove-Item -LiteralPath owned.txt; [IO.File]::WriteAllText((Join-Path $PWD 'new.txt'), 'partial'); exit 4"));
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal(4, result.GetProperty("exitCode").GetInt32());
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(source, "owned.txt")));
        Assert.False(File.Exists(Path.Combine(source, "new.txt")));
        Assert.Empty(result.GetProperty("delegation").GetProperty("publishedPaths").EnumerateArray());
        Assert.Equal("not_published", result.GetProperty("delegation").GetProperty("publicationState").GetString());
    }

    [Fact]
    public async Task GitWorktreePointerIsNeverCopiedIntoDelegatedWorkspace()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var source = Path.Combine(environment.Directory, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, ".git"), "gitdir: ../parent/.git/worktrees/shared");
        var delegated = new CodingDelegatedWorkspace(source, "run", "agent", [], Path.Combine(environment.Directory, "copies"));
        var result = await delegated.ExecuteCommandAsync(Command("if (Test-Path -LiteralPath .git) { exit 5 }; Write-Output 'no-external-git-pointer'"));
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.False(File.Exists(Path.Combine(delegated.WorkspacePath, ".git")));
        Assert.Equal("gitdir: ../parent/.git/worktrees/shared", await File.ReadAllTextAsync(Path.Combine(source, ".git")));
    }

    [Fact]
    public async Task PublishingOwnedHardlinkDoesNotMutateUnownedAlias()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var source = Path.Combine(environment.Directory, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "owned.txt"), "original");
        var linked = await new LocalCodingToolExecutor(source).ExecuteAsync("coding.command",
            Command("New-Item -ItemType HardLink -Path alias.txt -Target (Join-Path $PWD 'owned.txt') | Out-Null"));
        Assert.True(linked.GetProperty("success").GetBoolean(), linked.GetRawText());
        var delegated = new CodingDelegatedWorkspace(source, "run", "agent", ["owned.txt"], Path.Combine(environment.Directory, "copies"));
        await delegated.ExecuteCommandAsync(Command("[IO.File]::WriteAllText((Join-Path $PWD 'owned.txt'), 'child-change')"));
        Assert.Equal("child-change", await File.ReadAllTextAsync(Path.Combine(source, "owned.txt")));
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(source, "alias.txt")));
    }

    [Fact]
    public async Task ProcessCreatedJunctionCannotMasqueradeAsDeletedOwnedFiles()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var source = Path.Combine(environment.Directory, "source");
        Directory.CreateDirectory(Path.Combine(source, "child"));
        await File.WriteAllTextAsync(Path.Combine(source, "child", "owned.txt"), "original");
        var delegated = new CodingDelegatedWorkspace(source, "run", "agent", ["child/"], Path.Combine(environment.Directory, "copies"));
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => delegated.ExecuteCommandAsync(Command(
                "Remove-Item -LiteralPath child -Recurse; New-Item -ItemType Directory other | Out-Null; New-Item -ItemType Junction -Path child -Target (Join-Path $PWD 'other') | Out-Null")));
            Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(source, "child", "owned.txt")));
        }
        finally
        {
            // Remove only the fixture link itself, before the generic test-directory
            // cleanup visits entries. Never traverse or recursively delete its target.
            var junction = Path.GetFullPath(Path.Combine(delegated.WorkspacePath, "child"));
            Assert.StartsWith(delegated.WorkspacePath + Path.DirectorySeparatorChar, junction, StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(junction) && (File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(junction, recursive: false);
        }
    }

    [Fact]
    public async Task ExplicitGeneratedScopeIsCopiedAndPublishedWithoutUnrelatedGeneratedFiles()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var source = Path.Combine(environment.Directory, "source");
        Directory.CreateDirectory(Path.Combine(source, "artifacts", "generated"));
        Directory.CreateDirectory(Path.Combine(source, "obj"));
        await File.WriteAllTextAsync(Path.Combine(source, "artifacts", "generated", "input.txt"), "generated-input");
        await File.WriteAllTextAsync(Path.Combine(source, "obj", "parent-lock.txt"), "parent-build");
        var delegated = new CodingDelegatedWorkspace(source, "run", "agent", ["artifacts/generated/"], Path.Combine(environment.Directory, "copies"));
        var result = await delegated.ExecuteCommandAsync(Command("if (Test-Path -LiteralPath obj/parent-lock.txt) { exit 9 }; [IO.File]::WriteAllText((Join-Path $PWD 'artifacts/generated/result.txt'), (Get-Content artifacts/generated/input.txt -Raw))"));
        Assert.True(result.GetProperty("success").GetBoolean(), result.GetRawText());
        Assert.Equal("generated-input", await File.ReadAllTextAsync(Path.Combine(source, "artifacts", "generated", "result.txt")));
        Assert.Equal("parent-build", await File.ReadAllTextAsync(Path.Combine(source, "obj", "parent-lock.txt")));
    }

    [Fact]
    public async Task SameChildCopyCannotBeRefreshedWhileItsCommandStillRuns()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var source = Path.Combine(environment.Directory, "source");
        Directory.CreateDirectory(source);
        var cache = Path.Combine(environment.Directory, "copies");
        var first = new CodingDelegatedWorkspace(source, "run", "agent", [], cache);
        var replay = new CodingDelegatedWorkspace(source, "run", "agent", [], cache);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = first.ExecuteCommandAsync(Command("Write-Output 'first-active'; Start-Sleep -Seconds 2"), _ =>
        {
            entered.TrySetResult();
            return release.Task;
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replay.ExecuteCommandAsync(
                Command("[IO.File]::WriteAllText((Join-Path $PWD 'second-started.txt'), 'unsafe')"), cancellationToken: stop.Token));
            Assert.False(File.Exists(Path.Combine(replay.WorkspacePath, "second-started.txt")));
        }
        finally
        {
            release.TrySetResult();
            await running;
        }
    }

    [Fact]
    public async Task PublishIoFailureRollsBackEditsDeletionsAndNewFilesAndReturnsCompleteReceipt()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var source = Path.Combine(environment.Directory, "source");
        Directory.CreateDirectory(Path.Combine(source, "z-blocked.txt"));
        await File.WriteAllTextAsync(Path.Combine(source, "a-edited.txt"), "original-a");
        await File.WriteAllTextAsync(Path.Combine(source, "b-deleted.txt"), "original-b");
        var delegated = new CodingDelegatedWorkspace(source, "run", "agent", ["a-edited.txt", "b-deleted.txt", "c-new.txt", "z-blocked.txt"], Path.Combine(environment.Directory, "copies"));
        // Empty source directories are not copied. The command can produce this
        // file, but publishing it over the original directory fails after a/b/c.
        var result = await delegated.ExecuteCommandAsync(Command("[IO.File]::WriteAllText((Join-Path $PWD 'a-edited.txt'), 'child'); Remove-Item -LiteralPath b-deleted.txt; [IO.File]::WriteAllText((Join-Path $PWD 'c-new.txt'), 'new'); [IO.File]::WriteAllText((Join-Path $PWD 'z-blocked.txt'), 'blocked')"));
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal("original-a", await File.ReadAllTextAsync(Path.Combine(source, "a-edited.txt")));
        Assert.Equal("original-b", await File.ReadAllTextAsync(Path.Combine(source, "b-deleted.txt")));
        Assert.False(File.Exists(Path.Combine(source, "c-new.txt")));
        Assert.True(Directory.Exists(Path.Combine(source, "z-blocked.txt")));
        var receipt = result.GetProperty("delegation");
        Assert.Equal("rolled_back", receipt.GetProperty("publicationState").GetString());
        Assert.Empty(receipt.GetProperty("publishedPaths").EnumerateArray());
        Assert.Equal(RolledBackFixturePaths, receipt.GetProperty("rolledBackPaths").EnumerateArray().Select(path => path.GetString()).Order());
        Assert.Empty(Directory.EnumerateFiles(source, ".go-agent-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CancellationAfterFirstAtomicPublishFinishesTheCommitWithAllPathsAccountedFor()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var source = Path.Combine(environment.Directory, "source");
        Directory.CreateDirectory(source);
        var delegated = new CodingDelegatedWorkspace(source, "run", "agent", ["a.txt", "b.txt"], Path.Combine(environment.Directory, "copies"));
        using var stop = new CancellationTokenSource();
        var result = await delegated.PublishAsync([("a.txt", "a"u8.ToArray()), ("b.txt", "b"u8.ToArray())],
            new Dictionary<string, string>(), stop.Token, path => { if (path == "a.txt") stop.Cancel(); });
        Assert.True(stop.IsCancellationRequested);
        Assert.Null(result.Error);
        Assert.Equal(PublishedFixturePaths, result.PublishedPaths);
        Assert.Equal("a", await File.ReadAllTextAsync(Path.Combine(source, "a.txt")));
        Assert.Equal("b", await File.ReadAllTextAsync(Path.Combine(source, "b.txt")));
    }

    [Fact]
    public async Task RollbackNeverOverwritesLaterParentEditAndRetainsOriginalBackupInReceipt()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var source = Path.Combine(environment.Directory, "source");
        Directory.CreateDirectory(source);
        var file = Path.Combine(source, "a.txt");
        await File.WriteAllTextAsync(file, "original");
        var delegated = new CodingDelegatedWorkspace(source, "run", "agent", ["a.txt"], Path.Combine(environment.Directory, "copies"));
        var baseline = new Dictionary<string, string> { ["a.txt"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("original"u8)) };
        var receipt = await delegated.PublishAsync([("a.txt", "child"u8.ToArray())], baseline, CancellationToken.None, _ =>
        {
            File.WriteAllText(file, "parent-later");
            throw new IOException("Subsequent publish failed");
        });
        Assert.NotNull(receipt.Error);
        Assert.Equal("parent-later", await File.ReadAllTextAsync(file));
        Assert.Equal("a.txt", Assert.Single(receipt.PublishedPaths));
        Assert.Equal("a.txt", Assert.Single(receipt.ConflictPaths));
        Assert.Empty(receipt.RolledBackPaths);
        Assert.Equal("original", await File.ReadAllTextAsync(Assert.Single(receipt.RecoveryFiles!).Value));
    }

    [Fact]
    public async Task CancelledPublicationBeforeCommitNeverChangesOriginalFiles()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var source = Path.Combine(environment.Directory, "source");
        Directory.CreateDirectory(source);
        var delegated = new CodingDelegatedWorkspace(source, "run", "agent", ["a.txt"], Path.Combine(environment.Directory, "copies"));
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delegated.PublishAsync([("a.txt", "new"u8.ToArray())],
            new Dictionary<string, string>(), stop.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(source));
    }

    [Fact]
    public async Task RetentionEvictsOldestOwnedCopiesByCountAndNeverDeletesAnActiveLeaseOrForeignDirectory()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var cache = Path.Combine(environment.Directory, "copies");
        var active = CacheEntry(cache, 'a', DateTime.UtcNow.AddDays(-10));
        var oldest = CacheEntry(cache, 'b', DateTime.UtcNow.AddDays(-6));
        var middle = CacheEntry(cache, 'c', DateTime.UtcNow.AddDays(-5));
        var newest = CacheEntry(cache, 'd', DateTime.UtcNow);
        var foreign = Path.Combine(cache, new string('f', 64));
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "keep.txt"), "not-owned");
        Directory.CreateDirectory(Path.Combine(cache, ".leases"));
        using (var lease = new FileStream(Path.Combine(cache, ".leases", Path.GetFileName(active) + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(2, CodingDelegatedWorkspace.CleanupInactiveCopies(cache, maximumCopies: 2));
            Assert.True(Directory.Exists(active));
            Assert.False(Directory.Exists(oldest));
            Assert.False(Directory.Exists(middle));
            Assert.True(Directory.Exists(newest));
            Assert.True(File.Exists(Path.Combine(foreign, "keep.txt")));
        }
        Assert.Equal(1, CodingDelegatedWorkspace.CleanupInactiveCopies(cache));
        Assert.False(Directory.Exists(active));
    }

    [Fact]
    public async Task RetentionByteBudgetIncludesGeneratedDependencies()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var cache = Path.Combine(environment.Directory, "copies");
        var old = CacheEntry(cache, 'a', DateTime.UtcNow.AddDays(-1));
        var fresh = CacheEntry(cache, 'b', DateTime.UtcNow);
        Directory.CreateDirectory(Path.Combine(old, "workspace", "node_modules"));
        await File.WriteAllBytesAsync(Path.Combine(old, "workspace", "node_modules", "generated.bin"), new byte[4096]);
        Assert.Equal(1, CodingDelegatedWorkspace.CleanupInactiveCopies(cache, maximumBytes: 1024));
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(fresh));
    }

    [Fact]
    public async Task RetentionUnderstandsLegacyCopiesButNeverDeletesTheirActiveLease()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var cache = Path.Combine(environment.Directory, "copies");
        var entry = Path.Combine(cache, new string('a', 64));
        var workspace = Path.Combine(entry, "workspace");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "keep.txt"), "active-command");
        Directory.SetLastWriteTimeUtc(workspace, DateTime.UtcNow.AddDays(-10));
        using (var active = new FileStream(Path.Combine(entry, "execution.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(0, CodingDelegatedWorkspace.CleanupInactiveCopies(cache, maximumCopies: 0));
            Assert.Equal("active-command", File.ReadAllText(Path.Combine(workspace, "keep.txt")));
        }
        Assert.Equal(1, CodingDelegatedWorkspace.CleanupInactiveCopies(cache));
        Assert.False(Directory.Exists(entry));
    }

    [Fact]
    public async Task RetentionRemovesJunctionOnlyAndNeverTraversesOutsideTheOwnedEntry()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var cache = Path.Combine(environment.Directory, "copies");
        var entry = CacheEntry(cache, 'a', DateTime.UtcNow.AddDays(-10));
        var outside = Path.Combine(environment.Directory, "external-parent");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "parent-data");
        var workspace = Path.Combine(entry, "workspace");
        var junction = Path.Combine(workspace, "external");
        try
        {
            var created = await new LocalCodingToolExecutor(workspace).ExecuteAsync("coding.command",
                Command("New-Item -ItemType Junction -Path external -Target '" + outside.Replace("'", "''") + "' | Out-Null"));
            Assert.True(created.GetProperty("success").GetBoolean(), created.GetRawText());
            Assert.Equal(1, CodingDelegatedWorkspace.CleanupInactiveCopies(cache));
            Assert.False(Directory.Exists(entry));
            Assert.Equal("parent-data", File.ReadAllText(Path.Combine(outside, "keep.txt")));
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(cache) + Path.DirectorySeparatorChar, Path.GetFullPath(junction), StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(junction) && (File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(junction, recursive: false);
        }
    }

    [Fact]
    public async Task RetentionIgnoresMalformedOwnershipReceipts()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var cache = Path.Combine(environment.Directory, "copies");
        var entry = CacheEntry(cache, 'a', DateTime.UtcNow.AddDays(-10));
        foreach (var malformed in new[] { "[]", "{\"schema\":4}", "not-json", "{\"schema\":\"other-owner\"}" })
        {
            File.WriteAllText(Path.Combine(entry, "owner.json"), malformed);
            Assert.Equal(0, CodingDelegatedWorkspace.CleanupInactiveCopies(cache, maximumCopies: 0));
            Assert.True(Directory.Exists(entry));
        }
    }

    private static string CacheEntry(string root, char identity, DateTime lastUsed)
    {
        var entry = Path.Combine(root, new string(identity, 64));
        Directory.CreateDirectory(Path.Combine(entry, "workspace"));
        var marker = Path.Combine(entry, "owner.json");
        File.WriteAllText(marker, "{\"schema\":\"go.delegated-workspace.v1\"}");
        File.SetLastWriteTimeUtc(marker, lastUsed);
        return entry;
    }

    private static JsonElement Command(string command) => JsonSerializer.SerializeToElement(new
    { executable = "powershell.exe", arguments = new[] { "-NoProfile", "-NonInteractive", "-Command", command }, timeoutSeconds = 15 });
}
