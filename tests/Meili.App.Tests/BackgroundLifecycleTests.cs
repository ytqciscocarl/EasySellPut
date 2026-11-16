using System.IO;
using System.Windows;
using Meili.App.Services;
using Meili.App.Views.Controls;
using Meili.App.Views.Pages;
using Meili.Core.Quotes;
namespace Meili.App.Tests;
public sealed class BackgroundLifecycleTests
{
    [Fact] public void Background_and_minimize_unload_private_charts_and_restore_navigation_without_losing_the_model()
    {
        WpfHost.Run(async()=>
        {
            var w=new MainWindow();w.Show();w.NavigateForSmoke(typeof(ChartsPage));await ScreenRenderTests.Settle();var navigation=(Wpf.Ui.Controls.NavigationView)w.FindName("RootNavigation");var host=(System.Windows.Controls.Grid)w.FindName("NativePageHost");var page=UiAudit.Descendants<ChartsPage>(w).Single();var chart=(WebChartControl)page.FindName("Chart");var model=page.DataContext;var shell=AppServices.Get<ShellViewModel>();
            var theme=AppServices.Get<ThemeService>();theme.Apply(Meili.Core.Settings.AppThemeMode.System,w);
            try
            {
                Assert.NotNull(chart.Session);w.RunInBackground();await ScreenRenderTests.Settle();Assert.True(w.PresentationSuspended);Assert.Null(chart.Session);Assert.False(shell.IsClockRunning);Assert.False(host.Children.Contains(navigation));
                for(var i=0;i<3;i++){w.WindowState=WindowState.Normal;w.Show();await ScreenRenderTests.Settle();Assert.NotNull(chart.Session);Assert.Same(model,page.DataContext);Assert.True(shell.IsClockRunning);w.Hide();await ScreenRenderTests.Settle();Assert.Null(chart.Session);}
                w.Show();w.WindowState=WindowState.Normal;await ScreenRenderTests.Settle();Assert.True(host.Children.Contains(navigation));
            }
            finally{w.Close();theme.Apply(Meili.Core.Settings.AppThemeMode.Dark);}
        });
    }
}
