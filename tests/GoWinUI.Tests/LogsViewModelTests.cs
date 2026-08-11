using GoWinUI.App.ViewModels;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;

namespace GoWinUI.Tests;

public sealed class LogsViewModelTests
{
    [Fact]
    public void RefreshBuildsTimestampedTextFromTheLatestFiveThousandEntries()
    {
        var source = new StubSessionLog(Enumerable.Range(1, 6_000)
            .Select(index => new SessionLogEntry(
                index,
                DateTimeOffset.UnixEpoch.AddSeconds(index),
                "Information",
                "Test",
                $"Entry {index}",
                index,
                null,
                new Dictionary<string, string?>()))
            .ToArray());
        using var viewModel = new LogsViewModel(source);
        viewModel.Refresh();

        var lines = viewModel.LogText.Split(Environment.NewLine);
        Assert.Equal(5_000, lines.Length);
        Assert.Matches(@"^\[\d{2}:\d{2}:\d{2}\] Entry 1001$", lines[0]);
        Assert.Matches(@"^\[\d{2}:\d{2}:\d{2}\] Entry 6000$", lines[^1]);
        Assert.DoesNotContain("Entry 1000", viewModel.LogText, StringComparison.Ordinal);
    }

    private sealed class StubSessionLog(IReadOnlyList<SessionLogEntry> entries) : ISessionLog
    {
        public event EventHandler<SessionLogEntry>? EntryAdded
        {
            add { }
            remove { }
        }

        public IReadOnlyList<SessionLogEntry> Snapshot(
            string? minimumLevel = null,
            string? category = null,
            string? search = null) => entries;

        public void Clear()
        {
        }

        public Task ExportAsync(
            Stream destination,
            bool asJson,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
