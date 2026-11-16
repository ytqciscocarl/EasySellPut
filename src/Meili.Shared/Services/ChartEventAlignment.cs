using Meili.Automation;
using Meili.Core.Models;
using Meili.Core.Time;
using Meili.Shared.ViewModels;

namespace Meili.Shared.Services;

public sealed record ChartEvent(MarketEvent Event, long? Time, string Alignment);

/// <summary>Render anchors are separate from original event dates; no future candles are invented.</summary>
public static class ChartEventAlignment
{
    public static IReadOnlyList<ChartEvent> Map(ChartsViewModel vm)
        => Map(vm.Timeline, vm.Series, vm.Range);
    public static IReadOnlyList<ChartEvent> Map(MarketTimeline? timeline, BarSeries? series, ChartRangeKind range)
    {
        if (timeline is null || series is null || timeline.Symbol != series.Symbol) return [];
        var bars = series.Bars.Where(b => double.IsFinite(b.Close)).OrderBy(b => b.Time).DistinctBy(b => b.Time).ToArray();
        return timeline.Events.Select(e =>
        {
            if (e.Date is not { } day || bars.Length == 0) return new ChartEvent(e, null, "outside_chart");
            var first = DateOnly.FromDateTime(MarketClock.ToEastern(bars[0].Time));
            var last = DateOnly.FromDateTime(MarketClock.ToEastern(bars[^1].Time));
            if (day < first || day > last) return new ChartEvent(e, null, "outside_chart");
            if (range is ChartRangeKind.Week1 or ChartRangeKind.Month1)
            {
                var b = bars.LastOrDefault(b => DateOnly.FromDateTime(MarketClock.ToEastern(b.Time)) <= day);
                return new ChartEvent(e, b?.Time.ToUnixTimeSeconds(), "period_bucket");
            }
            var bar = bars.FirstOrDefault(b => series.Interval.IsIntraday() && e.Published is { } instant
                ? b.Time >= instant : DateOnly.FromDateTime(MarketClock.ToEastern(b.Time)) >= day);
            return new ChartEvent(e, bar?.Time.ToUnixTimeSeconds(), bar is null ? "outside_chart" :
                e.Published == bar.Time ? "exact" : series.Interval.IsIntraday() && e.Published is not null ? "next_bar" :
                DateOnly.FromDateTime(MarketClock.ToEastern(bar.Time)) == day ? "same_day" : "next_bar");
        }).ToArray();
    }
}
