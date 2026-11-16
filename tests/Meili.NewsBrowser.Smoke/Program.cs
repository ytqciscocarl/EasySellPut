using System.IO;
using System.Net.Http;
using Meili.Testing;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Meili.Core.Models;
using Meili.Core.Settings;
using Meili.Core.Abstractions;
using Meili.Shared.ViewModels;
using Meili.Shared.Services;
#if WINDOWS_READER
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Wpf;
using Meili.App.Services;
using NativeChart = Meili.App.Views.Controls.WebChartControl;
#else
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Meili.Mac.Platform;
using NativeChart = Meili.Mac.Platform.WebChartControl;
#endif

internal static class Program
{
    private static int _exitCode = 1;
    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        { Console.Error.WriteLine("Native browser smoke requires Windows or macOS."); return 1; }
        _ = new LocalizationService();
#if WINDOWS_READER
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) => { await Probe(); app.Shutdown(_exitCode); };
        app.Run();
#else
        AppBuilder.Configure<ProbeApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
#endif
        return _exitCode;
    }
#if !WINDOWS_READER
    public sealed class ProbeApp : Application
    {
        public override void Initialize() => Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        public override void OnFrameworkInitializationCompleted()
        {
            base.OnFrameworkInitializationCompleted();
            ((IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!).ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Dispatcher.UIThread.Post(async () =>
            {
                await Probe();
                ((IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!).Shutdown(_exitCode);
            });
        }
    }
#endif
    private static async Task Probe()
    {
        using var server = new Pages();
        var directory = Path.Combine(Path.GetTempPath(), "meili-reader-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Meili.Core.Diagnostics.AppLog.Initialize(directory);
        // The actual reader uses its own temporary profile; never touch a user's browsing data.
        AppServices.DataDirectory = new Meili.Core.Settings.DataDirectoryInfo(directory, Meili.Core.Settings.DataDirectorySource.CommandLine);
        var window = new NewsReaderWindow();
        NewsReaderWindow? reopened = null;
        try
        {
            window.Show();
            window.OpenArticle(server.Url + "one", "Initial");
            await Until(() => window.Title == "Reader One", "first HTML document title");
            window.OpenArticle(server.Url + "two", "Initial two");
            await Until(() => window.Title == "Reader Two", "second HTML document title");
#if WINDOWS_READER
            var web = ((DockPanel)window.Content).Children.OfType<WebView2>().Single();
            web.GoBack();
#else
            var web = ((DockPanel)window.Content!).Children.OfType<NewsWebView>().Single();
            web.Back();
#endif
            await Until(() => window.Title == "Reader One", "back navigation");
#if WINDOWS_READER
            web.GoForward();
#else
            web.Forward();
#endif
            await Until(() => window.Title == "Reader Two", "forward navigation");
            var requests = server.Count;
            web.Reload();
            await Until(() => server.Count > requests && window.Title == "Reader Two", "reload request");
            window.OpenArticle("file:///private/blocked.html", "Blocked");
            await Task.Delay(300);
#if WINDOWS_READER
            if (web.Source?.Scheme != "http") throw new Exception("Blocked URL reached WebView2");
#else
            if (!web.Address.StartsWith(server.Url, StringComparison.Ordinal)) throw new Exception("Blocked URL reached WKWebView");
#endif
            window.OpenArticle(server.Url + "missing", "Missing");
            await Until(() => ((DockPanel)window.Content!).Children.OfType<TextBlock>().Any(t => t.Text == LocalizationService.Current["Reader_Failed"]), "HTTP error message");
            window.Close();
            reopened = new NewsReaderWindow(); reopened.Show();
            reopened.OpenArticle(server.Url + "one", "Reopened");
            await Until(() => reopened.Title == "Reader One", "reader recreation after disposal");
            Console.WriteLine("PASS: native HTML load, document titles, back, forward, reload, protocol blocking, HTTP error and close/reopen.");
            await ProbeWebTabs(server);
            await ProbeChart(directory);
            await ProbeWorkbench(directory);
#if !WINDOWS_READER
            await ProbeMenuBar();
#endif
            _exitCode = 0;
        }
        catch (Exception ex)
        {
            _exitCode = 1;
            Console.Error.WriteLine(ex);
            Console.Error.WriteLine($"Title={window.Title}; HTTP fixture requests={server.Count}");
            Console.Error.WriteLine("UI status: " + string.Join(" | ", ((DockPanel)window.Content!).Children.OfType<TextBlock>().Select(t => t.Text)));
#if !WINDOWS_READER
            var failedWeb = ((DockPanel)window.Content!).Children.OfType<NewsWebView>().Single();
            Console.Error.WriteLine($"WK title={failedWeb.DocumentTitle}; address={failedWeb.Address}; status={failedWeb.StatusKey}; bounds={failedWeb.Bounds}");
#endif
            foreach (var file in Directory.EnumerateFiles(Path.Combine(directory, "logs"), "*.log"))
                Console.Error.WriteLine(await File.ReadAllTextAsync(file));
        }
        finally { reopened?.Close(); window.Close(); }
    }
#if !WINDOWS_READER
    private static async Task ProbeMenuBar()
    {
        var loc=LocalizationService.Current;var original=loc.Language;loc.Apply("zh-CN");
        try
        {
            using var item=new MacQuoteStatusItem();
            var quote=new Quote{Symbol="NVDA",Price=162.85,ChangePercent=1.2,Source="fixture",DelayMinutes=15,AsOf=DateTimeOffset.UtcNow.AddMinutes(-15),FetchedAt=DateTimeOffset.UtcNow};
            var presentation=MenuBarQuote.Create("NVDA",quote,false,loc,DateTimeOffset.UtcNow);
            var activated=0;
            item.Update(presentation,[new QuoteMenuEntry("Fixture quote"),new QuoteMenuEntry("Open chart",()=>activated++)],UpDownColorMode.RedUp);
            await Task.Delay(300);
            if(item.Title!=presentation.Title)throw new Exception("Native menu bar title differs from quote");
            item.PrepareMenu();item.InvokeMenuItem(1);
            if(activated!=1)throw new Exception("Native menu bar target/action failed");
            var output=Path.Combine("build","out","ci");Directory.CreateDirectory(output);
            item.CaptureButton(Path.Combine(output,"desktop-smoke-menu-bar-red.png"));
            item.Update(presentation,[],UpDownColorMode.GreenUp);await Task.Delay(150);
            item.CaptureButton(Path.Combine(output,"desktop-smoke-menu-bar-green.png"));
            item.Update(MenuBarQuote.Create("NVDA",quote,true,loc,DateTimeOffset.UtcNow),[],UpDownColorMode.GreenUp);
            if(!item.Title.Contains(loc["Mb_Stale"]))throw new Exception("Failed quote did not mark native price stale");
            item.Update(MenuBarQuote.Create("MSFT",null,false,loc,DateTimeOffset.UtcNow),[],UpDownColorMode.GreenUp);
            if(item.Title!="MSFT —")throw new Exception("Missing native quote reused the previous symbol's price");
            Console.WriteLine("PASS: native NSStatusItem title, AppKit menu actions, both color conventions, stale/missing quotes, PNG capture and disposal.");
        }
        finally{loc.Apply(original);}
        using var recreated=new MacQuoteStatusItem();
        recreated.Update(MenuBarQuote.Create("AAPL",null,false,loc,DateTimeOffset.UtcNow),[],UpDownColorMode.RedUp);
        if(recreated.Title!="AAPL —")throw new Exception("Menu bar recreation failed");
    }
#endif
    private static async Task ProbeWebTabs(Pages server)
    {
        var first = new WebTabPane(server.Url + "one",articleMode:true);
        var second = new WebTabPane(server.Url + "two");
#if WINDOWS_READER
        var host=new Meili.App.Views.Controls.ArticleSplitView(second);
#else
        var host=new Meili.Mac.Views.Controls.ArticleSplitView(second);
#endif
        host.Open(first);
        var window = new Window { Width = 1320, Height = 760, Content = host };
        try
        {
            window.Show();
            await Until(() => first.DocumentTitle == "Reader One" && second.DocumentTitle == "Reader Two", "two workspace browser documents");
            var requestsBeforeResize=server.DocumentCount;window.Width=980;await Task.Delay(250);
            if(host.IsVertical)throw new Exception("Compact article split must remain side by side");
            window.Width=1320;await Task.Delay(250);
            if(host.IsVertical||server.DocumentCount!=requestsBeforeResize)throw new Exception($"Article resize: vertical={host.IsVertical}, documents={requestsBeforeResize}->{server.DocumentCount}");
#if WINDOWS_READER
            var web = ((DockPanel)first.Content).Children.OfType<WebView2>().Single();
            first.Visibility = Visibility.Collapsed; second.Visibility = Visibility.Visible;
            await first.SetPresentationActiveAsync(false);await Until(()=>first.IsSuspended,"hidden WebView2 suspension");
#else
            var web = ((DockPanel)first.Content!).Children.OfType<NewsWebView>().Single();
            first.IsVisible = false; second.IsVisible = true;
            first.SetPresentationActive(false);if(first.IsPresentationPolling)throw new Exception("Hidden WebKit polling continued");
#endif
            await Task.Delay(250);
            var before = server.Count;
#if WINDOWS_READER
            first.Visibility = Visibility.Visible; second.Visibility = Visibility.Collapsed;
            await first.SetPresentationActiveAsync(true);if(first.IsSuspended)throw new Exception("WebView2 did not resume");
#else
            first.IsVisible = true; second.IsVisible = false;
            first.SetPresentationActive(true);if(!first.IsPresentationPolling)throw new Exception("WebKit presentation polling did not resume");
#endif
            await Task.Delay(350);
            if (server.Count != before || first.DocumentTitle != "Reader One") throw new Exception("Switching tabs reloaded the web session");
#if WINDOWS_READER
            web.CoreWebView2.Navigate(server.Url + "two");
#else
            web.Navigate(server.Url + "two");
#endif
            await Until(() => first.DocumentTitle == "Reader Two", "workspace tab navigation");
#if WINDOWS_READER
            web.GoBack();
#else
            web.Back();
#endif
            await Until(() => first.DocumentTitle == "Reader One", "workspace tab retained back history");
#if WINDOWS_READER
            if (web.CoreWebView2.Settings.IsWebMessageEnabled || web.CoreWebView2.Settings.AreHostObjectsAllowed) throw new Exception("Workspace page has native privileges");
            web.CoreWebView2.Navigate("file:///private/blocked.html");
#else
            web.Navigate("file:///private/blocked.html");
#endif
            await Task.Delay(250);
            if (!first.Address.StartsWith(server.Url, StringComparison.Ordinal)) throw new Exception("Unsafe workspace navigation was accepted");
            Console.WriteLine("PASS: workspace tabs load in native engine, retain hidden sessions/history, and reject native protocols without host privileges.");
        }
        finally
        {
            window.Close();
#if WINDOWS_READER
            first.Dispose(); second.Dispose();
#endif
        }
    }

    private static async Task ProbeChart(string directory)
    {
#if WINDOWS_READER
        Ui.Platform = new WpfUiPlatform();
#else
        Ui.Platform = new MacUiPlatform();
#endif
        var settings = new SettingsStore(directory);
        var vm = new ChartsViewModel(new WatchlistStore(settings), new ChartData(), LocalizationService.Current,
            new SymbolContext(), settings, new ChartDrawingStore(directory));
        vm.SymbolText = "FIXTURE"; vm.Range = ChartRangeKind.Minute5;
        vm.ShowMa = vm.ShowEma = vm.ShowBoll = vm.ShowVolume = vm.ShowMacd = vm.ShowRsi = vm.ShowKdj = true;
        await vm.LoadAsync();
        ChartPalette Palette(bool dark, bool green)
        {
            var p = ThemePalette.For(dark); var (up, down) = p.UpDown(green ? UpDownColorMode.GreenUp : UpDownColorMode.RedUp);
            ScottPlot.Color C(uint n) => new((byte)(n >> 16), (byte)(n >> 8), (byte)n);
            return ChartPalette.Create(dark, C(up), C(down), C(p.PrimaryText), C(p.MutedText), p);
        }
        var palette = Palette(true, false);
        var chart = new NativeChart();
        var window = new Window { Width = 1200, Height = 800, Content = chart };
        EventHandler changed = (_, _) => chart.Update(palette);
        vm.PlotChanged += changed;
        bool Result(Func<JsonElement, bool> check)
        {
            if (chart.LastDiagnostic is not { } text) return false;
            using var d = JsonDocument.Parse(text);
            if (d.RootElement.GetProperty("status").GetString() == "error") throw new Exception("Chart script: " + text);
            return check(d.RootElement);
        }
        try
        {
            window.Show(); chart.Start(vm, palette);
            await Until(() => Result(d => d.GetProperty("panes").GetInt32() == 5), "TradingView five panes: " + chart.LastDiagnostic);
            if (!Result(d => d.GetProperty("rows").GetInt32() == 120 && d.GetProperty("candles").GetInt32() == 119)) throw new Exception("Missing OHLC was fabricated");
            await Until(() => Result(d => d.GetProperty("sidebarPermanent").GetBoolean() && d.GetProperty("sidebarViews").GetInt32() == 3 && d.GetProperty("events").GetInt32() == 3 && d.GetProperty("eventMarkers").GetInt32() == 2 && d.GetProperty("historyGroups").GetInt32() == 2 && d.GetProperty("historyStops").GetInt32() == 2), "News/calendar markers, date groups and history stops in native chart");
            if(!Result(d=>d.GetProperty("sessionBands").GetInt32()>0 && d.GetProperty("sessionPanes").GetInt32()==5))throw new Exception("Session background primitives missing from native panes");
            vm.StartDrawingCommand.Execute(DrawingKind.TrendLine); vm.AddDrawingPoint(vm.Series!.Bars[10].Time, 105);
            await Until(() => Result(d => d.GetProperty("drawingPreview").GetBoolean() && d.GetProperty("drawingHandles").GetInt32() == 1), "Native first-anchor drawing preview");
            vm.CancelDrawing();
            await Until(() => Result(d => !d.GetProperty("drawingPreview").GetBoolean()), "Native drawing cancellation clears preview");
            vm.MaPeriod = 5;
            await Until(() => Result(d => Math.Abs(d.GetProperty("lastMa").GetDouble() - vm.Snapshot!.Sma[^1]) < 1e-8), "Analytics MA parity in actual chart series");
            vm.ShowRsi = false;
            await Until(() => Result(d => d.GetProperty("panes").GetInt32() == 4), "Indicator pane toggle");
            palette = Palette(false, true); chart.Update(palette);
            await Until(() => Result(d => d.GetProperty("background").GetString() == $"#{ThemePalette.Light.Chart & 0xFFFFFF:X6}" && d.GetProperty("up").GetString() == "#1A7050"), "Theme and green-up convention");
            chart.Stop(); chart.Start(vm, palette);
            await Until(() => Result(d => d.GetProperty("panes").GetInt32() == 4), "Chart reattachment after disposal");
            Console.WriteLine("PASS: TradingView native WebView, 120 bars/119 candles, news/calendar markers, five indicator panes, Analytics MA parity, pane toggle, theme/colors and restart.");
        }
        finally { vm.PlotChanged -= changed; vm.Deactivate(); chart.Stop(); window.Close(); }
    }
    private static async Task ProbeWorkbench(string directory)
    {
        var vm=await WorkbenchFixture.CreateAsync(directory);
        var palette=WorkbenchFixture.Palette(true);
        var chart=new NativeChart();var window=new Window{Width=1280,Height=850,Content=chart};
        EventHandler changed=(_,_)=>chart.Update(palette);
        vm.ChartChanged+=changed;vm.WhatIfChanged+=changed;
        bool Result(Func<JsonElement,bool> predicate)
        {
            if(chart.LastDiagnostic is not{} json)return false;
            using var document=JsonDocument.Parse(json);var root=document.RootElement;
            if(root.GetProperty("status").GetString()=="error")throw new Exception(json);
            return predicate(root);
        }
        using var http=new HttpClient(new HttpClientHandler{UseProxy=false});
        async Task Send(object value)
        {
            using var request=new HttpRequestMessage(HttpMethod.Post,new Uri(chart.Session!.PageUri,"event")){Content=new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value))};
            request.Headers.Add("Origin",chart.Session.Origin);request.Content.Headers.ContentType=new("application/json");
            using var response=await http.SendAsync(request);response.EnsureSuccessStatusCode();
        }
        try
        {
            window.Show();chart.Start(vm,palette);
            await Until(()=>Result(d=>d.GetProperty("contracts").GetInt32()==18),"Native workspace document");
            await Send(new{kind="add-contract",key=vm.WorkbenchKey,contract="Call100",side="Long",quantity=1});
            await Until(()=>Result(d=>d.GetProperty("legs").GetInt32()==1&&d.GetProperty("points").GetInt32()==vm.Curve!.Prices.Length&&d.GetProperty("renderedPoints").GetInt32()==vm.Curve.Prices.Length),"Native linked curve after first leg");
            await Send(new{kind="add-contract",key=vm.WorkbenchKey,contract="Call110",side="Short",quantity=1});
            await Until(()=>Result(d=>d.GetProperty("legs").GetInt32()==2),"Native spread construction");
            if(Math.Abs(vm.ExpiryAnalysis!.MaxProfit-850)>1e-8)throw new Exception("Spread payoff mismatch");
            await Send(new{kind="edit-leg",key=vm.WorkbenchKey,id=vm.Legs[0].Id,field="quantity",quantity=2});
            await Until(()=>Result(d=>d.GetProperty("quantities")[0].GetString()=="2"),"Native quantity/curve linkage");
            await Send(new{kind="what-if",key=vm.WorkbenchKey,price=110});
            await Until(()=>Result(d=>d.GetProperty("whatIf").GetDouble()==110),"Native what-if valuation");
            palette=WorkbenchFixture.Palette(false);chart.Update(palette);
            await Until(()=>Result(d=>d.GetProperty("background").GetString()==$"#{ThemePalette.Light.Chart & 0xFFFFFF:X6}"),"Workspace theme");
            chart.Stop();chart.Start(vm,palette);
            await Until(()=>Result(d=>d.GetProperty("legs").GetInt32()==2),"Workspace reload preserves draft");
            Console.WriteLine("PASS: native linked workspace, chain, spread/Analytics payoff, quantity, what-if, theme and restart.");
        }
        catch(Exception){Console.Error.WriteLine("Workspace diagnostic: "+chart.LastDiagnostic);Console.Error.WriteLine($"Model legs={vm.Legs.Count}, points={vm.Curve?.Prices.Length}");throw;}
        finally{vm.ChartChanged-=changed;vm.WhatIfChanged-=changed;vm.Deactivate();chart.Stop();window.Close();}
    }
    private sealed class ChartData : IMarketDataService
    {
        public Task<BarSeries> GetBarsAsync(BarRequest request, CancellationToken ct = default)
        {
            var start = new DateTimeOffset(2025, 1, 6, 14, 30, 0, TimeSpan.Zero);
            return Task.FromResult(new BarSeries { Symbol = request.Symbol, Interval = request.Interval, Source = "fixture",
                Bars = Enumerable.Range(0, 120).Select(i => i == 70 ? Bar.CloseOnly(start.AddMinutes(i * 5), 170, 1000)
                    : Bar.FromSource(start.AddMinutes(i * 5), 100 + i, 101 + i, 99 + i, 100.5 + i, 1000)).ToArray() });
        }
        public Task<IReadOnlyList<Quote>> GetQuotesAsync(IReadOnlyList<string> symbols, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OptionChain> GetChainAsync(string symbol, DateOnly? expiration = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DateOnly>> GetExpirationsAsync(string symbol, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Fundamentals> GetFundamentalsAsync(string symbol, CancellationToken ct = default) => Task.FromResult(new Fundamentals{Symbol=symbol,Source="fixture",NextEarningsDate=new(2025,1,6),EarningsDateStatus=EventDateStatus.Estimated,ExDividendDate=new(2099,1,1)});
        public Task<FinancialStatements> GetFinancialsAsync(string symbol, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<NewsItem>> GetNewsAsync(string symbol, int count = 20, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<NewsItem>>([new(){Title="Fixture news",Url="https://example.com",Source="fixture",Published=new(2025,1,6,16,0,0,TimeSpan.Zero)}]);
        public Task<IReadOnlyList<SymbolMatch>> SearchAsync(string query, int limit = 10, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private static async Task Until(Func<bool> condition, string description)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException(description);
            await Task.Delay(100);
        }
    }
    private sealed class Pages : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private int _count;
        private int _documentCount;
        public int DocumentCount=>Volatile.Read(ref _documentCount);
        public int Count => Volatile.Read(ref _count);
        public string Url { get; }
        public Pages()
        {
            _listener.Start(); Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
            _ = Serve();
        }
        private async Task Serve()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = Respond(client);
                }
            }
            catch (OperationCanceledException) { } // expected: fixture shutdown
        }
        private async Task Respond(TcpClient client)
        {
            using (client)
            try
            {
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                    var line = await reader.ReadLineAsync(_stop.Token);
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_stop.Token))) { }
                    var path = line?.Split(' ').ElementAtOrDefault(1);
                    Interlocked.Increment(ref _count);if(path is "/one" or "/two")Interlocked.Increment(ref _documentCount);
                    var body = Encoding.UTF8.GetBytes($"<html><head><title>Reader {(path == "/one" ? "One" : "Two")}</title></head><body>Native engine fixture</body></html>");
                    var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(path == "/missing" ? "404 Not Found" : "200 OK")}\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, _stop.Token); await stream.WriteAsync(body, _stop.Token);
            }
            catch (OperationCanceledException) { } // expected: fixture shutdown
            catch (IOException) { } // expected: browser may abandon speculative connections
        }
        public void Dispose() { _stop.Cancel(); _listener.Stop(); _stop.Dispose(); }
    }
}
