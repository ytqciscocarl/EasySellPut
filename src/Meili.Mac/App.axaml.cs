using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Meili.Core.Diagnostics;
using Meili.Core.Settings;
using Meili.Mac.Platform;

namespace Meili.Mac;

public partial class App : Application
{
    private IntPtr _napActivity;
    private AlertStore? _wakeStore;private bool _alertsEnabled,_exiting;
    private MenuBarQuoteController? _menuQuotes;
    private QuotePanelWindow? _quotePanel;
    private MacQuoteStatusItem? _quoteStatus;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            Start(desktop, desktop.Args ?? []);
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>After the services are built (also used by the headless tests): language, theme and colours.</summary>
    public static void Boot()
    {
        foreach(var (key,family) in new[]{("AppFont","Anthropic Sans"),("NumberFont","Anthropic Sans"),("HeadingFont","Anthropic Serif"),("CodeFont","Anthropic Mono")})
            if(Avalonia.Media.FontManager.Current.SystemFonts.Any(f=>f.Name==family))Application.Current!.Resources[key]=new Avalonia.Media.FontFamily(family);
        var settings = AppServices.Get<SettingsStore>().Load();
        var loc = AppServices.Get<LocalizationService>();
        loc.Apply(settings.Language);
        var theme = AppServices.Get<MacTheme>();
        theme.ApplyUpDownColors(settings.EffectiveUpDownColors());
        // E1: until the user picks colours, they follow the interface language.
        loc.LanguageChanged += (_, _) => theme.ApplyUpDownColors(AppServices.Get<SettingsStore>().Current.EffectiveUpDownColors(loc.Language));
        theme.Apply(settings.Theme);
    }

