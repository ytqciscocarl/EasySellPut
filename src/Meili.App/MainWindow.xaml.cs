using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Meili.App.Services;
using Meili.App.Views.Pages;
using Meili.Core.Settings;
using Wpf.Ui.Controls;

namespace Meili.App;

public partial class MainWindow : FluentWindow
{
    private readonly SettingsStore _settings = AppServices.Get<SettingsStore>();
    private readonly ThemeService _theme = AppServices.Get<ThemeService>();
    private readonly LocalizationService _loc = AppServices.Get<LocalizationService>();
    private readonly AppLaunchOptions _options = AppServices.Get<AppLaunchOptions>();
    private readonly NavigationHub _nav = AppServices.Get<NavigationHub>();
    private readonly NotificationService _notify = AppServices.Get<NotificationService>();
    private bool _exitRequested;
    private bool _hidingToTray;
    private FrameworkElement? _currentPage;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = AppServices.Get<ShellViewModel>();
        SetupWorkspace();
        SetupNewsSplit();
        SetupBackgroundLifecycle();
        SetupUpdateNotifications();
        RestorePlacement();
        RootNavigation.Loaded += (_, _) =>
        {
            // The pane template is transparent over the window; paint it with the slightly darker sidebar token.
            if (RootNavigation.Template?.FindName("PaneGrid", RootNavigation) is System.Windows.FrameworkElement pane)
            {
                pane.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty, "SidebarBackgroundBrush");
                pane.Margin = new Thickness(0);
            }
            if (_currentPage is null) RootNavigation.Navigate(StartPageFromArgs() ?? typeof(DashboardPage));
        };
        Loaded += (_, _) =>
        {
            // System theme watching needs a loaded window handle.
            if (_theme.Mode == AppThemeMode.System) _theme.Apply(AppThemeMode.System, this);
            UpdateThemeIcon();
            _notify.Attach(this);
        };
        _theme.ThemeChanged += (_, _) => UpdateThemeIcon();
        StateChanged += OnStateChanged;
        Closing += OnClosing;
        _nav.Requested += OnNavigateRequested;
        _notify.ExitRequested += (_, _) => RequestExit();
        RootNavigation.Navigated += (_, e) => { _currentPage = e.Page as FrameworkElement; SyncMarketNavigation(); };
        WizardBox.WatchSymbols = AppServices.Get<WatchlistStore>().AllSymbols;
        WizardBox.CommitCommand = new RelayCommand(CommitWizard);
        PreviewKeyDown += OnShortcutKeyDown;
        PreviewTextInput += OnWindowTextInput;
        Wizard.IsKeyboardFocusWithinChanged += (_, e) => { if (e.NewValue is false) CloseWizard(); };
    }

    // ---------------- B3: self-check hooks ----------------

    internal Type? CurrentPageType => _currentPage?.GetType();
    internal bool WizardVisible => Wizard.Visibility == Visibility.Visible;

    internal void PrepareForSmoke()
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowState = WindowState.Normal;
        Left = -32000; Top = -32000; Width = 1320; Height = 840;
        ShowActivated = false;
        ShowInTaskbar = false;
        _smoke = true;
    }

    internal void NavigateForSmoke(Type page) => RootNavigation.Navigate(page);

    internal void OpenWizardForSmoke(string text)
    {
        Wizard.Visibility = Visibility.Visible;
        WizardBox.Text = text;
    }

    internal void CloseWizardForSmoke() => CloseWizard();

    /// <summary>0.4.1 theme check: the window parts that kept the old theme in 0.4.0, with a sample point in window pixels.</summary>
    internal IEnumerable<(string Part, FrameworkElement Element, Point Sample)> ThemeSurfacesForSmoke() =>
    [
        ("title-bar", TitleBar, new Point(320, 22)),
        ("navigation", RootNavigation, new Point(40, 560)),
        ("page", (FrameworkElement?)_currentPage ?? RootNavigation, new Point(1000, 400)),
        ("status-bar", StatusBarSurface, StatusBarSurface.TranslatePoint(new Point(8, StatusBarSurface.ActualHeight / 2), this)),
    ];

    private bool _smoke;

    // ---------------- D2: keyboard ----------------

    private void OnShortcutKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (KeyboardShortcuts.PageFor(key, Keyboard.Modifiers) is { } page)
        {
            CloseWizard();
            ShowMarketWorkspace();
            RootNavigation.Navigate(page);
            e.Handled = true;
            return;
        }
        if (key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (_activeWebsite is { } id) { _webTabs[id].ReloadPage(); e.Handled = true; return; }
            var cmd = KeyboardShortcuts.RefreshCommandOf(_currentPage?.DataContext);
            if (cmd?.CanExecute(null) == true) cmd.Execute(null);
            e.Handled = true;
            return;
        }
        if (key == Key.Escape && Wizard.Visibility == Visibility.Visible)
        {
            CloseWizard();
            e.Handled = true;
        }
    }

    private void OnWindowTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_activeWebsite is not null || Wizard.Visibility == Visibility.Visible) return;
        if (!KeyboardShortcuts.StartsWizard(e.Text, IsTextInput(Keyboard.FocusedElement as DependencyObject))) return;
        WatchlistSync();
        Wizard.Visibility = Visibility.Visible;
        WizardBox.Text = e.Text.ToUpperInvariant();
        WizardBox.FocusInput();
        // Put the caret after the first character instead of selecting it, so typing continues naturally.
        Dispatcher.BeginInvoke(() =>
        {
            if (Keyboard.FocusedElement is System.Windows.Controls.TextBox tb) { tb.SelectionLength = 0; tb.CaretIndex = tb.Text.Length; }
            _ = WizardBox.RefreshAsync(showAll: false, open: true);
        }, System.Windows.Threading.DispatcherPriority.Input);
        e.Handled = true;
    }

    private void WatchlistSync() => WizardBox.WatchSymbols = AppServices.Get<WatchlistStore>().AllSymbols;

    private static bool IsTextInput(DependencyObject? d)
    {
        for (var x = d; x is not null; x = x is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (x is TextBoxBase or System.Windows.Controls.PasswordBox || x is ComboBox { IsEditable: true }) return true;
        return false;
    }

    private void CommitWizard()
    {
        var text = WizardBox.Text;
        CloseWizard();
        if (!WatchlistStore.TryNormalizeSymbol(text, out var symbol, out _)) return;
        _nav.Go(AppPage.Charts, symbol);
    }

    private void CloseWizard()
    {
        if (Wizard.Visibility != Visibility.Visible) return;
        Wizard.Visibility = Visibility.Collapsed;
        WizardBox.Text = "";
        _currentPage?.Focus();
    }

    public void RequestExit()
    {
        _exitRequested = true;
        SavePlacement();
        Application.Current.Shutdown();
    }

    /// <summary>Optional <c>--page &lt;name&gt;</c> start page (used for screenshots and deep links).</summary>
    private static Type? StartPageFromArgs()
    {
        var args = Environment.GetCommandLineArgs();
        var i = Array.FindIndex(args, a => a.Equals("--page", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= args.Length) return null;
        return typeof(DashboardPage).Assembly.GetType($"Meili.App.Views.Pages.{args[i + 1]}Page", false, true);
    }

    private static Type PageType(AppPage page) => page switch
    {
        AppPage.Dashboard => typeof(DashboardPage),
        AppPage.Watchlist => typeof(WatchlistPage),
        AppPage.Charts => typeof(ChartsPage),
        AppPage.Options => typeof(OptionsPage),
        AppPage.Strategy => typeof(StrategyPage),
        AppPage.News => typeof(NewsPage),
        AppPage.Alerts => typeof(AlertsPage),
        AppPage.DataSources => typeof(DataSourcesPage),
        _ => typeof(SettingsPage),
    };

    private void OnNavigateRequested(object? sender, NavigationRequest e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            // D1: set the shared symbol first; the target page adopts it when it activates (or at once if it is
            // already showing), so every symbol-aware page is reachable with a symbol — not only K-line and alerts.
            if (!string.IsNullOrWhiteSpace(e.Symbol) && e.Page != AppPage.Alerts)
                AppServices.Get<SymbolContext>().Set(e.Symbol);
            ShowMarketWorkspace();
            RootNavigation.Navigate(PageType(e.Page));
            if (!string.IsNullOrWhiteSpace(e.Symbol) && e.Page == AppPage.Alerts)
                AppServices.Get<AlertsViewModel>().PrepareForSymbol(e.Symbol);
        });
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (_exitRequested || _options.ExitOnClose || _hidingToTray) return;
        if (WindowState == WindowState.Minimized)
            HideToTray();
    }

    private bool _asking;

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        switch (CloseDecision.Decide(_settings.Current.CloseAction, _exitRequested, _options.ExitOnClose))
        {
            case CloseOutcome.Exit:
                // With OnExplicitShutdown, closing the window alone would leave the process running in the tray.
                if (!_exitRequested && !_options.ExitOnClose) { e.Cancel = true; Dispatcher.BeginInvoke(RequestExit); return; }
                SavePlacement();
                return;
            case CloseOutcome.HideToTray:
                e.Cancel = true;
                HideToTray();
                _notify.ShowHiddenToTray();
                return;
            default:
                e.Cancel = true;
                if (!_asking) Dispatcher.BeginInvoke(AskCloseActionAsync);
                return;
        }
    }

    /// <summary>D4: first close — ask once, remember the answer (changeable in Settings).</summary>
    private async Task AskCloseActionAsync()
    {
        _asking = true;
        try
        {
            var box = new Wpf.Ui.Controls.MessageBox
            {
                Title = _loc["Close_AskTitle"],
                Content = new System.Windows.Controls.TextBlock { Text = _loc["Close_AskBody"], TextWrapping = TextWrapping.Wrap, MaxWidth = 420, FontSize = 14 },
                PrimaryButtonText = _loc["Close_ToTray"],
                SecondaryButtonText = _loc["Close_Exit"],
                CloseButtonText = _loc["Close_Cancel"],
                IsSecondaryButtonEnabled = true,
                Owner = this,
            };
            var r = await box.ShowDialogAsync();
            var choice = r switch
            {
                Wpf.Ui.Controls.MessageBoxResult.Primary => CloseAction.MinimizeToTray,
                Wpf.Ui.Controls.MessageBoxResult.Secondary => CloseAction.Exit,
                _ => CloseAction.Ask,
            };
            if (choice == CloseAction.Ask) return;            // cancelled: keep the window, ask again next time
            _settings.Update(s => s.CloseAction = choice);
            if (choice == CloseAction.Exit) RequestExit();
            else { HideToTray(); _notify.ShowHiddenToTray(); }
        }
        finally { _asking = false; }
    }

    private void HideToTray()
    {
        if (_hidingToTray) return;
        _hidingToTray = true;
        try
        {
            SavePlacement();
            // Without a tray (including --no-alerts), retain a taskbar restore path.
            if(!_notify.HasTrayIcon){ShowInTaskbar=true;WindowState=WindowState.Minimized;return;}
            WindowState = WindowState.Normal;
            ShowInTaskbar = false;
            Hide();
        }
        finally { _hidingToTray = false; }
    }

    private void OnToggleLanguage(object sender, RoutedEventArgs e)
    {
        var next = _loc.Language == "zh-CN" ? "en-US" : "zh-CN";
        _loc.Apply(next);
        _settings.Update(s => s.Language = next);
    }

    private void OnToggleTheme(object sender, RoutedEventArgs e)
    {
        var next = _theme.Toggle();
        _theme.Apply(next, this);
        _settings.Update(s => s.Theme = next);
    }

    private void UpdateThemeIcon() =>
        ThemeButton.Content = CreateNavigationIcon(NavigationIcons.Theme,ThemeButton,18);

    private void RestorePlacement()
    {
        var p = _settings.Current.Window;
        if (p is null || p.Width < 600 || p.Height < 400) return;
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                          SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!vs.IntersectsWith(new Rect(p.Left, p.Top, p.Width, p.Height))) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = p.Left; Top = p.Top; Width = p.Width; Height = p.Height;
        if (p.Maximized) WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        if (_smoke) return;
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _settings.Update(s => s.Window = new WindowPlacement
        {
            Left = r.Left, Top = r.Top, Width = r.Width, Height = r.Height,
            Maximized = WindowState == WindowState.Maximized,
        });
    }
}