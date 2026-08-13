using GoAi.Server.App.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using WinRT.Interop;

namespace GoAi.Server.App;

public sealed partial class MainWindow : Window
{
    private readonly DispatcherQueueTimer _refreshTimer;
    private readonly AppWindow _appWindow;
    private bool _allowClose;
    private bool _closing;

    public MainWindow(ServerDashboardViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        var windowHandle = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(windowHandle));
        ConfigureTitleBar();
        _appWindow.Resize(new SizeInt32(1380, 880));
        _appWindow.Closing += OnClosing;
        Closed += OnClosed;
        _refreshTimer = DispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromSeconds(5);
        _refreshTimer.Tick += OnRefreshTimerTick;
        _refreshTimer.Start();
        _ = RefreshAsync();
    }

    public ServerDashboardViewModel ViewModel { get; }

    internal Func<Task>? PrepareShutdownAsync { get; set; }

    public void BringToForeground()
    {
        if (_appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        Activate();
    }

    private void ConfigureTitleBar()
    {
        var titleBar = _appWindow.TitleBar;
        titleBar.ExtendsContentIntoTitleBar = true;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 248, 244, 255);
        titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(180, 248, 244, 255);
        titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(28, 255, 255, 255);
        titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(48, 255, 255, 255);
    }

    private async Task RefreshAsync()
    {
        try
        {
            await ViewModel.RefreshAsync();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ViewModel.GatewayDetail = $"Statusaktualisierung fehlgeschlagen ({exception.GetType().Name}).";
        }
    }

    private async void OnRefreshTimerTick(DispatcherQueueTimer sender, object args) => await RefreshAsync();

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var route = (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "overview";
        OverviewPanel.Visibility = route == "overview" ? Visibility.Visible : Visibility.Collapsed;
        ModelsPanel.Visibility = route == "models" ? Visibility.Visible : Visibility.Collapsed;
        ServicesPanel.Visibility = route == "services" ? Visibility.Visible : Visibility.Collapsed;
        SecurityPanel.Visibility = route == "security" ? Visibility.Visible : Visibility.Collapsed;
        LogsPanel.Visibility = route == "logs" ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnCreateApiKeyClick(object sender, RoutedEventArgs e)
    {
        _ = await ViewModel.CreateApiKeyAsync();
    }

    private void OnCopyApiKeyClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ViewModel.OneTimeApiKey))
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(ViewModel.OneTimeApiKey);
        Clipboard.SetContent(package);
        ViewModel.SecurityMessage = "Schlüssel wurde in die Zwischenablage kopiert.";
    }

    private void OnHideApiKeyClick(object sender, RoutedEventArgs e) => ViewModel.HideOneTimeApiKey();

    private async void OnRevokeApiKeyClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string keyId } && !string.IsNullOrWhiteSpace(keyId))
        {
            await ViewModel.RevokeApiKeyAsync(keyId);
        }
    }

    private async void OnSaveLmStudioTokenClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(LmStudioTokenBox.Password))
        {
            ViewModel.SecurityMessage = "Bitte zuerst einen LM-Studio-Token eingeben.";
            return;
        }

        await ViewModel.SaveLmStudioTokenAsync(LmStudioTokenBox.Password);
        LmStudioTokenBox.Password = string.Empty;
        await RefreshAsync();
    }

    private void OnLogTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.Select(textBox.Text.Length, 0);
            if (FindDescendantScrollViewer(textBox) is { } scrollViewer)
            {
                scrollViewer.ChangeView(null, scrollViewer.ScrollableHeight, null, disableAnimation: true);
            }
        }
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        if (_closing)
        {
            return;
        }

        _closing = true;
        _refreshTimer.Stop();
        if (PrepareShutdownAsync is not null)
        {
            await PrepareShutdownAsync();
        }

        _allowClose = true;
        Close();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _refreshTimer.Stop();
        _refreshTimer.Tick -= OnRefreshTimerTick;
        _appWindow.Closing -= OnClosing;
        ViewModel.DisposeSubscriptions();
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
