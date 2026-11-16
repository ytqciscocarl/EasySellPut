using Meili.Core.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using Meili.Core.Quotes;
using Microsoft.Toolkit.Uwp.Notifications;
using Meili.Core.Alerts;
using Meili.Core.Settings;

namespace Meili.App.Services;

/// <summary>Tray icon plus Windows toast notifications. Failures are written to the log folder, never shown as dialogs.</summary>
public sealed class NotificationService : IAlertNotifier, IDisposable
{
    public const string AppUserModelId = "MeiliDaogu.SellerTerminal";

    private readonly LocalizationService _loc;
    private readonly AppLaunchOptions _options;
    private readonly DataDirectoryInfo _dataDir;
    private readonly NavigationHub _nav;
    private readonly QuoteHub _quotes;private readonly SymbolContext _symbols;private readonly string _quoteOwner="windows-tray-"+Guid.NewGuid();
    private TaskbarIcon? _tray;
    private Meili.App.Views.QuotePanelWindow? _quotePanel;
    private bool _disposed;

    public event EventHandler? ExitRequested;
    public bool HasTrayIcon=>_tray is not null;

    public NotificationService(LocalizationService loc, AppLaunchOptions options, DataDirectoryInfo dataDir, NavigationHub nav, QuoteHub quotes, SymbolContext symbols)
    {
        _loc = loc;
        _options = options;
        _dataDir = dataDir;
        _nav = nav;_quotes=quotes;_symbols=symbols;
        _loc.LanguageChanged += (_, _) => UpdateTooltip();
        // --no-alerts (UI checks, tests) must not touch the toast COM/AUMID registration at all.
        if (!_options.NoAlerts)
        {
            try { ToastNotificationManagerCompat.OnActivated += OnToastActivated; } catch (Exception ex) { Log(ex); }
        }
    }

    public void Attach(Window window)
    {
        if (_options.NoAlerts || _tray is not null) return;
        try
        {
            TrySetAppUserModelId();
            _tray = new TaskbarIcon
            {
                ToolTipText = _loc["App_Title"],
                IconSource = new BitmapImage(new Uri("pack://application:,,,/MeiliTerminal;component/Assets/app.ico")),
                ContextMenu = BuildMenu(),
                NoLeftClickDelay = true,
            };
            _tray.TrayLeftMouseUp += async (_, _) => await ShowQuotePanelAsync();
            _quotes.Updated+=OnQuoteBatch;_symbols.Changed+=OnQuoteSymbol;OnQuoteSymbol(null,_symbols.Current??"SPY");
        }
        catch (Exception ex)
        {
            Log(ex);
        }
    }

    private void OnQuoteBatch(object? sender,QuoteBatch batch)=>Ui.Platform.Run(UpdateTooltip);
    private void OnQuoteSymbol(object? sender,string symbol)
    {
        if(_disposed)return;_quotes.Subscribe(_quoteOwner,[symbol]);UpdateTooltip();_ = RefreshTrayAsync(symbol);
    }
    private async Task RefreshTrayAsync(string symbol){try{await _quotes.GetQuotesAsync([symbol]);}catch(Exception ex){Log(ex);}}
    public async Task ShowQuotePanelAsync()
    {
        if(_disposed)return;
        _quotePanel??=new Meili.App.Views.QuotePanelWindow(AppServices.Get<QuotePanelViewModel>(),symbol=>{Restore();_nav.Go(AppPage.Charts,symbol);},()=>{Restore();_nav.Go(AppPage.Watchlist);},()=>{if(ExitRequested is not null)ExitRequested.Invoke(this,EventArgs.Empty);else Application.Current?.Shutdown();});
        if(_quotePanel.IsVisible){_quotePanel.HidePanel();return;}
        await _quotePanel.ShowPanelAsync();
    }
    public void UpdateTooltip()
    {
        if (_tray is null) return;
        try
        {
            var symbol=_symbols.Current??"SPY";
            _tray.ToolTipText=_quotes.TryGetCached(symbol,out var q)?symbol+" "+(double.IsFinite(q.Price)?Meili.Core.Models.NumberDisplay.Price(q.Price):"—")+" "+QuoteMovement.Percent(QuoteMovement.Values(q).Percent)+"\n"+FreshnessText.SourceAndTiming(q,_loc,DateTimeOffset.UtcNow)+"\n"+FreshnessText.AsOf(q.AsOf,_loc):_loc["App_Title"];
            if (_tray.ContextMenu is not null)
                _tray.ContextMenu = BuildMenu();
        }
        catch (Exception ex) { Log(ex); }
    }

    public void ShowAlert(PriceAlert alert, AlertEvalResult result, Meili.Core.Models.Quote? quote = null)
    {
        if (_options.NoAlerts || !result.Fired) return;
        try
        {
            var title = AlertText.Title(alert, _loc);
            var body = AlertText.Body(alert, result, _loc);
            if (result.UsedExtended) body += " " + _loc["Al_UsedExtended"];
            // F3: say where the price came from and how old it may be.
            var timing = quote is null ? null : FreshnessText.SourceAndTiming(quote, _loc, DateTimeOffset.Now);
            var toast = new ToastContentBuilder()
                .AddText(title)
                .AddText(body);
            if (!string.IsNullOrEmpty(timing)) toast.AddText(timing);
            toast
                .AddArgument("action", "open")
                .AddArgument("symbol", alert.Symbol)
                .Show();
        }
        catch (Exception ex)
        {
            Log(ex);
        }
    }

