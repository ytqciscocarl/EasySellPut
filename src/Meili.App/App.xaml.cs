using Meili.Core.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Meili.App.Services;
using Meili.Core.Settings;

namespace Meili.App;

public partial class App : Application
{
    private bool _handlingUnhandled;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        // H2: background-thread and unobserved task failures are logged too (they used to vanish).
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => AppLog.Error("app", "unhandled exception (terminating=" + ev.IsTerminating + ")", ev.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ev) => { AppLog.Warn("app", "unobserved task exception", ev.Exception); ev.SetObserved(); };

        var exeDir = AppContext.BaseDirectory;
        var dataDir = DataDirectoryResolver.Resolve(e.Args, exeDir,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        Directory.CreateDirectory(dataDir.Path);
        AppServices.DataDirectory = dataDir;
        AppLog.Initialize(dataDir.Path);
        AppLog.Info("app", $"start {string.Join(' ', e.Args)} data={dataDir.Path} ({dataDir.Source})");

        var options = AppLaunchOptions.Parse(e.Args);
        var perf = options.Perf ? new StartupPerf(dataDir.Path) : null;   // C6
        perf?.Mark("app-start");
        if (options.ExitOnClose)
            ShutdownMode = ShutdownMode.OnMainWindowClose;
        var services = new ServiceCollection();
        services.AddSingleton(options);
        AppComposition.Register(services, dataDir);
        AppServices.Provider = services.BuildServiceProvider();
        perf?.Mark("services");

        foreach(var (key,family) in new[]{("AppFont","Anthropic Sans"),("NumberFont","Anthropic Sans"),("HeadingFont","Anthropic Serif"),("CodeFont","Anthropic Mono")})
            if(System.Windows.Media.Fonts.SystemFontFamilies.Any(f=>f.Source==family))Resources[key]=new System.Windows.Media.FontFamily(family);
        var settings = AppServices.Get<SettingsStore>().Load();
        AppServices.Get<LocalizationService>().Apply(settings.Language);
        ThemeService.ApplyUpDownColors(settings.EffectiveUpDownColors());
        // E1: until the user picks colours, they follow the interface language.
        var loc = AppServices.Get<LocalizationService>();
        loc.LanguageChanged += (_, _) => ThemeService.ApplyUpDownColors(AppServices.Get<SettingsStore>().Current.EffectiveUpDownColors(loc.Language));

        var window = new MainWindow();
        MainWindow = window;
        AppServices.Get<ThemeService>().Apply(settings.Theme, window);
        if (options.Smoke)
        {
            // B3: real window, but off-screen, not activated and not in the taskbar, so the self-check never disturbs the desktop.
            window.PrepareForSmoke();
            TaskScheduler.UnobservedTaskException += (_, ev) => SmokeRunner.RecordError(ev.Exception);
        }
        if (perf is not null)
        {
            window.ContentRendered += (_, _) => perf.Mark("first-render");
            Dispatcher.BeginInvoke(() => perf.Mark("idle"), DispatcherPriority.ApplicationIdle);
        }
        if(options.StartInBackground)window.PrepareBackgroundStartup();else window.Show();
        perf?.Mark(options.StartInBackground?"background-ready":"window-shown");
        AppComposition.OnStarted(AppServices.Provider);
        if (options.Smoke)
            Dispatcher.BeginInvoke(async () =>
            {
                int code;
                try { code = await new SmokeRunner().RunAsync(window, options); }
                catch (Exception ex) { SmokeRunner.RecordError(ex); code = 2; }
                perf?.Mark("smoke-done");   // memory after every page was opened once
                window.RunInBackground();await Task.Delay(100);Shutdown(code);
            }, DispatcherPriority.ApplicationIdle);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { AppServices.Get<NotificationService>().Dispose(); } catch (Exception ex) { AppLog.Warn("app", "notification shutdown failed", ex); }
        AppServices.Get<AlertMonitor>().Dispose();   // only stops its subscription; nothing to fail
        try { AppServices.Get<IvAlertSampler>().Dispose(); AppServices.Get<AlertStore>().Flush(); } catch (Exception ex) { AppLog.Warn("app", "alert state flush failed", ex); }
        AppLog.Info("app", $"exit code {e.ApplicationExitCode}");
        (AppServices.Provider as IDisposable)?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Always mark handled first so a layout-time XAML error cannot re-enter this
        // handler (MessageBox would pump a new dispatcher frame and overflow the stack).
        e.Handled = true;
        if (AppServices.Provider is not null && AppServices.Get<AppLaunchOptions>().Smoke)
        {
            SmokeRunner.RecordError(e.Exception);   // no dialog during a self-check
            return;
        }
        if (_handlingUnhandled) return;
        _handlingUnhandled = true;
        try
        {
            var log = Path.Combine(AppServices.DataDirectory?.Path ?? AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(log);
            File.AppendAllText(Path.Combine(log, "crash.log"), $"[{DateTime.Now:O}] {e.Exception}{Environment.NewLine}{Environment.NewLine}");
            AppLog.Error("app", "unhandled UI exception (see crash.log)", e.Exception);
            var title = TryFindResource("App_Title") as string ?? "EasySellPut";
            var message = e.Exception.Message;
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                try { System.Windows.MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning); }
                catch (Exception ex) { AppLog.Error("app", "could not show the error dialog", ex); }
            });
        }
        catch { } // expected: last-chance handler — logging itself failed, nothing safer left to do
    }
}