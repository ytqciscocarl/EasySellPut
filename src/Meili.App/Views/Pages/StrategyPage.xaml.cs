using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScottPlot;
using Meili.App.Services;
using Color = ScottPlot.Color;

namespace Meili.App.Views.Pages;

public partial class StrategyPage : Page
{
    private readonly ThemeService _theme = AppServices.Get<ThemeService>();
    private readonly ChartPainter _themePaint = new();

    public StrategyPage()
    {
        InitializeComponent();
        var vm = AppServices.Get<StrategyViewModel>();
        DataContext = vm;
        Loaded += async (_, _) =>
        {
            HighlightHorizonButtons();
            await vm.ActivateAsync();
            RenderChart();
        };
        Unloaded += (_, _) => vm.Deactivate();
        vm.ChartChanged += (_, _) => Dispatcher.Invoke(() =>
        {
            HighlightHorizonButtons();
            RenderChart();
        });
        ThemeService.ColorsChanged += (_, _) => Dispatcher.Invoke(RenderChart);   // E5
    }

    private StrategyViewModel Vm => (StrategyViewModel)DataContext;


    private void OnHorizonClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days))
            Vm.SelectHorizonCommand.Execute(days);
    }

    private void HighlightHorizonButtons()
    {
        foreach (var child in FindButtons(this))
        {
            if (child.Tag is not string tag || !int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days))
                continue;
            var on = days == Vm.HorizonDays;
            child.SetResourceReference(Control.BackgroundProperty, on ? "AccentFillColorDefaultBrush" : "ControlFillColorSecondaryBrush");
            child.SetResourceReference(Control.ForegroundProperty, on ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorPrimaryBrush");
        }
    }

    private static IEnumerable<Button> FindButtons(DependencyObject root)
    {
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is Button b) yield return b;
            foreach (var x in FindButtons(c)) yield return x;
        }
    }

    private void RenderChart()
    {
        var plot = PayoffPlot.Plot;
        plot.Clear();
        var c = WpfChartPalette.Current(_theme.IsDark);
        var muted = c.Muted;
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
            s.LegendText = string.IsNullOrWhiteSpace(Vm.HorizonLabel) ? Find("St_CurrentCurve") : Vm.HorizonLabel;
            s.LineWidth = 1.8f;
            s.LinePattern = LinePattern.Dashed;
            s.MarkerSize = 0;
            s.Color = c.Series2;
        }

        var zero = plot.Add.HorizontalLine(0);
        zero.LineColor = muted;
        zero.LineWidth = 1;

        if (Vm.Spot is { } spot)
        {
            var v = plot.Add.VerticalLine(spot);
            v.LineColor = muted;
            v.LineWidth = 1;
            v.LinePattern = LinePattern.Dashed;
        }

        if (Vm.ExpiryAnalysis is { Breakevens.Count: > 0 } analysis)
        {
            var xs = analysis.Breakevens.ToArray();
            var ys = Enumerable.Repeat(0.0, xs.Length).ToArray();
            var be = plot.Add.Scatter(xs, ys);
            be.LegendText = Find("St_Breakeven");
            be.LineWidth = 0;
            be.MarkerSize = 9;
            be.MarkerShape = MarkerShape.FilledCircle;
            be.Color = c.Marker;
        }

        plot.Axes.Bottom.Label.Text = Find("St_PriceAxis");
        plot.Axes.Left.Label.Text = Find("St_PnlAxis");
        plot.Title(Find("St_Chart"));
        plot.Legend.IsVisible = true;
        plot.Legend.Alignment = Alignment.UpperRight;
        plot.Axes.AutoScale(); // E4: limits from an earlier render would otherwise stick
        PayoffPlot.Refresh();
    }

    private static string Find(string key) => Application.Current.TryFindResource(key) as string ?? key;
}
