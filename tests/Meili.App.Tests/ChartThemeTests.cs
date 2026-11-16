using System.Net.Http;
using System.Text.Json;
using Meili.App.Views.Controls;
using System.IO;
using System.Windows.Controls;
using ScottPlot.WPF;
using Meili.App.Services;
using Meili.App.Views.Pages;
using Meili.Core.Settings;
using MColor = System.Windows.Media.Color;

namespace Meili.App.Tests;

/// <summary>E5: ScottPlot charts repaint with the new theme and up/down colours, without reopening the page.</summary>
public class ChartThemeTests
{
    private static MColor M(ScottPlot.Color c) => MColor.FromRgb(c.R, c.G, c.B);

    [Fact]
    public void Series_colours_keep_3_to_1_contrast_on_the_plot_background_in_both_themes()
    {
        WpfHost.Run(() =>
        {
            try
            {
                foreach (var dark in new[] { true, false })
                {
                    ScreenRenderTests.Apply("zh-CN", dark);
                    var c = WpfChartPalette.Current(dark);
                    foreach (var s in new[] { c.Series1, c.Series2, c.Series3, c.Series4, c.Series5, c.Series6, c.Marker, c.Up, c.Down })
                        Assert.True(UiAudit.Contrast(M(s), M(c.DataBackground)) >= 3, $"{(dark ? "dark" : "light")} {s.ToHex()}");
                    Assert.True(UiAudit.Contrast(M(c.Text), M(c.Background)) >= 4.5);
                }
            }
            finally { ScreenRenderTests.Apply("zh-CN", true); }
        });
    }

    [Fact]
    public void Kline_page_forwards_runtime_theme_and_up_down_colors_to_the_local_webview_session()
    {
        WpfHost.Run(async () =>
        {
            try
            {
                ScreenRenderTests.Apply("zh-CN", true);
                var page = new ChartsPage();
                using var host = new OffscreenHost(page, 1280, 800);
                await ScreenRenderTests.Settle();
                var tools = UiAudit.Descendants<System.Windows.Controls.Primitives.ButtonBase>(page).Where(b => b.Command == ((ChartsViewModel)page.DataContext).StartDrawingCommand || b.Command == ((ChartsViewModel)page.DataContext).ClearDrawingsCommand).ToArray();
                Assert.Equal(3,tools.Length);Assert.All(tools,b=>{Assert.Equal(56,b.ActualWidth);Assert.Equal(56,b.ActualHeight);});
                var symbol=UiAudit.Descendants<SymbolBox>(page).First();
                var dropdown=UiAudit.Descendants<Wpf.Ui.Controls.Button>(symbol).Single(b=>System.Windows.Automation.AutomationProperties.GetAutomationId(b)=="SymbolBoxDropDown");
                Assert.Equal(32,dropdown.ActualWidth);Assert.Equal(32,dropdown.ActualHeight);
                var web = UiAudit.Descendants<WebChartControl>(page).Single();
                using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
                async Task<string?> Field(string key)
                {
                    using var json = JsonDocument.Parse(await http.GetStringAsync(new Uri(web.Session!.PageUri, "state")));
                    return json.RootElement.GetProperty("data").GetProperty("theme").GetProperty(key).GetString();
                }
                Assert.Equal($"#{ThemePalette.Dark.Chart & 0xFFFFFF:X6}", await Field("background"));
                ScreenRenderTests.Apply("zh-CN", false);
                ThemeService.ApplyUpDownColors(UpDownColorMode.GreenUp);
                await ScreenRenderTests.Settle();
                Assert.Equal($"#{ThemePalette.Light.Chart & 0xFFFFFF:X6}", await Field("background"));
                Assert.Equal("#1A7050", await Field("up"));
            }
            finally
            {
                ThemeService.ApplyUpDownColors(AppServices.Get<SettingsStore>().Current.UpDownColors);
                ScreenRenderTests.Apply("zh-CN", true);
            }
        });
    }

