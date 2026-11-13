using Avalonia.Controls;
using Avalonia.Threading;
using ScottPlot;
using Meili.Mac.Platform;

namespace Meili.Mac.Views;

public partial class StrategyPage : UserControl
{
    private readonly MacTheme _theme = AppServices.Get<MacTheme>();
    private readonly ChartPainter _themePaint = new();
    private readonly (Button Button, int Days)[] _horizons;

    public StrategyPage()
    {
        InitializeComponent();
        var vm = AppServices.Get<StrategyViewModel>();
        DataContext = vm;
        _horizons = [(Tn0, 0), (Tn7, 7), (Tn14, 14), (Tn30, 30)];
        foreach (var (b, days) in _horizons) b.CommandParameter = days;   // the command takes an int, not the XAML string
        EventHandler onChart = (_, _) => Dispatcher.UIThread.Post(Refresh);
        EventHandler onColors = (_, _) => Dispatcher.UIThread.Post(RenderChart);
        AttachedToVisualTree += async (_, _) =>
        {
            vm.ChartChanged += onChart;
            Ui.Platform.ColorsChanged += onColors;
            Refresh();
            await vm.ActivateAsync();
            Refresh();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            vm.ChartChanged -= onChart;
            Ui.Platform.ColorsChanged -= onColors;
            vm.Deactivate();
        };
    }

    private StrategyViewModel Vm => (StrategyViewModel)DataContext!;

    private void Refresh()
    {
        foreach (var (b, days) in _horizons) b.Classes.Set("Selected", days == Vm.HorizonDays);
        RenderChart();
    }

    private void RenderChart()
    {
        var loc = LocalizationService.Current;
        var plot = PayoffPlot.Plot;
        plot.Clear();
        var c = MacChartPalette.Current(_theme.IsDark);
        _themePaint.ApplyTheme(PayoffPlot, c);

        var curve = Vm.Curve;
        if (curve is null || curve.Prices.Length < 2)
        {
            PayoffPlot.Refresh();
            return;
        }

        var expiry = plot.Add.Scatter(curve.Prices, curve.ExpiryPnl);
        expiry.LegendText = Vm.ExpiryCurveLabel;
        expiry.LineWidth = 2.2f;
        expiry.MarkerSize = 0;
        expiry.Color = c.Series1;

        if (curve.ValuationPnl is { } tn && tn.Length == curve.Prices.Length)
        {
            var s = plot.Add.Scatter(curve.Prices, tn);
            s.LegendText = string.IsNullOrWhiteSpace(Vm.HorizonLabel) ? loc["St_CurrentCurve"] : Vm.HorizonLabel;
            s.LineWidth = 1.8f;
            s.LinePattern = LinePattern.Dashed;
            s.MarkerSize = 0;
            s.Color = c.Series2;
        }

        var zero = plot.Add.HorizontalLine(0);
        zero.LineColor = c.Muted;
        zero.LineWidth = 1;

        if (Vm.Spot is { } spot)
        {
            var v = plot.Add.VerticalLine(spot);
            v.LineColor = c.Muted;
            v.LineWidth = 1;
            v.LinePattern = LinePattern.Dashed;
        }

        if (Vm.ExpiryAnalysis is { Breakevens.Count: > 0 } analysis)
        {
            var xs = analysis.Breakevens.ToArray();
            var be = plot.Add.Scatter(xs, new double[xs.Length]);
            be.LegendText = loc["St_Breakeven"];
            be.LineWidth = 0;
            be.MarkerSize = 9;
            be.MarkerShape = MarkerShape.FilledCircle;
            be.Color = c.Marker;
        }

        plot.Axes.Bottom.Label.Text = loc["St_PriceAxis"];
        plot.Axes.Left.Label.Text = loc["St_PnlAxis"];
        plot.Title(loc["St_Chart"]);
        plot.Legend.IsVisible = true;
        plot.Legend.Alignment = Alignment.UpperRight;
        plot.Axes.AutoScale();   // E4: limits from an earlier render would otherwise stick
        PayoffPlot.Refresh();
    }
}
