using System.Windows.Controls;
using Meili.App.Services;
namespace Meili.App.Tests;
public sealed class NewsSplitTests
{
    [Fact] public void Split_layout_uses_actual_available_space_and_keeps_the_reading_ratio()
    {
        WpfHost.Run(()=>
        {
            var article=new Border();var split=new Meili.App.Views.Controls.ArticleSplitView(new Border());split.Open(article);
            foreach(var width in new[]{1440,980,1440})
            {
                split.Measure(new System.Windows.Size(width,900));split.Arrange(new System.Windows.Rect(0,0,width,900));split.UpdateLayout();
                Assert.False(split.IsVertical);
            }
            split.ColumnDefinitions[0].Width=new System.Windows.GridLength(3,System.Windows.GridUnitType.Star);split.Open(article);
            Assert.Equal(new System.Windows.GridLength(3,System.Windows.GridUnitType.Star),split.ColumnDefinitions[0].Width);split.Close();Assert.False(split.IsOpen);
        });
    }
    [Fact] public void News_reader_is_inline_compact_reusable_and_closable()
    {
        WpfHost.Run(async()=>
        {
            using var publisher=new NewsReaderFixture();
            var originalLanguage=LocalizationService.Current.Language;var originalDark=AppServices.Get<ThemeService>().IsDark;
            ScreenRenderTests.Apply("zh-CN",false);
            var model=AppServices.Get<ChartsViewModel>();model.SymbolText="AAPL";model.ShowInspector=false;await model.LoadAsync();Assert.NotNull(model.Series);
            var w=new MainWindow{Width=1440,Height=900};w.Show();w.NavigateForSmoke(typeof(Meili.App.Views.Pages.ChartsPage));await ScreenRenderTests.Settle();
            try
            {
                w.OpenNewsArticle(publisher.Url,"本地演示新闻");await ScreenRenderTests.Settle();
                Assert.True(w.NewsSplit!.IsOpen);Assert.False(w.NewsSplit.IsVertical);
                var pane=UiAudit.Descendants<WebTabPane>(w).Single();Assert.Empty(UiAudit.Descendants<TextBox>(pane));Assert.Equal(3,UiAudit.Descendants<Button>(pane).Count());
                var deadline=DateTimeOffset.UtcNow.AddSeconds(20);while(pane.DocumentTitle!="本地演示新闻"&&DateTimeOffset.UtcNow<deadline)await Task.Delay(100);
                Assert.Equal("本地演示新闻",pane.DocumentTitle);
                w.OpenNewsArticle(publisher.Url+"?second","Second");Assert.Same(pane,UiAudit.Descendants<WebTabPane>(w).Single());
                w.Width=980;await ScreenRenderTests.Settle();Assert.False(w.NewsSplit.IsVertical);
                Assert.Throws<ArgumentException>(()=>w.OpenNewsArticle("file:///tmp/blocked","Blocked"));
                if(Environment.GetEnvironmentVariable("MEILI_RENDER_DIR") is {Length:>0} dir)
                {
                    w.Width=Math.Min(1920,System.Windows.SystemParameters.WorkArea.Width);w.Height=Math.Min(1080,System.Windows.SystemParameters.WorkArea.Height);w.Left=System.Windows.SystemParameters.WorkArea.Left;w.Top=System.Windows.SystemParameters.WorkArea.Top;w.Activate();w.Topmost=true;await Task.Delay(400);
                    var chart=UiAudit.Descendants<Meili.App.Views.Controls.WebChartControl>(w).Single();
                    var renderDeadline=DateTimeOffset.UtcNow.AddSeconds(20);var rendered=false;
                    while(DateTimeOffset.UtcNow<renderDeadline)
                    {
                        if(chart.LastDiagnostic is {Length:>0} diagnostic)
                        {
                            using var json=System.Text.Json.JsonDocument.Parse(diagnostic);
                            if(json.RootElement.TryGetProperty("status",out var status)&&status.GetString()=="rendered"){rendered=true;break;}
                        }
                        await Task.Delay(100);
                    }
                    Assert.True(rendered,"Article split chart did not finish rendering");await Task.Delay(250);
                    System.IO.Directory.CreateDirectory(dir);
                    var captured=NewsReaderFixture.Capture(w,System.IO.Path.Combine(dir,"desktop-smoke-news-split-windows.png"));
                    var desktopCaptured=NewsReaderFixture.Capture(w,System.IO.Path.Combine(dir,"desktop-smoke-news-split-full-desktop.png"),entireDesktop:true);
                    System.IO.File.WriteAllText(System.IO.Path.Combine(dir,"news-split-native-capture.json"),System.Text.Json.JsonSerializer.Serialize(new{chartRendered=rendered,articleLoaded=pane.DocumentTitle=="本地演示新闻",captured,desktopCaptured,reason=captured?"foreground native window":"desktop obscured by another system window"}));w.Topmost=false;
                }
                w.CloseNewsArticle();await ScreenRenderTests.Settle();Assert.False(w.NewsSplit.IsOpen);Assert.Empty(UiAudit.Descendants<WebTabPane>(w));
            }
            finally{w.Close();ScreenRenderTests.Apply(originalLanguage,originalDark);}
        });
    }
}
