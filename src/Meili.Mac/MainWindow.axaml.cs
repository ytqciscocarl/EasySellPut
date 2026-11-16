using System.Reflection;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Meili.Core.Settings;
using Meili.Mac.Platform;
using Meili.Mac.Views;

namespace Meili.Mac;

public partial class MainWindow : Window
{
    /// <summary>⌘1…⌘9 in navigation order.</summary>
    public static readonly IReadOnlyList<AppPage> PageOrder =
    [
        AppPage.Dashboard, AppPage.Watchlist, AppPage.Charts, AppPage.Options, AppPage.Strategy,
        AppPage.News, AppPage.Alerts, AppPage.DataSources, AppPage.Settings,
    ];


    private readonly SettingsStore _settings = AppServices.Get<SettingsStore>();
    private readonly MacTheme _theme = AppServices.Get<MacTheme>();
    private readonly LocalizationService _loc = AppServices.Get<LocalizationService>();
    private readonly AppLaunchOptions _options = AppServices.Get<AppLaunchOptions>();
    private readonly MacNotifier _notify = AppServices.Get<MacNotifier>();
    /// <summary>Pages are created on first visit and kept, like the Windows navigation cache.</summary>
    private readonly Dictionary<AppPage, Control> _pages = [];
    private readonly DispatcherTimer _bannerTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(12) };
    private bool _allowClose;
    private bool _asking;

    public MainWindow()
    {
        InitializeComponent();
        // Follow actual platform support: do not leave a second band below server decorations.
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsExtendedIntoWindowDecorationsProperty || e.Property == WindowStateProperty)
                UpdateWindowHeader();
        };
        Opened += (_, _) => UpdateWindowHeader();
        UpdateWindowHeader();
        DataContext = AppServices.Get<ShellViewModel>();
        SetupWorkspace();
        SetupNewsSplit();
        SetupBackgroundLifecycle();
        SetupUpdateNotifications();
        RestorePlacement();
        var hub = AppServices.Get<NavigationHub>();
        EventHandler<NavigationRequest> onRequest = (_, e) => Dispatcher.UIThread.Post(() => { if (!_closed) OnNavigateRequested(e); });
        hub.Requested += onRequest;
        _notify.Banner += ShowBanner;
        Closed += (_, _) => { _closed = true; hub.Requested -= onRequest; _notify.Banner -= ShowBanner; };
        _bannerTimer.Tick += (_, _) => { _bannerTimer.Stop(); Banner.IsVisible = false; };
        WizardBox.WatchSymbols = AppServices.Get<WatchlistStore>().AllSymbols;
        WizardBox.CommitCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(CommitWizard);
        Wizard.AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Key.Escape) { CloseWizard(); e.Handled = true; } }, RoutingStrategies.Bubble);
        Wizard.LostFocus += (_, _) => { if (!Wizard.IsKeyboardFocusWithin && !WizardBox.IsDropDownOpen) CloseWizard(); };
        AddHandler(TextInputEvent, OnWindowTextInput, RoutingStrategies.Tunnel);
        Closing += OnClosing;
        Navigate(StartPageFromArgs() ?? AppPage.Dashboard);
    }

    private void UpdateWindowHeader() => WindowHeader.IsVisible =
        IsExtendedIntoWindowDecorations && WindowState != WindowState.FullScreen;

    private bool _closed;
    internal AppPage CurrentPage { get; private set; }
    internal Control? CurrentView => PageHost.Content as Control;
    internal bool WizardVisible => Wizard.IsVisible;
    internal bool BannerVisible => Banner.IsVisible;
    internal string BannerText => BannerTitle.Text + " " + BannerBody.Text;

    public static string TitleKey(AppPage page) => "Nav_" + page;

    public void Navigate(AppPage page)
    {
        if (!_pages.TryGetValue(page, out var view))
            _pages[page] = view = CreatePage(page);
        CurrentPage = page;
        ShowMarketWorkspace();
        PageHost.Content = _presentationSuspended?null:view;
    }

    private static Control CreatePage(AppPage page) => page switch
    {
        AppPage.Dashboard => new DashboardPage(),
        AppPage.Watchlist => new WatchlistPage(),
        AppPage.Charts => new ChartsPage(),
        AppPage.Options => new OptionsPage(),
        AppPage.Strategy => new StrategyPage(),
        AppPage.News => new NewsPage(),
        AppPage.Alerts => new AlertsPage(),
        AppPage.DataSources => new DataSourcesPage(),
        _ => new SettingsPage(),
    };

    private void OnNavigateRequested(NavigationRequest e)
    {
        // D1: set the shared symbol first; the target page adopts it when it activates.
        if (!string.IsNullOrWhiteSpace(e.Symbol) && e.Page != AppPage.Alerts)
            AppServices.Get<SymbolContext>().Set(e.Symbol);
        ShowFromDock();
        Navigate(e.Page);
        if (!string.IsNullOrWhiteSpace(e.Symbol) && e.Page == AppPage.Alerts)
            AppServices.Get<AlertsViewModel>().PrepareForSymbol(e.Symbol);
    }

    /// <summary>Optional <c>--page &lt;name&gt;</c> start page.</summary>
    private static AppPage? StartPageFromArgs()
    {
        var args = Environment.GetCommandLineArgs();
        var i = Array.FindIndex(args, a => a.Equals("--page", StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length && Enum.TryParse<AppPage>(args[i + 1], true, out var p) ? p : null;
    }

    // ---------------- menu actions ----------------

    public void RefreshCurrent() { if (_activeWebsite is { } id) _webTabs[id].ReloadPage(); else CommandOf(CurrentView?.DataContext, "RefreshCommand", "RecheckCommand")?.Execute(null); }

    public void ExportCurrent() => CommandOf(CurrentView?.DataContext, "ExportCommand")?.Execute(null);

    /// <summary>The page view-model's command by name (⌘R refresh, ⌘E export), if it has one.</summary>
    internal static ICommand? CommandOf(object? viewModel, params string[] names)
    {
        if (viewModel is null) return null;
        foreach (var name in names)
            if (viewModel.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(viewModel) is ICommand c && c.CanExecute(null))
                return c;
        return null;
    }

    public void ToggleLanguage()
    {
        var next = _loc.Language == "zh-CN" ? "en-US" : "zh-CN";
        _loc.Apply(next);
        _settings.Update(s => s.Language = next);
    }

    public void ToggleTheme()
    {
        var next = _theme.IsDark ? AppThemeMode.Light : AppThemeMode.Dark;
        _theme.Apply(next);
        _settings.Update(s => s.Theme = next);
    }

    private void OnToggleLanguage(object? sender, RoutedEventArgs e) => ToggleLanguage();
    private void OnToggleTheme(object? sender, RoutedEventArgs e) => ToggleTheme();

    public void ShowAbout() =>
        ShowBanner(_loc["App_Title"], string.Format(_loc["Mac_AboutBody"], typeof(MainWindow).Assembly.GetName().Version?.ToString(3)));

    // ---------------- banner ----------------

    private void ShowBanner(string title, string body)
    {
        var changed=!Banner.IsVisible || BannerTitle.Text!=title || BannerBody.Text!=body;
        BannerTitle.Text = title;
        BannerBody.Text = body;
        Banner.IsVisible = true;
        if(changed)Meili.Mac.Views.Controls.AppMotion.Enter(Banner);
        _bannerTimer.Stop();
        _bannerTimer.Start();
    }

    private void OnDismissBanner(object? sender, RoutedEventArgs e)
    {
        _bannerTimer.Stop();
        Banner.IsVisible = false;
    }

    // ---------------- D2 keyboard wizard ----------------

    public void OpenWizard()
    {
        ShowFromDock();
        Wizard.IsVisible = true;
        WizardBox.FocusInput();
    }

    private void OnWindowTextInput(object? sender, TextInputEventArgs e)
    {
        if (_activeWebsite is not null || Wizard.IsVisible || FocusManager?.GetFocusedElement() is TextBox or ComboBox) return;
        if (e.Text is not { Length: 1 } t || !(char.IsAsciiLetterOrDigit(t[0]) || t[0] is '^' or '.')) return;
        ShowFromDock();
        Wizard.IsVisible = true;
        WizardBox.Begin(t);
        e.Handled = true;
    }

    private void CommitWizard()
    {
        var text = WizardBox.Text;
        CloseWizard();
        if (WatchlistStore.TryNormalizeSymbol(text, out var symbol, out _))
            AppServices.Get<NavigationHub>().Go(AppPage.Charts, symbol);
    }

    private void CloseWizard()
    {
        if (!Wizard.IsVisible) return;
        Wizard.IsVisible = false;
        WizardBox.Text = "";
    }

    // ---------------- window life cycle ----------------

    /// <summary>Called when the app really quits (⌘Q, menu, Dock › Quit, logout).</summary>
    public void AllowClose()
    {
        _allowClose = true;
        SavePlacement();
    }

    public void ShowFromDock()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose) return;
        switch (CloseDecision.Decide(_settings.Current.CloseAction, exitRequested: false, _options.ExitOnClose))
        {
            case CloseOutcome.Exit:
                AllowClose();
                Dispatcher.UIThread.Post(() => (Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown());
                return;
            case CloseOutcome.HideToTray:
                e.Cancel = true;
                HideToDock();
                return;
            default:
                e.Cancel = true;
                if (!_asking) Dispatcher.UIThread.Post(() => _ = AskCloseActionAsync());
                return;
        }
    }

    private void HideToDock()
    {
        SavePlacement();
        Hide();
        _notify.ShowHiddenHint();
    }

    private async System.Threading.Tasks.Task AskCloseActionAsync()
    {
        _asking = true;
        try
        {
            var choice = await new CloseAskDialog().ShowDialog<CloseAction?>(this);
            if (choice is null) return;
            _settings.Update(s => s.CloseAction = choice.Value);
            if (choice == CloseAction.Exit)
            {
                AllowClose();
                Close();
                Dispatcher.UIThread.Post(() => (Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown());
            }
            else
                HideToDock();
        }
        finally { _asking = false; }
    }

    private void RestorePlacement()
    {
        var p = _settings.Current.Window;
        if (p is null || p.Width < 600 || p.Height < 400) return;
        Width = p.Width;
        Height = p.Height;
        var screen = Screens.ScreenFromPoint(new PixelPoint((int)p.Left, (int)p.Top));
        if (screen is not null)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint((int)p.Left, (int)p.Top);
        }
        if (p.Maximized) WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        var maximized = WindowState == WindowState.Maximized;
        var (w, h, x, y) = (Width, Height, Position.X, Position.Y);
        _settings.Update(s => s.Window = new WindowPlacement { Left = x, Top = y, Width = w, Height = h, Maximized = maximized });
    }
}
