using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Meili.Mac.Platform;
namespace Meili.Mac.Tests;
public sealed class NewsSplitTests
{
    [AvaloniaFact] public async Task Articles_share_the_shell_without_modal_or_address_bar_and_resize_without_recreating_the_reader()
    {
        MacHost.EnsureComposed();var w=new MainWindow{Width=1440,Height=900};w.Show();w.Navigate(AppPage.News);await Task.Delay(150);MacHost.Pump();var original=w.CurrentView;
        try
        {
            w.OpenNewsArticle("https://example.com/story-one","Fixture article");MacHost.Pump();
            Assert.Empty(w.OwnedWindows);Assert.True(w.NewsSplit!.IsOpen);Assert.False(w.NewsSplit.IsVertical);
            var browser=Assert.Single(w.NewsSplit.GetVisualDescendants().OfType<WebTabPane>());
            Assert.Empty(browser.GetVisualDescendants().OfType<TextBox>());
            Assert.Equal(3,browser.GetVisualDescendants().OfType<Button>().Count());
            Assert.Contains(browser.GetVisualDescendants().OfType<TextBlock>(),t=>t.Text=="example.com");
            MacHost.Save(w,"news-split-wide");
            w.NewsSplit.ColumnDefinitions[0].Width=new GridLength(3,GridUnitType.Star);
            w.OpenNewsArticle("https://example.com/story-two","Second fixture article");MacHost.Pump();Assert.Same(browser,Assert.Single(w.NewsSplit.GetVisualDescendants().OfType<WebTabPane>()));
            Assert.Equal(new GridLength(3,GridUnitType.Star),w.NewsSplit.ColumnDefinitions[0].Width);
            foreach(var dark in new[]{false,true})
            {
                AppServices.Get<MacTheme>().Apply(dark?Meili.Core.Settings.AppThemeMode.Dark:Meili.Core.Settings.AppThemeMode.Light);
                foreach(var width in new[]{1440,980}){w.Width=width;MacHost.Pump();Assert.False(w.NewsSplit.IsVertical);MacHost.Save(w,$"news-split-{width}-{dark}");}
            }
            Assert.Throws<ArgumentException>(()=>w.OpenNewsArticle("file:///tmp/blocked","Blocked"));
            w.KeyPress(Key.Escape,RawInputModifiers.None,PhysicalKey.Escape,null);MacHost.Pump();Assert.False(w.NewsSplit.IsOpen);Assert.Same(original,w.CurrentView);
        }
        finally{w.AllowClose();w.Close();}
    }
}
