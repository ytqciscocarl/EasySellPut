using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Meili.Mac.Views;
using Meili.Mac.Views.Controls;
namespace Meili.Mac.Tests;
public sealed class ChartFooterTests
{
    [AvaloniaFact]
    public async Task Status_metadata_and_help_share_one_row_and_keep_full_text_when_space_is_limited()
    {
        MacHost.EnsureComposed(); var w = new MainWindow();
        try
        {
            w.Show(); w.Navigate(AppPage.Charts); await Task.Delay(100); MacHost.Pump();
            var page = (ChartsPage)w.CurrentView!; var vm = (ChartsViewModel)page.DataContext!;
            vm.StatusText = "Yahoo · 120 bars · source and observation time remain available";
            vm.AdjustText = "Split adjusted · dividends excluded";
            vm.CrosshairText = "2026-11-15 09:30 O 120.00 H 121.00 L 119.00 C 120.50 V 10000 MA 120.10 EMA 120.20";
            vm.AdjustWarning = "";
            foreach (var width in new[] { 980, 1920 })
            {
                w.Width = width; w.Height = 1080; MacHost.Pump();
                var bar = page.FindControl<Border>("ChartStatusBar")!;
                var source = page.FindControl<TextBlock>("ChartSourceStatus")!;
                var adjust = page.FindControl<TextBlock>("ChartAdjustmentStatus")!;
                var help = page.FindControl<AppHelp>("ChartControlsHelp")!;
                Assert.InRange(bar.Bounds.Height, 28, 40);
                var sourceY = source.TranslatePoint(new Point(0, source.Bounds.Height / 2), bar)!.Value.Y;
                var adjustY = adjust.TranslatePoint(new Point(0, adjust.Bounds.Height / 2), bar)!.Value.Y;
                Assert.InRange(Math.Abs(sourceY - adjustY), 0, 1);
                Assert.Equal(vm.StatusText, ToolTip.GetTip(source)); Assert.Equal(vm.AdjustText, ToolTip.GetTip(adjust));
                Assert.True(source.Bounds.Width <= 180); Assert.True(adjust.Bounds.Width <= 136);
                Assert.NotEmpty(help.HelpText); help.IsExpanded = true; MacHost.Pump(); Assert.True(help.IsHelpOpen); help.IsExpanded = false;
            }
            MacHost.Save(w, "chart-single-status-row-1080p");
        }
        finally { w.AllowClose(); w.Close(); }
    }
}
