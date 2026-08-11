using GoWinUI.App.Pages;
using GoWinUI.App.Services;
using GoWinUI.App.ViewModels;
using GoWinUI.Core.Models;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using System.Globalization;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinRT.Interop;
using Microsoft.Extensions.Logging;

namespace GoWinUI.App;

public sealed partial class MainWindow : Window
{
    private const double MinimumPaneWidth = 280;
    private const double MaximumPaneWidth = 520;
    private const double MinimumContentWidth = 440;
    private readonly Dictionary<string, Type> _routes = new(StringComparer.Ordinal)
    {
        ["assistant"] = typeof(AssistantPage),
        ["projects"] = typeof(ProjectsPage),
        ["logs"] = typeof(LogsPage),
        ["settings"] = typeof(SettingsPage),
    };
    private readonly SettingsCoordinator _settings;
    private readonly ILogger<MainWindow> _logger;
    private readonly DispatcherQueueTimer _saveTimer;
    private readonly AppWindow _appWindow;
    private readonly nint _windowHandle;
    private bool _restored;
    private bool _isClosing;
    private bool _suppressSelection;
    private bool _closePreparationStarted;
    private bool _allowClose;
    private WindowPlacement _lastNormalPlacement = new();
    private WindowDisplayState _lastNonMinimizedState = WindowDisplayState.Normal;

    public MainWindow(
        ShellViewModel viewModel,
        SettingsCoordinator settings,
        ILogger<MainWindow> logger)
    {
        ViewModel = viewModel;
        _settings = settings;
        _logger = logger;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        _windowHandle = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_windowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        var iconPath = ApplicationAssets.ResolvePath("Assets", "AppLogo.ico");
        if (File.Exists(iconPath))
        {
            _appWindow.SetIcon(iconPath);
        }
        ConfigureTitleBar();

        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(450);
        _saveTimer.Tick += OnSaveTimerTick;
        _appWindow.Changed += OnAppWindowChanged;
        _appWindow.Closing += OnAppWindowClosing;
        RootNavigation.PaneOpened += OnPaneChanged;
        RootNavigation.PaneClosed += OnPaneChanged;
        Closed += OnClosed;
    }

    public ShellViewModel ViewModel { get; }

    internal Func<Task>? BeforeCloseAsync { get; set; }

    public async Task SaveStateAsync()
    {
        if (!_restored)
        {
            return;
        }

        var state = _isClosing ? _lastNormalPlacement.State : GetPresenterState();
        var placement = !_isClosing && IsPresenterRestored()
            ? CaptureNormalPlacement()
            : _lastNormalPlacement with { State = state };
        var selectedRoute = GetSelectedRoute();
        await _settings.UpdateAsync(current => current with
        {
            Window = placement,
            NavigationPaneWidth = RootNavigation.OpenPaneLength,
            IsNavigationPaneOpen = RootNavigation.IsPaneOpen,
            LastRoute = selectedRoute,
        });
    }

    public void BringToForeground()
    {
        if (_appWindow.Presenter is OverlappedPresenter presenter
            && presenter.State == OverlappedPresenterState.Minimized)
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
    }

    private async void OnNavigationLoaded(object sender, RoutedEventArgs e)
    {
        if (_restored)
        {
            return;
        }

        _restored = true;
        var settings = _settings.Current;
        RootNavigation.OpenPaneLength = Math.Clamp(
            settings.NavigationPaneWidth,
            MinimumPaneWidth,
            MaximumPaneWidth);
        RootNavigation.IsPaneOpen = settings.IsNavigationPaneOpen;
        RestoreWindow(settings.Window);
        NavigateTo(settings.LastRoute);
        await Task.Yield();
        ScheduleSave();
    }

