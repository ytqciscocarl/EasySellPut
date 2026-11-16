using System.Windows;
using System.Windows.Controls;
using Meili.App.Services;
using Meili.App.Views.Controls;
using Meili.App.Views.Pages;
namespace Meili.App.Tests;
public sealed class ChartFooterTests
{
    [Fact]
    public void Footer_is_one_aligned_row_with_accessible_metadata_and_controls_help()
    {
        WpfHost.Run(async () =>
        {
            var w = new MainWindow();
            try
            {
                w.Show(); w.NavigateForSmoke(typeof(ChartsPage)); await ScreenRenderTests.Settle();
                var page = UiAudit.Descendants<ChartsPage>(w).Single(); var vm = (ChartsViewModel)page.DataContext;
                vm.StatusText = "Yahoo · 120 bars · source and observation time remain available";
                vm.AdjustText = "Split adjusted · dividends excluded";
                vm.CrosshairText = "2026-11-15 09:30 O 120 H 121 L 119 C 120.50 V 10000 MA 120.10 EMA 120.20";
                vm.AdjustWarning = "";
                foreach (var width in new[] { 980, 1920 })
                {
                    w.Width = width; w.Height = 1080; await ScreenRenderTests.Settle();
                    var bar = (Border)page.FindName("ChartStatusBar");
                    var source = (TextBlock)page.FindName("ChartSourceStatus"); var adjust = (TextBlock)page.FindName("ChartAdjustmentStatus");
                    var help = (AppHelp)page.FindName("ChartControlsHelp");
                    Assert.InRange(bar.ActualHeight, 28, 40);
                    var sourceY = source.TranslatePoint(new Point(0, source.ActualHeight / 2), bar).Y;
                    var adjustY = adjust.TranslatePoint(new Point(0, adjust.ActualHeight / 2), bar).Y;
                    Assert.InRange(Math.Abs(sourceY - adjustY), 0, 1);
                    Assert.Equal(vm.StatusText, source.ToolTip); Assert.Equal(vm.AdjustText, adjust.ToolTip);
                    Assert.NotEmpty(help.HelpText); help.IsExpanded = true; await WpfHost.Settle(); Assert.True(help.IsHelpOpen); help.IsExpanded = false;
                }
            }
            finally { w.Close(); }
        });
    }
}
