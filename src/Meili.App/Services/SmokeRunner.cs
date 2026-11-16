using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Meili.Core.Abstractions;
using Meili.Core.Alerts;
using Meili.Core.Models;
using Meili.Core.Pricing;
using Meili.Core.Quotes;
using Meili.Core.Settings;
using Meili.Data.Cache;

namespace Meili.App.Services;

public enum SmokeStatus { Pass, Warn, Fail }

public sealed record SmokeCheck(string Name, SmokeStatus Status, string Detail, long Milliseconds);

/// <summary>
/// B3: <c>--smoke</c> self-check. Runs inside the real app (real composition, real window kept off-screen), walks every
/// page, exercises local storage (settings, SQLite cache incl. the native library), the string dictionaries and — unless
/// <c>--smoke-offline</c> — one live quote, K-line, option chain, IV and rate fetch. Writes a JSON report and exits
/// with 0 (no failures) or 1. Network problems are warnings, not failures: the machine may simply be offline.
/// </summary>
public sealed class SmokeRunner
{
    private readonly List<SmokeCheck> _checks = [];
    private static readonly List<string> Errors = [];

    /// <summary>Unhandled exceptions seen while smoke-testing (recorded instead of showing a dialog).</summary>
    public static void RecordError(Exception ex)
    {
        lock (Errors) Errors.Add(ex.GetType().Name + ": " + ex.Message);
    }

    public IReadOnlyList<SmokeCheck> Checks => _checks;

