using Meili.Core.Models;
using Meili.Core.Time;
namespace Meili.Shared.Tests;
public sealed class IntradaySparklineTests
{
    private static DateTimeOffset At(int day,int hour,int minute=0)=>MarketClock.FromEastern(new DateTime(2026,11,day,hour,minute,0));
    private static Bar B(DateTimeOffset t,double p)=>Bar.CloseOnly(t,p,1);
    [Fact] public void Uses_the_actual_latest_regular_trading_day_and_does_not_extend_into_the_future()
    {
        var series=new BarSeries{Symbol="AAPL",Interval=BarInterval.Minute5,Bars=[B(At(12,15),90),B(At(13,8),95),B(At(13,9,30),100),B(At(13,9,35),102),B(At(13,17),110),B(At(16,10),120)]};
        var graph=IntradaySparkline.Create(series,99,At(15,12));Assert.Equal(new DateOnly(2026,11,13),graph.Day);Assert.Equal(2,graph.Points.Count);Assert.Equal(0,graph.Points[0].X);Assert.InRange(graph.Points[^1].X,0.01,0.02);Assert.NotNull(graph.Baseline);
    }
    [Fact] public void Missing_intervals_stay_gaps_flat_prices_stay_centered_and_daily_bars_are_not_intraday()
    {
        var series=new BarSeries{Symbol="AAPL",Interval=BarInterval.Minute5,Bars=[B(At(13,9,30),100),B(At(13,9,35),double.NaN),B(At(13,10),100)]};var graph=IntradaySparkline.Create(series,null,At(15,12));Assert.All(graph.Points,p=>{Assert.Equal(.5,p.Y);Assert.True(p.Break);});Assert.Null(graph.Baseline);Assert.Empty(IntradaySparkline.Create(series with{Interval=BarInterval.Day1},null,At(15,12)).Points);
    }
}
