using System.Text.Json;
using Meili.Shared.ViewModels;
using Meili.Core.Models;
using Meili.Core.Time;

namespace Meili.Shared.Services;

/// <summary>Desktop-only rendering contract. Indicators come from the same Analytics snapshot as CLI/MCP.</summary>
public static class WebChartSnapshot
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string Serialize(ChartsViewModel vm, ChartPalette palette)
    {
        var bars = vm.Series?.Bars;
        var snapshot = vm.Snapshot;
        var indices = bars is null ? [] : Enumerable.Range(0, bars.Count)
            .GroupBy(i => bars[i].Time.ToUnixTimeSeconds()).Select(g => g.Last())
            .OrderBy(i => bars[i].Time).ToArray();
        static double? Finite(double n) => double.IsFinite(n) ? n : null;
        var rows = indices.Select(i =>
        {
            var b = bars![i];
            var complete = !b.Incomplete && b.IsConsistent();
            return new
            {
                time = b.Time.ToUnixTimeSeconds(), index = i,
                session = vm.Series!.Interval.IsIntraday() ? MarketClock.GetSession(b.Time).ToString() : null,
                open = complete ? Finite(b.Open) : null, high = complete ? Finite(b.High) : null,
                low = complete ? Finite(b.Low) : null, close = Finite(b.Close), incomplete = !complete,
                volume = b.Volume >= 0 ? Finite(b.Volume) : null,
                ma = snapshot is null ? null : Finite(snapshot.Sma[i]),
                ema = snapshot is null ? null : Finite(snapshot.Ema[i]),
                bollMiddle = snapshot is null ? null : Finite(snapshot.Bollinger.Middle[i]),
                bollUpper = snapshot is null ? null : Finite(snapshot.Bollinger.Upper[i]),
                bollLower = snapshot is null ? null : Finite(snapshot.Bollinger.Lower[i]),
                macd = snapshot is null ? null : Finite(snapshot.Macd.Macd[i]),
                signal = snapshot is null ? null : Finite(snapshot.Macd.Signal[i]),
                histogram = snapshot is null ? null : Finite(snapshot.Macd.Histogram[i]),
                rsi = snapshot is null ? null : Finite(snapshot.Rsi[i]),
                k = snapshot is null ? null : Finite(snapshot.Kdj.K[i]),
                d = snapshot is null ? null : Finite(snapshot.Kdj.D[i]),
                j = snapshot is null ? null : Finite(snapshot.Kdj.J[i]),
            };
        }).ToArray();
        var compare = vm.CompareLine is { } line ? line.Time.Select((t, i) => new { time = t.ToUnixTimeSeconds(), value = Finite(line.Close[i]) }).ToArray() : [];
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1, key = vm.ChartKey,
            symbol = vm.Series?.Symbol ?? vm.SymbolText, preserveView = vm.PreserveView,
            language = LocalizationService.Current.Language, intraday = vm.Series?.Interval.IsIntraday() ?? false,
            events = ChartEventAlignment.Map(vm), timelineStatus = vm.Timeline is not null && vm.Timeline.Symbol == vm.Series?.Symbol ? vm.Timeline.Status : "loading",
            coverage = vm.Timeline is not null && vm.Timeline.Symbol == vm.Series?.Symbol ? vm.Timeline.Coverage : null,
            rows, compare, compareLabel = vm.CompareLabel,
            show = new { ma = vm.ShowMa, ema = vm.ShowEma, boll = vm.ShowBoll, volume = vm.ShowVolume, macd = vm.ShowMacd, rsi = vm.ShowRsi, kdj = vm.ShowKdj },
            labels = new { ma = vm.MaLabel, ema = vm.EmaLabel, boll = vm.BollLabel, macd = vm.MacdLabel, rsi = vm.RsiLabel, kdj = vm.KdjLabel, volume = LocalizationService.Current["Ch_Volume"], empty = LocalizationService.Current["Ch_Empty"] },
            theme = Theme(palette),
            drawingMode = vm.DrawMode?.ToString(), drawingRevision = vm.DrawingRevision, drawingEpoch = vm.DrawingEpoch,
            drawingStart = vm.DrawingStart is { } start ? new { time = start.Time.ToUnixTimeSeconds(), price = start.Price } : null,
            drawings = vm.Drawings.Where(d => double.IsFinite(d.Price1) && double.IsFinite(d.Price2)).Select(d => new { kind = d.Kind.ToString(), price1 = d.Price1, time1 = d.Time1?.ToUnixTimeSeconds(), price2 = d.Price2, time2 = d.Time2?.ToUnixTimeSeconds() }),
        }, Json);
    }
    public static object Theme(ChartPalette palette)
    {
        static string Css(ScottPlot.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        var ui = ThemePalette.For(palette.Dark);
        static string Token(uint argb) => $"#{argb & 0xFFFFFF:X6}";
        var components=DesignTokens.ComponentBrushes(palette.Dark).ToDictionary(x=>x.Key,x=>Token(x.Argb));
        return new { reducedMotion=DesignMotion.Reduced, brand=components["BrandBrush"],focus=components["FocusBrush"],link=components["LinkBrush"],input=components["InputBackgroundBrush"],hover=components["HoverBrush"],pressed=components["PressedBrush"],danger=components["DangerBrush"], dark = palette.Dark, control = Token(ui.Control), border = Token(ui.Border), accent = Token(ui.Accent), errorBackground = Token(ui.ErrorBar), errorText = Token(ui.Degraded), sessionPre = Token(ui.PreMarketTint) + (ui.PreMarketTint >> 24).ToString("X2"), sessionPost = Token(ui.AfterHoursTint) + (ui.AfterHoursTint >> 24).ToString("X2"), background = Css(palette.Background), text = Css(palette.Text), muted = Css(palette.Muted), grid = $"{Css(palette.Grid)}{palette.Grid.A:X2}", up = Css(palette.Up), down = Css(palette.Down), colors = new[] { palette.Series1, palette.Series2, palette.Series3, palette.Series4, palette.Series5, palette.Series6 }.Select(Css) };
    }

}