    private void Start(IClassicDesktopStyleApplicationLifetime desktop, string[] args)
    {
        var dataDir = MacDataDirectory.Resolve(args, AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Directory.CreateDirectory(dataDir.Path);
        AppServices.DataDirectory = dataDir;
        AppLog.Initialize(dataDir.Path);
        AppLog.Info("app", $"start mac {string.Join(' ', args)} data={dataDir.Path} ({dataDir.Source}) os={Environment.OSVersion.VersionString}");
        Dispatcher.UIThread.UnhandledException += OnUiException;

        var options = AppLaunchOptions.Parse(args);
        var services = new ServiceCollection();
        services.AddSingleton(options);
        MacComposition.Register(services, dataDir);
        AppServices.Provider = services.BuildServiceProvider();
        Boot();

        var window = new MainWindow();
        // Avalonia automatically shows MainWindow after initialization. Defer assignment
        // for background startup so the lifetime cannot flash the main window.
        if(options.StartInBackground)Dispatcher.UIThread.Post(()=>desktop.MainWindow=window,DispatcherPriority.Send);
        else desktop.MainWindow=window;
        MacMenu.Install(this, window, desktop);
        desktop.ShutdownRequested += (_, _) => window.AllowClose();
        desktop.Exit += (_, e) => OnExit(e.ApplicationExitCode);
        // Dock icon click with no visible window: bring the hidden main window back (Mac convention).
        if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
            activatable.Activated += (_, e) => { if (e.Kind == ActivationKind.Reopen) window.ShowFromDock(); };
        if(options.StartInBackground)window.PrepareBackgroundStartup();else window.Show();
        SharedComposition.OnStarted(AppServices.Provider);
        if(OperatingSystem.IsMacOS())
        {
            try
            {
                MacQuoteStatusItem? status=null;
                status=new MacQuoteStatusItem(async()=>
                {
                    _quotePanel??=new QuotePanelWindow(AppServices.Get<QuotePanelViewModel>(),symbol=>{window.ShowFromDock();AppServices.Get<NavigationHub>().Go(AppPage.Charts,symbol);},()=>{window.ShowFromDock();AppServices.Get<NavigationHub>().Go(AppPage.Watchlist);},()=>desktop.TryShutdown());
                    if(_quotePanel.IsVisible){_quotePanel.HidePanel();return;}
                    var anchor=status!.Anchor;await _quotePanel.ShowPanelAsync(anchor.X,anchor.Y);
                });
                _quoteStatus=status;
                _menuQuotes=new MenuBarQuoteController(AppServices.Get<Meili.Core.Quotes.QuoteHub>(),AppServices.Get<SymbolContext>(),
                    AppServices.Get<WatchlistStore>(),AppServices.Get<LocalizationService>(),AppServices.Get<SettingsStore>(),status,
                    ()=>{window.ShowFromDock();AppServices.Get<NavigationHub>().Go(AppPage.Charts,AppServices.Get<SymbolContext>().Current);},()=>desktop.TryShutdown(),()=>window.RunInBackground());
            }
            catch(Exception ex){AppLog.Error("menu-bar","Could not initialize menu bar quotes",ex);}
        }
        if (options.Smoke)
            Dispatcher.UIThread.Post(async () => await PackageSmokeAsync(window, desktop, options), DispatcherPriority.Background);
        _alertsEnabled=!options.NoAlerts;
        if(OperatingSystem.IsMacOS())
        {
            _wakeStore=AppServices.Get<AlertStore>();_wakeStore.Changed+=OnAlertDemandChanged;UpdateAlertActivity();
        }
    }

    private async Task PackageSmokeAsync(MainWindow window, IClassicDesktopStyleApplicationLifetime desktop, AppLaunchOptions options)
    {
        var failures = new List<string>();
        try
        {
            if(options.StartInBackground)
            {
                if(!window.PresentationSuspended||window.IsVisible)throw new InvalidOperationException("Background startup showed the main window");
                window.ShowFromDock();await Task.Delay(200);
            }
            var caption=window.FindControl<Border>("WindowHeader")!;
            if(!window.IsExtendedIntoWindowDecorations||!caption.IsVisible||Math.Abs(caption.Bounds.Height-DesignTokens.WindowHeaderHeight)>1)
                throw new InvalidOperationException("Native integrated title band unavailable");
            if(window.CurrentView?.TranslatePoint(default,window)?.Y<caption.Bounds.Bottom)
                throw new InvalidOperationException("Native title band overlaps page content");
            if(_menuQuotes is null)throw new InvalidOperationException("Native menu bar quote subscriber unavailable");
            _quoteStatus!.InvokePanel();await Task.Delay(150);
            if(_quotePanel?.IsVisible!=true)throw new InvalidOperationException("Native status action did not open quote panel");
            _quotePanel.HidePanel();window.ShowFromDock();
            window.Navigate(AppPage.Settings);
            await Task.Delay(500);
            var settings = AppServices.Get<SettingsViewModel>();
            if (window.CurrentPage != AppPage.Settings || !settings.CanConnectAi) throw new InvalidOperationException("Settings/automation bundle unavailable");
            if (window.CurrentView?.FindControl<Button>("ConnectCodex")?.IsEnabled != true) throw new InvalidOperationException("Connect button not enabled");
            await settings.CopyCodexCommand.ExecuteAsync(null);
            var clipboard = await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(window.Clipboard!);
            if (clipboard != Meili.Automation.McpConnectionSetup.Bundled(AppContext.BaseDirectory, AppServices.DataDirectory.Path).CodexSetupCommand)
                throw new InvalidOperationException("Clipboard connection command mismatch");
            if (window.CurrentView?.FindControl<Button>("ConnectClaude")?.IsEnabled != true) throw new InvalidOperationException("Claude button not enabled");
            await settings.CopyClaudeCommand.ExecuteAsync(null);
            if (await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(window.Clipboard!) != Meili.Automation.McpConnectionSetup.Bundled(AppContext.BaseDirectory, AppServices.DataDirectory.Path).ClaudeSetupCommand) throw new InvalidOperationException("Claude setup command mismatch");
            var connection=Meili.Automation.McpConnectionSetup.Bundled(AppContext.BaseDirectory,AppServices.DataDirectory.Path);
            var shell=AppServices.Get<Meili.Shared.ViewModels.ShellViewModel>();
            if(window.FindControl<Button>("McpStatusIndicator")?.Command!=shell.CopyMcpSetupCommand)throw new InvalidOperationException("MCP status copy action unavailable");
            await shell.CopyMcpSetupCommand.ExecuteAsync(null);
            if(await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(window.Clipboard!)!=connection.ConfigurationPrompt(LocalizationService.Current.Language=="zh-CN")
                || shell.McpStatusText!=LocalizationService.Current["Status_McpCopied"])throw new InvalidOperationException("Universal MCP prompt/feedback mismatch");
            await settings.CopyUniversalSetupCommand.ExecuteAsync(null);
            if(await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(window.Clipboard!)!=connection.ConfigurationPrompt(LocalizationService.Current.Language=="zh-CN"))throw new InvalidOperationException("Settings/status universal prompts differ");
            if (LocalizationService.Current["Set_AiTitle"] == "Set_AiTitle") throw new InvalidOperationException("Missing connection resources");
            window.Navigate(AppPage.Charts);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(25);
            var chart = window.CurrentView?.FindControl<WebChartControl>("Chart");
            while (chart?.LastDiagnostic is null && DateTimeOffset.UtcNow < deadline) await Task.Delay(100);
            using var diagnostic = System.Text.Json.JsonDocument.Parse(chart?.LastDiagnostic ?? "{}");
            if (!diagnostic.RootElement.TryGetProperty("status", out var status) || status.GetString() != "rendered") throw new InvalidOperationException("Packaged WebView chart did not render: " + chart?.LastDiagnostic);
            var suspendedPage=window.CurrentView;window.RunInBackground();await Task.Delay(200);
            if(chart?.Session is not null||shell.IsClockRunning||!window.PresentationSuspended)throw new InvalidOperationException("Hidden window retained chart/chrome work");
            window.ShowFromDock();await Task.Delay(250);
            if(!ReferenceEquals(window.CurrentView,suspendedPage)||chart?.Session is null||!shell.IsClockRunning)throw new InvalidOperationException("Background restore lost navigation");
            deadline=DateTimeOffset.UtcNow.AddSeconds(25);while(chart.LastDiagnostic is null&&DateTimeOffset.UtcNow<deadline)await Task.Delay(100);
            using var restored=System.Text.Json.JsonDocument.Parse(chart.LastDiagnostic??"{}");
            if(!restored.RootElement.TryGetProperty("status",out var restoredStatus)||restoredStatus.GetString()!="rendered")throw new InvalidOperationException("Restored native chart did not render");
            window.Navigate(AppPage.Settings);
            var db = AppServices.Get<Meili.Data.Cache.MarketDb>();
            if (db.ReadSchemaVersion() != Meili.Data.Cache.MarketDb.SchemaVersion) throw new InvalidOperationException("Native SQLite unavailable");
        }
        catch (Exception ex) { AppLog.Error("package-smoke", "Packaged desktop self-check failed", ex); failures.Add(ex.ToString()); }
        var report = options.SmokeReport ?? Path.Combine(AppServices.DataDirectory.Path, "logs", "smoke.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
        await File.WriteAllTextAsync(report, System.Text.Json.JsonSerializer.Serialize(new { UpdateSource = Meili.Automation.UpdateClient.SourceConfiguration, Failures = failures.Count, Errors = failures, Checks = new[] { "real_window", "settings", "bundled_automation", "clipboard", "resources", "sqlite", "packaged_webview_chart", "native_menu_bar_quote", "native_quote_panel", "background_restore" }, Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), BackgroundStartupVerified=options.StartInBackground }));
        window.RunInBackground();window.AllowClose();
        desktop.Shutdown(failures.Count == 0 ? 0 : 1);
    }

    private static void OnUiException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Handled first and no modal dialog: the in-app banner cannot re-enter this handler or pump a nested loop.
        e.Handled = true;
        try
        {
            var log = Path.Combine(AppServices.DataDirectory?.Path ?? AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(log);
            File.AppendAllText(Path.Combine(log, "crash.log"), $"[{DateTime.Now:O}] {e.Exception}{Environment.NewLine}{Environment.NewLine}");
            AppLog.Error("app", "unhandled UI exception (see crash.log)", e.Exception);
            AppServices.Get<MacNotifier>().ShowError(e.Exception.Message);
        }
        catch (Exception ex)
        {
            AppLog.Error("app", "could not record an unhandled UI exception", ex);
        }
    }

    private void OnAlertDemandChanged(object? sender,EventArgs e)=>Dispatcher.UIThread.Post(UpdateAlertActivity);
    private void UpdateAlertActivity()
    {
        if(_exiting||_wakeStore is null)return;
        var needed=_alertsEnabled&&(_wakeStore.EnabledSymbols.Count>0||_wakeStore.IvSymbols.Count>0);
        if(needed&&_napActivity==IntPtr.Zero)_napActivity=MacNative.BeginActivity("Active price and IV alerts");
        else if(!needed&&_napActivity!=IntPtr.Zero){MacNative.EndActivity(_napActivity);_napActivity=IntPtr.Zero;}
    }
    private void OnExit(int code)
    {
        if(_exiting)return;_exiting=true;_quotePanel?.Close();_quotePanel=null;if(_wakeStore is not null)_wakeStore.Changed-=OnAlertDemandChanged;
        _menuQuotes?.Dispose();
        if (_napActivity != IntPtr.Zero){MacNative.EndActivity(_napActivity);_napActivity=IntPtr.Zero;}
        AppServices.Get<AlertMonitor>().Dispose();   // only stops its subscription; nothing to fail
        try { AppServices.Get<IvAlertSampler>().Dispose(); AppServices.Get<AlertStore>().Flush(); }
        catch (Exception ex) { AppLog.Warn("app", "alert state flush failed", ex); }
        AppLog.Info("app", $"exit code {code}");
        (AppServices.Provider as IDisposable)?.Dispose();
    }
}
