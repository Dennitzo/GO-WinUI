using CommunityToolkit.Mvvm.ComponentModel;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using Microsoft.UI.Dispatching;
using System.Collections.ObjectModel;

namespace GoWinUI.App.ViewModels;

public sealed partial class LogsViewModel : ObservableObject, IDisposable
{
    private readonly ISessionLog _log;
    private DispatcherQueue? _dispatcher;
    private bool _subscribed;

    public LogsViewModel(ISessionLog log)
    {
        _log = log;
    }

    public ObservableCollection<SessionLogEntry> Entries { get; } = [];

    [ObservableProperty]
    public partial string MinimumLevel { get; set; } = "Trace";

    [ObservableProperty]
    public partial string Category { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Search { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    [ObservableProperty]
    public partial bool AutoScroll { get; set; } = true;

    public void Initialize(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        if (!_subscribed)
        {
            _subscribed = true;
            _log.EntryAdded += OnEntryAdded;
        }

        Refresh();
    }

    public void Refresh()
    {
        var entries = _log.Snapshot(
            string.Equals(MinimumLevel, "Trace", StringComparison.OrdinalIgnoreCase) ? null : MinimumLevel,
            string.IsNullOrWhiteSpace(Category) ? null : Category.Trim(),
            string.IsNullOrWhiteSpace(Search) ? null : Search.Trim());
        Entries.Clear();
        foreach (var entry in entries)
        {
            Entries.Add(entry);
        }
    }

    public void Clear()
    {
        _log.Clear();
        Refresh();
    }

    public Task ExportAsync(Stream destination, bool asJson, CancellationToken cancellationToken = default) =>
        _log.ExportAsync(destination, asJson, cancellationToken);

    public void Dispose()
    {
        if (_subscribed)
        {
            _log.EntryAdded -= OnEntryAdded;
            _subscribed = false;
        }
    }

    private void OnEntryAdded(object? sender, SessionLogEntry entry)
    {
        if (IsPaused)
        {
            return;
        }

        _dispatcher?.TryEnqueue(Refresh);
    }
}
