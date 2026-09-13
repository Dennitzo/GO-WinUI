using GoWinUI.App.Services;
using GoWinUI.App.ViewModels;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Text.Json;

namespace GoWinUI.Tests;

public sealed class CodingChangesIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransientReaderLockPreservesTheLastSummaryUntilAnAtomicReplacementCanFinish(bool cancelBlockedRefresh)
    {
        if (!OperatingSystem.IsWindows()) return; // Windows denies rename when a reader omits FileShare.Delete.
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "project")).FullName;
        var source = Path.Combine(workspace, "source.txt");
        await File.WriteAllTextAsync(source, "original\n");
        var storage = CodingChangesMonitor.StorageDirectory(environment.Directory, Guid.NewGuid(), Guid.NewGuid());
        var summaries = new ConcurrentQueue<CodingChangesSummary>();
        var failures = new ConcurrentQueue<Exception>();
        var replacementBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var monitor = new CodingChangesMonitor(workspace, storage, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            summary => { summaries.Enqueue(summary); return Task.CompletedTask; }, failures.Enqueue,
            () => replacementBlocked.TrySetResult());
        await monitor.StartAsync(resume: false, CancellationToken.None);
        monitor.Cancel(); // Control refresh timing; the final scan remains enabled.
        var initial = (await CodingChangesMonitor.ReadLatestAsync(storage))!;
        await File.WriteAllTextAsync(source, "saved after reader release\n");
        using var refreshCancellation = new CancellationTokenSource();
        Task refresh;
        using (var reader = new FileStream(Path.Combine(storage, "latest.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            refresh = monitor.RefreshAsync(refreshCancellation.Token);
            await replacementBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(refresh.IsCompleted, "The old receipt must remain intact while its reader denies replacement.");
            Assert.Equal(initial.Revision, (await CodingChangesMonitor.ReadLatestAsync(storage))!.Revision);
            if (cancelBlockedRefresh)
            {
                await refreshCancellation.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
                Assert.Equal(initial.Revision, (await CodingChangesMonitor.ReadLatestAsync(storage))!.Revision);
                Assert.Empty(Directory.EnumerateFiles(storage, ".latest-*.tmp"));
            }
        }
        if (cancelBlockedRefresh) await monitor.RefreshAsync();
        else await refresh.WaitAsync(TimeSpan.FromSeconds(10));
        var saved = (await CodingChangesMonitor.ReadLatestAsync(storage))!;
        Assert.False(saved.IsPartial, saved.Notice);
        Assert.True(saved.Revision > initial.Revision);
        Assert.Contains("+saved after reader release", Assert.Single(saved.Files).Diff, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(storage, ".latest-*.tmp"));
        Assert.Empty(failures);
        Assert.All(summaries, static summary => Assert.False(summary.IsPartial, summary.Notice));
    }

    [Fact]
    public async Task RestartLoadsTheOriginalBaselineAndFinalScanIncludesChangesAfterCancellation()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "project")).FullName;
        var source = Path.Combine(workspace, "source.txt");
        await File.WriteAllTextAsync(source, "original\n");
        var sessionId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var storage = CodingChangesMonitor.StorageDirectory(environment.Directory, sessionId, messageId);
        var events = new ConcurrentQueue<CodingChangesSummary>();
        var failures = new ConcurrentQueue<Exception>();
        await using (var monitor = new CodingChangesMonitor(workspace, storage, sessionId, messageId, Guid.NewGuid(),
            summary => { events.Enqueue(summary); return Task.CompletedTask; }, failures.Enqueue))
        {
            await monitor.StartAsync(resume: false, CancellationToken.None);
            Assert.Empty(Assert.Single(events).Files);
            await File.WriteAllTextAsync(source, "intermediate content\n");
            await monitor.RefreshAsync();
        }
        var first = (await CodingChangesMonitor.ReadLatestAsync(storage))!;
        Assert.Contains("-original", Assert.Single(first.Files).Diff, StringComparison.Ordinal);

        await using (var resumed = new CodingChangesMonitor(workspace, storage, sessionId, messageId, Guid.NewGuid(),
            summary => { events.Enqueue(summary); return Task.CompletedTask; }, failures.Enqueue))
        {
            await resumed.StartAsync(resume: true, CancellationToken.None);
            resumed.Cancel();
            await File.WriteAllTextAsync(source, "final after cancellation\n");
        }
        var final = (await CodingChangesMonitor.ReadLatestAsync(storage))!;
        var change = Assert.Single(final.Files);
        Assert.False(final.IsPartial, final.Notice);
        Assert.Contains("-original", change.Diff, StringComparison.Ordinal);
        Assert.Contains("+final after cancellation", change.Diff, StringComparison.Ordinal);
        Assert.DoesNotContain("-intermediate content", change.Diff, StringComparison.Ordinal);
        Assert.True(final.Revision > first.Revision);
        Assert.True(final.Revision < 9_007_199_254_740_991);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task LegacyResumeNeverReplacesAMissingBaselineWithTheCurrentFiles()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "project")).FullName;
        await File.WriteAllTextAsync(Path.Combine(workspace, "already-modified.txt"), "existing work\n");
        var sessionId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var storage = CodingChangesMonitor.StorageDirectory(environment.Directory, sessionId, messageId);
        var events = new ConcurrentQueue<CodingChangesSummary>();
        var failures = new ConcurrentQueue<Exception>();
        await using (var monitor = new CodingChangesMonitor(workspace, storage, sessionId, messageId, Guid.NewGuid(),
            summary => { events.Enqueue(summary); return Task.CompletedTask; }, failures.Enqueue))
        {
            await monitor.StartAsync(resume: true, CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(workspace, "new-after-resume.txt"), "cannot establish a run baseline\n");
            await monitor.RefreshAsync();
        }
        Assert.All(events, summary =>
        {
            Assert.True(summary.IsPartial);
            Assert.False(string.IsNullOrWhiteSpace(summary.Notice));
            Assert.Empty(summary.Files);
        });
        var stored = (await CodingChangesMonitor.ReadLatestAsync(storage))!;
        Assert.True(stored.IsPartial);
        Assert.Empty(stored.Files);
        Assert.IsType<IOException>(Assert.Single(failures));
    }

    [Fact]
    public async Task PollPublishesActualChangesAndFinalRevertRemovesTheNetChange()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "project")).FullName;
        var source = Path.Combine(workspace, "source.txt");
        await File.WriteAllTextAsync(source, "baseline\n");
        var changed = new TaskCompletionSource<CodingChangesSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failures = new ConcurrentQueue<Exception>();
        var storage = CodingChangesMonitor.StorageDirectory(environment.Directory, Guid.NewGuid(), Guid.NewGuid());
        await using (var monitor = new CodingChangesMonitor(workspace, storage, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            summary => { if (summary.Files.Count > 0) changed.TrySetResult(summary); return Task.CompletedTask; }, failures.Enqueue))
        {
            await monitor.StartAsync(resume: false, CancellationToken.None);
            await File.WriteAllTextAsync(source, "real periodic modification\n");
            var live = await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains("+real periodic modification", Assert.Single(live.Files).Diff, StringComparison.Ordinal);
            monitor.Cancel();
            await File.WriteAllTextAsync(source, "baseline\n");
        }
        Assert.Empty((await CodingChangesMonitor.ReadLatestAsync(storage))!.Files);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task SnapshotReloadsTheLastCodingSummaryAndBridgePublishesTheSameMessageBinding()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Changes persistence");
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "project")).FullName;
        await chats.SetCodingWorkspacePathAsync(session.Id, workspace, activateCoding: true);
        var turn = await chats.AddTurnAsync(session.Id, "Modify a file");
        var storage = CodingChangesMonitor.StorageDirectory(environment.Directory, session.Id, turn.AssistantMessage.Id);
        await using (var monitor = new CodingChangesMonitor(workspace, storage, session.Id, turn.AssistantMessage.Id, Guid.NewGuid(),
            _ => Task.CompletedTask, exception => throw new InvalidOperationException("Unexpected tracker error", exception)))
        {
            await monitor.StartAsync(resume: false, CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(workspace, "added.txt"), "new file\n");
        }
        await chats.UpdateMessageAsync(turn.AssistantMessage.Id, "Created the file.", MessageStatus.Completed);
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        await settings.UpdateAsync(current => current with { ActiveSessionId = session.Id });
        Assert.Equal(Path.GetFullPath(environment.Directory), settings.DataDirectory);
        using var service = new GoAiAssistantService(null!, chats, null!, null!, null!, null!, null!, null!, null!, null!,
            null!, null!, null!, settings, null!, NullLogger<GoAiAssistantService>.Instance);
        var activity = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
        var coordinator = new AssistantCoordinator(chats, environment.Get<IWorkflowRepository>(), environment.Get<IDocumentIngestor>(),
            environment.Get<IContextAssembler>(), environment.Get<IPromptTriggerRepository>(), environment.Get<IAssistantAttachmentRepository>(),
            environment.Get<IChatArtifactRepository>(), environment.Get<IConversationSnapshotRepository>(), service, settings, activity);

        var snapshot = JsonSerializer.SerializeToElement(await coordinator.BuildSnapshotAsync(), JsonSerializerOptions.Web);
        var summary = snapshot.GetProperty("changesSummary");
        Assert.Equal(turn.AssistantMessage.Id, summary.GetProperty("messageId").GetGuid());
        Assert.Equal(workspace, summary.GetProperty("workspacePath").GetString());
        Assert.Equal("added.txt", Assert.Single(summary.GetProperty("files").EnumerateArray()).GetProperty("path").GetString());
        var stored = (await CodingChangesMonitor.ReadLatestAsync(storage))!;
        var emitted = new List<(string Type, JsonElement Payload)>();
        await coordinator.EmitGoAiUpdateAsync(new(GoAiAssistantUpdateKind.FileChangesChanged, turn.AssistantMessage, ChangesSummary: stored),
            (type, payload, _) =>
            {
                // AssistantPage forwards this type directly to PostAsync. Its host
                // allowlist must admit the live event, not only the JS bridge.
                Assert.True(AssistantWebBridge.IsOutgoingTypeAllowed(type), "The native WebView bridge rejected the live change summary.");
                emitted.Add((type, JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web)));
                return Task.CompletedTask;
            }, "changes-test");
        var bridgeEvent = Assert.Single(emitted);
        Assert.Equal("coding.changes", bridgeEvent.Type);
        Assert.Equal(summary.GetProperty("revision").GetInt64(), bridgeEvent.Payload.GetProperty("revision").GetInt64());
        Assert.Equal(session.Id, bridgeEvent.Payload.GetProperty("sessionId").GetGuid());
        Assert.False(AssistantWebBridge.IsIncomingTypeAllowed("coding.changes"));
        Assert.False(AssistantWebBridge.IsOutgoingTypeAllowed("coding.changes.untrusted"));
        await chats.AddTurnAsync(session.Id, "A later message without a Coding run");
        var nextSnapshot = JsonSerializer.SerializeToElement(await coordinator.BuildSnapshotAsync(), JsonSerializerOptions.Web);
        Assert.Equal(JsonValueKind.Null, nextSnapshot.GetProperty("changesSummary").ValueKind);
    }

    [Fact]
    public async Task UnavailableCacheProducesAPartialNoticeWithoutBlockingTheRunOrPollingAnUninitializedTracker()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "project")).FullName;
        await File.WriteAllTextAsync(Path.Combine(workspace, "source.txt"), "unchanged source\n");
        var blockedCache = Path.Combine(environment.Directory, "blocked-cache");
        await File.WriteAllTextAsync(blockedCache, "a file occupies the intended cache directory");
        var events = new ConcurrentQueue<CodingChangesSummary>();
        var failures = new ConcurrentQueue<Exception>();
        await using (var monitor = new CodingChangesMonitor(workspace, blockedCache, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            summary => { events.Enqueue(summary); return Task.CompletedTask; }, failures.Enqueue))
        {
            await monitor.StartAsync(resume: false, CancellationToken.None);
            var failedOverview = Assert.Single(events);
            Assert.True(failedOverview.IsPartial);
            Assert.False(string.IsNullOrWhiteSpace(failedOverview.Notice));
            Assert.Empty(failedOverview.Files);
            await monitor.RefreshAsync();
            Assert.Single(events);
        }
        Assert.Single(events);
        Assert.NotEmpty(failures);
        Assert.Equal("unchanged source\n", await File.ReadAllTextAsync(Path.Combine(workspace, "source.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyActuallyDeletedSessionsLoseTheirChangesCache(bool bulkDelete)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var retained = await chats.CreateSessionAsync("Pinned retained");
        await chats.SetPinnedAsync(retained.Id, true);
        var target = await chats.CreateSessionAsync("Delete target");
        if (!bulkDelete) await chats.SetPinnedAsync(target.Id, true); // Explicit deletion is still supported.
        var other = await chats.CreateSessionAsync("Other unpinned session");
        var retainedStorage = CodingChangesMonitor.StorageDirectory(environment.Directory, retained.Id, Guid.NewGuid());
        var targetStorage = CodingChangesMonitor.StorageDirectory(environment.Directory, target.Id, Guid.NewGuid());
        var otherStorage = CodingChangesMonitor.StorageDirectory(environment.Directory, other.Id, Guid.NewGuid());
        foreach (var storage in new[] { retainedStorage, targetStorage, otherStorage })
        {
            Directory.CreateDirectory(Path.Combine(storage, "blobs"));
            await File.WriteAllTextAsync(Path.Combine(storage, "baseline.json"), "fixture baseline");
            await File.WriteAllTextAsync(Path.Combine(storage, "blobs", "fixture"), "original fixture bytes");
        }
        var unrelated = Path.Combine(environment.Directory, "Cache", "unrelated.txt");
        await File.WriteAllTextAsync(unrelated, "retain unrelated cache");
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        await settings.UpdateAsync(current => current with { ActiveSessionId = retained.Id });
        var activity = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
        var coordinator = new AssistantCoordinator(chats, environment.Get<IWorkflowRepository>(), environment.Get<IDocumentIngestor>(),
            environment.Get<IContextAssembler>(), environment.Get<IPromptTriggerRepository>(), environment.Get<IAssistantAttachmentRepository>(),
            environment.Get<IChatArtifactRepository>(), environment.Get<IConversationSnapshotRepository>(), null, settings, activity);

        await coordinator.HandleAsync(new(AssistantWebBridge.ProtocolVersion, bulkDelete ? "session.clear" : "session.delete",
            "cache-lifecycle", JsonSerializer.SerializeToElement(new { sessionId = target.Id })), (_, _, _) => Task.CompletedTask);

        Assert.Null(await chats.GetSessionAsync(target.Id));
        Assert.False(Directory.Exists(Path.GetDirectoryName(targetStorage)));
        Assert.True((await chats.GetSessionAsync(retained.Id))!.IsPinned);
        Assert.Equal("fixture baseline", await File.ReadAllTextAsync(Path.Combine(retainedStorage, "baseline.json")));
        Assert.Equal("original fixture bytes", await File.ReadAllTextAsync(Path.Combine(retainedStorage, "blobs", "fixture")));
        Assert.Equal(!bulkDelete, await chats.GetSessionAsync(other.Id) is not null);
        Assert.Equal(!bulkDelete, Directory.Exists(otherStorage));
        Assert.Equal("retain unrelated cache", await File.ReadAllTextAsync(unrelated));
        await CodingChangesMonitor.DeleteSessionStorageAsync(environment.Directory, target.Id); // Repeated cleanup is harmless.
    }
}
