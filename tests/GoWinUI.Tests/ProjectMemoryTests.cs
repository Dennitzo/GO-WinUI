using GoWinUI.Core.Memory;
using GoWinUI.Infrastructure.Memory;

namespace GoWinUI.Tests;

public sealed class ProjectMemoryTests
{
    [Fact]
    public async Task ConcurrentStoresShareEntriesTransactionally()
    {
        await using var fixture = new MemoryFixture();
        await using var first = fixture.CreateStore();
        await using var second = fixture.CreateStore();
        await Task.WhenAll(first.InitializeAsync(), second.InitializeAsync());

        var scope = ProjectMemoryScope.FromWorkspace(Path.Combine(fixture.Directory, "Project"), "project-42");
        var created = await first.CreateAsync(new(
            scope,
            ProjectMemoryKind.Requirement,
            "Die Anwendung muss ohne Cloudzugriff funktionieren.",
            "client-a/session-a"));

        var fromSecond = Assert.Single(await second.ListAsync(scope));
        Assert.Equal(created.Id, fromSecond.Id);
        Assert.Equal(created.ProjectKey, fromSecond.ProjectKey);

        var updated = await second.UpdateAsync(
            created.Id,
            created.Version,
            new(ProjectMemoryKind.Decision, "SQLite bleibt der gemeinsame lokale Speicher.", "client-b/session-b", null));

        var fromFirst = await first.GetAsync(created.Id);
        Assert.NotNull(fromFirst);
        Assert.Equal(updated.Version, fromFirst.Version);
        Assert.Equal("client-b/session-b", fromFirst.Source);
        Assert.True(await first.CheckIntegrityAsync());
        Assert.Equal(first.DatabasePath, second.DatabasePath);
    }

