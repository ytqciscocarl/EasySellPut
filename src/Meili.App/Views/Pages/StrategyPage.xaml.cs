using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScottPlot;
using ScottPlot.Plottables;
using Meili.App.Services;
using Meili.Shared.Services;
using Meili.App.Views.Controls;
using Color = ScottPlot.Color;

namespace Meili.App.Views.Pages;

public partial class StrategyPage : Page
{
    private readonly ThemeService _theme = AppServices.Get<ThemeService>();
    private readonly ChartPainter _themePaint = new();
    private VerticalLine? _whatIfLine;
    private bool _attachedWhatIf;

    public StrategyPage()
    {
        InitializeComponent();
        var vm = AppServices.Get<StrategyViewModel>();
        DataContext = vm;
        Loaded += async (_, _) =>
        {
            if (_active) return;
            _active = true;
            AttachWhatIf(vm);
            vm.ChartChanged += OnChartChanged; vm.PropertyChanged += OnVmPropertyChanged;
            ThemeService.ColorsChanged += OnChartChanged;
            RefreshSurface();
            await vm.ActivateAsync(); RefreshSurface();
        };
        Unloaded += (_, _) =>
        {
            _active = false;
            DetachWhatIf(vm); vm.ChartChanged -= OnChartChanged; vm.PropertyChanged -= OnVmPropertyChanged;
            ThemeService.ColorsChanged -= OnChartChanged;
            Workspace.Stop(); vm.Deactivate();
        };
        SurfaceTabs.SelectionChanged += (_, _) => { if (_active) RefreshSurface(); };
        Ruler.ValueDragged += (_, price) => vm.SetWhatIfPrice(price);
    }

    private bool _active;
    public void ShowDetailedAnalysis() => DetailedTab.IsSelected = true;
    private void OnChartChanged(object? sender, EventArgs e) { if (_active) RefreshSurface(); }
    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    { if (_active && e.PropertyName is nameof(StrategyViewModel.IsBusy) or nameof(StrategyViewModel.StatusText) or nameof(StrategyViewModel.SymbolText)) UpdateWorkspace(); }
    private void UpdateWorkspace() { if (Workspace.Session is not null) Workspace.Update(WpfChartPalette.Current(_theme.IsDark)); }
    private void RefreshSurface()
    {
        if (!_active) return;
        if (WorkbenchTab.IsSelected)
        {
            if (Workspace.Session is null) Workspace.Start(Vm, WpfChartPalette.Current(_theme.IsDark));
            else UpdateWorkspace();
        }
        else { Workspace.Stop(); HighlightHorizonButtons(); HighlightTickButtons(); RenderChart(); SyncRuler(); }
    }
    private StrategyViewModel Vm => (StrategyViewModel)DataContext;

    private void AttachWhatIf(StrategyViewModel vm)
    {
        if (_attachedWhatIf) return;
        vm.WhatIfChanged += OnWhatIfChanged;
        _attachedWhatIf = true;
    }

    private void DetachWhatIf(StrategyViewModel vm)
    {
        if (!_attachedWhatIf) return;
        vm.WhatIfChanged -= OnWhatIfChanged;
        _attachedWhatIf = false;
    }

    private void OnWhatIfChanged(object? sender, EventArgs e)
        => Dispatcher.Invoke(() =>
        {
            UpdateWorkspace();
            if (_whatIfLine is not null && Vm.WhatIfPrice is { } px)
                _whatIfLine.X = px;
            SyncRuler();
            PayoffPlot.Refresh();
        });

    private void SyncRuler()
    {
        Ruler.Minimum = Vm.ChartLow;
        Ruler.Maximum = Vm.ChartHigh;
        Ruler.Step = Vm.TickStep;
        Ruler.Ticks = Vm.RulerTicks;
        if (Vm.WhatIfPrice is { } px) Ruler.Value = px;
        Ruler.InvalidateVisual();
    }

    private void OnSavedClick(object sender, RoutedEventArgs e)
    {
        SavedPopup.IsOpen = true;
    }

    private void OnHorizonClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days))
            Vm.SelectHorizonCommand.Execute(days);
    }

    private void OnTickDensityClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && tag.StartsWith("tick:", StringComparison.Ordinal)
            && int.TryParse(tag.AsSpan(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d))
            Vm.SelectTickDensityCommand.Execute(d);
    }

    private void OnChartClick(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(PayoffPlot);
        var c = PayoffPlot.Plot.GetCoordinates((float)pos.X, (float)pos.Y);
        if (double.IsFinite(c.X) && c.X > 0)
            Vm.SetWhatIfPrice(c.X);
    }

    private void HighlightHorizonButtons()
    {
        foreach (var child in FindButtons(this))
        {
            if (child.Tag is not string tag || !int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days))
                continue;
            var on = days == Vm.HorizonDays;
            PaintChip(child, on);
        }
    }

    private void HighlightTickButtons()
    {
        int current = (int)Vm.TickDensity;
        foreach (var child in FindButtons(this))
        {
            if (child.Tag is not string tag || !tag.StartsWith("tick:", StringComparison.Ordinal)) continue;
            if (!int.TryParse(tag.AsSpan(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d)) continue;
            PaintChip(child, d == current);
        }
    }

    private static void PaintChip(Button child, bool on)
    {
        child.SetResourceReference(Control.BackgroundProperty, on ? "AccentFillColorDefaultBrush" : "ControlFillColorSecondaryBrush");
        child.SetResourceReference(Control.ForegroundProperty, on ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorPrimaryBrush");
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
        if (!_active || WorkbenchTab.IsSelected) return;
        var plot = PayoffPlot.Plot;
        plot.Clear();
        _whatIfLine = null;
        var c = WpfChartPalette.Current(_theme.IsDark);
        var muted = c.Muted;
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

        if (Vm.WhatIfPrice is { } wx)
        {
            _whatIfLine = plot.Add.VerticalLine(wx);
            _whatIfLine.LineColor = c.Series2;
            _whatIfLine.LineWidth = 2;
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

    private static string Find(string key) => Application.Current.TryFindResource(key) as string ?? key;
}