    [Fact]
    public void Reader_chrome_uses_live_resources_and_localized_error_state()
    {
        WpfHost.Run(() =>
        {
            var loc = LocalizationService.Current;
            var reader = new NewsReaderWindow();
            try
            {
                var root = (DockPanel)reader.Content;
                var status = root.Children.OfType<TextBlock>().Single();
                var toolbar = root.Children.OfType<WrapPanel>().Single();
                Assert.Equal(System.Windows.Visibility.Collapsed, status.Visibility);
                foreach (var dark in new[] { true, false })
                {
                    ScreenRenderTests.Apply("zh-CN", dark);
                    var palette = ThemePalette.For(dark);
                    Assert.Equal(MColor.FromRgb((byte)(palette.Window >> 16), (byte)(palette.Window >> 8), (byte)palette.Window), ((System.Windows.Media.SolidColorBrush)reader.Background).Color);
                    reader.OpenArticle("file:///blocked", "Blocked");
                    Assert.Equal(System.Windows.Visibility.Visible, status.Visibility);
                    Assert.Equal(loc["Reader_Blocked"], status.Text);
                    Assert.All(toolbar.Children.OfType<Wpf.Ui.Controls.Button>(), b => Assert.True(b.FontSize >= 14 && b.MinHeight >= 32));
                }
                loc.Apply("en-US");
                Assert.Equal(loc["Reader_Blocked"], status.Text);
            }
            finally { reader.Close(); loc.Apply("zh-CN"); ScreenRenderTests.Apply("zh-CN", true); }
        });
    }

    [Fact]
    public void Strategy_default_workbench_and_details_share_one_model_and_release_hidden_browser()
    {
        WpfHost.Run(async()=>
        {
            var page=new StrategyPage();
            using var host=new OffscreenHost(page,1280,900);
            await ScreenRenderTests.Settle();
            var web=UiAudit.Descendants<WebChartControl>(page).Single();
            var session=web.Session;
            Assert.NotNull(session);Assert.True(session!.IsStrategyWorkbench);
            var model=page.DataContext;
            page.ShowDetailedAnalysis();await ScreenRenderTests.Settle();
            Assert.Null(web.Session);Assert.Same(model,page.DataContext);
            UiAudit.Descendants<TabControl>(page).First(t=>t.Name=="SurfaceTabs").SelectedIndex=0;
            await ScreenRenderTests.Settle();
            Assert.NotNull(web.Session);Assert.NotSame(session,web.Session);
        });
    }

    [Theory]
    [InlineData(typeof(OptionsPage))]
    [InlineData(typeof(StrategyPage))]
    public void Open_chart_follows_theme_and_up_down_switches(Type pageType)
    {
        var dir = Environment.GetEnvironmentVariable("MEILI_RENDER_DIR");
        WpfHost.Run(async () =>
        {
            var name = pageType.Name.Replace("Page", "").ToLowerInvariant();
            try
            {
                ScreenRenderTests.Apply("zh-CN", true);
                if(pageType==typeof(StrategyPage))
                {
                    // Other tests retain manual drafts in the singleton model. Build a
                    // known, renderable preset before asserting the detailed plot theme.
                    var vm=AppServices.Get<StrategyViewModel>();vm.SymbolText="MSFT";await vm.LoadAsync();
                    vm.SelectedPreset=vm.Presets.First(p=>p.Kind==Meili.Analytics.StrategyKind.CoveredCall);
                    vm.ApplyPresetCommand.Execute(null);Assert.True(vm.HasCurve);
                }
                var page = (Page)Activator.CreateInstance(pageType)!;
                if (page is StrategyPage strategyPage) strategyPage.ShowDetailedAnalysis();
                using var host = new OffscreenHost(page, 1280, 800);
                await ScreenRenderTests.Settle();
                var plot = UiAudit.Descendants<WpfPlot>(page).First(p => p.IsVisible);
                Assert.Equal(WpfChartPalette.Current(true).Background, plot.Plot.FigureBackground.Color);
                if (dir is { Length: > 0 }) host.SavePng(Path.Combine(dir, $"e5-{name}-1-dark.png"));

                ScreenRenderTests.Apply("zh-CN", false);                        // switch while the page is open
                await ScreenRenderTests.Settle();
                var light = WpfChartPalette.Current(false);
                Assert.Equal(light.Background, plot.Plot.FigureBackground.Color);
                Assert.Equal(light.DataBackground, plot.Plot.DataBackground.Color);
                if (dir is { Length: > 0 }) host.SavePng(Path.Combine(dir, $"e5-{name}-2-light.png"));


            }
            finally
            {
                ThemeService.ApplyUpDownColors(AppServices.Get<SettingsStore>().Current.UpDownColors);
                ScreenRenderTests.Apply("zh-CN", true);
            }
        });
    }
}
