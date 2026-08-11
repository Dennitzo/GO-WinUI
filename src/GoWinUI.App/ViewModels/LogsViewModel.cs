using CommunityToolkit.Mvvm.ComponentModel;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using Microsoft.UI.Dispatching;
using System.Text;

namespace GoWinUI.App.ViewModels;

public sealed partial class LogsViewModel : ObservableObject, IDisposable
{
    private const int MaximumVisibleEntries = 5_000;
    private readonly ISessionLog _log;
    private DispatcherQueue? _dispatcher;
    private bool _subscribed;
    private int _refreshQueued;

    public LogsViewModel(ISessionLog log)
    {
        _log = log;
    }

    [ObservableProperty]
    public partial string LogText { get; private set; } = string.Empty;

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
        var entries = _log.Snapshot().TakeLast(MaximumVisibleEntries);
        var text = new StringBuilder();
        foreach (var entry in entries)
        {
            if (text.Length > 0)
            {
                text.AppendLine();
            }

            AppendEntry(text, entry);
        }

        LogText = text.ToString();
    }

    public void Deactivate()
    {
        if (_subscribed)
        {
            _log.EntryAdded -= OnEntryAdded;
            _subscribed = false;
        }

        _dispatcher = null;
        Interlocked.Exchange(ref _refreshQueued, 0);
    }

    public void Dispose() => Deactivate();

    private void OnEntryAdded(object? sender, SessionLogEntry entry)
    {
        if (_dispatcher is null || Interlocked.Exchange(ref _refreshQueued, 1) != 0)
        {
            return;
        }

        if (!_dispatcher.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            Refresh();
        }))
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
        }
    }

    private static void AppendEntry(StringBuilder text, SessionLogEntry entry)
    {
        text.Append('[')
            .Append(entry.Timestamp.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture))
            .Append("] ")
            .Append(entry.Message.ReplaceLineEndings(" ").Trim());

        if (!string.IsNullOrWhiteSpace(entry.Exception))
        {
            text.AppendLine()
                .Append("           ")
                .Append(entry.Exception.ReplaceLineEndings(" ").Trim());
        }
    }
}