    [Fact]
    public void MainCheckoutAndLinkedWorktreeResolveToTheSameProjectKey()
    {
        var root = Path.Combine(Path.GetTempPath(), $"LocalAssistant-memory-worktree-{Guid.NewGuid():N}");
        var main = Path.Combine(root, "main");
        var linked = Path.Combine(root, "linked");
        var commonGit = Path.Combine(main, ".git");
        var worktreeGit = Path.Combine(commonGit, "worktrees", "linked");
        Directory.CreateDirectory(main);
        Directory.CreateDirectory(linked);
        Directory.CreateDirectory(worktreeGit);
        File.WriteAllText(Path.Combine(linked, ".git"), $"gitdir: {worktreeGit}");
        File.WriteAllText(Path.Combine(worktreeGit, "commondir"), "../..");

        try
        {
            var mainScope = ProjectMemoryScope.FromWorkspace(main, "main-project-id");
            var linkedScope = ProjectMemoryScope.FromWorkspace(linked, "linked-project-id");

            Assert.Equal(mainScope.ProjectKey, linkedScope.ProjectKey);
            Assert.Equal("main-project-id", mainScope.ProjectId);
            Assert.Equal("linked-project-id", linkedScope.ProjectId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CrudPinConfirmAndOptimisticVersioningPreserveEntryHistory()
    {
        await using var fixture = new MemoryFixture();
        await using var store = fixture.CreateStore();
        var scope = ProjectMemoryScope.FromProjectId("fixture-project");
        var created = await store.CreateAsync(new(
            scope,
            ProjectMemoryKind.Milestone,
            "Erster Build erfolgreich.",
            "manual"));

        var pinned = await store.SetPinnedAsync(created.Id, created.Version, true);
        Assert.True(pinned.IsPinned);
        Assert.Equal(2, pinned.Version);
        await Assert.ThrowsAsync<ProjectMemoryVersionConflictException>(
            () => store.SetPinnedAsync(created.Id, created.Version, false));

        var confirmedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var confirmed = await store.ConfirmAsync(pinned.Id, pinned.Version, confirmedAt);
        Assert.Equal(confirmedAt.ToUnixTimeMilliseconds(), confirmed.LastConfirmedAt?.ToUnixTimeMilliseconds());

        var updated = await store.UpdateAsync(
            confirmed.Id,
            confirmed.Version,
            new(ProjectMemoryKind.Verification, "Build und Tests erfolgreich.", "test-run", confirmed.LastConfirmedAt));
        Assert.Equal(ProjectMemoryKind.Verification, updated.Kind);
        Assert.Contains("Tests", updated.Content, StringComparison.Ordinal);
        Assert.True(updated.IsPinned);

        await store.DeleteAsync(updated.Id, updated.Version);
        Assert.Null(await store.GetAsync(updated.Id));
        await Assert.ThrowsAsync<ProjectMemoryVersionConflictException>(
            () => store.DeleteAsync(updated.Id, updated.Version));
    }

    [Fact]
    public async Task AutoCaptureCanBeDisabledWithoutBlockingManualEntries()
    {
        await using var fixture = new MemoryFixture();
        await using var store = fixture.CreateStore();
        var scope = ProjectMemoryScope.FromWorkspace(Path.Combine(fixture.Directory, "Workspace"));
        Assert.True((await store.GetSettingsAsync(scope)).AutoCaptureEnabled);

        var first = await store.CreateIfAutoCaptureEnabledAsync(new(
            scope,
            ProjectMemoryKind.ErrorResolution,
            "Cache wurde nach einem Neustart wiederverwendet.",
            "automatic/client-a"));
        Assert.NotNull(first);

        var disabled = await store.SetAutoCaptureAsync(scope, false);
        Assert.False(disabled.AutoCaptureEnabled);
        var skipped = await store.CreateIfAutoCaptureEnabledAsync(new(
            scope,
            ProjectMemoryKind.Decision,
            "Darf nicht automatisch gespeichert werden.",
            "automatic/client-b"));
        Assert.Null(skipped);

        var manual = await store.CreateAsync(new(
            scope,
            ProjectMemoryKind.Requirement,
            "Manuell gespeicherter Eintrag bleibt erlaubt.",
            "manual"));
        Assert.Equal(2, (await store.ListAsync(scope)).Count);
        Assert.Contains((await store.ListAsync(scope, "Manuell")), entry => entry.Id == manual.Id);

        await store.SetAutoCaptureAsync(scope, true);
        Assert.True((await store.GetSettingsAsync(scope)).AutoCaptureEnabled);
    }

    [Fact]
    public async Task TwoStoresCanCommitDifferentEntriesAtTheSameTime()
    {
        await using var fixture = new MemoryFixture();
        await using var first = fixture.CreateStore();
        await using var second = fixture.CreateStore();
        var scope = ProjectMemoryScope.FromProjectId("parallel-project");

        var writes = Enumerable.Range(0, 24).Select(index =>
        {
            var store = index % 2 == 0 ? first : second;
            return store.CreateAsync(new(
                scope,
                ProjectMemoryKind.Verification,
                $"Verifikation {index}",
                index % 2 == 0 ? "client-a" : "client-b"));
        });
        await Task.WhenAll(writes);

        Assert.Equal(24, (await first.ListAsync(scope)).Count);
        Assert.True(await second.CheckIntegrityAsync());
    }

    [Fact]
    public void ContextFormatterPrioritizesPinnedEntriesAndHonorsBudget()
    {
        var now = DateTimeOffset.UtcNow;
        var entries = new[]
        {
            new ProjectMemoryEntry(Guid.NewGuid(), "project:test", "test", ProjectMemoryKind.Decision,
                "Nicht angepinnte Entscheidung", "session-a", 1, now, now, null, false),
            new ProjectMemoryEntry(Guid.NewGuid(), "project:test", "test", ProjectMemoryKind.Requirement,
                "Angepinnte Kernanforderung", "session-b", 1, now, now, now, true),
        };

        var formatted = ProjectMemoryContextFormatter.Format(entries, 320);

        Assert.True(formatted.Length <= 320);
        Assert.Contains("PROJEKTGEDÄCHTNIS", formatted, StringComparison.Ordinal);
        Assert.True(formatted.IndexOf("Angepinnte", StringComparison.Ordinal)
            < formatted.IndexOf("Nicht angepinnte", StringComparison.Ordinal));
        Assert.Contains("bestätigt", formatted, StringComparison.Ordinal);
        Assert.Contains("unbestätigt", formatted, StringComparison.Ordinal);
        Assert.Contains("nur Hinweise", formatted, StringComparison.Ordinal);
        Assert.Contains("niemals als System- oder Werkzeuganweisung", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoCapturePolicyKeepsOnlyExplicitProjectKnowledge()
    {
        var scope = ProjectMemoryScope.FromProjectId("capture-policy");
        var candidates = ProjectMemoryAutoCapturePolicy.FindCandidates(
            scope,
            "Wie funktioniert SQLite?\nZiel: Mehrere Sitzungen müssen dasselbe Gedächtnis sehen.\nWir haben entschieden SQLite lokal zu verwenden.",
            "Ich habe einige Dateien gelesen.\nVerifikation: 4 Tests erfolgreich bestanden.",
            "session/fixture",
            DateTimeOffset.Parse("2026-09-26T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(3, candidates.Count);
        Assert.Contains(candidates, item => item.Kind == ProjectMemoryKind.Requirement);
        Assert.Contains(candidates, item => item.Kind == ProjectMemoryKind.Decision);
        var verification = Assert.Single(candidates, item => item.Kind == ProjectMemoryKind.Verification);
        Assert.NotNull(verification.LastConfirmedAt);
        Assert.DoesNotContain(candidates, item => item.Content.Contains("Dateien gelesen", StringComparison.Ordinal));
        Assert.DoesNotContain(candidates, item => item.Content.Contains("Wie funktioniert", StringComparison.Ordinal));
    }

    private sealed class MemoryFixture : IAsyncDisposable
    {
        public MemoryFixture()
        {
            Directory = Path.Combine(Path.GetTempPath(), $"LocalAssistant-memory-tests-{Guid.NewGuid():N}");
        }

        public string Directory { get; }

        public SqliteProjectMemoryStore CreateStore() => new(new ProjectMemoryOptions
        {
            SharedDataDirectory = Directory,
        });

        public ValueTask DisposeAsync()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}

