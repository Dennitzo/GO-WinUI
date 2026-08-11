using GoWinUI.App.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GoWinUI.App.Pages;

public sealed partial class LogsPage : Page
{
    private bool _scrollQueued;

    public LogsPage()
    {
        ViewModel = App.Current.GetService<LogsViewModel>();
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    public LogsViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Initialize(DispatcherQueue);
        QueueScrollToEnd();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => ViewModel.Deactivate();

    private void OnLogTextChanged(object sender, TextChangedEventArgs e) => QueueScrollToEnd();

    private void QueueScrollToEnd()
    {
        if (_scrollQueued)
        {
            return;
        }

        _scrollQueued = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _scrollQueued = false;
            LogTextBox.Select(LogTextBox.Text.Length, 0);
            if (FindDescendantScrollViewer(LogTextBox) is { } scrollViewer)
            {
                scrollViewer.ChangeView(null, scrollViewer.ScrollableHeight, null, disableAnimation: true);
            }
        });
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject parent)
    {
        var childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is ScrollViewer scrollViewer)
            {
                return scrollViewer;
            }

            if (FindDescendantScrollViewer(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