    public async Task<int> RunAsync(MainWindow window, AppLaunchOptions options)
    {
        var sw = Stopwatch.StartNew();
        if(options.StartInBackground)await CheckAsync("background-startup",async()=>
        {
            if(!window.PresentationSuspended)throw new InvalidOperationException("Background startup did not suspend presentation");
            AppServices.Get<NotificationService>().Restore(window);await Task.Delay(250);return "background startup retained a native restore path";
        });
        await CheckAsync("data-dir", () =>
        {
            var dir = AppServices.DataDirectory.Path;
            var probe = Path.Combine(dir, ".smoke-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return Task.FromResult($"{dir} ({AppServices.DataDirectory.Source})");
        });
        await CheckAsync("settings", () =>
        {
            var store = AppServices.Get<SettingsStore>();
            store.Update(_ => { });
            var again = new SettingsStore(AppServices.DataDirectory.Path).Load();
            return Task.FromResult($"language={again.Language}, close={again.CloseAction}");
        });
        await CheckAsync("sqlite-cache", () =>
        {
            var db = AppServices.Get<MarketDb>();
            var t = DateTimeOffset.UtcNow.Date;
            var bars = new[] { Bar.FromSource(new DateTimeOffset(t.AddDays(-1), TimeSpan.Zero), 1, 2, 0.5, 1.5, 100) };
            db.SaveBars("__SMOKE__", BarInterval.Day1, bars, new BarCacheMeta("smoke", DateTimeOffset.UtcNow, null, null, false));
            var (loaded, meta) = db.LoadBars("__SMOKE__", BarInterval.Day1);
            if (loaded.Count != 1 || meta?.Source != "smoke") throw new InvalidOperationException("bar round-trip mismatch");
            db.AddIvSample("__SMOKE__", DateTimeOffset.UtcNow, "Iv30", 0.3, "smoke");
            if (db.LoadIvSamples("__SMOKE__", DateTimeOffset.UtcNow.AddMinutes(-5)).Count == 0) throw new InvalidOperationException("iv sample round-trip failed");
            return Task.FromResult($"{db.FilePath}, schema v{db.ReadSchemaVersion()}");
        });
        await CheckAsync("strings", () =>
        {
            var loc = AppServices.Get<LocalizationService>();
            var current = loc.Language;
            loc.Apply("en-US");
            var en = loc["Nav_Dashboard"];
            loc.Apply("zh-CN");
            var zh = loc["Nav_Dashboard"];
            loc.Apply(current);
            if (en == "Nav_Dashboard" || zh == "Nav_Dashboard" || en == zh) throw new InvalidOperationException($"dictionaries not loaded (en={en}, zh={zh})");
            return Task.FromResult($"en={en}, zh={zh}");
        });
        foreach (var page in KeyboardShortcuts.PageOrder)
        {
            await CheckAsync("page:" + page.Name.Replace("Page", ""), async () =>
            {
                int before;
                lock (Errors) before = Errors.Count;
                var rail=(System.Windows.Controls.StackPanel)window.FindName("BuiltinRail");
                var home=(System.Windows.Controls.Primitives.ToggleButton)window.FindName("HomeWorkspace");
                var target=Enum.Parse<AppPage>(page.Name.Replace("Page",""));
                var settingsButton=(System.Windows.Controls.Primitives.ToggleButton)window.FindName("SettingsButton");
                var button=target==AppPage.Dashboard?home:target==AppPage.Settings?settingsButton:rail.Children.OfType<System.Windows.Controls.Primitives.ToggleButton>().Single(b=>Equals(b.Tag,target));
                button.BringIntoView();button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                await Task.Delay(1500);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                string[] newErrors;
                lock (Errors) newErrors = Errors.Skip(before).ToArray();
                if (newErrors.Length > 0) throw new InvalidOperationException(string.Join(" | ", newErrors));
                if (window.CurrentPageType != page) throw new InvalidOperationException($"navigation ended on {window.CurrentPageType?.Name ?? "none"}");
                var workspace=(System.Windows.Controls.Grid)window.FindName("WorkspaceGrid");
                if(workspace.ColumnDefinitions.Count!=2 || workspace.ColumnDefinitions[0].ActualWidth!=64 || window.FindName("ChannelNav") is not null)throw new InvalidOperationException("The wide navigation column returned");
                if(rail.Children.OfType<System.Windows.Controls.Primitives.ToggleButton>().Append(home).Append(settingsButton).Count(b=>b.IsChecked==true)!=1 || button.IsChecked!=true)throw new InvalidOperationException("Navigation rail selection is inconsistent");
                foreach(var name in new[]{"SettingsButton","LanguageButton","ThemeButton","MotionButton"})
                {
                    var action=(System.Windows.Controls.Primitives.ButtonBase)window.FindName(name);
                    if(action.ActualWidth!=28 || action.ActualHeight!=24)throw new InvalidOperationException("Status action bounds: "+name);
                    var icon=(System.Windows.Controls.Viewbox)action.Content;var center=icon.TranslatePoint(new Point(icon.ActualWidth/2,icon.ActualHeight/2),action);
                    if(Math.Abs(center.X-action.ActualWidth/2)>.6 || Math.Abs(center.Y-action.ActualHeight/2)>.6)throw new InvalidOperationException("Off-center status icon: "+name);
                }
                return "loaded via single-rail button";
            });
        }
        if (Directory.Exists(Path.Combine(AppContext.BaseDirectory, "automation"))) await CheckAsync("ai-connection", async () =>
        {
            var settings = AppServices.Get<SettingsViewModel>();
            if (!settings.CanConnectAi) throw new InvalidOperationException("Bundled CLI unavailable");
            window.NavigateForSmoke(typeof(Meili.App.Views.Pages.SettingsPage));
            await Task.Delay(300);
            var page = window.ThemeSurfacesForSmoke().First(p => p.Part == "page").Element;
            if (page.FindName("ConnectCodex") is not System.Windows.Controls.Button { IsEnabled: true }) throw new InvalidOperationException("Connect button not enabled");
            await settings.CopyCodexCommand.ExecuteAsync(null);
            var expected = Meili.Automation.McpConnectionSetup.Bundled(AppContext.BaseDirectory, AppServices.DataDirectory.Path).CodexSetupCommand;
            if (Clipboard.GetText() != expected) throw new InvalidOperationException("Clipboard connection command mismatch");
            if (page.FindName("ConnectClaude") is not System.Windows.Controls.Button { IsEnabled: true }) throw new InvalidOperationException("Claude button not enabled");
            await settings.CopyClaudeCommand.ExecuteAsync(null);
            if (Clipboard.GetText() != Meili.Automation.McpConnectionSetup.Bundled(AppContext.BaseDirectory, AppServices.DataDirectory.Path).ClaudeSetupCommand) throw new InvalidOperationException("Claude setup command mismatch");
            var shell=AppServices.Get<ShellViewModel>();
            if(window.FindName("McpStatusIndicator") is not System.Windows.Controls.Button statusButton || statusButton.Command!=shell.CopyMcpSetupCommand)throw new InvalidOperationException("MCP status copy action unavailable");
            await shell.CopyMcpSetupCommand.ExecuteAsync(null);
            var prompt=Meili.Automation.McpConnectionSetup.Bundled(AppContext.BaseDirectory,AppServices.DataDirectory.Path).ConfigurationPrompt(LocalizationService.Current.Language=="zh-CN");
            if(Clipboard.GetText()!=prompt || shell.McpStatusText!=LocalizationService.Current["Status_McpCopied"])throw new InvalidOperationException("Universal MCP prompt/feedback mismatch");
            await settings.CopyUniversalSetupCommand.ExecuteAsync(null);
            if(Clipboard.GetText()!=prompt)throw new InvalidOperationException("Settings/status universal prompts differ");
            return "Codex/Claude commands and universal status/settings MCP + skill prompt copied";
        });
        if (Directory.Exists(Path.Combine(AppContext.BaseDirectory, "automation"))) await CheckAsync("packaged-webview", async () =>
        {
            window.NavigateForSmoke(typeof(Meili.App.Views.Pages.ChartsPage));
            await Task.Delay(500);
            var page = window.ThemeSurfacesForSmoke().First(p => p.Part == "page").Element;
            var chart = page.FindName("Chart") as Meili.App.Views.Controls.WebChartControl;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(25);
            while (chart?.LastDiagnostic is null && DateTimeOffset.UtcNow < deadline) await Task.Delay(100);
            using var diagnostic = JsonDocument.Parse(chart?.LastDiagnostic ?? "{}");
            if (!diagnostic.RootElement.TryGetProperty("status", out var status) || status.GetString() != "rendered") throw new InvalidOperationException("Packaged chart unavailable: " + chart?.LastDiagnostic);
            window.RunInBackground();await Task.Delay(200);
            if(chart?.Session is not null||AppServices.Get<ShellViewModel>().IsClockRunning||!window.PresentationSuspended)throw new InvalidOperationException("Hidden chart/chrome still active");
            AppServices.Get<NotificationService>().Restore(window);await Task.Delay(350);
            if(chart?.Session is null||!AppServices.Get<ShellViewModel>().IsClockRunning)throw new InvalidOperationException("Background restore failed");
            deadline=DateTimeOffset.UtcNow.AddSeconds(25);while(chart.LastDiagnostic is null&&DateTimeOffset.UtcNow<deadline)await Task.Delay(100);
            using var restored=JsonDocument.Parse(chart.LastDiagnostic??"{}");if(!restored.RootElement.TryGetProperty("status",out var restoredStatus)||restoredStatus.GetString()!="rendered")throw new InvalidOperationException("Restored native chart did not render");
            return "real bundled chart rendered, released in background and restored";
        });
        await CheckAsync("quote-panel",async()=>
        {
            using var model=new QuotePanelViewModel(AppServices.Get<Meili.Core.Quotes.QuoteHub>(),AppServices.Get<Meili.Core.Abstractions.IMarketDataService>(),AppServices.Get<WatchlistStore>(),AppServices.Get<SymbolContext>(),AppServices.Get<LocalizationService>());
            var panel=new Meili.App.Views.QuotePanelWindow(model,_=>{},()=>{},()=>{});
            var pending=panel.ShowPanelAsync();await Task.Delay(150);
            if(!panel.IsVisible || model.Rows.Count==0)throw new InvalidOperationException("Native quote panel unavailable");
            panel.HidePanel();await pending;panel.Close();return "native tray/pinned quote panel opened and canceled cleanly";
        });
        await CheckAsync("keyboard-wizard", () =>
        {
            window.OpenWizardForSmoke("A");
            var ok = window.WizardVisible;
            window.CloseWizardForSmoke();
            if (!ok) throw new InvalidOperationException("wizard did not open");
            return Task.FromResult("opens and closes");
        });

        await CheckAsync("theme-switch", () => ThemeSwitchAsync(window, options));

        if (!options.SmokeOffline)
        {
            var data = AppServices.Get<IMarketDataService>();
            await NetAsync("net:quote", async ct =>
            {
                var q = (await AppServices.Get<QuoteHub>().GetQuotesAsync(["AAPL"], ct)).FirstOrDefault();
                if (q is null || q.Price <= 0) throw new InvalidOperationException("no AAPL quote");
                return $"AAPL {NumberDisplay.Price(q.Price)} from {q.Source}, {DataFreshness.Evaluate(q, DateTimeOffset.Now).Kind}";
            });
            await NetAsync("net:bars", async ct =>
            {
                var s = await data.GetBarsAsync(new BarRequest("AAPL", BarInterval.Day1, DateTimeOffset.UtcNow.AddMonths(-3)), ct);
                return $"{s.Bars.Count} daily bars from {s.Source}{(s.OfflineCache ? " (offline cache)" : "")}";
            });
            await NetAsync("net:options", async ct =>
            {
                var exps = await data.GetExpirationsAsync("AAPL", ct);
                var chain = await data.GetChainAsync("AAPL", exps.FirstOrDefault(), ct);
                return $"{exps.Count} expirations, {chain.Contracts.Count} contracts from {chain.Source}";
            });
            await NetAsync("net:iv", async ct =>
            {
                var s = await AppServices.Get<IIvSource>().GetAsync("AAPL", DateTimeOffset.Now, ct) ?? throw new InvalidOperationException("no IV sample");
                return $"{s.Percent:0.0}% ({s.Basis}, {s.Source})";
            });
            await NetAsync("net:rate", async ct =>
            {
                var r = await AppServices.Get<RiskFreeRateProvider>().GetAsync(ct);
                if (r.Origin == RateOrigin.FixedFallback) throw new InvalidOperationException($"^IRX unavailable, fallback {r.Percent:0.00}%");
                return $"{r.Percent:0.00}% ({r.Origin})";
            });
        }
        else _checks.Add(new SmokeCheck("net:*", SmokeStatus.Warn, "skipped (--smoke-offline)", 0));

        var fails = _checks.Count(c => c.Status == SmokeStatus.Fail);
        WriteReport(options, sw.Elapsed, fails);
        return fails == 0 ? 0 : 1;
    }

    /// <summary>Brushes every window part resolves must be the application's current ones after a runtime switch.</summary>
    private static readonly string[] ThemeKeys =
    [
        "ApplicationBackgroundBrush", "TextFillColorPrimaryBrush", "CardBackgroundFillColorDefaultBrush", "LayerFillColorDefaultBrush",
        "ControlFillColorSecondaryBrush", "UpBrush", "DownBrush", "RowHighlightBrush", "ItmBrush", "HealthyBrush",
    ];

    /// <summary>
    /// 0.4.1: switch light→dark→light (or the reverse) on the real window with the options page open — the 0.4.0 bug
    /// left the title bar and navigation dark and table cells grey. Each part must resolve the application's current
    /// brushes and its sampled pixel must have the theme's lightness; window renders are saved next to the report.
    /// </summary>
    private static async Task<string> ThemeSwitchAsync(MainWindow window, AppLaunchOptions options)
    {
        var theme = AppServices.Get<ThemeService>();
        var start = theme.Mode;
        window.NavigateForSmoke(typeof(Views.Pages.OptionsPage));
        await Task.Delay(800);
        var dir = Path.GetDirectoryName(options.SmokeReport ?? Path.Combine(AppServices.DataDirectory.Path, "logs", "smoke.json"))!;
        var notes = new List<string>();
        try
        {
            foreach (var dark in new[] { !theme.IsDark, theme.IsDark })
            {
                theme.Apply(dark ? AppThemeMode.Dark : AppThemeMode.Light, window);
                await Task.Delay(300);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var name = dark ? "dark" : "light";
                if (window.Resources.Count > 0 || window.Resources.MergedDictionaries.Count > 0)
                    throw new InvalidOperationException($"{name}: window holds {window.Resources.Count} local resources / {window.Resources.MergedDictionaries.Count} dictionaries");
                var (px, stride) = RenderWindow(window, Path.Combine(dir, $"theme-switch-{name}.png"));
                foreach (var (part, element, at) in window.ThemeSurfacesForSmoke())
                {
                    foreach (var key in ThemeKeys)
                    {
                        var expected = ((System.Windows.Media.SolidColorBrush)Application.Current.FindResource(key)).Color;
                        var actual = ((System.Windows.Media.SolidColorBrush)element.FindResource(key)).Color;
                        if (expected != actual) throw new InvalidOperationException($"{name}: {part} resolves {key} as {actual}, application has {expected}");
                    }
                    var i = (int)at.Y * stride + (int)at.X * 4;
                    var lum = (0.0722 * px[i] + 0.7152 * px[i + 1] + 0.2126 * px[i + 2]) / 255;   // Pbgra32
                    if (dark ? lum > 0.35 : lum < 0.6) throw new InvalidOperationException($"{name}: {part} pixel at {at} has lightness {lum:0.00}");
                    notes.Add($"{name}/{part} {lum:0.00}");
                }
            }
        }
        finally { theme.Apply(start, window); }
        return string.Join(", ", notes);
    }

    private static (byte[] Pixels, int Stride) RenderWindow(Window window, string png)
    {
        int w = (int)window.ActualWidth, h = (int)window.ActualHeight, stride = w * 4;
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(window);
        var px = new byte[stride * h];
        rtb.CopyPixels(px, stride, 0);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using (var fs = File.Create(png)) enc.Save(fs);
        return (px, stride);
    }

    private async Task CheckAsync(string name, Func<Task<string>> body)
    {
        var sw = Stopwatch.StartNew();
        try { _checks.Add(new SmokeCheck(name, SmokeStatus.Pass, await body(), sw.ElapsedMilliseconds)); }
        catch (Exception ex) { _checks.Add(new SmokeCheck(name, SmokeStatus.Fail, ex.GetType().Name + ": " + ex.Message, sw.ElapsedMilliseconds)); }
    }

    private async Task NetAsync(string name, Func<CancellationToken, Task<string>> body)
    {
        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { _checks.Add(new SmokeCheck(name, SmokeStatus.Pass, await body(cts.Token), sw.ElapsedMilliseconds)); }
        catch (Exception ex) { _checks.Add(new SmokeCheck(name, SmokeStatus.Warn, ex.GetType().Name + ": " + ex.Message, sw.ElapsedMilliseconds)); }
    }

    private void WriteReport(AppLaunchOptions options, TimeSpan elapsed, int fails)
    {
        var path = options.SmokeReport ?? Path.Combine(AppServices.DataDirectory.Path, "logs", "smoke.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string[] errors;
        lock (Errors) errors = [.. Errors];
        var report = new
        {
            UpdateSource = Meili.Automation.UpdateClient.SourceConfiguration,
            Version = typeof(SmokeRunner).Assembly.GetName().Version?.ToString(),
            Process = Environment.ProcessPath,
            StartedAt = DateTimeOffset.Now - elapsed,
            ElapsedSeconds = Math.Round(elapsed.TotalSeconds, 1),
            Failures = fails,
            Warnings = _checks.Count(c => c.Status == SmokeStatus.Warn),
            Checks = _checks.Select(c => new { c.Name, Status = c.Status.ToString(), c.Detail, c.Milliseconds }),
            UnhandledErrors = errors,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Meili.Core.Diagnostics.AppLog.Info("smoke", $"done: {fails} failures, report {path}");
    }
}
