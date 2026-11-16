using Meili.Core.Models;
using Meili.Core.Time;
namespace Meili.Shared.Services;

public sealed record SparkPoint(double X,double Y,bool Break=false);
public sealed record IntradaySparkline(IReadOnlyList<SparkPoint> Points,DateOnly? Day,double? Baseline,string Source,bool Offline)
{
    public static readonly IntradaySparkline Empty=new([],null,null,"",false);
    // Fixed regular-session time axis. Missing intervals stay gaps; weekends retain their actual trading date.
    public static IntradaySparkline Create(BarSeries series,double? previous,DateTimeOffset now)
    {
        if(!series.Interval.IsIntraday())return Empty;
        var bars=series.Bars.Where(b=>b.Time<=now).OrderBy(b=>b.Time).DistinctBy(b=>b.Time)
            .Where(b=>{var t=MarketClock.ToEastern(b.Time).TimeOfDay;return t>=TimeSpan.FromHours(9.5)&&t<TimeSpan.FromHours(16);}).ToArray();
        if(bars.Length==0)return Empty;
        var day=DateOnly.FromDateTime(MarketClock.ToEastern(bars[^1].Time));bars=bars.Where(b=>DateOnly.FromDateTime(MarketClock.ToEastern(b.Time))==day).ToArray();
        var values=bars.Where(b=>double.IsFinite(b.Close)&&b.Close>0).Select(b=>b.Close).ToList();if(values.Count<2)return Empty;
        if(previous is >0 && double.IsFinite(previous.Value))values.Add(previous.Value);
        var lo=values.Min();var hi=values.Max();var span=Math.Max(hi-lo,Math.Max(hi*.001,0.001));
        double Y(double p)=>hi==lo?.5:.1+.8*(hi-p)/span;
        var result=new List<SparkPoint>();DateTimeOffset? last=null;var broken=true;
        foreach(var b in bars){if(!double.IsFinite(b.Close)||b.Close<=0){broken=true;continue;}var gap=last is { } t && b.Time-t>series.Interval.Duration()*2;result.Add(new((MarketClock.ToEastern(b.Time).TimeOfDay.TotalMinutes-570)/390,Y(b.Close),broken||gap));last=b.Time;broken=false;}
        return new(result,day,previous is >0&&double.IsFinite(previous.Value)?Y(previous.Value):null,series.Source,series.OfflineCache);
    }
}