    private void RestoreWindow(WindowPlacement placement)
    {
        var requestedX = ToInt(placement.X, 120);
        var requestedY = ToInt(placement.Y, 80);
        var displayArea = FindSavedDisplay(placement.MonitorId)
            ?? DisplayArea.GetFromPoint(
                new PointInt32(requestedX, requestedY),
                DisplayAreaFallback.Primary);
        if (displayArea is null)
        {
            return;
        }

        var work = displayArea.WorkArea;
        _appWindow.Move(new PointInt32(
            Math.Clamp(requestedX, work.X, work.X + Math.Max(0, work.Width - 1)),
            Math.Clamp(requestedY, work.Y, work.Y + Math.Max(0, work.Height - 1))));

        var currentDpi = Math.Max(96, GetDpi());
        var savedDpi = double.IsFinite(placement.SavedDpi) && placement.SavedDpi >= 48
            ? placement.SavedDpi
            : 96;
        var scale = currentDpi / savedDpi;
        var requestedWidth = ScaleToInt(placement.Width, scale, 1280);
        var requestedHeight = ScaleToInt(placement.Height, scale, 820);
        var minimumWidth = Math.Min(work.Width, ScaleToInt(720, currentDpi / 96, 720));
        var minimumHeight = Math.Min(work.Height, ScaleToInt(540, currentDpi / 96, 540));
        var width = Math.Clamp(requestedWidth, minimumWidth, Math.Max(1, work.Width));
        var height = Math.Clamp(requestedHeight, minimumHeight, Math.Max(1, work.Height));
        var x = Math.Clamp(requestedX, work.X, work.X + work.Width - width);
        var y = Math.Clamp(requestedY, work.Y, work.Y + work.Height - height);
        _appWindow.MoveAndResize(new RectInt32(x, y, width, height));
        _lastNormalPlacement = new WindowPlacement(
            x,
            y,
            width,
            height,
            GetMonitorId(displayArea),
            currentDpi,
            WindowDisplayState.Normal);
        _lastNonMinimizedState = placement.State;

        if (placement.State == WindowDisplayState.Maximized
            && _appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
    }

    private async void OnSelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppressSelection)
        {
            return;
        }

        var route = args.IsSettingsSelected
            ? "settings"
            : args.SelectedItemContainer?.Tag as string;
        if (route is null || !_routes.TryGetValue(route, out var pageType))
        {
            return;
        }

        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            if (ContentFrame.Content is AssistantPage assistantPage)
            {
                await assistantPage.FlushDraftAsync();
            }