    /// <summary>B1: IV alert toast; the third line says what was measured (IV30 index or ATM IV of which expiry).</summary>
    public void ShowIvAlert(PriceAlert alert, AlertEvalResult result, IvSample sample)
    {
        if (_options.NoAlerts || !result.Fired) return;
        try
        {
            new ToastContentBuilder()
                .AddText(AlertText.Title(alert, _loc))
                .AddText(AlertText.Body(alert, result, _loc))
                .AddText(AlertText.IvBasisText(sample, _loc))
                .AddArgument("action", "open")
                .AddArgument("symbol", alert.Symbol)
                .Show();
        }
        catch (Exception ex) { Log(ex); }
    }

    private bool _hiddenHintShown;

    /// <summary>D4: once per session, say that the app keeps running (and alerts keep working) in the tray.</summary>
    public void ShowHiddenToTray()
    {
        if (_hiddenHintShown || _options.NoAlerts || _tray is null) return;
        _hiddenHintShown = true;
        try { _tray.ShowNotification(_loc["Close_HiddenTitle"], _loc["Close_HiddenBody"]); }
        catch (Exception ex) { Log(ex); }
    }

    /// <summary>F10: tray balloon when alert monitoring stops getting quotes (once per interruption).</summary>
    public void ShowMonitorInterrupted(MonitorHealth health)
    {
        if (_options.NoAlerts || _tray is null) return;
        try
        {
            _tray.ShowNotification(_loc["Al_MonitorDownTitle"],
                string.Format(_loc["Al_MonitorDownBody"], health.ConsecutiveFailures, health.FailingSince?.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) ?? ""));
        }
        catch (Exception ex) { Log(ex); }
    }

    public void ShowMonitorRecovered()
    {
        if (_options.NoAlerts || _tray is null) return;
        try { _tray.ShowNotification(_loc["Al_MonitorUpTitle"], _loc["Al_MonitorUpBody"]); }
        catch (Exception ex) { Log(ex); }
    }

    public void Restore(Window? window = null)
    {
        window ??= Application.Current?.MainWindow;
        if (window is null) return;
        window.Dispatcher.Invoke(() =>
        {
            window.Show();
            window.ShowInTaskbar = true;
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
            window.Activate();
            window.Topmost = true;
            window.Topmost = false;
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_options.NoAlerts)
        {
            try { ToastNotificationManagerCompat.OnActivated -= OnToastActivated; } catch (Exception ex) { Log(ex); }
        }
        _quotes.Unsubscribe(_quoteOwner);_quotes.Updated-=OnQuoteBatch;_symbols.Changed-=OnQuoteSymbol;
        _quotePanel?.Close();_quotePanel=null;
        try { _tray?.Dispose(); } catch (Exception ex) { Log(ex); }
        _tray = null;
    }

    private ContextMenu BuildMenu()
    {
        var show = new MenuItem { Header = _loc["Tray_Show"], FontSize = 14 };
        show.Click += (_, _) => Restore();
        var exit = new MenuItem { Header = _loc["Tray_Exit"], FontSize = 14 };
        exit.Click += (_, _) =>
        {
            if (ExitRequested is not null) ExitRequested.Invoke(this, EventArgs.Empty);
            else Application.Current?.Shutdown();
        };
        var quotes=new MenuItem{Header=_loc["QP_Title"],FontSize=14};quotes.Click+=async(_,_)=>await ShowQuotePanelAsync();
        var background=new MenuItem{Header=_loc["Bg_Run"],FontSize=14};background.Click+=(_,_)=>{if(Application.Current?.MainWindow is Meili.App.MainWindow main)main.RunInBackground();};
        return new ContextMenu { Items = { quotes, show, background, exit }, FontSize = 14 };
    }

    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        try
        {
            var args = ToastArguments.Parse(e.Argument);
            var symbol = args.TryGetValue("symbol", out var s) ? s : null;
            var disp = Application.Current?.Dispatcher;
            if (disp is null) return;
            disp.BeginInvoke(() =>
            {
                Restore();
                if (!string.IsNullOrWhiteSpace(symbol))
                    _nav.Go(AppPage.Charts, symbol);
            });
        }
        catch (Exception ex) { Log(ex); }
    }

    private void TrySetAppUserModelId()
    {
        try { _ = SetCurrentProcessExplicitAppUserModelID(AppUserModelId); } catch (Exception ex) { Log(ex); }
    }

    /// <summary>H2: notification failures go to the shared app log (previously a separate alerts.log).</summary>
    private static void Log(Exception ex) => AppLog.Warn("notify", "notification failed", ex);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);
}