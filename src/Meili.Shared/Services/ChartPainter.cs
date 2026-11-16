using ScottPlot;
using ScottPlot.Plottables;
using Meili.Analytics;
using Meili.Shared.ViewModels;
using Meili.Core.Models;
using BarSeries = Meili.Core.Models.BarSeries;
using Meili.Core.Time;
using Meili.Core.Settings;
using Color = ScottPlot.Color;

namespace Meili.Shared.Services;

/// <summary>Themes the remaining ScottPlot option/strategy plots. K-lines use TradingView in WebView.</summary>
public sealed class ChartPainter
{
    private const float FontSize = 12f;

    /// <summary>A font with CJK glyphs, set by each front end ("Microsoft YaHei UI" on Windows, "PingFang SC" on macOS).</summary>
    public static string FontName { get; set; } = "Microsoft YaHei UI";

    /// <summary>Colours of the last themed option/strategy plot.</summary>
    public ChartPalette Palette { get; private set; } = null!;

    public void ApplyTheme(IPlotControl plot, ChartPalette palette)
    {
        Palette = palette;
        var (text, grid, bg, dataBg) = (palette.Text, palette.Grid, palette.Background, palette.DataBackground);
        var p = plot.Plot;
        p.FigureBackground.Color = bg;
        p.DataBackground.Color = dataBg;
        p.Axes.Color(text);
        p.Axes.Bottom.TickLabelStyle.FontName = FontName;
        p.Axes.Bottom.TickLabelStyle.FontSize = FontSize;
        p.Axes.Left.TickLabelStyle.FontName = FontName;
        p.Axes.Left.TickLabelStyle.FontSize = FontSize;
        p.Axes.Bottom.Label.FontName = FontName;
        p.Axes.Bottom.Label.FontSize = FontSize;
        p.Axes.Left.Label.FontName = FontName;
        p.Axes.Left.Label.FontSize = FontSize;
        p.Axes.Title.Label.FontName = FontName; // E4: the default title font has no CJK glyphs ("IV 微笑" drew as boxes)
        p.Axes.Title.Label.FontSize = FontSize + 1;
        p.Legend.FontName = FontName;
        p.Legend.FontSize = FontSize;
        p.Legend.BackgroundColor = dataBg;
        p.Legend.FontColor = text;
        p.Legend.OutlineColor = grid;
        p.Grid.MajorLineColor = grid;
        p.Grid.MinorLineColor = grid.WithAlpha(40);
        plot.Menu?.Clear();
    }


}

/// <summary>Splits bars into drawable candles and close-only points (F2). Pure, so it is unit-tested without a plot.</summary>
public sealed class PricePlan
{
    public List<Meili.Core.Models.Bar> Candles { get; } = [];
    public List<double> CloseX { get; } = [];
    public List<double> CloseY { get; } = [];
    public bool LineOnly { get; private set; }

    public static PricePlan Build(IReadOnlyList<Meili.Core.Models.Bar> bars)
    {
        var plan = new PricePlan();
        foreach (var b in bars)
        {
            // Re-check consistency even if the provider did: ScottPlot throws on Low > Open/Close.
            if (!b.Incomplete && b.IsConsistent()) plan.Candles.Add(b);
            else { plan.CloseX.Add(MarketClock.ToEastern(b.Time).ToOADate()); plan.CloseY.Add(b.Close); }
        }
        plan.LineOnly = plan.Candles.Count == 0 && plan.CloseX.Count > 0;
        return plan;
    }
}