            ContentFrame.Navigate(
                pageType,
                null,
                args.RecommendedNavigationTransitionInfo);
        }

        await _settings.UpdateAsync(current => current with { LastRoute = route });
    }

    private void NavigateTo(string requestedRoute)
    {
        var route = _routes.ContainsKey(requestedRoute) ? requestedRoute : "assistant";
        _suppressSelection = true;
        try
        {
            if (route == "settings")
            {
                RootNavigation.SelectedItem = RootNavigation.SettingsItem;
            }
            else
            {
                RootNavigation.SelectedItem = RootNavigation.MenuItems
                    .OfType<NavigationViewItem>()
                    .FirstOrDefault(item => string.Equals(item.Tag as string, route, StringComparison.Ordinal));
            }
        }
        finally
        {
            _suppressSelection = false;
        }

        ContentFrame.Navigate(_routes[route]);
    }

    private void OnNavigated(object sender, NavigationEventArgs e)
    {
        ViewModel.ActivePageTitle = e.SourcePageType.Name switch
        {
            nameof(AssistantPage) => "AI Assistent",
            nameof(ProjectsPage) => "Projekte",
            nameof(LogsPage) => "Logs",
            nameof(SettingsPage) => "Einstellungen",
            _ => "GO",
        };
    }

    private void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
    {
        e.Handled = true;
        var pageType = e.SourcePageType?.FullName ?? "unknown";
        AppLog.NavigationFailed(_logger, e.Exception, pageType);
        ViewModel.ActivePageTitle = "Seite nicht verfügbar";

        var content = new StackPanel
        {
            MaxWidth = 520,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 12,
        };
        content.Children.Add(new FontIcon { Glyph = "\uEA39", FontSize = 32 });
        content.Children.Add(new TextBlock
        {
            Text = "Die Seite konnte nicht geladen werden.",
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
        });
        content.Children.Add(new TextBlock
        {
            Text = "Prüfe bei der Assistentenansicht insbesondere die Microsoft Edge WebView2 Evergreen Runtime.",
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });
        ContentFrame.Content = content;
    }

    private void OnPaneResizeDelta(object sender, DragDeltaEventArgs e)
    {
        var maximum = Math.Max(
            MinimumPaneWidth,
            Math.Min(MaximumPaneWidth, RootNavigation.ActualWidth - MinimumContentWidth));
        RootNavigation.OpenPaneLength = Math.Clamp(
            RootNavigation.OpenPaneLength + e.HorizontalChange,
            MinimumPaneWidth,
            maximum);
    }

    private void OnPaneResizeCompleted(object sender, DragCompletedEventArgs e) => ScheduleSave();

    private void OnPaneChanged(NavigationView sender, object args) => ScheduleSave();

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!_restored || _isClosing)
        {
            return;
        }

        if (sender.Presenter is OverlappedPresenter presenter
            && presenter.State == OverlappedPresenterState.Restored
            && (args.DidPositionChange || args.DidSizeChange || args.DidPresenterChange))
        {
            _lastNormalPlacement = CaptureNormalPlacement();
            _lastNonMinimizedState = WindowDisplayState.Normal;
        }
        else if (sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized })
        {
            _lastNonMinimizedState = WindowDisplayState.Maximized;
        }

        if (args.DidPositionChange || args.DidSizeChange || args.DidPresenterChange)
        {
            ScheduleSave();
        }
    }

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        if (_closePreparationStarted)
        {
            return;
        }

        _closePreparationStarted = true;
        if (ContentFrame.Content is AssistantPage assistantPage)
        {
            await assistantPage.FlushDraftAsync();
        }

        try
        {
            await SaveStateAsync();
        }
        catch (Exception exception)
        {
            AppLog.WindowStateSaveFailed(_logger, exception);
        }

        try
        {
            if (BeforeCloseAsync is not null)
            {
                await BeforeCloseAsync();
            }
        }
        catch (Exception exception)
        {
            AppLog.ShutdownCleanupFailed(_logger, exception);
        }
        finally
        {
            _allowClose = true;
            Close();
        }
    }

    private void ScheduleSave()
    {
        if (!_restored || _isClosing)
        {
            return;
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async void OnSaveTimerTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        await SaveStateAsync();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        try
        {
            if (IsPresenterRestored())
            {
                _lastNormalPlacement = CaptureNormalPlacement();
                _lastNonMinimizedState = WindowDisplayState.Normal;
            }
            else if (_appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized })
            {
                _lastNonMinimizedState = WindowDisplayState.Maximized;
            }

            _lastNormalPlacement = _lastNormalPlacement with { State = _lastNonMinimizedState };
        }
        catch (InvalidOperationException)
        {
            // The last AppWindow.Changed snapshot remains valid during teardown.
        }

        _isClosing = true;
        _saveTimer.Stop();
        _saveTimer.Tick -= OnSaveTimerTick;
        _appWindow.Changed -= OnAppWindowChanged;
        _appWindow.Closing -= OnAppWindowClosing;
        RootNavigation.PaneOpened -= OnPaneChanged;
        RootNavigation.PaneClosed -= OnPaneChanged;
    }

    private WindowPlacement CaptureNormalPlacement()
    {
        var displayArea = DisplayArea.GetFromWindowId(
            _appWindow.Id,
            DisplayAreaFallback.Primary);
        return new WindowPlacement(
            _appWindow.Position.X,
            _appWindow.Position.Y,
            _appWindow.Size.Width,
            _appWindow.Size.Height,
            displayArea is null ? null : GetMonitorId(displayArea),
            GetDpi(),
            WindowDisplayState.Normal);
    }

    private WindowDisplayState GetPresenterState()
    {
        return _appWindow.Presenter is OverlappedPresenter presenter
            ? presenter.State switch
            {
                OverlappedPresenterState.Maximized => WindowDisplayState.Maximized,
                OverlappedPresenterState.Restored => WindowDisplayState.Normal,
                _ => _lastNonMinimizedState,
            }
            : _lastNonMinimizedState;
    }

    private bool IsPresenterRestored() =>
        _appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored };

    private string GetSelectedRoute()
    {
        if (RootNavigation.SelectedItem == RootNavigation.SettingsItem)
        {
            return "settings";
        }

        return (RootNavigation.SelectedItem as NavigationViewItem)?.Tag as string
            ?? _settings.Current.LastRoute;
    }

    private double GetDpi()
    {
        var nativeDpi = GetDpiForWindow(_windowHandle);
        if (nativeDpi >= 48)
        {
            return nativeDpi;
        }

        var scale = RootNavigation.XamlRoot?.RasterizationScale ?? 1;
        return double.IsFinite(scale) && scale > 0 ? scale * 96 : 96;
    }

    private static DisplayArea? FindSavedDisplay(string? monitorId)
    {
        if (string.IsNullOrWhiteSpace(monitorId))
        {
            return null;
        }

        return DisplayArea.FindAll().FirstOrDefault(
            area => string.Equals(GetMonitorId(area), monitorId, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetMonitorId(DisplayArea area) => area.DisplayId.Value.ToString("X16", CultureInfo.InvariantCulture);

    private static int ScaleToInt(double value, double scale, int fallback)
    {
        if (!double.IsFinite(value) || value <= 0 || !double.IsFinite(scale) || scale <= 0)
        {
            return fallback;
        }

        return (int)Math.Clamp(
            Math.Round(value * scale, MidpointRounding.AwayFromZero),
            1,
            int.MaxValue);
    }

    private static int ToInt(double value, int fallback)
    {
        return double.IsFinite(value)
            ? (int)Math.Clamp(Math.Round(value), int.MinValue, int.MaxValue)
            : fallback;
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint windowHandle);
}
