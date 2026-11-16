using System.Windows;
using System.Windows.Media;
using Meili.App.Services;
namespace Meili.App.Tests;
public sealed class WindowChromeTests
{
    [Fact] public void Integrated_caption_preserves_controls_and_tracks_live_theme()
    {
        WpfHost.Run(async()=>
        {
            var w=new MainWindow{Width=980,Height=840};w.Show();
            try
            {
                var title=(Wpf.Ui.Controls.TitleBar)w.FindName("TitleBar");
                foreach(var dark in new[]{false,true})
                {
                    ScreenRenderTests.Apply("zh-CN",dark);await ScreenRenderTests.Settle();
                    Assert.Equal(DesignTokens.WindowHeaderHeight,title.ActualHeight);
                    Assert.Equal(ThemePalette.For(dark).Window,ColorValue((SolidColorBrush)title.Background));
                    Assert.Empty(title.Title);Assert.Equal(LocalizationService.Current["App_Title"],w.Title);
                    Assert.True(w.ExtendsContentIntoTitleBar);Assert.True(title.ShowClose);Assert.True(title.ShowMinimize);Assert.True(title.ShowMaximize);
                    var workspace=(FrameworkElement)w.FindName("WorkspaceGrid");Assert.True(workspace.TranslatePoint(default,w).Y>=title.ActualHeight);
                }
            }
            finally{w.Close();}
        });
    }
    private static uint ColorValue(SolidColorBrush b)=>(uint)(b.Color.A<<24|b.Color.R<<16|b.Color.G<<8|b.Color.B);
}
