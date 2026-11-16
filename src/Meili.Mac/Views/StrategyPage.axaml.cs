using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using ScottPlot;
using ScottPlot.Plottables;
using Meili.Mac.Platform;
using Meili.Shared.Services;

namespace Meili.Mac.Views;

public partial class StrategyPage : UserControl
{
    private readonly MacTheme _theme = AppServices.Get<MacTheme>();
    private readonly ChartPainter _themePaint = new();
    private readonly (Button Button, int Days)[] _horizons = [];
    private readonly (Button Button, int Density)[] _ticks = [];
    private VerticalLine? _whatIfLine;
    private EventHandler? _onChart, _onColors, _onWhatIf;
    private bool _attached;

    public StrategyPage()
    {
        InitializeComponent();
        var vm = AppServices.Get<StrategyViewModel>();
        DataContext = vm;
        _horizons = [(Tn0, 0), (Tn7, 7), (Tn14, 14), (Tn30, 30)];
        _ticks = [(Td0, 0), (Td1, 1), (Td2, 2)];
        foreach (var (b, days) in _horizons) b.CommandParameter = days;
        foreach (var (b, d) in _ticks) b.CommandParameter = d;
        Ruler.ValueDragged += (_, price) => vm.SetWhatIfPrice(price);
        _onChart = (_, _) => Dispatcher.UIThread.Post(RefreshAll);
        _onColors = (_, _) => Dispatcher.UIThread.Post(RenderChart);
        _onWhatIf = (_, _) => Dispatcher.UIThread.Post(MoveWhatIf);
        SurfaceTabs.SelectionChanged += (_, _) => { if (_attached) RefreshAll(); };
        AttachedToVisualTree += async (_, _) =>
        {
            if (!_attached)
            {
                vm.ChartChanged += _onChart;
                vm.WhatIfChanged += _onWhatIf;
                Ui.Platform.ColorsChanged += _onColors;
                _attached = true;
                vm.PropertyChanged += OnVmPropertyChanged;
            }
            RefreshAll();
            await vm.ActivateAsync();
            RefreshAll();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (_attached)
            {
                vm.ChartChanged -= _onChart;
                vm.WhatIfChanged -= _onWhatIf;
                Ui.Platform.ColorsChanged -= _onColors;
                _attached = false;
                vm.PropertyChanged -= OnVmPropertyChanged;
                Workspace.Stop();
            }
            vm.Deactivate();
        };
    }

    public void ShowDetailedAnalysis() => DetailedTab.IsSelected = true;
    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    { if (_attached && e.PropertyName is nameof(StrategyViewModel.IsBusy) or nameof(StrategyViewModel.StatusText) or nameof(StrategyViewModel.SymbolText)) UpdateWorkspace(); }
    private void UpdateWorkspace() { if (Workspace.Session is not null) Workspace.Update(MacChartPalette.Current(_theme.IsDark)); }
    private StrategyViewModel Vm => (StrategyViewModel)DataContext!;

    private void OnChartClick(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        var pos = e.GetPosition(PayoffPlot);
        var c = PayoffPlot.Plot.GetCoordinates((float)pos.X, (float)pos.Y);
        if (double.IsFinite(c.X) && c.X > 0)
            Vm.SetWhatIfPrice(c.X);
    }

    private void RefreshAll()
    {
        if (!_attached) return;
        if (WorkbenchTab.IsSelected)
        {
            if (Workspace.Session is null) Workspace.Start(Vm, MacChartPalette.Current(_theme.IsDark));
            else UpdateWorkspace();
            return;
        }
        Workspace.Stop();
        foreach (var (b, days) in _horizons) b.Classes.Set("Selected", days == Vm.HorizonDays);
        int density = (int)Vm.TickDensity;
        foreach (var (b, d) in _ticks) b.Classes.Set("Selected", d == density);
        RenderChart();
        SyncRuler();
    }

    private void MoveWhatIf()
    {
        UpdateWorkspace();
        if (_whatIfLine is not null && Vm.WhatIfPrice is { } px)
            _whatIfLine.X = px;
        SyncRuler();
        PayoffPlot.Refresh();
    }

    private void SyncRuler()
    {
        Ruler.Minimum = Vm.ChartLow;
        Ruler.Maximum = Vm.ChartHigh;
        Ruler.Step = Vm.TickStep;
        Ruler.Ticks = Vm.RulerTicks;
        if (Vm.WhatIfPrice is { } px) Ruler.Value = px;
        Ruler.InvalidateVisual();
    }

    private void RenderChart()
    {
        if (!_attached) return;
        if (WorkbenchTab.IsSelected) { UpdateWorkspace(); return; }
        var loc = LocalizationService.Current;
        var plot = PayoffPlot.Plot;
        plot.Clear();
        _whatIfLine = null;
        var c = MacChartPalette.Current(_theme.IsDark);
        _themePaint.ApplyTheme(PayoffPlot, c);

        var curve = Vm.Curve;
        if (curve is null || curve.Prices.Length < 2)
        {
            PayoffPlot.Refresh();
            return;
        }

        FillZones(plot, curve, c);

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

        if (Vm.WhatIfPrice is { } wx)
        {
            _whatIfLine = plot.Add.VerticalLine(wx);
            _whatIfLine.LineColor = c.Series2;
            _whatIfLine.LineWidth = 2;
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
        plot.Axes.AutoScale();
        PayoffPlot.Refresh();
    }

    private static void FillZones(Plot plot, Meili.Analytics.PayoffCurve curve, ChartPalette c)
    {
        int n = curve.Prices.Length;
        int i = 0;
        while (i < n)
        {
            int sign = Math.Sign(curve.ExpiryPnl[i]);
            int j = i + 1;
            while (j < n && Math.Sign(curve.ExpiryPnl[j]) == sign) j++;
            if (sign != 0)
            {
                var span = plot.Add.HorizontalSpan(curve.Prices[i], curve.Prices[Math.Min(j, n - 1)]);
                span.LineWidth = 0;
                var fill = (sign > 0 ? c.Up : c.Down).WithAlpha(28);
                span.FillStyle.Color = fill;
                span.LineStyle.Color = fill.WithAlpha(0);
            }
            i = j;
        }
    }
}
